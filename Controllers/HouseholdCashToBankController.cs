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
    private const string PackageVersion = "PKG-015n-FEAT-02";

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
                canTransfer = false,
                backendVersion = PackageVersion
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
            backendVersion = PackageVersion,
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
                    x.TransferDirection,
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
        string? direction,
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
                    $"[{PackageVersion}] Kwota wpłaty na konto musi być większa od 0."
            });
        }

        var transferDirection =
            NormalizeTransferDirection(
                direction);

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
                    $"[{PackageVersion}] Kwota wpłaty na konto musi być większa od 0."
            });
        }

        var sourceBalanceMinor =
            transferDirection == "BankToCash"
                ? overviewBefore.Ledger.BankBalanceMinor
                : overviewBefore.Ledger.CashBalanceMinor;

        var sourceName =
            transferDirection == "BankToCash"
                ? "koncie bankowym"
                : "kasie domowej";

        if (sourceBalanceMinor < amountMinor)
        {
            var missingMinor =
                amountMinor
                - Math.Max(
                    0,
                    sourceBalanceMinor);

            return BadRequest(new
            {
                message =
                    $"[{PackageVersion}] Brak wystarczających środków na {sourceName}. " +
                    $"Dostępne: {Math.Max(0, sourceBalanceMinor) / 100m:N2} {currency}, " +
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
            var databasePath =
                ResolveDatabasePath();

            await using var connection =
                CreateConnection(
                    databasePath);

            await connection.OpenAsync(
                cancellationToken);

            await using var transaction =
                await connection.BeginTransactionAsync(
                    cancellationToken);

            var schema =
                await DiscoverLedgerSchemaAsync(
                    connection,
                    transaction,
                    current.HouseholdId,
                    cancellationToken);

            if (schema is null)
            {
                var diagnostics =
                    await BuildSchemaDiagnosticsAsync(
                        connection,
                        transaction,
                        cancellationToken);

                throw new InvalidOperationException(
                    $"[{PackageVersion}] Nie udało się automatycznie odnaleźć ledgeru gospodarstwa. " +
                    $"Baza: {Path.GetFileName(databasePath)}. Wykryte tabele: {diagnostics}");
            }

            var transferId =
                Guid.NewGuid();

            var correlationId =
                CorrelationIdMiddleware.Get(
                    HttpContext);

            var directions =
                await ResolveDirectionsAsync(
                    connection,
                    transaction,
                    schema,
                    cancellationToken);

            var pockets =
                await ResolvePocketsAsync(
                    connection,
                    transaction,
                    schema,
                    cancellationToken);

            var entryType =
                await ResolveEntryTypeAsync(
                    connection,
                    transaction,
                    schema,
                    cancellationToken);

            var sourcePocket =
                transferDirection == "BankToCash"
                    ? pockets.BankPocket
                    : pockets.CashPocket;

            var targetPocket =
                transferDirection == "BankToCash"
                    ? pockets.CashPocket
                    : pockets.BankPocket;

            await InsertLedgerEntryAsync(
                connection,
                transaction,
                schema,
                entryId:
                    Guid.NewGuid(),
                direction:
                    directions.OutDirection,
                pocket:
                    sourcePocket,
                entryType:
                    entryType,
                amountMinor:
                    amountMinor,
                currencyCode:
                    currency,
                occurredAtUtc:
                    transferredAtUtc,
                referenceId:
                    transferId,
                correlationId:
                    correlationId,
                cancellationToken:
                    cancellationToken);

            await InsertLedgerEntryAsync(
                connection,
                transaction,
                schema,
                entryId:
                    Guid.NewGuid(),
                direction:
                    directions.InDirection,
                pocket:
                    targetPocket,
                entryType:
                    entryType,
                amountMinor:
                    amountMinor,
                currencyCode:
                    currency,
                occurredAtUtc:
                    transferredAtUtc,
                referenceId:
                    transferId,
                correlationId:
                    correlationId,
                cancellationToken:
                    cancellationToken);

            await transaction.CommitAsync(
                cancellationToken);

            var overviewAfter = await finance.GetOverviewAsync(
                current.HouseholdId,
                current.PersonId,
                true,
                cancellationToken);

            var expectedCash =
                transferDirection == "BankToCash"
                    ? overviewBefore.Ledger.CashBalanceMinor + amountMinor
                    : overviewBefore.Ledger.CashBalanceMinor - amountMinor;

            var expectedBank =
                transferDirection == "BankToCash"
                    ? overviewBefore.Ledger.BankBalanceMinor - amountMinor
                    : overviewBefore.Ledger.BankBalanceMinor + amountMinor;

            if (overviewAfter.Ledger.CashBalanceMinor != expectedCash
                || overviewAfter.Ledger.BankBalanceMinor != expectedBank)
            {
                throw new InvalidOperationException(
                    $"[{PackageVersion}] Wpisy transferu zostały zapisane, ale saldo nie zmieniło się zgodnie z oczekiwaniem. " +
                    $"Przed: kasa {overviewBefore.Ledger.CashBalanceMinor / 100m:N2}, bank {overviewBefore.Ledger.BankBalanceMinor / 100m:N2}. " +
                    $"Po: kasa {overviewAfter.Ledger.CashBalanceMinor / 100m:N2}, bank {overviewAfter.Ledger.BankBalanceMinor / 100m:N2}. " +
                    $"Oczekiwano: kasa {expectedCash / 100m:N2}, bank {expectedBank / 100m:N2} {currency}. " +
                    $"Schemat: ledger={schema.LedgerTable}, entries={schema.EntryTable}, " +
                    $"direction {directions.OutDirection}/{directions.InDirection}, pocket {pockets.CashPocket}/{pockets.BankPocket}.");
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
                    $"[{PackageVersion}] Transfer zmienił saldo łączne, co jest niedozwolone.");
            }

            var store =
                new HouseholdCashToBankTransferStore(
                    environment.ContentRootPath);

            await store.AddAsync(
                new HouseholdCashToBankTransferRecord
                {
                    Id =
                        transferId,
                    TransferDirection =
                        transferDirection,
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
                backendVersion = PackageVersion,
                amountMinor,
                currencyCode = currency,
                cashBalanceMinor =
                    overviewAfter.Ledger.CashBalanceMinor,
                bankBalanceMinor =
                    overviewAfter.Ledger.BankBalanceMinor,
                message =
                    transferDirection == "BankToCash"
                        ? $"Wypłacono {amountMinor / 100m:N2} {currency} z konta bankowego do kasy domowej. " +
                          $"Kasa: {overviewAfter.Ledger.CashBalanceMinor / 100m:N2} {currency}, " +
                          $"bank: {overviewAfter.Ledger.BankBalanceMinor / 100m:N2} {currency}. [{PackageVersion}]"
                        : $"Wpłacono {amountMinor / 100m:N2} {currency} z kasy domowej na konto bankowe. " +
                          $"Kasa: {overviewAfter.Ledger.CashBalanceMinor / 100m:N2} {currency}, " +
                          $"bank: {overviewAfter.Ledger.BankBalanceMinor / 100m:N2} {currency}. [{PackageVersion}]"
            });
        }
        catch (Exception ex)
        {
            return BadRequest(new
            {
                message =
                    ex.Message.StartsWith(
                        "[PKG-",
                        StringComparison.OrdinalIgnoreCase)
                        ? ex.Message
                        : $"[{PackageVersion}] {ex.Message}"
            });
        }
    }

    private async Task<LedgerSchema?> DiscoverLedgerSchemaAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid householdId,
        CancellationToken cancellationToken)
    {
        var tables =
            await ReadTablesAsync(
                connection,
                transaction,
                cancellationToken);

        var tableColumns =
            new Dictionary<string, List<ColumnInfo>>(
                StringComparer.OrdinalIgnoreCase);

        foreach (var table in tables)
        {
            tableColumns[table] =
                await ReadColumnInfosAsync(
                    connection,
                    transaction,
                    table,
                    cancellationToken);
        }

        var entryCandidates =
            tableColumns
                .Where(x =>
                    FindColumn(
                        x.Value,
                        "AmountMinor") is not null
                    && FindColumn(
                        x.Value,
                        "Direction") is not null
                    && FindColumn(
                        x.Value,
                        "Pocket") is not null
                    && FindColumn(
                        x.Value,
                        "CurrencyCode") is not null
                    && FindColumn(
                        x.Value,
                        "Id") is not null)
                .OrderByDescending(x =>
                    x.Key.Contains(
                        "Ledger",
                        StringComparison.OrdinalIgnoreCase))
                .ToArray();

        foreach (var entryCandidate in entryCandidates)
        {
            var entryTable =
                entryCandidate.Key;

            var entryColumns =
                entryCandidate.Value;

            var ledgerFk =
                FindColumn(
                    entryColumns,
                    "HouseholdLedgerId",
                    "LedgerId");

            var directHousehold =
                FindColumn(
                    entryColumns,
                    "HouseholdId");

            if (ledgerFk is null
                && directHousehold is null)
            {
                continue;
            }

            if (ledgerFk is not null)
            {
                var referencedLedgerTable =
                    await TryResolveReferencedTableAsync(
                        connection,
                        transaction,
                        entryTable,
                        ledgerFk.Name,
                        cancellationToken);

                var ledgerCandidates =
                    new List<string>();

                if (!string.IsNullOrWhiteSpace(
                        referencedLedgerTable))
                {
                    ledgerCandidates.Add(
                        referencedLedgerTable);
                }

                ledgerCandidates.AddRange(
                    tableColumns
                        .Where(x =>
                            FindColumn(
                                x.Value,
                                "Id") is not null
                            && FindColumn(
                                x.Value,
                                "HouseholdId") is not null)
                        .Select(x => x.Key)
                        .Where(x =>
                            !ledgerCandidates.Contains(
                                x,
                                StringComparer.OrdinalIgnoreCase)));

                foreach (var ledgerTable in ledgerCandidates)
                {
                    if (!tableColumns.TryGetValue(
                            ledgerTable,
                            out var ledgerColumns))
                    {
                        continue;
                    }

                    var ledgerIdColumn =
                        FindColumn(
                            ledgerColumns,
                            "Id");

                    var householdColumn =
                        FindColumn(
                            ledgerColumns,
                            "HouseholdId");

                    if (ledgerIdColumn is null
                        || householdColumn is null)
                    {
                        continue;
                    }

                    var ledgerRow =
                        await FindLedgerRowAsync(
                            connection,
                            transaction,
                            ledgerTable,
                            ledgerIdColumn.Name,
                            householdColumn.Name,
                            householdId,
                            cancellationToken);

                    if (ledgerRow is null)
                    {
                        continue;
                    }

                    return BuildSchema(
                        ledgerTable,
                        ledgerIdColumn.Name,
                        householdColumn.Name,
                        ledgerRow.Value.LedgerIdDbValue,
                        entryTable,
                        entryColumns,
                        ledgerFk.Name);
                }
            }

            if (directHousehold is not null)
            {
                var matching =
                    await HasHouseholdEntryAsync(
                        connection,
                        transaction,
                        entryTable,
                        directHousehold.Name,
                        householdId,
                        cancellationToken);

                if (!matching)
                {
                    continue;
                }

                return BuildSchema(
                    ledgerTable:
                        entryTable,
                    ledgerIdColumn:
                        directHousehold.Name,
                    householdColumn:
                        directHousehold.Name,
                    ledgerIdDbValue:
                        ConvertGuidForDb(
                            householdId,
                            await GetColumnPrototypeAsync(
                                connection,
                                transaction,
                                entryTable,
                                directHousehold.Name,
                                cancellationToken)),
                    entryTable:
                        entryTable,
                    entryColumns:
                        entryColumns,
                    ledgerFkColumn:
                        directHousehold.Name);
            }
        }

        return null;
    }

    private static LedgerSchema BuildSchema(
        string ledgerTable,
        string ledgerIdColumn,
        string householdColumn,
        object ledgerIdDbValue,
        string entryTable,
        List<ColumnInfo> entryColumns,
        string ledgerFkColumn)
    {
        string Required(
            params string[] names) =>
            FindColumn(
                    entryColumns,
                    names)?.Name
            ?? throw new InvalidOperationException(
                $"Brak wymaganej kolumny {string.Join("/", names)} w tabeli {entryTable}.");

        return new LedgerSchema(
            LedgerTable:
                ledgerTable,
            LedgerIdColumn:
                ledgerIdColumn,
            HouseholdColumn:
                householdColumn,
            LedgerIdDbValue:
                ledgerIdDbValue,
            EntryTable:
                entryTable,
            EntryIdColumn:
                Required("Id"),
            LedgerFkColumn:
                ledgerFkColumn,
            EntryTypeColumn:
                FindColumn(
                    entryColumns,
                    "EntryType",
                    "Type")?.Name,
            DirectionColumn:
                Required("Direction"),
            PocketColumn:
                Required("Pocket"),
            AmountColumn:
                Required("AmountMinor"),
            CurrencyColumn:
                Required("CurrencyCode"),
            OccurredAtColumn:
                FindColumn(
                    entryColumns,
                    "OccurredAtUtc",
                    "OccurredAt",
                    "CreatedAtUtc",
                    "BookedAtUtc")?.Name,
            ReferenceTypeColumn:
                FindColumn(
                    entryColumns,
                    "ReferenceType")?.Name,
            ReferenceIdColumn:
                FindColumn(
                    entryColumns,
                    "ReferenceId")?.Name,
            CorrelationIdColumn:
                FindColumn(
                    entryColumns,
                    "CorrelationId")?.Name,
            ReversalOfColumn:
                FindColumn(
                    entryColumns,
                    "ReversalOfId")?.Name,
            EntryColumns:
                entryColumns);
    }

    private async Task InsertLedgerEntryAsync(
        DbConnection connection,
        DbTransaction transaction,
        LedgerSchema schema,
        Guid entryId,
        string direction,
        string pocket,
        string entryType,
        long amountMinor,
        string currencyCode,
        DateTime occurredAtUtc,
        Guid referenceId,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var values =
            new Dictionary<string, object?>(
                StringComparer.OrdinalIgnoreCase);

        var idPrototype =
            await GetColumnPrototypeAsync(
                connection,
                transaction,
                schema.EntryTable,
                schema.EntryIdColumn,
                cancellationToken);

        values[schema.EntryIdColumn] =
            ConvertGuidForDb(
                entryId,
                idPrototype);

        values[schema.LedgerFkColumn] =
            schema.LedgerIdDbValue;

        if (schema.EntryTypeColumn is not null)
        {
            values[schema.EntryTypeColumn] =
                entryType;
        }

        values[schema.DirectionColumn] =
            direction;

        values[schema.PocketColumn] =
            pocket;

        values[schema.AmountColumn] =
            amountMinor;

        values[schema.CurrencyColumn] =
            currencyCode;

        if (schema.OccurredAtColumn is not null)
        {
            values[schema.OccurredAtColumn] =
                occurredAtUtc;
        }

        if (schema.ReferenceTypeColumn is not null)
        {
            values[schema.ReferenceTypeColumn] =
                "InternalPocketTransfer";
        }

        if (schema.ReferenceIdColumn is not null)
        {
            var prototype =
                await GetColumnPrototypeAsync(
                    connection,
                    transaction,
                    schema.EntryTable,
                    schema.ReferenceIdColumn,
                    cancellationToken);

            values[schema.ReferenceIdColumn] =
                ConvertGuidForDb(
                    referenceId,
                    prototype);
        }

        if (schema.CorrelationIdColumn is not null)
        {
            values[schema.CorrelationIdColumn] =
                correlationId;
        }

        if (schema.ReversalOfColumn is not null)
        {
            values[schema.ReversalOfColumn] =
                null;
        }

        foreach (var column in schema.EntryColumns)
        {
            if (values.ContainsKey(
                    column.Name))
            {
                continue;
            }

            if (column.PrimaryKey)
            {
                continue;
            }

            if (!column.NotNull)
            {
                continue;
            }

            if (column.DefaultValue is not null)
            {
                continue;
            }

            throw new InvalidOperationException(
                $"[{PackageVersion}] Tabela {schema.EntryTable} wymaga dodatkowej kolumny „{column.Name}”, której transfer nie potrafi bezpiecznie uzupełnić.");
        }

        var columns =
            values.Keys.ToArray();

        var parameters =
            columns
                .Select((_, index) =>
                    $"$p{index}")
                .ToArray();

        await using var command =
            connection.CreateCommand();

        command.Transaction =
            transaction;

        command.CommandText =
            $"INSERT INTO {Quote(schema.EntryTable)} " +
            $"({string.Join(", ", columns.Select(Quote))}) " +
            $"VALUES ({string.Join(", ", parameters)});";

        for (var index = 0;
             index < columns.Length;
             index++)
        {
            AddParameter(
                command,
                parameters[index],
                values[columns[index]]);
        }

        await command.ExecuteNonQueryAsync(
            cancellationToken);
    }

    private static async Task<DirectionMap> ResolveDirectionsAsync(
        DbConnection connection,
        DbTransaction transaction,
        LedgerSchema schema,
        CancellationToken cancellationToken)
    {
        var values =
            await ReadDistinctStringsAsync(
                connection,
                transaction,
                schema.EntryTable,
                schema.DirectionColumn,
                cancellationToken);

        var inDirection =
            Match(
                values,
                "Credit",
                "In",
                "Income",
                "Increase",
                "Deposit",
                "Inbound");

        var outDirection =
            Match(
                values,
                "Debit",
                "Out",
                "Expense",
                "Decrease",
                "Withdrawal",
                "Outbound");

        var createSql =
            await ReadCreateSqlAsync(
                connection,
                transaction,
                schema.EntryTable,
                cancellationToken);

        inDirection ??=
            ContainsToken(
                createSql,
                "Credit")
                ? "Credit"
                : null;

        outDirection ??=
            ContainsToken(
                createSql,
                "Debit")
                ? "Debit"
                : null;

        inDirection ??=
            "Credit";

        outDirection ??=
            "Debit";

        return new DirectionMap(
            inDirection,
            outDirection);
    }

    private static async Task<PocketMap> ResolvePocketsAsync(
        DbConnection connection,
        DbTransaction transaction,
        LedgerSchema schema,
        CancellationToken cancellationToken)
    {
        var values =
            await ReadDistinctStringsAsync(
                connection,
                transaction,
                schema.EntryTable,
                schema.PocketColumn,
                cancellationToken);

        var cash =
            Match(
                values,
                "Cash",
                "Gotowka",
                "Gotówka");

        var bank =
            Match(
                values,
                "Bank",
                "BankAccount",
                "Account");

        cash ??=
            "Cash";

        bank ??=
            "Bank";

        return new PocketMap(
            cash,
            bank);
    }

    private static async Task<string> ResolveEntryTypeAsync(
        DbConnection connection,
        DbTransaction transaction,
        LedgerSchema schema,
        CancellationToken cancellationToken)
    {
        if (schema.EntryTypeColumn is null)
        {
            return "Transfer";
        }

        var createSql =
            await ReadCreateSqlAsync(
                connection,
                transaction,
                schema.EntryTable,
                cancellationToken);

        if (ContainsToken(
                createSql,
                "Transfer"))
        {
            return "Transfer";
        }

        var values =
            await ReadDistinctStringsAsync(
                connection,
                transaction,
                schema.EntryTable,
                schema.EntryTypeColumn,
                cancellationToken);

        var preferred =
            Match(
                values,
                "Transfer",
                "Manual",
                "Adjustment",
                "Payment",
                "ContributionPayment",
                "InvoicePayment");

        if (preferred is not null)
        {
            return preferred;
        }

        return values.FirstOrDefault()
               ?? "Transfer";
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

    private static async Task<List<ColumnInfo>> ReadColumnInfosAsync(
        DbConnection connection,
        DbTransaction transaction,
        string tableName,
        CancellationToken cancellationToken)
    {
        var result =
            new List<ColumnInfo>();

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
                new ColumnInfo(
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

    private static ColumnInfo? FindColumn(
        IEnumerable<ColumnInfo> columns,
        params string[] names)
    {
        foreach (var name in names)
        {
            var exact =
                columns.FirstOrDefault(x =>
                    string.Equals(
                        x.Name,
                        name,
                        StringComparison.OrdinalIgnoreCase));

            if (exact is not null)
            {
                return exact;
            }
        }

        return null;
    }

    private static async Task<string?> TryResolveReferencedTableAsync(
        DbConnection connection,
        DbTransaction transaction,
        string tableName,
        string fromColumn,
        CancellationToken cancellationToken)
    {
        await using var command =
            connection.CreateCommand();

        command.Transaction =
            transaction;

        command.CommandText =
            $"PRAGMA foreign_key_list({Quote(tableName)});";

        await using var reader =
            await command.ExecuteReaderAsync(
                cancellationToken);

        while (await reader.ReadAsync(
                   cancellationToken))
        {
            var targetTable =
                reader.GetString(2);

            var sourceColumn =
                reader.GetString(3);

            if (string.Equals(
                    sourceColumn,
                    fromColumn,
                    StringComparison.OrdinalIgnoreCase))
            {
                return targetTable;
            }
        }

        return null;
    }

    private static async Task<(object LedgerIdDbValue, object HouseholdDbValue)?> FindLedgerRowAsync(
        DbConnection connection,
        DbTransaction transaction,
        string tableName,
        string idColumn,
        string householdColumn,
        Guid householdId,
        CancellationToken cancellationToken)
    {
        await using var command =
            connection.CreateCommand();

        command.Transaction =
            transaction;

        command.CommandText =
            $"SELECT {Quote(idColumn)}, {Quote(householdColumn)} " +
            $"FROM {Quote(tableName)};";

        await using var reader =
            await command.ExecuteReaderAsync(
                cancellationToken);

        while (await reader.ReadAsync(
                   cancellationToken))
        {
            var ledgerIdDb =
                reader.GetValue(0);

            var householdDb =
                reader.GetValue(1);

            if (TryReadGuid(
                    householdDb,
                    out var parsedHousehold)
                && parsedHousehold ==
                    householdId)
            {
                return (
                    ledgerIdDb,
                    householdDb);
            }
        }

        return null;
    }

    private static async Task<bool> HasHouseholdEntryAsync(
        DbConnection connection,
        DbTransaction transaction,
        string tableName,
        string householdColumn,
        Guid householdId,
        CancellationToken cancellationToken)
    {
        await using var command =
            connection.CreateCommand();

        command.Transaction =
            transaction;

        command.CommandText =
            $"SELECT {Quote(householdColumn)} " +
            $"FROM {Quote(tableName)};";

        await using var reader =
            await command.ExecuteReaderAsync(
                cancellationToken);

        while (await reader.ReadAsync(
                   cancellationToken))
        {
            if (TryReadGuid(
                    reader.GetValue(0),
                    out var parsed)
                && parsed == householdId)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryReadGuid(
        object? value,
        out Guid guid)
    {
        if (value is Guid direct)
        {
            guid =
                direct;
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

    private static object ConvertGuidForDb(
        Guid guid,
        object? prototype)
    {
        if (prototype is byte[])
        {
            return guid.ToByteArray();
        }

        if (prototype is Guid)
        {
            return guid;
        }

        return guid.ToString("D");
    }

    private static async Task<object?> GetColumnPrototypeAsync(
        DbConnection connection,
        DbTransaction transaction,
        string tableName,
        string columnName,
        CancellationToken cancellationToken)
    {
        await using var command =
            connection.CreateCommand();

        command.Transaction =
            transaction;

        command.CommandText =
            $"SELECT {Quote(columnName)} " +
            $"FROM {Quote(tableName)} " +
            $"WHERE {Quote(columnName)} IS NOT NULL " +
            "LIMIT 1;";

        var value =
            await command.ExecuteScalarAsync(
                cancellationToken);

        return value is DBNull
            ? null
            : value;
    }

    private static async Task<List<string>> ReadDistinctStringsAsync(
        DbConnection connection,
        DbTransaction transaction,
        string tableName,
        string columnName,
        CancellationToken cancellationToken)
    {
        var result =
            new List<string>();

        await using var command =
            connection.CreateCommand();

        command.Transaction =
            transaction;

        command.CommandText =
            $"SELECT DISTINCT {Quote(columnName)} " +
            $"FROM {Quote(tableName)} " +
            $"WHERE {Quote(columnName)} IS NOT NULL;";

        await using var reader =
            await command.ExecuteReaderAsync(
                cancellationToken);

        while (await reader.ReadAsync(
                   cancellationToken))
        {
            var value =
                reader.GetValue(0)?.ToString();

            if (!string.IsNullOrWhiteSpace(
                    value))
            {
                result.Add(
                    value);
            }
        }

        return result;
    }

    private static async Task<string> ReadCreateSqlAsync(
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
            SELECT COALESCE(sql, '')
            FROM sqlite_master
            WHERE type = 'table'
              AND name = $tableName
            LIMIT 1;
            """;

        AddParameter(
            command,
            "$tableName",
            tableName);

        var value =
            await command.ExecuteScalarAsync(
                cancellationToken);

        return value?.ToString()
               ?? string.Empty;
    }

    private static bool ContainsToken(
        string text,
        string token) =>
        text.Contains(
            token,
            StringComparison.OrdinalIgnoreCase);

    private static string? Match(
        IEnumerable<string> values,
        params string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            var match =
                values.FirstOrDefault(x =>
                    string.Equals(
                        x,
                        candidate,
                        StringComparison.OrdinalIgnoreCase));

            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    private static async Task<string> BuildSchemaDiagnosticsAsync(
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        var tables =
            await ReadTablesAsync(
                connection,
                transaction,
                cancellationToken);

        var summaries =
            new List<string>();

        foreach (var table in tables)
        {
            if (!table.Contains(
                    "ledger",
                    StringComparison.OrdinalIgnoreCase)
                && !table.Contains(
                    "finance",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var columns =
                await ReadColumnInfosAsync(
                    connection,
                    transaction,
                    table,
                    cancellationToken);

            summaries.Add(
                $"{table}({string.Join(",", columns.Select(x => x.Name))})");
        }

        if (summaries.Count == 0)
        {
            return string.Join(
                ", ",
                tables.Take(25));
        }

        return string.Join(
            " | ",
            summaries);
    }

    private string ResolveDatabasePath()
    {
        var dataRoot =
            configuration["EDom:Data:RootPath"]
            ?? "App_Data";

        var databasePath =
            configuration["EDom:Data:DatabasePath"]
            ?? "Database";

        var databaseFileName =
            configuration["EDom:Data:DatabaseFileName"]
            ?? "e-dom.db";

        var configured =
            Path.GetFullPath(
                Path.Combine(
                    environment.ContentRootPath,
                    dataRoot,
                    databasePath,
                    databaseFileName));

        if (System.IO.File.Exists(
                configured))
        {
            return configured;
        }

        var appData =
            Path.Combine(
                environment.ContentRootPath,
                "App_Data");

        if (System.IO.Directory.Exists(
                appData))
        {
            var fallback =
                System.IO.Directory
                    .EnumerateFiles(
                        appData,
                        "*.db",
                        SearchOption.AllDirectories)
                    .OrderByDescending(x =>
                        string.Equals(
                            Path.GetFileName(x),
                            "e-dom.db",
                            StringComparison.OrdinalIgnoreCase))
                    .FirstOrDefault();

            if (fallback is not null)
            {
                return fallback;
            }
        }

        throw new InvalidOperationException(
            $"[{PackageVersion}] Nie znaleziono pliku SQLite. Oczekiwano: {configured}");
    }

    private DbConnection CreateConnection(
        string databasePath)
    {
        var providerType =
            Type.GetType(
                "Microsoft.Data.Sqlite.SqliteConnection, Microsoft.Data.Sqlite",
                throwOnError: false);

        if (providerType is null)
        {
            throw new InvalidOperationException(
                $"[{PackageVersion}] Nie znaleziono dostawcy Microsoft.Data.Sqlite.");
        }

        var busyTimeout =
            configuration.GetValue<int?>(
                "EDom:Data:SqliteBusyTimeoutSeconds")
            ?? 5;

        var connectionString =
            $"Data Source={databasePath};" +
            $"Mode=ReadWrite;" +
            $"Cache=Shared;" +
            $"Foreign Keys=True;" +
            $"Default Timeout={busyTimeout}";

        return (DbConnection?)Activator.CreateInstance(
                   providerType,
                   connectionString)
               ?? throw new InvalidOperationException(
                   $"[{PackageVersion}] Nie udało się utworzyć połączenia SQLite.");
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

    private static string NormalizeTransferDirection(
        string? value) =>
        string.Equals(
            value,
            "BankToCash",
            StringComparison.OrdinalIgnoreCase)
            ? "BankToCash"
            : "CashToBank";

    private static string? Clean(
        string? value) =>
        string.IsNullOrWhiteSpace(
            value)
            ? null
            : value.Trim();

    private static string Quote(
        string identifier) =>
        "\"" +
        identifier.Replace(
            "\"",
            "\"\"") +
        "\"";

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

    private sealed record LedgerSchema(
        string LedgerTable,
        string LedgerIdColumn,
        string HouseholdColumn,
        object LedgerIdDbValue,
        string EntryTable,
        string EntryIdColumn,
        string LedgerFkColumn,
        string? EntryTypeColumn,
        string DirectionColumn,
        string PocketColumn,
        string AmountColumn,
        string CurrencyColumn,
        string? OccurredAtColumn,
        string? ReferenceTypeColumn,
        string? ReferenceIdColumn,
        string? CorrelationIdColumn,
        string? ReversalOfColumn,
        List<ColumnInfo> EntryColumns);

    private sealed record ColumnInfo(
        string Name,
        string DeclaredType,
        bool NotNull,
        object? DefaultValue,
        bool PrimaryKey);

    private sealed record DirectionMap(
        string InDirection,
        string OutDirection);

    private sealed record PocketMap(
        string CashPocket,
        string BankPocket);
}
