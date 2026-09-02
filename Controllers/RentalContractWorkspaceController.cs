using EDom.Application.Rental;
using EDom.Web.Authorization;
using EDom.Web.Infrastructure;
using EDom.Web.Models;
using EDom.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EDom.Web.Controllers;

[Authorize]
[Route("Rental/Workspace")]
public sealed class RentalContractWorkspaceController(
    WebAccessService access,
    IRentalService rentalService,
    IWebHostEnvironment environment) : Controller
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

        var model = await rentalService.GetOverviewAsync(
            actor,
            cancellationToken);

        var current = await access.GetCurrentAsync(cancellationToken);
        if (current is null)
        {
            return Unauthorized();
        }

        var store = new RentalContractAnnexUiStore(environment.ContentRootPath);
        var annexes = await store.GetForHouseholdAsync(
            current.HouseholdId,
            cancellationToken);

        return Json(new
        {
            canManage = model.CanManage,
            annexes
        });
    }

    [HttpPost("CreateAnnex")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateAnnex(
        Guid contractId,
        DateOnly effectiveOn,
        decimal? newRentAmount,
        DateOnly? newLeaseTo,
        string? clauseTitle,
        string? clauseText,
        string? reason,
        CancellationToken cancellationToken)
    {
        var actor = await GetActorAsync(cancellationToken);
        if (actor is null)
        {
            return Unauthorized();
        }

        var current = await access.GetCurrentAsync(cancellationToken);
        if (current is null)
        {
            return Unauthorized();
        }

        try
        {
            var model = await rentalService.GetOverviewAsync(
                actor,
                cancellationToken);

            if (!model.CanManage)
            {
                return Forbid();
            }

            var contract = model.Contracts.FirstOrDefault(
                x => x.ContractId == contractId);

            if (contract is null)
            {
                return BadRequest(new
                {
                    message = "Nie znaleziono wybranej umowy."
                });
            }

            if (effectiveOn < contract.LeaseFrom)
            {
                return BadRequest(new
                {
                    message = "Aneks nie może obowiązywać przed rozpoczęciem umowy."
                });
            }

            if (newRentAmount is < 0m)
            {
                return BadRequest(new
                {
                    message = "Czynsz nie może być ujemny."
                });
            }

            if (newLeaseTo.HasValue
                && newLeaseTo.GetValueOrDefault() < effectiveOn)
            {
                return BadRequest(new
                {
                    message = "Nowa data końca umowy nie może być wcześniejsza niż data wejścia aneksu w życie."
                });
            }

            var cleanTitle = Clean(clauseTitle);
            var cleanClause = Clean(clauseText);
            var cleanReason = Clean(reason);

            var hasRentChange = newRentAmount.HasValue;
            var hasEndDateChange = newLeaseTo.HasValue
                && newLeaseTo != contract.LeaseTo;
            var hasNewClause = cleanTitle is not null
                || cleanClause is not null;

            if (!hasRentChange
                && !hasEndDateChange
                && !hasNewClause
                && cleanReason is null)
            {
                return BadRequest(new
                {
                    message = "Wprowadź przynajmniej jedną zmianę lub nowe postanowienie."
                });
            }

            var nativeReason = BuildNativeReason(
                cleanReason,
                cleanTitle,
                cleanClause);

            long? newRentMinor = newRentAmount.HasValue
                ? ToMinor(newRentAmount.GetValueOrDefault())
                : null;

            await rentalService.CreateAmendmentAsync(
                actor,
                new(
                    contractId,
                    effectiveOn,
                    newRentMinor,
                    newLeaseTo,
                    nativeReason),
                cancellationToken);

            var store = new RentalContractAnnexUiStore(
                environment.ContentRootPath);

            var annex = await store.AddAsync(
                current.HouseholdId,
                contractId,
                effectiveOn,
                contract.TenantName,
                contract.RoomName,
                contract.CurrencyCode,
                contract.RentAmountMinor,
                newRentMinor,
                contract.LeaseTo,
                newLeaseTo,
                cleanTitle,
                cleanClause,
                cleanReason,
                current.UserAccountId,
                cancellationToken);

            return Json(new
            {
                ok = true,
                annexId = annex.Id,
                annexNumber = annex.AnnexNumber,
                message =
                    $"Utworzono Aneks nr {annex.AnnexNumber}. Oryginalna umowa pozostała bez zmian."
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

    [HttpGet("Annex/{annexId:guid}")]
    public async Task<IActionResult> Annex(
        Guid annexId,
        CancellationToken cancellationToken)
    {
        var current = await access.GetCurrentAsync(cancellationToken);
        if (current is null)
        {
            return Forbid();
        }

        var actor = await GetActorAsync(cancellationToken);
        if (actor is null)
        {
            return Forbid();
        }

        var overview = await rentalService.GetOverviewAsync(
            actor,
            cancellationToken);

        var store = new RentalContractAnnexUiStore(environment.ContentRootPath);
        var annex = await store.GetAsync(
            current.HouseholdId,
            annexId,
            cancellationToken);

        if (annex is null)
        {
            return NotFound();
        }

        if (!overview.CanManage
            && !overview.Contracts.Any(x =>
                x.ContractId == annex.ContractId))
        {
            return Forbid();
        }

        return View(new RentalAnnexPreviewViewModel(
            annex,
            current.HouseholdName));
    }

    private async Task<RentalActor?> GetActorAsync(
        CancellationToken cancellationToken)
    {
        var current = await access.GetCurrentAsync(cancellationToken);

        return current is null
            ? null
            : new RentalActor(
                current.UserAccountId,
                current.PersonId,
                current.HouseholdId,
                CorrelationIdMiddleware.Get(HttpContext),
                DateTime.UtcNow);
    }

    private static string BuildNativeReason(
        string? reason,
        string? clauseTitle,
        string? clauseText)
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(reason))
        {
            parts.Add(reason.Trim());
        }

        if (!string.IsNullOrWhiteSpace(clauseTitle)
            || !string.IsNullOrWhiteSpace(clauseText))
        {
            var title = string.IsNullOrWhiteSpace(clauseTitle)
                ? "Dodatkowe postanowienie"
                : clauseTitle.Trim();

            var body = string.IsNullOrWhiteSpace(clauseText)
                ? string.Empty
                : clauseText.Trim();

            parts.Add(
                string.IsNullOrWhiteSpace(body)
                    ? $"Dodano postanowienie: {title}."
                    : $"Dodano postanowienie: {title}. {body}");
        }

        return parts.Count == 0
            ? "Zmiana warunków umowy."
            : string.Join(" | ", parts);
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim();

    private static long ToMinor(decimal amount) =>
        checked((long)Math.Round(
            amount * 100m,
            0,
            MidpointRounding.AwayFromZero));
}
