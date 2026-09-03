using System.Collections;
using System.Reflection;
using EDom.Application.Rental;
using EDom.Domain.Rental;
using EDom.Web.Authorization;
using EDom.Web.Infrastructure;
using EDom.Web.Models;
using EDom.Web.Services;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EDom.Web.Controllers;

[Authorize]
[Route("Rental/SettlementCorrections")]
public sealed class TenantSettlementCorrectionsController(
    WebAccessService access,
    ITenantSettlementService settlementService,
    IAntiforgery antiforgery,
    IWebHostEnvironment environment) : Controller
{
    [HttpGet("Data")]
    public async Task<IActionResult> Data(
        CancellationToken cancellationToken)
    {
        var actor = await GetActorAsync(cancellationToken);
        var current = await access.GetCurrentAsync(cancellationToken);

        if (actor is null || current is null)
        {
            return Unauthorized();
        }

        var overview = await settlementService.GetOverviewAsync(
            actor,
            cancellationToken);

        var store = new TenantSettlementCorrectionUiStore(
            environment.ContentRootPath);

        var correctionHistory = await store.GetForHouseholdAsync(
            current.HouseholdId,
            cancellationToken);

        var requestToken = antiforgery
            .GetAndStoreTokens(HttpContext)
            .RequestToken;

        var settlements = AsObjects(GetValue(overview, "Settlements"))
            .Select(x =>
            {
                var settlementId = GetGuid(x, "Id");
                var status = GetString(x, "Status");

                var lines = AsObjects(GetValue(x, "Lines"))
                    .ToArray();

                var pelletAmountMinor = lines
                    .Where(line => string.Equals(
                        GetString(line, "LineType"),
                        TenantSettlementLineTypes.Pellet,
                        StringComparison.OrdinalIgnoreCase))
                    .Sum(line => GetLong(line, "AmountMinor"));

                var correctionLineMinor = lines
                    .Where(line => string.Equals(
                        GetString(line, "LineType"),
                        TenantSettlementLineTypes.Correction,
                        StringComparison.OrdinalIgnoreCase))
                    .Sum(line => GetLong(line, "AmountMinor"));

                var storedCorrections = correctionHistory
                    .Where(c => c.SettlementId == settlementId)
                    .OrderByDescending(c => c.CreatedAtUtc)
                    .Select(c => new
                    {
                        c.Id,
                        c.CorrectionType,
                        c.DeltaMinor,
                        c.CurrencyCode,
                        c.DueDate,
                        c.Reason,
                        c.CreatedAtUtc
                    })
                    .ToArray();

                var dueDate = GetNullableDateOnly(x, "DueDate");

                return new
                {
                    settlementId,
                    tenantName = GetString(x, "TenantName"),
                    roomName = GetString(x, "RoomName"),
                    periodKey = GetString(x, "PeriodKey"),
                    status,
                    currencyCode = GetString(x, "CurrencyCode", "PLN"),
                    totalDueMinor = GetLong(x, "TotalDueMinor"),
                    paidMinor = GetLong(x, "PaidMinor"),
                    remainingMinor = GetLong(x, "RemainingMinor"),
                    payerPersonId = GetGuid(x, "PayerPersonId"),
                    dueDate,
                    pelletAmountMinor,
                    correctionLineMinor,
                    corrections = storedCorrections,
                    lockedForNormalEdit = !IsEditableStatus(status)
                };
            })
            .Where(x => x.settlementId != Guid.Empty)
            .ToArray();

        return Json(new
        {
            canManage = GetBool(overview, "CanManage"),
            isTenant = GetBool(overview, "IsTenant"),
            requestToken,
            settlements
        });
    }

    [HttpPost("Create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(
        Guid settlementId,
        string correctionType,
        string operation,
        decimal amount,
        DateOnly? dueDate,
        string? reason,
        CancellationToken cancellationToken)
    {
        var actor = await GetActorAsync(cancellationToken);
        var current = await access.GetCurrentAsync(cancellationToken);

        if (actor is null || current is null)
        {
            return Unauthorized();
        }

        var overview = await settlementService.GetOverviewAsync(
            actor,
            cancellationToken);

        if (!GetBool(overview, "CanManage"))
        {
            return Forbid();
        }

        var settlement = AsObjects(GetValue(overview, "Settlements"))
            .FirstOrDefault(x =>
                GetGuid(x, "Id") == settlementId);

        if (settlement is null)
        {
            return BadRequest(new
            {
                message = "Nie znaleziono rozliczenia lokatora."
            });
        }

        var status = GetString(settlement, "Status");

        if (IsEditableStatus(status))
        {
            return BadRequest(new
            {
                message =
                    "To rozliczenie jest jeszcze otwarte. Zmień jego pozycje i przelicz je ponownie zamiast tworzyć korektę."
            });
        }

        if (amount <= 0m)
        {
            return BadRequest(new
            {
                message = "Kwota korekty musi być większa od 0."
            });
        }

        var normalizedType = NormalizeType(correctionType);
        var normalizedOperation = NormalizeOperation(
            operation,
            normalizedType);

        var amountMinor = ToMinor(amount);

        var deltaMinor = normalizedOperation == "Subtract"
            ? checked(-amountMinor)
            : amountMinor;

        if (deltaMinor > 0 && !dueDate.HasValue)
        {
            return BadRequest(new
            {
                message = "Dla korekty zwiększającej należność podaj termin płatności."
            });
        }

        var currency = GetString(
            settlement,
            "CurrencyCode",
            "PLN");

        var periodKey = GetString(
            settlement,
            "PeriodKey");

        var tenantName = GetString(
            settlement,
            "TenantName");

        var roomName = GetString(
            settlement,
            "RoomName");

        var typeLabel = normalizedType == "Pellet"
            ? "Pellet / ogrzewanie"
            : "Korekta ręczna";

        var operationLabel = deltaMinor >= 0
            ? "doliczenie"
            : "odjęcie";

        var cleanReason = string.IsNullOrWhiteSpace(reason)
            ? null
            : reason.Trim();

        var fullReason =
            $"{typeLabel} — {operationLabel} {Math.Abs(deltaMinor) / 100m:N2} {currency}. " +
            $"Lokator: {tenantName}, {roomName}, okres {periodKey}." +
            (dueDate.HasValue
                ? $" Termin płatności korekty: {dueDate.Value:yyyy-MM-dd}."
                : string.Empty) +
            (cleanReason is null
                ? string.Empty
                : $" Powód: {cleanReason}");

        try
        {
            await settlementService.CorrectSettlementAsync(
                actor,
                new(
                    settlementId,
                    deltaMinor,
                    fullReason),
                cancellationToken);

            // Dodatnia korekta jest realną nową należnością.
            // Termin zapisujemy również jako widoczne dla lokatora uzgodnienie,
            // aby termin był częścią standardowego modelu rozliczeń.
            if (deltaMinor > 0 && dueDate.HasValue)
            {
                await settlementService.CreatePaymentArrangementAsync(
                    actor,
                    new(
                        settlementId,
                        deltaMinor,
                        dueDate.Value,
                        $"Termin płatności korekty — {typeLabel}",
                        fullReason,
                        true),
                    cancellationToken);
            }

            var store = new TenantSettlementCorrectionUiStore(
                environment.ContentRootPath);

            await store.AddAsync(
                new TenantSettlementCorrectionUiRecord
                {
                    Id = Guid.NewGuid(),
                    HouseholdId = current.HouseholdId,
                    SettlementId = settlementId,
                    CorrectionType = normalizedType,
                    DeltaMinor = deltaMinor,
                    CurrencyCode = currency,
                    DueDate = dueDate,
                    Reason = cleanReason,
                    TenantName = tenantName,
                    RoomName = roomName,
                    PeriodKey = periodKey,
                    CreatedAtUtc = DateTime.UtcNow,
                    CreatedByUserAccountId = current.UserAccountId
                },
                cancellationToken);

            return Json(new
            {
                ok = true,
                deltaMinor,
                correctionType = normalizedType,
                dueDate,
                message = normalizedType == "Pellet"
                    ? $"Utworzono korektę pelletu: {(deltaMinor >= 0 ? "+" : "-")}{Math.Abs(deltaMinor) / 100m:N2} {currency}. {(dueDate.HasValue ? $"Termin: {dueDate.Value:dd.MM.yyyy}." : "")}"
                    : $"Utworzono korektę rozliczenia: {(deltaMinor >= 0 ? "+" : "-")}{Math.Abs(deltaMinor) / 100m:N2} {currency}."
            });
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
        catch (Exception ex)
        {
            return BadRequest(new
            {
                message = ex.Message
            });
        }
    }

    [HttpPost("Payment/Submit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SubmitCorrectionPayment(
        Guid settlementId,
        decimal amount,
        string currencyCode,
        DateTime declaredPaidAtLocal,
        string paymentMethod,
        CancellationToken cancellationToken)
    {
        var actor = await GetActorAsync(cancellationToken);

        if (actor is null)
        {
            return Unauthorized();
        }

        var overview = await settlementService.GetOverviewAsync(
            actor,
            cancellationToken);

        if (!GetBool(overview, "IsTenant"))
        {
            return Forbid();
        }

        var settlement = AsObjects(GetValue(overview, "Settlements"))
            .FirstOrDefault(x =>
                GetGuid(x, "Id") == settlementId);

        if (settlement is null)
        {
            return BadRequest(new
            {
                message = "Nie znaleziono rozliczenia lub nie jest ono widoczne dla tego lokatora."
            });
        }

        if (amount <= 0m)
        {
            return BadRequest(new
            {
                message = "Kwota wpłaty musi być większa od 0."
            });
        }

        var remainingMinor = GetLong(
            settlement,
            "RemainingMinor");

        var amountMinor = ToMinor(amount);

        if (remainingMinor > 0 && amountMinor > remainingMinor)
        {
            return BadRequest(new
            {
                message =
                    $"Kwota wpłaty przekracza aktualną należność. Pozostało {remainingMinor / 100m:N2} {GetString(settlement, "CurrencyCode", "PLN")}."
            });
        }

        var declaredUtc = DateTime.SpecifyKind(
                declaredPaidAtLocal,
                DateTimeKind.Local)
            .ToUniversalTime();

        try
        {
            await settlementService.SubmitPaymentAsync(
                actor,
                new(
                    settlementId,
                    amountMinor,
                    NormalizeCurrency(currencyCode),
                    declaredUtc,
                    NormalizePaymentMethod(paymentMethod),
                    null,
                    null),
                cancellationToken);

            return Json(new
            {
                ok = true,
                message =
                    "Wpłata korekty została zgłoszona. Administrator zobaczy ją w zgłoszeniach wpłat i zatwierdzi standardowym mechanizmem."
            });
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
        catch (Exception ex)
        {
            return BadRequest(new
            {
                message = ex.Message
            });
        }
    }

    private async Task<RentalActor?> GetActorAsync(
        CancellationToken cancellationToken)
    {
        var current = await access.GetCurrentAsync(
            cancellationToken);

        return current is null
            ? null
            : new RentalActor(
                current.UserAccountId,
                current.PersonId,
                current.HouseholdId,
                CorrelationIdMiddleware.Get(HttpContext),
                DateTime.UtcNow);
    }

    private static bool IsEditableStatus(
        string? status) =>
        status is
            TenantSettlementStatuses.Draft
            or TenantSettlementStatuses.AwaitingData
            or TenantSettlementStatuses.ReadyForApproval;

    private static string NormalizeType(
        string? value)
    {
        var normalized = (value ?? string.Empty)
            .Trim()
            .ToLowerInvariant();

        return normalized is "pellet" or "pelet"
            ? "Pellet"
            : "Other";
    }

    private static string NormalizeOperation(
        string? value,
        string correctionType)
    {
        if (correctionType == "Pellet")
        {
            return "Add";
        }

        return string.Equals(
            value,
            "Subtract",
            StringComparison.OrdinalIgnoreCase)
            ? "Subtract"
            : "Add";
    }

    private static string NormalizeCurrency(
        string? value)
    {
        var currency = (value ?? "PLN")
            .Trim()
            .ToUpperInvariant();

        return currency.Length == 3
            ? currency
            : "PLN";
    }

    private static string NormalizePaymentMethod(
        string? value) =>
        string.Equals(
            value,
            "Cash",
            StringComparison.OrdinalIgnoreCase)
            ? "Cash"
            : "Bank";

    private static long ToMinor(
        decimal amount) =>
        checked((long)Math.Round(
            amount * 100m,
            0,
            MidpointRounding.AwayFromZero));

    private static IEnumerable<object> AsObjects(
        object? value)
    {
        if (value is not IEnumerable enumerable)
        {
            yield break;
        }

        foreach (var item in enumerable)
        {
            if (item is not null)
            {
                yield return item;
            }
        }
    }

    private static object? GetValue(
        object? source,
        params string[] names)
    {
        if (source is null)
        {
            return null;
        }

        var type = source.GetType();

        foreach (var name in names)
        {
            var property = type.GetProperty(
                name,
                BindingFlags.Public
                | BindingFlags.Instance
                | BindingFlags.IgnoreCase);

            if (property is not null)
            {
                return property.GetValue(source);
            }
        }

        return null;
    }

    private static string GetString(
        object source,
        string name,
        string fallback = "") =>
        GetValue(source, name)?.ToString()
        ?? fallback;

    private static bool GetBool(
        object source,
        string name)
    {
        var value = GetValue(source, name);
        return value is bool result && result;
    }

    private static Guid GetGuid(
        object source,
        params string[] names)
    {
        var value = GetValue(source, names);

        if (value is Guid guid)
        {
            return guid;
        }

        return Guid.TryParse(
            value?.ToString(),
            out var parsed)
            ? parsed
            : Guid.Empty;
    }

    private static long GetLong(
        object source,
        params string[] names)
    {
        var value = GetValue(source, names);

        if (value is null)
        {
            return 0L;
        }

        try
        {
            return Convert.ToInt64(value);
        }
        catch
        {
            return 0L;
        }
    }

    private static DateOnly? GetNullableDateOnly(
        object source,
        params string[] names)
    {
        var value = GetValue(source, names);

        if (value is DateOnly dateOnly)
        {
            return dateOnly;
        }

        if (value is DateTime dateTime)
        {
            return DateOnly.FromDateTime(dateTime);
        }

        return DateOnly.TryParse(
            value?.ToString(),
            out var parsed)
            ? parsed
            : null;
    }
}
