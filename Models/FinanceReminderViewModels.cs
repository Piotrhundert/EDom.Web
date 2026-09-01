namespace EDom.Web.Models;

public sealed record FinanceReminderItemViewModel(
    string Type,
    string Category,
    string Title,
    string Description,
    DateOnly DueOn,
    int DaysUntil,
    long? AmountMinor,
    string? CurrencyCode,
    string Severity,
    string SourceType,
    string SourceId,
    int? LeadDays = null);

public sealed class FinanceReminderSummaryViewModel
{
    public DateOnly Today { get; init; }
    public IReadOnlyList<FinanceReminderItemViewModel> Items { get; init; } = Array.Empty<FinanceReminderItemViewModel>();
    public int DueTodayCount { get; init; }
    public int UpcomingCount { get; init; }
    public int OverdueCount { get; init; }
    public int SubscriptionCount { get; init; }
    public int IncomeCount { get; init; }
    public int PaymentCount { get; init; }
    public int HouseholdContributionCount { get; init; }
}
