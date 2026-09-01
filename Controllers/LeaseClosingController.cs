using EDom.Application.Collaboration;
using EDom.Application.Rental;
using EDom.Domain.Rental;
using EDom.Web.Authorization;
using EDom.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EDom.Web.Controllers;

[Authorize]
[Route("Rental/Closing")]
public sealed class LeaseClosingController(
    WebAccessService access,
    ILeaseClosingService closingService,
    IRentalService rentalService,
    ICollaborationService collaborationService) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var actor = await GetActorAsync(cancellationToken); if (actor is null) return Forbid();
        var model = await closingService.GetOverviewAsync(actor, cancellationToken);
        if (!model.CanManage && model.Closings.Count == 0) return Forbid();
        return View(model);
    }

    [HttpPost("Start"), ValidateAntiForgeryToken]
    public Task<IActionResult> Start(Guid contractId, DateOnly? plannedMoveOutOn, DateOnly actualMoveOutOn, string reason, CancellationToken cancellationToken)
        => ExecuteAsync(async actor => { await closingService.StartAsync(actor, new(contractId, plannedMoveOutOn, actualMoveOutOn, reason), cancellationToken); return "Rozpoczęto proces zamknięcia najmu."; }, cancellationToken);

    [HttpPost("ReturnProtocol"), ValidateAntiForgeryToken]
    public Task<IActionResult> ReturnProtocol(Guid closingId, Guid contractId, DateOnly protocolDate, string? notes, CancellationToken cancellationToken)
        => ExecuteAsync(async actor =>
        {
            await rentalService.CreateProtocolAsync(actor, new CreateHandoverProtocolRequest(contractId, ProtocolTypes.Return, protocolDate, notes), cancellationToken);
            await closingService.RefreshAsync(actor, closingId, cancellationToken);
            return "Utworzono protokół odbioru pokoju i odświeżono saldo końcowe.";
        }, cancellationToken);

    [HttpPost("SkipReturnProtocol"), ValidateAntiForgeryToken]
    public Task<IActionResult> SkipReturnProtocol(Guid closingId, string reason, CancellationToken cancellationToken)
        => ExecuteAsync(async actor => { await closingService.SkipReturnProtocolAsync(actor, new(closingId, reason), cancellationToken); return "Pominięcie protokołu odbioru zapisano wraz z uzasadnieniem."; }, cancellationToken);

    [HttpPost("Deposit/Deduct"), ValidateAntiForgeryToken]
    public Task<IActionResult> DeductDeposit(Guid closingId, decimal amount, string category, string reason, CancellationToken cancellationToken)
        => ExecuteAsync(async actor => { await closingService.AddDepositDeductionAsync(actor, new(closingId, ToMinor(amount), category, reason), cancellationToken); return "Zapisano udokumentowane potrącenie z kaucji."; }, cancellationToken);

    [HttpPost("Deposit/Refund"), ValidateAntiForgeryToken]
    public Task<IActionResult> RefundDeposit(Guid closingId, decimal amount, string paymentMethod, string reason, CancellationToken cancellationToken)
        => ExecuteAsync(async actor => { await closingService.RefundDepositAsync(actor, new(closingId, ToMinor(amount), paymentMethod, reason), cancellationToken); return "Zapisano zwrot kaucji."; }, cancellationToken);

    [HttpPost("Refresh"), ValidateAntiForgeryToken]
    public Task<IActionResult> Refresh(Guid closingId, CancellationToken cancellationToken)
        => ExecuteAsync(async actor => { await closingService.RefreshAsync(actor, closingId, cancellationToken); return "Przeliczono końcowe saldo najmu."; }, cancellationToken);

    [HttpPost("Finalize"), ValidateAntiForgeryToken]
    public Task<IActionResult> Finalize(Guid closingId, string accountDisposition, CancellationToken cancellationToken)
        => ExecuteAsync(async actor => { await closingService.FinalizeAsync(actor, new(closingId, accountDisposition), cancellationToken); return "Najem został rozliczony i formalnie zamknięty. Historia umowy i rozliczeń została zachowana."; }, cancellationToken);

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
