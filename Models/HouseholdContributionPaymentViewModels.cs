namespace EDom.Web.Models;

public sealed class HouseholdContributionPaymentsViewModel
{
    public string HouseholdName { get; init; } = "Gospodarstwo";
    public bool CanSubmit { get; init; }
    public bool CanApprove { get; init; }
    public Guid? SelectedObligationId { get; init; }
    public IReadOnlyList<ContributionObligationVm> MyObligations { get; init; } = Array.Empty<ContributionObligationVm>();
    public IReadOnlyList<ContributionSubmissionVm> MySubmissions { get; init; } = Array.Empty<ContributionSubmissionVm>();
    public IReadOnlyList<ContributionSubmissionVm> PendingForApproval { get; init; } = Array.Empty<ContributionSubmissionVm>();
}

public sealed record ContributionObligationVm(
    Guid Id,
    string PeriodKey,
    long RequiredMinor,
    long PaidMinor,
    long RemainingMinor,
    string CurrencyCode,
    DateOnly DueDate,
    string Status,
    bool HasPendingSubmission);

public sealed record ContributionSubmissionVm(
    Guid Id,
    Guid PersonId,
    string PersonName,
    string PeriodKey,
    long AmountMinor,
    string CurrencyCode,
    string PaymentMethod,
    DateTime PaidAtUtc,
    string Status,
    long? ApprovedAmountMinor,
    string? DecisionReason);
