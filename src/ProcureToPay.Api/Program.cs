using Asp.Versioning;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Mvc;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using ProcureToPay.Api.ExceptionHandling;
using ProcureToPay.Api.Health;
using ProcureToPay.Api.Identity;
using ProcureToPay.Application;
using ProcureToPay.Infrastructure;
using ProcureToPay.Infrastructure.Persistence.Organization;
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
    .AddCheck<PolicyConfigurationHealthCheck>("policy-configuration", tags: ["ready"]);

var telemetryServiceName = builder.Configuration["OpenTelemetry:ServiceName"]
    ?? builder.Environment.ApplicationName;
builder.Services
    .AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService(telemetryServiceName))
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddOtlpExporter());

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

var app = builder.Build();

// Configure the HTTP request pipeline.
app.UseExceptionHandler();

app.UseHttpsRedirection();

if (app.Environment.IsDevelopment())
{
    app.UseCors(AngularDevelopmentCorsPolicy);
}

app.UseAuthentication();
app.UseMiddleware<CurrentUserProvisioningMiddleware>();

app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().WithDocumentPerVersion();
    app.MapScalarApiReference();
}

app.MapHealthChecks("/health");
app.MapHealthChecks("/health/bootstrap", new HealthCheckOptions
{
    Predicate = registration => registration.Name == "organization-bootstrap"
});
app.MapHealthChecks("/health/policy", new HealthCheckOptions
{
    Predicate = registration => registration.Name == "policy-configuration"
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
