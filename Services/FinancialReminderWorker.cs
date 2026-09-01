using EDom.Domain.Households;
using EDom.Domain.Identity;
using EDom.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EDom.Web.Services;

public sealed class FinancialReminderWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<FinancialReminderWorker> logger) : BackgroundService
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ScanAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Financial reminder scan failed.");
            }

            await Task.Delay(ScanInterval, stoppingToken);
        }
    }

    private async Task ScanAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EDomDbContext>();
        var reminderService = scope.ServiceProvider.GetRequiredService<FinanceReminderService>();

        var memberships = await db.HouseholdMemberships.AsNoTracking()
            .Where(x => x.Status == MembershipStatuses.Active && x.ValidTo == null)
            .Select(x => new { x.HouseholdId, x.PersonId })
            .Distinct()
            .ToListAsync(cancellationToken);

        if (memberships.Count == 0) return;

        var personIds = memberships.Select(x => x.PersonId).Distinct().ToArray();
        var accounts = await db.UserAccounts.AsNoTracking()
            .Where(x => x.PersonId != null && personIds.Contains(x.PersonId.Value) && x.Status != UserAccountStatuses.Deleted)
            .Select(x => new { x.Id, PersonId = x.PersonId!.Value, x.Status })
            .ToListAsync(cancellationToken);

        var published = 0;
        foreach (var membership in memberships)
        {
            var account = accounts.FirstOrDefault(x => x.PersonId == membership.PersonId &&
                (x.Status == UserAccountStatuses.Active || x.Status == UserAccountStatuses.Locked));
            if (account is null) continue;

            published += await reminderService.PublishForAccountAsync(
                membership.HouseholdId,
                account.Id,
                membership.PersonId,
                cancellationToken);
        }

        if (published > 0)
            logger.LogInformation("Financial reminder scan published {Count} notification candidates.", published);
    }
}
