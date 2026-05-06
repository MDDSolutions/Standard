namespace FileRelay.Client;

public class UploadProgress
{
    public int ChunksDone { get; init; }
    public int ChunksTotal { get; init; }
    public long BytesConfirmed { get; init; }
    public long BytesInFlight { get; init; }
    public long BytesTotal { get; init; }
    public double TransferRateMBps { get; init; }
    public TimeSpan? EstimatedRemaining { get; init; }

    public long BytesSent => BytesConfirmed + BytesInFlight;
    public double Percent => BytesTotal == 0 ? 0 : (double)BytesSent / BytesTotal * 100;
}
