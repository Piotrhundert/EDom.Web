using EDom.Domain.Households;
using EDom.Domain.Identity;
using EDom.Infrastructure.Persistence;
using EDom.Web.Authorization;
using EDom.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EDom.Web.Controllers;

[Authorize]
[Route("FinanceReminders")]
public sealed class FinanceRemindersController(
    EDomDbContext db,
    WebAccessService access,
    FinanceReminderService reminderService) : Controller
{
    [HttpGet("Summary")]
    public async Task<IActionResult> Summary(CancellationToken cancellationToken)
    {
        var current = await access.GetCurrentAsync(cancellationToken);
        if (current is null) return Unauthorized();

        var account = await db.UserAccounts.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == current.UserAccountId && x.Status != UserAccountStatuses.Deleted, cancellationToken);
        var personId = account?.PersonId is Guid pid ? pid : Guid.Empty;
        if (personId == Guid.Empty) return NotFound();

        var belongs = await db.HouseholdMemberships.AsNoTracking().AnyAsync(x =>
            x.HouseholdId == current.HouseholdId && x.PersonId == personId &&
            x.Status == MembershipStatuses.Active && x.ValidTo == null,
            cancellationToken);
        if (!belongs) return Forbid();

        // Widok dashboardu jest wyłącznie odczytem. Publikacja powiadomień odbywa się
        // w FinancialReminderWorker, aby nie wykonywać zapisów do SQLite podczas
        // renderowania strony i zapytań RBAC nawigacji.
        var model = await reminderService.GetSummaryAsync(current.HouseholdId, current.UserAccountId, personId, cancellationToken);
        return Json(model);
    }
}
