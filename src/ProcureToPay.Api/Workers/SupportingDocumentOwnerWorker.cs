using ProcureToPay.Infrastructure.Persistence.PurchaseOrders;

namespace ProcureToPay.Api.Workers;

/// <summary>
/// Productive hosted worker of the supporting document owner (SPEC 11 REQ-08, NFR-03): the documented
/// worker is separate from Sourcing and runs the real processor on a bounded interval.
/// </summary>
public sealed class SupportingDocumentOwnerWorker(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<SupportingDocumentOwnerWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(
            configuration.GetValue("PurchaseOrders:SupportingDocumentWorkerIntervalSeconds", 5));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var processor = scope.ServiceProvider.GetRequiredService<SupportingDocumentOwnerProcessor>();
                await processor.ProcessDueAsync(DateTimeOffset.UtcNow, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Supporting document worker sweep failed; it will retry.");
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
