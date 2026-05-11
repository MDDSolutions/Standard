using System.Security.Cryptography;
using FileRelay.Core.Interfaces;
using Microsoft.Data.Sqlite;

namespace FileRelay.Storage.Sqlite;

public class SqliteKeyStore : IKeyStore
{
    private readonly string _connectionString;

    public SqliteKeyStore(string databasePath)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();

        using var conn = Open();
        ExecuteNonQuery(conn, """
            CREATE TABLE IF NOT EXISTS AppKeys (
                AppId          TEXT NOT NULL PRIMARY KEY,
                CurrentKey     TEXT NOT NULL,
                PreviousKey    TEXT,
                GracePeriodEnd TEXT
            )
            """);
    }

    public async Task SeedAsync(string appId, string seedKey)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO AppKeys (AppId, CurrentKey)
            VALUES (@AppId, @CurrentKey)
            """;
        cmd.Parameters.AddWithValue("@AppId", appId);
        cmd.Parameters.AddWithValue("@CurrentKey", seedKey);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<KeyAuthResult?> AuthenticateAsync(string appId, string providedKey, TimeSpan gracePeriod)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();

        string? currentKey, previousKey, gracePeriodEnd;
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT CurrentKey, PreviousKey, GracePeriodEnd FROM AppKeys WHERE AppId = @AppId";
            cmd.Parameters.AddWithValue("@AppId", appId);
            using var reader = await cmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) return null;
            currentKey     = reader.GetString(0);
            previousKey    = reader.IsDBNull(1) ? null : reader.GetString(1);
            gracePeriodEnd = reader.IsDBNull(2) ? null : reader.GetString(2);
        }

        if (providedKey == currentKey)
        {
            // Stamp GracePeriodEnd the first time the new key is used after a rotation.
            if (previousKey != null && gracePeriodEnd == null)
            {
                var end = DateTime.UtcNow.Add(gracePeriod);
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE AppKeys SET GracePeriodEnd = @End WHERE AppId = @AppId";
                cmd.Parameters.AddWithValue("@End", end.ToString("O"));
                cmd.Parameters.AddWithValue("@AppId", appId);
                await cmd.ExecuteNonQueryAsync();
            }
            tx.Commit();
            return new KeyAuthResult(KeyStatus.Current);
        }

        if (previousKey != null && providedKey == previousKey)
        {
            if (gracePeriodEnd == null)
            {
                tx.Commit();
                return new KeyAuthResult(KeyStatus.PreviousGracePending);
            }

            var end = DateTime.Parse(gracePeriodEnd);
            if (DateTime.UtcNow < end)
            {
                tx.Commit();
                return new KeyAuthResult(KeyStatus.PreviousGraceActive);
            }

            // Grace period expired — reject without updating state.
            tx.Rollback();
            return null;
        }

        tx.Rollback();
        return null;
    }

    public async Task<string> RotateAsync(string appId, byte[] clientEntropy)
    {
        var newKey = GenerateKey(clientEntropy);

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE AppKeys
            SET PreviousKey    = CurrentKey,
                CurrentKey     = @NewKey,
                GracePeriodEnd = NULL
            WHERE AppId = @AppId
            """;
        cmd.Parameters.AddWithValue("@NewKey", newKey);
        cmd.Parameters.AddWithValue("@AppId", appId);
        await cmd.ExecuteNonQueryAsync();

        return newKey;
    }

    public async Task<(string Current, string? Previous)?> GetKeysAsync(string appId)
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "SELECT CurrentKey, PreviousKey, GracePeriodEnd FROM AppKeys WHERE AppId = @AppId";
        cmd.Parameters.AddWithValue("@AppId", appId);
        using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;

        var previous = reader.IsDBNull(1) ? null : reader.GetString(1);
        var gracePeriodEnd = reader.IsDBNull(2) ? null : reader.GetString(2);
        if (previous != null && gracePeriodEnd != null && DateTime.UtcNow >= DateTime.Parse(gracePeriodEnd))
            previous = null;

        return (reader.GetString(0), previous);
    }

    public async Task<bool> HasActiveGracePeriodAsync(string appId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT GracePeriodEnd FROM AppKeys WHERE AppId = @AppId";
        cmd.Parameters.AddWithValue("@AppId", appId);
        var result = await cmd.ExecuteScalarAsync();
        if (result is null or DBNull) return false;
        return DateTime.UtcNow < DateTime.Parse((string)result);
    }

    private static string GenerateKey(byte[] clientEntropy)
    {
        var serverRandom = RandomNumberGenerator.GetBytes(32);
        // XOR server random with the SHA-256 of client entropy so neither side alone controls the output.
        var clientHash = SHA256.HashData(clientEntropy.Length > 0 ? clientEntropy : RandomNumberGenerator.GetBytes(32));
        var combined = new byte[32];
        for (var i = 0; i < 32; i++)
            combined[i] = (byte)(serverRandom[i] ^ clientHash[i]);
        return Convert.ToBase64String(SHA256.HashData(combined));
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    private static void ExecuteNonQuery(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
