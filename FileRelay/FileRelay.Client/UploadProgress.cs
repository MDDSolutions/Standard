namespace FileRelay.Client;

public class UploadProgress
{
    public int ChunksDone { get; set; }
    public int ChunksTotal { get; set; }
    public long BytesConfirmed { get; set; }
    public long BytesInFlight { get; set; }
    public long BytesTotal { get; set; }
    public double TransferRateMBps { get; set; }
    public TimeSpan? EstimatedRemaining { get; set; }

    public long BytesSent => BytesConfirmed + BytesInFlight;
    public double Percent => BytesTotal == 0 ? 0 : (double)BytesSent / BytesTotal * 100;
}
