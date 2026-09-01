namespace EDom.Web.Models;

public sealed class UserAdministrationPageViewModel
{
    public Guid HouseholdId { get; init; }
    public string HouseholdName { get; init; } = string.Empty;
    public Guid CurrentAccountId { get; init; }
    public bool CanManageSecurity { get; init; }
    public IReadOnlyList<UserAdministrationRow> Users { get; init; } = [];
    public int TotalCount => Users.Count;
    public int ActiveCount => Users.Count(x => x.AccountStatus == "Active");
    public int LockedCount => Users.Count(x => x.AccountStatus == "Locked");
    public int InactiveCount => Users.Count(x => x.AccountStatus == "Inactive");
    public int MustChangePasswordCount => Users.Count(x => x.MustChangePassword);
}

public sealed record UserAdministrationRoleRow(
    string RoleCode,
    string RoleName,
    string ProfileName,
    string ScopeType,
    string? ScopeId,
    DateTime? ValidToUtc);

public sealed class UserAdministrationRow
{
    public Guid AccountId { get; init; }
    public Guid PersonId { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public string Login { get; init; } = string.Empty;
    public string OrganizationalRole { get; init; } = string.Empty;
    public string AccountStatus { get; init; } = string.Empty;
    public string AccountStatusLabel { get; init; } = string.Empty;
    public bool IsApproved { get; init; }
    public string ApprovalLabel => IsApproved ? "Zatwierdzony" : "Nieaktywny / do zatwierdzenia";
    public bool IsLocked => AccountStatus == "Locked";
    public bool IsActive => AccountStatus == "Active";
    public bool IsInactive => AccountStatus == "Inactive";
    public bool IsCurrentAccount { get; init; }
    public bool IsLastSuperAdministrator { get; init; }
    public bool MustChangePassword { get; init; }
    public DateTime? LastLoginAtUtc { get; init; }
    public DateTime? PasswordChangedAtUtc { get; init; }
    public int FailedLoginCount { get; init; }
    public string? LockoutReason { get; init; }
    public IReadOnlyList<UserAdministrationRoleRow> Roles { get; init; } = [];
}
