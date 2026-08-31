using EDom.Application.Setup;

namespace EDom.Web.Infrastructure;

public sealed class FirstRunMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IFirstRunSetupService firstRunSetupService)
    {
        if (IsAlwaysAllowed(context.Request.Path))
        {
            await next(context);
            return;
        }

        var state = await firstRunSetupService.GetStateAsync(context.RequestAborted);
        if (!state.IsConsistent)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsJsonAsync(new
            {
                decisionCode = "FIRST_RUN_STATE_INCONSISTENT",
                message = "Stan pierwszego uruchomienia wymaga diagnostyki.",
                correlationId = CorrelationIdMiddleware.Get(context)
            }, context.RequestAborted);
            return;
        }

        if (!state.SetupRequired)
        {
            await next(context);
            return;
        }

        if (context.Request.Path.StartsWithSegments("/api"))
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsJsonAsync(new
            {
                decisionCode = "FIRST_RUN_REQUIRED",
                message = "Najpierw zakończ kreator pierwszego uruchomienia.",
                correlationId = CorrelationIdMiddleware.Get(context)
            }, context.RequestAborted);
            return;
        }

        context.Response.Redirect("/Setup");
    }

    private static bool IsAlwaysAllowed(PathString path)
        => path.StartsWithSegments("/Setup")
           || path.StartsWithSegments("/health")
           || path.StartsWithSegments("/css")
           || path.StartsWithSegments("/js")
           || path.StartsWithSegments("/lib")
           || path.StartsWithSegments("/favicon.ico");
}
