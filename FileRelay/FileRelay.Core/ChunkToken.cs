using System.Security.Cryptography;
using System.Text;

namespace FileRelay.Core;

public static class ChunkToken
{
    public static string Compute(string apiKey, string appId, Guid transferId, int chunkIndex)
    {
        var keyBytes   = Encoding.UTF8.GetBytes(apiKey);
        var inputBytes = Encoding.UTF8.GetBytes($"{appId}|{transferId:N}|{chunkIndex}");
        using var hmac = new HMACSHA256(keyBytes);
        return Convert.ToBase64String(hmac.ComputeHash(inputBytes));
    }

    public static bool Validate(byte[] token, string apiKey, string appId, Guid transferId, int chunkIndex)
    {
        var keyBytes   = Encoding.UTF8.GetBytes(apiKey);
        var inputBytes = Encoding.UTF8.GetBytes($"{appId}|{transferId:N}|{chunkIndex}");
        using var hmac = new HMACSHA256(keyBytes);
        var expected   = hmac.ComputeHash(inputBytes);
        return FixedTimeEquals(token, expected);
    }

    // Constant-time comparison to prevent timing attacks.
    private static bool FixedTimeEquals(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        int diff = 0;
        for (int i = 0; i < a.Length; i++)
            diff |= a[i] ^ b[i];
        return diff == 0;
    }
}
