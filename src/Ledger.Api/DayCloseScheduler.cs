using Ledger.Core;
using Ledger.Data;

namespace Ledger.Api;

/// <summary>
/// Closes business days as they end. Every replica runs it; closing is idempotent and serialised per store in the database,
/// so two replicas closing the same day at once is harmless. Off unless Ledger:AutoCloseIntervalSeconds is above 0.
/// </summary>
public sealed class DayCloseScheduler(DayCloser closer, LedgerOptions options, ILogger<DayCloseScheduler> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (options.AutoCloseIntervalSeconds <= 0) return;

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.AutoCloseIntervalSeconds));
        do
        {
            try
            {
                var closed = await closer.CloseDueAsync(ct);
                if (closed.Count > 0) log.LogInformation("Closed business days for {Stores} stores", closed.Count);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Closing due business days failed; will retry on the next tick");
            }
        } while (await timer.WaitForNextTickAsync(ct));
    }
}
