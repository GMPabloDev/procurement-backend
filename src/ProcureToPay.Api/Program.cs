using Asp.Versioning;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using ProcureToPay.Api;
using ProcureToPay.Api.Authentication;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using ProcureToPay.Api.ExceptionHandling;
using ProcureToPay.Api.Health;
using ProcureToPay.Api.Identity;
using ProcureToPay.Api.Workers;
using ProcureToPay.Application;
using ProcureToPay.Infrastructure;
using ProcureToPay.Infrastructure.Persistence.Approval;
using ProcureToPay.Infrastructure.Persistence.Organization;
using ProcureToPay.Infrastructure.Persistence.Policy;
using Scalar.AspNetCore;

const string AngularDevelopmentCorsPolicy = "AngularDevelopment";

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();
builder.Services.Configure<ApiBehaviorOptions>(options =>
{
    options.InvalidModelStateResponseFactory = context =>
    {
        var problem = new ValidationProblemDetails(context.ModelState)
        {
            Status = StatusCodes.Status400BadRequest,
            Title = "Validation failed",
            Type = "/problems/validation",
            Instance = context.HttpContext.Request.Path
        };
        problem.Extensions["traceId"] = context.HttpContext.TraceIdentifier;
        return new BadRequestObjectResult(problem);
    };
});
builder.Services
    .AddApiVersioning(options =>
    {
        options.DefaultApiVersion = new ApiVersion(1, 0);
        options.AssumeDefaultVersionWhenUnspecified = true;
        options.ReportApiVersions = true;
        options.ApiVersionReader = new QueryStringApiVersionReader();
    })
    .AddMvc()
    .AddApiExplorer(options =>
    {
        options.GroupNameFormat = "'v'VVV";
        options.SubstituteApiVersionInUrl = true;
    })
    .AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();

var jwtBearerConfiguration = builder.Configuration.GetRequiredSection("Authentication:JwtBearer");
var authority = jwtBearerConfiguration["Authority"];
var audience = jwtBearerConfiguration["Audience"];

if (string.IsNullOrWhiteSpace(authority) || string.IsNullOrWhiteSpace(audience))
{
    throw new InvalidOperationException(
        "Authentication:JwtBearer:Authority and Authentication:JwtBearer:Audience must be configured.");
}

var requireHttpsMetadata = bool.TryParse(
    jwtBearerConfiguration["RequireHttpsMetadata"],
    out var configuredRequireHttpsMetadata)
    ? configuredRequireHttpsMetadata
    : true;

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(ApprovalServiceAuthentication.Scheme, options =>
    {
        // SPEC 05 REQ-06: the policy exception verifier trusts only service tokens issued for
        // its dedicated audience; org user tokens remain on the default scheme.
        options.Authority = authority;
        options.Audience = builder.Configuration["Authentication:ServiceJwt:Audience"]
            ?? ApprovalServiceAuthentication.DefaultAudience;
        options.RequireHttpsMetadata = requireHttpsMetadata;
        options.MapInboundClaims = false;
        options.TokenValidationParameters.ValidTypes = ["JWT"];
    })
    .AddJwtBearer(options =>
    {
        options.Authority = authority;
        options.Audience = audience;
        options.RequireHttpsMetadata = requireHttpsMetadata;
        options.MapInboundClaims = false;
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = context =>
            {
                if (context.Principal?.FindFirst("iss") is null ||
                    context.Principal.FindFirst("sub") is null)
                {
                    context.Fail("The token must contain issuer and subject.");
                }
                return Task.CompletedTask;
            },
            OnChallenge = async context =>
            {
                context.HandleResponse();
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.ContentType = "application/problem+json";
                await context.Response.WriteAsJsonAsync(new
                {
                    type = "/problems/authentication-required",
                    title = "Authentication required",
                    status = StatusCodes.Status401Unauthorized,
                    traceId = context.HttpContext.TraceIdentifier
                }, context.HttpContext.RequestAborted);
            }
        };
    });
builder.Services.AddAuthorization();

var sqlServerConnectionString = builder.Configuration.GetConnectionString("SqlServer")
    ?? throw new InvalidOperationException("Connection string 'ConnectionStrings:SqlServer' is not configured.");
