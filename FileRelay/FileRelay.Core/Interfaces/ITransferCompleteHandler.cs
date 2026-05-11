using FileRelay.Core.Models;

namespace FileRelay.Core.Interfaces;

public interface ITransferCompleteHandler
{
    /// <summary>
    /// Called after the file is finalized on disk but before the 200 is sent to the client.
    /// Return false to reject the transfer — the client receives a 422. Throw to signal an
    /// unexpected error — the client receives a 500. In either case the file is already renamed
    /// (finalization happened before this call). Use this for post-write validation that the
    /// client should know about synchronously, e.g. virus scan, schema check.
    /// </summary>
    Task<bool> OnValidateAsync(CompletedTransfer transfer, CancellationToken ct);

    /// <summary>
    /// Called after the 200 has been sent. Errors are logged and swallowed — the client is
    /// already gone. Use this for notifications, indexing, or other work that does not need
    /// to block the client acknowledgement.
    /// </summary>
    Task OnCompleteAsync(CompletedTransfer transfer, CancellationToken ct);
}
