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
    /// Called before each retry delay. Parameters: the exception that triggered the
    /// retry, the 1-based attempt number, and the delay before the next attempt.
    /// </summary>
    public Action<Exception, int, TimeSpan>? OnRetry { get; set; }

    /// <summary>
    /// Called when the server indicates the request was authenticated with the previous key.
    /// Value is "previous-grace-pending" (new key not yet used) or "previous-grace-active"
    /// (new key has been used; old key has a countdown).
    /// </summary>
    public Action<string>? OnKeyWarning { get; set; }

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

    /// <summary>
    /// When true, the client computes SHA-256 of the entire file before negotiating and
    /// the server verifies the assembled file against it at completion. Adds one sequential
    /// read of the file (~1-3 seconds per GB on typical SSDs). Default true.
    /// Set to false only when pre-hashing latency is unacceptable for very large files.
    /// </summary>
    public bool ComputeFileHash { get; set; } = false;

    internal int EffectiveParallelConnections => Throttle?.ParallelConnections ?? ParallelConnections;
}
