using System.Text.Json;
using EDom.Web.Models;

namespace EDom.Web.Services;

public sealed class SubmeterTenantChargeStore
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        };

    private readonly string filePath;

    public SubmeterTenantChargeStore(string contentRootPath)
    {
        filePath = Path.Combine(
            contentRootPath,
            "App_Data",
            "Utilities",
            "submeter-tenant-charges.json");
    }

    public async Task<IReadOnlyList<SubmeterTenantChargeRecord>> GetAsync(
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

    public async Task AddAsync(
        SubmeterTenantChargeRecord record,
        CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken);

        try
        {
            var all = await LoadUnsafeAsync(cancellationToken);
            all.Add(record);
            await SaveUnsafeAsync(all, cancellationToken);
        }
        finally
        {
            Gate.Release();
        }
    }

    private async Task<List<SubmeterTenantChargeRecord>> LoadUnsafeAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
        {
            return [];
        }

        await using var stream = File.OpenRead(filePath);

        return await JsonSerializer.DeserializeAsync<List<SubmeterTenantChargeRecord>>(
                   stream,
                   JsonOptions,
                   cancellationToken)
               ?? [];
    }

    private async Task SaveUnsafeAsync(
        List<SubmeterTenantChargeRecord> records,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);

        var tempPath = filePath + ".tmp";

        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(
                stream,
                records,
                JsonOptions,
                cancellationToken);
        }

        File.Move(tempPath, filePath, true);
    }
}
