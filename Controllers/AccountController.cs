using System.Security.Claims;
using EDom.Application.Identity;
using EDom.Web.Authentication;
using EDom.Web.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EDom.Web.Controllers;

[Route("Account")]
public sealed class AccountController(IIdentityService identityService) : Controller
{
    [AllowAnonymous]
    [HttpGet("Login")]
    public IActionResult Login(string? returnUrl = null)
    {
        if (User.Identity?.IsAuthenticated == true)
            return RedirectToAction("Index", "Home");

        return View(new LoginViewModel { ReturnUrl = returnUrl });
    }

    [AllowAnonymous]
    [HttpPost("Login")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(LoginViewModel model, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return View(model);

        var result = await identityService.LoginAsync(
            model.Login,
            model.Password,
            IdentityRequestContextFactory.Create(HttpContext),
            cancellationToken);

        if (!result.Succeeded)
        {
            ModelState.AddModelError(string.Empty, result.Message);
            return View(model);
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, result.UserAccountId!.Value.ToString("D")),
            new(ClaimTypes.Name, result.Login!),
            new(EDomClaimTypes.SessionId, result.SessionId!.Value.ToString("D")),
            new(EDomClaimTypes.SessionToken, result.SessionToken!),
            new(EDomClaimTypes.SecurityStamp, result.SecurityStamp!),
            new(EDomClaimTypes.AccessGeneration, result.AccessGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture))
        };
        if (result.PersonId is { } personId)
            claims.Add(new Claim(EDomClaimTypes.PersonId, personId.ToString("D")));

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "EDomCookie"));
        await HttpContext.SignInAsync("EDomCookie", principal, new AuthenticationProperties
        {
            IsPersistent = false,
            AllowRefresh = false,
            ExpiresUtc = result.ExpiresAtUtc
        });

        if (!string.IsNullOrWhiteSpace(model.ReturnUrl) && Url.IsLocalUrl(model.ReturnUrl))
            return LocalRedirect(model.ReturnUrl);

        return RedirectToAction("Index", "Home");
    }

    [Authorize]
    [HttpPost("Logout")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout(CancellationToken cancellationToken)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var sessionId = Guid.Parse(User.FindFirstValue(EDomClaimTypes.SessionId)!);

        await identityService.RevokeSessionAsync(
            userId,
            sessionId,
            IdentityRequestContextFactory.Create(HttpContext),
            "UserLogout",
            cancellationToken);

        await HttpContext.SignOutAsync("EDomCookie");
        return RedirectToAction(nameof(Login));
    }

    [AllowAnonymous]
    [HttpGet("AccessDenied")]
    public IActionResult AccessDenied() => View();
}
