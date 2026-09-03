using System.Data;
using System.Data.Common;
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
[Route("Utilities/Contracts")]
public sealed class UtilityContractWorkspaceController(
    WebAccessService access,
    IUtilitiesService utilitiesService,
    EDomDbContext db,
    IWebHostEnvironment environment) : Controller
{
    private const string PackageVersion = "PKG-015o-FEAT-04-FIX-04";

    [HttpGet("Data/{contractId:guid}")]
    public async Task<IActionResult> Data(
        Guid contractId,
        CancellationToken cancellationToken)
    {
        var current = await access.GetCurrentAsync(cancellationToken);
        if (current is null)
        {
            return Unauthorized();
        }

        try
        {
            var row = await GetContractRowAsync(
                contractId,
                cancellationToken);

            if (row is null)
            {
                return NotFound(new
                {
                    message = "Nie znaleziono umowy operatora."
                });
            }

            var parcelId = ReadGuid(row.Values, "ParcelId");

            if (parcelId == Guid.Empty
                || !await db.Parcels
                    .AsNoTracking()
                    .AnyAsync(
                        x =>
                            x.Id == parcelId
                            && x.HouseholdId == current.HouseholdId,
                        cancellationToken))
            {
                return Forbid();
            }

            var parcelName = await db.Parcels
                .AsNoTracking()
                .Where(x => x.Id == parcelId)
                .Select(x => x.Name)
                .FirstOrDefaultAsync(cancellationToken);

            var canEdit = await CanEditAsync(
                current.HouseholdId,
                parcelId,
                cancellationToken);

            var store = new UtilityContractChangeStore(
                environment.ContentRootPath);

            var history = await store.GetAsync(
                current.HouseholdId,
                contractId,
                cancellationToken);

            var tariff = await GetBaseTariffAsync(
                contractId,
                cancellationToken);

            return Json(new
            {
                canEdit,
                tariff = ToTariffDto(tariff),
                contract = new
                {
                    id = contractId,
                    parcelId,
                    parcelName,
                    operatorName = ReadString(row.Values, "OperatorName"),
                    medium = ReadString(row.Values, "Medium"),
                    contractNumber = ReadNullableString(row.Values, "ContractNumber"),
                    accountPoint = ReadNullableString(row.Values, "AccountPoint"),
                    billingSchedule = ReadString(row.Values, "BillingSchedule", "Monthly"),
                    validFrom = ReadDate(row.Values, "ValidFrom"),
                    validTo = ReadNullableDate(row.Values, "ValidTo"),
                    fixedChargeMinor = ReadLong(row.Values, "FixedChargeMinor"),
                    currencyCode = ReadString(row.Values, "CurrencyCode", "PLN")
                },
                history = history.Select(x => new
                {
                    x.Id,
                    x.ChangedAtUtc,
                    x.Reason,
                    changes = x.Changes.Select(change => new
                    {
                        field = Label(change.Field),
                        change.Before,
                        change.After
                    })
                })
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

    [HttpPost("Update")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Update(
        Guid contractId,
        string operatorName,
        string? contractNumber,
        string? accountPoint,
        string billingSchedule,
        DateOnly validFrom,
        DateOnly? validTo,
        decimal fixedCharge,
        string currencyCode,
        string? reason,
        CancellationToken cancellationToken)
    {
        var current = await access.GetCurrentAsync(cancellationToken);
        if (current is null)
        {
            return Unauthorized();
        }

        try
        {
            var row = await GetContractRowAsync(
                contractId,
                cancellationToken);

            if (row is null)
            {
                return NotFound(new
                {
                    message = "Nie znaleziono umowy operatora."
                });
            }

            var parcelId = ReadGuid(row.Values, "ParcelId");

            if (parcelId == Guid.Empty
                || !await db.Parcels
                    .AsNoTracking()
                    .AnyAsync(
                        x =>
                            x.Id == parcelId
                            && x.HouseholdId == current.HouseholdId,
                        cancellationToken))
            {
                return Forbid();
            }

            if (!await CanEditAsync(
                    current.HouseholdId,
                    parcelId,
                    cancellationToken))
            {
                return Forbid();
            }

            operatorName = (operatorName ?? string.Empty).Trim();

            if (operatorName.Length < 2)
            {
                return BadRequest(new
                {
                    message = "Nazwa operatora jest wymagana."
                });
            }

            if (validTo.HasValue && validTo.Value < validFrom)
            {
                return BadRequest(new
                {
                    message = "Data końca umowy nie może być wcześniejsza niż data początku."
                });
            }

            if (fixedCharge < 0m)
            {
                return BadRequest(new
                {
                    message = "Opłata stała nie może być ujemna."
                });
            }

            var normalizedCurrency = NormalizeCurrency(currencyCode);
            var fixedChargeMinor = ToMinor(fixedCharge);

            var desired = new Dictionary<string, object?>(
                StringComparer.OrdinalIgnoreCase)
            {
                ["OperatorName"] = operatorName,
                ["ContractNumber"] = Clean(contractNumber),
                ["AccountPoint"] = Clean(accountPoint),
                ["BillingSchedule"] = string.IsNullOrWhiteSpace(billingSchedule)
                    ? "Monthly"
                    : billingSchedule.Trim(),
                ["ValidFrom"] = validFrom.ToString("yyyy-MM-dd"),
                ["ValidTo"] = validTo?.ToString("yyyy-MM-dd"),
                ["FixedChargeMinor"] = fixedChargeMinor,
                ["CurrencyCode"] = normalizedCurrency
            };

            var changes = BuildChanges(row.Values, desired);

            if (changes.Count == 0)
            {
                return Json(new
                {
                    ok = true,
                    changed = false,
                    message = "Nie wykryto zmian w umowie."
                });
            }

            await UpdateContractRowAsync(
                row,
                desired,
                cancellationToken);

            var store = new UtilityContractChangeStore(
                environment.ContentRootPath);

            await store.AddAsync(
                new UtilityContractChangeRecord
                {
                    Id = Guid.NewGuid(),
                    HouseholdId = current.HouseholdId,
                    ContractId = contractId,
                    ChangedAtUtc = DateTime.UtcNow,
                    ChangedByUserAccountId = current.UserAccountId,
                    Reason = string.IsNullOrWhiteSpace(reason)
                        ? "Edycja umowy w module Media"
                        : reason.Trim(),
                    Changes = changes
                },
                cancellationToken);

            return Json(new
            {
                ok = true,
                changed = true,
                message = $"Zapisano zmiany umowy operatora. Historia zmian została zachowana ({changes.Count} pól)."
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


    [HttpPost("BaseTariff/Save")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveBaseTariff(
        Guid contractId,
        Guid? tariffId,
        string name,
        DateOnly validFrom,
        DateOnly? validTo,
        string currencyCode,
        string zoneCode,
        string componentCode,
        string ratePerUnit,
        string unitCode,
        string? reason,
        CancellationToken cancellationToken)
    {
        var current = await access.GetCurrentAsync(
            cancellationToken);

        if (current is null)
        {
            return Unauthorized();
        }

        try
        {
            var contractRow = await GetContractRowAsync(
                contractId,
                cancellationToken);

            if (contractRow is null)
            {
                return NotFound(new
                {
                    message = "Nie znaleziono umowy operatora."
                });
            }

            var parcelId = ReadGuid(
                contractRow.Values,
                "ParcelId");

            if (parcelId == Guid.Empty
                || !await db.Parcels
                    .AsNoTracking()
                    .AnyAsync(
                        x =>
                            x.Id == parcelId
                            && x.HouseholdId == current.HouseholdId,
                        cancellationToken))
            {
                return Forbid();
            }

            if (!await CanEditAsync(
                    current.HouseholdId,
                    parcelId,
                    cancellationToken))
            {
                return Forbid();
            }

            name = string.IsNullOrWhiteSpace(name)
                ? "Taryfa podstawowa"
                : name.Trim();

            zoneCode = string.IsNullOrWhiteSpace(zoneCode)
                ? "ALL"
                : zoneCode.Trim().ToUpperInvariant();

            componentCode = string.IsNullOrWhiteSpace(componentCode)
                ? "Consumption"
                : componentCode.Trim();

            unitCode = string.IsNullOrWhiteSpace(unitCode)
                ? DefaultUnitForMedium(
                    ReadString(
                        contractRow.Values,
                        "Medium"))
                : unitCode.Trim();

            if (validTo.HasValue
                && validTo.Value < validFrom)
            {
                return BadRequest(new
                {
                    message =
                        "Data końca taryfy nie może być wcześniejsza niż data początku."
                });
            }

            if (!TryParseFlexibleDecimal(
                    ratePerUnit,
                    out var parsedRatePerUnit)
                || parsedRatePerUnit <= 0m)
            {
                return BadRequest(new
                {
                    message =
                        $"Nie udało się odczytać stawki „{ratePerUnit}”. " +
                        "Podaj liczbę większą od 0, np. 1,25 lub 1.25."
                });
            }

            var normalizedCurrency =
                NormalizeCurrency(currencyCode);

            var before =
                await GetBaseTariffAsync(
                    contractId,
                    cancellationToken);

            var actor =
                new UtilityActor(
                    current.UserAccountId,
                    current.PersonId,
                    current.HouseholdId,
                    CorrelationIdMiddleware.Get(HttpContext),
                    DateTime.UtcNow);

            Guid savedTariffId;

            if (before is null)
            {
                await utilitiesService.CreateTariffAsync(
                    actor,
                    new(
                        contractId,
                        name,
                        validFrom,
                        validTo,
                        normalizedCurrency,
                        "Gross",
                        "{}",
                        [
                            new(
                                zoneCode,
                                componentCode,
                                parsedRatePerUnit,
                                6,
                                unitCode,
                                validFrom,
                                validTo)
                        ]),
                    cancellationToken);

                var created =
                    await GetBaseTariffAsync(
                        contractId,
                        cancellationToken);

                if (created is null)
                {
                    throw new InvalidOperationException(
                        "Taryfa została zapisana, ale nie udało się jej ponownie odczytać.");
                }

                savedTariffId =
                    created.Id;
            }
            else
            {
                if (tariffId.HasValue
                    && tariffId.Value != Guid.Empty
                    && tariffId.Value != before.Id)
                {
                    var requested =
                        await GetTariffByIdAsync(
                            contractId,
                            tariffId.Value,
                            cancellationToken);

                    if (requested is not null)
                    {
                        before =
                            requested;
                    }
                }

                if (before.Rate is null)
                {
                    savedTariffId =
                        await RecreateIncompleteTariffThroughServiceAsync(
                            actor,
                            before,
                            contractId,
                            name,
                            validFrom,
                            validTo,
                            normalizedCurrency,
                            zoneCode,
                            componentCode,
                            parsedRatePerUnit,
                            unitCode,
                            cancellationToken);
                }
                else
                {
                    await UpdateExistingBaseTariffAsync(
                        before,
                        name,
                        validFrom,
                        validTo,
                        normalizedCurrency,
                        zoneCode,
                        componentCode,
                        parsedRatePerUnit,
                        unitCode,
                        cancellationToken);

                    savedTariffId =
                        before.Id;
                }
            }

            var after =
                await GetTariffByIdAsync(
                    contractId,
                    savedTariffId,
                    cancellationToken)
                ?? await GetBaseTariffAsync(
                    contractId,
                    cancellationToken);

            if (after is not null
                && after.RatePerUnit > 0m)
            {
                await CleanupIncompleteDuplicateTariffsAsync(
                    contractId,
                    after.Id,
                    cancellationToken);
            }

            var changes =
                BuildTariffHistoryChanges(
                    before,
                    after);

            if (changes.Count > 0)
            {
                var store =
                    new UtilityContractChangeStore(
                        environment.ContentRootPath);

                await store.AddAsync(
                    new UtilityContractChangeRecord
                    {
                        Id =
                            Guid.NewGuid(),
                        HouseholdId =
                            current.HouseholdId,
                        ContractId =
                            contractId,
                        ChangedAtUtc =
                            DateTime.UtcNow,
                        ChangedByUserAccountId =
                            current.UserAccountId,
                        Reason =
                            string.IsNullOrWhiteSpace(reason)
                                ? "Zmiana taryfy podstawowej umowy"
                                : reason.Trim(),
                        Changes =
                            changes
                    },
                    cancellationToken);
            }

            return Json(new
            {
                ok = true,
                tariff = ToTariffDto(after),
                message =
                    before is null
                        ? "Utworzono taryfę podstawową umowy."
                        : before.Rate is null
                            ? "Naprawiono niepełną taryfę: usunięto wadliwą wersję bez stawki i utworzono ją ponownie przez oficjalny serwis Media."
                            : "Zapisano taryfę podstawową umowy."
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

    private async Task<bool> CanEditAsync(
        Guid householdId,
        Guid parcelId,
        CancellationToken cancellationToken)
    {
        if (await access.CanAsync(
                "utilities.invoice.manage",
                ResourceScopeTypes.Household,
                householdId.ToString("D"),
                cancellationToken: cancellationToken))
        {
            return true;
        }

        return await access.CanAsync(
            "utilities.invoice.manage",
            ResourceScopeTypes.Property,
            parcelId.ToString("D"),
            cancellationToken: cancellationToken);
    }

    private async Task<ContractRow?> GetContractRowAsync(
        Guid contractId,
        CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        var closeWhenDone = connection.State != ConnectionState.Open;

        if (closeWhenDone)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            var tables = await ReadTablesAsync(
                connection,
                cancellationToken);

            foreach (var table in tables
                         .OrderByDescending(x =>
                             x.Contains("UtilityContract", StringComparison.OrdinalIgnoreCase))
                         .ThenByDescending(x =>
                             x.Contains("Contract", StringComparison.OrdinalIgnoreCase)))
            {
                var columns = await ReadColumnsAsync(
                    connection,
                    table,
                    cancellationToken);

                if (!columns.Contains("Id")
                    || !columns.Contains("ParcelId")
                    || !columns.Contains("OperatorName")
                    || !columns.Contains("Medium"))
                {
                    continue;
                }

                await using var command = connection.CreateCommand();
                command.CommandText =
                    $"SELECT rowid, * FROM {Quote(table)};";

                await using var reader = await command.ExecuteReaderAsync(
                    cancellationToken);

                while (await reader.ReadAsync(cancellationToken))
                {
                    var values = new Dictionary<string, object?>(
                        StringComparer.OrdinalIgnoreCase);

                    var rowId = Convert.ToInt64(reader.GetValue(0));

                    for (var index = 1; index < reader.FieldCount; index++)
                    {
                        values[reader.GetName(index)] =
                            reader.IsDBNull(index)
                                ? null
                                : reader.GetValue(index);
                    }

                    if (TryReadGuid(
                            values.TryGetValue("Id", out var rawId)
                                ? rawId
                                : null,
                            out var parsed)
                        && parsed == contractId)
                    {
                        return new ContractRow(
                            table,
                            rowId,
                            columns,
                            values);
                    }
                }
            }

            return null;
        }
        finally
        {
            if (closeWhenDone)
            {
                await connection.CloseAsync();
            }
        }
    }

    private async Task UpdateContractRowAsync(
        ContractRow row,
        Dictionary<string, object?> desired,
        CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        var closeWhenDone = connection.State != ConnectionState.Open;

        if (closeWhenDone)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            await using var command = connection.CreateCommand();

            var assignments = new List<string>();
            var parameterIndex = 0;

            foreach (var item in desired)
            {
                if (!row.Columns.Contains(item.Key))
                {
                    continue;
                }

                var parameterName = $"$p{parameterIndex++}";
                assignments.Add($"{Quote(item.Key)} = {parameterName}");

                var parameter = command.CreateParameter();
                parameter.ParameterName = parameterName;
                parameter.Value = item.Value ?? DBNull.Value;
                command.Parameters.Add(parameter);
            }

            if (assignments.Count == 0)
            {
                throw new InvalidOperationException(
                    "Tabela umów nie zawiera pól możliwych do edycji.");
            }

            command.CommandText =
                $"UPDATE {Quote(row.TableName)} " +
                $"SET {string.Join(", ", assignments)} " +
                "WHERE rowid = $rowid;";

            var rowIdParameter = command.CreateParameter();
            rowIdParameter.ParameterName = "$rowid";
            rowIdParameter.Value = row.RowId;
            command.Parameters.Add(rowIdParameter);

            var affected = await command.ExecuteNonQueryAsync(
                cancellationToken);

            if (affected != 1)
            {
                throw new InvalidOperationException(
                    "Nie udało się zapisać zmian umowy.");
            }
        }
        finally
        {
            if (closeWhenDone)
            {
                await connection.CloseAsync();
            }
        }
    }


    private static object? ToTariffDto(
        BaseTariffSnapshot? tariff) =>
        tariff is null
            ? null
            : new
            {
                id = tariff.Id,
                name = tariff.Name,
                validFrom = tariff.ValidFrom.ToString("yyyy-MM-dd"),
                validTo = tariff.ValidTo?.ToString("yyyy-MM-dd"),
                currencyCode = tariff.CurrencyCode,
                zoneCode = tariff.ZoneCode,
                componentCode = tariff.ComponentCode,
                ratePerUnit = tariff.RatePerUnit,
                unitCode = tariff.UnitCode,
                versionCount = tariff.VersionCount
            };

    private async Task<BaseTariffSnapshot?> GetBaseTariffAsync(
        Guid contractId,
        CancellationToken cancellationToken)
    {
        var all =
            await GetTariffsAsync(
                contractId,
                cancellationToken);

        var today =
            DateOnly.FromDateTime(
                DateTime.Today);

        return all
            .Where(x =>
                x.ValidFrom <= today
                && (!x.ValidTo.HasValue
                    || x.ValidTo.Value >= today))
            .OrderByDescending(x => x.RatePerUnit > 0m)
            .ThenByDescending(x => x.ValidFrom)
            .ThenByDescending(x => x.RowId)
            .FirstOrDefault()
            ?? all
                .OrderByDescending(x => x.RatePerUnit > 0m)
                .ThenByDescending(x => x.ValidFrom)
                .ThenByDescending(x => x.RowId)
                .FirstOrDefault();
    }

    private async Task<BaseTariffSnapshot?> GetTariffByIdAsync(
        Guid contractId,
        Guid tariffId,
        CancellationToken cancellationToken)
    {
        var all =
            await GetTariffsAsync(
                contractId,
                cancellationToken);

        return all.FirstOrDefault(
            x => x.Id == tariffId);
    }

    private async Task<IReadOnlyList<BaseTariffSnapshot>> GetTariffsAsync(
        Guid contractId,
        CancellationToken cancellationToken)
    {
        var connection =
            db.Database.GetDbConnection();

        var closeWhenDone =
            connection.State != ConnectionState.Open;

        if (closeWhenDone)
        {
            await connection.OpenAsync(
                cancellationToken);
        }

        try
        {
            var tables =
                await ReadTablesAsync(
                    connection,
                    cancellationToken);

            var tableColumns =
                new Dictionary<string, HashSet<string>>(
                    StringComparer.OrdinalIgnoreCase);

            foreach (var table in tables)
            {
                tableColumns[table] =
                    await ReadColumnsAsync(
                        connection,
                        table,
                        cancellationToken);
            }

            var tariffRows =
                new List<TariffDbRow>();

            foreach (var candidate in tableColumns
                         .Where(x =>
                             x.Value.Contains("Id")
                             && (x.Value.Contains("UtilityContractId")
                                 || x.Value.Contains("ContractId"))
                             && x.Value.Contains("ValidFrom"))
                         .OrderByDescending(x =>
                             x.Key.Contains(
                                 "Tariff",
                                 StringComparison.OrdinalIgnoreCase)))
            {
                var contractColumn =
                    candidate.Value.Contains("UtilityContractId")
                        ? "UtilityContractId"
                        : "ContractId";

                await using var command =
                    connection.CreateCommand();

                command.CommandText =
                    $"SELECT rowid, * FROM {Quote(candidate.Key)};";

                await using var reader =
                    await command.ExecuteReaderAsync(
                        cancellationToken);

                while (await reader.ReadAsync(
                           cancellationToken))
                {
                    var values =
                        new Dictionary<string, object?>(
                            StringComparer.OrdinalIgnoreCase);

                    var rowId =
                        Convert.ToInt64(
                            reader.GetValue(0));

                    for (var index = 1;
                         index < reader.FieldCount;
                         index++)
                    {
                        values[reader.GetName(index)] =
                            reader.IsDBNull(index)
                                ? null
                                : reader.GetValue(index);
                    }

                    if (!TryReadGuid(
                            values.TryGetValue(
                                contractColumn,
                                out var contractRaw)
                                ? contractRaw
                                : null,
                            out var rowContractId)
                        || rowContractId != contractId)
                    {
                        continue;
                    }

                    if (!TryReadGuid(
                            values.TryGetValue(
                                "Id",
                                out var idRaw)
                                ? idRaw
                                : null,
                            out var tariffId))
                    {
                        continue;
                    }

                    tariffRows.Add(
                        new TariffDbRow(
                            candidate.Key,
                            rowId,
                            candidate.Value,
                            values,
                            tariffId));
                }

                if (tariffRows.Count > 0
                    && candidate.Key.Contains(
                        "Tariff",
                        StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
            }

            var result =
                new List<BaseTariffSnapshot>();

            foreach (var tariff in tariffRows)
            {
                var rate =
                    await FindPrimaryRateAsync(
                        connection,
                        tableColumns,
                        tariff.Id,
                        cancellationToken);

                result.Add(
                    new BaseTariffSnapshot(
                        tariff.Id,
                        tariff.TableName,
                        tariff.RowId,
                        tariff.Columns,
                        tariff.Values,
                        rate,
                        ReadString(
                            tariff.Values,
                            "Name",
                            "Taryfa podstawowa"),
                        ParseDateOnly(
                            tariff.Values.TryGetValue(
                                "ValidFrom",
                                out var from)
                                ? from
                                : null)
                            ?? DateOnly.FromDateTime(
                                DateTime.Today),
                        ParseDateOnly(
                            tariff.Values.TryGetValue(
                                "ValidTo",
                                out var to)
                                ? to
                                : null),
                        ReadString(
                            tariff.Values,
                            "CurrencyCode",
                            "PLN"),
                        rate?.ZoneCode
                            ?? "ALL",
                        rate?.ComponentCode
                            ?? "Consumption",
                        rate?.RatePerUnit
                            ?? 0m,
                        rate?.UnitCode
                            ?? "",
                        tariffRows.Count));
            }

            return result
                .OrderByDescending(x =>
                    x.ValidFrom)
                .ToArray();
        }
        finally
        {
            if (closeWhenDone)
            {
                await connection.CloseAsync();
            }
        }
    }

    private static async Task<RateDbRow?> FindPrimaryRateAsync(
        DbConnection connection,
        IReadOnlyDictionary<string, HashSet<string>> tableColumns,
        Guid tariffId,
        CancellationToken cancellationToken)
    {
        var matches =
            new List<RateDbRow>();

        foreach (var candidate in tableColumns)
        {
            var fk =
                candidate.Value.Contains("UtilityTariffVersionId")
                    ? "UtilityTariffVersionId"
                    : candidate.Value.Contains("TariffVersionId")
                        ? "TariffVersionId"
                        : candidate.Value.Contains("TariffId")
                            ? "TariffId"
                            : null;

            if (fk is null
                || !HasAnyRateColumn(
                    candidate.Value))
            {
                continue;
            }

            await using var command =
                connection.CreateCommand();

            command.CommandText =
                $"SELECT rowid, * FROM {Quote(candidate.Key)};";

            await using var reader =
                await command.ExecuteReaderAsync(
                    cancellationToken);

            while (await reader.ReadAsync(
                       cancellationToken))
            {
                var values =
                    new Dictionary<string, object?>(
                        StringComparer.OrdinalIgnoreCase);

                var rowId =
                    Convert.ToInt64(
                        reader.GetValue(0));

                for (var index = 1;
                     index < reader.FieldCount;
                     index++)
                {
                    values[reader.GetName(index)] =
                        reader.IsDBNull(index)
                            ? null
                            : reader.GetValue(index);
                }

                if (!TryReadGuid(
                        values.TryGetValue(
                            fk,
                            out var fkRaw)
                            ? fkRaw
                            : null,
                        out var linkedTariffId)
                    || linkedTariffId != tariffId)
                {
                    continue;
                }

                var rate =
                    ReadRatePerUnit(
                        values);

                matches.Add(
                    new RateDbRow(
                        candidate.Key,
                        rowId,
                        candidate.Value,
                        values,
                        ReadString(
                            values,
                            "ZoneCode",
                            "ALL"),
                        ReadString(
                            values,
                            "ComponentCode",
                            "Consumption"),
                        rate,
                        ReadString(
                            values,
                            "UnitCode")));
            }
        }

        return matches
            .OrderByDescending(x =>
                IsConsumptionComponent(
                    x.ComponentCode))
            .ThenByDescending(x =>
                string.Equals(
                    x.ZoneCode,
                    "ALL",
                    StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();
    }

    private async Task CleanupIncompleteDuplicateTariffsAsync(
        Guid contractId,
        Guid keepTariffId,
        CancellationToken cancellationToken)
    {
        var all =
            await GetTariffsAsync(
                contractId,
                cancellationToken);

        var incomplete =
            all
                .Where(x =>
                    x.Id != keepTariffId
                    && x.Rate is null)
                .ToArray();

        if (incomplete.Length == 0)
        {
            return;
        }

        var connection =
            db.Database.GetDbConnection();

        var closeWhenDone =
            connection.State != ConnectionState.Open;

        if (closeWhenDone)
        {
            await connection.OpenAsync(
                cancellationToken);
        }

        try
        {
            foreach (var item in incomplete)
            {
                await using var command =
                    connection.CreateCommand();

                command.CommandText =
                    $"DELETE FROM {Quote(item.TableName)} " +
                    "WHERE rowid = $rowid;";

                var parameter =
                    command.CreateParameter();

                parameter.ParameterName =
                    "$rowid";

                parameter.Value =
                    item.RowId;

                command.Parameters.Add(
                    parameter);

                await command.ExecuteNonQueryAsync(
                    cancellationToken);
            }
        }
        finally
        {
            if (closeWhenDone)
            {
                await connection.CloseAsync();
            }
        }
    }

    private async Task<Guid> RecreateIncompleteTariffThroughServiceAsync(
        UtilityActor actor,
        BaseTariffSnapshot incomplete,
        Guid contractId,
        string name,
        DateOnly validFrom,
        DateOnly? validTo,
        string currencyCode,
        string zoneCode,
        string componentCode,
        decimal ratePerUnit,
        string unitCode,
        CancellationToken cancellationToken)
    {
        // Nie zgadujemy fizycznego modelu tabeli stawek.
        // Usuwamy wyłącznie niepełny rekord wersji taryfy i pozwalamy
        // oficjalnemu IUtilitiesService utworzyć kompletną wersję + stawkę.
        await DeleteIncompleteTariffRowAsync(
            incomplete,
            cancellationToken);

        try
        {
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

            var recreated =
                await GetBaseTariffAsync(
                    contractId,
                    cancellationToken);

            if (recreated is null)
            {
                throw new InvalidOperationException(
                    "Serwis utworzył taryfę, ale nie udało się jej ponownie odczytać.");
            }

            if (recreated.Rate is null
                || recreated.RatePerUnit <= 0m)
            {
                throw new InvalidOperationException(
                    "Serwis utworzył wersję taryfy, ale odczyt nadal nie zawiera poprawnej stawki.");
            }

            return recreated.Id;
        }
        catch
        {
            // Jeżeli CreateTariffAsync zakończy się błędem, przywracamy
            // dokładnie ten rekord taryfy, który istniał przed operacją.
            await RestoreIncompleteTariffRowAsync(
                incomplete,
                CancellationToken.None);

            throw;
        }
    }

    private async Task DeleteIncompleteTariffRowAsync(
        BaseTariffSnapshot tariff,
        CancellationToken cancellationToken)
    {
        var connection =
            db.Database.GetDbConnection();

        var closeWhenDone =
            connection.State != ConnectionState.Open;

        if (closeWhenDone)
        {
            await connection.OpenAsync(
                cancellationToken);
        }

        try
        {
            await using var command =
                connection.CreateCommand();

            command.CommandText =
                $"DELETE FROM {Quote(tariff.TableName)} " +
                "WHERE rowid = $rowid;";

            var rowId =
                command.CreateParameter();

            rowId.ParameterName =
                "$rowid";

            rowId.Value =
                tariff.RowId;

            command.Parameters.Add(
                rowId);

            var affected =
                await command.ExecuteNonQueryAsync(
                    cancellationToken);

            if (affected != 1)
            {
                throw new InvalidOperationException(
                    "Nie udało się usunąć niepełnego rekordu taryfy przed jego naprawą.");
            }
        }
        finally
        {
            if (closeWhenDone)
            {
                await connection.CloseAsync();
            }
        }
    }

    private async Task RestoreIncompleteTariffRowAsync(
        BaseTariffSnapshot tariff,
        CancellationToken cancellationToken)
    {
        var connection =
            db.Database.GetDbConnection();

        var closeWhenDone =
            connection.State != ConnectionState.Open;

        if (closeWhenDone)
        {
            await connection.OpenAsync(
                cancellationToken);
        }

        try
        {
            // Jeśli rekord został już odtworzony przez inną operację,
            // nie próbujemy wstawiać duplikatu.
            await using (var exists =
                         connection.CreateCommand())
            {
                exists.CommandText =
                    $"SELECT COUNT(1) FROM {Quote(tariff.TableName)} " +
                    "WHERE rowid = $rowid;";

                var rowId =
                    exists.CreateParameter();

                rowId.ParameterName =
                    "$rowid";

                rowId.Value =
                    tariff.RowId;

                exists.Parameters.Add(
                    rowId);

                var count =
                    Convert.ToInt64(
                        await exists.ExecuteScalarAsync(
                            cancellationToken)
                        ?? 0L);

                if (count > 0)
                {
                    return;
                }
            }

            var values =
                tariff.Values
                    .Where(x =>
                        tariff.Columns.Contains(
                            x.Key))
                    .ToArray();

            if (values.Length == 0)
            {
                throw new InvalidOperationException(
                    "Nie można przywrócić niepełnej taryfy — brak snapshotu jej pól.");
            }

            await using var command =
                connection.CreateCommand();

            var columns =
                values
                    .Select(x => x.Key)
                    .ToArray();

            var parameters =
                columns
                    .Select((_, index) =>
                        $"$p{index}")
                    .ToArray();

            command.CommandText =
                $"INSERT INTO {Quote(tariff.TableName)} " +
                $"({string.Join(", ", columns.Select(Quote))}) " +
                $"VALUES ({string.Join(", ", parameters)});";

            for (var index = 0;
                 index < values.Length;
                 index++)
            {
                var parameter =
                    command.CreateParameter();

                parameter.ParameterName =
                    parameters[index];

                parameter.Value =
                    values[index].Value
                    ?? DBNull.Value;

                command.Parameters.Add(
                    parameter);
            }

            await command.ExecuteNonQueryAsync(
                cancellationToken);
        }
        finally
        {
            if (closeWhenDone)
            {
                await connection.CloseAsync();
            }
        }
    }

    private async Task UpdateExistingBaseTariffAsync(
        BaseTariffSnapshot tariff,
        string name,
        DateOnly validFrom,
        DateOnly? validTo,
        string currencyCode,
        string zoneCode,
        string componentCode,
        decimal ratePerUnit,
        string unitCode,
        CancellationToken cancellationToken)
    {
        var connection =
            db.Database.GetDbConnection();

        var closeWhenDone =
            connection.State != ConnectionState.Open;

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
            var desiredTariff =
                new Dictionary<string, object?>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["Name"] =
                        name,
                    ["ValidFrom"] =
                        validFrom.ToString("yyyy-MM-dd"),
                    ["ValidTo"] =
                        validTo?.ToString("yyyy-MM-dd"),
                    ["CurrencyCode"] =
                        currencyCode
                };

            await UpdateDynamicRowAsync(
                connection,
                transaction,
                tariff.TableName,
                tariff.RowId,
                tariff.Columns,
                desiredTariff,
                cancellationToken);

            if (tariff.Rate is null)
            {
                // Wcześniejsza nieudana operacja mogła pozostawić wersję
                // taryfy bez rekordu stawki. Naprawiamy ją w miejscu,
                // zamiast tworzyć drugą nakładającą się wersję.
                await CreateMissingTariffRateAsync(
                    connection,
                    transaction,
                    tariff,
                    zoneCode,
                    componentCode,
                    ratePerUnit,
                    unitCode,
                    validFrom,
                    validTo,
                    cancellationToken);
            }
            else
            {
                var desiredRate =
                    new Dictionary<string, object?>(
                        StringComparer.OrdinalIgnoreCase)
                    {
                        ["ZoneCode"] =
                            zoneCode,
                        ["ComponentCode"] =
                            componentCode,
                        ["UnitCode"] =
                            unitCode,
                        ["ValidFrom"] =
                            validFrom.ToString("yyyy-MM-dd"),
                        ["ValidTo"] =
                            validTo?.ToString("yyyy-MM-dd")
                    };

                if (tariff.Rate.Columns.Contains(
                        "RatePerUnit"))
                {
                    desiredRate["RatePerUnit"] =
                        ratePerUnit;
                }
                else if (tariff.Rate.Columns.Contains(
                             "UnitRate"))
                {
                    desiredRate["UnitRate"] =
                        ratePerUnit;
                }
                else if (tariff.Rate.Columns.Contains(
                             "ValueScaled"))
                {
                    const int scale = 6;

                    desiredRate["ValueScaled"] =
                        checked(
                            (long)Math.Round(
                                ratePerUnit
                                * (decimal)Math.Pow(
                                    10,
                                    scale),
                                0,
                                MidpointRounding.AwayFromZero));

                    if (tariff.Rate.Columns.Contains(
                            "Scale"))
                    {
                        desiredRate["Scale"] =
                            scale;
                    }
                }
                else if (tariff.Rate.Columns.Contains(
                             "RateScaled"))
                {
                    const int scale = 6;

                    desiredRate["RateScaled"] =
                        checked(
                            (long)Math.Round(
                                ratePerUnit
                                * (decimal)Math.Pow(
                                    10,
                                    scale),
                                0,
                                MidpointRounding.AwayFromZero));

                    if (tariff.Rate.Columns.Contains(
                            "Scale"))
                    {
                        desiredRate["Scale"] =
                            scale;
                    }

                    if (tariff.Rate.Columns.Contains(
                            "RateScale"))
                    {
                        desiredRate["RateScale"] =
                            scale;
                    }
                }
                else if (tariff.Rate.Columns.Contains(
                             "RateMinorPerUnit"))
                {
                    desiredRate["RateMinorPerUnit"] =
                        ToMinor(
                            ratePerUnit);
                }
                else
                {
                    throw new InvalidOperationException(
                        "Nie rozpoznano kolumny stawki istniejącej taryfy.");
                }

                await UpdateDynamicRowAsync(
                    connection,
                    transaction,
                    tariff.Rate.TableName,
                    tariff.Rate.RowId,
                    tariff.Rate.Columns,
                    desiredRate,
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

    private static async Task CreateMissingTariffRateAsync(
        DbConnection connection,
        DbTransaction transaction,
        BaseTariffSnapshot tariff,
        string zoneCode,
        string componentCode,
        decimal ratePerUnit,
        string unitCode,
        DateOnly validFrom,
        DateOnly? validTo,
        CancellationToken cancellationToken)
    {
        var tables =
            await ReadTablesAsync(
                connection,
                cancellationToken);

        var candidates =
            new List<(string TableName, List<DynamicColumnInfo> Columns, string TariffFk)>();

        foreach (var table in tables)
        {
            var columns =
                await ReadDynamicColumnInfoAsync(
                    connection,
                    transaction,
                    table,
                    cancellationToken);

            var names =
                columns
                    .Select(x => x.Name)
                    .ToHashSet(
                        StringComparer.OrdinalIgnoreCase);

            var tariffFk =
                names.Contains("UtilityTariffVersionId")
                    ? "UtilityTariffVersionId"
                    : names.Contains("TariffVersionId")
                        ? "TariffVersionId"
                        : names.Contains("TariffId")
                            ? "TariffId"
                            : null;

            if (tariffFk is null
                || !HasAnyRateColumn(names))
            {
                continue;
            }

            candidates.Add(
                (
                    table,
                    columns,
                    tariffFk));
        }

        var candidate =
            candidates
                .OrderByDescending(x =>
                    x.TableName.Contains(
                        "TariffRate",
                        StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(x =>
                    x.TableName.Contains(
                        "Rate",
                        StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault();

        if (candidate.TableName is null)
        {
            throw new InvalidOperationException(
                "Nie udało się odnaleźć tabeli stawek taryf. Wersja taryfy istnieje, ale jej brakującej stawki nie można automatycznie utworzyć.");
        }

        var columnNames =
            candidate.Columns
                .Select(x => x.Name)
                .ToHashSet(
                    StringComparer.OrdinalIgnoreCase);

        var values =
            new Dictionary<string, object?>(
                StringComparer.OrdinalIgnoreCase);

        // Id nowego rekordu zapisujemy w takim samym formacie jak Id wersji taryfy.
        var tariffIdRaw =
            tariff.Values.TryGetValue(
                "Id",
                out var rawTariffId)
                ? rawTariffId
                : tariff.Id.ToString("D");

        if (columnNames.Contains("Id"))
        {
            values["Id"] =
                ConvertGuidLike(
                    Guid.NewGuid(),
                    tariffIdRaw);
        }

        values[candidate.TariffFk] =
            ConvertGuidLike(
                tariff.Id,
                tariffIdRaw);

        SetIfPresent(
            values,
            columnNames,
            "ZoneCode",
            zoneCode);

        SetIfPresent(
            values,
            columnNames,
            "ComponentCode",
            componentCode);

        SetIfPresent(
            values,
            columnNames,
            "UnitCode",
            unitCode);

        SetIfPresent(
            values,
            columnNames,
            "ValidFrom",
            validFrom.ToString("yyyy-MM-dd"));

        SetIfPresent(
            values,
            columnNames,
            "ValidTo",
            validTo?.ToString("yyyy-MM-dd"));

        if (columnNames.Contains("RatePerUnit"))
        {
            values["RatePerUnit"] =
                ratePerUnit;
        }
        else if (columnNames.Contains("UnitRate"))
        {
            values["UnitRate"] =
                ratePerUnit;
        }
        else if (columnNames.Contains("ValueScaled"))
        {
            const int scale = 6;

            values["ValueScaled"] =
                checked(
                    (long)Math.Round(
                        ratePerUnit
                        * (decimal)Math.Pow(
                            10,
                            scale),
                        0,
                        MidpointRounding.AwayFromZero));

            SetIfPresent(
                values,
                columnNames,
                "Scale",
                scale);
        }
        else if (columnNames.Contains("RateScaled"))
        {
            const int scale = 6;

            values["RateScaled"] =
                checked(
                    (long)Math.Round(
                        ratePerUnit
                        * (decimal)Math.Pow(
                            10,
                            scale),
                        0,
                        MidpointRounding.AwayFromZero));

            SetIfPresent(
                values,
                columnNames,
                "Scale",
                scale);

            SetIfPresent(
                values,
                columnNames,
                "RateScale",
                scale);
        }
        else if (columnNames.Contains("RateMinorPerUnit"))
        {
            values["RateMinorPerUnit"] =
                ToMinor(
                    ratePerUnit);
        }
        else
        {
            throw new InvalidOperationException(
                $"Tabela {candidate.TableName} nie zawiera rozpoznawanej kolumny stawki.");
        }

        // Typowe pola techniczne wymagane przez encje.
        var now =
            DateTime.UtcNow;

        SetIfPresent(
            values,
            columnNames,
            "Version",
            1);

        SetIfPresent(
            values,
            columnNames,
            "CreatedAtUtc",
            now);

        SetIfPresent(
            values,
            columnNames,
            "UpdatedAtUtc",
            now);

        SetIfPresent(
            values,
            columnNames,
            "IsActive",
            true);

        SetIfPresent(
            values,
            columnNames,
            "Status",
            "Active");

        // Nie zgadujemy wartości nietypowych wymaganych pól. Jeżeli schemat
        // kiedyś się zmieni, użytkownik dostanie precyzyjny komunikat.
        var unresolvedRequired =
            candidate.Columns
                .Where(x =>
                    x.NotNull
                    && !x.PrimaryKey
                    && x.DefaultValue is null
                    && !values.ContainsKey(
                        x.Name))
                .Select(x => x.Name)
                .ToArray();

        if (unresolvedRequired.Length > 0)
        {
            throw new InvalidOperationException(
                $"Nie można automatycznie utworzyć brakującej stawki w tabeli {candidate.TableName}. " +
                $"Wymagane nieobsłużone pola: {string.Join(", ", unresolvedRequired)}.");
        }

        var insertColumns =
            values.Keys.ToArray();

        var parameters =
            insertColumns
                .Select((_, index) =>
                    $"$p{index}")
                .ToArray();

        await using var command =
            connection.CreateCommand();

        command.Transaction =
            transaction;

        command.CommandText =
            $"INSERT INTO {Quote(candidate.TableName)} " +
            $"({string.Join(", ", insertColumns.Select(Quote))}) " +
            $"VALUES ({string.Join(", ", parameters)});";

        for (var index = 0;
             index < insertColumns.Length;
             index++)
        {
            var parameter =
                command.CreateParameter();

            parameter.ParameterName =
                parameters[index];

            parameter.Value =
                values[insertColumns[index]]
                ?? DBNull.Value;

            command.Parameters.Add(
                parameter);
        }

        await command.ExecuteNonQueryAsync(
            cancellationToken);
    }

    private static async Task<List<DynamicColumnInfo>> ReadDynamicColumnInfoAsync(
        DbConnection connection,
        DbTransaction transaction,
        string tableName,
        CancellationToken cancellationToken)
    {
        var result =
            new List<DynamicColumnInfo>();

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
                new DynamicColumnInfo(
                    Name:
                        reader.GetString(1),
                    DeclaredType:
                        reader.IsDBNull(2)
                            ? string.Empty
                            : reader.GetString(2),
                    NotNull:
                        !reader.IsDBNull(3)
                        && Convert.ToInt32(
                            reader.GetValue(3)) != 0,
                    DefaultValue:
                        reader.IsDBNull(4)
                            ? null
                            : reader.GetValue(4),
                    PrimaryKey:
                        !reader.IsDBNull(5)
                        && Convert.ToInt32(
                            reader.GetValue(5)) != 0));
        }

        return result;
    }

    private static object ConvertGuidLike(
        Guid value,
        object? prototype)
    {
        if (prototype is byte[])
        {
            return value.ToByteArray();
        }

        if (prototype is Guid)
        {
            return value;
        }

        return value.ToString("D");
    }

    private static void SetIfPresent(
        IDictionary<string, object?> values,
        IReadOnlySet<string> columns,
        string columnName,
        object? value)
    {
        if (columns.Contains(
                columnName))
        {
            values[columnName] =
                value;
        }
    }

    private static async Task UpdateDynamicRowAsync(
        DbConnection connection,
        DbTransaction transaction,
        string tableName,
        long rowId,
        IReadOnlySet<string> columns,
        IReadOnlyDictionary<string, object?> desired,
        CancellationToken cancellationToken)
    {
        var usable =
            desired
                .Where(x =>
                    columns.Contains(
                        x.Key))
                .ToArray();

        if (usable.Length == 0)
        {
            return;
        }

        await using var command =
            connection.CreateCommand();

        command.Transaction =
            transaction;

        var assignments =
            new List<string>();

        for (var index = 0;
             index < usable.Length;
             index++)
        {
            var parameterName =
                $"$p{index}";

            assignments.Add(
                $"{Quote(usable[index].Key)} = {parameterName}");

            var parameter =
                command.CreateParameter();

            parameter.ParameterName =
                parameterName;

            parameter.Value =
                usable[index].Value
                ?? DBNull.Value;

            command.Parameters.Add(
                parameter);
        }

        command.CommandText =
            $"UPDATE {Quote(tableName)} " +
            $"SET {string.Join(", ", assignments)} " +
            "WHERE rowid = $rowid;";

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
                $"Nie udało się zaktualizować rekordu w tabeli {tableName}.");
        }
    }

    private static List<UtilityContractFieldChange> BuildTariffHistoryChanges(
        BaseTariffSnapshot? before,
        BaseTariffSnapshot? after)
    {
        var result =
            new List<UtilityContractFieldChange>();

        Add(
            "BaseTariffName",
            before?.Name,
            after?.Name);

        Add(
            "BaseTariffValidFrom",
            before?.ValidFrom.ToString("yyyy-MM-dd"),
            after?.ValidFrom.ToString("yyyy-MM-dd"));

        Add(
            "BaseTariffValidTo",
            before?.ValidTo?.ToString("yyyy-MM-dd"),
            after?.ValidTo?.ToString("yyyy-MM-dd"));

        Add(
            "BaseTariffRate",
            before is null
                ? null
                : before.RatePerUnit.ToString(
                    "0.######",
                    System.Globalization.CultureInfo.InvariantCulture),
            after is null
                ? null
                : after.RatePerUnit.ToString(
                    "0.######",
                    System.Globalization.CultureInfo.InvariantCulture));

        Add(
            "BaseTariffUnit",
            before?.UnitCode,
            after?.UnitCode);

        Add(
            "BaseTariffZone",
            before?.ZoneCode,
            after?.ZoneCode);

        return result;

        void Add(
            string field,
            string? oldValue,
            string? newValue)
        {
            if (string.Equals(
                    oldValue,
                    newValue,
                    StringComparison.Ordinal))
            {
                return;
            }

            result.Add(
                new UtilityContractFieldChange
                {
                    Field =
                        field,
                    Before =
                        oldValue,
                    After =
                        newValue
                });
        }
    }

    private static bool HasAnyRateColumn(
        IReadOnlySet<string> columns) =>
        columns.Contains("RatePerUnit")
        || columns.Contains("UnitRate")
        || columns.Contains("ValueScaled")
        || columns.Contains("RateScaled")
        || columns.Contains("RateMinorPerUnit");

    private static bool IsConsumptionComponent(
        string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && (value.Contains(
                "Consumption",
                StringComparison.OrdinalIgnoreCase)
            || value.Contains(
                "Energy",
                StringComparison.OrdinalIgnoreCase)
            || value.Contains(
                "Variable",
                StringComparison.OrdinalIgnoreCase));

    private static decimal ReadRatePerUnit(
        IReadOnlyDictionary<string, object?> values)
    {
        if (TryDecimal(
                values,
                "RatePerUnit",
                out var direct)
            || TryDecimal(
                values,
                "UnitRate",
                out direct))
        {
            return direct;
        }

        if (TryLong(
                values,
                "ValueScaled",
                out var valueScaled))
        {
            var valueScale =
                TryLong(
                    values,
                    "Scale",
                    out var rawValueScale)
                    ? (int)rawValueScale
                    : 6;

            return valueScaled
                   / (decimal)Math.Pow(
                       10,
                       Math.Max(0, valueScale));
        }

        if (TryLong(
                values,
                "RateScaled",
                out var scaled))
        {
            var scale =
                TryLong(
                    values,
                    "Scale",
                    out var rawScale)
                    ? (int)rawScale
                    : TryLong(
                        values,
                        "RateScale",
                        out rawScale)
                        ? (int)rawScale
                        : 6;

            return scaled
                   / (decimal)Math.Pow(
                       10,
                       Math.Max(0, scale));
        }

        if (TryLong(
                values,
                "RateMinorPerUnit",
                out var minor))
        {
            return minor / 100m;
        }

        return 0m;
    }

    private static bool TryDecimal(
        IReadOnlyDictionary<string, object?> values,
        string name,
        out decimal result)
    {
        result = 0m;

        if (!values.TryGetValue(
                name,
                out var value)
            || value is null
            || value is DBNull)
        {
            return false;
        }

        try
        {
            result =
                Convert.ToDecimal(
                    value);

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryLong(
        IReadOnlyDictionary<string, object?> values,
        string name,
        out long result)
    {
        result = 0L;

        if (!values.TryGetValue(
                name,
                out var value)
            || value is null
            || value is DBNull)
        {
            return false;
        }

        try
        {
            result =
                Convert.ToInt64(
                    value);

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static DateOnly? ParseDateOnly(
        object? value)
    {
        if (value is null
            || value is DBNull)
        {
            return null;
        }

        if (value is DateOnly direct)
        {
            return direct;
        }

        if (value is DateTime dateTime)
        {
            return DateOnly.FromDateTime(
                dateTime);
        }

        if (DateOnly.TryParse(
                value.ToString(),
                out var parsed))
        {
            return parsed;
        }

        if (DateTime.TryParse(
                value.ToString(),
                out var parsedDateTime))
        {
            return DateOnly.FromDateTime(
                parsedDateTime);
        }

        return null;
    }

    private static string DefaultUnitForMedium(
        string? medium) =>
        medium switch
        {
            "Electricity" => "kWh",
            "Water" => "m3",
            "Gas" => "m3",
            "Heating" => "kWh",
            _ => "unit"
        };

    private static List<UtilityContractFieldChange> BuildChanges(
        Dictionary<string, object?> current,
        Dictionary<string, object?> desired)
    {
        var result = new List<UtilityContractFieldChange>();

        foreach (var item in desired)
        {
            if (!current.ContainsKey(item.Key))
            {
                continue;
            }

            var before = NormalizeForComparison(current[item.Key]);
            var after = NormalizeForComparison(item.Value);

            if (string.Equals(
                    before,
                    after,
                    StringComparison.Ordinal))
            {
                continue;
            }

            result.Add(
                new UtilityContractFieldChange
                {
                    Field = item.Key,
                    Before = before,
                    After = after
                });
        }

        return result;
    }

    private static string? NormalizeForComparison(object? value)
    {
        if (value is null || value is DBNull)
        {
            return null;
        }

        if (value is DateOnly dateOnly)
        {
            return dateOnly.ToString("yyyy-MM-dd");
        }

        if (value is DateTime dateTime)
        {
            return dateTime.ToString("yyyy-MM-dd");
        }

        return value.ToString()?.Trim();
    }

    private static async Task<List<string>> ReadTablesAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        var result = new List<string>();

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT name
            FROM sqlite_master
            WHERE type = 'table'
              AND name NOT LIKE 'sqlite_%'
            ORDER BY name;
            """;

        await using var reader = await command.ExecuteReaderAsync(
            cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    private static async Task<HashSet<string>> ReadColumnsAsync(
        DbConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        var result = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);

        await using var command = connection.CreateCommand();
        command.CommandText =
            $"PRAGMA table_info({Quote(tableName)});";

        await using var reader = await command.ExecuteReaderAsync(
            cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(reader.GetString(1));
        }

        return result;
    }

    private static Guid ReadGuid(
        Dictionary<string, object?> values,
        string name)
    {
        if (!values.TryGetValue(name, out var value))
        {
            return Guid.Empty;
        }

        return TryReadGuid(value, out var parsed)
            ? parsed
            : Guid.Empty;
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

        if (value is byte[] bytes && bytes.Length == 16)
        {
            guid = new Guid(bytes);
            return true;
        }

        return Guid.TryParse(
            value?.ToString(),
            out guid);
    }

    private static string ReadString(
        Dictionary<string, object?> values,
        string name,
        string fallback = "")
    {
        var value = ReadNullableString(values, name);
        return string.IsNullOrWhiteSpace(value)
            ? fallback
            : value;
    }

    private static string? ReadNullableString(
        Dictionary<string, object?> values,
        string name)
    {
        if (!values.TryGetValue(name, out var value)
            || value is null
            || value is DBNull)
        {
            return null;
        }

        var result = value.ToString()?.Trim();
        return string.IsNullOrWhiteSpace(result)
            ? null
            : result;
    }

    private static long ReadLong(
        Dictionary<string, object?> values,
        string name)
    {
        if (!values.TryGetValue(name, out var value)
            || value is null
            || value is DBNull)
        {
            return 0L;
        }

        try
        {
            return Convert.ToInt64(value);
        }
        catch
        {
            return 0L;
        }
    }

    private static string? ReadDate(
        Dictionary<string, object?> values,
        string name)
    {
        var raw = ReadNullableString(values, name);

        if (DateOnly.TryParse(raw, out var date))
        {
            return date.ToString("yyyy-MM-dd");
        }

        if (DateTime.TryParse(raw, out var dateTime))
        {
            return DateOnly.FromDateTime(dateTime).ToString("yyyy-MM-dd");
        }

        return raw;
    }

    private static string? ReadNullableDate(
        Dictionary<string, object?> values,
        string name) =>
        ReadDate(values, name);

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

        // Polska postać: 1,25
        if (decimal.TryParse(
                normalized,
                styles,
                System.Globalization.CultureInfo.GetCultureInfo("pl-PL"),
                out result))
        {
            return true;
        }

        // Postać wysyłana przez input type=number / FormData: 1.25
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

    private static string NormalizeCurrency(string? value)
    {
        var currency = (value ?? "PLN")
            .Trim()
            .ToUpperInvariant();

        return currency.Length == 3
            ? currency
            : "PLN";
    }

    private static long ToMinor(decimal value) =>
        checked((long)Math.Round(
            value * 100m,
            0,
            MidpointRounding.AwayFromZero));

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim();

    private static string Label(string field) =>
        field switch
        {
            "OperatorName" => "Operator",
            "ContractNumber" => "Numer umowy",
            "AccountPoint" => "Punkt / PPE",
            "BillingSchedule" => "Harmonogram rozliczeń",
            "ValidFrom" => "Obowiązuje od",
            "ValidTo" => "Obowiązuje do",
            "FixedChargeMinor" => "Opłata stała",
            "CurrencyCode" => "Waluta",
            "BaseTariffName" => "Taryfa podstawowa — nazwa",
            "BaseTariffValidFrom" => "Taryfa podstawowa — od",
            "BaseTariffValidTo" => "Taryfa podstawowa — do",
            "BaseTariffRate" => "Taryfa podstawowa — stawka",
            "BaseTariffUnit" => "Taryfa podstawowa — jednostka",
            "BaseTariffZone" => "Taryfa podstawowa — strefa",
            _ => field
        };

    private static string Quote(string identifier) =>
        "\"" + identifier.Replace("\"", "\"\"") + "\"";


    private sealed record TariffDbRow(
        string TableName,
        long RowId,
        HashSet<string> Columns,
        Dictionary<string, object?> Values,
        Guid Id);

    private sealed record DynamicColumnInfo(
        string Name,
        string DeclaredType,
        bool NotNull,
        object? DefaultValue,
        bool PrimaryKey);

    private sealed record RateDbRow(
        string TableName,
        long RowId,
        HashSet<string> Columns,
        Dictionary<string, object?> Values,
        string ZoneCode,
        string ComponentCode,
        decimal RatePerUnit,
        string UnitCode);

    private sealed record BaseTariffSnapshot(
        Guid Id,
        string TableName,
        long RowId,
        HashSet<string> Columns,
        Dictionary<string, object?> Values,
        RateDbRow? Rate,
        string Name,
        DateOnly ValidFrom,
        DateOnly? ValidTo,
        string CurrencyCode,
        string ZoneCode,
        string ComponentCode,
        decimal RatePerUnit,
        string UnitCode,
        int VersionCount);

    private sealed record ContractRow(
        string TableName,
        long RowId,
        HashSet<string> Columns,
        Dictionary<string, object?> Values);
}
