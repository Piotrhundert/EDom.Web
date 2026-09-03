using System.Collections;
using System.Reflection;
using EDom.Application.Property;
using EDom.Application.Rental;
using EDom.Web.Authorization;
using EDom.Web.Infrastructure;
using EDom.Web.Models;
using EDom.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EDom.Web.Controllers;

[Authorize]
[Route("Rental/Pellet")]
public sealed class TenantPelletPoolController(
    WebAccessService access,
    ITenantSettlementService settlementService,
    IRentalService rentalService,
    IPropertyAssetService propertyService,
    IWebHostEnvironment environment) : Controller
{
    [HttpGet("Data")]
    public async Task<IActionResult> Data(
        CancellationToken cancellationToken)
    {
        var current = await access.GetCurrentAsync(
            cancellationToken);

        if (current is null)
        {
            return Unauthorized();
        }

        var actor = CreateRentalActor(current);

        var overview = await settlementService.GetOverviewAsync(
            actor,
            cancellationToken);

        var engine = new TenantPelletPoolEngine(
            settlementService,
            rentalService,
            propertyService,
            environment.ContentRootPath);

        var data = await engine.BuildDataAsync(
            actor,
            current.HouseholdId,
            cancellationToken);

        var editableSettlements = AsObjects(GetValue(
                overview,
                "Settlements"))
            .Select(settlement =>
            {
                var status = GetString(
                    settlement,
                    "Status");

                return new
                {
                    settlementId = GetGuid(
                        settlement,
                        "Id"),
                    leaseContractId = ResolveLeaseContractId(
                        overview,
                        settlement),
                    tenantName = GetString(
                        settlement,
                        "TenantName"),
                    roomName = GetString(
                        settlement,
                        "RoomName"),
                    periodKey = GetString(
                        settlement,
                        "PeriodKey"),
                    status,
                    pelletAmountMinor = GetPelletLineAmountMinor(
                        settlement),
                    editable =
                        status is
                            "Draft"
                            or "AwaitingData"
                            or "ReadyForApproval"
                };
            })
            .Where(x =>
                x.settlementId != Guid.Empty
                && x.leaseContractId != Guid.Empty)
            .ToArray();

        return Json(new
        {
            canManage = overview.CanManage,
            data,
            editableSettlements
        });
    }

    [HttpPost("ApplyToSettlement")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ApplyToSettlement(
        Guid leaseContractId,
        string periodKey,
        CancellationToken cancellationToken)
    {
        var current = await access.GetCurrentAsync(
            cancellationToken);

        if (current is null)
        {
            return Unauthorized();
        }

        var actor = CreateRentalActor(current);

        var overview = await settlementService.GetOverviewAsync(
            actor,
            cancellationToken);

        if (!overview.CanManage)
        {
            return Forbid();
        }

        var settlement = AsObjects(GetValue(
                overview,
                "Settlements"))
            .FirstOrDefault(x =>
                ResolveLeaseContractId(
                    overview,
                    x) == leaseContractId
                && string.Equals(
                    GetString(x, "PeriodKey"),
                    periodKey,
                    StringComparison.OrdinalIgnoreCase));

        if (settlement is null)
        {
            return BadRequest(new
            {
                message =
                    "Nie znaleziono projektu rozliczenia dla tego lokatora i miesiąca."
            });
        }

        var status = GetString(
            settlement,
            "Status");

        if (status is not (
            "Draft"
            or "AwaitingData"
            or "ReadyForApproval"))
        {
            return BadRequest(new
            {
                message =
                    "Pellet można dopisać bezpośrednio tylko do otwartego projektu. Dla zatwierdzonego lub opublikowanego rachunku użyj korekty."
            });
        }

        var existingPelletMinor =
            GetPelletLineAmountMinor(
                settlement);

        if (existingPelletMinor > 0)
        {
            return Json(new
            {
                ok = true,
                alreadyExists = true,
                amountMinor = existingPelletMinor,
                message =
                    $"Pellet jest już w tym rozliczeniu: {existingPelletMinor / 100m:N2} PLN. Nie utworzono duplikatu."
            });
        }

        try
        {
            var engine = new TenantPelletPoolEngine(
                settlementService,
                rentalService,
                propertyService,
                environment.ContentRootPath);

            var amountMinor = await engine.ApplyAsync(
                actor,
                current.HouseholdId,
                leaseContractId,
                periodKey,
                cancellationToken);

            if (amountMinor <= 0)
            {
                return BadRequest(new
                {
                    message =
                        "Nie udało się naliczyć pelletu. Sprawdź, czy dla domu istnieje aktywna pula obejmująca ten miesiąc oraz czy lokator ma aktywną umowę najmu."
                });
            }

            return Json(new
            {
                ok = true,
                alreadyExists = false,
                amountMinor,
                message =
                    $"Dodano pellet do projektu rozliczenia: {amountMinor / 100m:N2} PLN. Możesz teraz ponownie zatwierdzić i opublikować rachunek."
            });
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
        catch (Exception ex)
        {
            return BadRequest(new
            {
                message = ex.Message
            });
        }
    }

    [HttpGet("PreviewCorrections/{poolId:guid}")]
    public async Task<IActionResult> PreviewCorrections(
        Guid poolId,
        CancellationToken cancellationToken)
    {
        var current = await access.GetCurrentAsync(
            cancellationToken);

        if (current is null)
        {
            return Unauthorized();
        }

        var actor = CreateRentalActor(current);

        var overview = await settlementService.GetOverviewAsync(
            actor,
            cancellationToken);

        if (!overview.CanManage)
        {
            return Forbid();
        }

        try
        {
            var engine = new TenantPelletPoolEngine(
                settlementService,
                rentalService,
                propertyService,
                environment.ContentRootPath);

            var preview = await engine.PreviewClosedCorrectionsAsync(
                actor,
                current.HouseholdId,
                poolId,
                cancellationToken);

            return Json(new
            {
                ok = true,
                preview
            });
        }
        catch (Exception ex)
        {
            return BadRequest(new
            {
                message = ex.Message
            });
        }
    }

    [HttpPost("GenerateCorrections")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> GenerateCorrections(
        Guid poolId,
        DateOnly dueDate,
        CancellationToken cancellationToken)
    {
        var current = await access.GetCurrentAsync(
            cancellationToken);

        if (current is null)
        {
            return Unauthorized();
        }

        var actor = CreateRentalActor(current);

        var overview = await settlementService.GetOverviewAsync(
            actor,
            cancellationToken);

        if (!overview.CanManage)
        {
            return Forbid();
        }

        if (dueDate < DateOnly.FromDateTime(DateTime.Today))
        {
            return BadRequest(new
            {
                message =
                    "Termin płatności nowych korekt nie może być wcześniejszy niż dzisiaj."
            });
        }

        try
        {
            var engine = new TenantPelletPoolEngine(
                settlementService,
                rentalService,
                propertyService,
                environment.ContentRootPath);

            var result = await engine.GenerateClosedCorrectionsAsync(
                actor,
                current.HouseholdId,
                poolId,
                dueDate,
                current.UserAccountId,
                cancellationToken);

            return Json(new
            {
                ok = true,
                result,
                message = result.CorrectionCount == 0
                    ? "Nie ma zamkniętych rozliczeń wymagających korekty pelletu."
                    : $"Wygenerowano {result.CorrectionCount} korekt na łączną kwotę {result.CorrectionAmountMinor / 100m:N2} PLN. Termin płatności: {dueDate:dd.MM.yyyy}."
            });
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
        catch (Exception ex)
        {
            return BadRequest(new
            {
                message = ex.Message
            });
        }
    }

    [HttpPost("Create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(
        Guid buildingId,
        string seasonName,
        DateOnly periodFrom,
        DateOnly periodTo,
        DateOnly purchaseDate,
        decimal totalAmount,
        string currencyCode,
        decimal? palletCount,
        decimal? weightKg,
        string? supplier,
        string? documentNo,
        string? notes,
        CancellationToken cancellationToken)
    {
        var current = await access.GetCurrentAsync(
            cancellationToken);

        if (current is null)
        {
            return Unauthorized();
        }

        var actor = CreateRentalActor(current);
        var settlementOverview = await settlementService.GetOverviewAsync(
            actor,
            cancellationToken);

        if (!settlementOverview.CanManage)
        {
            return Forbid();
        }

        try
        {
            if (periodTo < periodFrom)
            {
                throw new InvalidOperationException(
                    "Koniec okresu puli pelletu nie może być wcześniejszy niż początek.");
            }

            if (totalAmount <= 0m)
            {
                throw new InvalidOperationException(
                    "Koszt zakupu pelletu musi być większy od 0.");
            }

            if (palletCount is < 0m
                || weightKg is < 0m)
            {
                throw new InvalidOperationException(
                    "Ilość palet i masa pelletu nie mogą być ujemne.");
            }

            var propertyActor = new PropertyActor(
                current.UserAccountId,
                current.PersonId,
                current.HouseholdId,
                CorrelationIdMiddleware.Get(HttpContext),
                DateTime.UtcNow);

            var propertyOverview = await propertyService.GetOverviewAsync(
                propertyActor,
                cancellationToken);

            var building = propertyOverview.Buildings.FirstOrDefault(x =>
                x.Id == buildingId);

            if (building is null)
            {
                throw new InvalidOperationException(
                    "Nie znaleziono wybranego domu.");
            }

            if (!propertyOverview.Rooms.Any(x =>
                    x.BuildingId == buildingId
                    && x.IsRentable))
            {
                throw new InvalidOperationException(
                    "Wybrany budynek nie ma pomieszczeń przeznaczonych do wynajmu.");
            }

            var item = new TenantPelletPoolRecord
            {
                Id = Guid.NewGuid(),
                HouseholdId = current.HouseholdId,
                BuildingId = building.Id,
                BuildingName = building.Name,
                SeasonName = string.IsNullOrWhiteSpace(seasonName)
                    ? $"{periodFrom:yyyy-MM} – {periodTo:yyyy-MM}"
                    : seasonName.Trim(),
                PeriodFrom = periodFrom,
                PeriodTo = periodTo,
                PurchaseDate = purchaseDate,
                TotalAmountMinor = ToMinor(totalAmount),
                CurrencyCode = NormalizeCurrency(currencyCode),
                PalletCount = palletCount,
                WeightKg = weightKg,
                Supplier = Clean(supplier),
                DocumentNo = Clean(documentNo),
                Notes = Clean(notes),
                Status = TenantPelletPoolStatuses.Active,
                CreatedAtUtc = DateTime.UtcNow,
                CreatedByUserAccountId = current.UserAccountId
            };

            var store = new TenantPelletPoolStore(
                environment.ContentRootPath);

            await store.CreatePoolAsync(
                item,
                cancellationToken);

            return Json(new
            {
                ok = true,
                message =
                    $"Utworzono pulę pelletu dla {building.Name}: {totalAmount:N2} {item.CurrencyCode}. Koszt będzie dzielony wyłącznie między aktywnych lokatorów."
            });
        }
        catch (Exception ex)
        {
            return BadRequest(new
            {
                message = ex.Message
            });
        }
    }

    private static Guid ResolveLeaseContractId(
        object overview,
        object settlement)
    {
        var direct = GetGuid(
            settlement,
            "LeaseContractId",
            "ContractId");

        if (direct != Guid.Empty)
        {
            return direct;
        }

        var tenantName = GetString(
            settlement,
            "TenantName");

        var roomName = GetString(
            settlement,
            "RoomName");

        foreach (var contract in AsObjects(GetValue(
                     overview,
                     "Contracts")))
        {
            if (string.Equals(
                    GetString(contract, "TenantName"),
                    tenantName,
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    GetString(contract, "RoomName"),
                    roomName,
                    StringComparison.OrdinalIgnoreCase))
            {
                return GetGuid(
                    contract,
                    "ContractId",
                    "Id");
            }
        }

        return Guid.Empty;
    }

    private static long GetPelletLineAmountMinor(
        object settlement)
    {
        long total = 0;

        foreach (var line in AsObjects(GetValue(
                     settlement,
                     "Lines")))
        {
            if (!string.Equals(
                    GetString(line, "LineType"),
                    "Pellet",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            total = checked(
                total + GetLong(
                    line,
                    "AmountMinor"));
        }

        return total;
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

    private RentalActor CreateRentalActor(
        WebUserContext current) =>
        new(
            current.UserAccountId,
            current.PersonId,
            current.HouseholdId,
            CorrelationIdMiddleware.Get(HttpContext),
            DateTime.UtcNow);

    private static string NormalizeCurrency(
        string? value)
    {
        var currency = (value ?? "PLN")
            .Trim()
            .ToUpperInvariant();

        return currency.Length == 3
            ? currency
            : "PLN";
    }

    private static string? Clean(
        string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim();

    private static long ToMinor(
        decimal amount) =>
        checked((long)Math.Round(
            amount * 100m,
            0,
            MidpointRounding.AwayFromZero));
}
