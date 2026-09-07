using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using EDom.Application.Rental;
using EDom.Application.Utilities;
using EDom.Domain.Rental;
using EDom.Domain.Utilities;
using EDom.Infrastructure.Persistence;
using EDom.Web.Authorization;
using EDom.Web.Infrastructure;
using EDom.Web.Models;
using EDom.Web.Services;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EDom.Web.Controllers;

[Authorize]
[Route("Utilities/SubmeterTenant")]
public sealed class SubmeterTenantSettlementController(
    WebAccessService access,
    IUtilitiesService utilitiesService,
    IRentalService rentalService,
    ITenantSettlementService settlementService,
    EDomDbContext db,
    IAntiforgery antiforgery,
    IWebHostEnvironment environment) : Controller
{
    private const string PackageVersion = "PKG-015q-FEAT-04-FIX-04";

    [HttpGet("Data")]
    public async Task<IActionResult> Data(
        CancellationToken cancellationToken)
    {
        var current = await access.GetCurrentAsync(cancellationToken);
        if (current is null)
        {
            return Unauthorized();
        }

        var utilityActor = new UtilityActor(
            current.UserAccountId,
            current.PersonId,
            current.HouseholdId,
            CorrelationIdMiddleware.Get(HttpContext),
            DateTime.UtcNow);

        var rentalActor = new RentalActor(
            current.UserAccountId,
            current.PersonId,
            current.HouseholdId,
            CorrelationIdMiddleware.Get(HttpContext),
            DateTime.UtcNow);

        var utilities = await utilitiesService.GetOverviewAsync(
            utilityActor,
            cancellationToken);

        var rental = await rentalService.GetOverviewAsync(
            rentalActor,
            cancellationToken);

        if (!rental.CanManage)
        {
            return Json(new
            {
                canManage = false,
                submeters = Array.Empty<object>()
            });
        }

        var rooms = await GetHouseholdRoomsAsync(
            current.HouseholdId,
            cancellationToken);

        var roomById = rooms.ToDictionary(x => x.Id);

        var store = new SubmeterTenantChargeStore(
            environment.ContentRootPath);

        var generated = await store.GetAsync(
            current.HouseholdId,
            cancellationToken);

        var result = new List<object>();

        foreach (var meter in utilities.Meters)
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

            var roomId = GetGuid(meter, "LocationId");

            if (roomId == Guid.Empty
                || !roomById.TryGetValue(roomId, out var room))
            {
                continue;
            }

            var snapshot = BuildSnapshot(
                utilities,
                rental,
                meter,
                room.Id,
                room.Name,
                generated);

            // PKG-015q-FEAT-04: rozliczenie lokatora z podlicznika dotyczy
            // wyłącznie energii elektrycznej. Woda jest rozliczana z FV
            // po jej pełnym opłaceniu przez dom i dzielona według osób.
            if (!string.Equals(
                    snapshot.Medium,
                    "Electricity",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            result.Add(new
            {
                meterId = snapshot.MeterId,
                meterName = snapshot.MeterName,
                medium = snapshot.Medium,
                unitCode = snapshot.UnitCode,
                roomId = snapshot.RoomId,
                roomName = snapshot.RoomName,
                tenantName = snapshot.TenantName,
                leaseContractId = snapshot.LeaseContractId,
                periodKey = snapshot.PeriodKey,
                previousReadingId = snapshot.PreviousReadingId,
                currentReadingId = snapshot.CurrentReadingId,
                previousReadingAtUtc = snapshot.PreviousReadingAtUtc,
                currentReadingAtUtc = snapshot.CurrentReadingAtUtc,
                zoneCode = snapshot.ZoneCode,
                previousValue = snapshot.PreviousValue,
                currentValue = snapshot.CurrentValue,
                consumption = snapshot.Consumption,
                parentMeterId = snapshot.ParentMeterId,
                parentMeterName = snapshot.ParentMeterName,
                recommendedRatePerUnit = snapshot.RecommendedRatePerUnit,
                rateSource = snapshot.RateSource,
                recommendedDistributionRatePerUnit = snapshot.RecommendedDistributionRatePerUnit,
                distributionRateSource = snapshot.DistributionRateSource,
                alreadyGenerated = snapshot.AlreadyGenerated,
                energyAlreadyGenerated = snapshot.AlreadyGenerated,
                distributionAlreadyGenerated = snapshot.GeneratedDistributionAmountMinor > 0,
                generatedRatePerUnit = snapshot.GeneratedRatePerUnit,
                generatedAmountMinor = snapshot.GeneratedAmountMinor,
                generatedDistributionRatePerUnit = snapshot.GeneratedDistributionRatePerUnit,
                generatedDistributionAmountMinor = snapshot.GeneratedDistributionAmountMinor,
                generatedTotalAmountMinor = checked(snapshot.GeneratedAmountMinor + snapshot.GeneratedDistributionAmountMinor),
                canGenerate = snapshot.CanGenerate,
                blockReason = snapshot.BlockReason
            });
        }

        var requestToken = antiforgery
            .GetAndStoreTokens(HttpContext)
            .RequestToken;

        return Json(new
        {
            canManage = true,
            requestToken,
            submeters = result
        });
    }

    [HttpPost("Generate")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Generate(
        Guid meterId,
        Guid currentReadingId,
        string periodKey,
        string ratePerUnit,
        string distributionRatePerUnit,
        CancellationToken cancellationToken)
    {
        var current = await access.GetCurrentAsync(cancellationToken);
        if (current is null)
        {
            return Unauthorized();
        }

        if (!TryParseFlexibleDecimal(
                ratePerUnit,
                out var parsedRatePerUnit)
            || parsedRatePerUnit <= 0m)
        {
            return BadRequest(new
            {
                message =
                    $"Nie udało się odczytać stawki energii „{ratePerUnit}”. " +
                    "Podaj liczbę większą od 0, np. 1,15 lub 1.15."
            });
        }

        if (!TryParseFlexibleDecimal(
                distributionRatePerUnit,
                out var parsedDistributionRatePerUnit)
            || parsedDistributionRatePerUnit <= 0m)
        {
            return BadRequest(new
            {
                message =
                    $"Nie udało się odczytać stawki przesyłu „{distributionRatePerUnit}”. " +
                    "Podaj liczbę większą od 0, np. 0,42 lub 0.42."
            });
        }

        if (!IsPeriodKey(periodKey))
        {
            return BadRequest(new
            {
                message = "Okres musi mieć format RRRR-MM."
            });
        }

        var correlationId =
            CorrelationIdMiddleware.Get(HttpContext);

        var utilityActor = new UtilityActor(
            current.UserAccountId,
            current.PersonId,
            current.HouseholdId,
            correlationId,
            DateTime.UtcNow);

        var rentalActor = new RentalActor(
            current.UserAccountId,
            current.PersonId,
            current.HouseholdId,
            correlationId,
            DateTime.UtcNow);

        var utilities = await utilitiesService.GetOverviewAsync(
            utilityActor,
            cancellationToken);

        var rental = await rentalService.GetOverviewAsync(
            rentalActor,
            cancellationToken);

        if (!rental.CanManage)
        {
            return Forbid();
        }

        var rooms = await GetHouseholdRoomsAsync(
            current.HouseholdId,
            cancellationToken);

        var roomById = rooms.ToDictionary(x => x.Id);

        var meter = utilities.Meters.FirstOrDefault(
            x => GetGuid(x, "Id") == meterId);

        if (meter is null)
        {
            return BadRequest(new
            {
                message = "Nie znaleziono podlicznika."
            });
        }

        if (!string.Equals(
                GetString(meter, "MeterType"),
                "Sub",
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                GetString(meter, "LocationType"),
                "Room",
                StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new
            {
                message =
                    "Do rozliczenia lokatora można użyć tylko podlicznika przypisanego bezpośrednio do pokoju."
            });
        }

        if (!string.Equals(
                GetString(meter, "Medium"),
                "Electricity",
                StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new
            {
                message =
                    "PKG-015q-FEAT-04: podlicznik lokatora służy tutaj wyłącznie do rozliczenia prądu. Woda jest dzielona według liczby osób po opłaceniu FV."
            });
        }

        var roomId = GetGuid(meter, "LocationId");

        if (roomId == Guid.Empty
            || !roomById.TryGetValue(roomId, out var room))
        {
            return BadRequest(new
            {
                message =
                    "Podlicznik nie jest przypisany do pokoju należącego do tego gospodarstwa."
            });
        }

        var store = new SubmeterTenantChargeStore(
            environment.ContentRootPath);

        var generated = await store.GetAsync(
            current.HouseholdId,
            cancellationToken);

        var snapshot = BuildSnapshot(
            utilities,
            rental,
            meter,
            room.Id,
            room.Name,
            generated);

        if (!snapshot.CanGenerate)
        {
            return BadRequest(new
            {
                message =
                    snapshot.BlockReason
                    ?? "Brak danych do wyliczenia podlicznika."
            });
        }

        if (snapshot.CurrentReadingId != currentReadingId)
        {
            return BadRequest(new
            {
                message =
                    "Od czasu otwarcia formularza pojawił się nowszy zatwierdzony odczyt. Odśwież stronę i wykonaj wyliczenie ponownie."
            });
        }

        if (snapshot.LeaseContractId == Guid.Empty)
        {
            return BadRequest(new
            {
                message =
                    "Nie znaleziono aktywnej umowy lokatora dla pokoju w dniu odczytu."
            });
        }

        var settlementOverview =
            await settlementService.GetOverviewAsync(
                rentalActor,
                cancellationToken);

        var settlement = FindSettlement(
            settlementOverview,
            snapshot.LeaseContractId,
            periodKey);

        if (settlement is null)
        {
            await settlementService.BuildDraftAsync(
                rentalActor,
                new(
                    snapshot.LeaseContractId,
                    periodKey),
                cancellationToken);

            settlementOverview =
                await settlementService.GetOverviewAsync(
                    rentalActor,
                    cancellationToken);

            settlement = FindSettlement(
                settlementOverview,
                snapshot.LeaseContractId,
                periodKey);
        }

        if (settlement is null)
        {
            return BadRequest(new
            {
                message =
                    "Nie udało się utworzyć projektu rozliczenia lokatora."
            });
        }

        var settlementStatus =
            GetString(settlement, "Status");

        if (!IsEditableSettlementStatus(
                settlementStatus))
        {
            return BadRequest(new
            {
                message =
                    $"Rozliczenie {periodKey} ma status „{settlementStatus}” i nie można już dopisać pozycji bezpośrednio. Cofnij rozliczenie do projektu albo użyj korekty."
            });
        }

        var settlementId =
            GetGuid(settlement, "Id");

        var currency =
            GetString(
                settlement,
                "CurrencyCode",
                "PLN");

        var existingCharge = generated.FirstOrDefault(x =>
            x.MeterId == snapshot.MeterId
            && x.CurrentReadingId == snapshot.CurrentReadingId);

        var energyAmountMinor =
            existingCharge?.AmountMinor > 0
                ? existingCharge.AmountMinor
                : checked(
                    (long)Math.Round(
                        snapshot.Consumption
                        * parsedRatePerUnit
                        * 100m,
                        0,
                        MidpointRounding.AwayFromZero));

        var distributionAmountMinor =
            existingCharge?.DistributionAmountMinor > 0
                ? existingCharge.DistributionAmountMinor
                : checked(
                    (long)Math.Round(
                        snapshot.Consumption
                        * parsedDistributionRatePerUnit
                        * 100m,
                        0,
                        MidpointRounding.AwayFromZero));

        if (energyAmountMinor <= 0
            || distributionAmountMinor <= 0)
        {
            return BadRequest(new
            {
                message =
                    "Wyliczona kwota energii lub przesyłu wynosi 0. Sprawdź odczyty i obie stawki."
            });
        }

        // AddManualLineAsync dopuszcza ręczne typy techniczne, dlatego
        // obie pozycje zapisujemy jako Adjustment, a znaczenie zachowujemy
        // w SourceType i audycie. W rozliczeniu są pokazywane jako osobne
        // pozycje: energia z podlicznika oraz przesył/dystrybucja.
        const string lineType = "Adjustment";

        var energySourceType =
            $"SubmeterElectricityEnergy:{snapshot.MeterName}";

        var distributionSourceType =
            $"SubmeterElectricityDistribution:{snapshot.MeterName}";

        var energyExists =
            await TenantElectricityLineExistsAsync(
                settlementId,
                snapshot.CurrentReadingId,
                distribution: false,
                cancellationToken);

        if (!energyExists)
        {
            var energyAudit = JsonSerializer.Serialize(new
            {
                source = "SubmeterReading",
                package = PackageVersion,
                sourceType = energySourceType,
                settlementLineType = lineType,
                medium = "Electricity",
                component = "Energy",
                displayLabel = $"Prąd — {snapshot.MeterName}",
                meterId = snapshot.MeterId,
                meterName = snapshot.MeterName,
                roomId = snapshot.RoomId,
                roomName = snapshot.RoomName,
                tenantName = snapshot.TenantName,
                leaseContractId = snapshot.LeaseContractId,
                previousReadingId = snapshot.PreviousReadingId,
                currentReadingId = snapshot.CurrentReadingId,
                previousReadingAtUtc = snapshot.PreviousReadingAtUtc,
                currentReadingAtUtc = snapshot.CurrentReadingAtUtc,
                zoneCode = snapshot.ZoneCode,
                previousValue = snapshot.PreviousValue,
                currentValue = snapshot.CurrentValue,
                consumption = snapshot.Consumption,
                unitCode = snapshot.UnitCode,
                parentMeterId = snapshot.ParentMeterId,
                parentMeterName = snapshot.ParentMeterName,
                ratePerUnit = parsedRatePerUnit,
                rateSource =
                    snapshot.RecommendedRatePerUnit is decimal recommendedRateForAudit
                    && recommendedRateForAudit == parsedRatePerUnit
                        ? snapshot.RateSource
                        : "ManualOverride",
                amountMinor = energyAmountMinor,
                currencyCode = currency
            });

            await settlementService.AddManualLineAsync(
                rentalActor,
                new(
                    settlementId,
                    lineType,
                    energyAmountMinor,
                    currency,
                    energySourceType,
                    null,
                    energyAudit),
                cancellationToken);
        }

        var distributionExists =
            await TenantElectricityLineExistsAsync(
                settlementId,
                snapshot.CurrentReadingId,
                distribution: true,
                cancellationToken);

        if (!distributionExists)
        {
            var distributionAudit = JsonSerializer.Serialize(new
            {
                source = "SubmeterReading",
                package = PackageVersion,
                sourceType = distributionSourceType,
                settlementLineType = lineType,
                medium = "Electricity",
                component = "DistributionVariable",
                displayLabel = "Przesył / dystrybucja prądu",
                meterId = snapshot.MeterId,
                meterName = snapshot.MeterName,
                roomId = snapshot.RoomId,
                roomName = snapshot.RoomName,
                tenantName = snapshot.TenantName,
                leaseContractId = snapshot.LeaseContractId,
                previousReadingId = snapshot.PreviousReadingId,
                currentReadingId = snapshot.CurrentReadingId,
                previousReadingAtUtc = snapshot.PreviousReadingAtUtc,
                currentReadingAtUtc = snapshot.CurrentReadingAtUtc,
                zoneCode = snapshot.ZoneCode,
                previousValue = snapshot.PreviousValue,
                currentValue = snapshot.CurrentValue,
                consumption = snapshot.Consumption,
                unitCode = snapshot.UnitCode,
                parentMeterId = snapshot.ParentMeterId,
                parentMeterName = snapshot.ParentMeterName,
                ratePerUnit = parsedDistributionRatePerUnit,
                rateSource =
                    snapshot.RecommendedDistributionRatePerUnit is decimal recommendedDistributionRateForAudit
                    && recommendedDistributionRateForAudit == parsedDistributionRatePerUnit
                        ? snapshot.DistributionRateSource
                        : "ManualOverride",
                amountMinor = distributionAmountMinor,
                currencyCode = currency
            });

            await settlementService.AddManualLineAsync(
                rentalActor,
                new(
                    settlementId,
                    lineType,
                    distributionAmountMinor,
                    currency,
                    distributionSourceType,
                    null,
                    distributionAudit),
                cancellationToken);
        }

        await store.AddAsync(
            new SubmeterTenantChargeRecord
            {
                Id = existingCharge?.Id ?? Guid.NewGuid(),
                HouseholdId = current.HouseholdId,
                MeterId = snapshot.MeterId,
                PreviousReadingId = snapshot.PreviousReadingId,
                CurrentReadingId = snapshot.CurrentReadingId,
                LeaseContractId = snapshot.LeaseContractId,
                SettlementId = settlementId,
                RoomId = snapshot.RoomId,
                RoomName = snapshot.RoomName,
                TenantName = snapshot.TenantName,
                PeriodKey = periodKey,
                Medium = "Electricity",
                ZoneCode = snapshot.ZoneCode,
                UnitCode = snapshot.UnitCode,
                PreviousValue = snapshot.PreviousValue,
                CurrentValue = snapshot.CurrentValue,
                Consumption = snapshot.Consumption,
                RatePerUnit =
                    existingCharge?.RatePerUnit > 0
                        ? existingCharge.RatePerUnit
                        : parsedRatePerUnit,
                AmountMinor = energyAmountMinor,
                DistributionRatePerUnit =
                    existingCharge?.DistributionRatePerUnit > 0
                        ? existingCharge.DistributionRatePerUnit
                        : parsedDistributionRatePerUnit,
                DistributionAmountMinor = distributionAmountMinor,
                CurrencyCode = currency,
                RateSource =
                    existingCharge?.RateSource
                    ?? (snapshot.RecommendedRatePerUnit is decimal recommendedRate
                        && recommendedRate == parsedRatePerUnit
                            ? snapshot.RateSource
                            : "ManualOverride"),
                DistributionRateSource =
                    existingCharge?.DistributionRateSource
                    ?? (snapshot.RecommendedDistributionRatePerUnit is decimal recommendedDistributionRate
                        && recommendedDistributionRate == parsedDistributionRatePerUnit
                            ? snapshot.DistributionRateSource
                            : "ManualOverride"),
                CreatedAtUtc = existingCharge?.CreatedAtUtc ?? DateTime.UtcNow,
                CreatedByUserAccountId = existingCharge?.CreatedByUserAccountId ?? current.UserAccountId
            },
            cancellationToken);

        return Json(new
        {
            ok = true,
            package = PackageVersion,
            settlementId,
            energyAmountMinor,
            distributionAmountMinor,
            totalAmountMinor = checked(energyAmountMinor + distributionAmountMinor),
            currencyCode = currency,
            message =
                $"Rozliczono prąd {snapshot.TenantName} za {periodKey}: " +
                $"energia {energyAmountMinor / 100m:N2} {currency} + " +
                $"przesył {distributionAmountMinor / 100m:N2} {currency} = " +
                $"{(energyAmountMinor + distributionAmountMinor) / 100m:N2} {currency}."
        });
    }

    private SubmeterSnapshot BuildSnapshot(
        UtilityOverview utilities,
        RentalOverview rental,
        object meter,
        Guid roomId,
        string roomName,
        IReadOnlyList<SubmeterTenantChargeRecord> generated)
    {
        var meterId =
            GetGuid(meter, "Id");

        var meterName =
            GetString(meter, "Name", "Podlicznik");

        var medium =
            GetString(meter, "Medium");

        var unitCode =
            GetString(meter, "UnitCode");

        var approved = utilities.Readings
            .Where(x =>
                x.MeterId == meterId
                && x.Status == ReadingStatuses.Approved)
            .OrderByDescending(x => x.ReadingAtUtc)
            .ToArray();

        if (approved.Length < 2)
        {
            return SubmeterSnapshot.Blocked(
                meterId,
                meterName,
                medium,
                unitCode,
                roomId,
                roomName,
                "Potrzebne są co najmniej dwa zatwierdzone odczyty: początkowy i bieżący.");
        }

        var currentReading =
            approved[0];

        var currentValue =
            ReadPreferredValue(
                utilities,
                currentReading.Id);

        if (currentValue is null)
        {
            return SubmeterSnapshot.Blocked(
                meterId,
                meterName,
                medium,
                unitCode,
                roomId,
                roomName,
                "Najnowszy zatwierdzony odczyt nie zawiera wartości.");
        }

        var previousReading =
            approved
                .Skip(1)
                .FirstOrDefault(x =>
                    HasZone(
                        utilities,
                        x.Id,
                        currentValue.ZoneCode));

        if (previousReading is null)
        {
            return SubmeterSnapshot.Blocked(
                meterId,
                meterName,
                medium,
                unitCode,
                roomId,
                roomName,
                $"Brak wcześniejszego zatwierdzonego odczytu dla strefy {currentValue.ZoneCode}.");
        }

        var previousValue =
            ReadValue(
                utilities,
                previousReading.Id,
                currentValue.ZoneCode);

        if (previousValue is null)
        {
            return SubmeterSnapshot.Blocked(
                meterId,
                meterName,
                medium,
                unitCode,
                roomId,
                roomName,
                "Nie udało się odczytać poprzedniej wartości podlicznika.");
        }

        var consumption =
            currentValue.Value
            - previousValue.Value;

        if (consumption < 0m)
        {
            return SubmeterSnapshot.Blocked(
                meterId,
                meterName,
                medium,
                unitCode,
                roomId,
                roomName,
                "Bieżący stan jest niższy od poprzedniego. Najpierw zarejestruj korektę, wymianę lub zerowanie licznika.");
        }

        var readingLocal =
            currentReading.ReadingAtUtc.ToLocalTime();

        var readingDate =
            DateOnly.FromDateTime(readingLocal);

        var roomContracts = rental.Contracts
            .Where(x =>
                x.Status == LeaseStatuses.Signed
                && (
                    GetGuid(x, "RoomId") == roomId
                    || string.Equals(
                        x.RoomName,
                        roomName,
                        StringComparison.OrdinalIgnoreCase)))
            .OrderBy(x => x.LeaseFrom)
            .ToArray();

        var contract = roomContracts
            .FirstOrDefault(x =>
                x.LeaseFrom <= readingDate
                && (!x.LeaseTo.HasValue
                    || x.LeaseTo.Value >= readingDate));

        var nextContract = contract is null
            ? roomContracts
                .Where(x => x.LeaseFrom > readingDate)
                .OrderBy(x => x.LeaseFrom)
                .FirstOrDefault()
            : null;

        var parentMeterId =
            GetGuid(
                meter,
                "ParentMeterId");

        var parentMeter =
            parentMeterId == Guid.Empty
                ? null
                : utilities.Meters.FirstOrDefault(x =>
                    GetGuid(x, "Id") == parentMeterId);

        var parentMeterName =
            parentMeter is null
                ? ""
                : GetString(
                    parentMeter,
                    "Name",
                    "Licznik główny");

        var recommendedRate =
            parentMeter is null
                ? null
                : ResolveMainMeterTariffRate(
                    utilities,
                    parentMeter,
                    unitCode,
                    currentValue.ZoneCode,
                    readingDate,
                    "Energy");

        var recommendedDistributionRate =
            parentMeter is null
                ? null
                : ResolveMainMeterTariffRate(
                    utilities,
                    parentMeter,
                    unitCode,
                    currentValue.ZoneCode,
                    readingDate,
                    "Distribution");

        var alreadyGenerated =
            generated.FirstOrDefault(x =>
                x.MeterId == meterId
                && x.CurrentReadingId == currentReading.Id);

        var distributionAlreadyGenerated =
            alreadyGenerated?.DistributionAmountMinor > 0;

        return new SubmeterSnapshot(
            MeterId:
                meterId,
            MeterName:
                meterName,
            Medium:
                medium,
            UnitCode:
                unitCode,
            RoomId:
                roomId,
            RoomName:
                roomName,
            TenantName:
                contract?.TenantName
                ?? nextContract?.TenantName
                ?? "",
            LeaseContractId:
                contract?.ContractId
                ?? nextContract?.ContractId
                ?? Guid.Empty,
            PeriodKey:
                readingLocal.ToString("yyyy-MM"),
            PreviousReadingId:
                previousReading.Id,
            CurrentReadingId:
                currentReading.Id,
            PreviousReadingAtUtc:
                previousReading.ReadingAtUtc,
            CurrentReadingAtUtc:
                currentReading.ReadingAtUtc,
            ZoneCode:
                currentValue.ZoneCode,
            PreviousValue:
                previousValue.Value,
            CurrentValue:
                currentValue.Value,
            Consumption:
                consumption,
            ParentMeterId:
                parentMeterId,
            ParentMeterName:
                parentMeterName,
            RecommendedRatePerUnit:
                recommendedRate?.Rate,
            RateSource:
                recommendedRate?.Source
                ?? (parentMeter is null
                    ? "Brak przypisanego licznika głównego"
                    : $"Brak stawki energii w aktywnej taryfie licznika głównego „{parentMeterName}”"),
            RecommendedDistributionRatePerUnit:
                recommendedDistributionRate?.Rate,
            DistributionRateSource:
                recommendedDistributionRate?.Source
                ?? (parentMeter is null
                    ? "Brak przypisanego licznika głównego"
                    : $"Brak stawki przesyłu w aktywnej taryfie licznika głównego „{parentMeterName}”"),
            AlreadyGenerated:
                alreadyGenerated is not null,
            GeneratedRatePerUnit:
                alreadyGenerated?.RatePerUnit ?? 0m,
            GeneratedAmountMinor:
                alreadyGenerated?.AmountMinor ?? 0,
            GeneratedDistributionRatePerUnit:
                alreadyGenerated?.DistributionRatePerUnit ?? 0m,
            GeneratedDistributionAmountMinor:
                alreadyGenerated?.DistributionAmountMinor ?? 0,
            CanGenerate:
                contract is not null
                && consumption > 0m
                && (!distributionAlreadyGenerated),
            BlockReason:
                contract is null
                    ? nextContract is not null
                        ? $"Odczyt {readingDate:yyyy-MM-dd} jest wcześniejszy niż początek umowy lokatora ({nextContract.LeaseFrom:yyyy-MM-dd}). Tych {consumption:0.###} {unitCode} nie naliczamy nowemu lokatorowi. Dodaj zatwierdzony odczyt po rozpoczęciu umowy; wtedy aplikacja policzy zużycie od stanu początkowego."
                        : "Brak aktywnej umowy lokatora dla tego pokoju w dniu odczytu."
                    : consumption <= 0m
                        ? "Zużycie podlicznika musi być większe od 0."
                        : distributionAlreadyGenerated
                            ? "Energia i przesył dla tego odczytu zostały już rozliczone."
                            : alreadyGenerated is not null
                                ? "Energia jest już rozliczona — do uzupełnienia pozostał przesył / dystrybucja."
                                : null);
    }

    private static ReadingValueSnapshot? ReadPreferredValue(
        UtilityOverview overview,
        Guid readingId)
    {
        var values = overview.ReadingValues
            .Where(x => x.MeterReadingId == readingId)
            .ToArray();

        var selected =
            values.FirstOrDefault(x =>
                string.Equals(
                    x.ZoneCode,
                    "ALL",
                    StringComparison.OrdinalIgnoreCase))
            ?? values.FirstOrDefault();

        if (selected is null)
        {
            return null;
        }

        return new ReadingValueSnapshot(
            selected.ZoneCode,
            selected.ValueScaled
            / (decimal)Math.Pow(10, selected.Scale),
            selected.UnitCode);
    }

    private static ReadingValueSnapshot? ReadValue(
        UtilityOverview overview,
        Guid readingId,
        string zoneCode)
    {
        var selected = overview.ReadingValues
            .FirstOrDefault(x =>
                x.MeterReadingId == readingId
                && string.Equals(
                    x.ZoneCode,
                    zoneCode,
                    StringComparison.OrdinalIgnoreCase));

        if (selected is null)
        {
            return null;
        }

        return new ReadingValueSnapshot(
            selected.ZoneCode,
            selected.ValueScaled
            / (decimal)Math.Pow(10, selected.Scale),
            selected.UnitCode);
    }

    private static bool HasZone(
        UtilityOverview overview,
        Guid readingId,
        string zoneCode) =>
        overview.ReadingValues.Any(x =>
            x.MeterReadingId == readingId
            && string.Equals(
                x.ZoneCode,
                zoneCode,
                StringComparison.OrdinalIgnoreCase));

    private static RateSnapshot? ResolveMainMeterTariffRate(
        object overview,
        object parentMeter,
        string unitCode,
        string zoneCode,
        DateOnly date,
        string componentKind)
    {
        var parentMeterId =
            GetGuid(
                parentMeter,
                "Id");

        if (parentMeterId == Guid.Empty)
        {
            return null;
        }

        var allObjects =
            WalkObjectGraph(
                    overview,
                    maxDepth: 6)
                .ToArray();

        // 1. Ustalamy umowę / umowy powiązane z licznikiem głównym.
        var contractIds =
            ResolveContractIdsForMeter(
                allObjects,
                parentMeterId);

        if (contractIds.Count == 0)
        {
            return null;
        }

        // 2. Ustalamy wersje taryf należące do tych umów.
        var tariffVersionIds =
            new HashSet<Guid>();

        var tariffRoots =
            new List<object>();

        foreach (var item in allObjects)
        {
            var typeName =
                item.GetType().Name;

            var itemContractId =
                GetGuid(
                    item,
                    "UtilityContractId",
                    "ContractId");

            if (itemContractId == Guid.Empty
                || !contractIds.Contains(
                    itemContractId))
            {
                continue;
            }

            if (!typeName.Contains(
                    "Tariff",
                    StringComparison.OrdinalIgnoreCase)
                && !HasProperty(
                    item,
                    "TariffVersionId")
                && !HasProperty(
                    item,
                    "UtilityTariffVersionId"))
            {
                continue;
            }

            tariffRoots.Add(
                item);

            var tariffId =
                GetGuid(
                    item,
                    "Id",
                    "TariffId",
                    "TariffVersionId",
                    "UtilityTariffVersionId");

            if (tariffId != Guid.Empty)
            {
                tariffVersionIds.Add(
                    tariffId);
            }
        }

        // 3. Zbieramy wszystkie stawki. Stawka może:
        //    a) być dzieckiem obiektu wersji taryfy,
        //    b) być osobnym rekordem z TariffVersionId.
        var rateCandidates =
            new List<object>();

        foreach (var tariffRoot in tariffRoots)
        {
            foreach (var nested in WalkObjectGraph(
                         tariffRoot,
                         maxDepth: 3))
            {
                if (LooksLikeRate(
                        nested))
                {
                    rateCandidates.Add(
                        nested);
                }
            }
        }

        foreach (var item in allObjects)
        {
            var linkedTariffId =
                GetGuid(
                    item,
                    "UtilityTariffVersionId",
                    "TariffVersionId",
                    "TariffId");

            if (linkedTariffId != Guid.Empty
                && tariffVersionIds.Contains(
                    linkedTariffId)
                && LooksLikeRate(
                    item))
            {
                rateCandidates.Add(
                    item);
            }
        }

        // Niektóre modele zwracają wersję taryfy razem z pojedynczą stawką
        // w tym samym DTO. Takie obiekty również dopuszczamy.
        rateCandidates.AddRange(
            tariffRoots.Where(
                LooksLikeRate));

        var candidates =
            new List<(int Score, decimal Rate, string Source)>();

        foreach (var item in rateCandidates
                     .Distinct(
                         ReferenceComparer.Instance))
        {
            var rate =
                TryReadRate(
                    item);

            if (!rate.HasValue
                || rate.Value <= 0m)
            {
                continue;
            }

            var candidateUnit =
                GetString(
                    item,
                    "UnitCode");

            if (!string.IsNullOrWhiteSpace(
                    candidateUnit)
                && !string.Equals(
                    candidateUnit,
                    unitCode,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var validFrom =
                GetDateOnly(
                    item,
                    "ValidFrom",
                    "EffectiveFrom");

            var validTo =
                GetNullableDateOnly(
                    item,
                    "ValidTo",
                    "EffectiveTo");

            if (validFrom.HasValue
                && validFrom.Value > date)
            {
                continue;
            }

            if (validTo.HasValue
                && validTo.Value < date)
            {
                continue;
            }

            var candidateZone =
                GetString(
                    item,
                    "ZoneCode");

            if (!string.IsNullOrWhiteSpace(
                    candidateZone)
                && !string.Equals(
                    candidateZone,
                    zoneCode,
                    StringComparison.OrdinalIgnoreCase)
                && !string.Equals(
                    candidateZone,
                    "ALL",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var component =
                GetString(
                    item,
                    "ComponentCode");

            var isEnergyComponent =
                component.Contains(
                    "Energy",
                    StringComparison.OrdinalIgnoreCase)
                || component.Contains(
                    "Consumption",
                    StringComparison.OrdinalIgnoreCase);

            var isDistributionComponent =
                component.Contains(
                    "NetworkVariable",
                    StringComparison.OrdinalIgnoreCase)
                || component.Contains(
                    "DistributionVariable",
                    StringComparison.OrdinalIgnoreCase)
                || component.Contains(
                    "TransmissionVariable",
                    StringComparison.OrdinalIgnoreCase);

            if (string.Equals(
                    componentKind,
                    "Energy",
                    StringComparison.OrdinalIgnoreCase)
                && !isEnergyComponent)
            {
                continue;
            }

            if (string.Equals(
                    componentKind,
                    "Distribution",
                    StringComparison.OrdinalIgnoreCase)
                && !isDistributionComponent)
            {
                continue;
            }

            var score = 100;

            if (string.Equals(
                    candidateUnit,
                    unitCode,
                    StringComparison.OrdinalIgnoreCase))
            {
                score += 20;
            }

            if (string.Equals(
                    candidateZone,
                    zoneCode,
                    StringComparison.OrdinalIgnoreCase))
            {
                score += 20;
            }
            else if (string.Equals(
                         candidateZone,
                         "ALL",
                         StringComparison.OrdinalIgnoreCase))
            {
                score += 10;
            }

            if (component.Contains(
                    "Consumption",
                    StringComparison.OrdinalIgnoreCase)
                || component.Contains(
                    "Energy",
                    StringComparison.OrdinalIgnoreCase)
                || component.Contains(
                    "Variable",
                    StringComparison.OrdinalIgnoreCase))
            {
                score += 30;
            }

            if (component.Contains(
                    "Fixed",
                    StringComparison.OrdinalIgnoreCase)
                || component.Contains(
                    "Subscription",
                    StringComparison.OrdinalIgnoreCase)
                || component.Contains(
                    "Capacity",
                    StringComparison.OrdinalIgnoreCase))
            {
                score -= 50;
            }

            var tariffName =
                FindTariffNameForRate(
                    item,
                    tariffRoots,
                    tariffVersionIds);

            var source =
                $"Taryfa licznika głównego „{GetString(parentMeter, "Name", "główny")}”";

            if (!string.IsNullOrWhiteSpace(
                    tariffName))
            {
                source +=
                    $" · {tariffName}";
            }

            if (!string.IsNullOrWhiteSpace(
                    component))
            {
                source +=
                    $" · {component}";
            }

            candidates.Add(
                (
                    score,
                    rate.Value,
                    source)
                );
        }

        var best =
            candidates
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.Rate)
                .FirstOrDefault();

        return best.Rate > 0m
            ? new RateSnapshot(
                best.Rate,
                best.Source)
            : null;
    }

    private static HashSet<Guid> ResolveContractIdsForMeter(
        IReadOnlyList<object> allObjects,
        Guid meterId)
    {
        var result =
            new HashSet<Guid>();

        foreach (var item in allObjects)
        {
            var typeName =
                item.GetType().Name;

            var contractId =
                GetGuid(
                    item,
                    "UtilityContractId",
                    "ContractId");

            var linkedMeterId =
                GetGuid(
                    item,
                    "MeterId",
                    "MainMeterId");

            // Tabela / DTO relacji Contract <-> Meter.
            if (linkedMeterId == meterId
                && contractId != Guid.Empty)
            {
                result.Add(
                    contractId);
            }

            // Sam obiekt umowy może zawierać pojedynczy MeterId.
            if (typeName.Contains(
                    "Contract",
                    StringComparison.OrdinalIgnoreCase)
                && linkedMeterId == meterId)
            {
                var ownId =
                    GetGuid(
                        item,
                        "Id",
                        "ContractId",
                        "UtilityContractId");

                if (ownId != Guid.Empty)
                {
                    result.Add(
                        ownId);
                }
            }

            // Umowa może przechowywać kolekcję MeterIds albo obiektów Meter.
            if (!typeName.Contains(
                    "Contract",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var ownContractId =
                GetGuid(
                    item,
                    "Id",
                    "ContractId",
                    "UtilityContractId");

            if (ownContractId == Guid.Empty)
            {
                continue;
            }

            foreach (var property in item.GetType()
                         .GetProperties(
                             BindingFlags.Public
                             | BindingFlags.Instance))
            {
                if (!property.CanRead
                    || property.GetIndexParameters().Length > 0
                    || !property.Name.Contains(
                        "Meter",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                object? value;

                try
                {
                    value =
                        property.GetValue(
                            item);
                }
                catch
                {
                    continue;
                }

                if (ContainsGuid(
                        value,
                        meterId))
                {
                    result.Add(
                        ownContractId);

                    break;
                }
            }
        }

        return result;
    }

    private static bool ContainsGuid(
        object? value,
        Guid expected)
    {
        if (value is null)
        {
            return false;
        }

        if (value is Guid guid)
        {
            return guid == expected;
        }

        if (Guid.TryParse(
                value.ToString(),
                out var parsed))
        {
            return parsed == expected;
        }

        if (value is not IEnumerable enumerable
            || value is string)
        {
            return false;
        }

        foreach (var item in enumerable)
        {
            if (item is null)
            {
                continue;
            }

            if (item is Guid itemGuid
                && itemGuid == expected)
            {
                return true;
            }

            if (Guid.TryParse(
                    item.ToString(),
                    out var itemParsed)
                && itemParsed == expected)
            {
                return true;
            }

            var objectId =
                GetGuid(
                    item,
                    "Id",
                    "MeterId");

            if (objectId == expected)
            {
                return true;
            }
        }

        return false;
    }

    private static bool LooksLikeRate(
        object item)
    {
        var typeName =
            item.GetType().Name;

        return HasProperty(
                   item,
                   "RatePerUnit")
               || HasProperty(
                   item,
                   "ValueScaled")
               || HasProperty(
                   item,
                   "RateScaled")
               || HasProperty(
                   item,
                   "UnitRate")
               || HasProperty(
                   item,
                   "RateMinorPerUnit")
               || typeName.Contains(
                   "TariffRate",
                   StringComparison.OrdinalIgnoreCase);
    }

    private static string FindTariffNameForRate(
        object rate,
        IReadOnlyList<object> tariffRoots,
        IReadOnlySet<Guid> tariffVersionIds)
    {
        var rateTariffId =
            GetGuid(
                rate,
                "UtilityTariffVersionId",
                "TariffVersionId",
                "TariffId");

        if (rateTariffId != Guid.Empty)
        {
            var matching =
                tariffRoots.FirstOrDefault(x =>
                    GetGuid(
                        x,
                        "Id",
                        "TariffVersionId",
                        "TariffId",
                        "UtilityTariffVersionId")
                    == rateTariffId);

            if (matching is not null)
            {
                return GetString(
                    matching,
                    "Name");
            }
        }

        // Jeśli stawka jest zagnieżdżona w taryfie i nie ma FK,
        // pokazujemy pierwszą znalezioną nazwę aktywnej wersji.
        return tariffRoots
            .Select(x =>
                GetString(
                    x,
                    "Name"))
            .FirstOrDefault(x =>
                !string.IsNullOrWhiteSpace(
                    x))
            ?? "";
    }

    private static IEnumerable<object> WalkObjectGraph(
        object root,
        int maxDepth)
    {
        var visited =
            new HashSet<object>(
                ReferenceComparer.Instance);

        var queue =
            new Queue<(object Item, int Depth)>();

        queue.Enqueue(
            (root, 0));

        while (queue.Count > 0)
        {
            var (item, depth) =
                queue.Dequeue();

            if (!visited.Add(item))
            {
                continue;
            }

            yield return item;

            if (depth >= maxDepth)
            {
                continue;
            }

            if (item is string
                || item.GetType().IsPrimitive
                || item is decimal
                || item is Guid
                || item is DateTime
                || item is DateOnly)
            {
                continue;
            }

            if (item is IEnumerable enumerable)
            {
                foreach (var child in enumerable)
                {
                    if (child is not null)
                    {
                        queue.Enqueue(
                            (child, depth + 1));
                    }
                }

                continue;
            }

            foreach (var property in item.GetType()
                         .GetProperties(
                             BindingFlags.Public
                             | BindingFlags.Instance))
            {
                if (!property.CanRead
                    || property.GetIndexParameters().Length > 0)
                {
                    continue;
                }

                object? value;

                try
                {
                    value = property.GetValue(item);
                }
                catch
                {
                    continue;
                }

                if (value is not null)
                {
                    queue.Enqueue(
                        (value, depth + 1));
                }
            }
        }
    }

    private static decimal? TryReadRate(
        object source)
    {
        var direct =
            GetDecimal(
                source,
                "RatePerUnit",
                "UnitRate");

        if (direct.HasValue)
        {
            return direct;
        }

        var valueScaled =
            GetNullableLong(
                source,
                "ValueScaled");

        if (valueScaled.HasValue)
        {
            var scale =
                GetInt(
                    source,
                    "Scale");

            return valueScaled.Value
                   / (decimal)Math.Pow(
                       10,
                       Math.Max(0, scale));
        }

        var scaled =
            GetNullableLong(
                source,
                "RateScaled");

        if (scaled.HasValue)
        {
            var scale =
                GetInt(
                    source,
                    "Scale",
                    "RateScale");

            return scaled.Value
                   / (decimal)Math.Pow(
                       10,
                       Math.Max(0, scale));
        }

        var minor =
            GetNullableLong(
                source,
                "RateMinorPerUnit");

        return minor.HasValue
            ? minor.Value / 100m
            : null;
    }

    private async Task<bool> TenantElectricityLineExistsAsync(
        Guid settlementId,
        Guid currentReadingId,
        bool distribution,
        CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        var closeWhenDone =
            connection.State != System.Data.ConnectionState.Open;

        if (closeWhenDone)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            await using var command = connection.CreateCommand();

            command.CommandText = distribution
                ? """
                  SELECT COUNT(1)
                  FROM "TenantSettlementLines"
                  WHERE "TenantSettlementId" = $settlementId
                    AND COALESCE("SourceType",'') LIKE 'SubmeterElectricityDistribution%'
                    AND COALESCE("CalculationSnapshotJson",'') LIKE $readingId;
                  """
                : """
                  SELECT COUNT(1)
                  FROM "TenantSettlementLines"
                  WHERE "TenantSettlementId" = $settlementId
                    AND (
                           COALESCE("SourceType",'') = 'SubmeterElectricity'
                        OR COALESCE("SourceType",'') LIKE 'SubmeterElectricityEnergy%'
                    )
                    AND COALESCE("CalculationSnapshotJson",'') LIKE $readingId;
                  """;

            var settlementParameter = command.CreateParameter();
            settlementParameter.ParameterName = "$settlementId";
            settlementParameter.Value = settlementId.ToString("D");
            command.Parameters.Add(settlementParameter);

            var readingParameter = command.CreateParameter();
            readingParameter.ParameterName = "$readingId";
            readingParameter.Value = $"%{currentReadingId:D}%";
            command.Parameters.Add(readingParameter);

            return Convert.ToInt64(
                await command.ExecuteScalarAsync(cancellationToken)
                ?? 0L) > 0;
        }
        finally
        {
            if (closeWhenDone)
            {
                await connection.CloseAsync();
            }
        }
    }

    private static object? FindSettlement(
        TenantSettlementOverview overview,
        Guid leaseContractId,
        string periodKey) =>
        overview.Settlements.FirstOrDefault(x =>
        {
            var contractId =
                GetGuid(
                    x,
                    "LeaseContractId",
                    "ContractId");

            return contractId == leaseContractId
                   && string.Equals(
                       x.PeriodKey,
                       periodKey,
                       StringComparison.OrdinalIgnoreCase);
        });

    private async Task<List<RoomSnapshot>> GetHouseholdRoomsAsync(
        Guid householdId,
        CancellationToken cancellationToken)
    {
        var parcelIds = await db.Parcels
            .AsNoTracking()
            .Where(x => x.HouseholdId == householdId)
            .Select(x => x.Id)
            .ToArrayAsync(cancellationToken);

        var buildingIds = await db.Buildings
            .AsNoTracking()
            .Where(x => parcelIds.Contains(x.ParcelId))
            .Select(x => x.Id)
            .ToArrayAsync(cancellationToken);

        return await db.Rooms
            .AsNoTracking()
            .Where(x => buildingIds.Contains(x.BuildingId))
            .Select(x => new RoomSnapshot(x.Id, x.Name))
            .ToListAsync(cancellationToken);
    }

    private static bool IsEditableSettlementStatus(
        string? status) =>
        status is
            TenantSettlementStatuses.Draft
            or TenantSettlementStatuses.AwaitingData
            or TenantSettlementStatuses.ReadyForApproval;

    private static string MediumLabel(
        string? medium) =>
        medium switch
        {
            "Electricity" => "prąd",
            "Water" => "woda",
            "Gas" => "gaz",
            _ => "media"
        };

    private static bool TryParseFlexibleDecimal(
        string? value,
        out decimal result)
    {
        result = 0m;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized =
            value.Trim()
                .Replace("\u00A0", "")
                .Replace(" ", "");

        const System.Globalization.NumberStyles styles =
            System.Globalization.NumberStyles.AllowLeadingSign
            | System.Globalization.NumberStyles.AllowDecimalPoint;

        // Wartość wpisana po polsku, np. 1,15.
        if (decimal.TryParse(
                normalized,
                styles,
                System.Globalization.CultureInfo.GetCultureInfo("pl-PL"),
                out result))
        {
            return true;
        }

        // input type=number / FormData zwykle wysyła 1.15.
        if (decimal.TryParse(
                normalized,
                styles,
                System.Globalization.CultureInfo.InvariantCulture,
                out result))
        {
            return true;
        }

        return false;
    }

    private static bool IsPeriodKey(
        string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length != 7
            || value[4] != '-')
        {
            return false;
        }

        return int.TryParse(
                   value[..4],
                   out var year)
               && year is >= 2000 and <= 2200
               && int.TryParse(
                   value[5..],
                   out var month)
               && month is >= 1 and <= 12;
    }

    private static bool HasProperty(
        object source,
        string name) =>
        source.GetType().GetProperty(
            name,
            BindingFlags.Public
            | BindingFlags.Instance
            | BindingFlags.IgnoreCase)
        is not null;

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
            var property = source.GetType()
                .GetProperty(
                    name,
                    BindingFlags.Public
                    | BindingFlags.Instance
                    | BindingFlags.IgnoreCase);

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
        GetValue(source, name)?.ToString()
        ?? fallback;

    private static Guid GetGuid(
        object source,
        params string[] names)
    {
        var value =
            GetValue(source, names);

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

    private static decimal? GetDecimal(
        object source,
        params string[] names)
    {
        var value =
            GetValue(source, names);

        if (value is null)
        {
            return null;
        }

        try
        {
            return Convert.ToDecimal(value);
        }
        catch
        {
            return null;
        }
    }

    private static long? GetNullableLong(
        object source,
        params string[] names)
    {
        var value =
            GetValue(source, names);

        if (value is null)
        {
            return null;
        }

        try
        {
            return Convert.ToInt64(value);
        }
        catch
        {
            return null;
        }
    }

    private static int GetInt(
        object source,
        params string[] names)
    {
        var value =
            GetValue(source, names);

        if (value is null)
        {
            return 0;
        }

        try
        {
            return Convert.ToInt32(value);
        }
        catch
        {
            return 0;
        }
    }

    private static DateOnly? GetDateOnly(
        object source,
        params string[] names)
    {
        var value =
            GetValue(source, names);

        if (value is DateOnly dateOnly)
        {
            return dateOnly;
        }

        if (value is DateTime dateTime)
        {
            return DateOnly.FromDateTime(dateTime);
        }

        return DateOnly.TryParse(
            value?.ToString(),
            out var parsed)
            ? parsed
            : null;
    }

    private static DateOnly? GetNullableDateOnly(
        object source,
        params string[] names) =>
        GetDateOnly(source, names);

    private sealed record RoomSnapshot(
        Guid Id,
        string Name);

    private sealed record ReadingValueSnapshot(
        string ZoneCode,
        decimal Value,
        string UnitCode);

    private sealed record RateSnapshot(
        decimal Rate,
        string Source);

    private sealed record SubmeterSnapshot(
        Guid MeterId,
        string MeterName,
        string Medium,
        string UnitCode,
        Guid RoomId,
        string RoomName,
        string TenantName,
        Guid LeaseContractId,
        string PeriodKey,
        Guid PreviousReadingId,
        Guid CurrentReadingId,
        DateTime PreviousReadingAtUtc,
        DateTime CurrentReadingAtUtc,
        string ZoneCode,
        decimal PreviousValue,
        decimal CurrentValue,
        decimal Consumption,
        Guid ParentMeterId,
        string ParentMeterName,
        decimal? RecommendedRatePerUnit,
        string RateSource,
        decimal? RecommendedDistributionRatePerUnit,
        string DistributionRateSource,
        bool AlreadyGenerated,
        decimal GeneratedRatePerUnit,
        long GeneratedAmountMinor,
        decimal GeneratedDistributionRatePerUnit,
        long GeneratedDistributionAmountMinor,
        bool CanGenerate,
        string? BlockReason)
    {
        public static SubmeterSnapshot Blocked(
            Guid meterId,
            string meterName,
            string medium,
            string unitCode,
            Guid roomId,
            string roomName,
            string reason) =>
            new(
                meterId,
                meterName,
                medium,
                unitCode,
                roomId,
                roomName,
                "",
                Guid.Empty,
                DateTime.Today.ToString("yyyy-MM"),
                Guid.Empty,
                Guid.Empty,
                DateTime.MinValue,
                DateTime.MinValue,
                "ALL",
                0m,
                0m,
                0m,
                Guid.Empty,
                "",
                null,
                "",
                null,
                "",
                false,
                0m,
                0,
                0m,
                0,
                false,
                reason);
    }

    private sealed class ReferenceComparer :
        IEqualityComparer<object>
    {
        public static readonly ReferenceComparer Instance =
            new();

        public new bool Equals(
            object? x,
            object? y) =>
            ReferenceEquals(x, y);

        public int GetHashCode(
            object obj) =>
            RuntimeHelpers.GetHashCode(obj);
    }
}
