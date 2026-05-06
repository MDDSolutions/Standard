namespace FileRelay.Core.Interfaces;

public interface IChunkVerifier
{
    string ComputeHash(Stream data);
    bool Verify(Stream data, string expectedHash);
}
