using System.Security.Claims;
using EDom.Application.Identity;
using Microsoft.AspNetCore.Authentication;

namespace EDom.Web.Authentication;

public sealed class SessionValidationMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IIdentityService identityService)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            var userIdRaw = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
            var sessionIdRaw = context.User.FindFirstValue(EDomClaimTypes.SessionId);
            var token = context.User.FindFirstValue(EDomClaimTypes.SessionToken);
            var stamp = context.User.FindFirstValue(EDomClaimTypes.SecurityStamp);
            var generationRaw = context.User.FindFirstValue(EDomClaimTypes.AccessGeneration);

            var userId = Guid.Empty;
            var sessionId = Guid.Empty;
            long generation = 0;

            var claimsValid = Guid.TryParse(userIdRaw, out userId)
                              && Guid.TryParse(sessionIdRaw, out sessionId)
                              && !string.IsNullOrWhiteSpace(token)
                              && !string.IsNullOrWhiteSpace(stamp)
                              && long.TryParse(generationRaw, out generation);

            var validation = claimsValid
                ? await identityService.ValidateSessionAsync(userId, sessionId, token!, stamp!, generation, context.RequestAborted)
                : new SessionValidationResult(false);

            if (!validation.IsValid)
            {
                await context.SignOutAsync("EDomCookie");
                context.User = new ClaimsPrincipal(new ClaimsIdentity());
            }
        }

        await next(context);
    }
}
