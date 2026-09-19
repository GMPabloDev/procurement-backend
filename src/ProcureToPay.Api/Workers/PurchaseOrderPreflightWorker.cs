using ProcureToPay.Infrastructure.Persistence.PurchaseOrders;

namespace ProcureToPay.Api.Workers;

/// <summary>
/// Repeats the Purchase Orders takeover preflight (SPEC 11 REQ-10/REQ-12, CA-10). The first sweep runs
/// immediately, so a database that could not be read at startup keeps the command gate closed until a
/// later sweep verifies the fence and reopens it; a deployment that drifts afterwards closes it again.
/// </summary>
public sealed class PurchaseOrderPreflightWorker(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<PurchaseOrderPreflightWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(
            configuration.GetValue("PurchaseOrders:PreflightIntervalSeconds", 60));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                await scope.ServiceProvider
                    .GetRequiredService<PurchaseOrderContractPreflight>()
                    .EnsureTakeoverExpansionAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "The purchase order takeover preflight failed; commands stay unavailable.");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
