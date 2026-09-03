namespace EDom.Web.Models;

public sealed class TenantSettlementCorrectionUiRecord
{
    public Guid Id { get; set; }
    public Guid HouseholdId { get; set; }
    public Guid SettlementId { get; set; }
    public string CorrectionType { get; set; } = "Other";
    public long DeltaMinor { get; set; }
    public string CurrencyCode { get; set; } = "PLN";
    public DateOnly? DueDate { get; set; }
    public string? Reason { get; set; }
    public string TenantName { get; set; } = "";
    public string RoomName { get; set; } = "";
    public string PeriodKey { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
    public Guid CreatedByUserAccountId { get; set; }
}
