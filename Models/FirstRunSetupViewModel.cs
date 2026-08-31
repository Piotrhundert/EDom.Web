using System.ComponentModel.DataAnnotations;

namespace EDom.Web.Models;

public sealed class FirstRunSetupViewModel
{
    [Required(ErrorMessage = "Podaj nazwę gospodarstwa.")]
    [StringLength(120)]
    [Display(Name = "Nazwa gospodarstwa")]
    public string HouseholdName { get; set; } = "Mój e-dom";

    [Required(ErrorMessage = "Podaj imię administratora.")]
    [StringLength(100)]
    [Display(Name = "Imię")]
    public string FirstName { get; set; } = string.Empty;

    [Required(ErrorMessage = "Podaj nazwisko administratora.")]
    [StringLength(100)]
    [Display(Name = "Nazwisko")]
    public string LastName { get; set; } = string.Empty;

    [DataType(DataType.Date)]
    [Display(Name = "Data urodzenia (opcjonalnie)")]
    public DateOnly? BirthDate { get; set; }

    [Required(ErrorMessage = "Podaj login.")]
    [StringLength(100, MinimumLength = 3)]
    [Display(Name = "Login")]
    public string Login { get; set; } = string.Empty;

    [Required(ErrorMessage = "Podaj hasło.")]
    [DataType(DataType.Password)]
    [Display(Name = "Hasło")]
    public string Password { get; set; } = string.Empty;

    [Required(ErrorMessage = "Powtórz hasło.")]
    [DataType(DataType.Password)]
    [Compare(nameof(Password), ErrorMessage = "Hasła nie są identyczne.")]
    [Display(Name = "Powtórz hasło")]
    public string ConfirmPassword { get; set; } = string.Empty;
}
