using EDom.Application.HouseholdFinance;
using EDom.Application.Households;
using EDom.Domain.Authorization;
using EDom.Web.Authorization;
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

            var invoices = overview.Invoices
                .Where(x => x.RemainingMinor > 0)
                .OrderBy(x => x.DueDate)
                .Select(x => new
                {
                    id = x.Id,
                    invoiceNo = x.InvoiceNo,
                    supplier = x.Supplier,
                    amountMinor = x.RemainingMinor,
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

        return Json(new
        {
            canManage = false,
            assignments = ownAssignments
        });
    }

    [HttpPost("Assign")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Assign(
        Guid invoiceId,
        Guid personId,
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
        if (existing.Any(x => x.InvoiceId == invoiceId && x.Status == "Submitted"))
        {
            return BadRequest(new { message = "Dla tej faktury domownik zgłosił już prywatne opłacenie. Najpierw rozpatrz jego rozliczenie." });
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

        var assignment = await _store.AssignAsync(
            current.HouseholdId,
            invoice.Id,
            invoice.InvoiceNo,
            invoice.Supplier,
            invoice.RemainingMinor,
            invoice.CurrencyCode,
            invoice.DueDate,
            person.PersonId,
            person.DisplayName,
            current.UserAccountId,
            note,
            cancellationToken);

        return Json(new
        {
            ok = true,
            message = $"Fakturę {invoice.InvoiceNo} przekazano do opłacenia: {person.DisplayName}.",
            assignment
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

        if (!await _store.MarkSubmittedAsync(
                assignment.Id,
                current.HouseholdId,
                current.PersonId,
                cancellationToken))
        {
            return BadRequest(new { message = "Płatność została zgłoszona, ale nie udało się zaktualizować statusu zadania. Odśwież stronę i sprawdź sekcję rachunków opłaconych prywatnie." });
        }

        return Json(new
        {
            ok = true,
            message = "Zarejestrowano opłacenie faktury ze środków prywatnych. Administrator zobaczy zgłoszenie do rozliczenia."
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
