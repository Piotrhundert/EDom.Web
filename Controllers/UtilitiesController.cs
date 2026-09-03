using System.Data.Common;
using EDom.Application.Utilities;
using EDom.Application.Collaboration;
using EDom.Domain.Authorization;
using EDom.Domain.Utilities;
using EDom.Infrastructure.Persistence;
using EDom.Web.Authorization;
using EDom.Web.Infrastructure;
using EDom.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EDom.Web.Controllers;

[Authorize]
[Route("Utilities")]
public sealed class UtilitiesController(
    WebAccessService access,
    IUtilitiesService utilitiesService,
    ICollaborationService collaborationService,
    EDomDbContext db) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(
        CancellationToken cancellationToken)
    {
        var actor = await GetActorAsync(cancellationToken);
        if (actor is null) return Forbid();

        var overview = await utilitiesService.GetOverviewAsync(
            actor,
            cancellationToken);

        var parcels = await db.Parcels
            .AsNoTracking()
            .Where(x => x.HouseholdId == actor.HouseholdId)
            .OrderBy(x => x.Name)
            .ToListAsync(cancellationToken);

        var parcelIds = parcels
            .Select(x => x.Id)
            .ToArray();

        var buildings = await db.Buildings
            .AsNoTracking()
            .Where(x => parcelIds.Contains(x.ParcelId))
            .OrderBy(x => x.Name)
            .ToListAsync(cancellationToken);

        var buildingIds = buildings
            .Select(x => x.Id)
            .ToArray();

        var rooms = await db.Rooms
            .AsNoTracking()
            .Where(x => buildingIds.Contains(x.BuildingId))
            .OrderBy(x => x.Name)
            .ToListAsync(cancellationToken);

        var canManage = await access.CanAsync(
            "utilities.invoice.manage",
            ResourceScopeTypes.Household,
            actor.HouseholdId.ToString("D"),
            cancellationToken: cancellationToken);

        if (!canManage)
        {
            foreach (var parcel in parcels)
            {
                if (await access.CanAsync(
                        "utilities.invoice.manage",
                        ResourceScopeTypes.Property,
                        parcel.Id.ToString("D"),
                        cancellationToken: cancellationToken))
                {
                    canManage = true;
                    break;
                }
            }
        }

        if (overview.Meters.Count == 0)
        {
            if (!canManage) return Forbid();
        }

        return View(
            new UtilitiesPageViewModel(
                overview,
                parcels,
                buildings,
                rooms,
                canManage));
    }

    [HttpPost("Reading/Submit"), ValidateAntiForgeryToken]
    public async Task<IActionResult> SubmitReading(
        Guid meterId,
        DateTime readingAtLocal,
        decimal value,
        string zoneCode,
        string source,
        IFormFile? photo,
        CancellationToken cancellationToken)
    {
        var actor = await GetActorAsync(cancellationToken);
        if (actor is null) return Forbid();

        try
        {
            Guid? photoDocumentId = null;

            if (photo is { Length: > 0 })
            {
                if (photo.Length > 25 * 1024 * 1024)
                {
                    throw new InvalidOperationException(
                        "Zdjęcie odczytu przekracza limit 25 MB.");
                }

                await using var memory = new MemoryStream();
                await photo.CopyToAsync(memory, cancellationToken);

                var document =
                    await collaborationService.CreateDocumentAsync(
                        new CollaborationActor(
                            actor.AccountId,
                            actor.PersonId,
                            actor.HouseholdId,
                            actor.CorrelationId,
                            DateTime.UtcNow),
                        new CreateDocumentRequest(
                            $"Odczyt licznika {readingAtLocal:yyyy-MM-dd HH:mm}",
                            "MeterReading",
                            "Standard",
                            ResourceScopeTypes.Household,
                            actor.HouseholdId.ToString("D"),
                            photo.FileName,
                            string.IsNullOrWhiteSpace(photo.ContentType)
                                ? "application/octet-stream"
                                : photo.ContentType,
                            memory.ToArray(),
                            SourceModule: "Utilities",
                            SourceObjectType: "Meter",
                            SourceObjectId: meterId.ToString("D")),
                        cancellationToken);

                photoDocumentId = document.Id;
            }

            await utilitiesService.SubmitReadingAsync(
                actor,
                new(
                    meterId,
                    readingAtLocal.ToUniversalTime(),
                    source,
                    [
                        new(
                            zoneCode,
                            value)
                    ],
                    photoDocumentId),
                cancellationToken);

            TempData["Success"] =
                "Odczyt został zgłoszony do zatwierdzenia.";
        }
        catch (Exception ex)
        {
            TempData["Error"] = ex.Message;
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpPost("Reading/Approve"), ValidateAntiForgeryToken]
    public Task<IActionResult> ApproveReading(
        Guid readingId,
        string? reason,
        CancellationToken cancellationToken)
        => ExecuteAsync(
            async actor =>
            {
                await utilitiesService.ApproveReadingAsync(
                    actor,
                    readingId,
                    reason,
                    cancellationToken);

                return "Odczyt został zatwierdzony.";
            },
            cancellationToken);

    [HttpPost("Reading/Reject"), ValidateAntiForgeryToken]
    public Task<IActionResult> RejectReading(
        Guid readingId,
        string reason,
        CancellationToken cancellationToken)
        => ExecuteAsync(
            async actor =>
            {
                await utilitiesService.RejectReadingAsync(
                    actor,
                    readingId,
                    reason,
                    cancellationToken);

                return "Odczyt został odrzucony.";
            },
            cancellationToken);

    [HttpPost("Reading/Correct"), ValidateAntiForgeryToken]
    public Task<IActionResult> CorrectReading(
        Guid readingId,
        DateTime readingAtLocal,
        decimal value,
        string zoneCode,
        string reason,
        bool resetOrReplacement,
        CancellationToken cancellationToken)
        => ExecuteAsync(
            async actor =>
            {
                await utilitiesService.CorrectReadingAsync(
                    actor,
                    readingId,
                    readingAtLocal.ToUniversalTime(),
                    [
                        new(
                            zoneCode,
                            value)
                    ],
                    reason,
                    resetOrReplacement,
                    cancellationToken);

                return "Utworzono jawną korektę odczytu; poprzedni rekord pozostał w historii.";
            },
            cancellationToken);

    [HttpPost("Contract/Create"), ValidateAntiForgeryToken]
    public Task<IActionResult> CreateContract(
        Guid parcelId,
        Guid meterId,
        string operatorName,
        string medium,
        string? contractNumber,
        string? accountPoint,
        string billingSchedule,
        DateOnly validFrom,
        DateOnly? validTo,
        decimal fixedCharge,
        string currencyCode,
        CancellationToken cancellationToken)
        => ExecuteAsync(
            async actor =>
            {
                await utilitiesService.CreateContractAsync(
                    actor,
                    new(
                        parcelId,
                        operatorName,
                        medium,
                        contractNumber,
                        accountPoint,
                        billingSchedule,
                        validFrom,
                        validTo,
                        ToMinor(fixedCharge),
                        currencyCode,
                        meterId == Guid.Empty
                            ? Array.Empty<Guid>()
                            : [meterId]),
                    cancellationToken);

                return "Dodano umowę operatora.";
            },
            cancellationToken);

    [HttpPost("Tariff/Create"), ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateTariff(
        Guid contractId,
        string name,
        DateOnly validFrom,
        DateOnly? validTo,
        string currencyCode,
        string zoneCode,
        string componentCode,
        decimal ratePerUnit,
        string unitCode,
        bool replaceExistingTariff,
        CancellationToken cancellationToken)
    {
        var actor = await GetActorAsync(
            cancellationToken);

        if (actor is null)
        {
            return Forbid();
        }

        if (validTo.HasValue
            && validTo.Value < validFrom)
        {
            TempData["Error"] =
                "Data końca taryfy nie może być wcześniejsza niż data początku.";

            return RedirectToAction(nameof(Index));
        }

        TariffPeriodChangeSet? closedPeriods = null;

        try
        {
            if (replaceExistingTariff)
            {
                closedPeriods =
                    await ClosePreviousTariffPeriodAsync(
                        contractId,
                        validFrom,
                        validTo,
                        cancellationToken);
            }

            await utilitiesService.CreateTariffAsync(
                actor,
                new(
                    contractId,
                    name,
                    validFrom,
                    validTo,
                    currencyCode,
                    "Gross",
                    "{}",
                    [
                        new(
                            zoneCode,
                            componentCode,
                            ratePerUnit,
                            6,
                            unitCode,
                            validFrom,
                            validTo)
                    ]),
                cancellationToken);

            if (closedPeriods is { ClosedTariffCount: > 0 })
            {
                TempData["Success"] =
                    $"Dodano nową wersję taryfy od {validFrom:dd.MM.yyyy}. " +
                    $"Poprzednią taryfę zakończono {validFrom.AddDays(-1):dd.MM.yyyy}; historia została zachowana.";
            }
            else
            {
                TempData["Success"] =
                    "Dodano wersję taryfy i stawkę.";
            }
        }
        catch (UnauthorizedAccessException)
        {
            if (closedPeriods is not null)
            {
                await RestoreTariffPeriodsAsync(
                    closedPeriods,
                    CancellationToken.None);
            }

            return Forbid();
        }
        catch (Exception ex)
        {
            if (closedPeriods is not null)
            {
                try
                {
                    await RestoreTariffPeriodsAsync(
                        closedPeriods,
                        CancellationToken.None);
                }
                catch
                {
                    // Pierwotny błąd jest ważniejszy dla użytkownika.
                    // Dane do przywrócenia pozostają w change set podczas bieżącej operacji.
                }
            }

            TempData["Error"] =
                replaceExistingTariff
                    ? $"Nie udało się utworzyć nowej wersji taryfy: {ex.Message}"
                    : ex.Message;
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpPost("Forecast/Create"), ValidateAntiForgeryToken]
    public Task<IActionResult> CreateForecast(
        Guid contractId,
        Guid? meterId,
        DateOnly periodFrom,
        DateOnly periodTo,
        decimal estimatedQuantity,
        string zoneCode,
        CancellationToken cancellationToken)
        => ExecuteAsync(
            async actor =>
            {
                await utilitiesService.CreateForecastAsync(
                    actor,
                    new(
                        contractId,
                        meterId,
                        periodFrom,
                        periodTo,
                        ToScaled(
                            estimatedQuantity,
                            3),
                        3,
                        zoneCode),
                    cancellationToken);

                return "Utworzono prognozę bez księgowania kosztu.";
            },
            cancellationToken);

    [HttpPost("Invoice/Create"), ValidateAntiForgeryToken]
    public Task<IActionResult> CreateInvoice(
        Guid contractId,
        string invoiceNo,
        DateOnly periodFrom,
        DateOnly periodTo,
        DateOnly issuedOn,
        DateOnly dueDate,
        decimal totalAmount,
        string currencyCode,
        string componentCode,
        CancellationToken cancellationToken)
        => ExecuteAsync(
            async actor =>
            {
                var minor =
                    ToMinor(totalAmount);

                await utilitiesService.RegisterInvoiceAsync(
                    actor,
                    new(
                        contractId,
                        invoiceNo,
                        periodFrom,
                        periodTo,
                        issuedOn,
                        dueDate,
                        minor,
                        currencyCode,
                        [
                            new(
                                componentCode,
                                minor)
                        ]),
                    cancellationToken);

                return "Zarejestrowano fakturę operatora i powiązano ją z finansami domowymi.";
            },
            cancellationToken);

    [HttpPost("Allocation/Manual"), ValidateAntiForgeryToken]
    public Task<IActionResult> ManualAllocation(
        Guid invoiceId,
        Guid parcelId,
        string medium,
        string targetType1,
        Guid targetId1,
        decimal amount1,
        string? targetType2,
        Guid? targetId2,
        decimal? amount2,
        string? targetType3,
        Guid? targetId3,
        decimal? amount3,
        string? note,
        CancellationToken cancellationToken)
        => ExecuteAsync(
            async actor =>
            {
                var items =
                    new List<AllocationInput>
                    {
                        new(
                            targetType1,
                            targetId1,
                            ToMinor(amount1))
                    };

                if (!string.IsNullOrWhiteSpace(targetType2)
                    && targetId2.HasValue
                    && amount2.HasValue
                    && amount2.Value != 0)
                {
                    items.Add(
                        new(
                            targetType2,
                            targetId2.Value,
                            ToMinor(amount2.Value)));
                }

                if (!string.IsNullOrWhiteSpace(targetType3)
                    && targetId3.HasValue
                    && amount3.HasValue
                    && amount3.Value != 0)
                {
                    items.Add(
                        new(
                            targetType3,
                            targetId3.Value,
                            ToMinor(amount3.Value)));
                }

                await utilitiesService.CreateManualAllocationAsync(
                    actor,
                    new(
                        invoiceId,
                        parcelId,
                        medium,
                        items,
                        note),
                    cancellationToken);

                return "Zatwierdzono ręczną alokację pełnej kwoty.";
            },
            cancellationToken);

    [HttpPost("Allocation/Person"), ValidateAntiForgeryToken]
    public Task<IActionResult> PerPersonAllocation(
        Guid invoiceId,
        Guid parcelId,
        string medium,
        string targetType,
        Guid targetId,
        int personCount,
        CancellationToken cancellationToken)
        => ExecuteAsync(
            async actor =>
            {
                await utilitiesService.CreatePerPersonAllocationAsync(
                    actor,
                    new(
                        invoiceId,
                        parcelId,
                        medium,
                        targetType,
                        targetId,
                        personCount,
                        "{\"source\":\"UI\"}"),
                    cancellationToken);

                return "Zapisano alokację wraz ze snapshotem liczby osób.";
            },
            cancellationToken);

    [HttpPost("WasteRate/Create"), ValidateAntiForgeryToken]
    public Task<IActionResult> CreateWasteRate(
        Guid parcelId,
        decimal amountPerPerson,
        string currencyCode,
        DateOnly validFrom,
        DateOnly? validTo,
        bool childAsAdult,
        CancellationToken cancellationToken)
        => ExecuteAsync(
            async actor =>
            {
                await utilitiesService.CreateWasteRateAsync(
                    actor,
                    new(
                        parcelId,
                        ToMinor(amountPerPerson),
                        currencyCode,
                        validFrom,
                        validTo,
                        $"{{\"childAsAdult\":{childAsAdult.ToString().ToLowerInvariant()}}}"),
                    cancellationToken);

                return "Dodano stawkę odpadów na osobę.";
            },
            cancellationToken);

    [HttpPost("Pellet/Create"), ValidateAntiForgeryToken]
    public Task<IActionResult> CreatePellet(
        Guid buildingId,
        string supplier,
        decimal quantity,
        string unitCode,
        decimal totalAmount,
        string currencyCode,
        DateOnly deliveryDate,
        string startPeriodKey,
        CancellationToken cancellationToken)
        => ExecuteAsync(
            async actor =>
            {
                await utilitiesService.CreatePelletPlanAsync(
                    actor,
                    new(
                        buildingId,
                        supplier,
                        quantity,
                        unitCode,
                        ToMinor(totalAmount),
                        currencyCode,
                        deliveryDate,
                        startPeriodKey,
                        12),
                    cancellationToken);

                return "Zapisano zakup pelletu i utworzono 12-miesięczny plan kosztu.";
            },
            cancellationToken);

    private async Task<TariffPeriodChangeSet> ClosePreviousTariffPeriodAsync(
        Guid contractId,
        DateOnly newValidFrom,
        DateOnly? newValidTo,
        CancellationToken cancellationToken)
    {
        if (newValidFrom == DateOnly.MinValue)
        {
            throw new InvalidOperationException(
                "Nieprawidłowa data początku nowej taryfy.");
        }

        var closeOn =
            newValidFrom.AddDays(-1);

        var connection =
            db.Database.GetDbConnection();

        var closeWhenDone =
            connection.State != System.Data.ConnectionState.Open;

        if (closeWhenDone)
        {
            await connection.OpenAsync(
                cancellationToken);
        }

        var changes =
            new TariffPeriodChangeSet();

        await using var transaction =
            await connection.BeginTransactionAsync(
                cancellationToken);

        try
        {
            var tables =
                await ReadTablesAsync(
                    connection,
                    transaction,
                    cancellationToken);

            var tableColumns =
                new Dictionary<string, HashSet<string>>(
                    StringComparer.OrdinalIgnoreCase);

            foreach (var table in tables)
            {
                tableColumns[table] =
                    await ReadColumnsAsync(
                        connection,
                        transaction,
                        table,
                        cancellationToken);
            }

            var tariffCandidates =
                tableColumns
                    .Where(x =>
                        x.Value.Contains("Id")
                        && (x.Value.Contains("UtilityContractId")
                            || x.Value.Contains("ContractId"))
                        && x.Value.Contains("ValidFrom")
                        && x.Value.Contains("ValidTo"))
                    .OrderByDescending(x =>
                        x.Key.Contains(
                            "Tariff",
                            StringComparison.OrdinalIgnoreCase))
                    .ToArray();

            foreach (var candidate in tariffCandidates)
            {
                var contractColumn =
                    candidate.Value.Contains("UtilityContractId")
                        ? "UtilityContractId"
                        : "ContractId";

                var tariffRows =
                    await ReadTariffRowsAsync(
                        connection,
                        transaction,
                        candidate.Key,
                        contractColumn,
                        cancellationToken);

                foreach (var row in tariffRows)
                {
                    if (row.ContractId != contractId)
                    {
                        continue;
                    }

                    if (row.ValidFrom >= newValidFrom)
                    {
                        if (PeriodsOverlap(
                                row.ValidFrom,
                                row.ValidTo,
                                newValidFrom,
                                newValidTo))
                        {
                            throw new InvalidOperationException(
                                $"Istnieje już taryfa rozpoczynająca się {row.ValidFrom:dd.MM.yyyy}. " +
                                "Automatyczne zastąpienie działa tylko dla wcześniejszej taryfy, którą można zakończyć dzień przed nową. " +
                                "Zmień datę początku albo uporządkuj przyszłą wersję taryfy.");
                        }

                        continue;
                    }

                    if (!PeriodsOverlap(
                            row.ValidFrom,
                            row.ValidTo,
                            newValidFrom,
                            newValidTo))
                    {
                        continue;
                    }

                    changes.Rows.Add(
                        new TariffPeriodRowChange(
                            candidate.Key,
                            row.RowId,
                            row.Id,
                            "ValidTo",
                            row.ValidToRaw));

                    await UpdateRowValueAsync(
                        connection,
                        transaction,
                        candidate.Key,
                        row.RowId,
                        "ValidTo",
                        closeOn.ToString("yyyy-MM-dd"),
                        cancellationToken);

                    changes.ClosedTariffIds.Add(
                        row.Id);

                    changes.ClosedTariffCount++;
                }
            }

            if (changes.ClosedTariffIds.Count > 0)
            {
                foreach (var candidate in tableColumns)
                {
                    var tariffFk =
                        candidate.Value.Contains("UtilityTariffVersionId")
                            ? "UtilityTariffVersionId"
                            : candidate.Value.Contains("TariffVersionId")
                                ? "TariffVersionId"
                                : candidate.Value.Contains("TariffId")
                                    ? "TariffId"
                                    : null;

                    if (tariffFk is null
                        || !candidate.Value.Contains("ValidTo"))
                    {
                        continue;
                    }

                    var linkedRows =
                        await ReadLinkedRateRowsAsync(
                            connection,
                            transaction,
                            candidate.Key,
                            tariffFk,
                            cancellationToken);

                    foreach (var row in linkedRows)
                    {
                        if (!changes.ClosedTariffIds.Contains(
                                row.TariffId))
                        {
                            continue;
                        }

                        if (row.ValidTo.HasValue
                            && row.ValidTo.Value < newValidFrom)
                        {
                            continue;
                        }

                        changes.Rows.Add(
                            new TariffPeriodRowChange(
                                candidate.Key,
                                row.RowId,
                                Guid.Empty,
                                "ValidTo",
                                row.ValidToRaw));

                        await UpdateRowValueAsync(
                            connection,
                            transaction,
                            candidate.Key,
                            row.RowId,
                            "ValidTo",
                            closeOn.ToString("yyyy-MM-dd"),
                            cancellationToken);
                    }
                }
            }

            await transaction.CommitAsync(
                cancellationToken);

            return changes;
        }
        catch
        {
            await transaction.RollbackAsync(
                cancellationToken);

            throw;
        }
        finally
        {
            if (closeWhenDone)
            {
                await connection.CloseAsync();
            }
        }
    }

    private async Task RestoreTariffPeriodsAsync(
        TariffPeriodChangeSet changes,
        CancellationToken cancellationToken)
    {
        if (changes.Rows.Count == 0)
        {
            return;
        }

        var connection =
            db.Database.GetDbConnection();

        var closeWhenDone =
            connection.State != System.Data.ConnectionState.Open;

        if (closeWhenDone)
        {
            await connection.OpenAsync(
                cancellationToken);
        }

        await using var transaction =
            await connection.BeginTransactionAsync(
                cancellationToken);

        try
        {
            foreach (var row in changes.Rows.AsEnumerable().Reverse())
            {
                await UpdateRowValueAsync(
                    connection,
                    transaction,
                    row.TableName,
                    row.RowId,
                    row.ColumnName,
                    row.PreviousValue,
                    cancellationToken);
            }

            await transaction.CommitAsync(
                cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(
                cancellationToken);

            throw;
        }
        finally
        {
            if (closeWhenDone)
            {
                await connection.CloseAsync();
            }
        }
    }

    private static bool PeriodsOverlap(
        DateOnly firstFrom,
        DateOnly? firstTo,
        DateOnly secondFrom,
        DateOnly? secondTo)
    {
        var firstEnd =
            firstTo ?? DateOnly.MaxValue;

        var secondEnd =
            secondTo ?? DateOnly.MaxValue;

        return firstFrom <= secondEnd
               && secondFrom <= firstEnd;
    }

    private static async Task<List<string>> ReadTablesAsync(
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        var result =
            new List<string>();

        await using var command =
            connection.CreateCommand();

        command.Transaction =
            transaction;

        command.CommandText =
            """
            SELECT name
            FROM sqlite_master
            WHERE type = 'table'
              AND name NOT LIKE 'sqlite_%'
            ORDER BY name;
            """;

        await using var reader =
            await command.ExecuteReaderAsync(
                cancellationToken);

        while (await reader.ReadAsync(
                   cancellationToken))
        {
            result.Add(
                reader.GetString(0));
        }

        return result;
    }

    private static async Task<HashSet<string>> ReadColumnsAsync(
        DbConnection connection,
        DbTransaction transaction,
        string tableName,
        CancellationToken cancellationToken)
    {
        var result =
            new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);

        await using var command =
            connection.CreateCommand();

        command.Transaction =
            transaction;

        command.CommandText =
            $"PRAGMA table_info({Quote(tableName)});";

        await using var reader =
            await command.ExecuteReaderAsync(
                cancellationToken);

        while (await reader.ReadAsync(
                   cancellationToken))
        {
            result.Add(
                reader.GetString(1));
        }

        return result;
    }

    private static async Task<List<TariffRow>> ReadTariffRowsAsync(
        DbConnection connection,
        DbTransaction transaction,
        string tableName,
        string contractColumn,
        CancellationToken cancellationToken)
    {
        var result =
            new List<TariffRow>();

        await using var command =
            connection.CreateCommand();

        command.Transaction =
            transaction;

        command.CommandText =
            $"SELECT rowid, {Quote("Id")}, {Quote(contractColumn)}, " +
            $"{Quote("ValidFrom")}, {Quote("ValidTo")} " +
            $"FROM {Quote(tableName)};";

        await using var reader =
            await command.ExecuteReaderAsync(
                cancellationToken);

        while (await reader.ReadAsync(
                   cancellationToken))
        {
            if (!TryReadGuid(
                    reader.IsDBNull(1)
                        ? null
                        : reader.GetValue(1),
                    out var id)
                || !TryReadGuid(
                    reader.IsDBNull(2)
                        ? null
                        : reader.GetValue(2),
                    out var contractId))
            {
                continue;
            }

            var validFrom =
                TryReadDateOnly(
                    reader.IsDBNull(3)
                        ? null
                        : reader.GetValue(3));

            if (!validFrom.HasValue)
            {
                continue;
            }

            var validToRaw =
                reader.IsDBNull(4)
                    ? null
                    : reader.GetValue(4);

            var validTo =
                TryReadDateOnly(
                    validToRaw);

            result.Add(
                new TariffRow(
                    Convert.ToInt64(
                        reader.GetValue(0)),
                    id,
                    contractId,
                    validFrom.Value,
                    validTo,
                    validToRaw));
        }

        return result;
    }

    private static async Task<List<LinkedRateRow>> ReadLinkedRateRowsAsync(
        DbConnection connection,
        DbTransaction transaction,
        string tableName,
        string tariffFkColumn,
        CancellationToken cancellationToken)
    {
        var result =
            new List<LinkedRateRow>();

        await using var command =
            connection.CreateCommand();

        command.Transaction =
            transaction;

        command.CommandText =
            $"SELECT rowid, {Quote(tariffFkColumn)}, {Quote("ValidTo")} " +
            $"FROM {Quote(tableName)};";

        await using var reader =
            await command.ExecuteReaderAsync(
                cancellationToken);

        while (await reader.ReadAsync(
                   cancellationToken))
        {
            if (!TryReadGuid(
                    reader.IsDBNull(1)
                        ? null
                        : reader.GetValue(1),
                    out var tariffId))
            {
                continue;
            }

            var validToRaw =
                reader.IsDBNull(2)
                    ? null
                    : reader.GetValue(2);

            result.Add(
                new LinkedRateRow(
                    Convert.ToInt64(
                        reader.GetValue(0)),
                    tariffId,
                    TryReadDateOnly(
                        validToRaw),
                    validToRaw));
        }

        return result;
    }

    private static async Task UpdateRowValueAsync(
        DbConnection connection,
        DbTransaction transaction,
        string tableName,
        long rowId,
        string columnName,
        object? value,
        CancellationToken cancellationToken)
    {
        await using var command =
            connection.CreateCommand();

        command.Transaction =
            transaction;

        command.CommandText =
            $"UPDATE {Quote(tableName)} " +
            $"SET {Quote(columnName)} = $value " +
            "WHERE rowid = $rowid;";

        var valueParameter =
            command.CreateParameter();

        valueParameter.ParameterName =
            "$value";

        valueParameter.Value =
            value ?? DBNull.Value;

        command.Parameters.Add(
            valueParameter);

        var rowParameter =
            command.CreateParameter();

        rowParameter.ParameterName =
            "$rowid";

        rowParameter.Value =
            rowId;

        command.Parameters.Add(
            rowParameter);

        var affected =
            await command.ExecuteNonQueryAsync(
                cancellationToken);

        if (affected != 1)
        {
            throw new InvalidOperationException(
                $"Nie udało się zaktualizować okresu taryfy w tabeli {tableName}.");
        }
    }

    private static bool TryReadGuid(
        object? value,
        out Guid guid)
    {
        if (value is Guid direct)
        {
            guid = direct;
            return true;
        }

        if (value is byte[] bytes
            && bytes.Length == 16)
        {
            guid =
                new Guid(bytes);

            return true;
        }

        return Guid.TryParse(
            value?.ToString(),
            out guid);
    }

    private static DateOnly? TryReadDateOnly(
        object? value)
    {
        if (value is null
            || value is DBNull)
        {
            return null;
        }

        if (value is DateOnly dateOnly)
        {
            return dateOnly;
        }

        if (value is DateTime dateTime)
        {
            return DateOnly.FromDateTime(
                dateTime);
        }

        var text =
            value.ToString();

        if (DateOnly.TryParse(
                text,
                out var parsed))
        {
            return parsed;
        }

        if (DateTime.TryParse(
                text,
                out var parsedDateTime))
        {
            return DateOnly.FromDateTime(
                parsedDateTime);
        }

        return null;
    }

    private static string Quote(
        string identifier) =>
        "\"" +
        identifier.Replace(
            "\"",
            "\"\"") +
        "\"";

    private async Task<IActionResult> ExecuteAsync(
        Func<UtilityActor, Task<string>> operation,
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

    private async Task<UtilityActor?> GetActorAsync(
        CancellationToken cancellationToken)
    {
        var current =
            await access.GetCurrentAsync(
                cancellationToken);

        return current is null
            ? null
            : new UtilityActor(
                current.UserAccountId,
                current.PersonId,
                current.HouseholdId,
                CorrelationIdMiddleware.Get(HttpContext),
                DateTime.UtcNow);
    }

    private static long ToMinor(
        decimal amount) =>
        checked(
            (long)Math.Round(
                amount * 100m,
                0,
                MidpointRounding.AwayFromZero));

    private static long ToScaled(
        decimal amount,
        int scale) =>
        checked(
            (long)Math.Round(
                amount
                * (decimal)Math.Pow(
                    10,
                    scale),
                0,
                MidpointRounding.AwayFromZero));

    private sealed class TariffPeriodChangeSet
    {
        public List<TariffPeriodRowChange> Rows { get; } = [];
        public HashSet<Guid> ClosedTariffIds { get; } = [];
        public int ClosedTariffCount { get; set; }
    }

    private sealed record TariffPeriodRowChange(
        string TableName,
        long RowId,
        Guid TariffId,
        string ColumnName,
        object? PreviousValue);

    private sealed record TariffRow(
        long RowId,
        Guid Id,
        Guid ContractId,
        DateOnly ValidFrom,
        DateOnly? ValidTo,
        object? ValidToRaw);

    private sealed record LinkedRateRow(
        long RowId,
        Guid TariffId,
        DateOnly? ValidTo,
        object? ValidToRaw);
}
