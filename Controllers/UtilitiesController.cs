using EDom.Application.Utilities;
using EDom.Application.Collaboration;
using EDom.Domain.Authorization;
using EDom.Domain.Utilities;
using EDom.Infrastructure.Persistence;
using EDom.Web.Authorization;
using EDom.Web.Infrastructure;
using EDom.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EDom.Web.Controllers;

[Authorize]
[Route("Utilities")]
public sealed class UtilitiesController(
    WebAccessService access,
    IUtilitiesService utilitiesService,
    ICollaborationService collaborationService,
    EDomDbContext db) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var actor = await GetActorAsync(cancellationToken); if (actor is null) return Forbid();
        var overview = await utilitiesService.GetOverviewAsync(actor, cancellationToken);
        var parcels = await db.Parcels.AsNoTracking().Where(x => x.HouseholdId == actor.HouseholdId).OrderBy(x => x.Name).ToListAsync(cancellationToken);
        var parcelIds = parcels.Select(x => x.Id).ToArray();
        var buildings = await db.Buildings.AsNoTracking().Where(x => parcelIds.Contains(x.ParcelId)).OrderBy(x => x.Name).ToListAsync(cancellationToken);
        var buildingIds = buildings.Select(x => x.Id).ToArray();
        var rooms = await db.Rooms.AsNoTracking().Where(x => buildingIds.Contains(x.BuildingId)).OrderBy(x => x.Name).ToListAsync(cancellationToken);
        var canManage = await access.CanAsync("utilities.invoice.manage", ResourceScopeTypes.Household, actor.HouseholdId.ToString("D"), cancellationToken: cancellationToken);
        if (!canManage)
            foreach (var parcel in parcels)
                if (await access.CanAsync("utilities.invoice.manage", ResourceScopeTypes.Property, parcel.Id.ToString("D"), cancellationToken: cancellationToken)) { canManage = true; break; }
        if (overview.Meters.Count == 0)
        {
            if (!canManage) return Forbid();
        }
        return View(new UtilitiesPageViewModel(overview, parcels, buildings, rooms, canManage));
    }

    [HttpPost("Reading/Submit"), ValidateAntiForgeryToken]
    public async Task<IActionResult> SubmitReading(Guid meterId, DateTime readingAtLocal, decimal value, string zoneCode, string source, IFormFile? photo, CancellationToken cancellationToken)
    {
        var actor = await GetActorAsync(cancellationToken); if (actor is null) return Forbid();
        try
        {
            Guid? photoDocumentId = null;
            if (photo is { Length: > 0 })
            {
                if (photo.Length > 25 * 1024 * 1024) throw new InvalidOperationException("Zdjęcie odczytu przekracza limit 25 MB.");
                await using var memory = new MemoryStream();
                await photo.CopyToAsync(memory, cancellationToken);
                var document = await collaborationService.CreateDocumentAsync(
                    new CollaborationActor(actor.AccountId, actor.PersonId, actor.HouseholdId, actor.CorrelationId, actor.NowUtc),
                    new CreateDocumentRequest($"Odczyt licznika {readingAtLocal:yyyy-MM-dd HH:mm}", "MeterReading", "Standard", ResourceScopeTypes.Household, actor.HouseholdId.ToString("D"), photo.FileName, string.IsNullOrWhiteSpace(photo.ContentType) ? "application/octet-stream" : photo.ContentType, memory.ToArray(), SourceModule: "Utilities", SourceObjectType: "Meter", SourceObjectId: meterId.ToString("D")),
                    cancellationToken);
                photoDocumentId = document.Id;
            }
            await utilitiesService.SubmitReadingAsync(actor, new(meterId, readingAtLocal.ToUniversalTime(), source, [new(zoneCode, value)], photoDocumentId), cancellationToken);
            TempData["Success"] = "Odczyt został zgłoszony do zatwierdzenia.";
        }
        catch (Exception ex) { TempData["Error"] = ex.Message; }
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("Reading/Approve"), ValidateAntiForgeryToken]
    public Task<IActionResult> ApproveReading(Guid readingId, string? reason, CancellationToken cancellationToken)
        => ExecuteAsync(async actor => { await utilitiesService.ApproveReadingAsync(actor, readingId, reason, cancellationToken); return "Odczyt został zatwierdzony."; }, cancellationToken);

    [HttpPost("Reading/Reject"), ValidateAntiForgeryToken]
    public Task<IActionResult> RejectReading(Guid readingId, string reason, CancellationToken cancellationToken)
        => ExecuteAsync(async actor => { await utilitiesService.RejectReadingAsync(actor, readingId, reason, cancellationToken); return "Odczyt został odrzucony."; }, cancellationToken);

    [HttpPost("Reading/Correct"), ValidateAntiForgeryToken]
    public Task<IActionResult> CorrectReading(Guid readingId, DateTime readingAtLocal, decimal value, string zoneCode, string reason, bool resetOrReplacement, CancellationToken cancellationToken)
        => ExecuteAsync(async actor => { await utilitiesService.CorrectReadingAsync(actor, readingId, readingAtLocal.ToUniversalTime(), [new(zoneCode, value)], reason, resetOrReplacement, cancellationToken); return "Utworzono jawną korektę odczytu; poprzedni rekord pozostał w historii."; }, cancellationToken);

    [HttpPost("Contract/Create"), ValidateAntiForgeryToken]
    public Task<IActionResult> CreateContract(Guid parcelId, Guid meterId, string operatorName, string medium, string? contractNumber, string? accountPoint, string billingSchedule, DateOnly validFrom, DateOnly? validTo, decimal fixedCharge, string currencyCode, CancellationToken cancellationToken)
        => ExecuteAsync(async actor => { await utilitiesService.CreateContractAsync(actor, new(parcelId, operatorName, medium, contractNumber, accountPoint, billingSchedule, validFrom, validTo, ToMinor(fixedCharge), currencyCode, meterId == Guid.Empty ? Array.Empty<Guid>() : [meterId]), cancellationToken); return "Dodano umowę operatora."; }, cancellationToken);

    [HttpPost("Tariff/Create"), ValidateAntiForgeryToken]
    public Task<IActionResult> CreateTariff(Guid contractId, string name, DateOnly validFrom, DateOnly? validTo, string currencyCode, string zoneCode, string componentCode, decimal ratePerUnit, string unitCode, CancellationToken cancellationToken)
        => ExecuteAsync(async actor => { await utilitiesService.CreateTariffAsync(actor, new(contractId, name, validFrom, validTo, currencyCode, "Gross", "{}", [new(zoneCode, componentCode, ratePerUnit, 6, unitCode, validFrom, validTo)]), cancellationToken); return "Dodano wersję taryfy i stawkę."; }, cancellationToken);

    [HttpPost("Forecast/Create"), ValidateAntiForgeryToken]
    public Task<IActionResult> CreateForecast(Guid contractId, Guid? meterId, DateOnly periodFrom, DateOnly periodTo, decimal estimatedQuantity, string zoneCode, CancellationToken cancellationToken)
        => ExecuteAsync(async actor => { await utilitiesService.CreateForecastAsync(actor, new(contractId, meterId, periodFrom, periodTo, ToScaled(estimatedQuantity, 3), 3, zoneCode), cancellationToken); return "Utworzono prognozę bez księgowania kosztu."; }, cancellationToken);

    [HttpPost("Invoice/Create"), ValidateAntiForgeryToken]
    public Task<IActionResult> CreateInvoice(Guid contractId, string invoiceNo, DateOnly periodFrom, DateOnly periodTo, DateOnly issuedOn, DateOnly dueDate, decimal totalAmount, string currencyCode, string componentCode, CancellationToken cancellationToken)
        => ExecuteAsync(async actor => { var minor = ToMinor(totalAmount); await utilitiesService.RegisterInvoiceAsync(actor, new(contractId, invoiceNo, periodFrom, periodTo, issuedOn, dueDate, minor, currencyCode, [new(componentCode, minor)]), cancellationToken); return "Zarejestrowano fakturę operatora i powiązano ją z finansami domowymi."; }, cancellationToken);

    [HttpPost("Allocation/Manual"), ValidateAntiForgeryToken]
    public Task<IActionResult> ManualAllocation(Guid invoiceId, Guid parcelId, string medium, string targetType1, Guid targetId1, decimal amount1, string? targetType2, Guid? targetId2, decimal? amount2, string? targetType3, Guid? targetId3, decimal? amount3, string? note, CancellationToken cancellationToken)
        => ExecuteAsync(async actor =>
        {
            var items = new List<AllocationInput> { new(targetType1, targetId1, ToMinor(amount1)) };
            if (!string.IsNullOrWhiteSpace(targetType2))
            {
                if (targetId2.HasValue)
                {
                    if (amount2.HasValue)
                    {
                        if (amount2.Value != 0) items.Add(new(targetType2, targetId2.Value, ToMinor(amount2.Value)));
                    }
                }
            }
            if (!string.IsNullOrWhiteSpace(targetType3))
            {
                if (targetId3.HasValue)
                {
                    if (amount3.HasValue)
                    {
                        if (amount3.Value != 0) items.Add(new(targetType3, targetId3.Value, ToMinor(amount3.Value)));
                    }
                }
            }
            await utilitiesService.CreateManualAllocationAsync(actor, new(invoiceId, parcelId, medium, items, note), cancellationToken);
            return "Zatwierdzono ręczną alokację pełnej kwoty.";
        }, cancellationToken);

    [HttpPost("Allocation/Person"), ValidateAntiForgeryToken]
    public Task<IActionResult> PerPersonAllocation(Guid invoiceId, Guid parcelId, string medium, string targetType, Guid targetId, int personCount, CancellationToken cancellationToken)
        => ExecuteAsync(async actor => { await utilitiesService.CreatePerPersonAllocationAsync(actor, new(invoiceId, parcelId, medium, targetType, targetId, personCount, "{\"source\":\"UI\"}"), cancellationToken); return "Zapisano alokację wraz ze snapshotem liczby osób."; }, cancellationToken);

    [HttpPost("WasteRate/Create"), ValidateAntiForgeryToken]
    public Task<IActionResult> CreateWasteRate(Guid parcelId, decimal amountPerPerson, string currencyCode, DateOnly validFrom, DateOnly? validTo, bool childAsAdult, CancellationToken cancellationToken)
        => ExecuteAsync(async actor => { await utilitiesService.CreateWasteRateAsync(actor, new(parcelId, ToMinor(amountPerPerson), currencyCode, validFrom, validTo, $"{{\"childAsAdult\":{childAsAdult.ToString().ToLowerInvariant()}}}"), cancellationToken); return "Dodano stawkę odpadów na osobę."; }, cancellationToken);

    [HttpPost("Pellet/Create"), ValidateAntiForgeryToken]
    public Task<IActionResult> CreatePellet(Guid buildingId, string supplier, decimal quantity, string unitCode, decimal totalAmount, string currencyCode, DateOnly deliveryDate, string startPeriodKey, CancellationToken cancellationToken)
        => ExecuteAsync(async actor => { await utilitiesService.CreatePelletPlanAsync(actor, new(buildingId, supplier, quantity, unitCode, ToMinor(totalAmount), currencyCode, deliveryDate, startPeriodKey, 12), cancellationToken); return "Zapisano zakup pelletu i utworzono 12-miesięczny plan kosztu."; }, cancellationToken);

    private async Task<IActionResult> ExecuteAsync(Func<UtilityActor, Task<string>> operation, CancellationToken cancellationToken)
    {
        var actor = await GetActorAsync(cancellationToken); if (actor is null) return Forbid();
        try { TempData["Success"] = await operation(actor); }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (Exception ex) { TempData["Error"] = ex.Message; }
        return RedirectToAction(nameof(Index));
    }

    private async Task<UtilityActor?> GetActorAsync(CancellationToken cancellationToken)
    {
        var current = await access.GetCurrentAsync(cancellationToken);
        return current is null ? null : new UtilityActor(current.UserAccountId, current.PersonId, current.HouseholdId, CorrelationIdMiddleware.Get(HttpContext), DateTime.UtcNow);
    }

    private static long ToMinor(decimal amount) => checked((long)Math.Round(amount * 100m, 0, MidpointRounding.AwayFromZero));
    private static long ToScaled(decimal amount, int scale) => checked((long)Math.Round(amount * (decimal)Math.Pow(10, scale), 0, MidpointRounding.AwayFromZero));
}
