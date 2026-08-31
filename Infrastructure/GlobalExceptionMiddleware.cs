using EDom.Application.Common.Results;

namespace EDom.Web.Infrastructure;

public sealed class GlobalExceptionMiddleware(RequestDelegate next, ILogger<GlobalExceptionMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception exception)
        {
            var correlationId = CorrelationIdMiddleware.Get(context);
            var safe = SafeErrorMapper.Map(exception, correlationId);

            logger.LogError(
                "Unhandled e-dom error. DecisionCode={DecisionCode}, CorrelationId={CorrelationId}, ExceptionType={ExceptionType}",
                safe.DecisionCode,
                safe.CorrelationId,
                exception.GetType().FullName);

            if (context.Response.HasStarted)
                throw;

            context.Response.Clear();
            context.Response.StatusCode = safe.StatusCode;
            context.Response.ContentType = "application/problem+json";
            await context.Response.WriteAsJsonAsync(new
            {
                type = "about:blank",
                title = safe.StatusCode >= 500 ? "Błąd aplikacji" : "Operacja nie może zostać wykonana",
                status = safe.StatusCode,
                detail = safe.SafeMessage,
                decisionCode = safe.DecisionCode,
                correlationId = safe.CorrelationId
            }, context.RequestAborted);
        }
    }
}
