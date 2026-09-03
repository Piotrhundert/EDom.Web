namespace EDom.Web.Models;

public sealed class UtilityContractChangeRecord
{
    public Guid Id { get; set; }
    public Guid HouseholdId { get; set; }
    public Guid ContractId { get; set; }
    public DateTime ChangedAtUtc { get; set; }
    public Guid ChangedByUserAccountId { get; set; }
    public string Reason { get; set; } = "";
    public List<UtilityContractFieldChange> Changes { get; set; } = [];
}

public sealed class UtilityContractFieldChange
{
    public string Field { get; set; } = "";
    public string? Before { get; set; }
    public string? After { get; set; }
}
