using FileRelay.Core;

namespace FileRelay.Client;

public class UploadOptions
{
    public int ParallelConnections { get; set; } = 4;
    public int PreferredChunkSizeMB { get; set; } = 50;
    public int MaxRetries { get; set; } = 5;
    public TransferContext? Context { get; set; }
    public Action<UploadProgress>? OnProgress { get; set; }
    public double ProgressIntervalSeconds { get; set; } = 1.0;
}
