using EDom.Application.Property;
using EDom.Application.Administration;
using EDom.Domain.Authorization;
using EDom.Domain.Property;
using EDom.Web.Authorization;
using EDom.Web.Infrastructure;
using EDom.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EDom.Web.Controllers;

[Authorize]
[Route("Property")]
public sealed class PropertyController(WebAccessService access, IPropertyAssetService propertyService, IAdministrationCrudService adminCrud) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var actor = await GetActorAsync(cancellationToken);
        if (actor is null) return Forbid();
        var overview = await propertyService.GetOverviewAsync(actor, cancellationToken);
        if (overview.Parcels.Count == 0
            && !await access.CanAsync("property.structure.manage", ResourceScopeTypes.Household, actor.HouseholdId.ToString("D"), cancellationToken: cancellationToken))
            return Forbid();

        ViewData["CanCreateParcel"] = await access.CanAsync("property.structure.manage", ResourceScopeTypes.Household, actor.HouseholdId.ToString("D"), cancellationToken: cancellationToken);
        var canAssets = false;
        var canMeters = false;
        foreach (var parcel in overview.Parcels)
        {
            canAssets |= await access.CanAsync("assets.equipment.manage_household", ResourceScopeTypes.Property, parcel.Id.ToString("D"), resourceType: "Asset", cancellationToken: cancellationToken);
            canMeters |= await access.CanAsync("utilities.reading.approve", ResourceScopeTypes.Property, parcel.Id.ToString("D"), resourceType: "Meter", cancellationToken: cancellationToken);
        }
        ViewData["CanAssets"] = canAssets;
        ViewData["CanMeters"] = canMeters;
        return View(overview);
    }

    [HttpPost("Parcel"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Parcel(CreateParcelViewModel model, CancellationToken cancellationToken)
    {
        var actor = await GetActorAsync(cancellationToken); if (actor is null) return Forbid();
        await propertyService.CreateParcelAsync(actor, new CreateParcelRequest(model.Name, model.AddressText, model.RegistryNo, model.Area, model.OwnershipType, model.AcquiredOn), cancellationToken);
        TempData["Success"] = "Dodano działkę.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("Building"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Building(CreateBuildingViewModel model, CancellationToken cancellationToken)
    {
        var actor = await GetActorAsync(cancellationToken); if (actor is null) return Forbid();
        await propertyService.CreateBuildingAsync(actor, new CreateBuildingRequest(model.ParcelId, model.Name, model.BuildingType, model.UsableArea, model.Floors, model.BuildYear, model.FunctionType), cancellationToken);
        TempData["Success"] = "Dodano budynek.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("Room"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Room(CreateRoomViewModel model, CancellationToken cancellationToken)
    {
        var actor = await GetActorAsync(cancellationToken); if (actor is null) return Forbid();
        await propertyService.CreateRoomAsync(actor, new CreateRoomRequest(model.BuildingId, model.Name, model.RoomType, model.Area, model.FloorNo, model.IsRentable, model.IsCommonArea, model.Capacity), cancellationToken);
        TempData["Success"] = "Dodano pomieszczenie.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("Status"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Status(ChangePropertyStatusViewModel model, CancellationToken cancellationToken)
    {
        var actor = await GetActorAsync(cancellationToken); if (actor is null) return Forbid();
        switch (model.ObjectType)
        {
            case "Parcel": await propertyService.ChangeParcelStatusAsync(actor, model.Id, model.NewStatus, model.Reason, cancellationToken); break;
            case "Building": await propertyService.ChangeBuildingStatusAsync(actor, model.Id, model.NewStatus, model.Reason, cancellationToken); break;
            case "Room": await propertyService.ChangeRoomStatusAsync(actor, model.Id, model.NewStatus, model.Reason, cancellationToken); break;
            default: return BadRequest();
        }
        TempData["Success"] = "Zmieniono status i zachowano wpis historii.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("Asset"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Asset(CreateAssetViewModel input, CancellationToken cancellationToken)
    {
        var actor = await GetActorAsync(cancellationToken); if (actor is null) return Forbid();
        await propertyService.CreateAssetAsync(actor, new CreateAssetRequest(input.Name, input.CategoryCode, input.OwnershipType, input.Manufacturer, input.Model, input.SerialNo, InitialRoomId: input.RoomId, AssignedFrom: DateOnly.FromDateTime(DateTime.Today)), cancellationToken);
        TempData["Success"] = "Dodano element wyposażenia.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("AssetAssignment"), ValidateAntiForgeryToken]
    public async Task<IActionResult> AssetAssignment(AssignAssetViewModel model, CancellationToken cancellationToken)
    {
        var actor = await GetActorAsync(cancellationToken); if (actor is null) return Forbid();
        await propertyService.AssignAssetAsync(actor, new AssignAssetRequest(model.AssetId, AssetAssignmentTargets.Room, model.RoomId, model.ValidFrom, model.ConditionAtStart), cancellationToken);
        TempData["Success"] = "Zapisano nowe przypisanie wyposażenia bez usuwania poprzedniej historii.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("AssetWithdraw"), ValidateAntiForgeryToken]
    public async Task<IActionResult> AssetWithdraw(WithdrawAssetViewModel model, CancellationToken cancellationToken)
    {
        var actor = await GetActorAsync(cancellationToken); if (actor is null) return Forbid();
        await propertyService.WithdrawAssetAsync(actor, model.AssetId, model.EndedOn, model.ConditionAtEnd, cancellationToken);
        TempData["Success"] = "Wycofano wyposażenie; historia przypisań pozostała zachowana.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("Meter"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Meter(CreateMeterViewModel model, CancellationToken cancellationToken)
    {
        var actor = await GetActorAsync(cancellationToken); if (actor is null) return Forbid();
        await propertyService.CreateMeterAsync(actor, new CreateMeterRequest(model.Name, model.Medium, model.MeterType, model.UnitCode, model.LocationType, model.LocationId, model.ParentMeterId, model.SerialNo, model.InstalledOn), cancellationToken);
        TempData["Success"] = model.MeterType == MeterTypes.Sub ? "Dodano podlicznik." : "Dodano licznik główny.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("UpdateRecord"), ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateRecord(UpdatePropertyRecordViewModel model, CancellationToken cancellationToken)
    {
        var actor = await GetActorAsync(cancellationToken); if (actor is null) return Forbid();
        var permission = model.RecordType is "Asset" ? "assets.equipment.manage_household" : model.RecordType is "Meter" ? "utilities.reading.approve" : "property.structure.manage";
        if (!await access.CanAsync(permission, ResourceScopeTypes.Household, actor.HouseholdId.ToString("D"), resourceType: model.RecordType, resourceId: model.RecordId.ToString("D"), cancellationToken: cancellationToken)) return Forbid();
        try
        {
            await adminCrud.UpdatePropertyRecordAsync(new UpdatePropertyRecordRequest(actor.HouseholdId, model.RecordType, model.RecordId, model.Name, model.SecondaryText, model.Area, model.NumberValue, actor.AccountId, actor.CorrelationId), cancellationToken);
            TempData["Success"] = "Zapisano zmiany obiektu.";
        }
        catch (Exception ex) { TempData["Error"] = ex.Message; }
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("ArchiveRecord"), ValidateAntiForgeryToken]
    public async Task<IActionResult> ArchiveRecord(string recordType, Guid recordId, string reason, CancellationToken cancellationToken)
    {
        var actor = await GetActorAsync(cancellationToken); if (actor is null) return Forbid();
        var permission = recordType is "Asset" ? "assets.equipment.manage_household" : recordType is "Meter" ? "utilities.reading.approve" : "property.structure.manage";
        if (!await access.CanAsync(permission, ResourceScopeTypes.Household, actor.HouseholdId.ToString("D"), resourceType: recordType, resourceId: recordId.ToString("D"), cancellationToken: cancellationToken)) return Forbid();
        try
        {
            await adminCrud.ArchivePropertyRecordAsync(actor.HouseholdId, recordType, recordId, actor.AccountId, actor.CorrelationId, string.IsNullOrWhiteSpace(reason) ? "Archiwizacja obiektu" : reason, cancellationToken);
            TempData["Success"] = "Obiekt został zarchiwizowany, a jego historia pozostała zachowana.";
        }
        catch (Exception ex) { TempData["Error"] = ex.Message; }
        return RedirectToAction(nameof(Index));
    }

    private async Task<PropertyActor?> GetActorAsync(CancellationToken cancellationToken)
    {
        var current = await access.GetCurrentAsync(cancellationToken);
        return current is null ? null : new PropertyActor(current.UserAccountId, current.PersonId, current.HouseholdId, CorrelationIdMiddleware.Get(HttpContext), DateTime.UtcNow);
    }
}
