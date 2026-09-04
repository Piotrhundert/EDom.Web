namespace EDom.Web.Models;

public sealed class UtilityInvoiceDistributionRecord
{
    public Guid Id { get; set; }
    public string RecordType { get; set; } = "TenantCharge";
    public Guid HouseholdId { get; set; }
    public Guid UtilityInvoiceId { get; set; }
    public Guid UtilityContractId { get; set; }
    public string InvoiceNo { get; set; } = "";
    public string Medium { get; set; } = "";
    public string PeriodKey { get; set; } = "";
    public long GrossAmountMinor { get; set; }
    public long HouseholdShareMinor { get; set; }
    public long TenantShareMinor { get; set; }
    public int HouseholdPersonCount { get; set; }
    public int TenantPersonCount { get; set; }
    public string CurrencyCode { get; set; } = "PLN";
    public string AllocationMode { get; set; } = "";
    public Guid? LeaseContractId { get; set; }
    public Guid? SettlementId { get; set; }
    public string? TenantName { get; set; }
    public int TenantPersons { get; set; }
    public long TenantAmountMinor { get; set; }
    public string? SettlementOperation { get; set; }

    // Dane wejściowe zachowane do późniejszego rozliczenia lokatorów.
    // Dla wody rozliczenie jest generowane dopiero po pełnym opłaceniu FV przez dom.
    public DateOnly? PeriodFrom { get; set; }
    public DateOnly? PeriodTo { get; set; }
    public DateOnly? IssuedOn { get; set; }
    public DateOnly? DueDate { get; set; }
    public string? TotalConsumptionText { get; set; }
    public string? TenantConsumptionText { get; set; }
    public long? ManualTenantAmountMinor { get; set; }
    public string? TenantOccupancyJson { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public Guid CreatedByUserAccountId { get; set; }
}

public sealed record UtilityTenantOccupancyInput(
    Guid LeaseContractId,
    int Persons);
