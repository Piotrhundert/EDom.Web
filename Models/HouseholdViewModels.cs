using System.ComponentModel.DataAnnotations;

namespace EDom.Web.Models;

public sealed class AddHouseholdPersonViewModel
{
    [Required, Display(Name = "Imię")] public string FirstName { get; set; } = string.Empty;
    [Required, Display(Name = "Nazwisko")] public string LastName { get; set; } = string.Empty;
    [DataType(DataType.Date), Display(Name = "Data urodzenia")] public DateOnly? BirthDate { get; set; }
    [Display(Name = "Profil dziecka bez konta")] public bool IsChild { get; set; }
    [Display(Name = "Rola organizacyjna")] public string OrganizationalRole { get; set; } = "Member";
    [EmailAddress, Display(Name = "E-mail")] public string? Email { get; set; }
    [Phone, Display(Name = "Telefon")] public string? Phone { get; set; }
    [Display(Name = "Miejscowość")] public string? City { get; set; }
    [Display(Name = "Kod pocztowy")] public string? PostalCode { get; set; }
    [Display(Name = "Ulica")] public string? Street { get; set; }
    [Display(Name = "Nr budynku")] public string? BuildingNo { get; set; }
    [Display(Name = "Nr lokalu")] public string? UnitNo { get; set; }
}

public sealed class ProfileChangeViewModel
{
    public Guid PersonId { get; set; }
    [Display(Name = "Imię")] public string? FirstName { get; set; }
    [Display(Name = "Nazwisko")] public string? LastName { get; set; }
    [DataType(DataType.Date), Display(Name = "Data urodzenia")] public DateOnly? BirthDate { get; set; }
    [EmailAddress, Display(Name = "E-mail")] public string? Email { get; set; }
    [Phone, Display(Name = "Telefon")] public string? Phone { get; set; }
    [Display(Name = "Miejscowość")] public string? City { get; set; }
    [Display(Name = "Kod pocztowy")] public string? PostalCode { get; set; }
    [Display(Name = "Ulica")] public string? Street { get; set; }
    [Display(Name = "Nr budynku")] public string? BuildingNo { get; set; }
    [Display(Name = "Nr lokalu")] public string? UnitNo { get; set; }
    [Display(Name = "Uzasadnienie")] public string? Reason { get; set; }
}

public sealed class GuardianViewModel
{
    [Required] public Guid ChildPersonId { get; set; }
    [Required] public Guid GuardianPersonId { get; set; }
    [Required] public string RelationshipType { get; set; } = "Parent";
    public bool IsPrimary { get; set; }
    [DataType(DataType.Date)] public DateOnly ValidFrom { get; set; } = DateOnly.FromDateTime(DateTime.Today);
    public bool AllowChildFinance { get; set; } = true;
    public bool AllowChildCalendar { get; set; } = true;
}

public sealed class FamilyGroupViewModel
{
    [Required, Display(Name = "Nazwa grupy")] public string Name { get; set; } = string.Empty;
}

public sealed class FamilyRelationshipViewModel
{
    [Required] public Guid PersonAId { get; set; }
    [Required] public Guid PersonBId { get; set; }
    [Required] public string RelationshipType { get; set; } = "Spouse";
    public string Direction { get; set; } = "Bidirectional";
    [DataType(DataType.Date)] public DateOnly ValidFrom { get; set; } = DateOnly.FromDateTime(DateTime.Today);
}

public sealed class ResidenceViewModel
{
    [Required] public Guid PersonId { get; set; }
    public string ResidenceType { get; set; } = "Household";
    [DataType(DataType.Date)] public DateOnly ValidFrom { get; set; } = DateOnly.FromDateTime(DateTime.Today);
    [DataType(DataType.Date)] public DateOnly? ValidTo { get; set; }
    public Guid? PayerPersonId { get; set; }
}

public sealed class FamilyGroupMemberViewModel
{
    public Guid GroupId { get; set; }
    public Guid PersonId { get; set; }
    [Display(Name = "Rola w grupie")] public string GroupRole { get; set; } = "Member";
    [Display(Name = "Grupa podstawowa")] public bool IsPrimary { get; set; }
}

public sealed class AdminEditPersonViewModel
{
    public Guid PersonId { get; set; }
    [Required] public string FirstName { get; set; } = string.Empty;
    [Required] public string LastName { get; set; } = string.Empty;
    public DateOnly? BirthDate { get; set; }
    [EmailAddress] public string? Email { get; set; }
    [Phone] public string? Phone { get; set; }
    public string OrganizationalRole { get; set; } = "Member";
    [Required] public string Reason { get; set; } = "Edycja profilu przez administratora";
}
