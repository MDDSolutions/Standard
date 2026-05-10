using System.Text.Json;
using FileRelay.Core;
using FileRelay.Core.Interfaces;
using FileRelay.Core.Models;
using Microsoft.Data.SqlClient;

namespace FileRelay.Storage.SqlServer;

public class SqlServerTransferStateStore : ITransferStateStore
{
    private const int SchemaVersion = 2;

    private readonly string _connectionString;

    public SqlServerTransferStateStore(string connectionString)
    {
        _connectionString = connectionString;
        InitializeSchema();
    }

    private void InitializeSchema()
    {
        using var conn = Open();

        ExecuteNonQuery(conn, """
            IF NOT EXISTS (SELECT * FROM sys.schemas WHERE name = 'FileRelay')
                EXEC('CREATE SCHEMA FileRelay')
            """);

        ExecuteNonQuery(conn, """
            IF NOT EXISTS (SELECT * FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = 'FileRelay' AND TABLE_NAME = 'SchemaVersion')
            BEGIN
                CREATE TABLE FileRelay.SchemaVersion (Version INT NOT NULL);
                INSERT INTO FileRelay.SchemaVersion (Version) VALUES (0);
            END
            """);

        var version = ExecuteScalar<int>(conn, "SELECT Version FROM FileRelay.SchemaVersion");

        if (version == 0)
        {
            CreateSchema(conn);
            ExecuteNonQuery(conn, $"UPDATE FileRelay.SchemaVersion SET Version = {SchemaVersion}");
        }
        else if (version == 1)
        {
            ExecuteNonQuery(conn, "ALTER TABLE FileRelay.Transfers ADD AppId NVARCHAR(256) NOT NULL DEFAULT ''");
            ExecuteNonQuery(conn, $"UPDATE FileRelay.SchemaVersion SET Version = {SchemaVersion}");
        }
        else if (version != SchemaVersion)
        {
            throw new InvalidOperationException(
                $"FileRelay database schema version mismatch: expected {SchemaVersion}, found {version}. " +
                "Drop and recreate the database objects to start fresh, or apply a migration.");
        }
    }

    private static void CreateSchema(SqlConnection conn)
    {
        ExecuteNonQuery(conn, """
            CREATE TABLE FileRelay.Transfers (
                TransferId      UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
                AppId           NVARCHAR(256)    NOT NULL DEFAULT '',
                Filename        NVARCHAR(1000)   NOT NULL,
                FileSizeBytes   BIGINT           NOT NULL,
                FileHash        NVARCHAR(100),
                Context         NVARCHAR(MAX),
                ChunkSizeBytes  BIGINT           NOT NULL,
                TotalChunks     INT              NOT NULL,
                IsComplete      BIT              NOT NULL DEFAULT 0,
                CreatedAt       DATETIME2        NOT NULL,
                LastActivityAt  DATETIME2        NOT NULL
            );

            CREATE TABLE FileRelay.ConfirmedChunks (
                TransferId  UNIQUEIDENTIFIER NOT NULL,
                ChunkIndex  INT              NOT NULL,
                ChunkHash   NVARCHAR(100)    NOT NULL,
                PRIMARY KEY (TransferId, ChunkIndex),
                FOREIGN KEY (TransferId) REFERENCES FileRelay.Transfers(TransferId)
            );
            """);
    }

    public async Task<TransferState> GetOrCreateAsync(TransferNegotiateRequest request, int serverChunkSizeMB)
    {
        using var conn = Open();
        var contextJson = SerializeContext(request.Context);

        TransferState? existing = null;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT TransferId, Filename, FileSizeBytes, FileHash, Context,
                       ChunkSizeBytes, TotalChunks, IsComplete, CreatedAt, LastActivityAt, AppId
                FROM FileRelay.Transfers
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
                existing = ReadStateRow(reader);
        }

        if (existing != null)
        {
            await LoadChunksAsync(conn, existing);
            return existing;
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
                INSERT INTO FileRelay.Transfers
                    (TransferId, AppId, Filename, FileSizeBytes, FileHash, Context,
                     ChunkSizeBytes, TotalChunks, IsComplete, CreatedAt, LastActivityAt)
                VALUES
                    (@TransferId, @AppId, @Filename, @FileSizeBytes, @FileHash, @Context,
                     @ChunkSizeBytes, @TotalChunks, 0, @CreatedAt, @LastActivityAt)
                """;
            cmd.Parameters.AddWithValue("@TransferId",     state.TransferId);
            cmd.Parameters.AddWithValue("@AppId",          state.AppId);
            cmd.Parameters.AddWithValue("@Filename",       state.Filename);
            cmd.Parameters.AddWithValue("@FileSizeBytes",  state.FileSizeBytes);
            cmd.Parameters.AddWithValue("@FileHash",       (object?)state.FileHash ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Context",        (object?)contextJson ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@ChunkSizeBytes", state.ChunkSizeBytes);
            cmd.Parameters.AddWithValue("@TotalChunks",    state.TotalChunks);
            cmd.Parameters.AddWithValue("@CreatedAt",      now);
            cmd.Parameters.AddWithValue("@LastActivityAt", now);
            await cmd.ExecuteNonQueryAsync();
        }

        return state;
    }

    public async Task<TransferState?> GetAsync(Guid transferId)
    {
        using var conn = Open();

        TransferState? state = null;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT TransferId, Filename, FileSizeBytes, FileHash, Context,
                       ChunkSizeBytes, TotalChunks, IsComplete, CreatedAt, LastActivityAt, AppId
                FROM FileRelay.Transfers
                WHERE TransferId = @TransferId
                """;
            cmd.Parameters.AddWithValue("@TransferId", transferId);

            using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
                state = ReadStateRow(reader);
        }

