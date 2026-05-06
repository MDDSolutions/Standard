namespace FileRelay.Core.Models;

public class TransferStatusResponse
{
    public Guid TransferId { get; set; }
    public string Filename { get; set; } = "";
    public int ChunksTotal { get; set; }
    public int ChunksConfirmed { get; set; }
    public IReadOnlyList<int> ChunksNeeded { get; set; } = Array.Empty<int>();
    public bool IsComplete { get; set; }
}
