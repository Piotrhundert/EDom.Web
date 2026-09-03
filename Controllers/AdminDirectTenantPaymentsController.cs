using System.Collections;
using System.Reflection;
using EDom.Application.Rental;
using EDom.Domain.Rental;
using EDom.Web.Authorization;
using EDom.Web.Infrastructure;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EDom.Web.Controllers;

[Authorize]
[Route("Rental/AdminDirectPayments")]
public sealed class AdminDirectTenantPaymentsController(
    WebAccessService access,
    ITenantSettlementService settlementService,
    IAntiforgery antiforgery) : Controller
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

        var canManage = GetBool(
            overview,
            "CanManage");

        if (!canManage)
        {
            return Json(new
            {
                canManage = false,
                settlements = Array.Empty<object>()
            });
        }

        var requestToken = antiforgery
            .GetAndStoreTokens(HttpContext)
            .RequestToken;

        var settlements = AsObjects(GetValue(
                overview,
                "Settlements"))
            .Select(settlement =>
            {
                var status = GetString(
                    settlement,
                    "Status");

                var totalDueMinor = GetLong(
                    settlement,
                    "TotalDueMinor");

                var paidMinor = GetLong(
                    settlement,
                    "PaidMinor");

                var remainingMinor = GetLong(
                    settlement,
                    "RemainingMinor");

                var canAccept =
                    IsPayableStatus(status);

                return new
                {
                    settlementId = GetGuid(
                        settlement,
                        "Id"),
                    tenantName = GetString(
                        settlement,
                        "TenantName"),
                    roomName = GetString(
                        settlement,
                        "RoomName"),
                    periodKey = GetString(
                        settlement,
                        "PeriodKey"),
                    status,
                    currencyCode = GetString(
                        settlement,
                        "CurrencyCode",
                        "PLN"),
                    totalDueMinor,
                    paidMinor,
                    remainingMinor,
                    canAccept,
                    paymentHint =
                        !canAccept
                            ? "Wpłatę można przyjąć dopiero po opublikowaniu rozliczenia."
                            : remainingMinor > 0
                                ? $"Do zapłaty: {remainingMinor / 100m:N2} {GetString(settlement, "CurrencyCode", "PLN")}."
                                : "Rozliczenie jest już opłacone. Dodatkowa wpłata utworzy nadpłatę."
                };
            })
            .Where(x =>
                x.settlementId != Guid.Empty)
            .ToArray();

        return Json(new
        {
            canManage = true,
            requestToken,
            settlements
        });
    }

    [HttpPost("Receive")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Receive(
        Guid settlementId,
        decimal amount,
        string currencyCode,
        DateTime paidAtLocal,
        string paymentMethod,
        string? note,
        CancellationToken cancellationToken)
    {
        var actor = await GetActorAsync(
            cancellationToken);

        if (actor is null)
        {
            return Unauthorized();
        }

        var overviewBefore = await settlementService.GetOverviewAsync(
            actor,
            cancellationToken);

        if (!GetBool(
                overviewBefore,
                "CanManage"))
        {
            return Forbid();
        }

        var settlementBefore = FindSettlement(
            overviewBefore,
            settlementId);

        if (settlementBefore is null)
        {
            return BadRequest(new
            {
                message =
                    "Nie znaleziono rozliczenia lokatora."
            });
        }

        var settlementStatus = GetString(
            settlementBefore,
            "Status");

        if (!IsPayableStatus(
                settlementStatus))
        {
            return BadRequest(new
            {
                message =
                    "Wpłatę można przyjąć dopiero do opublikowanego rozliczenia. Jeśli rachunek jest projektem, najpierw go zatwierdź i opublikuj."
            });
        }

        if (amount <= 0m)
        {
            return BadRequest(new
            {
                message =
                    "Kwota wpłaty musi być większa od 0."
            });
        }

        var normalizedCurrency =
            NormalizeCurrency(currencyCode);

        var settlementCurrency =
            NormalizeCurrency(
                GetString(
                    settlementBefore,
                    "CurrencyCode",
                    "PLN"));

        if (!string.Equals(
                normalizedCurrency,
                settlementCurrency,
                StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new
            {
                message =
                    $"Waluta wpłaty ({normalizedCurrency}) nie zgadza się z walutą rozliczenia ({settlementCurrency})."
            });
        }

        var normalizedMethod =
            NormalizePaymentMethod(
                paymentMethod);

        var amountMinor =
            ToMinor(amount);

        var paidUtc =
            DateTime.SpecifyKind(
                    paidAtLocal,
                    DateTimeKind.Local)
                .ToUniversalTime();

        var beforeSubmissionIds =
            GetSubmissionIds(
                settlementBefore);

        try
        {
            // Krok 1: używamy standardowego mechanizmu zgłoszenia wpłaty.
            await settlementService.SubmitPaymentAsync(
                actor,
                new(
                    settlementId,
                    amountMinor,
                    normalizedCurrency,
                    paidUtc,
                    normalizedMethod,
                    null,
                    null),
                cancellationToken);

            // Krok 2: jako administrator od razu odnajdujemy nowo utworzone
            // zgłoszenie i zatwierdzamy je tym samym mechanizmem, którego
            // używa standardowy przycisk „Zatwierdź” w tabeli zgłoszeń.
            var overviewAfterSubmit =
                await settlementService.GetOverviewAsync(
                    actor,
                    cancellationToken);

            var settlementAfterSubmit =
                FindSettlement(
                    overviewAfterSubmit,
                    settlementId);

            if (settlementAfterSubmit is null)
            {
                throw new InvalidOperationException(
                    "Wpłata została zgłoszona, ale nie udało się ponownie odczytać rozliczenia.");
            }

            var newSubmission =
                FindNewSubmission(
                    settlementAfterSubmit,
                    beforeSubmissionIds,
                    amountMinor,
                    normalizedMethod);

            if (newSubmission is null)
            {
                throw new InvalidOperationException(
                    "Wpłata została zgłoszona, ale nie udało się jednoznacznie odnaleźć nowego zgłoszenia do zaksięgowania. Sprawdź sekcję „Zgłoszenia wpłat” — wpłata mogła pozostać oczekująca.");
            }

            var submissionId =
                GetGuid(
                    newSubmission,
                    "Id");

            if (submissionId == Guid.Empty)
            {
                throw new InvalidOperationException(
                    "Nowe zgłoszenie wpłaty nie ma prawidłowego identyfikatora.");
            }

            var submissionStatus =
                GetString(
                    newSubmission,
                    "Status");

            if (!string.Equals(
                    submissionStatus,
                    "Approved",
                    StringComparison.OrdinalIgnoreCase))
            {
                var cleanNote =
                    string.IsNullOrWhiteSpace(note)
                        ? null
                        : note.Trim();

                var decisionReason =
                    normalizedMethod == "Cash"
                        ? "Wpłata gotówkowa przyjęta bezpośrednio przez administratora."
                        : "Wpłata przelewem zaksięgowana bezpośrednio przez administratora.";

                if (cleanNote is not null)
                {
                    decisionReason +=
                        $" Uwagi: {cleanNote}";
                }

                await settlementService.ApprovePaymentAsync(
                    actor,
                    new(
                        submissionId,
                        amountMinor,
                        decisionReason),
                    cancellationToken);
            }

            var overviewFinal =
                await settlementService.GetOverviewAsync(
                    actor,
                    cancellationToken);

            var settlementFinal =
                FindSettlement(
                    overviewFinal,
                    settlementId);

            var remainingMinor =
                settlementFinal is null
                    ? 0L
                    : GetLong(
                        settlementFinal,
                        "RemainingMinor");

            var totalPaidMinor =
                settlementFinal is null
                    ? 0L
                    : GetLong(
                        settlementFinal,
                        "PaidMinor");

            var overpaymentMinor =
                settlementFinal is null
                    ? 0L
                    : Math.Max(
                        0,
                        totalPaidMinor
                        - GetLong(
                            settlementFinal,
                            "TotalDueMinor"));

            var sourceLabel =
                normalizedMethod == "Cash"
                    ? "kasie domowej"
                    : "rachunku bankowym domu";

            var message =
                $"Przyjęto i zaksięgowano {amountMinor / 100m:N2} {normalizedCurrency} od {GetString(settlementBefore, "TenantName")}. Kwota została ujęta w {sourceLabel}.";

            if (remainingMinor > 0)
            {
                message +=
                    $" Pozostało do zapłaty: {remainingMinor / 100m:N2} {normalizedCurrency}.";
            }
            else if (overpaymentMinor > 0)
            {
                message +=
                    $" Powstała nadpłata: {overpaymentMinor / 100m:N2} {normalizedCurrency}.";
            }
            else
            {
                message +=
                    " Rozliczenie jest opłacone.";
            }

            return Json(new
            {
                ok = true,
                submissionId,
                amountMinor,
                paymentMethod =
                    normalizedMethod,
                remainingMinor,
                totalPaidMinor,
                overpaymentMinor,
                message
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

    private static object? FindSettlement(
        object overview,
        Guid settlementId) =>
        AsObjects(GetValue(
                overview,
                "Settlements"))
            .FirstOrDefault(x =>
                GetGuid(
                    x,
                    "Id") == settlementId);

    private static HashSet<Guid> GetSubmissionIds(
        object settlement)
    {
        var result =
            new HashSet<Guid>();

        foreach (var submission in AsObjects(
                     GetValue(
                         settlement,
                         "Submissions")))
        {
            var id = GetGuid(
                submission,
                "Id");

            if (id != Guid.Empty)
            {
                result.Add(id);
            }
        }

        return result;
    }

    private static object? FindNewSubmission(
        object settlement,
        HashSet<Guid> beforeIds,
        long amountMinor,
        string paymentMethod)
    {
        var candidates =
            AsObjects(GetValue(
                    settlement,
                    "Submissions"))
                .Where(x =>
                {
                    var id =
                        GetGuid(
                            x,
                            "Id");

                    if (id == Guid.Empty
                        || beforeIds.Contains(id))
                    {
                        return false;
                    }

                    var method =
                        GetString(
                            x,
                            "PaymentMethod");

                    var amount =
                        GetLong(
                            x,
                            "AmountMinor");

                    return amount == amountMinor
                           && string.Equals(
                               method,
                               paymentMethod,
                               StringComparison.OrdinalIgnoreCase);
                })
                .OrderByDescending(x =>
                    GetDateTime(
                        x,
                        "SubmittedAtUtc",
                        "CreatedAtUtc",
                        "DeclaredPaidAtUtc")
                    ?? DateTime.MinValue)
                .ToArray();

        return candidates.FirstOrDefault();
    }

    private static bool IsPayableStatus(
        string? status) =>
        status is
            TenantSettlementStatuses.Published
            or TenantSettlementStatuses.PartiallyPaid
            or TenantSettlementStatuses.Overdue
            or TenantSettlementStatuses.Corrected
            or TenantSettlementStatuses.Paid
            or TenantSettlementStatuses.PaidLate
        || string.Equals(
            status,
            "Overpaid",
            StringComparison.OrdinalIgnoreCase);

    private static string NormalizeCurrency(
        string? value)
    {
        var currency =
            (value ?? "PLN")
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
        checked(
            (long)Math.Round(
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

        var type =
            source.GetType();

        foreach (var name in names)
        {
            var property =
                type.GetProperty(
                    name,
                    BindingFlags.Public
                    | BindingFlags.Instance
                    | BindingFlags.IgnoreCase);

            if (property is not null)
            {
                return property.GetValue(
                    source);
            }
        }

        return null;
    }

    private static string GetString(
        object source,
        string name,
        string fallback = "") =>
        GetValue(
            source,
            name)?.ToString()
        ?? fallback;

    private static bool GetBool(
        object source,
        string name)
    {
        var value =
            GetValue(
                source,
                name);

        return value is bool result
               && result;
    }

    private static Guid GetGuid(
        object source,
        params string[] names)
    {
        var value =
            GetValue(
                source,
                names);

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
        var value =
            GetValue(
                source,
                names);

        if (value is null)
        {
            return 0L;
        }

        try
        {
            return Convert.ToInt64(
                value);
        }
        catch
        {
            return 0L;
        }
    }

    private static DateTime? GetDateTime(
        object source,
        params string[] names)
    {
        var value =
            GetValue(
                source,
                names);

        if (value is DateTime dateTime)
        {
            return dateTime;
        }

        return DateTime.TryParse(
            value?.ToString(),
            out var parsed)
            ? parsed
            : null;
    }
}
