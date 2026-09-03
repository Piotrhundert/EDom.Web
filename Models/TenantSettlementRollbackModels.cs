namespace EDom.Web.Models;

public sealed class TenantSettlementRollbackRecord
{
    public Guid Id { get; set; }
    public Guid HouseholdId { get; set; }
    public Guid SettlementId { get; set; }
    public Guid? LeaseContractId { get; set; }
    public Guid? PayerPersonId { get; set; }
    public string TenantName { get; set; } = "";
    public string RoomName { get; set; } = "";
    public string PeriodKey { get; set; } = "";
    public string PreviousStatus { get; set; } = "";
    public string ReopenedStatus { get; set; } = "Draft";
    public string Reason { get; set; } = "";
    public long PaidMinorAtRollback { get; set; }
    public bool KeptApprovedPayments { get; set; }
    public DateTime ReopenedAtUtc { get; set; }
    public Guid ReopenedByUserAccountId { get; set; }
}
