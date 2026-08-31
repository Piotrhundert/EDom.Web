using System.Security.Claims;
using EDom.Application.Identity;
using EDom.Web.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EDom.Web.Controllers;

[ApiController]
[Route("api/identity")]
public sealed class IdentityController(IIdentityService identityService) : ControllerBase
{
    [AllowAnonymous]
    [HttpPost("login")]
    public async Task<IActionResult> Login(LoginRequest request, CancellationToken cancellationToken)
    {
        var result = await identityService.LoginAsync(
            request.Login,
            request.Password,
            IdentityRequestContextFactory.Create(HttpContext),
            cancellationToken);

        if (!result.Succeeded)
            return Unauthorized(new { message = result.Message });

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

        return Ok(new
        {
            authenticated = true,
            login = result.Login,
            personId = result.PersonId,
            mustChangePassword = result.MustChangePassword,
            expiresAtUtc = result.ExpiresAtUtc
        });
    }

    [Authorize]
    [HttpPost("logout")]
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
        return Ok(new { loggedOut = true });
    }

    [Authorize]
    [HttpPost("logout-all")]
    public async Task<IActionResult> LogoutAll(CancellationToken cancellationToken)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        await identityService.RevokeAllSessionsForUserAsync(
            userId,
            IdentityRequestContextFactory.Create(HttpContext),
            "UserRequestedLogoutAll",
            cancellationToken);
        await HttpContext.SignOutAsync("EDomCookie");
        return Ok(new { loggedOutAllDevices = true });
    }

    [Authorize]
    [HttpPost("change-password")]
    public async Task<IActionResult> ChangePassword(ChangePasswordRequest request, CancellationToken cancellationToken)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var result = await identityService.ChangePasswordAsync(
            userId,
            request.CurrentPassword,
            request.NewPassword,
            IdentityRequestContextFactory.Create(HttpContext),
            cancellationToken);

        if (!result.Succeeded)
            return BadRequest(new { message = result.Message });

        await HttpContext.SignOutAsync("EDomCookie");
        return Ok(new { changed = true, message = result.Message });
    }

    [Authorize]
    [HttpGet("me")]
    public IActionResult Me()
        => Ok(new
        {
            authenticated = true,
            userAccountId = User.FindFirstValue(ClaimTypes.NameIdentifier),
            login = User.Identity?.Name,
            personId = User.FindFirstValue(EDomClaimTypes.PersonId)
        });

    public sealed record LoginRequest(string Login, string Password);
    public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);
}
