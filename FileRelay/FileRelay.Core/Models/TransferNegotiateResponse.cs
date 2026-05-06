namespace FileRelay.Core.Models;

public class TransferNegotiateResponse
{
    public Guid TransferId { get; set; }
    public int ChunkSizeMB { get; set; }
    public int TotalChunks { get; set; }
    public IReadOnlyList<int> ChunksNeeded { get; set; } = Array.Empty<int>();
}