builder.Services
    .AddHealthChecks()
    .AddSqlServer(sqlServerConnectionString, name: "sqlserver", tags: ["ready"])
    .AddCheck<OrganizationBootstrapHealthCheck>("organization-bootstrap", tags: ["ready"])
    .AddCheck<PolicyConfigurationHealthCheck>("policy-configuration", tags: ["ready"])
    .AddCheck<ApprovalHealthCheck>("approval", tags: ["ready"])
    .AddCheck<PolicyExceptionHealthCheck>("policy-exception", tags: ["ready"])
    .AddCheck<PurchaseRequestHealthCheck>("purchase-request", tags: ["ready"])
    .AddCheck<BudgetHealthCheck>("budget", tags: ["ready"])
    // SPEC 09 REQ-12: supplier owner, catalog, key provider, adapters, processor and storage.
    .AddCheck<SupplierHealthCheck>("supplier", tags: ["ready"])
    // SPEC 10 REQ-13/REQ-14: owner registrations, typed provider, approval adapter and storage.
    .AddCheck<SourcingHealthCheck>("sourcing", tags: ["ready"])
    .AddCheck<SupportingDocumentHealthCheck>("supporting-documents", tags: ["ready"]);

var telemetryServiceName = builder.Configuration["OpenTelemetry:ServiceName"]
    ?? builder.Environment.ApplicationName;
builder.Services
    .AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService(telemetryServiceName))
    .WithTracing(tracing => tracing
        .AddSource(PolicyTelemetry.SourceName)
        .AddSource(ApprovalTelemetry.SourceName)
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddOtlpExporter())
    .WithMetrics(metrics => metrics
        .AddMeter(ApprovalTelemetry.SourceName));

