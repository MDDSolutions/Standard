using System.Text.Json;
using FileRelay.Core;
using FileRelay.Core.Interfaces;
using FileRelay.Core.Models;
using Microsoft.Data.Sqlite;

namespace FileRelay.Storage.Sqlite;

public class SqliteTransferStateStore : ITransferStateStore
{
    private const int SchemaVersion = 2;

    private readonly string _connectionString;

    public SqliteTransferStateStore(string databasePath)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();

        InitializeSchema();
    }

    private void InitializeSchema()
    {
        using var conn = Open();

        var version = ExecuteScalar<long>(conn, "PRAGMA user_version");
        if (version == 0)
        {
            CreateSchema(conn);
            ExecuteNonQuery(conn, $"PRAGMA user_version = {SchemaVersion}");
        }
        else if (version == 1)
        {
            ExecuteNonQuery(conn, "ALTER TABLE Transfers ADD COLUMN AppId TEXT NOT NULL DEFAULT ''");
            ExecuteNonQuery(conn, $"PRAGMA user_version = {SchemaVersion}");
        }
        else if (version != SchemaVersion)
        {
            throw new InvalidOperationException(
                $"FileRelay database schema version mismatch: expected {SchemaVersion}, found {version}. " +
                "Delete the database file to start fresh, or apply a migration.");
        }
    }

    private static void CreateSchema(SqliteConnection conn)
    {
        ExecuteNonQuery(conn, """
            CREATE TABLE Transfers (
                TransferId      TEXT    NOT NULL PRIMARY KEY,
                AppId           TEXT    NOT NULL DEFAULT '',
                Filename        TEXT    NOT NULL,
                FileSizeBytes   INTEGER NOT NULL,
                FileHash        TEXT,
                Context         TEXT,
                ChunkSizeBytes  INTEGER NOT NULL,
                TotalChunks     INTEGER NOT NULL,
                IsComplete      INTEGER NOT NULL DEFAULT 0,
                CreatedAt       TEXT    NOT NULL,
                LastActivityAt  TEXT    NOT NULL
            );

            CREATE TABLE ConfirmedChunks (
                TransferId  TEXT    NOT NULL,
                ChunkIndex  INTEGER NOT NULL,
                ChunkHash   TEXT    NOT NULL,
                PRIMARY KEY (TransferId, ChunkIndex),
                FOREIGN KEY (TransferId) REFERENCES Transfers(TransferId)
            );
            """);
    }

    public async Task<TransferState> GetOrCreateAsync(TransferNegotiateRequest request, int serverChunkSizeMB)
    {
        using var conn = Open();

        var contextJson = SerializeContext(request.Context);

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT TransferId, Filename, FileSizeBytes, FileHash, Context,
                       ChunkSizeBytes, TotalChunks, IsComplete, CreatedAt, LastActivityAt, AppId
                FROM Transfers
                WHERE IsComplete = 0
                  AND AppId        = @AppId
                  AND Filename      = @Filename
                  AND FileSizeBytes = @FileSizeBytes
                  AND (Context = @Context OR (Context IS NULL AND @Context IS NULL))
                """;
            cmd.Parameters.AddWithValue("@AppId", request.AppId);
            cmd.Parameters.AddWithValue("@Filename", request.Filename);
            cmd.Parameters.AddWithValue("@FileSizeBytes", request.FileSizeBytes);
            cmd.Parameters.AddWithValue("@Context", (object?)contextJson ?? DBNull.Value);

            using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
                return await ReadStateWithChunks(conn, reader);
        }

        var chunkSizeBytes = (long)serverChunkSizeMB * 1024 * 1024;
        var now = DateTime.UtcNow;
        var state = new TransferState
        {
            TransferId     = Guid.NewGuid(),
            AppId          = request.AppId,
            Filename       = request.Filename,
            FileSizeBytes  = request.FileSizeBytes,
            FileHash       = request.FileHash,
            Context        = request.Context,
            ChunkSizeBytes = chunkSizeBytes,
            TotalChunks    = ChunkMath.TotalChunks(request.FileSizeBytes, chunkSizeBytes),
            CreatedAt      = now,
            LastActivityAt = now
        };

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO Transfers
                    (TransferId, AppId, Filename, FileSizeBytes, FileHash, Context,
                     ChunkSizeBytes, TotalChunks, IsComplete, CreatedAt, LastActivityAt)
                VALUES
                    (@TransferId, @AppId, @Filename, @FileSizeBytes, @FileHash, @Context,
                     @ChunkSizeBytes, @TotalChunks, 0, @CreatedAt, @LastActivityAt)
                """;
            cmd.Parameters.AddWithValue("@TransferId",     state.TransferId.ToString());
            cmd.Parameters.AddWithValue("@AppId",          state.AppId);
            cmd.Parameters.AddWithValue("@Filename",       state.Filename);
            cmd.Parameters.AddWithValue("@FileSizeBytes",  state.FileSizeBytes);
            cmd.Parameters.AddWithValue("@FileHash",       (object?)state.FileHash ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Context",        (object?)contextJson ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@ChunkSizeBytes", state.ChunkSizeBytes);
            cmd.Parameters.AddWithValue("@TotalChunks",    state.TotalChunks);
            cmd.Parameters.AddWithValue("@CreatedAt",      now.ToString("O"));
            cmd.Parameters.AddWithValue("@LastActivityAt", now.ToString("O"));
            await cmd.ExecuteNonQueryAsync();
        }

        return state;
    }

    public async Task<TransferState?> GetAsync(Guid transferId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT TransferId, Filename, FileSizeBytes, FileHash, Context,
                   ChunkSizeBytes, TotalChunks, IsComplete, CreatedAt, LastActivityAt, AppId
            FROM Transfers
            WHERE TransferId = @TransferId
            """;
        cmd.Parameters.AddWithValue("@TransferId", transferId.ToString());

        using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        return await ReadStateWithChunks(conn, reader);
    }

    public async Task ConfirmChunkAsync(Guid transferId, int chunkIndex, string chunkHash)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT OR IGNORE INTO ConfirmedChunks (TransferId, ChunkIndex, ChunkHash)
                VALUES (@TransferId, @ChunkIndex, @ChunkHash)
                """;
            cmd.Parameters.AddWithValue("@TransferId", transferId.ToString());
            cmd.Parameters.AddWithValue("@ChunkIndex", chunkIndex);
            cmd.Parameters.AddWithValue("@ChunkHash", chunkHash);
            await cmd.ExecuteNonQueryAsync();
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE Transfers SET LastActivityAt = @Now WHERE TransferId = @TransferId";
            cmd.Parameters.AddWithValue("@Now", DateTime.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("@TransferId", transferId.ToString());
            await cmd.ExecuteNonQueryAsync();
        }

        tx.Commit();
    }

    public async Task<IReadOnlyList<int>> GetMissingChunksAsync(Guid transferId)
    {
        using var conn = Open();

        int totalChunks;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT TotalChunks FROM Transfers WHERE TransferId = @TransferId";
            cmd.Parameters.AddWithValue("@TransferId", transferId.ToString());
            var result = await cmd.ExecuteScalarAsync();
            if (result is null) return Array.Empty<int>();
            totalChunks = Convert.ToInt32(result);
        }

        var confirmed = new HashSet<int>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT ChunkIndex FROM ConfirmedChunks WHERE TransferId = @TransferId";
            cmd.Parameters.AddWithValue("@TransferId", transferId.ToString());
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                confirmed.Add(reader.GetInt32(0));
        }

        return Enumerable.Range(1, totalChunks)
            .Where(i => !confirmed.Contains(i))
            .ToList();
    }

    public async Task MarkCompleteAsync(Guid transferId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE Transfers SET IsComplete = 1 WHERE TransferId = @TransferId";
        cmd.Parameters.AddWithValue("@TransferId", transferId.ToString());
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task PruneCompletedAsync(TimeSpan retention)
    {
        var cutoff = DateTime.UtcNow - retention;
        using var conn = Open();
        using var tx = conn.BeginTransaction();

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                DELETE FROM ConfirmedChunks WHERE TransferId IN (
                    SELECT TransferId FROM Transfers
                    WHERE IsComplete = 1 AND CreatedAt < @Cutoff
                )
                """;
            cmd.Parameters.AddWithValue("@Cutoff", cutoff.ToString("O"));
            await cmd.ExecuteNonQueryAsync();
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM Transfers WHERE IsComplete = 1 AND CreatedAt < @Cutoff";
            cmd.Parameters.AddWithValue("@Cutoff", cutoff.ToString("O"));
            await cmd.ExecuteNonQueryAsync();
        }

        tx.Commit();
    }

    public async Task<IReadOnlyList<TransferState>> GetInactiveIncompleteTransfersAsync(TimeSpan inactivityThreshold)
    {
        var cutoff = DateTime.UtcNow - inactivityThreshold;
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT TransferId, Filename, FileSizeBytes, FileHash, Context,
                   ChunkSizeBytes, TotalChunks, IsComplete, CreatedAt, LastActivityAt, AppId
            FROM Transfers
            WHERE IsComplete = 0 AND LastActivityAt < @Cutoff
            """;
        cmd.Parameters.AddWithValue("@Cutoff", cutoff.ToString("O"));

        var results = new List<TransferState>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            results.Add(await ReadStateWithChunks(conn, reader));

        return results;
    }

    public async Task DeleteTransferStateAsync(Guid transferId)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM ConfirmedChunks WHERE TransferId = @TransferId";
            cmd.Parameters.AddWithValue("@TransferId", transferId.ToString());
            await cmd.ExecuteNonQueryAsync();
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM Transfers WHERE TransferId = @TransferId";
            cmd.Parameters.AddWithValue("@TransferId", transferId.ToString());
            await cmd.ExecuteNonQueryAsync();
        }

        tx.Commit();
    }

    private static async Task<TransferState> ReadStateWithChunks(SqliteConnection conn, SqliteDataReader reader)
    {
        var transferId = Guid.Parse(reader.GetString(0));
        var contextJson = reader.IsDBNull(4) ? null : reader.GetString(4);

        var state = new TransferState
        {
            TransferId     = transferId,
            AppId          = reader.IsDBNull(10) ? "" : reader.GetString(10),
            Filename       = reader.GetString(1),
            FileSizeBytes  = reader.GetInt64(2),
            FileHash       = reader.IsDBNull(3) ? null : reader.GetString(3),
            Context        = contextJson != null ? DeserializeContext(contextJson) : null,
            ChunkSizeBytes = reader.GetInt64(5),
            TotalChunks    = reader.GetInt32(6),
            IsComplete     = reader.GetInt32(7) == 1,
            CreatedAt      = DateTime.Parse(reader.GetString(8)),
            LastActivityAt = DateTime.Parse(reader.GetString(9))
        };

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT ChunkIndex FROM ConfirmedChunks WHERE TransferId = @TransferId";
        cmd.Parameters.AddWithValue("@TransferId", transferId.ToString());
        using var chunkReader = await cmd.ExecuteReaderAsync();
        while (await chunkReader.ReadAsync())
            state.ConfirmedChunks.Add(chunkReader.GetInt32(0));

        return state;
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    private static string? SerializeContext(TransferContext? context)
        => context != null ? JsonSerializer.Serialize(context, JsonOptions) : null;

    private static TransferContext? DeserializeContext(string json)
        => JsonSerializer.Deserialize<TransferContext>(json, JsonOptions);

    private static readonly JsonSerializerOptions JsonOptions = new();

    private static void ExecuteNonQuery(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static T ExecuteScalar<T>(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return (T)Convert.ChangeType(cmd.ExecuteScalar()!, typeof(T));
    }
}
