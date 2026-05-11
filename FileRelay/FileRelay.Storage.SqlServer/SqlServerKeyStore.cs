using System.Security.Cryptography;
using FileRelay.Core.Interfaces;
using Microsoft.Data.SqlClient;

namespace FileRelay.Storage.SqlServer;

public class SqlServerKeyStore : IKeyStore
{
    private readonly string _connectionString;

    public SqlServerKeyStore(string connectionString)
    {
        _connectionString = connectionString;

        using var conn = Open();

        ExecuteNonQuery(conn, """
            IF NOT EXISTS (SELECT * FROM sys.schemas WHERE name = 'FileRelay')
                EXEC('CREATE SCHEMA FileRelay')
            """);

        ExecuteNonQuery(conn, """
            IF NOT EXISTS (SELECT * FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = 'FileRelay' AND TABLE_NAME = 'AppKeys')
            BEGIN
                CREATE TABLE FileRelay.AppKeys (
                    AppId          NVARCHAR(256) NOT NULL PRIMARY KEY,
                    CurrentKey     NVARCHAR(500) NOT NULL,
                    PreviousKey    NVARCHAR(500),
                    GracePeriodEnd DATETIME2
                )
            END
            """);
    }

    public async Task SeedAsync(string appId, string seedKey)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            IF NOT EXISTS (SELECT 1 FROM FileRelay.AppKeys WHERE AppId = @AppId)
                INSERT INTO FileRelay.AppKeys (AppId, CurrentKey) VALUES (@AppId, @CurrentKey)
            """;
        cmd.Parameters.AddWithValue("@AppId",      appId);
        cmd.Parameters.AddWithValue("@CurrentKey", seedKey);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<KeyAuthResult?> AuthenticateAsync(string appId, string providedKey, TimeSpan gracePeriod)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();

        string? currentKey, previousKey;
        DateTime? gracePeriodEnd;
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT CurrentKey, PreviousKey, GracePeriodEnd FROM FileRelay.AppKeys WHERE AppId = @AppId";
            cmd.Parameters.AddWithValue("@AppId", appId);
            using var reader = await cmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) return null;
            currentKey     = reader.GetString(0);
            previousKey    = reader.IsDBNull(1) ? null : reader.GetString(1);
            gracePeriodEnd = reader.IsDBNull(2) ? null : reader.GetDateTime(2);
        }

        if (providedKey == currentKey)
        {
            // Stamp GracePeriodEnd the first time the new key is used after a rotation.
            if (previousKey != null && gracePeriodEnd == null)
            {
                var end = DateTime.UtcNow.Add(gracePeriod);
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE FileRelay.AppKeys SET GracePeriodEnd = @End WHERE AppId = @AppId";
                cmd.Parameters.AddWithValue("@End",   end);
                cmd.Parameters.AddWithValue("@AppId", appId);
                await cmd.ExecuteNonQueryAsync();
            }
            await tx.CommitAsync();
            return new KeyAuthResult(KeyStatus.Current);
        }

        if (previousKey != null && providedKey == previousKey)
        {
            if (gracePeriodEnd == null)
            {
                await tx.CommitAsync();
                return new KeyAuthResult(KeyStatus.PreviousGracePending);
            }

            if (DateTime.UtcNow < gracePeriodEnd.Value)
            {
                await tx.CommitAsync();
                return new KeyAuthResult(KeyStatus.PreviousGraceActive);
            }

            // Grace period expired — reject without updating state.
            await tx.RollbackAsync();
            return null;
        }

        await tx.RollbackAsync();
        return null;
    }

    public async Task<string> RotateAsync(string appId, byte[] clientEntropy)
    {
        var newKey = GenerateKey(clientEntropy);

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE FileRelay.AppKeys
            SET PreviousKey    = CurrentKey,
                CurrentKey     = @NewKey,
                GracePeriodEnd = NULL
            WHERE AppId = @AppId
            """;
        cmd.Parameters.AddWithValue("@NewKey", newKey);
        cmd.Parameters.AddWithValue("@AppId",  appId);
        await cmd.ExecuteNonQueryAsync();

        return newKey;
    }

    public async Task<(string Current, string? Previous)?> GetKeysAsync(string appId)
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "SELECT CurrentKey, PreviousKey FROM FileRelay.AppKeys WHERE AppId = @AppId";
        cmd.Parameters.AddWithValue("@AppId", appId);
        using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        return (reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    public async Task<bool> HasActiveGracePeriodAsync(string appId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT GracePeriodEnd FROM FileRelay.AppKeys WHERE AppId = @AppId";
        cmd.Parameters.AddWithValue("@AppId", appId);
        var result = await cmd.ExecuteScalarAsync();
        if (result is null or DBNull) return false;
        return DateTime.UtcNow < (DateTime)result;
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

    private SqlConnection Open()
    {
        var conn = new SqlConnection(_connectionString);
        conn.Open();
        return conn;
    }

    private static void ExecuteNonQuery(SqlConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
