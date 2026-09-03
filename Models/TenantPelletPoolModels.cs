namespace EDom.Web.Models;

public static class TenantPelletPoolStatuses
{
    public const string Active = "Active";
    public const string Closed = "Closed";
}

public sealed class TenantPelletPoolRecord
{
    public Guid Id { get; set; }
    public Guid HouseholdId { get; set; }
    public Guid BuildingId { get; set; }
    public string BuildingName { get; set; } = "";
    public string SeasonName { get; set; } = "";
    public DateOnly PeriodFrom { get; set; }
    public DateOnly PeriodTo { get; set; }
    public DateOnly PurchaseDate { get; set; }
    public long TotalAmountMinor { get; set; }
    public string CurrencyCode { get; set; } = "PLN";
    public decimal? PalletCount { get; set; }
    public decimal? WeightKg { get; set; }
    public string? Supplier { get; set; }
    public string? DocumentNo { get; set; }
    public string? Notes { get; set; }
    public string Status { get; set; } = TenantPelletPoolStatuses.Active;
    public DateTime CreatedAtUtc { get; set; }
    public Guid CreatedByUserAccountId { get; set; }
}

public sealed class TenantPelletPurchaseRecord
{
    public Guid Id { get; set; }
    public Guid HouseholdId { get; set; }
    public Guid PoolId { get; set; }
    public Guid BuildingId { get; set; }
    public string SourceInvoiceNo { get; set; } = "";
    public string Supplier { get; set; } = "";
    public DateOnly PurchaseDate { get; set; }
    public DateOnly PeriodFrom { get; set; }
    public DateOnly PeriodTo { get; set; }
    public long AmountMinor { get; set; }
    public string CurrencyCode { get; set; } = "PLN";
    public decimal? PalletCount { get; set; }
    public decimal? WeightKg { get; set; }
    public string? Notes { get; set; }
    public bool LinkedToExistingManualPool { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

public sealed class TenantPelletInvoiceAdjustmentRecord
{
    public Guid Id { get; set; }
    public Guid HouseholdId { get; set; }
    public Guid PoolId { get; set; }
    public Guid PurchaseId { get; set; }
    public Guid SettlementId { get; set; }
    public Guid LeaseContractId { get; set; }
    public string PeriodKey { get; set; } = "";
    public long AmountMinor { get; set; }
    public string Mode { get; set; } = "";
    public string StatusBefore { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
}

public sealed record TenantPelletPoolUpsertResult(
    TenantPelletPoolRecord Pool,
    TenantPelletPurchaseRecord Purchase,
    bool PoolCreated,
    bool PurchaseWasDuplicate,
    long AddedToPoolMinor);

public sealed record TenantPelletInvoiceReconcileResult(
    int CorrectionCount,
    int OpenSettlementLineCount,
    long CorrectionAmountMinor,
    long OpenSettlementAmountMinor);

public sealed class TenantPelletMonthPlanRecord
{
    public Guid Id { get; set; }
    public Guid HouseholdId { get; set; }
    public Guid PoolId { get; set; }
    public string PeriodKey { get; set; } = "";
    public long PoolAllocatedBeforeMinor { get; set; }
    public long PoolRemainingBeforeMinor { get; set; }
    public int MonthsRemaining { get; set; }
    public long MonthlyBudgetMinor { get; set; }
    public int TenantCount { get; set; }
    public List<TenantPelletPlanShareRecord> Shares { get; set; } = [];
    public DateTime CreatedAtUtc { get; set; }
}

public sealed class TenantPelletPlanShareRecord
{
    public Guid LeaseContractId { get; set; }
    public string TenantName { get; set; } = "";
    public string RoomName { get; set; } = "";
    public long AmountMinor { get; set; }
}

public sealed class TenantPelletApplicationRecord
{
    public Guid Id { get; set; }
    public Guid HouseholdId { get; set; }
    public Guid PoolId { get; set; }
    public Guid PlanId { get; set; }
    public Guid SettlementId { get; set; }
    public Guid LeaseContractId { get; set; }
    public string PeriodKey { get; set; } = "";
    public string TenantName { get; set; } = "";
    public string RoomName { get; set; } = "";
    public long AmountMinor { get; set; }
    public string CurrencyCode { get; set; } = "PLN";
    public DateTime AppliedAtUtc { get; set; }
}


public sealed record TenantPelletCorrectionPreviewRow(
    Guid SettlementId,
    Guid LeaseContractId,
    string PeriodKey,
    string TenantName,
    string RoomName,
    string SettlementStatus,
    long MonthlyPoolMinor,
    int TenantCount,
    long TargetShareMinor,
    long AlreadyAssignedPelletMinor,
    long CorrectionNeededMinor,
    bool ClosedSettlement);

public sealed record TenantPelletCorrectionPreviewResult(
    Guid PoolId,
    string BuildingName,
    string SeasonName,
    string CurrencyCode,
    long PoolTotalMinor,
    long AlreadyAllocatedMinor,
    long ProposedCorrectionMinor,
    int ClosedCorrectionCount,
    IReadOnlyList<TenantPelletCorrectionPreviewRow> Rows);

public sealed record TenantPelletCorrectionGenerationResult(
    Guid PoolId,
    int CorrectionCount,
    long CorrectionAmountMinor,
    DateOnly DueDate);
