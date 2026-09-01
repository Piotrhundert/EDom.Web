namespace EDom.Web.Models;

public sealed class RoleStudioPageViewModel
{
    public Guid HouseholdId { get; init; }
    public string HouseholdName { get; init; } = string.Empty;
    public string SelectedRoleCode { get; init; } = string.Empty;
    public bool SelectedRoleIsCustom { get; init; }
    public bool SelectedRoleIsProtected { get; init; }
    public IReadOnlyList<RoleStudioRoleRow> Roles { get; init; } = Array.Empty<RoleStudioRoleRow>();
    public IReadOnlyList<RoleStudioPermissionRow> Permissions { get; init; } = Array.Empty<RoleStudioPermissionRow>();
    public IReadOnlyList<RoleStudioAssignmentRow> Assignments { get; init; } = Array.Empty<RoleStudioAssignmentRow>();
    public IReadOnlyList<RoleStudioProfileRow> Profiles { get; init; } = Array.Empty<RoleStudioProfileRow>();
    public IReadOnlyList<RoleStudioUserRow> Users { get; init; } = Array.Empty<RoleStudioUserRow>();
    public IReadOnlyList<string> ScopeTypes { get; init; } = Array.Empty<string>();
    public int AllowCount { get; init; }
    public int DenyCount { get; init; }
    public int UnsetCount { get; init; }
}

public sealed record RoleStudioRoleRow(
    string Code,
    string Name,
    bool IsCustom,
    bool IsProtected,
    int AllowCount,
    int DenyCount,
    int ActiveAssignments);

public sealed record RoleStudioPermissionRow(
    string Code,
    string Description,
    string GroupKey,
    string GroupLabel,
    string GroupDescription,
    string UiKind,
    string ActionLabel,
    string ImpactDescription,
    string DefaultScopeType,
    string RiskLevel,
    string IntroducedPackage,
    string Effect);

public sealed record RoleStudioAssignmentRow(
    Guid Id,
    Guid UserAccountId,
    string UserDisplayName,
    string Login,
    string RoleCode,
    string RoleName,
    string ProfileCode,
    string ProfileName,
    string ScopeType,
    string? ScopeId,
    DateTime ValidFromUtc,
    DateTime? ValidToUtc,
    string Reason,
    bool IsCurrent);

public sealed record RoleStudioProfileRow(string Code, string Name, int Rank);
public sealed record RoleStudioUserRow(Guid AccountId, string DisplayName, string Login, string Status);
