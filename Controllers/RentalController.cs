using EDom.Application.Collaboration;
using EDom.Application.Rental;
using EDom.Domain.Rental;
using EDom.Web.Authorization;
using EDom.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EDom.Web.Controllers;

[Authorize]
[Route("Rental")]
public sealed class RentalController(
    WebAccessService access,
    IRentalService rentalService,
    ILeaseClosingService leaseClosingService,
    ICollaborationService collaborationService) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var actor = await GetActorAsync(cancellationToken); if (actor is null) return Forbid();
        var model = await rentalService.GetOverviewAsync(actor, cancellationToken);
        if (!model.CanManage && !model.IsTenant && model.Contracts.Count == 0) return Forbid();
        return View(model);
    }

    [HttpPost("Template"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Template(string name, string leaseType, string bodyTemplate, DateOnly effectiveFrom, CancellationToken cancellationToken)
        => await ExecuteAsync(async actor => { await rentalService.CreateTemplateAsync(actor, new(name, leaseType, bodyTemplate, effectiveFrom), cancellationToken); return "Dodano szablon umowy."; }, cancellationToken);

    [HttpPost("Create"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(Guid roomId, Guid? templateId, string firstName, string lastName, DateOnly? birthDate,
        string? email, string? phone, string login, string temporaryPassword, bool mustChangePassword, Guid? landlordPersonId,
        DateOnly leaseFrom, DateOnly? leaseTo, decimal rentAmount, string currencyCode, int dueDay, decimal advanceAmount,
        decimal depositAmount, string? utilitiesRulesText, CancellationToken cancellationToken)
        => await ExecuteAsync(async actor =>
        {
            await rentalService.CreateLeaseDraftAsync(actor, new CreateLeaseDraftRequest(roomId, templateId, null, firstName, lastName, birthDate,
                email, phone, login, temporaryPassword, mustChangePassword, landlordPersonId, leaseFrom, leaseTo,
                ToMinor(rentAmount), currencyCode, dueDay, ToMinor(advanceAmount), ToMinor(depositAmount), utilitiesRulesText), cancellationToken);
            return "Przygotowano konto lokatora, umowę i dokument PDF. Umowa czeka na potwierdzenie podpisania.";
        }, cancellationToken);

    [HttpPost("Activate"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Activate(Guid contractId, DateOnly signedOn, string signatureMethod, string? comment, CancellationToken cancellationToken)
        => await ExecuteAsync(async actor => { await rentalService.ActivateLeaseAsync(actor, new(contractId, signedOn, signatureMethod, comment), cancellationToken); return "Umowa została podpisana i aktywowana; pokój jest wynajęty."; }, cancellationToken);

    [HttpPost("Amend"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Amend(Guid contractId, DateOnly effectiveOn, decimal? newRentAmount, DateOnly? newLeaseTo, string? reason, CancellationToken cancellationToken)
        => await ExecuteAsync(async actor => { await rentalService.CreateAmendmentAsync(actor, new(contractId, effectiveOn, newRentAmount.HasValue ? ToMinor(newRentAmount.Value) : null, newLeaseTo, reason), cancellationToken); return "Utworzono aneks bez nadpisywania pierwotnych warunków."; }, cancellationToken);

    [HttpPost("End"), ValidateAntiForgeryToken]
    public async Task<IActionResult> End(Guid contractId, DateOnly endedOn, string reason, CancellationToken cancellationToken)
        => await ExecuteAsync(async actor =>
        {
            await leaseClosingService.StartAsync(actor, new StartLeaseClosingRequest(contractId, endedOn, endedOn, reason), cancellationToken);
            return "Rozpoczęto proces zamknięcia najmu i wygaszono aktywne przypisanie lokatora. Pokój zostanie zwolniony dopiero po odbiorze i rozliczeniu końcowym.";
        }, cancellationToken);

    [HttpPost("Deposit"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Deposit(Guid contractId, decimal amount, DateOnly paidOn, CancellationToken cancellationToken)
        => await ExecuteAsync(async actor => { await rentalService.RecordDepositPaymentAsync(actor, new(contractId, ToMinor(amount), paidOn), cancellationToken); return "Zarejestrowano wpłatę kaucji."; }, cancellationToken);

    [HttpPost("Protocol"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Protocol(Guid contractId, string protocolType, DateOnly protocolDate, string? notes, CancellationToken cancellationToken)
        => await ExecuteAsync(async actor => { await rentalService.CreateProtocolAsync(actor, new(contractId, protocolType, protocolDate, notes), cancellationToken); return "Utworzono protokół wraz z dokumentem PDF."; }, cancellationToken);

    [HttpGet("Document/{documentId:guid}")]
    public async Task<IActionResult> Document(Guid documentId, CancellationToken cancellationToken)
    {
        var current = await access.GetCurrentAsync(cancellationToken); if (current is null) return Forbid();
        var download = await collaborationService.DownloadDocumentAsync(new CollaborationActor(current.UserAccountId, current.PersonId, current.HouseholdId, CorrelationIdMiddleware.Get(HttpContext), DateTime.UtcNow), documentId, cancellationToken);
        return download is null ? NotFound() : File(download.Content, download.ContentType, download.FileName);
    }

    private async Task<IActionResult> ExecuteAsync(Func<RentalActor, Task<string>> operation, CancellationToken cancellationToken)
    {
        var actor = await GetActorAsync(cancellationToken); if (actor is null) return Forbid();
        try { TempData["Success"] = await operation(actor); }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (Exception ex) { TempData["Error"] = ex.Message; }
        return RedirectToAction(nameof(Index));
    }

    private async Task<RentalActor?> GetActorAsync(CancellationToken cancellationToken)
    {
        var current = await access.GetCurrentAsync(cancellationToken);
        return current is null ? null : new RentalActor(current.UserAccountId, current.PersonId, current.HouseholdId, CorrelationIdMiddleware.Get(HttpContext), DateTime.UtcNow);
    }

    private static long ToMinor(decimal amount) => checked((long)Math.Round(amount * 100m, 0, MidpointRounding.AwayFromZero));
}
