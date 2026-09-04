using System.Data.Common;
using EDom.Application.Property;
using EDom.Domain.Authorization;
using EDom.Domain.Property;
using EDom.Web.Authorization;
using EDom.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EDom.Web.Controllers;

[Authorize]
[Route("Property/MeterConfig")]
public sealed class PropertyMeterConfigurationController(
    WebAccessService access,
    IPropertyAssetService propertyService,
    IConfiguration configuration,
    IWebHostEnvironment environment) : Controller
{
    private static readonly IReadOnlyDictionary<string, string[]> AllowedUnits =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Electricity"] = ["kWh", "MWh", "Wh"],
            ["Water"] = ["m3", "l"],
            ["Gas"] = ["m3", "kWh"],
            ["Heating"] = ["kWh", "MWh", "GJ"],
            ["Other"] = ["unit", "szt", "m3", "l", "kWh"]
        };

    [HttpPost("Edit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(
        Guid meterId,
        string name,
        string medium,
        string meterType,
        string unitCode,
        string locationType,
        Guid locationId,
        Guid? parentMeterId,
        CancellationToken cancellationToken)
    {
        var current = await access.GetCurrentAsync(cancellationToken);
        if (current is null)
        {
            return Unauthorized();
        }

        if (!await access.CanAsync(
                "utilities.reading.approve",
                ResourceScopeTypes.Household,
                current.HouseholdId.ToString("D"),
                resourceType: "Meter",
                resourceId: meterId.ToString("D"),
                cancellationToken: cancellationToken))
        {
            return Forbid();
        }

        name = (name ?? string.Empty).Trim();
        medium = NormalizeMedium(medium);
        meterType = NormalizeMeterType(meterType);
        unitCode = NormalizeUnit(unitCode);
        locationType = NormalizeLocationType(locationType);

        if (string.IsNullOrWhiteSpace(name))
        {
            TempData["Error"] = "Nazwa licznika jest wymagana.";
            return Redirect("/Property#property-meters");
        }

        if (!AllowedUnits.TryGetValue(medium, out var allowed)
            || !allowed.Contains(unitCode, StringComparer.OrdinalIgnoreCase))
        {
            TempData["Error"] =
                $"Jednostka „{unitCode}” nie jest dozwolona dla medium „{MediumLabel(medium)}”.";
            return Redirect("/Property#property-meters");
        }

        try
        {
            var actor = new PropertyActor(
                current.UserAccountId,
                current.PersonId,
                current.HouseholdId,
                CorrelationIdMiddleware.Get(HttpContext),
                DateTime.UtcNow);

            var overview = await propertyService.GetOverviewAsync(
                actor,
                cancellationToken);

            var existing = overview.Meters.FirstOrDefault(x => x.Id == meterId);
            if (existing is null)
            {
                TempData["Error"] = "Nie znaleziono licznika w bieżącym gospodarstwie.";
                return Redirect("/Property#property-meters");
            }

            if (string.Equals(meterType, MeterTypes.Sub, StringComparison.OrdinalIgnoreCase)
                && string.Equals(existing.MeterType, MeterTypes.Main, StringComparison.OrdinalIgnoreCase)
                && overview.Meters.Any(x =>
                    x.ParentMeterId == meterId
                    && !string.Equals(x.Status, "Archived", StringComparison.OrdinalIgnoreCase)))
            {
                TempData["Error"] =
                    "Ten licznik główny ma aktywne podliczniki. Nie można zmienić go na podlicznik, dopóki zależne podliczniki istnieją.";
                return Redirect("/Property#property-meters");
            }

            var validation = PropertyController.ValidateMeterPlacement(
                overview,
                meterType,
                medium,
                locationType,
                locationId,
                parentMeterId,
                meterId);

            if (validation.Error is not null)
            {
                TempData["Error"] = validation.Error;
                return Redirect("/Property#property-meters");
            }

            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE "Meters"
                SET "Name" = $name,
                    "Medium" = $medium,
                    "MeterType" = $meterType,
                    "UnitCode" = $unitCode,
                    "LocationType" = $locationType,
                    "LocationId" = $locationId,
                    "ParentMeterId" = $parentMeterId,
                    "Version" = COALESCE("Version", 0) + 1
                WHERE "Id" = $meterId
                  AND "HouseholdId" = $householdId
                  AND "Status" <> 'Archived';
                """;

            AddParameter(update, "$name", name);
            AddParameter(update, "$medium", medium);
            AddParameter(update, "$meterType", meterType);
            AddParameter(update, "$unitCode", unitCode);
            AddParameter(update, "$locationType", locationType);
            AddParameter(update, "$locationId", locationId);
            AddParameter(update, "$parentMeterId", validation.ParentMeterId);
            AddParameter(update, "$meterId", meterId);
            AddParameter(update, "$householdId", current.HouseholdId);

            var affected = await update.ExecuteNonQueryAsync(cancellationToken);
            if (affected != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                TempData["Error"] = "Nie udało się zaktualizować licznika.";
                return Redirect("/Property#property-meters");
            }

            await transaction.CommitAsync(cancellationToken);

            TempData["Success"] = meterType == MeterTypes.Sub
                ? string.Equals(
                        locationType,
                        "Room",
                        StringComparison.OrdinalIgnoreCase)
                    ? $"Zapisano podlicznik „{name}” dla pokoju/lokalu — może być używany do rozliczeń lokatora."
                    : $"Zapisano podlicznik techniczny „{name}” dla {(string.Equals(locationType, "Parcel", StringComparison.OrdinalIgnoreCase) ? "działki" : "domu")}."
                : $"Zapisano licznik główny „{name}”.";
        }
        catch (Exception ex)
        {
            TempData["Error"] =
                $"Nie udało się zapisać konfiguracji licznika: {ex.Message}";
        }

        return Redirect("/Property#property-meters");
    }

    private DbConnection CreateConnection()
    {
        var providerType = Type.GetType(
            "Microsoft.Data.Sqlite.SqliteConnection, Microsoft.Data.Sqlite",
            throwOnError: false);

        if (providerType is null)
        {
            throw new InvalidOperationException(
                "Nie znaleziono dostawcy Microsoft.Data.Sqlite.");
        }

        var dataRoot = configuration["EDom:Data:RootPath"] ?? "App_Data";
        var databasePath = configuration["EDom:Data:DatabasePath"] ?? "Database";
        var databaseFileName =
            configuration["EDom:Data:DatabaseFileName"] ?? "e-dom.db";
        var busyTimeout =
            configuration.GetValue<int?>("EDom:Data:SqliteBusyTimeoutSeconds") ?? 5;

        var filePath = Path.GetFullPath(Path.Combine(
            environment.ContentRootPath,
            dataRoot,
            databasePath,
            databaseFileName));

        var connectionString =
            $"Data Source={filePath};Mode=ReadWrite;Cache=Shared;Foreign Keys=True;Default Timeout={busyTimeout}";

        return (DbConnection?)Activator.CreateInstance(
                   providerType,
                   connectionString)
               ?? throw new InvalidOperationException(
                   "Nie udało się utworzyć połączenia SQLite.");
    }

    private static void AddParameter(
        DbCommand command,
        string name,
        object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private static string NormalizeMedium(string? value) =>
        (value ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "electricity" => "Electricity",
            "water" => "Water",
            "gas" => "Gas",
            "heating" => "Heating",
            "other" => "Other",
            _ => "Other"
        };

    private static string NormalizeMeterType(string? value) =>
        string.Equals(
            value?.Trim(),
            MeterTypes.Sub,
            StringComparison.OrdinalIgnoreCase)
            ? MeterTypes.Sub
            : MeterTypes.Main;

    private static string NormalizeLocationType(string? value) =>
        (value ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "parcel" => "Parcel",
            "building" => "Building",
            "room" => "Room",
            _ => string.Empty
        };

    private static string NormalizeUnit(string? value) =>
        (value ?? string.Empty).Trim() switch
        {
            "m³" => "m3",
            "M3" => "m3",
            "L" => "l",
            "szt." => "szt",
            var x => x
        };

    private static string MediumLabel(string medium) => medium switch
    {
        "Electricity" => "Prąd",
        "Water" => "Woda",
        "Gas" => "Gaz",
        "Heating" => "Ogrzewanie",
        _ => "Inne"
    };
}