builder.Services.AddCors(options =>
{
    options.AddPolicy(AngularDevelopmentCorsPolicy, policy =>
    {
        policy
            .WithOrigins("http://localhost:4200", "https://localhost:4200")
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

// Productive workers (REQ-10): the outbox dispatcher and the reconciliation runs execute
// automatically. Tests opt out by default and may enable it explicitly to exercise the real
// background sweep; the host fails closed on a v1 baseline before starting.
if (builder.Configuration.GetValue("Approval:Worker:Enabled", !builder.Environment.IsEnvironment("Testing")))
{
    builder.Services.AddHostedService<ApprovalWorkflowWorker>();
    // SPEC 11 REQ-08: the supporting document owner worker is separate from Sourcing.
    builder.Services.AddHostedService<SupportingDocumentOwnerWorker>();
    // SPEC 11 REQ-10/REQ-12: the takeover preflight reopens or closes the command gate.
    builder.Services.AddHostedService<PurchaseOrderPreflightWorker>();
}

var app = builder.Build();

// Configure the HTTP request pipeline.
app.UseExceptionHandler();

// SPEC 06 REQ-11: the purchase request snapshot budget applies while the body is read.
app.UseMiddleware<PurchaseRequestSizeLimitMiddleware>();

app.UseHttpsRedirection();

if (app.Environment.IsDevelopment())
{
    app.UseCors(AngularDevelopmentCorsPolicy);
}

app.UseAuthentication();
app.UseMiddleware<CurrentUserProvisioningMiddleware>();

app.UseAuthorization();
// SPEC 11 REQ-11: state-changing commands of the module are refused while its preflight is unmet.
app.UseMiddleware<PurchaseOrderCommandGateMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().WithDocumentPerVersion();
    app.MapScalarApiReference();
}

// SPEC 11 REQ-10/REQ-12: commands are only enabled when every historical request-wide takeover is
// represented by its per-line rows; an unexpandable fence stops the host instead of serving commands.
// A database that cannot be read does not authorize anything either (every command fails closed with
// 503), so the preflight only refuses to start on a *readable* inconsistent state.
using (var preflightScope = app.Services.CreateScope())
{
    try
    {
        await preflightScope.ServiceProvider
            .GetRequiredService<ProcureToPay.Infrastructure.Persistence.PurchaseOrders.PurchaseOrderContractPreflight>()
            .EnsureTakeoverExpansionAsync();
    }
    catch (Exception exception) when (exception is
                                          ProcureToPay.Domain.Modules.PurchaseOrders
                                              .PurchaseOrderDependencyUnavailableException or
                                          Microsoft.Data.SqlClient.SqlException or
                                          Microsoft.EntityFrameworkCore.DbUpdateException or
                                          InvalidOperationException)
    {
        // The expected fail-closed outcomes (an inconsistent legacy fence or an unreadable database)
        // let the host start with the command gate closed: reads stay available, commands answer the
        // contractual 503 and the preflight worker retries until it can reopen them (REQ-10/REQ-12).
        preflightScope.ServiceProvider
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("PurchaseOrderContractPreflight")
            .LogWarning(
                exception,
                "The purchase order takeover preflight is not satisfied; commands stay unavailable "
                + "until it succeeds.");
    }
}

app.MapHealthChecks("/health");
app.MapHealthChecks("/health/bootstrap", new HealthCheckOptions
{
    Predicate = registration => registration.Name == "organization-bootstrap"
});
app.MapHealthChecks("/health/policy", new HealthCheckOptions
{
    Predicate = registration => registration.Name == "policy-configuration",
    ResponseWriter = async (context, report) =>
    {
        var entry = report.Entries.Values.Single();
        var code = report.Status == HealthStatus.Healthy
            ? "POLICY_CONFIGURATION_OK"
            : entry.Exception is not null
                ? "POLICY_CONFIGURATION_UNAVAILABLE"
                : entry.Description ?? "POLICY_CONFIGURATION_CORRUPT";
        await context.Response.WriteAsJsonAsync(new
        {
            status = report.Status.ToString().ToUpperInvariant(),
            code
        });
    }
});

app.MapHealthChecks("/health/approval", new HealthCheckOptions
{
    Predicate = registration => registration.Name == "approval",
    ResponseWriter = async (context, report) =>
    {
        var entry = report.Entries.Values.Single();
        var code = report.Status == HealthStatus.Healthy
            ? "APPROVAL_OK"
            : entry.Exception is not null
                ? "APPROVAL_UNAVAILABLE"
                : entry.Description ?? "APPROVAL_DEGRADED";
        await context.Response.WriteAsJsonAsync(new
        {
            status = report.Status.ToString().ToUpperInvariant(),
            code
        });
    }
});

app.MapHealthChecks("/health/purchase-request", new HealthCheckOptions
{
    Predicate = registration => registration.Name == "purchase-request",
    ResponseWriter = async (context, report) =>
    {
        var entry = report.Entries.Values.Single();
        var code = report.Status == HealthStatus.Healthy
            ? "PURCHASE_REQUEST_OK"
            : entry.Exception is not null
                ? "PURCHASE_REQUEST_UNAVAILABLE"
                : entry.Description ?? "PURCHASE_REQUEST_DEGRADED";
        await context.Response.WriteAsJsonAsync(new
        {
            status = report.Status.ToString().ToUpperInvariant(),
            code
        });
    }
});

app.MapHealthChecks("/health/budget", new HealthCheckOptions
{
    Predicate = registration => registration.Name == "budget",
    ResponseWriter = async (context, report) =>
    {
        var entry = report.Entries.Values.Single();
        var code = report.Status == HealthStatus.Healthy
            ? "BUDGET_OK"
            : entry.Description ?? "BUDGET_DEGRADED";
        await context.Response.WriteAsJsonAsync(new
        {
            status = report.Status.ToString().ToUpperInvariant(),
            code
        });
    }
});

app.MapHealthChecks("/health/supplier", new HealthCheckOptions
{
    Predicate = registration => registration.Name == "supplier",
    ResponseWriter = async (context, report) =>
    {
        var entry = report.Entries.Values.Single();
        var code = report.Status == HealthStatus.Healthy
            ? "SUPPLIER_OK"
            : entry.Description ?? "SUPPLIER_DEGRADED";
        await context.Response.WriteAsJsonAsync(new
        {
            status = report.Status.ToString().ToUpperInvariant(),
            code
        });
    }
});

app.MapControllers();

if (args.Contains("--organization-bootstrap", StringComparer.Ordinal) ||
    args.Contains("--organization-recover-admin", StringComparer.Ordinal))
{
    await using var scope = app.Services.CreateAsyncScope();
    var options = ReadBootstrapOptions(builder.Configuration);
    var bootstrapper = scope.ServiceProvider.GetRequiredService<OrganizationBootstrapper>();
    if (args.Contains("--organization-bootstrap", StringComparer.Ordinal))
    {
        _ = await bootstrapper.InitializeAsync(options);
    }
    else
    {
        _ = await bootstrapper.RecoverAdministratorAsync(options);
    }
    return;
}

app.Run();

static OrganizationBootstrapOptions ReadBootstrapOptions(IConfiguration configuration) => new(
    RequiredConfiguration(configuration, "Organization:Code"),
    RequiredConfiguration(configuration, "Organization:Name"),
    RequiredConfiguration(configuration, "Organization:BaseCurrency"),
    RequiredConfiguration(configuration, "Organization:TimeZoneId"),
    int.TryParse(configuration["Organization:FiscalYearStartMonth"], out var month) ? month : 0,
    RequiredConfiguration(configuration, "Organization:LegalEntityCode"),
    RequiredConfiguration(configuration, "Organization:LegalEntityName"),
    RequiredConfiguration(configuration, "Organization:InitialDepartmentCode"),
    RequiredConfiguration(configuration, "Organization:InitialDepartmentName"),
    RequiredConfiguration(configuration, "Organization:AdminIssuer"),
    RequiredConfiguration(configuration, "Organization:AdminSubject"),
    RequiredConfiguration(configuration, "Organization:Reason"));

static string RequiredConfiguration(IConfiguration configuration, string key) =>
    string.IsNullOrWhiteSpace(configuration[key])
        ? throw new InvalidOperationException($"Configuration '{key}' is required for organization operations.")
        : configuration[key]!;

public partial class Program
{
}
