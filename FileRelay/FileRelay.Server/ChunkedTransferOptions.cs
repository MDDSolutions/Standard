using FileRelay.Core;
using FileRelay.Core.Interfaces;

namespace FileRelay.Server;

public class AppUser
{
    public string AppId { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public IReadOnlyList<ITransferTarget> Targets { get; set; } = Array.Empty<ITransferTarget>();
}

public class ChunkedTransferOptions
{
    public string BasePath { get; set; } = "/transfer";
    public int ChunkSizeMB { get; set; } = 50;
    public ITransferStateStore StateStore { get; set; } = new InMemoryTransferStateStore();
    public IReadOnlyList<AppUser> Users { get; set; } = Array.Empty<AppUser>();
    public ITransferCompleteHandler? OnComplete { get; set; }

    /// <summary>
    /// Reject any request that did not arrive over HTTPS. Default true.
    /// Set to false only for local/test deployments where TLS is not available.
    /// </summary>
    public bool RequireHttps { get; set; } = true;

    /// <summary>
    /// How long to retain completed transfer records before pruning. Default 30 days.
    /// </summary>
    public TimeSpan CompletedTransferRetention { get; set; } = TimeSpan.FromDays(30);

    /// <summary>
    /// Incomplete transfers with no chunk activity for this long are candidates for
    /// reconciliation — state is deleted if the partial file is gone or wrong size.
    /// Default 7 days.
    /// </summary>
    public TimeSpan AbandonedTransferInactivityThreshold { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// Maximum aggregate receive rate across all concurrent uploads, in MB/s.
    /// 0 = unlimited.
    /// </summary>
    public double ServerReceiveMBps { get; set; } = 0;

    /// <summary>
    /// Build timestamp returned by the /ping endpoint.
    /// Set from Foundation.BuildTime(Assembly.GetExecutingAssembly()) in the host application.
    /// </summary>
    public DateTime? ServerBuildTime { get; set; }

    /// <summary>
    /// Recommended Kestrel settings for bulk chunk uploads. Add to ConfigureKestrel() in the host:
    ///   options.Limits.Http2.InitialConnectionWindowSize = 16 * 1024 * 1024;
    ///   options.Limits.Http2.InitialStreamWindowSize     =  4 * 1024 * 1024;
    ///   options.Limits.MaxRequestBodySize = (long)ChunkSizeMB * 1024 * 1024 * 4; // headroom
    /// The default HTTP/2 connection window (128KB) caps throughput at ~128MB/s at 1ms RTT.
    /// The default MaxRequestBodySize (30MB) will reject chunks larger than 30MB with HTTP 413.
    /// </summary>
    public static void ConfigureKestrelLimits(Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions options, int chunkSizeMB)
    {
        options.Limits.Http2.InitialConnectionWindowSize = 16 * 1024 * 1024;
        options.Limits.Http2.InitialStreamWindowSize     =  4 * 1024 * 1024;
        options.Limits.Http2.MaxFrameSize                =  1 * 1024 * 1024;
        options.Limits.MaxRequestBodySize = (long)chunkSizeMB * 1024 * 1024 * 4;
    }
}
