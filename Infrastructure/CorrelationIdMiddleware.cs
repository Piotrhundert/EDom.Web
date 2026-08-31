namespace EDom.Web.Infrastructure;

public sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Correlation-ID";
    public const string ItemKey = "EDom.CorrelationId";

    public async Task InvokeAsync(HttpContext context)
    {
        var incoming = context.Request.Headers[HeaderName].FirstOrDefault();
        var correlationId = Normalize(incoming);
        context.Items[ItemKey] = correlationId;
        context.Response.Headers[HeaderName] = correlationId;
        await next(context);
    }

    public static string Get(HttpContext context)
        => context.Items.TryGetValue(ItemKey, out var value) && value is string text && !string.IsNullOrWhiteSpace(text)
            ? text
            : Guid.NewGuid().ToString("N");

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Guid.NewGuid().ToString("N");

        var trimmed = value.Trim();
        if (trimmed.Length > 100 || trimmed.Any(ch => !(char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.')))
            return Guid.NewGuid().ToString("N");

        return trimmed;
    }
}
