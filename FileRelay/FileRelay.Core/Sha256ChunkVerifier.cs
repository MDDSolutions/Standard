using System.Security.Cryptography;
using FileRelay.Core.Interfaces;

namespace FileRelay.Core;

public class Sha256ChunkVerifier : IChunkVerifier
{
    public string ComputeHash(Stream data)
    {
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(data);
        return $"sha256:{Convert.ToBase64String(hash)}";
    }

    public bool Verify(Stream data, string expectedHash)
        => ComputeHash(data) == expectedHash;
}
