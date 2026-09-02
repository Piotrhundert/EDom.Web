using System.Data.Common;
using EDom.Application.Households;
using EDom.Application.Property;
using EDom.Domain.Authorization;
using EDom.Web.Authorization;
using EDom.Web.Infrastructure;
using EDom.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EDom.Web.Controllers;

[Authorize]
[Route("Property/Details")]
public sealed class PropertyDetailsController(
    WebAccessService access,
    IPropertyAssetService propertyService,
    IHouseholdFamilyService familyService,
    IConfiguration configuration,
    IWebHostEnvironment environment) : Controller
{
    [HttpGet("Data")]
    public async Task<IActionResult> Data(CancellationToken cancellationToken)
    {
        var current = await access.GetCurrentAsync(cancellationToken);
        if (current is null)
        {
            return Unauthorized();
        }

        var actor = CreateActor(current);
        var overview = await propertyService.GetOverviewAsync(actor, cancellationToken);
        var household = await familyService.GetOverviewAsync(
            current.HouseholdId,
            cancellationToken);

        var canManage = await CanManageAsync(
            current.HouseholdId,
            cancellationToken);

        var store = new PropertyExtendedDetailsStore(environment.ContentRootPath);
        var details = await store.GetLatestForHouseholdAsync(
            current.HouseholdId,
            cancellationToken);

        var owners = household.Persons
            .Where(x => x.HasAccount)
            .OrderBy(x => x.DisplayName)
            .Select(x => new
            {
                id = x.PersonId,
                name = x.DisplayName
            })
            .ToArray();

        object? Detail(string type, Guid id)
        {
            var detail = details.FirstOrDefault(x =>
                string.Equals(x.ObjectType, type, StringComparison.OrdinalIgnoreCase)
                && x.ObjectId == id);

            return detail is null
                ? null
                : new
                {
                    addressText = detail.AddressText,
                    landRegisterNumber = detail.LandRegisterNumber,
                    cadastralDistrict = detail.CadastralDistrict,
                    primaryOwnerPersonId = detail.PrimaryOwnerPersonId,
                    coOwnerPersonIds = detail.CoOwnerPersonIds,
                    ownershipShare = detail.OwnershipShare,
                    notes = detail.Notes,
                    updatedAtUtc = detail.UpdatedAtUtc
                };
        }

        var parcelRows = new List<object>();
        foreach (var x in overview.Parcels)
        {
            var area = await ReadNullableDecimalAsync(
                "Parcels",
                x.Id,
                current.HouseholdId,
                ["Area", "AreaM2", "SurfaceArea"],
                cancellationToken);

            parcelRows.Add(new
            {
                id = x.Id,
                name = x.Name,
                addressText = x.AddressText,
                registryNo = x.RegistryNo,
                area,
                ownershipType = x.OwnershipType,
                acquiredOn = x.AcquiredOn,
                status = x.Status,
                details = Detail("Parcel", x.Id)
            });
        }

        var buildingRows = new List<object>();
        foreach (var x in overview.Buildings)
        {
            var parcel = overview.Parcels.FirstOrDefault(p => p.Id == x.ParcelId);
            var usableArea = await ReadNullableDecimalAsync(
                "Buildings",
                x.Id,
                current.HouseholdId,
                ["UsableArea", "UsableAreaM2", "Area", "AreaM2"],
                cancellationToken);

            buildingRows.Add(new
            {
                id = x.Id,
                parcelId = x.ParcelId,
                parcelName = parcel?.Name,
                name = x.Name,
                buildingType = x.BuildingType,
                functionType = x.FunctionType,
                usableArea,
                floors = x.Floors,
                buildYear = x.BuildYear,
                status = x.Status,
                details = Detail("Building", x.Id)
            });
        }

        return Json(new
        {
            canManage,
            owners,
            parcels = parcelRows,
            buildings = buildingRows
        });
    }

    [HttpPost("SaveParcel")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveParcel(
        PropertyParcelDetailsInput input,
        CancellationToken cancellationToken)
    {
        var current = await access.GetCurrentAsync(cancellationToken);
        if (current is null) return Unauthorized();

        if (!await CanManageAsync(current.HouseholdId, cancellationToken))
        {
            return Forbid();
        }

        try
        {
            var actor = CreateActor(current);
            var overview = await propertyService.GetOverviewAsync(actor, cancellationToken);
            var parcel = overview.Parcels.FirstOrDefault(x => x.Id == input.ObjectId);

            if (parcel is null)
            {
                return BadRequest(new { message = "Nie znaleziono działki." });
            }

            ValidateCommon(input.Name, input.Area);
            await ValidateOwnersAsync(
                current.HouseholdId,
                input.PrimaryOwnerPersonId,
                input.CoOwnerPersonIds,
                cancellationToken);

            await UpdateRecordAsync(
                "Parcels",
                input.ObjectId,
                current.HouseholdId,
                new Dictionary<string, object?>
                {
                    ["Name"] = input.Name.Trim(),
                    ["AddressText"] = Clean(input.AddressText),
                    ["RegistryNo"] = Clean(input.RegistryNo),
                    ["Area"] = input.Area,
                    ["OwnershipType"] = Clean(input.OwnershipType) ?? "Owned",
                    ["AcquiredOn"] = input.AcquiredOn?.ToString("yyyy-MM-dd")
                },
                cancellationToken);

            await SaveDetailsSnapshotAsync(
                current,
                "Parcel",
                input.ObjectId,
                input.AddressText,
                input.LandRegisterNumber,
                input.CadastralDistrict,
                input.PrimaryOwnerPersonId,
                input.CoOwnerPersonIds,
                input.OwnershipShare,
                input.Notes,
                cancellationToken);

            return Json(new
            {
                ok = true,
                message = $"Zapisano pełne dane działki „{input.Name.Trim()}”."
            });
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("SaveBuilding")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveBuilding(
        PropertyBuildingDetailsInput input,
        CancellationToken cancellationToken)
    {
        var current = await access.GetCurrentAsync(cancellationToken);
        if (current is null) return Unauthorized();

        if (!await CanManageAsync(current.HouseholdId, cancellationToken))
        {
            return Forbid();
        }

        try
        {
            var actor = CreateActor(current);
            var overview = await propertyService.GetOverviewAsync(actor, cancellationToken);
            var building = overview.Buildings.FirstOrDefault(x => x.Id == input.ObjectId);

            if (building is null)
            {
                return BadRequest(new { message = "Nie znaleziono budynku." });
            }

            ValidateCommon(input.Name, input.UsableArea);

            if (input.Floors is < 0)
            {
                throw new InvalidOperationException(
                    "Liczba kondygnacji nie może być ujemna.");
            }

            if (input.BuildYear is < 1000 or > 2200)
            {
                throw new InvalidOperationException(
                    "Rok budowy jest poza dozwolonym zakresem.");
            }

            await ValidateOwnersAsync(
                current.HouseholdId,
                input.PrimaryOwnerPersonId,
                input.CoOwnerPersonIds,
                cancellationToken);

            await UpdateRecordAsync(
                "Buildings",
                input.ObjectId,
                current.HouseholdId,
                new Dictionary<string, object?>
                {
                    ["Name"] = input.Name.Trim(),
                    ["BuildingType"] = Clean(input.BuildingType) ?? "Residential",
                    ["FunctionType"] = Clean(input.FunctionType) ?? "FamilyHome",
                    ["UsableArea"] = input.UsableArea,
                    ["Floors"] = input.Floors,
                    ["BuildYear"] = input.BuildYear
                },
                cancellationToken);

            await SaveDetailsSnapshotAsync(
                current,
                "Building",
                input.ObjectId,
                input.AddressText,
                input.LandRegisterNumber,
                input.CadastralDistrict,
                input.PrimaryOwnerPersonId,
                input.CoOwnerPersonIds,
                input.OwnershipShare,
                input.Notes,
                cancellationToken);

            return Json(new
            {
                ok = true,
                message = $"Zapisano pełne dane budynku „{input.Name.Trim()}”."
            });
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    private async Task ValidateOwnersAsync(
        Guid householdId,
        Guid? primaryOwnerPersonId,
        IReadOnlyList<Guid>? coOwnerPersonIds,
        CancellationToken cancellationToken)
    {
        var household = await familyService.GetOverviewAsync(
            householdId,
            cancellationToken);

        var allowedIds = household.Persons
            .Where(x => x.HasAccount)
            .Select(x => x.PersonId)
            .ToHashSet();

        var resolvedPrimaryOwnerId = primaryOwnerPersonId.GetValueOrDefault();

        if (resolvedPrimaryOwnerId != Guid.Empty
            && !allowedIds.Contains(resolvedPrimaryOwnerId))
        {
            throw new InvalidOperationException(
                "Wybrany właściciel nie jest użytkownikiem tego gospodarstwa.");
        }

        foreach (var id in coOwnerPersonIds ?? [])
        {
            if (!allowedIds.Contains(id))
            {
                throw new InvalidOperationException(
                    "Jeden ze współwłaścicieli nie jest użytkownikiem tego gospodarstwa.");
            }
        }
    }

    private async Task SaveDetailsSnapshotAsync(
        WebUserContext current,
        string objectType,
        Guid objectId,
        string? addressText,
        string? landRegisterNumber,
        string? cadastralDistrict,
        Guid? primaryOwnerPersonId,
        IReadOnlyList<Guid>? coOwnerPersonIds,
        string? ownershipShare,
        string? notes,
        CancellationToken cancellationToken)
    {
        var resolvedPrimaryOwnerId = primaryOwnerPersonId.GetValueOrDefault();

        var distinctCoOwners = (coOwnerPersonIds ?? [])
            .Where(x => resolvedPrimaryOwnerId == Guid.Empty || x != resolvedPrimaryOwnerId)
            .Distinct()
            .ToArray();

        var store = new PropertyExtendedDetailsStore(environment.ContentRootPath);
        await store.AddSnapshotAsync(
            new(
                Guid.NewGuid(),
                current.HouseholdId,
                objectType,
                objectId,
                Clean(addressText),
                Clean(landRegisterNumber),
                Clean(cadastralDistrict),
                primaryOwnerPersonId,
                distinctCoOwners,
                Clean(ownershipShare),
                Clean(notes),
                DateTime.UtcNow,
                current.UserAccountId),
            cancellationToken);
    }

    private async Task UpdateRecordAsync(
        string tableName,
        Guid objectId,
        Guid householdId,
        IReadOnlyDictionary<string, object?> values,
        CancellationToken cancellationToken)
    {
        if (tableName is not ("Parcels" or "Buildings"))
        {
            throw new InvalidOperationException("Nieobsługiwany typ obiektu.");
        }

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        var columns = await ReadColumnsAsync(
            connection,
            tableName,
            cancellationToken);

        var usableValues = new List<KeyValuePair<string, object?>>();
        foreach (var item in values)
        {
            var actualColumn = ResolveColumnName(
                columns,
                tableName,
                item.Key);

            if (actualColumn is not null)
            {
                usableValues.Add(new(actualColumn, item.Value));
            }
        }

        if (usableValues.Count == 0)
        {
            throw new InvalidOperationException(
                $"Schemat tabeli {tableName} nie zawiera pól możliwych do edycji.");
        }

        var assignments = new List<string>();
        await using var command = connection.CreateCommand();

        var index = 0;
        foreach (var item in usableValues)
        {
            var parameterName = "$v" + index++;
            assignments.Add($"\"{item.Key}\" = {parameterName}");
            AddParameter(command, parameterName, item.Value);
        }

        if (columns.Contains("Version"))
        {
            assignments.Add("\"Version\" = COALESCE(\"Version\", 0) + 1");
        }

        command.CommandText =
            $"UPDATE \"{tableName}\" SET {string.Join(", ", assignments)} " +
            "WHERE \"Id\" = $id AND \"HouseholdId\" = $householdId;";

        AddParameter(command, "$id", objectId);
        AddParameter(command, "$householdId", householdId);

        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        if (affected != 1)
        {
            throw new InvalidOperationException(
                "Nie udało się zaktualizować obiektu w bazie danych.");
        }
    }

    private async Task<decimal?> ReadNullableDecimalAsync(
        string tableName,
        Guid objectId,
        Guid householdId,
        IReadOnlyList<string> candidateColumns,
        CancellationToken cancellationToken)
    {
        if (tableName is not ("Parcels" or "Buildings"))
        {
            return null;
        }

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        var columns = await ReadColumnsAsync(
            connection,
            tableName,
            cancellationToken);

        var column = candidateColumns.FirstOrDefault(columns.Contains);
        if (column is null)
        {
            return null;
        }

        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT \"{column}\" FROM \"{tableName}\" " +
            "WHERE \"Id\" = $id AND \"HouseholdId\" = $householdId LIMIT 1;";

        AddParameter(command, "$id", objectId);
        AddParameter(command, "$householdId", householdId);

        var raw = await command.ExecuteScalarAsync(cancellationToken);
        if (raw is null || raw is DBNull)
        {
            return null;
        }

        try
        {
            return Convert.ToDecimal(raw);
        }
        catch
        {
            return null;
        }
    }

    private static string? ResolveColumnName(
        HashSet<string> columns,
        string tableName,
        string requestedName)
    {
        if (columns.Contains(requestedName))
        {
            return requestedName;
        }

        if (string.Equals(tableName, "Parcels", StringComparison.OrdinalIgnoreCase)
            && string.Equals(requestedName, "Area", StringComparison.OrdinalIgnoreCase))
        {
            return new[] { "AreaM2", "SurfaceArea" }
                .FirstOrDefault(columns.Contains);
        }

        if (string.Equals(tableName, "Buildings", StringComparison.OrdinalIgnoreCase)
            && string.Equals(requestedName, "UsableArea", StringComparison.OrdinalIgnoreCase))
        {
            return new[] { "UsableAreaM2", "Area", "AreaM2" }
                .FirstOrDefault(columns.Contains);
        }

        return null;
    }

    private static async Task<HashSet<string>> ReadColumnsAsync(
        DbConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{tableName}\");";

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(reader.GetString(1));
        }

        return result;
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

    private async Task<bool> CanManageAsync(
        Guid householdId,
        CancellationToken cancellationToken) =>
        await access.CanAsync(
            "property.structure.manage",
            ResourceScopeTypes.Household,
            householdId.ToString("D"),
            cancellationToken: cancellationToken);

    private PropertyActor CreateActor(WebUserContext current) =>
        new(
            current.UserAccountId,
            current.PersonId,
            current.HouseholdId,
            CorrelationIdMiddleware.Get(HttpContext),
            DateTime.UtcNow);

    private static void ValidateCommon(string? name, decimal? area)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException("Nazwa jest wymagana.");
        }

        if (area is < 0m)
        {
            throw new InvalidOperationException(
                "Powierzchnia nie może być ujemna.");
        }
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim();

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
}

public sealed class PropertyParcelDetailsInput
{
    public Guid ObjectId { get; set; }
    public string Name { get; set; } = "";
    public string? AddressText { get; set; }
    public string? RegistryNo { get; set; }
    public decimal? Area { get; set; }
    public string? OwnershipType { get; set; }
    public DateOnly? AcquiredOn { get; set; }

    public string? LandRegisterNumber { get; set; }
    public string? CadastralDistrict { get; set; }
    public Guid? PrimaryOwnerPersonId { get; set; }
    public List<Guid> CoOwnerPersonIds { get; set; } = [];
    public string? OwnershipShare { get; set; }
    public string? Notes { get; set; }
}

public sealed class PropertyBuildingDetailsInput
{
    public Guid ObjectId { get; set; }
    public string Name { get; set; } = "";
    public string? AddressText { get; set; }
    public string? BuildingType { get; set; }
    public string? FunctionType { get; set; }
    public decimal? UsableArea { get; set; }
    public int? Floors { get; set; }
    public int? BuildYear { get; set; }

    public string? LandRegisterNumber { get; set; }
    public string? CadastralDistrict { get; set; }
    public Guid? PrimaryOwnerPersonId { get; set; }
    public List<Guid> CoOwnerPersonIds { get; set; } = [];
    public string? OwnershipShare { get; set; }
    public string? Notes { get; set; }
}
