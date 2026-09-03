using EDom.Application.Collaboration;
using EDom.Application.Rental;
using EDom.Application.Utilities;
using EDom.Domain.Rental;
using EDom.Domain.Utilities;
using EDom.Infrastructure.Persistence;
using EDom.Web.Authorization;
using EDom.Web.Infrastructure;
using EDom.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EDom.Web.Controllers;

[Authorize]
[Route("Rental")]
public sealed class RentalController(
    WebAccessService access,
    IRentalService rentalService,
    ILeaseClosingService leaseClosingService,
    ICollaborationService collaborationService,
    IUtilitiesService utilitiesService,
    EDomDbContext db) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(
        CancellationToken cancellationToken)
    {
        var actor = await GetActorAsync(cancellationToken);

        if (actor is null)
        {
            return Forbid();
        }

        var model = await rentalService.GetOverviewAsync(
            actor,
            cancellationToken);

        if (!model.CanManage
            && !model.IsTenant
            && model.Contracts.Count == 0)
        {
            return Forbid();
        }

        IReadOnlyList<RentalSigningMeterOptionViewModel> signingMeters =
            Array.Empty<RentalSigningMeterOptionViewModel>();

        if (model.CanManage)
        {
            try
            {
                signingMeters =
                    await GetSigningMeterOptionsAsync(
                        actor,
                        cancellationToken);
            }
            catch
            {
                // Brak modułu Media nie może blokować podglądu umów.
                signingMeters =
                    Array.Empty<RentalSigningMeterOptionViewModel>();
            }
        }

        ViewData["SigningMeters"] =
            signingMeters;

        return View(model);
    }

    [HttpPost("Template"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Template(
        string name,
        string leaseType,
        string bodyTemplate,
        DateOnly effectiveFrom,
        CancellationToken cancellationToken)
        => await ExecuteAsync(
            async actor =>
            {
                await rentalService.CreateTemplateAsync(
                    actor,
                    new(
                        name,
                        leaseType,
                        bodyTemplate,
                        effectiveFrom),
                    cancellationToken);

                return "Dodano szablon umowy.";
            },
            cancellationToken);

    [HttpPost("Create"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(
        Guid roomId,
        Guid? templateId,
        string firstName,
        string lastName,
        DateOnly? birthDate,
        string? email,
        string? phone,
        string login,
        string temporaryPassword,
        bool mustChangePassword,
        Guid? landlordPersonId,
        DateOnly leaseFrom,
        DateOnly? leaseTo,
        decimal rentAmount,
        string currencyCode,
        int dueDay,
        decimal advanceAmount,
        decimal depositAmount,
        string? utilitiesRulesText,
        CancellationToken cancellationToken)
        => await ExecuteAsync(
            async actor =>
            {
                await rentalService.CreateLeaseDraftAsync(
                    actor,
                    new CreateLeaseDraftRequest(
                        roomId,
                        templateId,
                        null,
                        firstName,
                        lastName,
                        birthDate,
                        email,
                        phone,
                        login,
                        temporaryPassword,
                        mustChangePassword,
                        landlordPersonId,
                        leaseFrom,
                        leaseTo,
                        ToMinor(rentAmount),
                        currencyCode,
                        dueDay,
                        ToMinor(advanceAmount),
                        ToMinor(depositAmount),
                        utilitiesRulesText),
                    cancellationToken);

                return "Przygotowano konto lokatora, umowę i dokument PDF. Umowa czeka na potwierdzenie podpisania.";
            },
            cancellationToken);

    [HttpPost("Activate"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Activate(
        Guid contractId,
        DateOnly signedOn,
        string signatureMethod,
        string? comment,
        bool addInitialMeterReading,
        Guid? initialMeterId,
        decimal? initialMeterValue,
        string? initialMeterZoneCode,
        CancellationToken cancellationToken)
    {
        var actor = await GetActorAsync(
            cancellationToken);

        if (actor is null)
        {
            return Forbid();
        }

        try
        {
            var overview =
                await rentalService.GetOverviewAsync(
                    actor,
                    cancellationToken);

            var contract =
                overview.Contracts.FirstOrDefault(
                    x => x.ContractId == contractId);

            if (contract is null)
            {
                TempData["Error"] =
                    "Nie znaleziono umowy do aktywacji.";

                return RedirectToAction(nameof(Index));
            }

            await rentalService.ActivateLeaseAsync(
                actor,
                new(
                    contractId,
                    signedOn,
                    signatureMethod,
                    comment),
                cancellationToken);

            if (!addInitialMeterReading)
            {
                TempData["Success"] =
                    "Umowa została podpisana i aktywowana; pokój jest wynajęty.";

                return RedirectToAction(nameof(Index));
            }

            if (!initialMeterId.HasValue
                || initialMeterId.Value == Guid.Empty
                || !initialMeterValue.HasValue)
            {
                TempData["Error"] =
                    "Umowa została aktywowana, ale nie zapisano odczytu początkowego: wybierz podlicznik i wpisz stan.";

                return RedirectToAction(nameof(Index));
            }

            if (initialMeterValue.Value < 0m)
            {
                TempData["Error"] =
                    "Umowa została aktywowana, ale odczyt początkowy nie może być ujemny.";

                return RedirectToAction(nameof(Index));
            }

            var signingMeters =
                await GetSigningMeterOptionsAsync(
                    actor,
                    cancellationToken);

            var selectedMeter =
                signingMeters.FirstOrDefault(x =>
                    x.MeterId == initialMeterId.Value
                    && string.Equals(
                        x.RoomName,
                        contract.RoomName,
                        StringComparison.OrdinalIgnoreCase));

            if (selectedMeter is null)
            {
                TempData["Error"] =
                    "Umowa została aktywowana, ale wybrany podlicznik nie jest przypisany do pokoju tej umowy.";

                return RedirectToAction(nameof(Index));
            }

            try
            {
                await SubmitAndApproveInitialReadingAsync(
                    actor,
                    selectedMeter.MeterId,
                    signedOn,
                    initialMeterValue.Value,
                    string.IsNullOrWhiteSpace(initialMeterZoneCode)
                        ? "ALL"
                        : initialMeterZoneCode.Trim(),
                    cancellationToken);

                TempData["Success"] =
                    $"Umowa została podpisana i aktywowana. Zapisano również zatwierdzony odczyt początkowy podlicznika „{selectedMeter.MeterName}”: {initialMeterValue.Value:N3} {selectedMeter.UnitCode}.";
            }
            catch (Exception ex)
            {
                TempData["Error"] =
                    $"Umowa została aktywowana, ale nie udało się zapisać odczytu początkowego podlicznika: {ex.Message}";
            }
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
        catch (Exception ex)
        {
            TempData["Error"] =
                ex.Message;
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpPost("Amend"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Amend(
        Guid contractId,
        DateOnly effectiveOn,
        decimal? newRentAmount,
        DateOnly? newLeaseTo,
        string? reason,
        CancellationToken cancellationToken)
        => await ExecuteAsync(
            async actor =>
            {
                await rentalService.CreateAmendmentAsync(
                    actor,
                    new(
                        contractId,
                        effectiveOn,
                        newRentAmount.HasValue
                            ? ToMinor(newRentAmount.Value)
                            : null,
                        newLeaseTo,
                        reason),
                    cancellationToken);

                return "Utworzono aneks bez nadpisywania pierwotnych warunków.";
            },
            cancellationToken);

    [HttpPost("End"), ValidateAntiForgeryToken]
    public async Task<IActionResult> End(
        Guid contractId,
        DateOnly endedOn,
        string reason,
        CancellationToken cancellationToken)
        => await ExecuteAsync(
            async actor =>
            {
                await leaseClosingService.StartAsync(
                    actor,
                    new StartLeaseClosingRequest(
                        contractId,
                        endedOn,
                        endedOn,
                        reason),
                    cancellationToken);

                return "Rozpoczęto proces zamknięcia najmu i wygaszono aktywne przypisanie lokatora. Pokój zostanie zwolniony dopiero po odbiorze i rozliczeniu końcowym.";
            },
            cancellationToken);

    [HttpPost("Deposit"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Deposit(
        Guid contractId,
        decimal amount,
        DateOnly paidOn,
        CancellationToken cancellationToken)
        => await ExecuteAsync(
            async actor =>
            {
                await rentalService.RecordDepositPaymentAsync(
                    actor,
                    new(
                        contractId,
                        ToMinor(amount),
                        paidOn),
                    cancellationToken);

                return "Zarejestrowano wpłatę kaucji.";
            },
            cancellationToken);

    [HttpPost("Protocol"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Protocol(
        Guid contractId,
        string protocolType,
        DateOnly protocolDate,
        string? notes,
        CancellationToken cancellationToken)
        => await ExecuteAsync(
            async actor =>
            {
                await rentalService.CreateProtocolAsync(
                    actor,
                    new(
                        contractId,
                        protocolType,
                        protocolDate,
                        notes),
                    cancellationToken);

                return "Utworzono protokół wraz z dokumentem PDF.";
            },
            cancellationToken);

    [HttpGet("Document/{documentId:guid}")]
    public async Task<IActionResult> Document(
        Guid documentId,
        CancellationToken cancellationToken)
    {
        var current =
            await access.GetCurrentAsync(
                cancellationToken);

        if (current is null)
        {
            return Forbid();
        }

        var download =
            await collaborationService.DownloadDocumentAsync(
                new CollaborationActor(
                    current.UserAccountId,
                    current.PersonId,
                    current.HouseholdId,
                    CorrelationIdMiddleware.Get(HttpContext),
                    DateTime.UtcNow),
                documentId,
                cancellationToken);

        return download is null
            ? NotFound()
            : File(
                download.Content,
                download.ContentType,
                download.FileName);
    }

    private async Task SubmitAndApproveInitialReadingAsync(
        RentalActor rentalActor,
        Guid meterId,
        DateOnly signedOn,
        decimal value,
        string zoneCode,
        CancellationToken cancellationToken)
    {
        var utilityActor =
            new UtilityActor(
                rentalActor.AccountId,
                rentalActor.PersonId,
                rentalActor.HouseholdId,
                rentalActor.CorrelationId,
                rentalActor.NowUtc);

        var before =
            await utilitiesService.GetOverviewAsync(
                utilityActor,
                cancellationToken);

        var beforeIds =
            before.Readings
                .Where(x => x.MeterId == meterId)
                .Select(x => x.Id)
                .ToHashSet();

        var localAt =
            DateTime.SpecifyKind(
                signedOn.ToDateTime(
                    new TimeOnly(12, 0)),
                DateTimeKind.Local);

        await utilitiesService.SubmitReadingAsync(
            utilityActor,
            new(
                meterId,
                localAt.ToUniversalTime(),
                "LeaseSigning",
                [
                    new(
                        zoneCode,
                        value)
                ],
                null),
            cancellationToken);

        var after =
            await utilitiesService.GetOverviewAsync(
                utilityActor,
                cancellationToken);

        var created =
            after.Readings
                .Where(x =>
                    x.MeterId == meterId
                    && !beforeIds.Contains(x.Id))
                .OrderByDescending(x => x.ReadingAtUtc)
                .FirstOrDefault();

        if (created is null)
        {
            throw new InvalidOperationException(
                "Nie udało się odnaleźć zapisanego odczytu początkowego.");
        }

        if (created.Status == ReadingStatuses.Submitted)
        {
            await utilitiesService.ApproveReadingAsync(
                utilityActor,
                created.Id,
                "Odczyt początkowy przy podpisaniu umowy najmu.",
                cancellationToken);
        }
    }

    private async Task<IReadOnlyList<RentalSigningMeterOptionViewModel>> GetSigningMeterOptionsAsync(
        RentalActor rentalActor,
        CancellationToken cancellationToken)
    {
        var utilityActor =
            new UtilityActor(
                rentalActor.AccountId,
                rentalActor.PersonId,
                rentalActor.HouseholdId,
                rentalActor.CorrelationId,
                rentalActor.NowUtc);

        var overview =
            await utilitiesService.GetOverviewAsync(
                utilityActor,
                cancellationToken);

        var meterRoomIds =
            overview.Meters
                .Where(x =>
                    string.Equals(
                        GetString(x, "MeterType"),
                        "Sub",
                        StringComparison.OrdinalIgnoreCase)
                    && string.Equals(
                        GetString(x, "LocationType"),
                        "Room",
                        StringComparison.OrdinalIgnoreCase))
                .Select(x =>
                    GetGuid(
                        x,
                        "LocationId"))
                .Where(x => x != Guid.Empty)
                .Distinct()
                .ToArray();

        if (meterRoomIds.Length == 0)
        {
            return Array.Empty<RentalSigningMeterOptionViewModel>();
        }

        var rooms =
            await db.Rooms
                .AsNoTracking()
                .Where(x =>
                    meterRoomIds.Contains(x.Id))
                .Select(x => new
                {
                    x.Id,
                    x.Name
                })
                .ToDictionaryAsync(
                    x => x.Id,
                    cancellationToken);

        var result =
            new List<RentalSigningMeterOptionViewModel>();

        foreach (var meter in overview.Meters)
        {
            if (!string.Equals(
                    GetString(meter, "MeterType"),
                    "Sub",
                    StringComparison.OrdinalIgnoreCase)
                || !string.Equals(
                    GetString(meter, "LocationType"),
                    "Room",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var roomId =
                GetGuid(
                    meter,
                    "LocationId");

            if (roomId == Guid.Empty
                || !rooms.TryGetValue(
                    roomId,
                    out var room))
            {
                continue;
            }

            result.Add(
                new RentalSigningMeterOptionViewModel
                {
                    MeterId =
                        GetGuid(
                            meter,
                            "Id"),
                    RoomId =
                        roomId,
                    RoomName =
                        room.Name,
                    MeterName =
                        GetString(
                            meter,
                            "Name",
                            "Podlicznik"),
                    Medium =
                        GetString(
                            meter,
                            "Medium"),
                    UnitCode =
                        GetString(
                            meter,
                            "UnitCode")
                });
        }

        return result
            .Where(x => x.MeterId != Guid.Empty)
            .OrderBy(x => x.RoomName)
            .ThenBy(x => x.MeterName)
            .ToArray();
    }

    private async Task<IActionResult> ExecuteAsync(
        Func<RentalActor, Task<string>> operation,
        CancellationToken cancellationToken)
    {
        var actor =
            await GetActorAsync(
                cancellationToken);

        if (actor is null)
        {
            return Forbid();
        }

        try
        {
            TempData["Success"] =
                await operation(actor);
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
        catch (Exception ex)
        {
            TempData["Error"] =
                ex.Message;
        }

        return RedirectToAction(nameof(Index));
    }

    private async Task<RentalActor?> GetActorAsync(
        CancellationToken cancellationToken)
    {
        var current =
            await access.GetCurrentAsync(
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

    private static object? GetValue(
        object? source,
        params string[] names)
    {
        if (source is null)
        {
            return null;
        }

        foreach (var name in names)
        {
            var property =
                source.GetType()
                    .GetProperty(
                        name,
                        System.Reflection.BindingFlags.Public
                        | System.Reflection.BindingFlags.Instance
                        | System.Reflection.BindingFlags.IgnoreCase);

            if (property is not null)
            {
                return property.GetValue(source);
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

    private static long ToMinor(
        decimal amount) =>
        checked(
            (long)Math.Round(
                amount * 100m,
                0,
                MidpointRounding.AwayFromZero));
}
