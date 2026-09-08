using Asp.Versioning;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using ProcureToPay.Api.ExceptionHandling;
using ProcureToPay.Api.Identity;
using ProcureToPay.Application;
using ProcureToPay.Infrastructure;
using Scalar.AspNetCore;

const string AngularDevelopmentCorsPolicy = "AngularDevelopment";

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();
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
        options.Events = new JwtBearerEvents
        {
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
    .AddSqlServer(sqlServerConnectionString, name: "sqlserver", tags: ["ready"]);

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

app.MapControllers();

app.Run();

public partial class Program
{
}
