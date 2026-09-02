using System.Text.Json;
using EDom.Web.Models;

namespace EDom.Web.Services;

public sealed class TenantOverpaymentStore
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        };

    private readonly string filePath;

    public TenantOverpaymentStore(string contentRootPath)
    {
        filePath = Path.Combine(
            contentRootPath,
            "App_Data",
            "Rental",
            "tenant-overpayments.json");
    }

    public async Task<IReadOnlyList<TenantOverpaymentRecord>> GetForHouseholdAsync(
        Guid householdId,
        CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            var all = await LoadUnsafeAsync(cancellationToken);
            return all
                .Where(x => x.HouseholdId == householdId)
                .OrderByDescending(x => x.CreatedAtUtc)
                .ToArray();
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task<TenantOverpaymentRecord> AddDecisionAsync(
        TenantOverpaymentRecord item,
        CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            var all = await LoadUnsafeAsync(cancellationToken);
            all.Add(item);
            await SaveUnsafeAsync(all, cancellationToken);
            return item;
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task<bool> AddApplicationAsync(
        Guid householdId,
        Guid creditId,
        Guid targetSettlementId,
        string targetPeriodKey,
        long amountMinor,
        CancellationToken cancellationToken)
    {
        if (amountMinor <= 0)
        {
            return false;
        }

        await Gate.WaitAsync(cancellationToken);
        try
        {
            var all = await LoadUnsafeAsync(cancellationToken);
            var credit = all.FirstOrDefault(x =>
                x.HouseholdId == householdId &&
                x.Id == creditId &&
                x.Decision == TenantOverpaymentDecisions.CarryForward);

            if (credit is null)
            {
                return false;
            }

            if (credit.Applications.Any(x =>
                    x.TargetSettlementId == targetSettlementId &&
                    string.Equals(
                        x.TargetPeriodKey,
                        targetPeriodKey,
                        StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            var used = credit.Applications.Sum(x => x.AmountMinor);
            var available = Math.Max(0, credit.AmountMinor - used);
            var accepted = Math.Min(available, amountMinor);

            if (accepted <= 0)
            {
                return false;
            }

            credit.Applications.Add(new TenantOverpaymentApplicationRecord
            {
                Id = Guid.NewGuid(),
                TargetSettlementId = targetSettlementId,
                TargetPeriodKey = targetPeriodKey,
                AmountMinor = accepted,
                AppliedAtUtc = DateTime.UtcNow
            });

            await SaveUnsafeAsync(all, cancellationToken);
            return true;
        }
        finally
        {
            Gate.Release();
        }
    }

    private async Task<List<TenantOverpaymentRecord>> LoadUnsafeAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
        {
            return [];
        }

        await using var stream = File.OpenRead(filePath);
        return await JsonSerializer.DeserializeAsync<List<TenantOverpaymentRecord>>(
                   stream,
                   JsonOptions,
                   cancellationToken)
               ?? [];
    }

    private async Task SaveUnsafeAsync(
        List<TenantOverpaymentRecord> items,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);

        var tempPath = filePath + ".tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(
                stream,
                items,
                JsonOptions,
                cancellationToken);
        }

        File.Move(tempPath, filePath, true);
    }
}
