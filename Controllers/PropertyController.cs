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
public sealed class PropertyController(
    WebAccessService access,
    IPropertyAssetService propertyService,
    IAdministrationCrudService adminCrud) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var actor = await GetActorAsync(cancellationToken);
        if (actor is null) return Forbid();

        var overview = await propertyService.GetOverviewAsync(actor, cancellationToken);
        if (overview.Parcels.Count == 0
            && !await access.CanAsync(
                "property.structure.manage",
                ResourceScopeTypes.Household,
                actor.HouseholdId.ToString("D"),
                cancellationToken: cancellationToken))
        {
            return Forbid();
        }

        ViewData["CanCreateParcel"] = await access.CanAsync(
            "property.structure.manage",
            ResourceScopeTypes.Household,
            actor.HouseholdId.ToString("D"),
            cancellationToken: cancellationToken);

        var canAssets = false;
        var canMeters = false;

        foreach (var parcel in overview.Parcels)
        {
            canAssets |= await access.CanAsync(
                "assets.equipment.manage_household",
                ResourceScopeTypes.Property,
                parcel.Id.ToString("D"),
                resourceType: "Asset",
                cancellationToken: cancellationToken);

            canMeters |= await access.CanAsync(
                "utilities.reading.approve",
                ResourceScopeTypes.Property,
                parcel.Id.ToString("D"),
                resourceType: "Meter",
                cancellationToken: cancellationToken);
        }

        ViewData["CanAssets"] = canAssets;
        ViewData["CanMeters"] = canMeters;

        return View(overview);
    }

    [HttpPost("Parcel"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Parcel(
        CreateParcelViewModel model,
        CancellationToken cancellationToken)
    {
        var actor = await GetActorAsync(cancellationToken);
        if (actor is null) return Forbid();

        await propertyService.CreateParcelAsync(
            actor,
            new CreateParcelRequest(
                model.Name,
                model.AddressText,
                model.RegistryNo,
                model.Area,
                model.OwnershipType,
                model.AcquiredOn),
            cancellationToken);

        TempData["Success"] = "Dodano działkę.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("Building"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Building(
        CreateBuildingViewModel model,
        CancellationToken cancellationToken)
    {
        var actor = await GetActorAsync(cancellationToken);
        if (actor is null) return Forbid();

        await propertyService.CreateBuildingAsync(
            actor,
            new CreateBuildingRequest(
                model.ParcelId,
                model.Name,
                model.BuildingType,
                model.UsableArea,
                model.Floors,
                model.BuildYear,
                model.FunctionType),
            cancellationToken);

        TempData["Success"] = "Dodano budynek.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("Room"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Room(
        CreateRoomViewModel model,
        CancellationToken cancellationToken)
    {
        var actor = await GetActorAsync(cancellationToken);
        if (actor is null) return Forbid();

        await propertyService.CreateRoomAsync(
            actor,
            new CreateRoomRequest(
                model.BuildingId,
                model.Name,
                model.RoomType,
                model.Area,
                model.FloorNo,
                model.IsRentable,
                model.IsCommonArea,
                model.Capacity),
            cancellationToken);

        TempData["Success"] = "Dodano pomieszczenie.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("Status"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Status(
        ChangePropertyStatusViewModel model,
        CancellationToken cancellationToken)
    {
        var actor = await GetActorAsync(cancellationToken);
        if (actor is null) return Forbid();

        switch (model.ObjectType)
        {
            case "Parcel":
                await propertyService.ChangeParcelStatusAsync(
                    actor, model.Id, model.NewStatus, model.Reason, cancellationToken);
                break;
            case "Building":
                await propertyService.ChangeBuildingStatusAsync(
                    actor, model.Id, model.NewStatus, model.Reason, cancellationToken);
                break;
            case "Room":
                await propertyService.ChangeRoomStatusAsync(
                    actor, model.Id, model.NewStatus, model.Reason, cancellationToken);
                break;
            default:
                return BadRequest();
        }

        TempData["Success"] = "Zmieniono status i zachowano wpis historii.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("Asset"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Asset(
        CreateAssetViewModel input,
        CancellationToken cancellationToken)
    {
        var actor = await GetActorAsync(cancellationToken);
        if (actor is null) return Forbid();

        await propertyService.CreateAssetAsync(
            actor,
            new CreateAssetRequest(
                input.Name,
                input.CategoryCode,
                input.OwnershipType,
                input.Manufacturer,
                input.Model,
                input.SerialNo,
                InitialRoomId: input.RoomId,
                AssignedFrom: DateOnly.FromDateTime(DateTime.Today)),
            cancellationToken);

        TempData["Success"] = "Dodano element wyposażenia.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("AssetAssignment"), ValidateAntiForgeryToken]
    public async Task<IActionResult> AssetAssignment(
        AssignAssetViewModel model,
        CancellationToken cancellationToken)
    {
        var actor = await GetActorAsync(cancellationToken);
        if (actor is null) return Forbid();

        await propertyService.AssignAssetAsync(
            actor,
            new AssignAssetRequest(
                model.AssetId,
                AssetAssignmentTargets.Room,
                model.RoomId,
                model.ValidFrom,
                model.ConditionAtStart),
            cancellationToken);

        TempData["Success"] =
            "Zapisano nowe przypisanie wyposażenia bez usuwania poprzedniej historii.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("AssetWithdraw"), ValidateAntiForgeryToken]
    public async Task<IActionResult> AssetWithdraw(
        WithdrawAssetViewModel model,
        CancellationToken cancellationToken)
    {
        var actor = await GetActorAsync(cancellationToken);
        if (actor is null) return Forbid();

        await propertyService.WithdrawAssetAsync(
            actor,
            model.AssetId,
            model.EndedOn,
            model.ConditionAtEnd,
            cancellationToken);

        TempData["Success"] =
            "Wycofano wyposażenie; historia przypisań pozostała zachowana.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("Meter"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Meter(
        CreateMeterViewModel model,
        CancellationToken cancellationToken)
    {
        var actor = await GetActorAsync(cancellationToken);
        if (actor is null) return Forbid();

        try
        {
            var overview = await propertyService.GetOverviewAsync(actor, cancellationToken);
            var validation = ValidateMeterPlacement(
                overview,
                model.MeterType,
                model.Medium,
                model.LocationType,
                model.LocationId,
                model.ParentMeterId,
                currentMeterId: null);

            if (validation.Error is not null)
            {
                TempData["Error"] = validation.Error;
                return Redirect("/Property#property-meters");
            }

            await propertyService.CreateMeterAsync(
                actor,
                new CreateMeterRequest(
                    model.Name,
                    model.Medium,
                    model.MeterType,
                    model.UnitCode,
                    model.LocationType,
                    model.LocationId,
                    validation.ParentMeterId,
                    model.SerialNo,
                    model.InstalledOn),
                cancellationToken);

            TempData["Success"] = model.MeterType == MeterTypes.Sub
                ? "Dodano podlicznik do rozliczeń lokatora."
                : "Dodano licznik główny.";
        }
        catch (Exception ex)
        {
            TempData["Error"] = ex.Message;
        }

        return Redirect("/Property#property-meters");
    }

    [HttpPost("UpdateRecord"), ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateRecord(
        UpdatePropertyRecordViewModel model,
        CancellationToken cancellationToken)
    {
        var actor = await GetActorAsync(cancellationToken);
        if (actor is null) return Forbid();

        var permission = model.RecordType is "Asset"
            ? "assets.equipment.manage_household"
            : model.RecordType is "Meter"
                ? "utilities.reading.approve"
                : "property.structure.manage";

        if (!await access.CanAsync(
                permission,
                ResourceScopeTypes.Household,
                actor.HouseholdId.ToString("D"),
                resourceType: model.RecordType,
                resourceId: model.RecordId.ToString("D"),
                cancellationToken: cancellationToken))
        {
            return Forbid();
        }

        try
        {
            await adminCrud.UpdatePropertyRecordAsync(
                new UpdatePropertyRecordRequest(
                    actor.HouseholdId,
                    model.RecordType,
                    model.RecordId,
                    model.Name,
                    model.SecondaryText,
                    model.Area,
                    model.NumberValue,
                    actor.AccountId,
                    actor.CorrelationId),
                cancellationToken);

            TempData["Success"] = "Zapisano zmiany obiektu.";
        }
        catch (Exception ex)
        {
            TempData["Error"] = ex.Message;
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpPost("ArchiveRecord"), ValidateAntiForgeryToken]
    public async Task<IActionResult> ArchiveRecord(
        string recordType,
        Guid recordId,
        string reason,
        CancellationToken cancellationToken)
    {
        var actor = await GetActorAsync(cancellationToken);
        if (actor is null) return Forbid();

        var permission = recordType is "Asset"
            ? "assets.equipment.manage_household"
            : recordType is "Meter"
                ? "utilities.reading.approve"
                : "property.structure.manage";

        if (!await access.CanAsync(
                permission,
                ResourceScopeTypes.Household,
                actor.HouseholdId.ToString("D"),
                resourceType: recordType,
                resourceId: recordId.ToString("D"),
                cancellationToken: cancellationToken))
        {
            return Forbid();
        }

        try
        {
            await adminCrud.ArchivePropertyRecordAsync(
                actor.HouseholdId,
                recordType,
                recordId,
                actor.AccountId,
                actor.CorrelationId,
                string.IsNullOrWhiteSpace(reason)
                    ? "Archiwizacja obiektu"
                    : reason,
                cancellationToken);

            TempData["Success"] =
                "Obiekt został zarchiwizowany, a jego historia pozostała zachowana.";
        }
        catch (Exception ex)
        {
            TempData["Error"] = ex.Message;
        }

        return RedirectToAction(nameof(Index));
    }

    internal static MeterPlacementValidationResult ValidateMeterPlacement(
        PropertyOverview overview,
        string meterType,
        string medium,
        string locationType,
        Guid? locationId,
        Guid? parentMeterId,
        Guid? currentMeterId)
    {
        var isSub = string.Equals(
            meterType,
            MeterTypes.Sub,
            StringComparison.OrdinalIgnoreCase);

        if (!locationId.HasValue || locationId.GetValueOrDefault() == Guid.Empty)
        {
            return new(
                "Wybierz lokalizację licznika.",
                null);
        }

        var resolvedLocationId = locationId.GetValueOrDefault();

        if (!isSub)
        {
            if (!string.Equals(locationType, "Parcel", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(locationType, "Building", StringComparison.OrdinalIgnoreCase))
            {
                return new(
                    "Licznik główny można przypisać do całej działki albo konkretnego domu/budynku.",
                    null);
            }

            var locationExists =
                (string.Equals(locationType, "Parcel", StringComparison.OrdinalIgnoreCase)
                 && overview.Parcels.Any(x => x.Id == resolvedLocationId))
                || (string.Equals(locationType, "Building", StringComparison.OrdinalIgnoreCase)
                    && overview.Buildings.Any(x => x.Id == resolvedLocationId));

            if (!locationExists)
            {
                return new("Wybrana lokalizacja licznika głównego nie istnieje.", null);
            }

            return new(null, null);
        }

        if (!string.Equals(locationType, "Room", StringComparison.OrdinalIgnoreCase))
        {
            return new(
                "Podlicznik służy wyłącznie do rozliczeń lokatorów i musi być przypisany do konkretnego pokoju/lokalu do wynajmu.",
                null);
        }

        var room = overview.Rooms.FirstOrDefault(x => x.Id == resolvedLocationId);
        if (room is null)
        {
            return new("Nie znaleziono wybranego pomieszczenia.", null);
        }

        if (!room.IsRentable)
        {
            return new(
                "Podlicznik można przypisać wyłącznie do pomieszczenia oznaczonego „Do wynajmu”.",
                null);
        }

        if (!parentMeterId.HasValue)
        {
            return new(
                "Podlicznik musi wskazywać licznik główny tego samego medium.",
                null);
        }

        var resolvedParentMeterId = parentMeterId.GetValueOrDefault();
        if (resolvedParentMeterId == Guid.Empty)
        {
            return new(
                "Podlicznik musi wskazywać licznik główny tego samego medium.",
                null);
        }

        var resolvedCurrentMeterId = currentMeterId.GetValueOrDefault();
        if (resolvedCurrentMeterId != Guid.Empty
            && resolvedParentMeterId == resolvedCurrentMeterId)
        {
            return new("Licznik nie może być własnym licznikiem nadrzędnym.", null);
        }

        var parent = overview.Meters.FirstOrDefault(
            x => x.Id == resolvedParentMeterId);
        if (parent is null)
        {
            return new("Nie znaleziono wybranego licznika głównego.", null);
        }

        if (!string.Equals(parent.MeterType, MeterTypes.Main, StringComparison.OrdinalIgnoreCase))
        {
            return new("Licznikiem nadrzędnym może być wyłącznie licznik główny.", null);
        }

        if (!string.Equals(parent.Status, MeterStatuses.Active, StringComparison.OrdinalIgnoreCase))
        {
            return new("Licznik główny musi być aktywny.", null);
        }

        if (!string.Equals(parent.Medium, medium, StringComparison.OrdinalIgnoreCase))
        {
            return new("Licznik główny i podlicznik muszą mieć to samo medium.", null);
        }

        var building = overview.Buildings.FirstOrDefault(x => x.Id == room.BuildingId);
        if (building is null)
        {
            return new("Nie znaleziono budynku dla wybranego pokoju.", null);
        }

        var sameBranch =
            (string.Equals(parent.LocationType, "Parcel", StringComparison.OrdinalIgnoreCase)
             && parent.LocationId == building.ParcelId)
            || (string.Equals(parent.LocationType, "Building", StringComparison.OrdinalIgnoreCase)
                && parent.LocationId == building.Id);

        if (!sameBranch)
        {
            return new(
                "Wybrany licznik główny nie obejmuje domu, w którym znajduje się ten pokój. Wybierz licznik główny tej działki albo tego budynku.",
                null);
        }

        return new(null, parentMeterId);
    }

    private async Task<PropertyActor?> GetActorAsync(
        CancellationToken cancellationToken)
    {
        var current = await access.GetCurrentAsync(cancellationToken);

        return current is null
            ? null
            : new PropertyActor(
                current.UserAccountId,
                current.PersonId,
                current.HouseholdId,
                CorrelationIdMiddleware.Get(HttpContext),
                DateTime.UtcNow);
    }

    internal sealed record MeterPlacementValidationResult(
        string? Error,
        Guid? ParentMeterId);
}
