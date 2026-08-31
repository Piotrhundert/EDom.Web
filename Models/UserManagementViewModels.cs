using System.ComponentModel.DataAnnotations;
using EDom.Application.Administration;

namespace EDom.Web.Models;

public sealed class UserManagementPageViewModel
{
    public ManagedUserOverview Overview { get; init; } = new([], [], [], []);
    public IReadOnlyList<ManagedRoleCatalogItem> RoleCatalog { get; init; } = [];
}

public sealed class CreateManagedUserViewModel
{
    public Guid? ExistingPersonId { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public DateOnly? BirthDate { get; set; }
    [EmailAddress] public string? Email { get; set; }
    public string? Phone { get; set; }
    public string OrganizationalRole { get; set; } = "Member";
    [Required] public string Login { get; set; } = string.Empty;
    [Required] public string TemporaryPassword { get; set; } = string.Empty;
    public bool MustChangePassword { get; set; } = true;
    [Required] public string RoleCode { get; set; } = "HouseholdMember";
    [Required] public string ProfileCode { get; set; } = "Standard";
}

public sealed class UpdateManagedUserViewModel
{
    public Guid AccountId { get; set; }
    [Required] public string Login { get; set; } = string.Empty;
    [Required] public string AccountStatus { get; set; } = "Active";
    [Required] public string RoleCode { get; set; } = "HouseholdMember";
    [Required] public string ProfileCode { get; set; } = "Standard";
    [Required] public string Reason { get; set; } = "Aktualizacja danych użytkownika";
}


public sealed class ChangeManagedUserRoleViewModel
{
    public Guid AccountId { get; set; }
    [Required] public string RoleCode { get; set; } = "HouseholdMember";
    [Required] public string ProfileCode { get; set; } = "Standard";
    [Required] public string Reason { get; set; } = "Zmiana roli użytkownika";
}

public sealed class ResetManagedPasswordViewModel
{
    public Guid AccountId { get; set; }
    [Required] public string TemporaryPassword { get; set; } = string.Empty;
    public bool MustChangePassword { get; set; } = true;
}
