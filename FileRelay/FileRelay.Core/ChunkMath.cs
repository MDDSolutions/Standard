namespace FileRelay.Core;

public static class ChunkMath
{
    public static int TotalChunks(long fileSizeBytes, long chunkSizeBytes)
        => (int)Math.Ceiling((double)fileSizeBytes / chunkSizeBytes);

    // chunkIndex is 1-based
    public static long ChunkOffset(int chunkIndex, long chunkSizeBytes)
        => (long)(chunkIndex - 1) * chunkSizeBytes;

    public static long ChunkLength(int chunkIndex, long fileSizeBytes, long chunkSizeBytes)
    {
        var offset = ChunkOffset(chunkIndex, chunkSizeBytes);
        return Math.Min(chunkSizeBytes, fileSizeBytes - offset);
    }
}
