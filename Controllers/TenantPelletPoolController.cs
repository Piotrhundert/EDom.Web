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

        return Json(new
        {
            canManage = overview.CanManage,
            data
        });
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
