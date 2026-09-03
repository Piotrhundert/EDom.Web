using System.Text.Json;
using EDom.Web.Models;

namespace EDom.Web.Services;

public sealed class HouseholdCashToBankTransferStore
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        };

    private readonly string filePath;

    public HouseholdCashToBankTransferStore(
        string contentRootPath)
    {
        filePath = Path.Combine(
            contentRootPath,
            "App_Data",
            "HouseholdFinance",
            "cash-to-bank-transfers.json");
    }

    public async Task<IReadOnlyList<HouseholdCashToBankTransferRecord>> GetAsync(
        Guid householdId,
        CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken);

        try
        {
            var all = await LoadUnsafeAsync(
                cancellationToken);

            return all
                .Where(x => x.HouseholdId == householdId)
                .OrderByDescending(x => x.TransferredAtUtc)
                .ThenByDescending(x => x.CreatedAtUtc)
                .ToArray();
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task AddAsync(
        HouseholdCashToBankTransferRecord item,
        CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken);

        try
        {
            var all = await LoadUnsafeAsync(
                cancellationToken);

            all.Add(item);

            await SaveUnsafeAsync(
                all,
                cancellationToken);
        }
        finally
        {
            Gate.Release();
        }
    }

    private async Task<List<HouseholdCashToBankTransferRecord>> LoadUnsafeAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
        {
            return [];
        }

        await using var stream =
            File.OpenRead(filePath);

        return await JsonSerializer.DeserializeAsync<List<HouseholdCashToBankTransferRecord>>(
                   stream,
                   JsonOptions,
                   cancellationToken)
               ?? [];
    }

    private async Task SaveUnsafeAsync(
        List<HouseholdCashToBankTransferRecord> items,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(
            Path.GetDirectoryName(filePath)!);

        var tempPath = filePath + ".tmp";

        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(
                stream,
                items,
                JsonOptions,
                cancellationToken);
        }

        File.Move(
            tempPath,
            filePath,
            true);
    }
}
