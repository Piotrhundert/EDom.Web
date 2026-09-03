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
                recommendedRatePerUnit = snapshot.RecommendedRatePerUnit,
                rateSource = snapshot.RateSource,
                alreadyGenerated = snapshot.AlreadyGenerated,
                generatedAmountMinor = snapshot.GeneratedAmountMinor,
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
        decimal ratePerUnit,
        CancellationToken cancellationToken)
    {
        var current = await access.GetCurrentAsync(cancellationToken);
        if (current is null)
        {
            return Unauthorized();
        }

        if (ratePerUnit <= 0m)
        {
            return BadRequest(new
            {
                message =
                    "Stawka za jednostkę musi być większa od 0."
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

        if (snapshot.AlreadyGenerated)
        {
            return BadRequest(new
            {
                message =
                    "Ten odczyt podlicznika został już dodany do rozliczenia lokatora. Nie utworzono duplikatu."
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

        var amountMinor =
            checked(
                (long)Math.Round(
                    snapshot.Consumption
                    * ratePerUnit
                    * 100m,
                    0,
                    MidpointRounding.AwayFromZero));

        if (amountMinor <= 0)
        {
            return BadRequest(new
            {
                message =
                    "Wyliczona kwota wynosi 0. Sprawdź odczyty i stawkę."
            });
        }

        var lineType = snapshot.Medium switch
        {
            "Electricity" => "Electricity",
            "Water" => "Water",
            "Gas" => "Gas",
            _ => "Adjustment"
        };

        var audit = JsonSerializer.Serialize(new
        {
            source = "SubmeterReading",
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
            ratePerUnit,
            rateSource =
                snapshot.RecommendedRatePerUnit.HasValue
                && snapshot.RecommendedRatePerUnit.Value == ratePerUnit
                    ? snapshot.RateSource
                    : "ManualOverride",
            amountMinor,
            currencyCode = currency
        });

        await settlementService.AddManualLineAsync(
            rentalActor,
            new(
                settlementId,
                lineType,
                amountMinor,
                currency,
                "SubmeterReading",
                null,
                audit),
            cancellationToken);

        await store.AddAsync(
            new SubmeterTenantChargeRecord
            {
                Id = Guid.NewGuid(),
                HouseholdId =
                    current.HouseholdId,
                MeterId =
                    snapshot.MeterId,
                PreviousReadingId =
                    snapshot.PreviousReadingId,
                CurrentReadingId =
                    snapshot.CurrentReadingId,
                LeaseContractId =
                    snapshot.LeaseContractId,
                SettlementId =
                    settlementId,
                RoomId =
                    snapshot.RoomId,
                RoomName =
                    snapshot.RoomName,
                TenantName =
                    snapshot.TenantName,
                PeriodKey =
                    periodKey,
                Medium =
                    snapshot.Medium,
                ZoneCode =
                    snapshot.ZoneCode,
                UnitCode =
                    snapshot.UnitCode,
                PreviousValue =
                    snapshot.PreviousValue,
                CurrentValue =
                    snapshot.CurrentValue,
                Consumption =
                    snapshot.Consumption,
                RatePerUnit =
                    ratePerUnit,
                AmountMinor =
                    amountMinor,
                CurrencyCode =
                    currency,
                RateSource =
                    snapshot.RecommendedRatePerUnit.HasValue
                    && snapshot.RecommendedRatePerUnit.Value == ratePerUnit
                        ? snapshot.RateSource
                        : "ManualOverride",
                CreatedAtUtc =
                    DateTime.UtcNow,
                CreatedByUserAccountId =
                    current.UserAccountId
            },
            cancellationToken);

        return Json(new
        {
            ok = true,
            settlementId,
            amountMinor,
            currencyCode = currency,
            message =
                $"Dodano do rozliczenia {snapshot.TenantName} za {periodKey}: " +
                $"{snapshot.Consumption:N3} {snapshot.UnitCode} × {ratePerUnit:N4} {currency}/{snapshot.UnitCode} " +
                $"= {amountMinor / 100m:N2} {currency}."
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
                        currentValue.Value.ZoneCode));

        if (previousReading is null)
        {
            return SubmeterSnapshot.Blocked(
                meterId,
                meterName,
                medium,
                unitCode,
                roomId,
                roomName,
                $"Brak wcześniejszego zatwierdzonego odczytu dla strefy {currentValue.Value.ZoneCode}.");
        }

        var previousValue =
            ReadValue(
                utilities,
                previousReading.Id,
                currentValue.Value.ZoneCode);

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
            currentValue.Value.Value
            - previousValue.Value.Value;

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

        var contract = rental.Contracts
            .Where(x =>
                x.Status == LeaseStatuses.Signed
                && x.LeaseFrom <= readingDate
                && (!x.LeaseTo.HasValue
                    || x.LeaseTo.Value >= readingDate))
            .FirstOrDefault(x =>
            {
                var contractRoomId =
                    GetGuid(x, "RoomId");

                return contractRoomId == roomId
                       || string.Equals(
                           x.RoomName,
                           roomName,
                           StringComparison.OrdinalIgnoreCase);
            });

        var recommendedRate =
            ResolveRecommendedRate(
                utilities,
                unitCode,
                currentValue.Value.ZoneCode,
                readingDate);

        var alreadyGenerated =
            generated.FirstOrDefault(x =>
                x.MeterId == meterId
                && x.CurrentReadingId == currentReading.Id);

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
                contract?.TenantName ?? "",
            LeaseContractId:
                contract?.ContractId ?? Guid.Empty,
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
                currentValue.Value.ZoneCode,
            PreviousValue:
                previousValue.Value.Value,
            CurrentValue:
                currentValue.Value.Value,
            Consumption:
                consumption,
            RecommendedRatePerUnit:
                recommendedRate?.Rate,
            RateSource:
                recommendedRate?.Source ?? "Brak automatycznej stawki",
            AlreadyGenerated:
                alreadyGenerated is not null,
            GeneratedAmountMinor:
                alreadyGenerated?.AmountMinor ?? 0,
            CanGenerate:
                contract is not null
                && consumption >= 0m
                && alreadyGenerated is null,
            BlockReason:
                contract is null
                    ? "Brak aktywnej umowy lokatora dla tego pokoju w dniu odczytu."
                    : alreadyGenerated is not null
                        ? "Ten odczyt został już rozliczony."
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

    private static RateSnapshot? ResolveRecommendedRate(
        object overview,
        string unitCode,
        string zoneCode,
        DateOnly date)
    {
        var candidates =
            new List<(int Score, decimal Rate, string Source)>();

        foreach (var item in WalkObjectGraph(
                     overview,
                     maxDepth: 4))
        {
            var typeName =
                item.GetType().Name;

            var hasRate =
                HasProperty(item, "RatePerUnit")
                || HasProperty(item, "RateScaled")
                || HasProperty(item, "UnitRate")
                || HasProperty(item, "RateMinorPerUnit");

            if (!hasRate
                && !typeName.Contains(
                    "Rate",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var rate =
                TryReadRate(item);

            if (!rate.HasValue
                || rate.Value <= 0m)
            {
                continue;
            }

            var candidateUnit =
                GetString(item, "UnitCode");

            if (!string.IsNullOrWhiteSpace(candidateUnit)
                && !string.Equals(
                    candidateUnit,
                    unitCode,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var validFrom =
                GetDateOnly(item, "ValidFrom");

            var validTo =
                GetNullableDateOnly(item, "ValidTo");

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
                GetString(item, "ZoneCode");

            if (!string.IsNullOrWhiteSpace(candidateZone)
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

            var score = 0;

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
                score += 10;
            }
            else if (string.Equals(
                         candidateZone,
                         "ALL",
                         StringComparison.OrdinalIgnoreCase))
            {
                score += 5;
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
                score += 10;
            }

            if (validFrom.HasValue)
            {
                score += 2;
            }

            candidates.Add(
                (
                    score,
                    rate.Value,
                    $"{typeName}{(string.IsNullOrWhiteSpace(component) ? "" : $" · {component}")}"
                ));
        }

        var best = candidates
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Rate)
            .FirstOrDefault();

        return best.Rate > 0m
            ? new RateSnapshot(
                best.Rate,
                best.Source)
            : null;
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
        decimal? RecommendedRatePerUnit,
        string RateSource,
        bool AlreadyGenerated,
        long GeneratedAmountMinor,
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
                null,
                "",
                false,
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
