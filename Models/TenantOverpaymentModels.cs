namespace EDom.Web.Models;

public sealed class TenantOverpaymentRecord
{
    public Guid Id { get; set; }
    public Guid HouseholdId { get; set; }
    public Guid LeaseContractId { get; set; }
    public Guid SourceSettlementId { get; set; }
    public string SourcePeriodKey { get; set; } = "";
    public Guid PayerPersonId { get; set; }
    public string TenantName { get; set; } = "";
    public string RoomName { get; set; } = "";
    public long AmountMinor { get; set; }
    public string CurrencyCode { get; set; } = "PLN";
    public string Decision { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
    public Guid CreatedByUserAccountId { get; set; }
    public DateOnly? RefundedOn { get; set; }
    public string? RefundMethod { get; set; }
    public string? Note { get; set; }
    public List<TenantOverpaymentApplicationRecord> Applications { get; set; } = [];
}

public sealed class TenantOverpaymentApplicationRecord
{
    public Guid Id { get; set; }
    public Guid TargetSettlementId { get; set; }
    public string TargetPeriodKey { get; set; } = "";
    public long AmountMinor { get; set; }
    public DateTime AppliedAtUtc { get; set; }
}

public static class TenantOverpaymentDecisions
{
    public const string CarryForward = "CarryForward";
    public const string Refunded = "Refunded";
}
