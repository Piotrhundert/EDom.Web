using EDom.Application.Collaboration;
using EDom.Application.HouseholdFinance;
using EDom.Infrastructure.Persistence;
using EDom.Web.Models;
using Microsoft.EntityFrameworkCore;

namespace EDom.Web.Services;

public sealed class FinanceReminderService(
    EDomDbContext db,
    ICollaborationService collaborationService,
    IHouseholdFinanceService householdFinanceService)
{
    private const int DefaultPrivatePaymentLeadDays = 3;
    private const int DefaultContributionLeadDays = 3;
    private const int IncomeLeadDays = 1;
    private const int SummaryHorizonDays = 14;

    public async Task<FinanceReminderSummaryViewModel> GetSummaryAsync(
        Guid householdId,
        Guid accountId,
        Guid personId,
        CancellationToken cancellationToken = default)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var items = await BuildItemsAsync(householdId, personId, today, SummaryHorizonDays, cancellationToken);

        return new FinanceReminderSummaryViewModel
        {
            Today = today,
            Items = items
                .OrderBy(x => x.DueOn)
                .ThenBy(x => x.Category, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(x => x.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToArray(),
            DueTodayCount = items.Count(x => x.DaysUntil == 0),
            UpcomingCount = items.Count(x => x.DaysUntil > 0),
            OverdueCount = items.Count(x => x.DaysUntil < 0),
            SubscriptionCount = items.Count(x => x.Type == "Subscription"),
            IncomeCount = items.Count(x => x.Type == "Income"),
            PaymentCount = items.Count(x => x.Type == "Payment"),
            HouseholdContributionCount = items.Count(x => x.Type == "HouseholdContribution")
        };
    }

    public async Task<int> PublishForAccountAsync(
        Guid householdId,
        Guid accountId,
        Guid personId,
        CancellationToken cancellationToken = default)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var items = await BuildItemsAsync(householdId, personId, today, SummaryHorizonDays, cancellationToken);
        var published = 0;

        foreach (var item in items)
        {
            var shouldPublish = item.Type switch
            {
                "Subscription" => ShouldPublishSubscription(item),
                "Income" => item.DaysUntil is >= 0 and <= IncomeLeadDays,
                "Payment" => item.DaysUntil <= DefaultPrivatePaymentLeadDays,
                "HouseholdContribution" => item.DaysUntil <= DefaultContributionLeadDays,
                _ => false
            };

            if (!shouldPublish) continue;

            var stage = item.DaysUntil switch
            {
                < 0 => "overdue",
                0 => "today",
                _ => "soon"
            };

            var notificationType = item.Type switch
            {
                "Subscription" => "PrivateSubscriptionDue",
                "Income" => "PrivateIncomeExpected",
                "Payment" => "PrivatePaymentDue",
                "HouseholdContribution" when item.DaysUntil < 0 => "HouseholdContributionOverdue",
                "HouseholdContribution" => "HouseholdContributionDue",
                _ => "FinancialReminder"
            };

            var title = item.DaysUntil switch
            {
                < 0 => $"Po terminie: {item.Title}",
                0 => $"Dzisiaj: {item.Title}",
                1 => $"Jutro: {item.Title}",
                _ => $"Nadchodzące: {item.Title}"
            };

            var amount = item.AmountMinor.HasValue && !string.IsNullOrWhiteSpace(item.CurrencyCode)
                ? $" Kwota: {item.AmountMinor.Value / 100m:0.00} {item.CurrencyCode}."
                : string.Empty;
            var message = $"{item.Description} Termin: {item.DueOn:yyyy-MM-dd}.{amount}";
            var idempotencyKey = $"finance-reminder:{item.Type}:{item.SourceId}:{item.DueOn:yyyyMMdd}:{stage}";
            var correlationId = $"finance-reminder-{Guid.NewGuid():N}";
            var dueAtUtc = item.DueOn.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

            await collaborationService.PublishNotificationAsync(
                new PublishNotificationRequest(
                    householdId,
                    accountId,
                    personId,
                    notificationType,
                    title,
                    message,
                    idempotencyKey,
                    correlationId,
                    item.SourceType,
                    item.SourceId,
                    dueAtUtc),
                cancellationToken);
            published++;
        }

        return published;
    }

    private async Task<List<FinanceReminderItemViewModel>> BuildItemsAsync(
        Guid householdId,
        Guid personId,
        DateOnly today,
        int horizonDays,
        CancellationToken cancellationToken)
    {
        var end = today.AddDays(horizonDays);
        var start = today.AddDays(-30);
        var result = new List<FinanceReminderItemViewModel>();

        var subscriptions = await db.Subscriptions.AsNoTracking()
                        .Where(x => EF.Property<Guid>(x, "HouseholdId") == householdId &&
                EF.Property<Guid>(x, "OwnerPersonId") == personId &&
                EF.Property<string>(x, "Status") == "Active")
            .Select(x => new
            {
                Id = EF.Property<Guid>(x, "Id"),
                Name = EF.Property<string>(x, "Name"),
                Provider = EF.Property<string?>(x, "Provider"),
                AmountMinor = EF.Property<long>(x, "PlannedAmountMinor"),
                CurrencyCode = EF.Property<string>(x, "CurrencyCode"),
                NextChargeOn = EF.Property<DateOnly>(x, "NextChargeOn"),
                ReminderDays = EF.Property<int>(x, "ReminderDays")
            })
            .ToListAsync(cancellationToken);

        foreach (var row in subscriptions.Where(x => x.NextChargeOn >= start && x.NextChargeOn <= end))
        {
            var days = row.NextChargeOn.DayNumber - today.DayNumber;
            result.Add(new FinanceReminderItemViewModel(
                "Subscription",
                "Subskrypcje",
                row.Name,
                string.IsNullOrWhiteSpace(row.Provider) ? "Nadchodzące obciążenie subskrypcji." : $"Nadchodzące obciążenie: {row.Provider}.",
                row.NextChargeOn,
                days,
                row.AmountMinor,
                row.CurrencyCode,
                Severity(days),
                "Subscription",
                row.Id.ToString("D"),
                row.ReminderDays));
        }

        var incomeSources = await db.IncomeSources.AsNoTracking()
                        .Where(x => EF.Property<Guid>(x, "HouseholdId") == householdId &&
                EF.Property<Guid>(x, "OwnerPersonId") == personId &&
                EF.Property<string>(x, "Status") == "Active")
            .Select(x => new
            {
                Id = EF.Property<Guid>(x, "Id"),
                Name = EF.Property<string>(x, "Name"),
                Frequency = EF.Property<string>(x, "Frequency"),
                PlannedDay = EF.Property<int?>(x, "PlannedDayOfMonth"),
                PlannedAmountMinor = EF.Property<long?>(x, "PlannedAmountMinor"),
                CurrencyCode = EF.Property<string>(x, "CurrencyCode")
            })
            .ToListAsync(cancellationToken);

        foreach (var row in incomeSources.Where(x => x.PlannedDay.HasValue))
        {
            var due = NextMonthlyDate(today, row.PlannedDay!.Value);
            if (due > end) continue;
            var days = due.DayNumber - today.DayNumber;
            result.Add(new FinanceReminderItemViewModel(
                "Income",
                "Dochody",
                row.Name,
                days == 0 ? "Planowany wpływ dochodu przypada dzisiaj." : "Zbliża się planowany termin wpływu dochodu.",
                due,
                days,
                row.PlannedAmountMinor,
                row.CurrencyCode,
                Severity(days),
                "IncomeSource",
                row.Id.ToString("D")));
        }

        var expenses = await db.PrivateExpenses.AsNoTracking()
                        .Where(x => EF.Property<Guid>(x, "HouseholdId") == householdId &&
                EF.Property<Guid>(x, "OwnerPersonId") == personId)
            .Select(x => new
            {
                Id = EF.Property<Guid>(x, "Id"),
                Name = EF.Property<string>(x, "Name"),
                PlannedAmountMinor = EF.Property<long>(x, "PlannedAmountMinor"),
                CurrencyCode = EF.Property<string>(x, "CurrencyCode"),
                DueOn = EF.Property<DateOnly>(x, "DueOn"),
                Status = EF.Property<string>(x, "Status"),
                SubscriptionId = EF.Property<Guid?>(x, "SubscriptionId")
            })
            .ToListAsync(cancellationToken);

        foreach (var row in expenses.Where(x => x.SubscriptionId == null && !IsClosedExpenseStatus(x.Status) && x.DueOn >= start && x.DueOn <= end))
        {
            var days = row.DueOn.DayNumber - today.DayNumber;
            result.Add(new FinanceReminderItemViewModel(
                "Payment",
                "Płatności",
                row.Name,
                days < 0 ? "Prywatna płatność jest po terminie." : "Nadchodzi termin prywatnej płatności.",
                row.DueOn,
                days,
                row.PlannedAmountMinor,
                row.CurrencyCode,
                Severity(days),
                "PrivateExpense",
                row.Id.ToString("D")));
        }

        try
        {
            var household = await householdFinanceService.GetOverviewAsync(householdId, personId, false, cancellationToken);
            foreach (var row in household.Obligations.Where(x => x.RemainingMinor > 0 && x.DueDate >= start && x.DueDate <= end))
            {
                var days = row.DueDate.DayNumber - today.DayNumber;
                result.Add(new FinanceReminderItemViewModel(
                    "HouseholdContribution",
                    "Wpłata dla domu",
                    $"Wpłata do domu · {row.PeriodKey}",
                    days < 0 ? "Cykliczna wpłata do gospodarstwa jest po terminie." : "Zbliża się termin cyklicznej wpłaty do gospodarstwa.",
                    row.DueDate,
                    days,
                    row.RemainingMinor,
                    row.CurrencyCode,
                    Severity(days),
                    "ContributionObligation",
                    row.Id.ToString("D")));
            }
        }
        catch (InvalidOperationException)
        {
            // Brak aktywnego ledgeru/reguły nie powinien blokować prywatnych przypomnień.
        }

        return result;
    }

    private static bool ShouldPublishSubscription(FinanceReminderItemViewModel item)
    {
        var leadDays = Math.Clamp(item.LeadDays ?? 3, 0, 30);
        return item.DaysUntil is >= 0 && item.DaysUntil <= leadDays;
    }

    private static bool IsClosedExpenseStatus(string? status)
        => status is not null && (status.Equals("Paid", StringComparison.OrdinalIgnoreCase)
            || status.Equals("Cancelled", StringComparison.OrdinalIgnoreCase)
            || status.Equals("Canceled", StringComparison.OrdinalIgnoreCase)
            || status.Equals("Archived", StringComparison.OrdinalIgnoreCase));

    private static DateOnly NextMonthlyDate(DateOnly today, int dayOfMonth)
    {
        static DateOnly ForMonth(int year, int month, int day)
            => new(year, month, Math.Min(Math.Max(day, 1), DateTime.DaysInMonth(year, month)));

        var candidate = ForMonth(today.Year, today.Month, dayOfMonth);
        if (candidate >= today) return candidate;
        var next = today.AddMonths(1);
        return ForMonth(next.Year, next.Month, dayOfMonth);
    }

    private static string Severity(int daysUntil)
        => daysUntil < 0 ? "Overdue" : daysUntil == 0 ? "Today" : daysUntil <= 3 ? "Soon" : "Future";
}
