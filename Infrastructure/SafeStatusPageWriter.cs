using System.Net;
using EDom.Application.Common.Results;

namespace EDom.Web.Infrastructure;

public static class SafeStatusPageWriter
{
    public static async Task WriteAsync(HttpContext context, SafeError safe)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(safe);

        var wantsHtml = !context.Request.Path.StartsWithSegments("/api")
                        && context.Request.Headers.Accept.ToString().Contains("text/html", StringComparison.OrdinalIgnoreCase);

        if (!wantsHtml)
        {
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
            return;
        }

        context.Response.ContentType = "text/html; charset=utf-8";
        var title = safe.StatusCode switch
        {
            403 => "Brak dostępu",
            404 => "Nie znaleziono strony",
            409 => "Nie można wykonać operacji",
            _ when safe.StatusCode >= 500 => "Błąd aplikacji",
            _ => "Nie można wykonać operacji"
        };

        var encodedTitle = WebUtility.HtmlEncode(title);
        var encodedMessage = WebUtility.HtmlEncode(safe.SafeMessage);
        var encodedDecision = WebUtility.HtmlEncode(safe.DecisionCode);
        var encodedCorrelation = WebUtility.HtmlEncode(safe.CorrelationId);

        await context.Response.WriteAsync($$"""
            <!doctype html>
            <html lang="pl">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <title>{{encodedTitle}} · e-dom</title>
              <style>
                body{margin:0;font-family:Segoe UI,Arial,sans-serif;background:#f3f6fb;color:#1e2937;display:grid;min-height:100vh;place-items:center;padding:24px}
                main{max-width:720px;background:white;border:1px solid #dde5f0;border-radius:24px;padding:32px;box-shadow:0 18px 50px rgba(14,30,58,.10)}
                h1{margin:0 0 12px} p{line-height:1.6;color:#64748b} code{background:#f2f6fb;padding:4px 8px;border-radius:8px}
                a{display:inline-block;margin-top:10px;color:#1d3557;font-weight:700}
              </style>
            </head>
            <body><main>
              <div>e-dom · błąd {{safe.StatusCode}}</div>
              <h1>{{encodedTitle}}</h1>
              <p>{{encodedMessage}}</p>
              <p>Kod: <code>{{encodedDecision}}</code><br>Identyfikator: <code>{{encodedCorrelation}}</code></p>
              <a href="/">Wróć do strony głównej</a>
            </main></body></html>
            """, context.RequestAborted);
    }
}
