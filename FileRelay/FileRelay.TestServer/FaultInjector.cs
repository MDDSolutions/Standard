public class FaultInjector
{
    private readonly object _lock = new();
    private readonly List<(Guid? TransferId, int ChunkIndex)> _queue = [];

    public void Enqueue(int chunkIndex, Guid? transferId = null)
    {
        lock (_lock) _queue.Add((transferId, chunkIndex));
    }

    // Consumes the first matching entry — specific transferId match preferred over wildcard.
    public bool TryConsume(Guid transferId, int chunkIndex)
    {
        lock (_lock)
        {
            var idx = _queue.FindIndex(f => f.TransferId == transferId && f.ChunkIndex == chunkIndex);
            if (idx < 0)
                idx = _queue.FindIndex(f => f.TransferId == null && f.ChunkIndex == chunkIndex);
            if (idx < 0) return false;
            _queue.RemoveAt(idx);
            return true;
        }
    }
}
