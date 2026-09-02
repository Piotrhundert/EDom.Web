using System.Text.Json;
using EDom.Web.Models;

namespace EDom.Web.Services;

public sealed class RentalContractAnnexUiStore
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        };

    private readonly string filePath;

    public RentalContractAnnexUiStore(string contentRootPath)
    {
        filePath = Path.Combine(
            contentRootPath,
            "App_Data",
            "Rental",
            "contract-annex-ui-history.json");
    }

    public async Task<IReadOnlyList<RentalAnnexUiRecord>> GetForHouseholdAsync(
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

    public async Task<RentalAnnexUiRecord?> GetAsync(
        Guid householdId,
        Guid annexId,
        CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            var all = await LoadUnsafeAsync(cancellationToken);
            return all.FirstOrDefault(x =>
                x.HouseholdId == householdId &&
                x.Id == annexId);
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task<RentalAnnexUiRecord> AddAsync(
        Guid householdId,
        Guid contractId,
        DateOnly effectiveOn,
        string tenantName,
        string roomName,
        string currencyCode,
        long oldRentAmountMinor,
        long? newRentAmountMinor,
        DateOnly? oldLeaseTo,
        DateOnly? newLeaseTo,
        string? clauseTitle,
        string? clauseText,
        string? reason,
        Guid createdByUserAccountId,
        CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            var all = await LoadUnsafeAsync(cancellationToken);

            var nextNumber = all
                .Where(x =>
                    x.HouseholdId == householdId &&
                    x.ContractId == contractId)
                .Select(x => x.AnnexNumber)
                .DefaultIfEmpty(0)
                .Max() + 1;

            var item = new RentalAnnexUiRecord(
                Guid.NewGuid(),
                householdId,
                contractId,
                nextNumber,
                effectiveOn,
                tenantName,
                roomName,
                currencyCode,
                oldRentAmountMinor,
                newRentAmountMinor,
                oldLeaseTo,
                newLeaseTo,
                Clean(clauseTitle),
                Clean(clauseText),
                Clean(reason),
                DateTime.UtcNow,
                createdByUserAccountId);

            all.Add(item);
            await SaveUnsafeAsync(all, cancellationToken);
            return item;
        }
        finally
        {
            Gate.Release();
        }
    }

    private async Task<List<RentalAnnexUiRecord>> LoadUnsafeAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
        {
            return [];
        }

        await using var stream = File.OpenRead(filePath);
        return await JsonSerializer.DeserializeAsync<List<RentalAnnexUiRecord>>(
                   stream,
                   JsonOptions,
                   cancellationToken)
               ?? [];
    }

    private async Task SaveUnsafeAsync(
        List<RentalAnnexUiRecord> items,
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

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim();
}
