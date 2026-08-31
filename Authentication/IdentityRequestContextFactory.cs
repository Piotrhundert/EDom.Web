using EDom.Application.Identity;
using EDom.Web.Infrastructure;

namespace EDom.Web.Authentication;

public static class IdentityRequestContextFactory
{
    public static IdentityRequestContext Create(HttpContext httpContext)
    {
        var correlationId = CorrelationIdMiddleware.Get(httpContext);
        var ip = httpContext.Connection.RemoteIpAddress?.ToString();
        var deviceInfo = httpContext.Request.Headers["User-Agent"].ToString();
        var deviceId = httpContext.Request.Headers["X-EDom-Device-Id"].FirstOrDefault();
        return new IdentityRequestContext(correlationId, ip, deviceInfo, deviceId);
    }
}
