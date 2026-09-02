using System.Text.Json;

namespace EDom.Web.Services;

public sealed record PropertyExtendedDetailsRecord(
    Guid VersionId,
    Guid HouseholdId,
    string ObjectType,
    Guid ObjectId,
    string? AddressText,
    string? LandRegisterNumber,
    string? CadastralDistrict,
    Guid? PrimaryOwnerPersonId,
    IReadOnlyList<Guid> CoOwnerPersonIds,
    string? OwnershipShare,
    string? Notes,
    DateTime UpdatedAtUtc,
    Guid UpdatedByUserAccountId);

public sealed class PropertyExtendedDetailsStore
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string filePath;

    public PropertyExtendedDetailsStore(string contentRootPath)
    {
        filePath = Path.Combine(
            contentRootPath,
            "App_Data",
            "Property",
            "extended-property-details.json");
    }

    public async Task<IReadOnlyList<PropertyExtendedDetailsRecord>> GetLatestForHouseholdAsync(
        Guid householdId,
        CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            var all = await LoadUnsafeAsync(cancellationToken);

            return all
                .Where(x => x.HouseholdId == householdId)
                .GroupBy(x => new { x.ObjectType, x.ObjectId })
                .Select(g => g.OrderByDescending(x => x.UpdatedAtUtc).First())
                .OrderBy(x => x.ObjectType)
                .ThenBy(x => x.ObjectId)
                .ToArray();
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task AddSnapshotAsync(
        PropertyExtendedDetailsRecord item,
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

    private async Task<List<PropertyExtendedDetailsRecord>> LoadUnsafeAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
        {
            return [];
        }

        await using var stream = File.OpenRead(filePath);
        return await JsonSerializer.DeserializeAsync<List<PropertyExtendedDetailsRecord>>(
                   stream,
                   JsonOptions,
                   cancellationToken)
               ?? [];
    }

    private async Task SaveUnsafeAsync(
        List<PropertyExtendedDetailsRecord> items,
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
