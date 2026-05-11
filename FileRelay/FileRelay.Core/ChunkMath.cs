namespace FileRelay.Core;

public static class ChunkMath
{
    public static int TotalChunks(long fileSizeBytes, long chunkSizeBytes)
    {
        if (fileSizeBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(fileSizeBytes), "File size cannot be negative.");
        if (chunkSizeBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(chunkSizeBytes), "Chunk size must be greater than zero.");
        if (fileSizeBytes == 0)
            return 1;

        var chunks = ((fileSizeBytes - 1) / chunkSizeBytes) + 1;
        if (chunks > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(fileSizeBytes), "File requires too many chunks.");

        return (int)chunks;
    }

    // chunkIndex is 1-based
    public static long ChunkOffset(int chunkIndex, long chunkSizeBytes)
    {
        if (chunkIndex < 1)
            throw new ArgumentOutOfRangeException(nameof(chunkIndex), "Chunk index is 1-based.");
        if (chunkSizeBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(chunkSizeBytes), "Chunk size must be greater than zero.");

        return checked((long)(chunkIndex - 1) * chunkSizeBytes);
    }

    public static long ChunkLength(int chunkIndex, long fileSizeBytes, long chunkSizeBytes)
    {
        var totalChunks = TotalChunks(fileSizeBytes, chunkSizeBytes);
        if (chunkIndex > totalChunks)
            throw new ArgumentOutOfRangeException(nameof(chunkIndex), "Chunk index exceeds the transfer chunk count.");
        if (fileSizeBytes == 0)
            return 0;

        var offset = ChunkOffset(chunkIndex, chunkSizeBytes);
        return Math.Min(chunkSizeBytes, fileSizeBytes - offset);
    }
}
