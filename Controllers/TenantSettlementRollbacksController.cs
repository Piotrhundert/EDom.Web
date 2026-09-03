using System.Collections;
using System.Data.Common;
using System.Reflection;
using EDom.Application.Rental;
using EDom.Domain.Rental;
using EDom.Web.Authorization;
using EDom.Web.Infrastructure;
using EDom.Web.Models;
using EDom.Web.Services;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EDom.Web.Controllers;

[Authorize]
[Route("Rental/SettlementRollbacks")]
public sealed class TenantSettlementRollbacksController(
    WebAccessService access,
    ITenantSettlementService settlementService,
    IAntiforgery antiforgery,
    IConfiguration configuration,
    IWebHostEnvironment environment) : Controller
{
    [HttpGet("Data")]
    public async Task<IActionResult> Data(CancellationToken cancellationToken)
    {
        var current = await access.GetCurrentAsync(cancellationToken);
        if (current is null) return Unauthorized();

        var actor = CreateActor(current);
        var overview = await settlementService.GetOverviewAsync(actor, cancellationToken);

        var canManage = GetBool(overview, "CanManage");
        var isTenant = GetBool(overview, "IsTenant");

        var store = new TenantSettlementRollbackStore(environment.ContentRootPath);
        var allRecords = await store.GetForHouseholdAsync(
            current.HouseholdId,
            cancellationToken);

        var visibleRecords = canManage
            ? allRecords
            : allRecords
                .Where(x =>
                    x.PayerPersonId.HasValue
                    && x.PayerPersonId.GetValueOrDefault() == current.PersonId)
                .ToArray();

        var history = new List<object>();

        foreach (var record in visibleRecords)
        {
            var currentStatus = await ReadSettlementStatusAsync(
                current.HouseholdId,
                record.SettlementId,
                cancellationToken);

            var activeRollback =
                string.IsNullOrWhiteSpace(currentStatus)
                || IsEditableStatus(currentStatus);

            history.Add(new
            {
                record.Id,
                record.SettlementId,
                record.LeaseContractId,
                record.PayerPersonId,
                record.TenantName,
                record.RoomName,
                record.PeriodKey,
                record.PreviousStatus,
                record.ReopenedStatus,
                record.Reason,
                record.PaidMinorAtRollback,
                record.KeptApprovedPayments,
                record.ReopenedAtUtc,
                currentStatus,
                activeRollback
            });
        }

        var settlements = AsObjects(GetValue(overview, "Settlements"))
            .Select(x =>
            {
                var status = GetString(x, "Status");
                var paidMinor = GetLong(x, "PaidMinor");

                var submissions = AsObjects(GetValue(x, "Submissions"))
                    .ToArray();

                var pendingSubmission = submissions.Any(s =>
                {
                    var submissionStatus = GetString(s, "Status");

                    return string.Equals(
                               submissionStatus,
                               "Pending",
                               StringComparison.OrdinalIgnoreCase)
                           || string.Equals(
                               submissionStatus,
                               "Submitted",
                               StringComparison.OrdinalIgnoreCase);
                });

                var approvedPaymentMinor = submissions
                    .Where(s => string.Equals(
                        GetString(s, "Status"),
                        "Approved",
                        StringComparison.OrdinalIgnoreCase))
                    .Sum(s =>
                        GetNullableLong(
                            s,
                            "ApprovedAmountMinor",
                            "DecisionAmountMinor",
                            "AcceptedAmountMinor")
                        ?? GetLong(s, "AmountMinor"));

                if (approvedPaymentMinor <= 0)
                    approvedPaymentMinor = Math.Max(0, paidMinor);

                var requiresKeepPayment =
                    approvedPaymentMinor > 0
                    || paidMinor > 0
                    || IsPaidStatus(status);

                var canRollback =
                    canManage
                    && !IsEditableStatus(status)
                    && !pendingSubmission;

                return new
                {
                    settlementId = GetGuid(x, "Id"),
                    leaseContractId = EmptyToNull(
                        GetGuid(x, "LeaseContractId", "ContractId")),
                    payerPersonId = GetNullableGuid(x, "PayerPersonId"),
                    tenantName = GetString(x, "TenantName"),
                    roomName = GetString(x, "RoomName"),
                    periodKey = GetString(x, "PeriodKey"),
                    status,
                    paidMinor,
                    currencyCode = GetString(x, "CurrencyCode", "PLN"),
                    approvedPaymentMinor,
                    requiresKeepPayment,
                    canRollback,
                    rollbackBlockedReason =
                        pendingSubmission
                            ? "Dla rozliczenia istnieje oczekujące zgłoszenie wpłaty. Najpierw je rozpatrz albo odrzuć."
                            : IsEditableStatus(status)
                                ? "Rozliczenie jest już otwarte do edycji."
                                : null
                };
            })
            .Where(x => x.settlementId != Guid.Empty)
            .ToArray();

        var requestToken = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;

        return Json(new
        {
            canManage,
            isTenant,
            requestToken,
            settlements,
            history
        });
    }

    [HttpPost("Reopen")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reopen(
        Guid settlementId,
        string reason,
        bool keepApprovedPayments,
        CancellationToken cancellationToken)
    {
        var current = await access.GetCurrentAsync(cancellationToken);
        if (current is null) return Unauthorized();

        var actor = CreateActor(current);
        var overview = await settlementService.GetOverviewAsync(actor, cancellationToken);

        if (!GetBool(overview, "CanManage"))
            return Forbid();

        var settlement = AsObjects(GetValue(overview, "Settlements"))
            .FirstOrDefault(x => GetGuid(x, "Id") == settlementId);

        if (settlement is null)
            return BadRequest(new { message = "Nie znaleziono rozliczenia lokatora." });

        var cleanReason = string.IsNullOrWhiteSpace(reason)
            ? null
            : reason.Trim();

        if (cleanReason is null || cleanReason.Length < 5)
        {
            return BadRequest(new
            {
                message = "Podaj powód cofnięcia rozliczenia (minimum 5 znaków)."
            });
        }

        var status = GetString(settlement, "Status");

        if (IsEditableStatus(status))
            return BadRequest(new { message = "Rozliczenie jest już otwarte do ponownej edycji." });

        var paidMinor = GetLong(settlement, "PaidMinor");

        var submissions = AsObjects(GetValue(settlement, "Submissions"))
            .ToArray();

        var pendingSubmission = submissions.FirstOrDefault(s =>
        {
            var submissionStatus = GetString(s, "Status");

            return string.Equals(
                       submissionStatus,
                       "Pending",
                       StringComparison.OrdinalIgnoreCase)
                   || string.Equals(
                       submissionStatus,
                       "Submitted",
                       StringComparison.OrdinalIgnoreCase);
        });

        if (pendingSubmission is not null)
        {
            return BadRequest(new
            {
                message =
                    "Nie można cofnąć rozliczenia, dopóki istnieje oczekujące zgłoszenie wpłaty. Najpierw je zatwierdź albo odrzuć."
            });
        }

        var approvedPaymentMinor = submissions
            .Where(s => string.Equals(
                GetString(s, "Status"),
                "Approved",
                StringComparison.OrdinalIgnoreCase))
            .Sum(s =>
                GetNullableLong(
                    s,
                    "ApprovedAmountMinor",
                    "DecisionAmountMinor",
                    "AcceptedAmountMinor")
                ?? GetLong(s, "AmountMinor"));

        if (approvedPaymentMinor <= 0)
            approvedPaymentMinor = Math.Max(0, paidMinor);

        var hasApprovedPayment =
            approvedPaymentMinor > 0
            || paidMinor > 0
            || IsPaidStatus(status);

        if (hasApprovedPayment && !keepApprovedPayments)
        {
            return BadRequest(new
            {
                message =
                    $"Rozliczenie ma już zaksięgowaną wpłatę {approvedPaymentMinor / 100m:N2} {GetString(settlement, "CurrencyCode", "PLN")}. Aby je cofnąć, zaznacz „Zachowaj zaksięgowaną wpłatę”."
            });
        }

        try
        {
            await SetSettlementBackToDraftAsync(
                current.HouseholdId,
                settlementId,
                cancellationToken);

            var store = new TenantSettlementRollbackStore(
                environment.ContentRootPath);

            await store.AddAsync(
                new TenantSettlementRollbackRecord
                {
                    Id = Guid.NewGuid(),
                    HouseholdId = current.HouseholdId,
                    SettlementId = settlementId,
                    LeaseContractId = EmptyToNull(
                        GetGuid(settlement, "LeaseContractId", "ContractId")),
                    PayerPersonId = GetNullableGuid(settlement, "PayerPersonId"),
                    TenantName = GetString(settlement, "TenantName"),
                    RoomName = GetString(settlement, "RoomName"),
                    PeriodKey = GetString(settlement, "PeriodKey"),
                    PreviousStatus = status,
                    ReopenedStatus = TenantSettlementStatuses.Draft,
                    Reason = cleanReason,
                    PaidMinorAtRollback = approvedPaymentMinor,
                    KeptApprovedPayments = hasApprovedPayment,
                    ReopenedAtUtc = DateTime.UtcNow,
                    ReopenedByUserAccountId = current.UserAccountId
                },
                cancellationToken);

            return Json(new
            {
                ok = true,
                message =
                    hasApprovedPayment
                        ? $"Cofnięto rozliczenie {GetString(settlement, "PeriodKey")} dla {GetString(settlement, "TenantName")}. Zaksięgowana wpłata {approvedPaymentMinor / 100m:N2} {GetString(settlement, "CurrencyCode", "PLN")} została zachowana. Po ponownym przeliczeniu lokator dopłaci tylko ewentualną różnicę albo powstanie nadpłata."
                        : $"Cofnięto rozliczenie {GetString(settlement, "PeriodKey")} dla {GetString(settlement, "TenantName")}. Lokator zobaczy informację o wycofaniu rachunku. Teraz możesz je ponownie przeliczyć, zatwierdzić i opublikować."
            });
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    private async Task SetSettlementBackToDraftAsync(
        Guid householdId,
        Guid settlementId,
        CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        var tableName = await FindSettlementTableAsync(
            connection,
            householdId,
            settlementId,
            cancellationToken);

        if (tableName is null)
        {
            throw new InvalidOperationException(
                "Nie udało się odnaleźć tabeli miesięcznych rozliczeń lokatorów w bazie danych.");
        }

        var columns = await ReadColumnsAsync(
            connection,
            tableName,
            cancellationToken);

        await using var command = connection.CreateCommand();

        var assignments = new List<string>
        {
            "\"Status\" = $status"
        };

        foreach (var column in new[]
                 {
                     "ApprovedAtUtc",
                     "ApprovedAt",
                     "PublishedAtUtc",
                     "PublishedAt"
                 })
        {
            if (columns.Contains(column))
                assignments.Add($"\"{column}\" = NULL");
        }

        if (columns.Contains("Version"))
            assignments.Add("\"Version\" = COALESCE(\"Version\", 0) + 1");

        command.CommandText =
            $"UPDATE \"{tableName}\" " +
            $"SET {string.Join(", ", assignments)} " +
            "WHERE \"Id\" = $id AND \"HouseholdId\" = $householdId;";

        AddParameter(command, "$status", TenantSettlementStatuses.Draft);
        AddParameter(command, "$id", settlementId);
        AddParameter(command, "$householdId", householdId);

        var affected = await command.ExecuteNonQueryAsync(cancellationToken);

        if (affected != 1)
            throw new InvalidOperationException("Nie udało się cofnąć rozliczenia do wersji roboczej.");
    }

    private async Task<string?> ReadSettlementStatusAsync(
        Guid householdId,
        Guid settlementId,
        CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        var tableName = await FindSettlementTableAsync(
            connection,
            householdId,
            settlementId,
            cancellationToken);

        if (tableName is null)
            return null;

        await using var command = connection.CreateCommand();

        command.CommandText =
            $"SELECT \"Status\" FROM \"{tableName}\" " +
            "WHERE \"Id\" = $id AND \"HouseholdId\" = $householdId LIMIT 1;";

        AddParameter(command, "$id", settlementId);
        AddParameter(command, "$householdId", householdId);

        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value?.ToString();
    }

    private static async Task<string?> FindSettlementTableAsync(
        DbConnection connection,
        Guid householdId,
        Guid settlementId,
        CancellationToken cancellationToken)
    {
        var candidates = new List<string>();

        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT name FROM sqlite_master " +
                "WHERE type = 'table' AND name NOT LIKE 'sqlite_%';";

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            while (await reader.ReadAsync(cancellationToken))
            {
                var name = reader.GetString(0);

                if (name.Contains("TenantSettlement", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("RentalSettlement", StringComparison.OrdinalIgnoreCase))
                {
                    candidates.Add(name);
                }
            }
        }

        foreach (var tableName in candidates)
        {
            var columns = await ReadColumnsAsync(
                connection,
                tableName,
                cancellationToken);

            if (!columns.Contains("Id")
                || !columns.Contains("HouseholdId")
                || !columns.Contains("Status"))
                continue;

            await using var probe = connection.CreateCommand();

            probe.CommandText =
                $"SELECT COUNT(1) FROM \"{tableName}\" " +
                "WHERE \"Id\" = $id AND \"HouseholdId\" = $householdId;";

            AddParameter(probe, "$id", settlementId);
            AddParameter(probe, "$householdId", householdId);

            var countRaw = await probe.ExecuteScalarAsync(cancellationToken);
            var count = countRaw is null ? 0L : Convert.ToInt64(countRaw);

            if (count == 1)
                return tableName;
        }

        return null;
    }

    private static async Task<HashSet<string>> ReadColumnsAsync(
        DbConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{tableName}\");";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
            result.Add(reader.GetString(1));

        return result;
    }

    private DbConnection CreateConnection()
    {
        var providerType = Type.GetType(
            "Microsoft.Data.Sqlite.SqliteConnection, Microsoft.Data.Sqlite",
            throwOnError: false);

        if (providerType is null)
            throw new InvalidOperationException("Nie znaleziono dostawcy Microsoft.Data.Sqlite.");

        var dataRoot = configuration["EDom:Data:RootPath"] ?? "App_Data";
        var databasePath = configuration["EDom:Data:DatabasePath"] ?? "Database";
        var databaseFileName = configuration["EDom:Data:DatabaseFileName"] ?? "e-dom.db";
        var busyTimeout =
            configuration.GetValue<int?>("EDom:Data:SqliteBusyTimeoutSeconds") ?? 5;

        var filePath = Path.GetFullPath(
            Path.Combine(
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

    private RentalActor CreateActor(WebUserContext current) =>
        new(
            current.UserAccountId,
            current.PersonId,
            current.HouseholdId,
            CorrelationIdMiddleware.Get(HttpContext),
            DateTime.UtcNow);

    private static bool IsEditableStatus(string? status) =>
        status is
            TenantSettlementStatuses.Draft
            or TenantSettlementStatuses.AwaitingData
            or TenantSettlementStatuses.ReadyForApproval;

    private static bool IsPaidStatus(string? status) =>
        status is
            TenantSettlementStatuses.Paid
            or TenantSettlementStatuses.PaidLate
            || string.Equals(status, "Overpaid", StringComparison.OrdinalIgnoreCase);

    private static Guid? EmptyToNull(Guid value) =>
        value == Guid.Empty ? null : value;

    private static IEnumerable<object> AsObjects(object? value)
    {
        if (value is not IEnumerable enumerable)
            yield break;

        foreach (var item in enumerable)
            if (item is not null)
                yield return item;
    }

    private static object? GetValue(object? source, params string[] names)
    {
        if (source is null)
            return null;

        var type = source.GetType();

        foreach (var name in names)
        {
            var property = type.GetProperty(
                name,
                BindingFlags.Public
                | BindingFlags.Instance
                | BindingFlags.IgnoreCase);

            if (property is not null)
                return property.GetValue(source);
        }

        return null;
    }

    private static string GetString(
        object source,
        string name,
        string fallback = "") =>
        GetValue(source, name)?.ToString() ?? fallback;

    private static bool GetBool(object source, string name)
    {
        var value = GetValue(source, name);
        return value is bool result && result;
    }

    private static Guid GetGuid(object source, params string[] names)
    {
        var value = GetValue(source, names);

        if (value is Guid guid)
            return guid;

        return Guid.TryParse(value?.ToString(), out var parsed)
            ? parsed
            : Guid.Empty;
    }

    private static Guid? GetNullableGuid(object source, params string[] names)
    {
        var value = GetValue(source, names);

        if (value is Guid guid)
            return guid == Guid.Empty ? null : guid;

        return Guid.TryParse(value?.ToString(), out var parsed)
               && parsed != Guid.Empty
            ? parsed
            : null;
    }

    private static long GetLong(object source, params string[] names)
    {
        var value = GetValue(source, names);

        if (value is null)
            return 0L;

        try
        {
            return Convert.ToInt64(value);
        }
        catch
        {
            return 0L;
        }
    }

    private static long? GetNullableLong(
        object source,
        params string[] names)
    {
        var value = GetValue(source, names);

        if (value is null)
            return null;

        try
        {
            return Convert.ToInt64(value);
        }
        catch
        {
            return null;
        }
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
}
