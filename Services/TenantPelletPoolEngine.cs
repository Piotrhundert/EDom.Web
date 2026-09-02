using System.Collections;
using System.Reflection;
using System.Text.Json;
using EDom.Application.Property;
using EDom.Application.Rental;
using EDom.Domain.Rental;
using EDom.Web.Models;

namespace EDom.Web.Services;

public sealed class TenantPelletPoolEngine(
    ITenantSettlementService settlementService,
    IRentalService rentalService,
    IPropertyAssetService propertyService,
    string contentRootPath)
{
    private readonly TenantPelletPoolStore store =
        new(contentRootPath);

    public async Task<long> ApplyAsync(
        RentalActor rentalActor,
        Guid householdId,
        Guid leaseContractId,
        string periodKey,
        CancellationToken cancellationToken)
    {
        if (!TryParseMonth(periodKey, out var monthStart, out var monthEnd))
        {
            return 0;
        }

        var settlementOverview = await settlementService.GetOverviewAsync(
            rentalActor,
            cancellationToken);

        var settlement = FindSettlement(
            settlementOverview,
            leaseContractId,
            periodKey);

        if (settlement is null)
        {
            return 0;
        }

        var settlementId = GetGuid(settlement, "Id");
        if (settlementId == Guid.Empty)
        {
            return 0;
        }

        var settlementStatus = GetString(settlement, "Status");
        if (settlementStatus is not (
            TenantSettlementStatuses.Draft
            or TenantSettlementStatuses.AwaitingData
            or TenantSettlementStatuses.ReadyForApproval))
        {
            return 0;
        }

        var rentalOverview = await rentalService.GetOverviewAsync(
            rentalActor,
            cancellationToken);

        var contract = AsObjects(GetValue(rentalOverview, "Contracts"))
            .FirstOrDefault(x =>
                GetGuid(x, "ContractId", "Id") == leaseContractId);

        if (contract is null)
        {
            return 0;
        }

        var propertyActor = new PropertyActor(
            rentalActor.AccountId,
            rentalActor.PersonId,
            rentalActor.HouseholdId,
            rentalActor.CorrelationId,
            rentalActor.NowUtc);

        var propertyOverview = await propertyService.GetOverviewAsync(
            propertyActor,
            cancellationToken);

        var buildingId = ResolveBuildingId(
            contract,
            propertyOverview);

        if (buildingId == Guid.Empty)
        {
            return 0;
        }

        var data = await store.GetAsync(
            householdId,
            cancellationToken);

        var pool = data.Pools
            .Where(x =>
                x.BuildingId == buildingId
                && x.Status == TenantPelletPoolStatuses.Active
                && x.PeriodFrom <= monthEnd
                && x.PeriodTo >= monthStart)
            .OrderBy(x => x.PeriodFrom)
            .ThenBy(x => x.CreatedAtUtc)
            .FirstOrDefault();

        if (pool is null)
        {
            return 0;
        }

        var participants = ResolveParticipants(
            rentalOverview,
            propertyOverview,
            buildingId,
            monthStart,
            monthEnd);

        if (participants.Count == 0)
        {
            return 0;
        }

        var plan = await store.GetOrCreatePlanAsync(
            householdId,
            pool.Id,
            periodKey,
            scoped => BuildPlan(
                householdId,
                pool,
                periodKey,
                monthStart,
                participants,
                scoped),
            cancellationToken);

        var share = plan.Shares.FirstOrDefault(x =>
            x.LeaseContractId == leaseContractId);

        if (share is null || share.AmountMinor <= 0)
        {
            return 0;
        }

        var existingApplication = await store.GetApplicationAsync(
            householdId,
            pool.Id,
            settlementId,
            cancellationToken);

        var lineExists = SettlementContainsPoolLine(
            settlement,
            pool.Id);

        var amountToUse = existingApplication?.AmountMinor
            ?? share.AmountMinor;

        if (!lineExists)
        {
            var snapshot = JsonSerializer.Serialize(new
            {
                kind = "TenantPelletAnnualPool",
                poolId = pool.Id,
                planId = plan.Id,
                pool.BuildingId,
                pool.BuildingName,
                pool.SeasonName,
                periodKey,
                tenantCount = plan.TenantCount,
                poolTotalMinor = pool.TotalAmountMinor,
                poolAllocatedBeforeMinor = plan.PoolAllocatedBeforeMinor,
                poolRemainingBeforeMinor = plan.PoolRemainingBeforeMinor,
                monthsRemaining = plan.MonthsRemaining,
                monthlyBudgetMinor = plan.MonthlyBudgetMinor,
                tenantShareMinor = amountToUse
            });

            await settlementService.AddManualLineAsync(
                rentalActor,
                new(
                    settlementId,
                    TenantSettlementLineTypes.Pellet,
                    amountToUse,
                    pool.CurrencyCode,
                    "PelletAnnualPool",
                    null,
                    snapshot),
                cancellationToken);
        }

        if (existingApplication is null)
        {
            await store.AddApplicationAsync(
                new TenantPelletApplicationRecord
                {
                    Id = Guid.NewGuid(),
                    HouseholdId = householdId,
                    PoolId = pool.Id,
                    PlanId = plan.Id,
                    SettlementId = settlementId,
                    LeaseContractId = leaseContractId,
                    PeriodKey = periodKey,
                    TenantName = GetString(contract, "TenantName"),
                    RoomName = GetString(contract, "RoomName"),
                    AmountMinor = amountToUse,
                    CurrencyCode = pool.CurrencyCode,
                    AppliedAtUtc = DateTime.UtcNow
                },
                cancellationToken);
        }

        return amountToUse;
    }

    public async Task<TenantPelletInvoiceReconcileResult> ReconcileInvoicePurchaseAsync(
        RentalActor rentalActor,
        Guid householdId,
        TenantPelletPoolUpsertResult upsert,
        CancellationToken cancellationToken)
    {
        var purchase = upsert.Purchase;
        var pool = upsert.Pool;

        var settlementOverview = await settlementService.GetOverviewAsync(
            rentalActor,
            cancellationToken);

        var rentalOverview = await rentalService.GetOverviewAsync(
            rentalActor,
            cancellationToken);

        var propertyActor = new PropertyActor(
            rentalActor.AccountId,
            rentalActor.PersonId,
            rentalActor.HouseholdId,
            rentalActor.CorrelationId,
            rentalActor.NowUtc);

        var propertyOverview = await propertyService.GetOverviewAsync(
            propertyActor,
            cancellationToken);

        var months = EnumerateMonths(
            purchase.PeriodFrom,
            purchase.PeriodTo)
            .ToArray();

        if (months.Length == 0)
            return new(0, 0, 0, 0);

        var baseMonthAmount = purchase.AmountMinor / months.Length;
        var monthRemainder = purchase.AmountMinor % months.Length;

        var correctionCount = 0;
        var openLineCount = 0;
        long correctionAmount = 0;
        long openLineAmount = 0;

        for (var monthIndex = 0; monthIndex < months.Length; monthIndex++)
        {
            var monthStart = months[monthIndex];
            var monthEnd = new DateOnly(
                monthStart.Year,
                monthStart.Month,
                DateTime.DaysInMonth(monthStart.Year, monthStart.Month));
            var periodKey = $"{monthStart:yyyy-MM}";

            var participants = ResolveParticipants(
                rentalOverview,
                propertyOverview,
                pool.BuildingId,
                monthStart,
                monthEnd)
                .OrderBy(x => x.LeaseContractId)
                .ToArray();

            if (participants.Length == 0)
                continue;

            var monthAmount = baseMonthAmount
                + (monthIndex < monthRemainder ? 1 : 0);
            var baseShare = monthAmount / participants.Length;
            var shareRemainder = monthAmount % participants.Length;

            for (var participantIndex = 0;
                 participantIndex < participants.Length;
                 participantIndex++)
            {
                var participant = participants[participantIndex];

                var settlement = FindSettlement(
                    settlementOverview,
                    participant.LeaseContractId,
                    periodKey);

                if (settlement is null)
                    continue;

                var settlementId = GetGuid(settlement, "Id");
                if (settlementId == Guid.Empty)
                    continue;

                if (await store.HasInvoiceAdjustmentAsync(
                        householdId,
                        purchase.Id,
                        settlementId,
                        cancellationToken))
                    continue;

                var desiredShare = baseShare
                    + (participantIndex < shareRemainder ? 1 : 0);

                if (desiredShare <= 0)
                    continue;

                var existingApplication = await store.GetApplicationAsync(
                    householdId,
                    pool.Id,
                    settlementId,
                    cancellationToken);

                var amountToAdd = desiredShare;

                // Jeżeli użytkownik wcześniej utworzył ręcznie pulę dokładnie
                // z tej samej faktury, nie naliczamy drugi raz kwoty już
                // przypisanej do tego rozliczenia.
                if (purchase.LinkedToExistingManualPool
                    && existingApplication is not null)
                {
                    amountToAdd = Math.Max(
                        0,
                        desiredShare - existingApplication.AmountMinor);
                }

                var status = GetString(settlement, "Status");

                if (amountToAdd <= 0)
                {
                    await store.AddInvoiceAdjustmentAsync(
                        new TenantPelletInvoiceAdjustmentRecord
                        {
                            Id = Guid.NewGuid(),
                            HouseholdId = householdId,
                            PoolId = pool.Id,
                            PurchaseId = purchase.Id,
                            SettlementId = settlementId,
                            LeaseContractId = participant.LeaseContractId,
                            PeriodKey = periodKey,
                            AmountMinor = 0,
                            Mode = "AlreadyIncluded",
                            StatusBefore = status,
                            CreatedAtUtc = DateTime.UtcNow
                        },
                        cancellationToken);
                    continue;
                }

                var isEditable = status is
                    TenantSettlementStatuses.Draft
                    or TenantSettlementStatuses.AwaitingData
                    or TenantSettlementStatuses.ReadyForApproval;

                if (isEditable)
                {
                    var snapshot = JsonSerializer.Serialize(new
                    {
                        kind = "PelletInvoice",
                        poolId = pool.Id,
                        purchaseId = purchase.Id,
                        purchase.SourceInvoiceNo,
                        purchase.Supplier,
                        periodKey,
                        tenantCount = participants.Length,
                        purchaseAmountMinor = purchase.AmountMinor,
                        monthAmountMinor = monthAmount,
                        tenantShareMinor = amountToAdd
                    });

                    await settlementService.AddManualLineAsync(
                        rentalActor,
                        new(
                            settlementId,
                            TenantSettlementLineTypes.Pellet,
                            amountToAdd,
                            purchase.CurrencyCode,
                            "PelletInvoice",
                            null,
                            snapshot),
                        cancellationToken);

                    openLineCount++;
                    openLineAmount = checked(openLineAmount + amountToAdd);
                }
                else
                {
                    var reason =
                        $"Pellet — korekta z faktury {purchase.SourceInvoiceNo} ({purchase.Supplier}). " +
                        $"Rozliczenie {periodKey} było już zatwierdzone albo opublikowane. " +
                        $"Dodatkowy udział kosztu pelletu: {amountToAdd / 100m:N2} {purchase.CurrencyCode}.";

                    await settlementService.CorrectSettlementAsync(
                        rentalActor,
                        new(
                            settlementId,
                            amountToAdd,
                            reason),
                        cancellationToken);

                    correctionCount++;
                    correctionAmount = checked(correctionAmount + amountToAdd);
                }

                await store.AddToApplicationAsync(
                    new TenantPelletApplicationRecord
                    {
                        Id = Guid.NewGuid(),
                        HouseholdId = householdId,
                        PoolId = pool.Id,
                        PlanId = Guid.Empty,
                        SettlementId = settlementId,
                        LeaseContractId = participant.LeaseContractId,
                        PeriodKey = periodKey,
                        TenantName = participant.TenantName,
                        RoomName = participant.RoomName,
                        CurrencyCode = purchase.CurrencyCode,
                        AppliedAtUtc = DateTime.UtcNow
                    },
                    amountToAdd,
                    cancellationToken);

                await store.AddInvoiceAdjustmentAsync(
                    new TenantPelletInvoiceAdjustmentRecord
                    {
                        Id = Guid.NewGuid(),
                        HouseholdId = householdId,
                        PoolId = pool.Id,
                        PurchaseId = purchase.Id,
                        SettlementId = settlementId,
                        LeaseContractId = participant.LeaseContractId,
                        PeriodKey = periodKey,
                        AmountMinor = amountToAdd,
                        Mode = isEditable ? "OpenLine" : "Correction",
                        StatusBefore = status,
                        CreatedAtUtc = DateTime.UtcNow
                    },
                    cancellationToken);
            }
        }

        return new(
            correctionCount,
            openLineCount,
            correctionAmount,
            openLineAmount);
    }

    public async Task<object> BuildDataAsync(
        RentalActor rentalActor,
        Guid householdId,
        CancellationToken cancellationToken)
    {
        var settlementOverview = await settlementService.GetOverviewAsync(
            rentalActor,
            cancellationToken);

        var rentalOverview = await rentalService.GetOverviewAsync(
            rentalActor,
            cancellationToken);

        var propertyActor = new PropertyActor(
            rentalActor.AccountId,
            rentalActor.PersonId,
            rentalActor.HouseholdId,
            rentalActor.CorrelationId,
            rentalActor.NowUtc);

        var propertyOverview = await propertyService.GetOverviewAsync(
            propertyActor,
            cancellationToken);

        var data = await store.GetAsync(
            householdId,
            cancellationToken);

        var buildings = propertyOverview.Buildings
            .Where(building =>
                propertyOverview.Rooms.Any(room =>
                    room.BuildingId == building.Id
                    && room.IsRentable))
            .OrderBy(x => x.Name)
            .Select(x => new
            {
                id = x.Id,
                name = x.Name,
                rentableRooms = propertyOverview.Rooms.Count(room =>
                    room.BuildingId == x.Id
                    && room.IsRentable)
            })
            .ToArray();

        var pools = data.Pools
            .OrderByDescending(x => x.PeriodFrom)
            .Select(pool =>
            {
                var applications = data.Applications
                    .Where(x => x.PoolId == pool.Id)
                    .ToArray();

                var allocatedMinor = applications.Sum(x => x.AmountMinor);

                var paidMinor = applications.Sum(app =>
                {
                    var settlement = FindSettlementById(
                        settlementOverview,
                        app.SettlementId);

                    if (settlement is null)
                    {
                        return 0L;
                    }

                    var paid = GetLong(
                        settlement,
                        "PaidMinor");

                    var due = GetLong(
                        settlement,
                        "TotalDueMinor");

                    var status = GetString(
                        settlement,
                        "Status");

                    var fullyPaid = status is
                        TenantSettlementStatuses.Paid
                        or TenantSettlementStatuses.PaidLate
                        || (due > 0 && paid >= due);

                    return fullyPaid
                        ? app.AmountMinor
                        : 0L;
                });

                var remainingMinor = Math.Max(
                    0,
                    pool.TotalAmountMinor - allocatedMinor);

                var currentPeriod = DateTime.Today.ToString("yyyy-MM");
                var currentPlan = data.Plans.FirstOrDefault(x =>
                    x.PoolId == pool.Id
                    && string.Equals(
                        x.PeriodKey,
                        currentPeriod,
                        StringComparison.OrdinalIgnoreCase));

                var currentTenantCount = currentPlan?.TenantCount
                    ?? ResolveParticipants(
                        rentalOverview,
                        propertyOverview,
                        pool.BuildingId,
                        new DateOnly(DateTime.Today.Year, DateTime.Today.Month, 1),
                        new DateOnly(
                            DateTime.Today.Year,
                            DateTime.Today.Month,
                            DateTime.DaysInMonth(DateTime.Today.Year, DateTime.Today.Month)))
                        .Count;

                return new
                {
                    pool.Id,
                    pool.BuildingId,
                    pool.BuildingName,
                    pool.SeasonName,
                    pool.PeriodFrom,
                    pool.PeriodTo,
                    pool.PurchaseDate,
                    pool.TotalAmountMinor,
                    pool.CurrencyCode,
                    pool.PalletCount,
                    pool.WeightKg,
                    pool.Supplier,
                    pool.DocumentNo,
                    pool.Notes,
                    pool.Status,
                    purchaseCount = data.Purchases.Count(x => x.PoolId == pool.Id),
                    correctionCount = data.InvoiceAdjustments.Count(x => x.PoolId == pool.Id && x.Mode == "Correction"),
                    correctionAmountMinor = data.InvoiceAdjustments
                        .Where(x => x.PoolId == pool.Id && x.Mode == "Correction")
                        .Sum(x => x.AmountMinor),
                    allocatedMinor,
                    paidMinor,
                    remainingMinor,
                    currentTenantCount,
                    currentMonthlyBudgetMinor = currentPlan?.MonthlyBudgetMinor,
                    currentPerTenantMinor = currentPlan is null
                        || currentPlan.TenantCount <= 0
                        ? (long?)null
                        : currentPlan.MonthlyBudgetMinor
                          / currentPlan.TenantCount,
                    plans = data.Plans
                        .Where(x => x.PoolId == pool.Id)
                        .OrderByDescending(x => x.PeriodKey)
                        .Take(12)
                        .Select(x => new
                        {
                            x.Id,
                            x.PeriodKey,
                            x.PoolRemainingBeforeMinor,
                            x.MonthsRemaining,
                            x.MonthlyBudgetMinor,
                            x.TenantCount,
                            x.Shares
                        })
                };
            })
            .ToArray();

        return new
        {
            buildings,
            pools
        };
    }

    private static TenantPelletMonthPlanRecord BuildPlan(
        Guid householdId,
        TenantPelletPoolRecord pool,
        string periodKey,
        DateOnly monthStart,
        IReadOnlyList<Participant> participants,
        TenantPelletPoolData data)
    {
        var allocatedBefore = data.Applications
            .Where(x =>
                x.PoolId == pool.Id
                && string.Compare(
                    x.PeriodKey,
                    periodKey,
                    StringComparison.OrdinalIgnoreCase) < 0)
            .Sum(x => x.AmountMinor);

        var remainingBefore = Math.Max(
            0,
            pool.TotalAmountMinor - allocatedBefore);

        var monthsRemaining = CountMonthsInclusive(
            monthStart,
            pool.PeriodTo);

        if (monthsRemaining <= 0)
        {
            monthsRemaining = 1;
        }

        var monthlyBudget = monthsRemaining == 1
            ? remainingBefore
            : remainingBefore / monthsRemaining;

        if (monthlyBudget <= 0 || participants.Count == 0)
        {
            monthlyBudget = 0;
        }

        var baseShare = participants.Count == 0
            ? 0
            : monthlyBudget / participants.Count;

        var remainder = participants.Count == 0
            ? 0
            : monthlyBudget % participants.Count;

        var shares = participants
            .OrderBy(x => x.LeaseContractId)
            .Select((x, index) => new TenantPelletPlanShareRecord
            {
                LeaseContractId = x.LeaseContractId,
                TenantName = x.TenantName,
                RoomName = x.RoomName,
                AmountMinor = baseShare + (index < remainder ? 1 : 0)
            })
            .ToList();

        return new TenantPelletMonthPlanRecord
        {
            Id = Guid.NewGuid(),
            HouseholdId = householdId,
            PoolId = pool.Id,
            PeriodKey = periodKey,
            PoolAllocatedBeforeMinor = allocatedBefore,
            PoolRemainingBeforeMinor = remainingBefore,
            MonthsRemaining = monthsRemaining,
            MonthlyBudgetMinor = monthlyBudget,
            TenantCount = participants.Count,
            Shares = shares,
            CreatedAtUtc = DateTime.UtcNow
        };
    }

    private static List<Participant> ResolveParticipants(
        object rentalOverview,
        PropertyOverview propertyOverview,
        Guid buildingId,
        DateOnly monthStart,
        DateOnly monthEnd)
    {
        var result = new List<Participant>();

        foreach (var contract in AsObjects(GetValue(rentalOverview, "Contracts")))
        {
            var contractId = GetGuid(
                contract,
                "ContractId",
                "Id");

            if (contractId == Guid.Empty)
            {
                continue;
            }

            var status = GetString(contract, "Status");
            if (!string.Equals(
                    status,
                    LeaseStatuses.Signed,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var leaseFrom = GetDateOnly(
                contract,
                "LeaseFrom");

            var leaseTo = GetNullableDateOnly(
                contract,
                "LeaseTo");

            if (leaseFrom.HasValue
                && leaseFrom.Value > monthEnd)
            {
                continue;
            }

            if (leaseTo.HasValue
                && leaseTo.Value < monthStart)
            {
                continue;
            }

            var resolvedBuildingId = ResolveBuildingId(
                contract,
                propertyOverview);

            if (resolvedBuildingId != buildingId)
            {
                continue;
            }

            result.Add(new Participant(
                contractId,
                GetString(contract, "TenantName"),
                GetString(contract, "RoomName")));
        }

        return result;
    }

    private static Guid ResolveBuildingId(
        object contract,
        PropertyOverview propertyOverview)
    {
        var roomId = GetGuid(
            contract,
            "RoomId",
            "RentalRoomId");

        if (roomId != Guid.Empty)
        {
            var room = propertyOverview.Rooms.FirstOrDefault(x =>
                x.Id == roomId);

            if (room is not null)
            {
                return room.BuildingId;
            }
        }

        var roomName = GetString(contract, "RoomName");
        if (string.IsNullOrWhiteSpace(roomName))
        {
            return Guid.Empty;
        }

        var candidates = propertyOverview.Rooms
            .Where(x =>
                string.Equals(
                    x.Name,
                    roomName,
                    StringComparison.OrdinalIgnoreCase))
            .ToArray();

        return candidates.Length == 1
            ? candidates[0].BuildingId
            : Guid.Empty;
    }

    private static object? FindSettlement(
        object overview,
        Guid leaseContractId,
        string periodKey)
    {
        var settlements = AsObjects(GetValue(
            overview,
            "Settlements"));

        foreach (var settlement in settlements)
        {
            if (!string.Equals(
                    GetString(settlement, "PeriodKey"),
                    periodKey,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var direct = GetGuid(
                settlement,
                "LeaseContractId",
                "ContractId");

            if (direct == leaseContractId)
            {
                return settlement;
            }
        }

        var contract = AsObjects(GetValue(
                overview,
                "Contracts"))
            .FirstOrDefault(x =>
                GetGuid(
                    x,
                    "ContractId",
                    "Id") == leaseContractId);

        if (contract is null)
        {
            return null;
        }

        var tenantName = GetString(
            contract,
            "TenantName");

        var roomName = GetString(
            contract,
            "RoomName");

        return settlements.FirstOrDefault(x =>
            string.Equals(
                GetString(x, "PeriodKey"),
                periodKey,
                StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                GetString(x, "TenantName"),
                tenantName,
                StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                GetString(x, "RoomName"),
                roomName,
                StringComparison.OrdinalIgnoreCase));
    }

    private static object? FindSettlementById(
        object overview,
        Guid settlementId) =>
        AsObjects(GetValue(
                overview,
                "Settlements"))
            .FirstOrDefault(x =>
                GetGuid(x, "Id") == settlementId);

    private static bool SettlementContainsPoolLine(
        object settlement,
        Guid poolId)
    {
        var token = poolId.ToString("D");

        foreach (var line in AsObjects(GetValue(
                     settlement,
                     "Lines")))
        {
            if (!string.Equals(
                    GetString(line, "SourceType"),
                    "PelletAnnualPool",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var propertyName in new[]
                     {
                         "CalculationSnapshotJson",
                         "CalculationSnapshot",
                         "SnapshotJson",
                         "Snapshot"
                     })
            {
                var raw = GetValue(
                    line,
                    propertyName)?.ToString();

                if (!string.IsNullOrWhiteSpace(raw)
                    && raw.Contains(
                        token,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return true;
        }

        return false;
    }

    private static IEnumerable<DateOnly> EnumerateMonths(
        DateOnly from,
        DateOnly to)
    {
        var current = new DateOnly(from.Year, from.Month, 1);
        var last = new DateOnly(to.Year, to.Month, 1);

        while (current <= last)
        {
            yield return current;
            current = current.AddMonths(1);
        }
    }

    private static int CountMonthsInclusive(
        DateOnly from,
        DateOnly to)
    {
        var first = new DateOnly(
            from.Year,
            from.Month,
            1);

        var last = new DateOnly(
            to.Year,
            to.Month,
            1);

        if (last < first)
        {
            return 0;
        }

        return ((last.Year - first.Year) * 12)
               + last.Month
               - first.Month
               + 1;
    }

    private static bool TryParseMonth(
        string periodKey,
        out DateOnly monthStart,
        out DateOnly monthEnd)
    {
        monthStart = default;
        monthEnd = default;

        if (string.IsNullOrWhiteSpace(periodKey)
            || periodKey.Length != 7
            || periodKey[4] != '-'
            || !int.TryParse(periodKey[..4], out var year)
            || !int.TryParse(periodKey[5..], out var month)
            || month is < 1 or > 12)
        {
            return false;
        }

        monthStart = new DateOnly(
            year,
            month,
            1);

        monthEnd = new DateOnly(
            year,
            month,
            DateTime.DaysInMonth(year, month));

        return true;
    }

    private static IEnumerable<object> AsObjects(
        object? value)
    {
        if (value is not IEnumerable enumerable)
        {
            yield break;
        }

        foreach (var item in enumerable)
        {
            if (item is not null)
            {
                yield return item;
            }
        }
    }

    private static object? GetValue(
        object? source,
        params string[] names)
    {
        if (source is null)
        {
            return null;
        }

        var type = source.GetType();

        foreach (var name in names)
        {
            var property = type.GetProperty(
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
        var value = GetValue(
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

    private static long GetLong(
        object source,
        params string[] names)
    {
        var value = GetValue(
            source,
            names);

        if (value is null)
        {
            return 0;
        }

        try
        {
            return Convert.ToInt64(value);
        }
        catch
        {
            return 0;
        }
    }

    private static DateOnly? GetDateOnly(
        object source,
        params string[] names) =>
        GetNullableDateOnly(
            source,
            names);

    private static DateOnly? GetNullableDateOnly(
        object source,
        params string[] names)
    {
        var value = GetValue(
            source,
            names);

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

    private sealed record Participant(
        Guid LeaseContractId,
        string TenantName,
        string RoomName);
}
