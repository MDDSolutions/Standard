namespace FileRelay.Core.Models;

public class CompletedTransfer
{
    public Guid TransferId { get; set; }
    public string Filename { get; set; } = "";
    public long FileSizeBytes { get; set; }
    public string? FileHash { get; set; }
    public DateTime CompletedAt { get; set; }
}
