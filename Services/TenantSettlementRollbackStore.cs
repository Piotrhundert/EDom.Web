using System.Text.Json;
using EDom.Web.Models;

namespace EDom.Web.Services;

public sealed class TenantSettlementRollbackStore
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string filePath;

    public TenantSettlementRollbackStore(string contentRootPath)
    {
        filePath = Path.Combine(
            contentRootPath,
            "App_Data",
            "Rental",
            "tenant-settlement-rollbacks.json");
    }

    public async Task<IReadOnlyList<TenantSettlementRollbackRecord>> GetForHouseholdAsync(
        Guid householdId,
        CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            var all = await LoadUnsafeAsync(cancellationToken);
            return all
                .Where(x => x.HouseholdId == householdId)
                .OrderByDescending(x => x.ReopenedAtUtc)
                .ToArray();
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task AddAsync(
        TenantSettlementRollbackRecord item,
        CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            var all = await LoadUnsafeAsync(cancellationToken);
            all.Add(item);
            await SaveUnsafeAsync(all, cancellationToken);
        }
        finally
        {
            Gate.Release();
        }
    }

    private async Task<List<TenantSettlementRollbackRecord>> LoadUnsafeAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
            return [];

        await using var stream = File.OpenRead(filePath);
        return await JsonSerializer.DeserializeAsync<List<TenantSettlementRollbackRecord>>(
                   stream,
                   JsonOptions,
                   cancellationToken)
               ?? [];
    }

    private async Task SaveUnsafeAsync(
        List<TenantSettlementRollbackRecord> items,
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
