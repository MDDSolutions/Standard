namespace FileRelay.Core.Models;

public class TransferNegotiateRequest
{
    public string Filename { get; set; } = "";
    public long FileSizeBytes { get; set; }
    public string? FileHash { get; set; }
    public int ChunkSizeMB { get; set; }
    public TransferContext? Context { get; set; }
}
