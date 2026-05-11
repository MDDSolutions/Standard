namespace FileRelay.Core.Models;

public class TransferNegotiateResponse
{
    public Guid TransferId { get; set; }
    public int ChunkSizeMB { get; set; }
    public int TotalChunks { get; set; }
    public IReadOnlyList<int> ChunksNeeded { get; set; } = Array.Empty<int>();

    /// <summary>
    /// Run indexes for chunks that require a value other than 1. Absent (null) in the
    /// overwhelming majority of responses — a chunk index not present here uses run index 1.
    /// </summary>
    public IReadOnlyDictionary<int, int>? ChunkRunIndexes { get; set; }

    /// <summary>
    /// HTTP port the server is listening on for chunk uploads. When present, the client
    /// should send chunk data over plain HTTP to this port on the same host, keeping
    /// control-plane traffic (negotiate, rotate-key) on HTTPS. Null when the server has not
    /// enabled the HTTP data path.
    /// </summary>
    public int? HttpChunkPort { get; set; }
}
