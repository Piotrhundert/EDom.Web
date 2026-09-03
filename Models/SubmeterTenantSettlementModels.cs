namespace EDom.Web.Models;

public sealed class RentalSigningMeterOptionViewModel
{
    public Guid MeterId { get; init; }
    public Guid RoomId { get; init; }
    public string RoomName { get; init; } = "";
    public string MeterName { get; init; } = "";
    public string Medium { get; init; } = "";
    public string UnitCode { get; init; } = "";
}

public sealed class SubmeterTenantChargeRecord
{
    public Guid Id { get; set; }
    public Guid HouseholdId { get; set; }
    public Guid MeterId { get; set; }
    public Guid PreviousReadingId { get; set; }
    public Guid CurrentReadingId { get; set; }
    public Guid LeaseContractId { get; set; }
    public Guid SettlementId { get; set; }
    public Guid RoomId { get; set; }
    public string RoomName { get; set; } = "";
    public string TenantName { get; set; } = "";
    public string PeriodKey { get; set; } = "";
    public string Medium { get; set; } = "";
    public string ZoneCode { get; set; } = "ALL";
    public string UnitCode { get; set; } = "";
    public decimal PreviousValue { get; set; }
    public decimal CurrentValue { get; set; }
    public decimal Consumption { get; set; }
    public decimal RatePerUnit { get; set; }
    public long AmountMinor { get; set; }
    public string CurrencyCode { get; set; } = "PLN";
    public string RateSource { get; set; } = "Manual";
    public DateTime CreatedAtUtc { get; set; }
    public Guid CreatedByUserAccountId { get; set; }
}
