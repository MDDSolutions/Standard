using FileRelay.Core.Models;

namespace FileRelay.Core.Interfaces;

public interface ITransferCompleteHandler
{
    Task OnCompleteAsync(CompletedTransfer transfer, CancellationToken ct);
}
