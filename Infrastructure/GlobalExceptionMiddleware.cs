using System.Globalization;
using EDom.Application.Common.Results;

namespace EDom.Web.Infrastructure;

public sealed class GlobalExceptionMiddleware(RequestDelegate next, ILogger<GlobalExceptionMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            // ConfirmIncome tworzy zatwierdzoną PrivateTransaction, której AmountMinor musi być > 0.
            // Walidujemy formularz przed wejściem do kontrolera, aby pusta/zerowa kwota nie kończyła się
            // naruszeniem CK_PrivateTransactions_Amount w SQLite.
            if (HttpMethods.IsPost(context.Request.Method)
                && (context.Request.Path.Value?.Contains("/PrivateFinance/ConfirmIncome", StringComparison.OrdinalIgnoreCase) ?? false)
                && context.Request.HasFormContentType)
            {
                var form = await context.Request.ReadFormAsync(context.RequestAborted);
                var rawAmount = form
                    .FirstOrDefault(x => x.Key.Contains("amount", StringComparison.OrdinalIgnoreCase))
                    .Value.FirstOrDefault();

                var normalized = (rawAmount ?? string.Empty).Trim().Replace(" ", string.Empty).Replace(',', '.');
                if (!decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount) || amount <= 0m)
                {
                    context.Response.Redirect("/PrivateFinance?incomeAmountError=1");
                    return;
                }
            }

            await next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // Przeglądarka może anulować żądanie podczas odświeżenia, przejścia na inną
            // stronę lub zamknięcia karty. EF Core przekazuje wtedy TaskCanceledException.
            // To nie jest błąd aplikacji i nie powinno trafiać do obsługi błędów 500.
            logger.LogDebug(
                "HTTP request canceled by client. CorrelationId={CorrelationId}, Path={Path}",
                CorrelationIdMiddleware.Get(context),
                context.Request.Path);
            return;
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
            await SafeStatusPageWriter.WriteAsync(context, safe);
        }
    }
}
