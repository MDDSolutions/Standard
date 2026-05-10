namespace FileRelay.Core.Models;

public class TransferNegotiateRequest
{
    public string Filename { get; set; } = "";
    public long FileSizeBytes { get; set; }
    public string? FileHash { get; set; }
    public int ChunkSizeMB { get; set; }
    public TransferContext? Context { get; set; }

    // Set by the server after authentication; never read from the client request body.
    public string AppId { get; set; } = "";
}
