using System.Data.Common;
using EDom.Application.HouseholdFinance;
using EDom.Domain.Authorization;
using EDom.SharedKernel.Values;
using EDom.Web.Authorization;
using EDom.Web.Infrastructure;
using EDom.Web.Models;
using EDom.Web.Services;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EDom.Web.Controllers;

[Authorize]
[Route("HouseholdFinance/CashToBank")]
public sealed class HouseholdCashToBankController(
    WebAccessService access,
    IHouseholdFinanceService finance,
    IAntiforgery antiforgery,
    IConfiguration configuration,
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

        if (!await CanTransferAsync(
                current,
                cancellationToken))
        {
            return Json(new
            {
                canTransfer = false
            });
        }

        var overview = await finance.GetOverviewAsync(
            current.HouseholdId,
            current.PersonId,
            true,
            cancellationToken);

        var store = new HouseholdCashToBankTransferStore(
            environment.ContentRootPath);

        var history = await store.GetAsync(
            current.HouseholdId,
            cancellationToken);

        var requestToken = antiforgery
            .GetAndStoreTokens(HttpContext)
            .RequestToken;

        return Json(new
        {
            canTransfer = true,
            requestToken,
            ledger = new
            {
                overview.Ledger.CurrencyCode,
                overview.Ledger.CashBalanceMinor,
                overview.Ledger.BankBalanceMinor,
                totalBalanceMinor =
                    checked(
                        overview.Ledger.CashBalanceMinor
                        + overview.Ledger.BankBalanceMinor)
            },
            history = history
                .Take(20)
                .Select(x => new
                {
                    x.Id,
                    x.AmountMinor,
                    x.CurrencyCode,
                    x.TransferredAtUtc,
                    x.Note,
                    x.CashBeforeMinor,
                    x.CashAfterMinor,
                    x.BankBeforeMinor,
                    x.BankAfterMinor
                })
        });
    }

    [HttpPost("Transfer")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Transfer(
        decimal amount,
        DateTime transferredAtLocal,
        string? note,
        CancellationToken cancellationToken)
    {
        var current = await access.GetCurrentAsync(
            cancellationToken);

        if (current is null)
        {
            return Unauthorized();
        }

        if (!await CanTransferAsync(
                current,
                cancellationToken))
        {
            return Forbid();
        }

        if (amount <= 0m)
        {
            return BadRequest(new
            {
                message =
                    "Kwota wpłaty na konto musi być większa od 0."
            });
        }

        var overviewBefore = await finance.GetOverviewAsync(
            current.HouseholdId,
            current.PersonId,
            true,
            cancellationToken);

        var currency =
            overviewBefore.Ledger.CurrencyCode;

        var amountMinor =
            Money.FromMajorRounded(
                    amount,
                    currency)
                .AmountMinor;

        if (amountMinor <= 0)
        {
            return BadRequest(new
            {
                message =
                    "Kwota wpłaty na konto musi być większa od 0."
            });
        }

        if (overviewBefore.Ledger.CashBalanceMinor < amountMinor)
        {
            var missingMinor =
                amountMinor
                - Math.Max(
                    0,
                    overviewBefore.Ledger.CashBalanceMinor);

            return BadRequest(new
            {
                message =
                    $"Brak wystarczającej gotówki w kasie domowej. " +
                    $"Dostępne: {Math.Max(0, overviewBefore.Ledger.CashBalanceMinor) / 100m:N2} {currency}, " +
                    $"wymagane: {amountMinor / 100m:N2} {currency}, " +
                    $"brakuje: {missingMinor / 100m:N2} {currency}."
            });
        }

        var transferredAtUtc =
            DateTime.SpecifyKind(
                    transferredAtLocal,
                    DateTimeKind.Local)
                .ToUniversalTime();

        try
        {
            await InsertCashToBankLedgerTransferAsync(
                current.HouseholdId,
                amountMinor,
                currency,
                transferredAtUtc,
                CorrelationIdMiddleware.Get(HttpContext),
                cancellationToken);

            // Salda są liczone z LedgerEntries, dlatego po zapisie
            // ponownie odczytujemy je przez oficjalny serwis.
            var overviewAfter = await finance.GetOverviewAsync(
                current.HouseholdId,
                current.PersonId,
                true,
                cancellationToken);

            var expectedCash =
                overviewBefore.Ledger.CashBalanceMinor
                - amountMinor;

            var expectedBank =
                overviewBefore.Ledger.BankBalanceMinor
                + amountMinor;

            if (overviewAfter.Ledger.CashBalanceMinor != expectedCash
                || overviewAfter.Ledger.BankBalanceMinor != expectedBank)
            {
                // Nie wykonujemy automatycznego przeciwzapisu tutaj,
                // bo byłby kolejną operacją finansową. Komunikat podaje
                // dokładny stan do diagnostyki. W normalnym schemacie
                // LedgerEntries ten warunek nie powinien wystąpić.
                throw new InvalidOperationException(
                    $"Wpisy transferu zostały zapisane, ale kontrola salda nie dała oczekiwanego wyniku. " +
                    $"Oczekiwano: kasa {expectedCash / 100m:N2} {currency}, bank {expectedBank / 100m:N2} {currency}. " +
                    $"Odczytano: kasa {overviewAfter.Ledger.CashBalanceMinor / 100m:N2} {currency}, " +
                    $"bank {overviewAfter.Ledger.BankBalanceMinor / 100m:N2} {currency}.");
            }

            var totalBefore =
                checked(
                    overviewBefore.Ledger.CashBalanceMinor
                    + overviewBefore.Ledger.BankBalanceMinor);

            var totalAfter =
                checked(
                    overviewAfter.Ledger.CashBalanceMinor
                    + overviewAfter.Ledger.BankBalanceMinor);

            if (totalBefore != totalAfter)
            {
                throw new InvalidOperationException(
                    "Kontrola salda łącznego nie powiodła się. Transfer nie może zmieniać łącznej wartości środków gospodarstwa.");
            }

            var store =
                new HouseholdCashToBankTransferStore(
                    environment.ContentRootPath);

            await store.AddAsync(
                new HouseholdCashToBankTransferRecord
                {
                    Id = Guid.NewGuid(),
                    HouseholdId =
                        current.HouseholdId,
                    AmountMinor =
                        amountMinor,
                    CurrencyCode =
                        currency,
                    TransferredAtUtc =
                        transferredAtUtc,
                    Note =
                        Clean(note),
                    CashBeforeMinor =
                        overviewBefore.Ledger.CashBalanceMinor,
                    CashAfterMinor =
                        overviewAfter.Ledger.CashBalanceMinor,
                    BankBeforeMinor =
                        overviewBefore.Ledger.BankBalanceMinor,
                    BankAfterMinor =
                        overviewAfter.Ledger.BankBalanceMinor,
                    CreatedByUserAccountId =
                        current.UserAccountId,
                    CreatedAtUtc =
                        DateTime.UtcNow
                },
                cancellationToken);

            return Json(new
            {
                ok = true,
                amountMinor,
                currencyCode = currency,
                cashBalanceMinor =
                    overviewAfter.Ledger.CashBalanceMinor,
                bankBalanceMinor =
                    overviewAfter.Ledger.BankBalanceMinor,
                message =
                    $"Wpłacono {amountMinor / 100m:N2} {currency} z kasy domowej na konto bankowe. " +
                    $"Kasa: {overviewAfter.Ledger.CashBalanceMinor / 100m:N2} {currency}, " +
                    $"bank: {overviewAfter.Ledger.BankBalanceMinor / 100m:N2} {currency}. " +
                    "Łączna wartość środków gospodarstwa nie zmieniła się."
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

    private async Task InsertCashToBankLedgerTransferAsync(
        Guid householdId,
        long amountMinor,
        string currencyCode,
        DateTime occurredAtUtc,
        string correlationId,
        CancellationToken cancellationToken)
    {
        await using var connection =
            CreateConnection();

        await connection.OpenAsync(
            cancellationToken);

        await using var transaction =
            await connection.BeginTransactionAsync(
                cancellationToken);

        var ledger = await GetHouseholdLedgerAsync(
            connection,
            transaction,
            householdId,
            cancellationToken);

        if (ledger is null)
        {
            throw new InvalidOperationException(
                "Nie znaleziono aktywnego ledgeru Finansów domowych dla tego gospodarstwa.");
        }

        if (!string.Equals(
                ledger.Value.CurrencyCode,
                currencyCode,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Waluta ledgeru ({ledger.Value.CurrencyCode}) nie zgadza się z walutą salda ({currencyCode}).");
        }

        var columns = await ReadColumnsAsync(
            connection,
            transaction,
            "LedgerEntries",
            cancellationToken);

        var requiredColumns = new[]
        {
            "Id",
            "HouseholdLedgerId",
            "EntryType",
            "Direction",
            "Pocket",
            "AmountMinor",
            "CurrencyCode",
            "OccurredAtUtc",
            "ReferenceType",
            "ReferenceId",
            "CorrelationId",
            "ReversalOfId"
        };

        var missing = requiredColumns
            .Where(x => !columns.Contains(x))
            .ToArray();

        if (missing.Length > 0)
        {
            throw new InvalidOperationException(
                "Tabela LedgerEntries ma nieoczekiwany schemat. Brak kolumn: "
                + string.Join(", ", missing));
        }

        var directionMap =
            await ResolveDirectionMapAsync(
                connection,
                transaction,
                ledger.Value.Id,
                cancellationToken);

        var transferId =
            Guid.NewGuid();

        // CASH: wypływ.
        await InsertLedgerEntryAsync(
            connection,
            transaction,
            entryId: Guid.NewGuid(),
            householdLedgerId: ledger.Value.Id,
            entryType: "Transfer",
            direction: directionMap.OutDirection,
            pocket: "Cash",
            amountMinor: amountMinor,
            currencyCode: currencyCode,
            occurredAtUtc: occurredAtUtc,
            referenceType: "CashToBankTransfer",
            referenceId: transferId,
            correlationId: correlationId,
            cancellationToken: cancellationToken);

        // BANK: wpływ.
        await InsertLedgerEntryAsync(
            connection,
            transaction,
            entryId: Guid.NewGuid(),
            householdLedgerId: ledger.Value.Id,
            entryType: "Transfer",
            direction: directionMap.InDirection,
            pocket: "Bank",
            amountMinor: amountMinor,
            currencyCode: currencyCode,
            occurredAtUtc: occurredAtUtc,
            referenceType: "CashToBankTransfer",
            referenceId: transferId,
            correlationId: correlationId,
            cancellationToken: cancellationToken);

        await transaction.CommitAsync(
            cancellationToken);
    }

    private static async Task<(Guid Id, string CurrencyCode)?> GetHouseholdLedgerAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid householdId,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(
                connection,
                transaction,
                "HouseholdLedgers",
                cancellationToken)
            || !await TableExistsAsync(
                connection,
                transaction,
                "LedgerEntries",
                cancellationToken))
        {
            throw new InvalidOperationException(
                "Baza nie zawiera wymaganych tabel HouseholdLedgers/LedgerEntries.");
        }

        await using var command =
            connection.CreateCommand();

        command.Transaction =
            transaction;

        command.CommandText =
            """
            SELECT "Id", "BaseCurrencyCode"
            FROM "HouseholdLedgers"
            WHERE "HouseholdId" = $householdId
            ORDER BY
                CASE
                    WHEN lower(COALESCE("Status", '')) IN ('active', 'open', 'opened') THEN 0
                    ELSE 1
                END,
                "OpenedOn" DESC
            LIMIT 1;
            """;

        AddParameter(
            command,
            "$householdId",
            householdId.ToString("D"));

        await using var reader =
            await command.ExecuteReaderAsync(
                cancellationToken);

        if (!await reader.ReadAsync(
                cancellationToken))
        {
            return null;
        }

        var idRaw =
            reader.GetValue(0)?.ToString();

        if (!Guid.TryParse(
                idRaw,
                out var ledgerId))
        {
            throw new InvalidOperationException(
                "Id znalezionego HouseholdLedgers nie jest prawidłowym GUID.");
        }

        var currency =
            reader.IsDBNull(1)
                ? "PLN"
                : reader.GetString(1);

        return (
            ledgerId,
            currency);
    }

    private static async Task<DirectionMap> ResolveDirectionMapAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid householdLedgerId,
        CancellationToken cancellationToken)
    {
        // Najpierw odczytujemy faktycznie używane wartości.
        var usedDirections =
            new List<string>();

        await using (var command =
                     connection.CreateCommand())
        {
            command.Transaction =
                transaction;

            command.CommandText =
                """
                SELECT DISTINCT "Direction"
                FROM "LedgerEntries"
                WHERE "HouseholdLedgerId" = $ledgerId
                  AND "Direction" IS NOT NULL
                ORDER BY "Direction";
                """;

            AddParameter(
                command,
                "$ledgerId",
                householdLedgerId.ToString("D"));

            await using var reader =
                await command.ExecuteReaderAsync(
                    cancellationToken);

            while (await reader.ReadAsync(
                       cancellationToken))
            {
                if (!reader.IsDBNull(0))
                {
                    var value =
                        reader.GetString(0);

                    if (!string.IsNullOrWhiteSpace(
                            value))
                    {
                        usedDirections.Add(
                            value);
                    }
                }
            }
        }

        // W aktualnym modelu domenowym wpływ jest Credit,
        // a wypływ Debit. Jeżeli baza używa równoważnych nazw,
        // rozpoznajemy je bez narzucania konkretnej wielkości liter.
        var inDirection =
            FindDirection(
                usedDirections,
                "Credit",
                "In",
                "Income",
                "Increase",
                "Deposit");

        var outDirection =
            FindDirection(
                usedDirections,
                "Debit",
                "Out",
                "Expense",
                "Decrease",
                "Withdrawal");

        // Nowy ledger może jeszcze nie mieć wpisów.
        inDirection ??= "Credit";
        outDirection ??= "Debit";

        if (string.Equals(
                inDirection,
                outDirection,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Nie udało się rozróżnić kierunku wpływu i wypływu w LedgerEntries.");
        }

        return new DirectionMap(
            inDirection,
            outDirection);
    }

    private static string? FindDirection(
        IEnumerable<string> existing,
        params string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            var found =
                existing.FirstOrDefault(x =>
                    string.Equals(
                        x,
                        candidate,
                        StringComparison.OrdinalIgnoreCase));

            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private static async Task InsertLedgerEntryAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid entryId,
        Guid householdLedgerId,
        string entryType,
        string direction,
        string pocket,
        long amountMinor,
        string currencyCode,
        DateTime occurredAtUtc,
        string referenceType,
        Guid referenceId,
        string correlationId,
        CancellationToken cancellationToken)
    {
        await using var command =
            connection.CreateCommand();

        command.Transaction =
            transaction;

        command.CommandText =
            """
            INSERT INTO "LedgerEntries"
            (
                "Id",
                "HouseholdLedgerId",
                "EntryType",
                "Direction",
                "Pocket",
                "AmountMinor",
                "CurrencyCode",
                "OccurredAtUtc",
                "ReferenceType",
                "ReferenceId",
                "CorrelationId",
                "ReversalOfId"
            )
            VALUES
            (
                $id,
                $ledgerId,
                $entryType,
                $direction,
                $pocket,
                $amount,
                $currency,
                $occurredAt,
                $referenceType,
                $referenceId,
                $correlationId,
                NULL
            );
            """;

        AddParameter(
            command,
            "$id",
            entryId.ToString("D"));

        AddParameter(
            command,
            "$ledgerId",
            householdLedgerId.ToString("D"));

        AddParameter(
            command,
            "$entryType",
            entryType);

        AddParameter(
            command,
            "$direction",
            direction);

        AddParameter(
            command,
            "$pocket",
            pocket);

        AddParameter(
            command,
            "$amount",
            amountMinor);

        AddParameter(
            command,
            "$currency",
            currencyCode);

        AddParameter(
            command,
            "$occurredAt",
            occurredAtUtc);

        AddParameter(
            command,
            "$referenceType",
            referenceType);

        AddParameter(
            command,
            "$referenceId",
            referenceId.ToString("D"));

        AddParameter(
            command,
            "$correlationId",
            correlationId);

        await command.ExecuteNonQueryAsync(
            cancellationToken);
    }

    private static async Task<bool> TableExistsAsync(
        DbConnection connection,
        DbTransaction transaction,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var command =
            connection.CreateCommand();

        command.Transaction =
            transaction;

        command.CommandText =
            """
            SELECT COUNT(1)
            FROM sqlite_master
            WHERE type = 'table'
              AND name = $tableName;
            """;

        AddParameter(
            command,
            "$tableName",
            tableName);

        var result =
            await command.ExecuteScalarAsync(
                cancellationToken);

        return result is not null
               && Convert.ToInt64(
                   result) > 0;
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
            $"PRAGMA table_info(\"{tableName}\");";

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

    private DbConnection CreateConnection()
    {
        var providerType =
            Type.GetType(
                "Microsoft.Data.Sqlite.SqliteConnection, Microsoft.Data.Sqlite",
                throwOnError: false);

        if (providerType is null)
        {
            throw new InvalidOperationException(
                "Nie znaleziono dostawcy Microsoft.Data.Sqlite.");
        }

        var dataRoot =
            configuration["EDom:Data:RootPath"]
            ?? "App_Data";

        var databasePath =
            configuration["EDom:Data:DatabasePath"]
            ?? "Database";

        var databaseFileName =
            configuration["EDom:Data:DatabaseFileName"]
            ?? "e-dom.db";

        var busyTimeout =
            configuration.GetValue<int?>(
                "EDom:Data:SqliteBusyTimeoutSeconds")
            ?? 5;

        var filePath =
            Path.GetFullPath(
                Path.Combine(
                    environment.ContentRootPath,
                    dataRoot,
                    databasePath,
                    databaseFileName));

        var connectionString =
            $"Data Source={filePath};" +
            $"Mode=ReadWrite;" +
            $"Cache=Shared;" +
            $"Foreign Keys=True;" +
            $"Default Timeout={busyTimeout}";

        return (DbConnection?)Activator.CreateInstance(
                   providerType,
                   connectionString)
               ?? throw new InvalidOperationException(
                   "Nie udało się utworzyć połączenia SQLite.");
    }

    private Task<bool> CanTransferAsync(
        WebUserContext current,
        CancellationToken cancellationToken) =>
        access.CanAsync(
            "householdfinance.invoice.pay",
            ResourceScopeTypes.Household,
            current.HouseholdId.ToString("D"),
            ownerPersonId:
                current.PersonId,
            resourceType:
                "HouseholdFinance",
            resourceId:
                current.HouseholdId.ToString("D"),
            cancellationToken:
                cancellationToken);

    private static string? Clean(
        string? value) =>
        string.IsNullOrWhiteSpace(
            value)
            ? null
            : value.Trim();

    private static void AddParameter(
        DbCommand command,
        string name,
        object? value)
    {
        var parameter =
            command.CreateParameter();

        parameter.ParameterName =
            name;

        parameter.Value =
            value
            ?? DBNull.Value;

        command.Parameters.Add(
            parameter);
    }

    private sealed record DirectionMap(
        string InDirection,
        string OutDirection);
}
