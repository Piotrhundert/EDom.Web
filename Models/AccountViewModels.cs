using System.ComponentModel.DataAnnotations;

namespace EDom.Web.Models;

public sealed class LoginViewModel
{
    [Required(ErrorMessage = "Podaj login.")]
    [Display(Name = "Login")]
    public string Login { get; set; } = string.Empty;

    [Required(ErrorMessage = "Podaj hasło.")]
    [DataType(DataType.Password)]
    [Display(Name = "Hasło")]
    public string Password { get; set; } = string.Empty;

    public string? ReturnUrl { get; set; }
}
