using EDom.Application.Common.Configuration;
using EDom.Application.Operations;
using EDom.Infrastructure;
using EDom.Infrastructure.Administration;
using EDom.Infrastructure.Authorization;
using EDom.Infrastructure.Identity;
using EDom.Infrastructure.Households;
using EDom.Infrastructure.HouseholdFinance;
using EDom.Infrastructure.Collaboration;
using EDom.Infrastructure.Persistence;
using EDom.Infrastructure.PrivateFinance;
using EDom.Infrastructure.Property;
using EDom.Infrastructure.Rental;
using EDom.Infrastructure.Utilities;
using EDom.Infrastructure.Storage;
using EDom.Web.Authentication;
using EDom.Web.Authorization;
using EDom.Web.Infrastructure;
using Microsoft.AspNetCore.Authentication.Cookies;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews();
builder.Services.AddHttpContextAccessor();

var eDomOptions = builder.Configuration
    .GetRequiredSection(EDomOptions.SectionName)
    .Get<EDomOptions>()
    ?? throw new InvalidOperationException("Nie można wczytać sekcji konfiguracji 'EDom'.");

ValidateConfiguration(eDomOptions, builder.Environment);

var appPaths = new AppPaths(builder.Environment.ContentRootPath, eDomOptions);
appPaths.EnsureDirectoriesExist();

builder.Services.AddSingleton(eDomOptions);
builder.Services.AddSingleton(appPaths);
builder.Services.AddEDomPersistence(appPaths, eDomOptions);
builder.Services.AddEDomIdentity();
builder.Services.AddEDomAuthorization();
builder.Services.AddEDomPlatformFoundation();
builder.Services.AddScoped<WebAccessService>();

builder.Services
    .AddAuthentication("EDomCookie")
    .AddCookie("EDomCookie", options =>
    {
        options.Cookie.Name = "e-dom.session";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.SlidingExpiration = false;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.LoginPath = "/Account/Login";
        options.AccessDeniedPath = "/Account/AccessDenied";
        options.Events = new CookieAuthenticationEvents
        {
            OnRedirectToLogin = context =>
            {
                if (context.Request.Path.StartsWithSegments("/api"))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                }

                context.Response.Redirect(context.RedirectUri);
                return Task.CompletedTask;
            },
            OnRedirectToAccessDenied = context =>
            {
                if (context.Request.Path.StartsWithSegments("/api"))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                }

                context.Response.Redirect(context.RedirectUri);
                return Task.CompletedTask;
            }
        };
    });

var app = builder.Build();

var databaseStartup = await DatabaseBootstrapper.InitializeAsync(app.Services, eDomOptions);

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<GlobalExceptionMiddleware>();
app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseMiddleware<FirstRunMiddleware>();
app.UseRouting();
app.UseAuthentication();
app.UseMiddleware<SessionValidationMiddleware>();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new
{
    status = "Healthy",
    application = eDomOptions.Application.Name,
    package = eDomOptions.Application.Package,
    environment = app.Environment.EnvironmentName,
    canonicalUrlConfigured = !string.IsNullOrWhiteSpace(eDomOptions.Hosting.CanonicalUrl),
    database = new
    {
        status = databaseStartup.IsHealthy ? "Healthy" : "Unhealthy",
        schemaVersion = databaseStartup.SchemaVersion,
        integrityCheck = databaseStartup.IntegrityCheck,
        journalMode = databaseStartup.JournalMode,
        foreignKeysEnabled = databaseStartup.ForeignKeysEnabled,
        instanceId = databaseStartup.DatabaseInstanceId
    },
    identity = new
    {
        status = "Ready",
        sessionValidation = true,
        lockoutThreshold = 3,
        passwordHasher = "ASP.NET Core Identity PasswordHasher"
    },
    authorization = new
    {
        status = "Ready",
        defaultDeny = true,
        resourceScope = true,
        explicitDeny = true,
        timedDelegations = true,
        correlationId = true
    },
    ui = new
    {
        status = "Ready",
        firstRunWizard = true,
        rbacNavigation = true,
        protectedModuleRouting = true
    },
    householdFamily = new
    {
        status = "Ready",
        childWithoutAccount = true,
        guardianScope = true,
        familyGroups = true,
        residenceHistory = true,
        controlledProfileChanges = true
    },
    privateFinance = new
    {
        status = "Ready",
        ownerPrivacy = true,
        recurringIncome = true,
        childLinkedRecords = true,
        subscriptions = true,
        integerMoney = true
    },
    householdFinance = new
    {
        status = "Ready",
        centralLedger = true,
        contributions = true,
        approvalFlow = true,
        invoices = true,
        privatePaidClaims = true,
        periodClose = true,
        integerMoney = true
    },
    collaboration = new
    {
        status = "Ready",
        documents = true,
        downloadReauthorization = true,
        calendarOwnGuardianFamilyGroup = true,
        inAppNotifications = true,
        idempotentDelivery = true
    },
    propertyEquipment = new
    {
        status = "Ready",
        propertyHierarchy = true,
        statusHistory = true,
        equipmentAssignmentHistory = true,
        meterHierarchy = true,
        propertyScopeValidation = true
    },
    crud = new
    {
        status = "Ready",
        userManagement = true,
        editExistingModules = true,
        safeArchive = true,
        lastAdministratorProtection = true,
        rbacProtectedActions = true
    },
    rentalContracts = new
    {
        status = "Ready",
        tenantAssignedRoomScope = true,
        generatedLeasePdf = true,
        atomicActivation = true,
        amendments = true,
        depositsAndProtocols = true
    },
    utilitiesEngine = new
    {
        status = "Ready",
        readingApprovalAndCorrection = true,
        contractsTariffsAndRates = true,
        forecastsWithoutLedger = true,
        operatorInvoicesAndComparison = true,
        allocationPolicies = true,
        pelletTwelveMonthPlan = true,
        fixedPointRates = true
    }
}));

