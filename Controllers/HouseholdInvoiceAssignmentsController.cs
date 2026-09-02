using EDom.Application.HouseholdFinance;
using EDom.Application.Households;
using EDom.Domain.Authorization;
using EDom.Web.Authorization;
using EDom.Web.Infrastructure;
using EDom.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EDom.Web.Controllers;

[Authorize]
[Route("HouseholdInvoiceAssignments")]
public sealed class HouseholdInvoiceAssignmentsController : Controller
{
    private readonly WebAccessService _access;
    private readonly IHouseholdFinanceService _finance;
    private readonly IHouseholdFamilyService _family;
    private readonly HouseholdInvoiceAssignmentStore _store;

    public HouseholdInvoiceAssignmentsController(
        WebAccessService access,
        IHouseholdFinanceService finance,
        IHouseholdFamilyService family,
        IWebHostEnvironment environment)
    {
        _access = access;
        _finance = finance;
        _family = family;
        _store = new HouseholdInvoiceAssignmentStore(environment.ContentRootPath);
    }

    [HttpGet("Data")]
    public async Task<IActionResult> Data(CancellationToken cancellationToken)
    {
        var current = await _access.GetCurrentAsync(cancellationToken);
        if (current is null) return Unauthorized();

        var canManage = await CanHouseholdAsync(
            "householdfinance.invoice.manage",
            current.HouseholdId,
            current.PersonId,
            cancellationToken);

        if (canManage)
        {
            var overview = await _finance.GetOverviewAsync(
                current.HouseholdId,
                current.PersonId,
                true,
                cancellationToken);
            var household = await _family.GetOverviewAsync(current.HouseholdId, cancellationToken);
            var assignments = await _store.GetForHouseholdAsync(current.HouseholdId, cancellationToken);

            var lockedInvoiceIds = assignments
                .Where(x => x.Status is "Assigned" or "Submitted" or "Approved")
                .Select(x => x.InvoiceId)
                .ToHashSet();

            var adminInvoiceStates = overview.Invoices
                .OrderBy(x => x.DueDate)
                .Select(x =>
                {
                    var latestAssignment = assignments
                        .Where(a => a.InvoiceId == x.Id)
                        .OrderByDescending(a => a.AssignedAtUtc)
                        .FirstOrDefault();
                    var reservedMinor = assignments
                        .Where(a =>
                            a.InvoiceId == x.Id &&
                            (a.Status is "Assigned" or "Submitted"
                             || (a.Status == "Approved" && !a.InvoicePaymentBookedAtUtc.HasValue)))
                        .Sum(a => a.Status == "Approved"
                            ? Math.Min(a.ApprovedAmountMinor ?? a.AmountMinor, a.AmountMinor)
                            : a.AmountMinor);
                    var householdAvailableMinor = Math.Max(0L, x.RemainingMinor - reservedMinor);

                    return new
                    {
                        id = x.Id,
                        invoiceNo = x.InvoiceNo,
                        supplier = x.Supplier,
                        grossMinor = x.GrossMinor,
                        paidMinor = x.PaidMinor,
                        remainingMinor = x.RemainingMinor,
                        currencyCode = x.CurrencyCode,
                        dueDate = x.DueDate,
                        status = x.Status,
                        assignmentStatus = latestAssignment?.Status,
                        assignmentAssigneeName = latestAssignment?.AssigneeName,
                        assignmentAmountMinor = latestAssignment?.AmountMinor ?? 0L,
                        assignmentAssignedAtUtc = latestAssignment?.AssignedAtUtc,
                        assignmentSubmittedAtUtc = latestAssignment?.SubmittedAtUtc,
                        assignmentApprovedAtUtc = latestAssignment?.ApprovedAtUtc,
                        assignmentApprovedAmountMinor = latestAssignment?.ApprovedAmountMinor,
                        assignmentInvoicePaymentBookedAtUtc = latestAssignment?.InvoicePaymentBookedAtUtc,
                        assignmentInvoicePaymentBookedAmountMinor = latestAssignment?.InvoicePaymentBookedAmountMinor,
                        assignmentSettlementType = latestAssignment?.SettlementType,
                        reservedMinor,
                        householdAvailableMinor
                    };
                })
                .ToArray();

            var invoices = overview.Invoices
                .Where(x => x.RemainingMinor > 0 && !lockedInvoiceIds.Contains(x.Id))
                .OrderBy(x => x.DueDate)
                .Select(x => new
                {
                    id = x.Id,
                    invoiceNo = x.InvoiceNo,
                    supplier = x.Supplier,
                    amountMinor = x.RemainingMinor,
                    maxAmountMinor = x.RemainingMinor,
                    currencyCode = x.CurrencyCode,
                    dueDate = x.DueDate
                })
                .ToArray();

            var people = household.Persons
                .Where(x => !x.IsChild)
                .OrderBy(x => x.DisplayName)
                .Select(x => new
                {
                    id = x.PersonId,
                    name = x.DisplayName
                })
                .ToArray();

            return Json(new
            {
                canManage = true,
                invoices,
                invoiceStates = adminInvoiceStates,
                people,
                assignments
            });
        }

        var canSubmitClaim = await CanOwnAsync(
            "householdfinance.claim.submit",
            current.PersonId,
            cancellationToken);
        if (!canSubmitClaim) return Forbid();

        var ownAssignments = await _store.GetForPersonAsync(
            current.HouseholdId,
            current.PersonId,
            cancellationToken);

        var ownOverview = await _finance.GetOverviewAsync(
            current.HouseholdId,
            current.PersonId,
            false,
            cancellationToken);

        var memberInvoiceStates = ownOverview.Invoices
            .OrderBy(x => x.DueDate)
            .Select(x =>
            {
                var latestAssignment = ownAssignments
                    .Where(a => a.InvoiceId == x.Id)
                    .OrderByDescending(a => a.AssignedAtUtc)
                    .FirstOrDefault();

                return new
                {
                    id = x.Id,
                    invoiceNo = x.InvoiceNo,
                    supplier = x.Supplier,
                    grossMinor = x.GrossMinor,
                    paidMinor = x.PaidMinor,
                    remainingMinor = x.RemainingMinor,
                    currencyCode = x.CurrencyCode,
                    dueDate = x.DueDate,
                    status = x.Status,
                    assignmentStatus = latestAssignment?.Status,
                    assignmentAmountMinor = latestAssignment?.AmountMinor ?? 0L,
                    assignmentSubmittedAtUtc = latestAssignment?.SubmittedAtUtc,
                    assignmentApprovedAtUtc = latestAssignment?.ApprovedAtUtc,
                    assignmentApprovedAmountMinor = latestAssignment?.ApprovedAmountMinor,
                    assignmentInvoicePaymentBookedAtUtc = latestAssignment?.InvoicePaymentBookedAtUtc,
                    assignmentInvoicePaymentBookedAmountMinor = latestAssignment?.InvoicePaymentBookedAmountMinor,
                    assignmentSettlementType = latestAssignment?.SettlementType
                };
            })
            .ToArray();

        return Json(new
        {
            canManage = false,
            invoiceStates = memberInvoiceStates,
            assignments = ownAssignments
        });
    }

