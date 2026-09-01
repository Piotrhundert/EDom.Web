namespace EDom.Web.Models;

public sealed record UserProfileRoleRow(
    string RoleCode,
    string RoleName,
    string ProfileCode,
    string ProfileName,
    string ScopeType,
    string? ScopeId,
    DateTime? ValidToUtc);

public sealed record UserProfileGroupRow(
    Guid GroupId,
    string GroupName,
    string GroupRole,
    DateOnly ValidFrom,
    DateOnly? ValidTo);

public sealed record UserProfileResidenceRow(
    string ResidenceType,
    string BuildingName,
    string RoomName,
    DateOnly ValidFrom,
    DateOnly? ValidTo);

public sealed record UserProfileEmergencyContactRow(string Name, string RelationshipType);

public sealed record UserProfileIdentityDocumentRow(
    string DocumentType,
    string CountryCode,
    DateOnly? IssuedOn,
    DateOnly? ExpiresOn,
    string Status);

public sealed record UserProfileAccountOption(
    Guid AccountId,
    string DisplayName,
    string Login,
    string Status);

public sealed class UserProfilePageViewModel
{
    public Guid AccountId { get; init; }
    public Guid PersonId { get; init; }
    public string FirstName { get; init; } = string.Empty;
    public string LastName { get; init; } = string.Empty;
    public string DisplayName => $"{FirstName} {LastName}".Trim();
    public DateOnly? BirthDate { get; init; }
    public string PersonType { get; init; } = string.Empty;
    public string PersonStatus { get; init; } = string.Empty;
    public DateTime CreatedAtUtc { get; init; }

    public string Login { get; init; } = string.Empty;
    public string AccountStatus { get; init; } = string.Empty;
    public DateTime? LastLoginAtUtc { get; init; }
    public int FailedLoginCount { get; init; }
    public string? LockoutReason { get; init; }
    public bool MustChangePassword { get; init; }
    public DateTime? PasswordChangedAtUtc { get; init; }

    public string HouseholdName { get; init; } = string.Empty;
    public string OrganizationalRole { get; init; } = string.Empty;
    public DateOnly MembershipValidFrom { get; init; }

    public string? Pesel { get; init; }
    public bool PeselAvailable { get; init; }
    public string? PeselStatusMessage { get; init; }

    public string? Email { get; init; }
    public string? Phone { get; init; }

    public string Country { get; init; } = "PL";
    public string? Region { get; init; }
    public string? City { get; init; }
    public string? PostalCode { get; init; }
    public string? Street { get; init; }
    public string? BuildingNo { get; init; }
    public string? UnitNo { get; init; }

    public bool IsOwnProfile { get; init; }
    public bool CanManageUsers { get; init; }
    public bool CanResetPassword { get; init; }
    public bool CanEditProfile { get; init; }

    public IReadOnlyList<UserProfileRoleRow> Roles { get; init; } = [];
    public IReadOnlyList<UserProfileGroupRow> Groups { get; init; } = [];
    public IReadOnlyList<UserProfileResidenceRow> Residences { get; init; } = [];
    public IReadOnlyList<UserProfileEmergencyContactRow> EmergencyContacts { get; init; } = [];
    public IReadOnlyList<UserProfileIdentityDocumentRow> IdentityDocuments { get; init; } = [];
    public IReadOnlyList<UserProfileAccountOption> HouseholdAccounts { get; init; } = [];
}

public sealed class UserProfileEditInput
{
    public Guid AccountId { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public DateOnly? BirthDate { get; set; }
    public string? Pesel { get; set; }
    public bool RemovePesel { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string Country { get; set; } = "PL";
    public string? Region { get; set; }
    public string? City { get; set; }
    public string? PostalCode { get; set; }
    public string? Street { get; set; }
    public string? BuildingNo { get; set; }
    public string? UnitNo { get; set; }
}
