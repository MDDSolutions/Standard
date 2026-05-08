using FileRelay.Core;
using FileRelay.Core.Interfaces;

namespace FileRelay.Server;

public class ChunkedTransferOptions
{
    public string BasePath { get; set; } = "/transfer";
    public int ChunkSizeMB { get; set; } = 50;
    public ITransferStateStore StateStore { get; set; } = new InMemoryTransferStateStore();
    public IReadOnlyList<ITransferTarget> Targets { get; set; } = Array.Empty<ITransferTarget>();
    public ITransferCompleteHandler? OnComplete { get; set; }

    /// <summary>
    /// Reject any request that did not arrive over HTTPS. Default true.
    /// Set to false only for local/test deployments where TLS is not available.
    /// </summary>
    public bool RequireHttps { get; set; } = true;


    /// <summary>
    /// Pre-shared API key. When set, all transfer endpoints require
    /// Authorization: Bearer {ApiKey}. Null disables authentication.
    /// </summary>
    public string? ApiKey { get; set; }

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
    /// Set from Miscellaneous.BuildTime(Assembly.GetExecutingAssembly()) in the host application.
    /// </summary>
    public DateTime? ServerBuildTime { get; set; }

}