    [HttpPost("Assign")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Assign(
        Guid invoiceId,
        Guid personId,
        long? amountMinor,
        string? note,
        CancellationToken cancellationToken)
    {
        var current = await _access.GetCurrentAsync(cancellationToken);
        if (current is null) return Unauthorized();

        if (!await CanHouseholdAsync(
                "householdfinance.invoice.manage",
                current.HouseholdId,
                current.PersonId,
                cancellationToken))
        {
            return Forbid();
        }

        if (invoiceId == Guid.Empty || personId == Guid.Empty)
        {
            return BadRequest(new { message = "Wybierz fakturę i osobę, która ma ją opłacić." });
        }

        if (!string.IsNullOrWhiteSpace(note) && note.Length > 500)
        {
            return BadRequest(new { message = "Notatka może mieć maksymalnie 500 znaków." });
        }

        var overview = await _finance.GetOverviewAsync(
            current.HouseholdId,
            current.PersonId,
            true,
            cancellationToken);

        var invoice = overview.Invoices
            .Where(x => x.Id == invoiceId && x.RemainingMinor > 0)
            .Select(x => new
            {
                x.Id,
                x.InvoiceNo,
                x.Supplier,
                x.RemainingMinor,
                x.CurrencyCode,
                x.DueDate
            })
            .FirstOrDefault();

        if (invoice is null)
        {
            return BadRequest(new { message = "Faktura nie istnieje albo została już w całości opłacona." });
        }

        var existing = await _store.GetForHouseholdAsync(current.HouseholdId, cancellationToken);
        var lockedAssignment = existing
            .Where(x => x.InvoiceId == invoiceId && x.Status is "Assigned" or "Submitted" or "Approved")
            .OrderByDescending(x => x.AssignedAtUtc)
            .FirstOrDefault();
        if (lockedAssignment is not null)
        {
            var lockMessage = lockedAssignment.Status == "Submitted"
                ? $"Ta faktura jest zablokowana: {lockedAssignment.AssigneeName} zgłosił(a) już jej opłacenie."
                : $"Ta faktura została już przekazana do opłacenia: {lockedAssignment.AssigneeName}. Anuluj aktywne przekazanie, zanim utworzysz nowe.";
            return BadRequest(new { message = lockMessage });
        }

        var assignedAmountMinor = amountMinor ?? invoice.RemainingMinor;
        if (assignedAmountMinor <= 0)
        {
            return BadRequest(new { message = "Kwota przekazana domownikowi musi być większa od 0." });
        }
        if (assignedAmountMinor > invoice.RemainingMinor)
        {
            return BadRequest(new { message = "Kwota przekazana domownikowi nie może być większa niż kwota pozostała do zapłaty na fakturze." });
        }

        var household = await _family.GetOverviewAsync(current.HouseholdId, cancellationToken);
        var person = household.Persons
            .Where(x => x.PersonId == personId && !x.IsChild)
            .Select(x => new { x.PersonId, x.DisplayName })
            .FirstOrDefault();

        if (person is null)
        {
            return BadRequest(new { message = "Wybrana osoba nie należy do tego gospodarstwa albo nie może otrzymać takiego zadania." });
        }

        HouseholdInvoiceAssignmentRecord assignment;
        try
        {
            assignment = await _store.AssignAsync(
                current.HouseholdId,
                invoice.Id,
                invoice.InvoiceNo,
                invoice.Supplier,
                assignedAmountMinor,
                invoice.CurrencyCode,
                invoice.DueDate,
                person.PersonId,
                person.DisplayName,
                current.UserAccountId,
                note,
                cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }

        return Json(new
        {
            ok = true,
            message = assignedAmountMinor == invoice.RemainingMinor
                ? $"Fakturę {invoice.InvoiceNo} przekazano do opłacenia: {person.DisplayName}."
                : $"Podzielono fakturę {invoice.InvoiceNo}: {person.DisplayName} odpowiada za wskazaną część, a pozostała kwota zostaje po stronie rachunku domu.",
            assignment
        });
    }

    [HttpPost("PayHouseShare")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PayHouseShare(
        Guid invoiceId,
        long amountMinor,
        string? sourceType,
        DateTime? paidAtUtc,
        CancellationToken cancellationToken)
    {
        var current = await _access.GetCurrentAsync(cancellationToken);
        if (current is null) return Unauthorized();

        if (!await CanHouseholdAsync(
                "householdfinance.invoice.pay",
                current.HouseholdId,
                current.PersonId,
                cancellationToken))
        {
            return Forbid();
        }

        if (invoiceId == Guid.Empty || amountMinor <= 0)
        {
            return BadRequest(new { message = "Wybierz fakturę i podaj prawidłową kwotę płatności." });
        }

        var normalizedSource = string.Equals(sourceType, "HouseholdCash", StringComparison.OrdinalIgnoreCase)
            ? "HouseholdCash"
            : "HouseholdBank";

        var overview = await _finance.GetOverviewAsync(
            current.HouseholdId,
            current.PersonId,
            true,
            cancellationToken);
        var invoice = overview.Invoices.FirstOrDefault(x => x.Id == invoiceId);
        if (invoice is null || invoice.RemainingMinor <= 0)
        {
            return BadRequest(new { message = "Faktura nie istnieje albo została już w całości opłacona." });
        }

        var assignments = await _store.GetForHouseholdAsync(current.HouseholdId, cancellationToken);
        var reservedMinor = assignments
            .Where(x =>
                x.InvoiceId == invoiceId &&
                (x.Status is "Assigned" or "Submitted"
                 || (x.Status == "Approved" && !x.InvoicePaymentBookedAtUtc.HasValue)))
            .Sum(x => x.Status == "Approved"
                ? Math.Min(x.ApprovedAmountMinor ?? x.AmountMinor, x.AmountMinor)
                : x.AmountMinor);
        var householdAvailableMinor = Math.Max(0L, invoice.RemainingMinor - reservedMinor);

        if (amountMinor > householdAvailableMinor)
        {
            return BadRequest(new
            {
                message = reservedMinor > 0
                    ? "Ta kwota przekracza część faktury pozostawioną do opłacenia przez rachunek domu. Część przypisana domownikowi jest zablokowana przed podwójnym księgowaniem."
                    : "Kwota płatności nie może przekraczać kwoty pozostałej do zapłaty."
            });
        }

        await _finance.PayInvoiceAsync(
            new PayHouseholdInvoiceRequest(
                invoiceId,
                amountMinor,
                normalizedSource,
                null,
                paidAtUtc ?? DateTime.UtcNow,
                current.UserAccountId,
                CorrelationIdMiddleware.Get(HttpContext)),
            cancellationToken);

        return Json(new { ok = true, message = "Zaksięgowano część faktury opłaconą z rachunku domu." });
    }

    [HttpPost("Approve")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Approve(
        Guid assignmentId,
        string? reason,
        CancellationToken cancellationToken)
    {
        var current = await _access.GetCurrentAsync(cancellationToken);
        if (current is null) return Unauthorized();

        if (!await CanHouseholdAsync(
                "householdfinance.claim.approve",
                current.HouseholdId,
                current.PersonId,
                cancellationToken)
            || !await CanHouseholdAsync(
                "householdfinance.invoice.pay",
                current.HouseholdId,
                current.PersonId,
                cancellationToken))
        {
            return Forbid();
        }

        var assignment = await _store.GetAsync(
            assignmentId,
            current.HouseholdId,
            cancellationToken);

        if (assignment is null)
        {
            return BadRequest(new { message = "Nie znaleziono przekazania faktury." });
        }

        var isNewApproval = assignment.Status == "Submitted";
        var isLegacyRepair = assignment.Status == "Approved" && !assignment.InvoicePaymentBookedAtUtc.HasValue;

        if (!isNewApproval && !isLegacyRepair)
        {
            return BadRequest(new
            {
                message = assignment.Status == "Approved"
                    ? "Część opłacona przez domownika jest już zaksięgowana przy fakturze."
                    : "Ta płatność nie oczekuje na zatwierdzenie administratora."
            });
        }

        var overview = await _finance.GetOverviewAsync(
            current.HouseholdId,
            current.PersonId,
            true,
            cancellationToken);

        var invoice = overview.Invoices.FirstOrDefault(x => x.Id == assignment.InvoiceId);
        if (invoice is null)
        {
            return BadRequest(new { message = "Nie znaleziono faktury powiązanej z tym zgłoszeniem." });
        }

        if (invoice.RemainingMinor <= 0)
        {
            if (isLegacyRepair)
            {
                await _store.MarkInvoicePaymentBookedAsync(
                    assignment.Id,
                    current.HouseholdId,
                    assignment.ApprovedAmountMinor ?? assignment.AmountMinor,
                    cancellationToken);

                return Json(new
                {
                    ok = true,
                    message = $"Faktura {assignment.InvoiceNo} jest już rozliczona w całości."
                });
            }

            return BadRequest(new { message = "Faktura jest już w całości zaksięgowana jako opłacona." });
        }

        var amountRequested = isLegacyRepair
            ? assignment.ApprovedAmountMinor ?? assignment.AmountMinor
            : assignment.AmountMinor;
        var amountToBook = Math.Min(amountRequested, invoice.RemainingMinor);

        if (amountToBook <= 0)
        {
            return BadRequest(new { message = "Brak kwoty do zaksięgowania przy fakturze." });
        }

        var decisionReason = string.IsNullOrWhiteSpace(reason)
            ? $"Zatwierdzono opłacenie faktury {assignment.InvoiceNo} przez {assignment.AssigneeName}."
            : reason.Trim();

        // Nowe zgłoszenie: najpierw zatwierdzamy roszczenie prywatne.
        // Przy naprawie starego rekordu roszczenie jest już zatwierdzone,
        // więc nie próbujemy zatwierdzać go drugi raz.
        if (isNewApproval)
        {
            var claimId = assignment.ClaimId;

            if (!claimId.HasValue)
            {
                var allAssignments = await _store.GetForHouseholdAsync(
                    current.HouseholdId,
                    cancellationToken);
                var alreadyLinkedClaimIds = allAssignments
                    .Where(x => x.ClaimId.HasValue)
                    .Select(x => x.ClaimId.GetValueOrDefault())
                    .ToHashSet();

                var candidates = overview.Claims
                    .Where(x =>
                        string.Equals(x.Status, "Pending", StringComparison.OrdinalIgnoreCase) &&
                        x.ClaimedAmountMinor == assignment.AmountMinor &&
                        string.Equals(x.CurrencyCode, assignment.CurrencyCode, StringComparison.OrdinalIgnoreCase) &&
                        !alreadyLinkedClaimIds.Contains(x.Id))
                    .ToArray();

                if (candidates.Length == 1)
                {
                    var matchedClaimId = candidates[0].Id;
                    claimId = matchedClaimId;
                    await _store.LinkClaimAsync(
                        assignment.Id,
                        current.HouseholdId,
                        matchedClaimId,
                        cancellationToken);
                }
            }

            var resolvedClaimId = claimId.GetValueOrDefault();
            if (resolvedClaimId == Guid.Empty)
            {
                return BadRequest(new
                {
                    message = "Nie udało się jednoznacznie powiązać zgłoszenia z roszczeniem prywatnym."
                });
            }

            var linkedClaim = overview.Claims.FirstOrDefault(x => x.Id == resolvedClaimId);
            if (linkedClaim is null)
            {
                return BadRequest(new { message = "Nie znaleziono roszczenia prywatnego powiązanego z tą fakturą." });
            }

            if (string.Equals(linkedClaim.Status, "Pending", StringComparison.OrdinalIgnoreCase))
            {
                await _finance.DecidePrivatePaidClaimAsync(
                    new DecidePrivatePaidClaimRequest(
                        resolvedClaimId,
                        amountToBook,
                        current.UserAccountId,
                        CorrelationIdMiddleware.Get(HttpContext),
                        decisionReason,
                        true),
                    cancellationToken);
            }
            else if (!string.Equals(linkedClaim.Status, "Approved", StringComparison.OrdinalIgnoreCase)
                     && !string.Equals(linkedClaim.Status, "PartiallyApproved", StringComparison.OrdinalIgnoreCase))
            {
                return BadRequest(new
                {
                    message = $"Roszczenie ma status „{linkedClaim.Status}” i nie może zostać zaksięgowane jako płatność faktury."
                });
            }
        }

        // Kluczowa część FIX-03:
        // prywatna płatność musi również zostać zapisana w HouseholdInvoicePayments.
        // SourceType=Private nie zmniejsza salda banku/gotówki gospodarstwa,
        // a PayerPersonId wskazuje faktycznego płatnika.
        await _finance.PayInvoiceAsync(
            new PayHouseholdInvoiceRequest(
                assignment.InvoiceId,
                amountToBook,
                "Private",
                assignment.AssigneePersonId,
                assignment.SubmittedAtUtc ?? DateTime.UtcNow,
                current.UserAccountId,
                CorrelationIdMiddleware.Get(HttpContext)),
            cancellationToken);

        if (isNewApproval)
        {
            var marked = await _store.MarkApprovedAsync(
                assignment.Id,
                current.HouseholdId,
                current.UserAccountId,
                amountToBook,
                cancellationToken);

            if (!marked)
            {
                return BadRequest(new
                {
                    message = "Płatność faktury została zaksięgowana, ale nie udało się zaktualizować statusu przekazania. Odśwież stronę."
                });
            }
        }
        else
        {
            await _store.MarkInvoicePaymentBookedAsync(
                assignment.Id,
                current.HouseholdId,
                amountToBook,
                cancellationToken);
        }

        var refreshed = await _finance.GetOverviewAsync(
            current.HouseholdId,
            current.PersonId,
            true,
            cancellationToken);
        var refreshedInvoice = refreshed.Invoices.FirstOrDefault(x => x.Id == assignment.InvoiceId);

        return Json(new
        {
            ok = true,
            message = refreshedInvoice is not null && refreshedInvoice.RemainingMinor <= 0
                ? $"Zaksięgowano płatność {assignment.AssigneeName}. Faktura {assignment.InvoiceNo} jest w pełni rozliczona."
                : $"Zaksięgowano prywatną część {amountToBook / 100m:N2} {assignment.CurrencyCode}. Pozostała część faktury nadal wymaga opłacenia.",
            invoice = refreshedInvoice is null ? null : new
            {
                id = refreshedInvoice.Id,
                paidMinor = refreshedInvoice.PaidMinor,
                remainingMinor = refreshedInvoice.RemainingMinor,
                status = refreshedInvoice.Status,
                currencyCode = refreshedInvoice.CurrencyCode
            }
        });
    }

    [HttpPost("Cancel")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel(Guid assignmentId, CancellationToken cancellationToken)
    {
        var current = await _access.GetCurrentAsync(cancellationToken);
        if (current is null) return Unauthorized();

        if (!await CanHouseholdAsync(
                "householdfinance.invoice.manage",
                current.HouseholdId,
                current.PersonId,
                cancellationToken))
        {
            return Forbid();
        }

        var changed = await _store.CancelAsync(assignmentId, current.HouseholdId, cancellationToken);
        if (!changed)
        {
            return BadRequest(new { message = "Nie można anulować tego przekazania." });
        }

        return Json(new { ok = true, message = "Przekazanie faktury zostało anulowane." });
    }

    [HttpPost("Pay")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Pay(
        Guid assignmentId,
        string? settlementType,
        CancellationToken cancellationToken)
    {
        var current = await _access.GetCurrentAsync(cancellationToken);
        if (current is null) return Unauthorized();

        if (!await CanOwnAsync(
                "householdfinance.claim.submit",
                current.PersonId,
                cancellationToken))
        {
            return Forbid();
        }

        var assignment = await _store.GetAsync(
            assignmentId,
            current.HouseholdId,
            cancellationToken);

        if (assignment is null || assignment.AssigneePersonId != current.PersonId)
        {
            return Forbid();
        }

        if (assignment.Status != "Assigned")
        {
            return BadRequest(new { message = "Ta faktura nie oczekuje już na opłacenie." });
        }

        var normalizedSettlementType = string.Equals(
            settlementType,
            "Compensation",
            StringComparison.OrdinalIgnoreCase)
            ? "Compensation"
            : "Refund";

        var description = $"Faktura przekazana do opłacenia: {assignment.InvoiceNo} — {assignment.Supplier}.";
        if (!string.IsNullOrWhiteSpace(assignment.Note))
        {
            description += $" Notatka administratora: {assignment.Note}";
        }

        // Zapamiętujemy listę roszczeń przed zgłoszeniem, aby pewnie powiązać
        // nowe roszczenie z konkretnym przekazaniem faktury.
        var claimsBefore = await _finance.GetOverviewAsync(
            current.HouseholdId,
            current.PersonId,
            false,
            cancellationToken);
        var claimIdsBefore = claimsBefore.Claims.Select(x => x.Id).ToHashSet();

        await _finance.SubmitPrivatePaidClaimAsync(
            new SubmitPrivatePaidClaimRequest(
                current.HouseholdId,
                current.PersonId,
                assignment.InvoiceId,
                assignment.AmountMinor,
                assignment.CurrencyCode,
                DateTime.UtcNow,
                normalizedSettlementType,
                description),
            cancellationToken);

        var claimsAfter = await _finance.GetOverviewAsync(
            current.HouseholdId,
            current.PersonId,
            false,
            cancellationToken);
        var newClaim = claimsAfter.Claims
            .Where(x =>
                !claimIdsBefore.Contains(x.Id) &&
                string.Equals(x.Status, "Pending", StringComparison.OrdinalIgnoreCase) &&
                x.ClaimedAmountMinor == assignment.AmountMinor &&
                string.Equals(x.CurrencyCode, assignment.CurrencyCode, StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();

        if (!await _store.MarkSubmittedAsync(
                assignment.Id,
                current.HouseholdId,
                current.PersonId,
                normalizedSettlementType,
                newClaim?.Id,
                cancellationToken))
        {
            return BadRequest(new { message = "Płatność została zgłoszona, ale nie udało się zaktualizować statusu zadania. Odśwież stronę i sprawdź sekcję rachunków opłaconych prywatnie." });
        }

        return Json(new
        {
            ok = true,
            message = "Opłacenie zostało zgłoszone. Faktura czeka teraz na zatwierdzenie i zaksięgowanie przez administratora."
        });
    }

    private Task<bool> CanHouseholdAsync(
        string permissionCode,
        Guid householdId,
        Guid personId,
        CancellationToken cancellationToken)
        => _access.CanAsync(
            permissionCode,
            ResourceScopeTypes.Household,
            householdId.ToString("D"),
            ownerPersonId: personId,
            resourceType: "HouseholdFinance",
            resourceId: householdId.ToString("D"),
            cancellationToken: cancellationToken);

    private Task<bool> CanOwnAsync(
        string permissionCode,
        Guid personId,
        CancellationToken cancellationToken)
        => _access.CanAsync(
            permissionCode,
            ResourceScopeTypes.Own,
            personId.ToString("D"),
            ownerPersonId: personId,
            resourceType: "HouseholdFinance",
            resourceId: personId.ToString("D"),
            cancellationToken: cancellationToken);
}