        if (state == null) return null;
        await LoadChunksAsync(conn, state);
        return state;
    }

    public async Task ConfirmChunkAsync(Guid transferId, int chunkIndex, string chunkHash)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                IF NOT EXISTS (
                    SELECT 1 FROM FileRelay.ConfirmedChunks
                    WHERE TransferId = @TransferId AND ChunkIndex = @ChunkIndex
                )
                INSERT INTO FileRelay.ConfirmedChunks (TransferId, ChunkIndex, ChunkHash)
                VALUES (@TransferId, @ChunkIndex, @ChunkHash)
                """;
            cmd.Parameters.AddWithValue("@TransferId", transferId);
            cmd.Parameters.AddWithValue("@ChunkIndex",  chunkIndex);
            cmd.Parameters.AddWithValue("@ChunkHash",   chunkHash);
            await cmd.ExecuteNonQueryAsync();
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE FileRelay.Transfers SET LastActivityAt = @Now WHERE TransferId = @TransferId";
            cmd.Parameters.AddWithValue("@Now",        DateTime.UtcNow);
            cmd.Parameters.AddWithValue("@TransferId", transferId);
            await cmd.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();
    }

    public async Task<IReadOnlyList<int>> GetMissingChunksAsync(Guid transferId)
    {
        using var conn = Open();

        int totalChunks;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT TotalChunks FROM FileRelay.Transfers WHERE TransferId = @TransferId";
            cmd.Parameters.AddWithValue("@TransferId", transferId);
            var result = await cmd.ExecuteScalarAsync();
            if (result is null or DBNull) return Array.Empty<int>();
            totalChunks = Convert.ToInt32(result);
        }

        var confirmed = new HashSet<int>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT ChunkIndex FROM FileRelay.ConfirmedChunks WHERE TransferId = @TransferId";
            cmd.Parameters.AddWithValue("@TransferId", transferId);
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
        cmd.CommandText = "UPDATE FileRelay.Transfers SET IsComplete = 1 WHERE TransferId = @TransferId";
        cmd.Parameters.AddWithValue("@TransferId", transferId);
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
                DELETE FROM FileRelay.ConfirmedChunks WHERE TransferId IN (
                    SELECT TransferId FROM FileRelay.Transfers
                    WHERE IsComplete = 1 AND CreatedAt < @Cutoff
                )
                """;
            cmd.Parameters.AddWithValue("@Cutoff", cutoff);
            await cmd.ExecuteNonQueryAsync();
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM FileRelay.Transfers WHERE IsComplete = 1 AND CreatedAt < @Cutoff";
            cmd.Parameters.AddWithValue("@Cutoff", cutoff);
            await cmd.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();
    }

    public async Task<IReadOnlyList<TransferState>> GetInactiveIncompleteTransfersAsync(TimeSpan inactivityThreshold)
    {
        var cutoff = DateTime.UtcNow - inactivityThreshold;
        using var conn = Open();

        var states = new List<TransferState>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT TransferId, Filename, FileSizeBytes, FileHash, Context,
                       ChunkSizeBytes, TotalChunks, IsComplete, CreatedAt, LastActivityAt, AppId
                FROM FileRelay.Transfers
                WHERE IsComplete = 0 AND LastActivityAt < @Cutoff
                """;
            cmd.Parameters.AddWithValue("@Cutoff", cutoff);

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                states.Add(ReadStateRow(reader));
        }

        foreach (var state in states)
            await LoadChunksAsync(conn, state);

        return states;
    }

    public async Task DeleteTransferStateAsync(Guid transferId)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM FileRelay.ConfirmedChunks WHERE TransferId = @TransferId";
            cmd.Parameters.AddWithValue("@TransferId", transferId);
            await cmd.ExecuteNonQueryAsync();
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM FileRelay.Transfers WHERE TransferId = @TransferId";
            cmd.Parameters.AddWithValue("@TransferId", transferId);
            await cmd.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();
    }

    private static TransferState ReadStateRow(SqlDataReader reader)
    {
        var contextJson = reader.IsDBNull(4) ? null : reader.GetString(4);
        return new TransferState
        {
            TransferId     = reader.GetGuid(0),
            AppId          = reader.IsDBNull(10) ? "" : reader.GetString(10),
            Filename       = reader.GetString(1),
            FileSizeBytes  = reader.GetInt64(2),
            FileHash       = reader.IsDBNull(3) ? null : reader.GetString(3),
            Context        = contextJson != null ? DeserializeContext(contextJson) : null,
            ChunkSizeBytes = reader.GetInt64(5),
            TotalChunks    = reader.GetInt32(6),
            IsComplete     = reader.GetBoolean(7),
            CreatedAt      = reader.GetDateTime(8),
            LastActivityAt = reader.GetDateTime(9)
        };
    }

    private static async Task LoadChunksAsync(SqlConnection conn, TransferState state)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT ChunkIndex FROM FileRelay.ConfirmedChunks WHERE TransferId = @TransferId";
        cmd.Parameters.AddWithValue("@TransferId", state.TransferId);
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            state.ConfirmedChunks.Add(reader.GetInt32(0));
    }

    private SqlConnection Open()
    {
        var conn = new SqlConnection(_connectionString);
        conn.Open();
        return conn;
    }

    private static string? SerializeContext(TransferContext? context)
        => context != null ? JsonSerializer.Serialize(context, JsonOptions) : null;

    private static TransferContext? DeserializeContext(string json)
        => JsonSerializer.Deserialize<TransferContext>(json, JsonOptions);

    private static readonly JsonSerializerOptions JsonOptions = new();

    private static void ExecuteNonQuery(SqlConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static T ExecuteScalar<T>(SqlConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return (T)Convert.ChangeType(cmd.ExecuteScalar()!, typeof(T));
    }
}
