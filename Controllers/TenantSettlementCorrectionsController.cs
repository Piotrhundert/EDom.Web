using System.Collections;
using System.Reflection;
using EDom.Application.Rental;
using EDom.Web.Authorization;
using EDom.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EDom.Web.Controllers;

[Authorize]
[Route("Rental/SettlementCorrections")]
public sealed class TenantSettlementCorrectionsController(
    WebAccessService access,
    ITenantSettlementService settlementService) : Controller
{
    [HttpGet("Data")]
    public async Task<IActionResult> Data(
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

        var settlements = AsObjects(GetValue(overview, "Settlements"))
            .Select(x =>
            {
                var status = GetString(x, "Status");

                return new
                {
                    settlementId = GetGuid(x, "Id"),
                    tenantName = GetString(x, "TenantName"),
                    roomName = GetString(x, "RoomName"),
                    periodKey = GetString(x, "PeriodKey"),
                    status,
                    currencyCode = GetString(x, "CurrencyCode", "PLN"),
                    totalDueMinor = GetLong(x, "TotalDueMinor"),
                    paidMinor = GetLong(x, "PaidMinor"),
                    lockedForNormalEdit = !IsEditableStatus(status)
                };
            })
            .Where(x => x.settlementId != Guid.Empty)
            .ToArray();

        return Json(new
        {
            canManage = GetBool(overview, "CanManage"),
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
        string? reason,
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
            ? "Pellet"
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

            return Json(new
            {
                ok = true,
                deltaMinor,
                correctionType = normalizedType,
                message = normalizedType == "Pellet"
                    ? $"Utworzono korektę pelletu: {(deltaMinor >= 0 ? "+" : "-")}{Math.Abs(deltaMinor) / 100m:N2} {currency}."
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
            "Draft"
            or "AwaitingData"
            or "ReadyForApproval";

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
}
