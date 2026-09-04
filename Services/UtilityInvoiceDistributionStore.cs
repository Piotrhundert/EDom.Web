using System.Text.Json;
using EDom.Web.Models;

namespace EDom.Web.Services;

public sealed class UtilityInvoiceDistributionStore
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        };

    private readonly string filePath;

    public UtilityInvoiceDistributionStore(
        string contentRootPath)
    {
        filePath = Path.Combine(
            contentRootPath,
            "App_Data",
            "Utilities",
            "invoice-distributions.json");
    }

    public async Task<IReadOnlyList<UtilityInvoiceDistributionRecord>> GetAsync(
        Guid householdId,
        CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken);

        try
        {
            var all =
                await LoadUnsafeAsync(
                    cancellationToken);

            return all
                .Where(x =>
                    x.HouseholdId == householdId)
                .OrderByDescending(x =>
                    x.CreatedAtUtc)
                .ToArray();
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task<bool> TenantChargeExistsAsync(
        Guid householdId,
        Guid utilityInvoiceId,
        Guid leaseContractId,
        CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken);

        try
        {
            var all =
                await LoadUnsafeAsync(
                    cancellationToken);

            return all.Any(x =>
                x.HouseholdId == householdId
                && x.UtilityInvoiceId == utilityInvoiceId
                && x.LeaseContractId == leaseContractId
                && string.Equals(
                    x.RecordType,
                    "TenantCharge",
                    StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task<bool> SummaryExistsAsync(
        Guid householdId,
        Guid utilityInvoiceId,
        CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken);

        try
        {
            var all =
                await LoadUnsafeAsync(
                    cancellationToken);

            return all.Any(x =>
                x.HouseholdId == householdId
                && x.UtilityInvoiceId == utilityInvoiceId
                && string.Equals(
                    x.RecordType,
                    "Summary",
                    StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task AddAsync(
        UtilityInvoiceDistributionRecord record,
        CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken);

        try
        {
            var all =
                await LoadUnsafeAsync(
                    cancellationToken);

            all.Add(record);

            await SaveUnsafeAsync(
                all,
                cancellationToken);
        }
        finally
        {
            Gate.Release();
        }
    }

    private async Task<List<UtilityInvoiceDistributionRecord>> LoadUnsafeAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
        {
            return [];
        }

        await using var stream =
            File.OpenRead(filePath);

        return await JsonSerializer.DeserializeAsync<List<UtilityInvoiceDistributionRecord>>(
                   stream,
                   JsonOptions,
                   cancellationToken)
               ?? [];
    }

    private async Task SaveUnsafeAsync(
        List<UtilityInvoiceDistributionRecord> records,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(
            Path.GetDirectoryName(filePath)!);

        var tempPath =
            filePath + ".tmp";

        await using (var stream =
                     File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(
                stream,
                records,
                JsonOptions,
                cancellationToken);
        }

        File.Move(
            tempPath,
            filePath,
            true);
    }
}
