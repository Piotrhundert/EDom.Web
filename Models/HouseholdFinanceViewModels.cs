using System.ComponentModel.DataAnnotations;
using EDom.Application.HouseholdFinance;

namespace EDom.Web.Models;

public sealed class HouseholdFinancePageViewModel
{
    public required HouseholdFinanceOverview Overview { get; init; }
    public IReadOnlyList<(Guid Id, string Name)> People { get; init; } = [];
    public bool CanManage { get; init; }
}

public sealed class AddContributionRuleViewModel
{
    public Guid PersonId { get; set; }
    [Required] public string Method { get; set; } = "Fixed";
    [Range(0, 90000000000000d)] public decimal? FixedAmount { get; set; }
    [Range(0, 100)] public decimal? Percent { get; set; }
    [Required] public string DuePolicyType { get; set; } = "FixedDay";
    [Range(0, 365)] public int DueDayOrOffset { get; set; } = 10;
    public DateOnly ValidFrom { get; set; } = DateOnly.FromDateTime(DateTime.Today);
    public DateOnly? ValidTo { get; set; }
}

public sealed class GenerateContributionViewModel
{
    public Guid ContributionRuleId { get; set; }
    public Guid PersonId { get; set; }
    [Required, RegularExpression("^[0-9]{4}-[0-9]{2}$")] public string PeriodKey { get; set; } = DateTime.Today.ToString("yyyy-MM");
    [Range(0, 90000000000000d)] public decimal? IncomeAmount { get; set; }
    public DateOnly? IncomeDate { get; set; }
    public bool IsDraft { get; set; } = true;
}

public sealed class SubmitContributionPaymentViewModel
{
    [Required, RegularExpression("^[0-9]{4}-[0-9]{2}$")] public string PeriodKey { get; set; } = DateTime.Today.ToString("yyyy-MM");
    [Range(0.01, 90000000000000d)] public decimal Amount { get; set; }
    [Required, StringLength(3)] public string CurrencyCode { get; set; } = "PLN";
    [Required] public string PaymentMethod { get; set; } = "Bank";
    public DateTime PaidAtUtc { get; set; } = DateTime.UtcNow;
    [StringLength(128)] public string? ProofFingerprint { get; set; }
    public Guid? ObligationId { get; set; }
    [Range(0, 90000000000000d)] public decimal? AllocateAmount { get; set; }
}

public sealed class ApproveContributionPaymentViewModel
{
    public Guid SubmissionId { get; set; }
    [Range(0.01, 90000000000000d)] public decimal ApprovedAmount { get; set; }
    public Guid? ObligationId { get; set; }
    [Range(0, 90000000000000d)] public decimal? AllocateAmount { get; set; }
    [StringLength(500)] public string? DecisionReason { get; set; }
}

public sealed class RejectContributionPaymentViewModel
{
    public Guid SubmissionId { get; set; }
    [Required, StringLength(500)] public string Reason { get; set; } = string.Empty;
}

public sealed class AddHouseholdInvoiceViewModel
{
    [Required, StringLength(120)] public string InvoiceNo { get; set; } = string.Empty;
    [Required, StringLength(200)] public string Supplier { get; set; } = string.Empty;
    [Required, StringLength(80)] public string CategoryCode { get; set; } = "Other";
    public DateOnly? PeriodFrom { get; set; }
    public DateOnly? PeriodTo { get; set; }
    public DateOnly IssuedOn { get; set; } = DateOnly.FromDateTime(DateTime.Today);
    public DateOnly DueDate { get; set; } = DateOnly.FromDateTime(DateTime.Today.AddDays(14));
    [Range(0, 90000000000000d)] public decimal? Net { get; set; }
    [Range(0, 90000000000000d)] public decimal? Vat { get; set; }
    [Range(0.01, 90000000000000d)] public decimal Gross { get; set; }
    [Required, StringLength(3)] public string CurrencyCode { get; set; } = "PLN";
}

public sealed class PayHouseholdInvoiceViewModel
{
    public Guid InvoiceId { get; set; }
    [Range(0.01, 90000000000000d)] public decimal Amount { get; set; }
    [Required] public string SourceType { get; set; } = "HouseholdBank";
    public DateTime PaidAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class SubmitPrivatePaidClaimViewModel
{
    public Guid? HouseholdInvoiceId { get; set; }
    [Range(0.01, 90000000000000d)] public decimal ClaimedAmount { get; set; }
    [Required, StringLength(3)] public string CurrencyCode { get; set; } = "PLN";
    public DateTime PaidAtUtc { get; set; } = DateTime.UtcNow;
    [Required] public string ProposedSettlementType { get; set; } = "Refund";
    [StringLength(1000)] public string? Description { get; set; }
}

public sealed class DecidePrivatePaidClaimViewModel
{
    public Guid ClaimId { get; set; }
    [Range(0, 90000000000000d)] public decimal? ApprovedAmount { get; set; }
    [Required, StringLength(500)] public string Reason { get; set; } = string.Empty;
    public bool Approve { get; set; }
}

public sealed class SettlePrivatePaidClaimViewModel
{
    public Guid ClaimId { get; set; }
    [Required] public string SettlementType { get; set; } = "Refund";
    [Range(0.01, 90000000000000d)] public decimal Amount { get; set; }
    public Guid? TargetObligationId { get; set; }
    [Required] public string Pocket { get; set; } = "Bank";
}

public sealed class AdjustContributionViewModel
{
    public Guid ObligationId { get; set; }
    public decimal AdjustmentAmount { get; set; }
    [Required, StringLength(500)] public string Reason { get; set; } = string.Empty;
}
