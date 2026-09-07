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

            var signingMeters =
                await GetSigningMeterOptionsAsync(
                    actor,
                    cancellationToken);

            var roomMeters = signingMeters
                .Where(x => string.Equals(
                    x.RoomName,
                    contract.RoomName,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();

            RentalSigningMeterOptionViewModel? selectedMeter = null;
            var zoneCode = string.IsNullOrWhiteSpace(initialMeterZoneCode)
                ? "ALL"
                : initialMeterZoneCode.Trim();

            if (roomMeters.Length > 0)
            {
                if (!initialMeterId.HasValue
                    || initialMeterId.Value == Guid.Empty
                    || !initialMeterValue.HasValue)
                {
                    TempData["Error"] =
                        $"Nie można aktywować umowy dla pokoju „{contract.RoomName}” bez odczytu początkowego podlicznika. Wybierz podlicznik i wpisz stan na dzień rozpoczęcia najmu {contract.LeaseFrom:dd.MM.yyyy}.";

                    return RedirectToAction(nameof(Index));
                }

                if (initialMeterValue.Value < 0m)
                {
                    TempData["Error"] =
                        "Odczyt początkowy podlicznika nie może być ujemny.";

                    return RedirectToAction(nameof(Index));
                }

                selectedMeter = roomMeters.FirstOrDefault(x =>
                    x.MeterId == initialMeterId.Value);

                if (selectedMeter is null)
                {
                    TempData["Error"] =
                        "Wybrany podlicznik nie jest przypisany do pokoju tej umowy.";

                    return RedirectToAction(nameof(Index));
                }

                await EnsureInitialReadingAsync(
                    actor,
                    selectedMeter,
                    contract.LeaseFrom,
                    initialMeterValue.Value,
                    zoneCode,
                    cancellationToken);
            }

            await rentalService.ActivateLeaseAsync(
                actor,
                new(
                    contractId,
                    signedOn,
                    signatureMethod,
                    comment),
                cancellationToken);

            TempData["Success"] = selectedMeter is null
                ? "Umowa została podpisana i aktywowana; pokój jest wynajęty."
                : $"Umowa została podpisana i aktywowana. Odczyt początkowy podlicznika „{selectedMeter.MeterName}” zapisano na dzień rozpoczęcia najmu {contract.LeaseFrom:dd.MM.yyyy}: {initialMeterValue!.Value:N3} {selectedMeter.UnitCode}.";
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

    [HttpPost("InitialMeterReading"), ValidateAntiForgeryToken]
    public async Task<IActionResult> InitialMeterReading(
        Guid contractId,
        Guid initialMeterId,
        decimal initialMeterValue,
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
            if (initialMeterValue < 0m)
            {
                TempData["Error"] =
                    "Odczyt początkowy podlicznika nie może być ujemny.";

                return RedirectToAction(nameof(Index));
            }

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
                    "Nie znaleziono umowy najmu.";

                return RedirectToAction(nameof(Index));
            }

            if (contract.Status != LeaseStatuses.Signed)
            {
                TempData["Error"] =
                    "Odczyt początkowy można uzupełnić dla aktywnej, podpisanej umowy.";

                return RedirectToAction(nameof(Index));
            }

            var signingMeters =
                await GetSigningMeterOptionsAsync(
                    actor,
                    cancellationToken);

            var selectedMeter = signingMeters.FirstOrDefault(x =>
                x.MeterId == initialMeterId
                && string.Equals(
                    x.RoomName,
                    contract.RoomName,
                    StringComparison.OrdinalIgnoreCase));

            if (selectedMeter is null)
            {
                TempData["Error"] =
                    "Wybrany podlicznik nie jest przypisany do pokoju tej umowy.";

                return RedirectToAction(nameof(Index));
            }

            var zoneCode = string.IsNullOrWhiteSpace(initialMeterZoneCode)
                ? "ALL"
                : initialMeterZoneCode.Trim();

            var created = await EnsureInitialReadingAsync(
                actor,
                selectedMeter,
                contract.LeaseFrom,
                initialMeterValue,
                zoneCode,
                cancellationToken);

            TempData["Success"] = created
                ? $"Zapisano odczyt początkowy podlicznika „{selectedMeter.MeterName}” dla umowy {contract.TenantName} na dzień {contract.LeaseFrom:dd.MM.yyyy}: {initialMeterValue:N3} {selectedMeter.UnitCode}. Od tego stanu będzie liczone zużycie lokatora."
                : $"Na dzień rozpoczęcia najmu {contract.LeaseFrom:dd.MM.yyyy} istnieje już taki sam zatwierdzony odczyt podlicznika „{selectedMeter.MeterName}”. Nie utworzono duplikatu.";
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

    private async Task<bool> EnsureInitialReadingAsync(
        RentalActor rentalActor,
        RentalSigningMeterOptionViewModel meter,
        DateOnly readingDate,
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

        var overview =
            await utilitiesService.GetOverviewAsync(
                utilityActor,
                cancellationToken);

        var existingReading = overview.Readings
            .Where(x =>
                x.MeterId == meter.MeterId
                && x.Status == ReadingStatuses.Approved
                && DateOnly.FromDateTime(x.ReadingAtUtc.ToLocalTime()) == readingDate)
            .OrderByDescending(x => x.ReadingAtUtc)
            .FirstOrDefault();

        if (existingReading is not null)
        {
            var existingValue = overview.ReadingValues
                .FirstOrDefault(x =>
                    x.MeterReadingId == existingReading.Id
                    && string.Equals(
                        x.ZoneCode,
                        zoneCode,
                        StringComparison.OrdinalIgnoreCase));

            if (existingValue is null)
            {
                throw new InvalidOperationException(
                    $"Na dzień rozpoczęcia najmu {readingDate:dd.MM.yyyy} istnieje już zatwierdzony odczyt podlicznika „{meter.MeterName}”, ale dla innej strefy. Sprawdź odczyty licznika przed zapisaniem stanu początkowego.");
            }

            var existingDecimal =
                existingValue.ValueScaled
                / (decimal)Math.Pow(10, existingValue.Scale);

            if (existingDecimal != value)
            {
                throw new InvalidOperationException(
                    $"Na dzień rozpoczęcia najmu {readingDate:dd.MM.yyyy} podlicznik „{meter.MeterName}” ma już zatwierdzony stan {existingDecimal:N3} {meter.UnitCode}. Nie można zapisać drugiego, innego stanu na ten sam dzień.");
            }

            return false;
        }

        await SubmitAndApproveInitialReadingAsync(
            rentalActor,
            meter.MeterId,
            readingDate,
            value,
            zoneCode,
            cancellationToken);

        return true;
    }

    private async Task SubmitAndApproveInitialReadingAsync(
        RentalActor rentalActor,
        Guid meterId,
        DateOnly readingDate,
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
                readingDate.ToDateTime(
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
                "Odczyt początkowy na dzień rozpoczęcia umowy najmu.",
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
