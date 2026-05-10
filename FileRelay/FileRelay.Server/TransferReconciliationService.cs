using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FileRelay.Server;

internal sealed class TransferReconciliationService : BackgroundService
{
    private readonly ChunkedTransferOptions _options;
    private readonly ILogger<TransferReconciliationService> _logger;

    public TransferReconciliationService(ChunkedTransferOptions options, ILogger<TransferReconciliationService> logger)
    {
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Stagger the first run by a minute so startup noise has settled.
        await Task.Delay(TimeSpan.FromMinutes(1), ct);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RunAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Transfer reconciliation failed.");
            }

            await Task.Delay(TimeSpan.FromHours(24), ct);
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        await PruneCompletedAsync(ct);
        await ReconcileIncompleteAsync(ct);
    }

    private async Task PruneCompletedAsync(CancellationToken ct)
    {
        await _options.StateStore.PruneCompletedAsync(_options.CompletedTransferRetention);
        _logger.LogInformation("Pruned completed transfers older than {Retention}.", _options.CompletedTransferRetention);
    }

    private async Task ReconcileIncompleteAsync(CancellationToken ct)
    {
        var candidates = await _options.StateStore.GetInactiveIncompleteTransfersAsync(
            _options.AbandonedTransferInactivityThreshold);

        if (candidates.Count == 0) return;

        _logger.LogInformation("Reconciling {Count} inactive incomplete transfer(s).", candidates.Count);

        foreach (var state in candidates)
        {
            ct.ThrowIfCancellationRequested();

            var targets = _options.Users.FirstOrDefault(u => u.AppId == state.AppId)?.Targets
                ?? Array.Empty<FileRelay.Core.Interfaces.ITransferTarget>();

            var intact = true;
            foreach (var target in targets)
            {
                if (!await target.IsPartialIntactAsync(state.TransferId, state.Filename, state.FileSizeBytes, state.Context, ct))
                {
                    intact = false;
                    break;
                }
            }

            if (!intact)
            {
                await _options.StateStore.DeleteTransferStateAsync(state.TransferId);
                _logger.LogWarning(
                    "Deleted state for transfer {TransferId} ({Filename}) — partial file missing or wrong size.",
                    state.TransferId, state.Filename);
            }
        }
    }
}
