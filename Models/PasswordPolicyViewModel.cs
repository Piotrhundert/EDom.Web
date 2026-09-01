namespace EDom.Web.Models;

public sealed class PasswordPolicyViewModel
{
    public Guid HouseholdId { get; init; }
    public string HouseholdName { get; init; } = string.Empty;
    public string PolicySource { get; init; } = string.Empty;
    public int MinLength { get; set; }
    public int MinUpper { get; set; }
    public int MinLower { get; set; }
    public int MinDigits { get; set; }
    public int MinSpecial { get; set; }
    public int HistoryCount { get; set; }
    public int? PasswordMaxAgeDays { get; set; }
    public int LockoutThreshold { get; init; } = 3;
    public long Version { get; init; }
}
