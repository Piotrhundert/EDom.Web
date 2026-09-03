namespace EDom.Web.Models;

public sealed class HouseholdCashToBankTransferRecord
{
    public Guid Id { get; set; }
    public Guid HouseholdId { get; set; }
    public long AmountMinor { get; set; }
    public string CurrencyCode { get; set; } = "PLN";
    public DateTime TransferredAtUtc { get; set; }
    public string? Note { get; set; }
    public long CashBeforeMinor { get; set; }
    public long CashAfterMinor { get; set; }
    public long BankBeforeMinor { get; set; }
    public long BankAfterMinor { get; set; }
    public Guid CreatedByUserAccountId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}
