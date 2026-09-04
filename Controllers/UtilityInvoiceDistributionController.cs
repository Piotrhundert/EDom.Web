using System.Reflection;
using System.Text.Json;
using EDom.Application.Households;
using EDom.Application.Rental;
using EDom.Application.Utilities;
using EDom.Domain.Authorization;
using EDom.Domain.Utilities;
using EDom.Infrastructure.Persistence;
using EDom.Web.Authorization;
using EDom.Web.Infrastructure;
using EDom.Web.Models;
using EDom.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EDom.Web.Controllers;

[Authorize]
[Route("Utilities/InvoiceDistribution")]
public sealed class UtilityInvoiceDistributionController(
    WebAccessService access,
    IUtilitiesService utilitiesService,
    IRentalService rentalService,
    ITenantSettlementService settlementService,
    IHouseholdFamilyService familyService,
    EDomDbContext db,
    IWebHostEnvironment environment) : Controller
{
    private const string PackageVersion =
        "PKG-015q-FEAT-02";

    [HttpGet("Data")]
    public async Task<IActionResult> Data(
        CancellationToken cancellationToken)
    {
        var current =
            await access.GetCurrentAsync(
                cancellationToken);

        if (current is null)
        {
            return Unauthorized();
        }

        if (!await CanManageAsync(
                current.HouseholdId,
                cancellationToken))
        {
            return Forbid();
        }

        var utilityActor =
            CreateUtilityActor(current);

        var rentalActor =
            CreateRentalActor(current);

        var utilities =
            await utilitiesService.GetOverviewAsync(
                utilityActor,
                cancellationToken);

        var rental =
            await rentalService.GetOverviewAsync(
                rentalActor,
                cancellationToken);

        var family =
            await familyService.GetOverviewAsync(
                current.HouseholdId,
                cancellationToken);

        var buildings =
            await db.Buildings
                .AsNoTracking()
                .ToDictionaryAsync(
                    x => x.Id,
                    x => x.Name,
                    cancellationToken);

        var contracts =
            utilities.Contracts
                .Where(x =>
                    x.Medium is
                        "Water"
                        or "Gas"
                        or "Waste")
                .OrderBy(x =>
                    x.Medium)
                .ThenBy(x =>
                    x.OperatorName)
                .Select(x => new
                {
                    x.Id,
                    x.Medium,
                    x.OperatorName,
                    x.ContractNumber,
                    label =
                        $"{MediumLabel(x.Medium)} · {x.OperatorName}" +
                        (string.IsNullOrWhiteSpace(
                            x.ContractNumber)
                            ? ""
                            : $" · {x.ContractNumber}")
                })
                .ToArray();

        var tenants =
            rental.Contracts
                .Select(x => new
                {
                    contractId =
                        GetGuid(
                            x,
                            "ContractId",
                            "Id"),
                    tenantName =
                        GetString(
                            x,
                            "TenantName",
                            "Lokator"),
                    roomName =
                        GetString(
                            x,
                            "RoomName",
                            "Pokój"),
                    status =
                        GetString(
                            x,
                            "Status"),
                    leaseFrom =
                        GetDateOnly(
                            x,
                            "LeaseFrom"),
                    leaseTo =
                        GetNullableDateOnly(
                            x,
                            "LeaseTo")
                })
                .Where(x =>
                    x.contractId != Guid.Empty)
                .OrderBy(x =>
                    x.tenantName)
                .ToArray();

        var waterSubmeters =
            new List<object>();

        foreach (var meter in utilities.Meters
                     .Where(x =>
                         string.Equals(
                             x.Medium,
                             "Water",
                             StringComparison.OrdinalIgnoreCase)
                         && string.Equals(
                             x.MeterType,
                             "Sub",
                             StringComparison.OrdinalIgnoreCase))
                     .OrderBy(x =>
                         x.Name))
        {
            var pair =
                GetLatestApprovedReadingPair(
                    utilities,
                    meter.Id);

            var locationType =
                GetString(
                    meter,
                    "LocationType");

            var locationId =
                GetGuid(
                    meter,
                    "LocationId");

            var locationName =
                string.Equals(
                    locationType,
                    "Building",
                    StringComparison.OrdinalIgnoreCase)
                && buildings.TryGetValue(
                    locationId,
                    out var buildingName)
                    ? buildingName
                    : string.Equals(
                        locationType,
                        "Parcel",
                        StringComparison.OrdinalIgnoreCase)
                        ? "Działka"
                        : "";

            waterSubmeters.Add(
                new
                {
                    meter.Id,
                    meter.Name,
                    meter.UnitCode,
                    locationType,
                    locationName,
                    previousValue =
                        pair?.PreviousValue,
                    currentValue =
                        pair?.CurrentValue,
                    consumption =
                        pair?.Consumption,
                    previousAtUtc =
                        pair?.PreviousAtUtc,
                    currentAtUtc =
                        pair?.CurrentAtUtc
                });
        }

        var store =
            new UtilityInvoiceDistributionStore(
                environment.ContentRootPath);

        var history =
            (await store.GetAsync(
                current.HouseholdId,
                cancellationToken))
            .Where(x =>
                string.Equals(
                    x.RecordType,
                    "Summary",
                    StringComparison.OrdinalIgnoreCase))
            .Take(8)
            .Select(x => new
            {
                x.Id,
                x.InvoiceNo,
                x.Medium,
                x.PeriodKey,
                x.GrossAmountMinor,
                x.HouseholdShareMinor,
                x.TenantShareMinor,
                x.HouseholdPersonCount,
                x.TenantPersonCount,
                x.CurrencyCode,
                x.AllocationMode,
                x.CreatedAtUtc
            })
            .ToArray();

        return Json(new
        {
            package =
                PackageVersion,
            contracts,
            householdPersons =
                family.Persons
                    .Select(x => new
                    {
                        x.PersonId,
                        x.DisplayName,
                        x.IsChild
                    })
                    .ToArray(),
            tenants,
            waterSubmeters,
            history
        });
    }

    [HttpPost("Create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(
        Guid contractId,
        string medium,
        string invoiceNo,
        string grossAmount,
        string currencyCode,
        DateOnly periodFrom,
        DateOnly periodTo,
        DateOnly issuedOn,
        DateOnly dueDate,
        string allocationMode,
        string? totalConsumption,
        string? tenantConsumption,
        string? manualTenantAmount,
        string? tenantOccupancyJson,
        CancellationToken cancellationToken)
    {
        var current =
            await access.GetCurrentAsync(
                cancellationToken);

        if (current is null)
        {
            return Unauthorized();
        }

        if (!await CanManageAsync(
                current.HouseholdId,
                cancellationToken))
        {
            return Forbid();
        }

        try
        {
            if (string.IsNullOrWhiteSpace(
                    invoiceNo))
            {
                throw new InvalidOperationException(
                    "Podaj numer faktury.");
            }

            if (periodTo < periodFrom)
            {
                throw new InvalidOperationException(
                    "Koniec okresu faktury nie może być wcześniejszy od początku.");
            }

            if (!TryParseFlexibleDecimal(
                    grossAmount,
                    out var grossMajor)
                || grossMajor <= 0m)
            {
                throw new InvalidOperationException(
                    $"Nie udało się odczytać kwoty faktury „{grossAmount}”. Podaj kwotę większą od 0.");
            }

            var currency =
                NormalizeCurrency(
                    currencyCode);

            var grossMinor =
                ToMinor(
                    grossMajor);

            var utilityActor =
                CreateUtilityActor(current);

            var rentalActor =
                CreateRentalActor(current);

            var utilities =
                await utilitiesService.GetOverviewAsync(
                    utilityActor,
                    cancellationToken);

            var contract =
                utilities.Contracts.FirstOrDefault(x =>
                    x.Id == contractId);

            if (contract is null)
            {
                throw new InvalidOperationException(
                    "Nie znaleziono wybranej umowy operatora.");
            }

            var normalizedMedium =
                NormalizeMedium(
                    medium);

            if (!string.Equals(
                    contract.Medium,
                    normalizedMedium,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Wybrana umowa nie odpowiada rodzajowi rozliczanego medium.");
            }

            if (normalizedMedium
                == "Electricity")
            {
                throw new InvalidOperationException(
                    "Fakturę prądu rozlicz przez dedykowany ekran operatora energii.");
            }

            var family =
                await familyService.GetOverviewAsync(
                    current.HouseholdId,
                    cancellationToken);

            var householdPersons =
                family.Persons.ToArray();

            var rental =
                await rentalService.GetOverviewAsync(
                    rentalActor,
                    cancellationToken);

            var eligibleTenants =
                BuildEligibleTenants(
                    rental.Contracts,
                    periodFrom,
                    periodTo);

            var occupancy =
                ParseOccupancy(
                    tenantOccupancyJson);

            var weightedTenants =
                eligibleTenants
                    .Select(x => new TenantWeight(
                        x.ContractId,
                        x.TenantName,
                        x.RoomName,
                        Math.Max(
                            1,
                            occupancy.TryGetValue(
                                x.ContractId,
                                out var persons)
                                ? persons
                                : 1)))
                    .ToArray();

            var tenantPersonCount =
                weightedTenants.Sum(x =>
                    x.Persons);

            long tenantShareMinor = 0;
            long householdShareMinor = grossMinor;
            string resolvedAllocationMode =
                normalizedMedium;

            if (normalizedMedium
                == "Water")
            {
                if (string.Equals(
                        allocationMode,
                        "ManualTenantAmount",
                        StringComparison.OrdinalIgnoreCase))
                {
                    if (!TryParseFlexibleDecimal(
                            manualTenantAmount,
                            out var manualTenantMajor)
                        || manualTenantMajor < 0m)
                    {
                        throw new InvalidOperationException(
                            "Podaj poprawną ręczną kwotę części lokatorów.");
                    }

                    tenantShareMinor =
                        ToMinor(
                            manualTenantMajor);

                    resolvedAllocationMode =
                        "WaterManualTenantAmount";
                }
                else
                {
                    if (!TryParseFlexibleDecimal(
                            totalConsumption,
                            out var totalConsumptionValue)
                        || totalConsumptionValue <= 0m)
                    {
                        throw new InvalidOperationException(
                            "Dla wody podaj całkowite zużycie z faktury w m³.");
                    }

                    if (!TryParseFlexibleDecimal(
                            tenantConsumption,
                            out var tenantConsumptionValue)
                        || tenantConsumptionValue < 0m)
                    {
                        throw new InvalidOperationException(
                            "Dla wody podaj zużycie lokatorów z podlicznika.");
                    }

                    if (tenantConsumptionValue
                        > totalConsumptionValue)
                    {
                        throw new InvalidOperationException(
                            "Zużycie lokatorów nie może być większe od całkowitego zużycia z faktury.");
                    }

                    tenantShareMinor =
                        checked(
                            (long)Math.Round(
                                grossMinor
                                * tenantConsumptionValue
                                / totalConsumptionValue,
                                0,
                                MidpointRounding.AwayFromZero));

                    resolvedAllocationMode =
                        "WaterByConsumption";
                }

                if (tenantPersonCount == 0)
                {
                    tenantShareMinor = 0;
                }

                if (tenantShareMinor
                    > grossMinor)
                {
                    throw new InvalidOperationException(
                        "Część przypisana lokatorom nie może przekroczyć całej faktury.");
                }

                householdShareMinor =
                    grossMinor
                    - tenantShareMinor;
            }
            else if (normalizedMedium
                     == "Waste")
            {
                var totalPersons =
                    householdPersons.Length
                    + tenantPersonCount;

                if (totalPersons <= 0)
                {
                    throw new InvalidOperationException(
                        "Nie ma osób, pomiędzy które można podzielić opłatę za odpady.");
                }

                tenantShareMinor =
                    checked(
                        grossMinor
                        * tenantPersonCount
                        / totalPersons);

                householdShareMinor =
                    grossMinor
                    - tenantShareMinor;

                resolvedAllocationMode =
                    "WasteByPersons";
            }
            else if (normalizedMedium
                     == "Gas")
            {
                tenantShareMinor = 0;
                householdShareMinor =
                    grossMinor;
                resolvedAllocationMode =
                    "GasHouseholdOnly";
            }

            var invoice =
                utilities.Invoices.FirstOrDefault(x =>
                    x.UtilityContractId == contractId
                    && string.Equals(
                        x.InvoiceNo?.Trim(),
                        invoiceNo.Trim(),
                        StringComparison.OrdinalIgnoreCase));

            if (invoice is null)
            {
                await utilitiesService.RegisterInvoiceAsync(
                    utilityActor,
                    new(
                        contractId,
                        invoiceNo.Trim(),
                        periodFrom,
                        periodTo,
                        issuedOn,
                        dueDate,
                        grossMinor,
                        currency,
                        [
                            new(
                                InvoiceComponentCode(
                                    normalizedMedium),
                                grossMinor)
                        ]),
                    cancellationToken);

                utilities =
                    await utilitiesService.GetOverviewAsync(
                        utilityActor,
                        cancellationToken);

                invoice =
                    utilities.Invoices.FirstOrDefault(x =>
                        x.UtilityContractId == contractId
                        && string.Equals(
                            x.InvoiceNo?.Trim(),
                            invoiceNo.Trim(),
                            StringComparison.OrdinalIgnoreCase));
            }

            if (invoice is null)
            {
                throw new InvalidOperationException(
                    "Faktura została przekazana do modułu Media, ale nie udało się odczytać utworzonego rekordu.");
            }

            if (invoice.TotalAmountMinor
                != grossMinor)
            {
                throw new InvalidOperationException(
                    $"Faktura {invoiceNo} już istnieje z inną kwotą. Istniejąca: {invoice.TotalAmountMinor / 100m:N2} {invoice.CurrencyCode}.");
            }

            var periodKey =
                periodTo.ToString(
                    "yyyy-MM");

            var store =
                new UtilityInvoiceDistributionStore(
                    environment.ContentRootPath);

            var tenantAmounts =
                AllocateTenantAmounts(
                    tenantShareMinor,
                    weightedTenants);

            var createdCharges = 0;
            var skippedCharges = 0;
            var correctionCharges = 0;

            foreach (var tenant in weightedTenants)
            {
                if (!tenantAmounts.TryGetValue(
                        tenant.ContractId,
                        out var tenantAmountMinor)
                    || tenantAmountMinor <= 0)
                {
                    continue;
                }

                if (await store.TenantChargeExistsAsync(
                        current.HouseholdId,
                        invoice.Id,
                        tenant.ContractId,
                        cancellationToken))
                {
                    skippedCharges++;
                    continue;
                }

                var settlementOverview =
                    await settlementService.GetOverviewAsync(
                        rentalActor,
                        cancellationToken);

                var settlement =
                    FindSettlement(
                        settlementOverview.Settlements,
                        tenant.ContractId,
                        periodKey);

                if (settlement is null)
                {
                    await settlementService.BuildDraftAsync(
                        rentalActor,
                        new(
                            tenant.ContractId,
                            periodKey),
                        cancellationToken);

                    settlementOverview =
                        await settlementService.GetOverviewAsync(
                            rentalActor,
                            cancellationToken);

                    settlement =
                        FindSettlement(
                            settlementOverview.Settlements,
                            tenant.ContractId,
                            periodKey);
                }

                if (settlement is null)
                {
                    throw new InvalidOperationException(
                        $"Nie udało się przygotować rozliczenia {tenant.TenantName} za {periodKey}.");
                }

                var settlementId =
                    GetGuid(
                        settlement,
                        "Id");

                var settlementStatus =
                    GetString(
                        settlement,
                        "Status");

                var sourceType =
                    normalizedMedium == "Water"
                        ? "WaterInvoice"
                        : "WasteInvoice";

                var snapshot =
                    JsonSerializer.Serialize(
                        new
                        {
                            source =
                                "UtilityInvoiceDistribution",
                            package =
                                PackageVersion,
                            utilityInvoiceId =
                                invoice.Id,
                            utilityContractId =
                                contract.Id,
                            invoiceNo =
                                invoiceNo.Trim(),
                            medium =
                                normalizedMedium,
                            periodFrom,
                            periodTo,
                            periodKey,
                            grossAmountMinor =
                                grossMinor,
                            householdShareMinor,
                            tenantShareMinor,
                            householdPersonCount =
                                householdPersons.Length,
                            tenantPersonCount,
                            tenantPersons =
                                tenant.Persons,
                            tenantAmountMinor,
                            allocationMode =
                                resolvedAllocationMode
                        });

                string settlementOperation;

                if (IsEditableSettlementStatus(
                        settlementStatus))
                {
                    await settlementService.AddManualLineAsync(
                        rentalActor,
                        new(
                            settlementId,
                            "Adjustment",
                            tenantAmountMinor,
                            currency,
                            sourceType,
                            null,
                            snapshot),
                        cancellationToken);

                    settlementOperation =
                        "Line";

                    createdCharges++;
                }
                else
                {
                    await settlementService.CorrectSettlementAsync(
                        rentalActor,
                        new(
                            settlementId,
                            tenantAmountMinor,
                            $"{MediumLabel(normalizedMedium)} — udział z FV {invoiceNo.Trim()} ({periodKey})."),
                        cancellationToken);

                    settlementOperation =
                        "Correction";

                    correctionCharges++;
                }

                await store.AddAsync(
                    new UtilityInvoiceDistributionRecord
                    {
                        Id =
                            Guid.NewGuid(),
                        RecordType =
                            "TenantCharge",
                        HouseholdId =
                            current.HouseholdId,
                        UtilityInvoiceId =
                            invoice.Id,
                        UtilityContractId =
                            contract.Id,
                        InvoiceNo =
                            invoiceNo.Trim(),
                        Medium =
                            normalizedMedium,
                        PeriodKey =
                            periodKey,
                        GrossAmountMinor =
                            grossMinor,
                        HouseholdShareMinor =
                            householdShareMinor,
                        TenantShareMinor =
                            tenantShareMinor,
                        HouseholdPersonCount =
                            householdPersons.Length,
                        TenantPersonCount =
                            tenantPersonCount,
                        CurrencyCode =
                            currency,
                        AllocationMode =
                            resolvedAllocationMode,
                        LeaseContractId =
                            tenant.ContractId,
                        SettlementId =
                            settlementId,
                        TenantName =
                            tenant.TenantName,
                        TenantPersons =
                            tenant.Persons,
                        TenantAmountMinor =
                            tenantAmountMinor,
                        SettlementOperation =
                            settlementOperation,
                        CreatedAtUtc =
                            DateTime.UtcNow,
                        CreatedByUserAccountId =
                            current.UserAccountId
                    },
                    cancellationToken);
            }

            if (!await store.SummaryExistsAsync(
                    current.HouseholdId,
                    invoice.Id,
                    cancellationToken))
            {
                await store.AddAsync(
                    new UtilityInvoiceDistributionRecord
                    {
                        Id =
                            Guid.NewGuid(),
                        RecordType =
                            "Summary",
                        HouseholdId =
                            current.HouseholdId,
                        UtilityInvoiceId =
                            invoice.Id,
                        UtilityContractId =
                            contract.Id,
                        InvoiceNo =
                            invoiceNo.Trim(),
                        Medium =
                            normalizedMedium,
                        PeriodKey =
                            periodKey,
                        GrossAmountMinor =
                            grossMinor,
                        HouseholdShareMinor =
                            householdShareMinor,
                        TenantShareMinor =
                            tenantShareMinor,
                        HouseholdPersonCount =
                            householdPersons.Length,
                        TenantPersonCount =
                            tenantPersonCount,
                        CurrencyCode =
                            currency,
                        AllocationMode =
                            resolvedAllocationMode,
                        CreatedAtUtc =
                            DateTime.UtcNow,
                        CreatedByUserAccountId =
                            current.UserAccountId
                    },
                    cancellationToken);
            }

            var message =
                normalizedMedium switch
                {
                    "Water" =>
                        $"Zarejestrowano FV za wodę {grossMajor:N2} {currency}. " +
                        $"Część gospodarstwa: {householdShareMinor / 100m:N2} {currency}; " +
                        $"część lokatorów: {tenantShareMinor / 100m:N2} {currency}.",
                    "Waste" =>
                        $"Zarejestrowano opłatę za odpady {grossMajor:N2} {currency}. " +
                        $"Podział: {householdPersons.Length} osób gospodarstwa + {tenantPersonCount} osób lokatorów.",
                    "Gas" =>
                        $"Zarejestrowano FV za gaz {grossMajor:N2} {currency}. Całość pozostaje kosztem gospodarstwa / Domu 1.",
                    _ =>
                        "Zarejestrowano fakturę."
                };

            return Json(new
            {
                ok = true,
                package =
                    PackageVersion,
                utilityInvoiceId =
                    invoice.Id,
                medium =
                    normalizedMedium,
                grossAmountMinor =
                    grossMinor,
                householdShareMinor,
                tenantShareMinor,
                householdPersonCount =
                    householdPersons.Length,
                tenantPersonCount,
                createdCharges,
                correctionCharges,
                skippedCharges,
                message =
                    message
                    + " Fakturę opłacasz w Finansach domowych jak każdą inną FV."
            });
        }
        catch (Exception ex)
        {
            return BadRequest(new
            {
                message =
                    $"[{PackageVersion}] {ex.Message}"
            });
        }
    }

    private async Task<bool> CanManageAsync(
        Guid householdId,
        CancellationToken cancellationToken) =>
        await access.CanAsync(
            "utilities.invoice.manage",
            ResourceScopeTypes.Household,
            householdId.ToString("D"),
            cancellationToken:
                cancellationToken);

    private UtilityActor CreateUtilityActor(
        WebUserContext current) =>
        new(
            current.UserAccountId,
            current.PersonId,
            current.HouseholdId,
            CorrelationIdMiddleware.Get(
                HttpContext),
            DateTime.UtcNow);

    private RentalActor CreateRentalActor(
        WebUserContext current) =>
        new(
            current.UserAccountId,
            current.PersonId,
            current.HouseholdId,
            CorrelationIdMiddleware.Get(
                HttpContext),
            DateTime.UtcNow);

    private static IReadOnlyList<TenantSnapshot> BuildEligibleTenants(
        IEnumerable<object> contracts,
        DateOnly periodFrom,
        DateOnly periodTo)
    {
        var result =
            new List<TenantSnapshot>();

        foreach (var contract in contracts)
        {
            var id =
                GetGuid(
                    contract,
                    "ContractId",
                    "Id");

            if (id == Guid.Empty)
            {
                continue;
            }

            var status =
                GetString(
                    contract,
                    "Status");

            if (status is
                "Prepared"
                or "Draft"
                or "Cancelled"
                or "Archived")
            {
                continue;
            }

            var leaseFrom =
                GetDateOnly(
                    contract,
                    "LeaseFrom")
                ?? DateOnly.MinValue;

            var leaseTo =
                GetNullableDateOnly(
                    contract,
                    "LeaseTo");

            var overlaps =
                leaseFrom <= periodTo
                && (!leaseTo.HasValue
                    || leaseTo.Value >= periodFrom);

            if (!overlaps)
            {
                continue;
            }

            result.Add(
                new TenantSnapshot(
                    id,
                    GetString(
                        contract,
                        "TenantName",
                        "Lokator"),
                    GetString(
                        contract,
                        "RoomName",
                        "Pokój")));
        }

        return result;
    }

    private static Dictionary<Guid, int> ParseOccupancy(
        string? json)
    {
        if (string.IsNullOrWhiteSpace(
                json))
        {
            return [];
        }

        try
        {
            var items =
                JsonSerializer.Deserialize<List<UtilityTenantOccupancyInput>>(
                    json,
                    new JsonSerializerOptions(
                        JsonSerializerDefaults.Web))
                ?? [];

            return items
                .Where(x =>
                    x.LeaseContractId != Guid.Empty)
                .GroupBy(x =>
                    x.LeaseContractId)
                .ToDictionary(
                    x => x.Key,
                    x => Math.Max(
                        1,
                        x.Last().Persons));
        }
        catch
        {
            throw new InvalidOperationException(
                "Nie udało się odczytać liczby osób przypisanych do lokatorów.");
        }
    }

    private static Dictionary<Guid, long> AllocateTenantAmounts(
        long totalMinor,
        IReadOnlyList<TenantWeight> tenants)
    {
        var result =
            new Dictionary<Guid, long>();

        if (totalMinor <= 0
            || tenants.Count == 0)
        {
            return result;
        }

        var totalWeight =
            tenants.Sum(x =>
                x.Persons);

        if (totalWeight <= 0)
        {
            return result;
        }

        long allocated = 0;

        for (var index = 0;
             index < tenants.Count;
             index++)
        {
            var tenant =
                tenants[index];

            var amount =
                index == tenants.Count - 1
                    ? totalMinor - allocated
                    : checked(
                        (long)Math.Round(
                            totalMinor
                            * tenant.Persons
                            / (decimal)totalWeight,
                            0,
                            MidpointRounding.AwayFromZero));

            amount =
                Math.Max(
                    0,
                    amount);

            result[tenant.ContractId] =
                amount;

            allocated +=
                amount;
        }

        return result;
    }

    private static object? FindSettlement(
        IEnumerable<object> settlements,
        Guid leaseContractId,
        string periodKey)
    {
        foreach (var settlement in settlements)
        {
            var contractId =
                GetGuid(
                    settlement,
                    "LeaseContractId",
                    "ContractId");

            var period =
                GetString(
                    settlement,
                    "PeriodKey");

            if (contractId == leaseContractId
                && string.Equals(
                    period,
                    periodKey,
                    StringComparison.OrdinalIgnoreCase))
            {
                return settlement;
            }
        }

        return null;
    }

    private static bool IsEditableSettlementStatus(
        string? status) =>
        status is
            "Draft"
            or "AwaitingData"
            or "ReadyForApproval";

    private static ReadingPair? GetLatestApprovedReadingPair(
        UtilityOverview overview,
        Guid meterId)
    {
        var readings =
            overview.Readings
                .Where(x =>
                    x.MeterId == meterId
                    && x.Status
                    == ReadingStatuses.Approved)
                .OrderByDescending(x =>
                    x.ReadingAtUtc)
                .Take(2)
                .ToArray();

        if (readings.Length < 2)
        {
            return null;
        }

        var current =
            GetReadingValue(
                overview,
                readings[0].Id);

        var previous =
            GetReadingValue(
                overview,
                readings[1].Id);

        if (!current.HasValue
            || !previous.HasValue)
        {
            return null;
        }

        var consumption =
            current.Value
            - previous.Value;

        if (consumption < 0m)
        {
            return null;
        }

        return new ReadingPair(
            previous.Value,
            current.Value,
            consumption,
            readings[1].ReadingAtUtc,
            readings[0].ReadingAtUtc);
    }

    private static decimal? GetReadingValue(
        UtilityOverview overview,
        Guid readingId)
    {
        var values =
            overview.ReadingValues
                .Where(x =>
                    x.MeterReadingId == readingId)
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

        return selected.ValueScaled
               / (decimal)Math.Pow(
                   10,
                   selected.Scale);
    }

    private static bool TryParseFlexibleDecimal(
        string? value,
        out decimal result)
    {
        result = 0m;

        if (string.IsNullOrWhiteSpace(
                value))
        {
            return false;
        }

        var normalized =
            value.Trim()
                .Replace(
                    "\u00A0",
                    "")
                .Replace(
                    " ",
                    "");

        const System.Globalization.NumberStyles styles =
            System.Globalization.NumberStyles.AllowLeadingSign
            | System.Globalization.NumberStyles.AllowDecimalPoint;

        if (decimal.TryParse(
                normalized,
                styles,
                System.Globalization.CultureInfo.GetCultureInfo(
                    "pl-PL"),
                out result))
        {
            return true;
        }

        return decimal.TryParse(
            normalized,
            styles,
            System.Globalization.CultureInfo.InvariantCulture,
            out result);
    }

    private static string NormalizeCurrency(
        string? value)
    {
        var currency =
            (value ?? "PLN")
                .Trim()
                .ToUpperInvariant();

        return currency.Length == 3
            ? currency
            : "PLN";
    }

    private static string NormalizeMedium(
        string? value) =>
        value switch
        {
            "Water" => "Water",
            "Gas" => "Gas",
            "Waste" => "Waste",
            "Electricity" => "Electricity",
            _ => throw new InvalidOperationException(
                "Nieobsługiwany rodzaj medium.")
        };

    private static string InvoiceComponentCode(
        string medium) =>
        medium switch
        {
            "Water" =>
                "WaterOperatorInvoice",
            "Gas" =>
                "GasOperatorInvoice",
            "Waste" =>
                "WasteOperatorInvoice",
            _ =>
                "UtilityOperatorInvoice"
        };

    private static string MediumLabel(
        string? medium) =>
        medium switch
        {
            "Water" =>
                "Woda",
            "Gas" =>
                "Gaz",
            "Waste" =>
                "Odpady / śmieci",
            "Electricity" =>
                "Prąd",
            _ =>
                "Media"
        };

    private static long ToMinor(
        decimal amount) =>
        checked(
            (long)Math.Round(
                amount * 100m,
                0,
                MidpointRounding.AwayFromZero));

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
                        BindingFlags.Public
                        | BindingFlags.Instance
                        | BindingFlags.IgnoreCase);

            if (property is not null)
            {
                return property.GetValue(
                    source);
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

    private static DateOnly? GetDateOnly(
        object source,
        params string[] names)
    {
        var value =
            GetValue(
                source,
                names);

        if (value is DateOnly dateOnly)
        {
            return dateOnly;
        }

        if (value is DateTime dateTime)
        {
            return DateOnly.FromDateTime(
                dateTime);
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
        GetDateOnly(
            source,
            names);

    private sealed record TenantSnapshot(
        Guid ContractId,
        string TenantName,
        string RoomName);

    private sealed record TenantWeight(
        Guid ContractId,
        string TenantName,
        string RoomName,
        int Persons);

    private sealed record ReadingPair(
        decimal PreviousValue,
        decimal CurrentValue,
        decimal Consumption,
        DateTime PreviousAtUtc,
        DateTime CurrentAtUtc);
}
