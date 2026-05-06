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
    /// Delay injected after each ~80 KB buffer read during chunk receipt.
    /// 0 = disabled. Example: 10 ms ≈ 8 MB/s, 40 ms ≈ 2 MB/s.
    /// </summary>
    public int SimulatedWanDelayPerBufferMs { get; set; } = 0;
}