app.MapGet("/health/database", async (
    EDomDbContext db,
    DatabaseHealthService healthService,
    CancellationToken cancellationToken) =>
{
    var report = await healthService.CheckAsync(db, cancellationToken);
    return report.IsHealthy ? Results.Ok(report) : Results.Problem("Kontrola SQLite nie powiodła się.", statusCode: 503);
});

if (app.Environment.IsDevelopment())
{
    app.MapGet("/health/identity/self-test", async (
        Pkg003IdentitySelfTest selfTest,
        CancellationToken cancellationToken) =>
    {
        var result = await selfTest.RunAsync(cancellationToken);
        return Results.Ok(result);
    });

    app.MapGet("/health/rbac/self-test", async (
        Pkg004AuthorizationSelfTest selfTest,
        CancellationToken cancellationToken) =>
    {
        var result = await selfTest.RunAsync(cancellationToken);
        return Results.Ok(result);
    });

    app.MapGet("/health/backup/self-test", async (
        IBackupService backupService,
        CancellationToken cancellationToken) =>
    {
        var result = await backupService.CreateAndVerifyAsync("PKG-012-self-test", cancellationToken);
        return result.Verified ? Results.Ok(result) : Results.Problem("Test backup/restore nie powiódł się.", statusCode: 503);
    });

    app.MapGet("/health/household/self-test", async (
        Pkg006HouseholdFamilySelfTest selfTest,
        CancellationToken cancellationToken) =>
    {
        var result = await selfTest.RunAsync(cancellationToken);
        return Results.Ok(result);
    });

    app.MapGet("/health/private-finance/self-test", async (
        Pkg007PrivateFinanceSelfTest selfTest,
        CancellationToken cancellationToken) =>
    {
        var result = await selfTest.RunAsync(cancellationToken);
        return Results.Ok(result);
    });

    app.MapGet("/health/household-finance/self-test", async (
        Pkg008HouseholdFinanceSelfTest selfTest,
        CancellationToken cancellationToken) =>
    {
        var result = await selfTest.RunAsync(cancellationToken);
        return Results.Ok(result);
    });

    app.MapGet("/health/collaboration/self-test", async (
        Pkg009CollaborationSelfTest selfTest,
        CancellationToken cancellationToken) =>
    {
        var result = await selfTest.RunAsync(cancellationToken);
        return Results.Ok(result);
    });

    app.MapGet("/health/property/self-test", async (
        Pkg010PropertyEquipmentSelfTest selfTest,
        CancellationToken cancellationToken) =>
    {
        var result = await selfTest.RunAsync(cancellationToken);
        return Results.Ok(result);
    });

    app.MapGet("/health/crud/self-test", async (
        Pkg010bAdministrationCrudSelfTest selfTest,
        CancellationToken cancellationToken) =>
    {
        var result = await selfTest.RunAsync(cancellationToken);
        return Results.Ok(result);
    });

    app.MapGet("/health/rental/self-test", async (
        Pkg011RentalContractsSelfTest selfTest,
        CancellationToken cancellationToken) =>
    {
        var result = await selfTest.RunAsync(cancellationToken);
        return Results.Ok(result);
    });

    app.MapGet("/health/utilities/self-test", async (
        Pkg012UtilitiesSelfTest selfTest,
        CancellationToken cancellationToken) =>
    {
        var result = await selfTest.RunAsync(cancellationToken);
        return Results.Ok(result);
    });
}

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();

static void ValidateConfiguration(EDomOptions options, IHostEnvironment environment)
{
    if (string.IsNullOrWhiteSpace(options.Application.Name))
        throw new InvalidOperationException("EDom:Application:Name jest wymagane.");
    if (string.IsNullOrWhiteSpace(options.Data.RootPath))
        throw new InvalidOperationException("EDom:Data:RootPath jest wymagane.");
    if (string.IsNullOrWhiteSpace(options.Data.DatabaseFileName))
        throw new InvalidOperationException("EDom:Data:DatabaseFileName jest wymagane.");
    if (!Uri.TryCreate(options.Hosting.CanonicalUrl, UriKind.Absolute, out var canonicalUri))
        throw new InvalidOperationException("EDom:Hosting:CanonicalUrl musi być poprawnym adresem bezwzględnym.");
    if (!environment.IsDevelopment() && canonicalUri.Scheme != Uri.UriSchemeHttps)
        throw new InvalidOperationException("W środowisku innym niż Development CanonicalUrl musi używać HTTPS.");
    if (string.IsNullOrWhiteSpace(options.Secrets.Provider))
        throw new InvalidOperationException("EDom:Secrets:Provider musi jawnie określać zewnętrzne źródło sekretów.");
}

public partial class Program { }
