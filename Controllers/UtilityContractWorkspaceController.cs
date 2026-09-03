using System.Data;
using System.Data.Common;
using EDom.Domain.Authorization;
using EDom.Infrastructure.Persistence;
using EDom.Web.Authorization;
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
    EDomDbContext db,
    IWebHostEnvironment environment) : Controller
{
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

            return Json(new
            {
                canEdit,
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
            _ => field
        };

    private static string Quote(string identifier) =>
        "\"" + identifier.Replace("\"", "\"\"") + "\"";

    private sealed record ContractRow(
        string TableName,
        long RowId,
        HashSet<string> Columns,
        Dictionary<string, object?> Values);
}
