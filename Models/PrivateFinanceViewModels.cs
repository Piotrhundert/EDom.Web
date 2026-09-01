using System.ComponentModel.DataAnnotations;
using EDom.Application.PrivateFinance;

namespace EDom.Web.Models;

public sealed class PrivateFinancePageViewModel
{
    public required PrivateFinanceOverview Overview { get; init; }
    public IReadOnlyList<(Guid Id, string Name)> Children { get; init; } = [];
    public IReadOnlyList<ContributionSubmissionVm> HouseholdContributions { get; init; } = Array.Empty<ContributionSubmissionVm>();
}
public sealed class AddPrivateAccountViewModel
{
    [Required, StringLength(160)] public string Name { get; set; } = string.Empty;
    [StringLength(160)] public string? BankName { get; set; }
    [Required, StringLength(50)] public string AccountType { get; set; } = "Personal";
    [Required, StringLength(3)] public string CurrencyCode { get; set; } = "PLN";
    [Range(-90000000000000d, 90000000000000d)] public decimal OpeningBalance { get; set; }
}
public sealed class AddIncomeSourceViewModel
{
    public Guid FinancialAccountId { get; set; }
    [Required, StringLength(160)] public string Name { get; set; } = "Wynagrodzenie";
    [Required, StringLength(50)] public string IncomeType { get; set; } = "Salary";
    [Range(0, 90000000000000d)] public decimal? PlannedAmount { get; set; }
    [Required, StringLength(3)] public string CurrencyCode { get; set; } = "PLN";
    [Required] public string Frequency { get; set; } = "Monthly";
    [Range(1,31)] public int? PlannedDayOfMonth { get; set; } = 10;
    public DateOnly ValidFrom { get; set; } = DateOnly.FromDateTime(DateTime.Today);
    public DateOnly? ValidTo { get; set; }
    public bool IsVariable { get; set; }
}
public sealed class ConfirmIncomeViewModel
{
    public Guid PlannedIncomeId { get; set; }
    public DateOnly ActualReceivedOn { get; set; } = DateOnly.FromDateTime(DateTime.Today);
    [Range(0, 90000000000000d)] public decimal ActualAmount { get; set; }
}
public sealed class AddBenefitViewModel
{
    public Guid BeneficiaryPersonId { get; set; }
    public Guid FinancialAccountId { get; set; }
    [Required, StringLength(160)] public string Name { get; set; } = "Świadczenie";
    [Required] public string BenefitType { get; set; } = "Other";
    [Range(0, 90000000000000d)] public decimal PlannedAmount { get; set; }
    [Required, StringLength(3)] public string CurrencyCode { get; set; } = "PLN";
    [Required] public string Frequency { get; set; } = "Monthly";
    public DateOnly ValidFrom { get; set; } = DateOnly.FromDateTime(DateTime.Today);
    public DateOnly? ValidTo { get; set; }
    [StringLength(200)] public string? Institution { get; set; }
    [StringLength(200)] public string? DecisionReference { get; set; }
}
public sealed class AddPrivateExpenseViewModel
{
    public Guid? FinancialAccountId { get; set; }
    [Required, StringLength(160)] public string Name { get; set; } = string.Empty;
    [Required, StringLength(80)] public string Category { get; set; } = "Other";
    [Range(0, 90000000000000d)] public decimal PlannedAmount { get; set; }
    [Required, StringLength(3)] public string CurrencyCode { get; set; } = "PLN";
    public DateOnly DueOn { get; set; } = DateOnly.FromDateTime(DateTime.Today);
    public Guid? ChildPersonId { get; set; }
    [Range(0, 90000000000000d)] public decimal? ChildAmount { get; set; }
}
public sealed class AddSubscriptionViewModel
{
    public Guid? FinancialAccountId { get; set; }
    public Guid? ChildPersonId { get; set; }
    [Required, StringLength(160)] public string Name { get; set; } = string.Empty;
    [StringLength(160)] public string? Provider { get; set; }
    [Required, StringLength(80)] public string Category { get; set; } = "Other";
    [Range(0, 90000000000000d)] public decimal PlannedAmount { get; set; }
    [Required, StringLength(3)] public string CurrencyCode { get; set; } = "PLN";
    [Required] public string Cycle { get; set; } = "Monthly";
    public DateOnly NextChargeOn { get; set; } = DateOnly.FromDateTime(DateTime.Today);
    public bool AutoRenew { get; set; } = true;
    [StringLength(100)] public string? PaymentMethodLabel { get; set; }
    [RegularExpression("^[0-9]{4}$", ErrorMessage = "Podaj wyłącznie 4 ostatnie cyfry.")] public string? PaymentMethodLast4 { get; set; }
    [Range(0,365)] public int ReminderDays { get; set; } = 7;
}
public sealed class UpdatePrivateRecordViewModel
{
    public string RecordType { get; set; } = string.Empty;
    public Guid RecordId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? SecondaryText { get; set; }
    public decimal? PlannedAmount { get; set; }
    public string CurrencyCode { get; set; } = "PLN";
    public DateOnly? DateValue { get; set; }
    public string? Status { get; set; }
}
