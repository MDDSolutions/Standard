using FileRelay.Core;
using FileRelay.Core.Models;

namespace FileRelay.Client;

public class UploadOptions
{
    /// <summary>
    /// Used when no Throttle is set. When a BandwidthLimiter is provided,
    /// its ParallelConnections value is used instead.
    /// </summary>
    public int ParallelConnections { get; set; } = 4;
    public int PreferredChunkSizeMB { get; set; } = 50;
    public int MaxRetries { get; set; } = 5;
    public TransferContext? Context { get; set; }
    public Action<UploadProgress>? OnProgress { get; set; }
    public double ProgressIntervalSeconds { get; set; } = 1.0;

    /// <summary>
    /// Called once after the server responds to the negotiate request, before any
    /// chunks are uploaded. Useful for logging whether a transfer is new or resumed.
    /// </summary>
    public Action<TransferNegotiateResponse>? OnNegotiated { get; set; }

    /// <summary>
    /// Shared bandwidth limiter. When set, all uploads using this limiter cooperate
    /// on a single throughput budget and ParallelConnections comes from the limiter.
    /// </summary>
    public BandwidthLimiter? Throttle { get; set; }

    /// <summary>
    /// Upload priority within the shared limiter. Lower number = higher priority.
    /// Default 50. Range 0-255.
    /// </summary>
    public byte Priority { get; set; } = 50;

    internal int EffectiveParallelConnections => Throttle?.ParallelConnections ?? ParallelConnections;
}
