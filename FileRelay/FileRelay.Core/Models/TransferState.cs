namespace FileRelay.Core.Models;

public class TransferState
{
    public Guid TransferId { get; set; }
    public string Filename { get; set; } = "";
    public long FileSizeBytes { get; set; }
    public string? FileHash { get; set; }
    public TransferContext? Context { get; set; }
    public long ChunkSizeBytes { get; set; }
    public int TotalChunks { get; set; }
    public HashSet<int> ConfirmedChunks { get; set; } = new();
    public bool IsComplete { get; set; }
    public DateTime CreatedAt { get; set; }
}
