using Affiant.Abstractions.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Affiant.Docket.Services;

public sealed class DocketExpiryService(
    IServiceScopeFactory scopeFactory,
    ILogger<DocketExpiryService> logger) : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TickInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await ExpireOverdueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "DocketExpiryService tick failed");
            }
        }
    }

    /// <summary>
    /// Runs one expiry tick. Public for testability — integration tests call this directly
    /// instead of waiting for the background timer.
    /// </summary>
    public async Task ExpireOverdueAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocketStore>();

        var now = DateTimeOffset.UtcNow;
        var expired = await store.ListExpiredAsync(now, ct);
        var expiredIds = expired.Select(e => e.EntryId).ToList();

        if (expiredIds.Count > 0)
        {
            await store.MarkExpiredAsync(expiredIds, ct);
            logger.LogInformation("Marked {Count} docket entries as expired", expiredIds.Count);
        }
    }
}
