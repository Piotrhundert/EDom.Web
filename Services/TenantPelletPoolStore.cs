using System.Text.Json;
using EDom.Web.Models;

namespace EDom.Web.Services;

public sealed class TenantPelletPoolStore
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string filePath;

    public TenantPelletPoolStore(string contentRootPath)
    {
        filePath = Path.Combine(
            contentRootPath,
            "App_Data",
            "Rental",
            "tenant-pellet-pools.json");
    }

    public async Task<TenantPelletPoolData> GetAsync(
        Guid householdId,
        CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            var data = await LoadUnsafeAsync(cancellationToken);
            Normalize(data);

            return new TenantPelletPoolData
            {
                Pools = data.Pools.Where(x => x.HouseholdId == householdId).ToList(),
                Plans = data.Plans.Where(x => x.HouseholdId == householdId).ToList(),
                Applications = data.Applications.Where(x => x.HouseholdId == householdId).ToList(),
                Purchases = data.Purchases.Where(x => x.HouseholdId == householdId).ToList(),
                InvoiceAdjustments = data.InvoiceAdjustments.Where(x => x.HouseholdId == householdId).ToList()
            };
        }
        finally { Gate.Release(); }
    }

    public async Task<TenantPelletPoolRecord> CreatePoolAsync(
        TenantPelletPoolRecord item,
        CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            var data = await LoadUnsafeAsync(cancellationToken);
            Normalize(data);

            var overlaps = data.Pools.Any(x =>
                x.HouseholdId == item.HouseholdId
                && x.BuildingId == item.BuildingId
                && x.Status == TenantPelletPoolStatuses.Active
                && x.PeriodFrom <= item.PeriodTo
                && x.PeriodTo >= item.PeriodFrom);

            if (overlaps)
            {
                throw new InvalidOperationException(
                    "Dla tego domu istnieje już aktywna pula pelletu obejmująca wskazany okres.");
            }

            data.Pools.Add(item);
            await SaveUnsafeAsync(data, cancellationToken);
            return item;
        }
        finally { Gate.Release(); }
    }

    public async Task<TenantPelletPoolUpsertResult> UpsertInvoicePurchaseAsync(
        Guid householdId,
        Guid buildingId,
        string buildingName,
        string seasonName,
        DateOnly periodFrom,
        DateOnly periodTo,
        DateOnly purchaseDate,
        long amountMinor,
        string currencyCode,
        decimal? palletCount,
        decimal? weightKg,
        string supplier,
        string sourceInvoiceNo,
        string? notes,
        Guid createdByUserAccountId,
        CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            var data = await LoadUnsafeAsync(cancellationToken);
            Normalize(data);

            var duplicatePurchase = data.Purchases.FirstOrDefault(x =>
                x.HouseholdId == householdId
                && x.BuildingId == buildingId
                && string.Equals(x.SourceInvoiceNo, sourceInvoiceNo, StringComparison.OrdinalIgnoreCase)
                && string.Equals(x.Supplier, supplier, StringComparison.OrdinalIgnoreCase));

            if (duplicatePurchase is not null)
            {
                var duplicatePool = data.Pools.First(x => x.Id == duplicatePurchase.PoolId);
                return new(duplicatePool, duplicatePurchase, false, true, 0);
            }

            var pool = data.Pools
                .Where(x =>
                    x.HouseholdId == householdId
                    && x.BuildingId == buildingId
                    && x.Status == TenantPelletPoolStatuses.Active
                    && x.PeriodFrom <= periodTo
                    && x.PeriodTo >= periodFrom)
                .OrderBy(x => x.PeriodFrom)
                .ThenBy(x => x.CreatedAtUtc)
                .FirstOrDefault();

            var poolCreated = false;
            var linkedToExistingManualPool = false;
            long addedToPoolMinor;

            if (pool is null)
            {
                pool = new TenantPelletPoolRecord
                {
                    Id = Guid.NewGuid(),
                    HouseholdId = householdId,
                    BuildingId = buildingId,
                    BuildingName = buildingName,
                    SeasonName = seasonName,
                    PeriodFrom = periodFrom,
                    PeriodTo = periodTo,
                    PurchaseDate = purchaseDate,
                    TotalAmountMinor = amountMinor,
                    CurrencyCode = currencyCode,
                    PalletCount = palletCount,
                    WeightKg = weightKg,
                    Supplier = supplier,
                    DocumentNo = sourceInvoiceNo,
                    Notes = notes,
                    Status = TenantPelletPoolStatuses.Active,
                    CreatedAtUtc = DateTime.UtcNow,
                    CreatedByUserAccountId = createdByUserAccountId
                };

                data.Pools.Add(pool);
                poolCreated = true;
                addedToPoolMinor = amountMinor;
            }
            else
            {
                var hasRecordedPurchases = data.Purchases.Any(x => x.PoolId == pool.Id);

                var looksLikeManualVersionOfSameInvoice =
                    !hasRecordedPurchases
                    && (
                        string.Equals(pool.DocumentNo, sourceInvoiceNo, StringComparison.OrdinalIgnoreCase)
                        || (
                            pool.TotalAmountMinor == amountMinor
                            && string.Equals(pool.Supplier, supplier, StringComparison.OrdinalIgnoreCase)
                            && pool.PurchaseDate == purchaseDate
                        )
                    );

                if (looksLikeManualVersionOfSameInvoice)
                {
                    linkedToExistingManualPool = true;
                    addedToPoolMinor = 0;
                }
                else
                {
                    if (!string.Equals(pool.CurrencyCode, currencyCode, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(
                            $"Istniejąca pula pelletu jest w {pool.CurrencyCode}, a faktura w {currencyCode}.");
                    }

                    pool.TotalAmountMinor = checked(pool.TotalAmountMinor + amountMinor);
                    pool.PeriodFrom = pool.PeriodFrom < periodFrom ? pool.PeriodFrom : periodFrom;
                    pool.PeriodTo = pool.PeriodTo > periodTo ? pool.PeriodTo : periodTo;

                    if (palletCount.HasValue)
                        pool.PalletCount = (pool.PalletCount ?? 0m) + palletCount.Value;
                    if (weightKg.HasValue)
                        pool.WeightKg = (pool.WeightKg ?? 0m) + weightKg.Value;

                    addedToPoolMinor = amountMinor;
                }
            }

            var purchase = new TenantPelletPurchaseRecord
            {
                Id = Guid.NewGuid(),
                HouseholdId = householdId,
                PoolId = pool.Id,
                BuildingId = buildingId,
                SourceInvoiceNo = sourceInvoiceNo,
                Supplier = supplier,
                PurchaseDate = purchaseDate,
                PeriodFrom = periodFrom,
                PeriodTo = periodTo,
                AmountMinor = amountMinor,
                CurrencyCode = currencyCode,
                PalletCount = palletCount,
                WeightKg = weightKg,
                Notes = notes,
                LinkedToExistingManualPool = linkedToExistingManualPool,
                CreatedAtUtc = DateTime.UtcNow
            };

            data.Purchases.Add(purchase);
            await SaveUnsafeAsync(data, cancellationToken);

            return new(pool, purchase, poolCreated, false, addedToPoolMinor);
        }
        finally { Gate.Release(); }
    }

    public async Task<TenantPelletMonthPlanRecord> GetOrCreatePlanAsync(
        Guid householdId,
        Guid poolId,
        string periodKey,
        Func<TenantPelletPoolData, TenantPelletMonthPlanRecord> factory,
        CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            var data = await LoadUnsafeAsync(cancellationToken);
            Normalize(data);

            var existing = data.Plans.FirstOrDefault(x =>
                x.HouseholdId == householdId
                && x.PoolId == poolId
                && string.Equals(x.PeriodKey, periodKey, StringComparison.OrdinalIgnoreCase));

            if (existing is not null) return existing;

            var scoped = new TenantPelletPoolData
            {
                Pools = data.Pools.Where(x => x.HouseholdId == householdId).ToList(),
                Plans = data.Plans.Where(x => x.HouseholdId == householdId).ToList(),
                Applications = data.Applications.Where(x => x.HouseholdId == householdId).ToList(),
                Purchases = data.Purchases.Where(x => x.HouseholdId == householdId).ToList(),
                InvoiceAdjustments = data.InvoiceAdjustments.Where(x => x.HouseholdId == householdId).ToList()
            };

            var created = factory(scoped);
            data.Plans.Add(created);
            await SaveUnsafeAsync(data, cancellationToken);
            return created;
        }
        finally { Gate.Release(); }
    }

    public async Task<TenantPelletApplicationRecord?> GetApplicationAsync(
        Guid householdId,
        Guid poolId,
        Guid settlementId,
        CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            var data = await LoadUnsafeAsync(cancellationToken);
            Normalize(data);

            return data.Applications.FirstOrDefault(x =>
                x.HouseholdId == householdId
                && x.PoolId == poolId
                && x.SettlementId == settlementId);
        }
        finally { Gate.Release(); }
    }

    public async Task<TenantPelletApplicationRecord> AddApplicationAsync(
        TenantPelletApplicationRecord item,
        CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            var data = await LoadUnsafeAsync(cancellationToken);
            Normalize(data);

            var existing = data.Applications.FirstOrDefault(x =>
                x.HouseholdId == item.HouseholdId
                && x.PoolId == item.PoolId
                && x.SettlementId == item.SettlementId);

            if (existing is not null) return existing;

            data.Applications.Add(item);
            await SaveUnsafeAsync(data, cancellationToken);
            return item;
        }
        finally { Gate.Release(); }
    }

    public async Task<TenantPelletApplicationRecord> AddToApplicationAsync(
        TenantPelletApplicationRecord item,
        long additionalAmountMinor,
        CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            var data = await LoadUnsafeAsync(cancellationToken);
            Normalize(data);

            var existing = data.Applications.FirstOrDefault(x =>
                x.HouseholdId == item.HouseholdId
                && x.PoolId == item.PoolId
                && x.SettlementId == item.SettlementId);

            if (existing is null)
            {
                item.AmountMinor = additionalAmountMinor;
                data.Applications.Add(item);
                existing = item;
            }
            else
            {
                existing.AmountMinor = checked(existing.AmountMinor + additionalAmountMinor);
            }

            await SaveUnsafeAsync(data, cancellationToken);
            return existing;
        }
        finally { Gate.Release(); }
    }

    public async Task<bool> HasInvoiceAdjustmentAsync(
        Guid householdId,
        Guid purchaseId,
        Guid settlementId,
        CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            var data = await LoadUnsafeAsync(cancellationToken);
            Normalize(data);

            return data.InvoiceAdjustments.Any(x =>
                x.HouseholdId == householdId
                && x.PurchaseId == purchaseId
                && x.SettlementId == settlementId);
        }
        finally { Gate.Release(); }
    }

    public async Task AddInvoiceAdjustmentAsync(
        TenantPelletInvoiceAdjustmentRecord item,
        CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            var data = await LoadUnsafeAsync(cancellationToken);
            Normalize(data);

            if (data.InvoiceAdjustments.Any(x =>
                    x.HouseholdId == item.HouseholdId
                    && x.PurchaseId == item.PurchaseId
                    && x.SettlementId == item.SettlementId))
                return;

            data.InvoiceAdjustments.Add(item);
            await SaveUnsafeAsync(data, cancellationToken);
        }
        finally { Gate.Release(); }
    }

    private async Task<TenantPelletPoolData> LoadUnsafeAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath)) return new TenantPelletPoolData();

        await using var stream = File.OpenRead(filePath);
        return await JsonSerializer.DeserializeAsync<TenantPelletPoolData>(
                   stream, JsonOptions, cancellationToken)
               ?? new TenantPelletPoolData();
    }

    private async Task SaveUnsafeAsync(
        TenantPelletPoolData data,
        CancellationToken cancellationToken)
    {
        Normalize(data);
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);

        var tempPath = filePath + ".tmp";
        await using (var stream = File.Create(tempPath))
            await JsonSerializer.SerializeAsync(stream, data, JsonOptions, cancellationToken);

        File.Move(tempPath, filePath, true);
    }

    private static void Normalize(TenantPelletPoolData data)
    {
        data.Pools ??= [];
        data.Plans ??= [];
        data.Applications ??= [];
        data.Purchases ??= [];
        data.InvoiceAdjustments ??= [];
    }
}

public sealed class TenantPelletPoolData
{
    public List<TenantPelletPoolRecord> Pools { get; set; } = [];
    public List<TenantPelletMonthPlanRecord> Plans { get; set; } = [];
    public List<TenantPelletApplicationRecord> Applications { get; set; } = [];
    public List<TenantPelletPurchaseRecord> Purchases { get; set; } = [];
    public List<TenantPelletInvoiceAdjustmentRecord> InvoiceAdjustments { get; set; } = [];
}
