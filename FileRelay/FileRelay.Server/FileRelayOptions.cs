using FileRelay.Core;
using FileRelay.Core.Interfaces;

namespace FileRelay.Server;

public class AppUser
{
    public string AppId { get; set; } = "";

    /// <summary>
    /// Used only to seed the key store on first run. Ignored once a key store entry exists for this AppId.
    /// </summary>
    public string SeedKey { get; set; } = "";

    public IReadOnlyList<ITransferTarget> Targets { get; set; } = Array.Empty<ITransferTarget>();
}

public class FileRelayOptions
{
    public string BasePath { get; set; } = "/transfer";
    public int ChunkSizeMB { get; set; } = 50;
    public ITransferStateStore StateStore { get; set; } = new InMemoryTransferStateStore();
    public IReadOnlyList<AppUser> Users { get; set; } = Array.Empty<AppUser>();
    public ITransferCompleteHandler? OnComplete { get; set; }

    /// <summary>
    /// Enables key rotation. When set, all authentication goes through the key store;
    /// AppUser.SeedKey is only used to bootstrap a new app on first run.
    /// </summary>
    public IKeyStore? KeyStore { get; set; }

    /// <summary>
    /// How long the previous key remains valid after the new key is first used.
    /// Only applies when KeyStore is configured. Default 1 hour.
    /// </summary>
    public TimeSpan KeyGracePeriod { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Reject any request that did not arrive over HTTPS. Default true.
    /// Set to false only for local/test deployments where TLS is not available.
    /// </summary>
    public bool RequireHttps { get; set; } = true;

    /// <summary>
    /// Allow chunk upload requests over plain HTTP even when RequireHttps is true.
    /// Safe to enable when the payload is pre-encrypted: chunk endpoints authenticate via a
    /// per-chunk HMAC token bound to the app ID, transfer ID, chunk index, and run index —
    /// the API key is never sent on these requests. Control-plane endpoints (negotiate,
    /// rotate-key, ping) continue to require HTTPS.
    /// </summary>
    public bool AllowHttpChunks { get; set; } = false;

    /// <summary>
    /// The plain-HTTP port this server listens on. When AllowHttpChunks is true and this
    /// is non-zero, the server advertises this port in negotiate responses so clients can
    /// automatically route chunk data over HTTP without any client-side configuration.
    /// </summary>
    public int HttpPort { get; set; }

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
