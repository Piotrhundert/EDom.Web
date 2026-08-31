using EDom.Application.Collaboration;
using EDom.Application.Administration;
using EDom.Domain.Authorization;
using EDom.Web.Authorization;
using EDom.Web.Infrastructure;
using EDom.Web.Models;
using EDom.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EDom.Web.Controllers;

[Authorize]
public sealed class DocumentsController(
    WebAccessService access,
    ICollaborationService collaboration,
    IAdministrationCrudService adminCrud,
    EDomDbContext db) : Controller
{
    [HttpGet("/Documents")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var current = await access.GetCurrentAsync(cancellationToken);
        if (current is null) return Challenge();
        var canOwn = await access.CanAsync("documents.document.manage_own", ResourceScopeTypes.Own, current.PersonId.ToString("D"), current.PersonId, resourceType: "DocumentRepository", cancellationToken: cancellationToken);
        var canShared = await access.CanAsync("documents.document.manage_shared", ResourceScopeTypes.Household, current.HouseholdId.ToString("D"), resourceType: "DocumentRepository", cancellationToken: cancellationToken);
        if (!canOwn && !canShared)
            return Forbid();
        ViewData["CurrentPersonId"] = current.PersonId;
        var actor = Actor(current);
        var items = await collaboration.ListDocumentsAsync(actor, cancellationToken);
        return View(new DocumentsPageModel(items, canOwn, canShared));
    }

    [HttpPost("/Documents/Upload")]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(26L * 1024 * 1024)]
    public async Task<IActionResult> Upload(string title, string? category, string? scope, IFormFile file, CancellationToken cancellationToken)
    {
        var current = await access.GetCurrentAsync(cancellationToken);
        if (current is null) return Challenge();
        if (file is null || file.Length <= 0) return BadRequest("Plik jest wymagany.");
        await using var stream = new MemoryStream();
        await file.CopyToAsync(stream, cancellationToken);
        var scopeType = string.Equals(scope, "Household", StringComparison.OrdinalIgnoreCase) ? ResourceScopeTypes.Household : ResourceScopeTypes.Own;
        try
        {
            await collaboration.CreateDocumentAsync(Actor(current), new CreateDocumentRequest(
                title, string.IsNullOrWhiteSpace(category) ? "Other" : category, "Standard", scopeType,
                scopeType == ResourceScopeTypes.Own ? current.PersonId.ToString("D") : current.HouseholdId.ToString("D"),
                file.FileName, file.ContentType, stream.ToArray()), cancellationToken);
            return RedirectToAction(nameof(Index));
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
    }

    [HttpGet("/Documents/{id:guid}/Download")]
    public async Task<IActionResult> Download(Guid id, CancellationToken cancellationToken)
    {
        var current = await access.GetCurrentAsync(cancellationToken);
        if (current is null) return Challenge();
        try
        {
            var result = await collaboration.DownloadDocumentAsync(Actor(current), id, cancellationToken);
            return result is null ? NotFound() : File(result.Content, result.ContentType, result.FileName);
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
    }

    [HttpPost("/Documents/{id:guid}/Edit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(Guid id, string title, string category, CancellationToken cancellationToken)
    {
        var current = await access.GetCurrentAsync(cancellationToken); if (current is null) return Challenge();
        var document = await db.DocumentItems.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.HouseholdId == current.HouseholdId, cancellationToken);
        if (document is null) return NotFound();
        if (document.OwnerPersonId != current.PersonId) return Forbid();
        var permission = document.ScopeType == ResourceScopeTypes.Own ? "documents.document.manage_own" : "documents.document.manage_shared";
        var scopeType = document.ScopeType == ResourceScopeTypes.Own ? ResourceScopeTypes.Own : ResourceScopeTypes.Household;
        var scopeId = document.ScopeType == ResourceScopeTypes.Own ? current.PersonId.ToString("D") : current.HouseholdId.ToString("D");
        if (!await access.CanAsync(permission, scopeType, scopeId, current.PersonId, resourceType: "Document", resourceId: id.ToString("D"), cancellationToken: cancellationToken)) return Forbid();
        try
        {
            await adminCrud.UpdateDocumentAsync(new UpdateDocumentAdminRequest(current.HouseholdId, current.PersonId, id, title, category, current.UserAccountId, CorrelationIdMiddleware.Get(HttpContext)), cancellationToken);
            TempData["Success"] = "Zmieniono metadane dokumentu.";
        }
        catch (Exception ex) { TempData["Error"] = ex.Message; }
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("/Documents/{id:guid}/Archive")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Archive(Guid id, CancellationToken cancellationToken)
    {
        var current = await access.GetCurrentAsync(cancellationToken); if (current is null) return Challenge();
        var document = await db.DocumentItems.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.HouseholdId == current.HouseholdId, cancellationToken);
        if (document is null) return NotFound();
        if (document.OwnerPersonId != current.PersonId) return Forbid();
        var permission = document.ScopeType == ResourceScopeTypes.Own ? "documents.document.manage_own" : "documents.document.manage_shared";
        var scopeType = document.ScopeType == ResourceScopeTypes.Own ? ResourceScopeTypes.Own : ResourceScopeTypes.Household;
        var scopeId = document.ScopeType == ResourceScopeTypes.Own ? current.PersonId.ToString("D") : current.HouseholdId.ToString("D");
        if (!await access.CanAsync(permission, scopeType, scopeId, current.PersonId, resourceType: "Document", resourceId: id.ToString("D"), cancellationToken: cancellationToken)) return Forbid();
        try { await adminCrud.ArchiveDocumentAsync(current.HouseholdId, current.PersonId, id, current.UserAccountId, CorrelationIdMiddleware.Get(HttpContext), cancellationToken); TempData["Success"] = "Dokument zarchiwizowano bez usuwania pliku historycznego."; }
        catch (Exception ex) { TempData["Error"] = ex.Message; }
        return RedirectToAction(nameof(Index));
    }

    private CollaborationActor Actor(WebUserContext current)
        => new(current.UserAccountId, current.PersonId, current.HouseholdId, CorrelationIdMiddleware.Get(HttpContext), DateTime.UtcNow);
}
