using System.Diagnostics;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
// pi-lens-ignore: lsp:CS0234
using ProcureToPay.Domain.Modules.Organization;
using ProcureToPay.Infrastructure.Persistence;
// pi-lens-ignore: lsp:CS0234
using ProcureToPay.Infrastructure.Persistence.Organization;
using Testcontainers.MsSql;

namespace ProcureToPay.IntegrationTests.Organization;

public sealed class OrganizationBootstrapperTests
{
    [Fact]
    public async Task Bootstrap_is_idempotent_and_recovery_adds_audited_admin()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var sqlServer = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest")
            .WithPassword("ProcureToPay_test_2026!")
            .Build();
        await sqlServer.StartAsync(cancellationToken);

        var options = new DbContextOptionsBuilder<ProcureToPayDbContext>()
            .UseSqlServer(sqlServer.GetConnectionString())
            .Options;
        await using var context = new ProcureToPayDbContext(options);
        await context.Database.MigrateAsync(cancellationToken);
        using var loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Warning));
        var bootstrapper = new OrganizationBootstrapper(
            context,
            loggerFactory.CreateLogger<OrganizationBootstrapper>());
        var configuration = CreateOptions("admin-1", "Initial bootstrap");

        Assert.True(await bootstrapper.InitializeAsync(configuration, cancellationToken));
        Assert.False(await bootstrapper.InitializeAsync(
            configuration with { Reason = "A different operational reason" }, cancellationToken));
        Assert.Equal(1, await context.Organizations.CountAsync(cancellationToken));
        Assert.Equal(1, await context.LegalEntities.CountAsync(cancellationToken));
        Assert.Equal(1, await context.Departments.CountAsync(cancellationToken));
        Assert.Equal(1, await context.UserProfiles.CountAsync(cancellationToken));
        Assert.Equal(1, await context.RoleAssignments.CountAsync(cancellationToken));
        Assert.Equal(1, await context.AdministrativeAuditRecords.CountAsync(cancellationToken));
        var bootstrapAudit = await context.AdministrativeAuditRecords.SingleAsync(cancellationToken);
        Assert.Equal("SYSTEM", bootstrapAudit.ActorType);
        Assert.Contains("\"after\"", bootstrapAudit.AfterJson, StringComparison.Ordinal);
        Assert.Contains("RoleAssignment", bootstrapAudit.AfterJson, StringComparison.Ordinal);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            bootstrapper.InitializeAsync(configuration with { OrganizationName = "Different" }, cancellationToken));

        var recovery = configuration with
        {
            AdminSubject = "admin-recovered",
            Reason = "Break-glass recovery after administrator loss"
        };
        var recoveredId = await bootstrapper.RecoverAdministratorAsync(recovery, cancellationToken);

        Assert.NotEqual(Guid.Empty, recoveredId);
        Assert.Equal(2, await context.UserProfiles.CountAsync(cancellationToken));
        Assert.Equal(2, await context.RoleAssignments.CountAsync(cancellationToken));
        Assert.Equal(2, await context.AdministrativeAuditRecords.CountAsync(cancellationToken));
        Assert.Equal(0, await context.AuthorityGrants.CountAsync(cancellationToken));
        var recoveryAudit = await context.AdministrativeAuditRecords
            .OrderByDescending(item => item.OccurredAt)
            .FirstAsync(cancellationToken);
        Assert.Contains("\"before\"", recoveryAudit.AfterJson, StringComparison.Ordinal);
        Assert.Contains("\"after\"", recoveryAudit.AfterJson, StringComparison.Ordinal);

        var existingAdmin = await context.UserProfiles.SingleAsync(
            item => item.Subject == "admin-1", cancellationToken);
        var existingAdminVersionBeforeRecovery = existingAdmin.Version + 1;
        existingAdmin.Status = (int)UserProfileStatus.Inactive;
        existingAdmin.Version = existingAdminVersionBeforeRecovery;
        await context.SaveChangesAsync(cancellationToken);
        var restoredExistingId = await bootstrapper.RecoverAdministratorAsync(configuration, cancellationToken);
        Assert.Equal(existingAdmin.Id, restoredExistingId);
        var existingRecoveryAudit = await context.AdministrativeAuditRecords
            .OrderByDescending(item => item.OccurredAt)
            .FirstAsync(cancellationToken);
        Assert.Contains($"\"version\":{existingAdminVersionBeforeRecovery}",
            existingRecoveryAudit.AfterJson, StringComparison.Ordinal);
        Assert.Contains($"\"version\":{existingAdminVersionBeforeRecovery + 1}",
            existingRecoveryAudit.AfterJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Published_api_cli_executes_bootstrap_and_recovery_without_http_routes()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var sqlServer = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest")
            .WithPassword("ProcureToPay_test_2026!")
            .Build();
        await sqlServer.StartAsync(cancellationToken);
        var connectionString = sqlServer.GetConnectionString();

        var options = new DbContextOptionsBuilder<ProcureToPayDbContext>()
            .UseSqlServer(connectionString)
            .Options;
        await using var context = new ProcureToPayDbContext(options);
        await context.Database.MigrateAsync(cancellationToken);
        await RunOrganizationOperationAsync(connectionString, "--organization-bootstrap", "admin-cli-1", cancellationToken);

        Assert.Equal(1, await context.Organizations.CountAsync(cancellationToken));
        Assert.Equal(1, await context.RoleAssignments.CountAsync(cancellationToken));

        await RunOrganizationOperationAsync(connectionString, "--organization-recover-admin", "admin-cli-2", cancellationToken);
        Assert.Equal(2, await context.UserProfiles.CountAsync(cancellationToken));
        Assert.Equal(2, await context.RoleAssignments.CountAsync(cancellationToken));
        Assert.Equal(2, await context.AdministrativeAuditRecords.CountAsync(cancellationToken));
    }

    [Fact]
    public async Task First_authenticated_request_creates_a_pending_profile_idempotently()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var sqlServer = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest")
            .WithPassword("ProcureToPay_test_2026!")
            .Build();
        await sqlServer.StartAsync(cancellationToken);
        var options = new DbContextOptionsBuilder<ProcureToPayDbContext>()
            .UseSqlServer(sqlServer.GetConnectionString())
            .Options;
        await using var context = new ProcureToPayDbContext(options);
        await context.Database.MigrateAsync(cancellationToken);
        using var loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Warning));
        var bootstrapper = new OrganizationBootstrapper(context, loggerFactory.CreateLogger<OrganizationBootstrapper>());
        await bootstrapper.InitializeAsync(CreateOptions("admin-1", "Initial bootstrap"), cancellationToken);

        var provisioning = new CurrentUserProvisioningService(context);
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim("iss", "https://keycloak.test/realms/procure-to-pay"),
            new Claim("sub", "jit-user-1"),
            new Claim("email", "jit@acme.test"),
            new Claim("name", "JIT User")
        ], "Bearer"));

        var first = await provisioning.EnsureProfileAsync(principal, cancellationToken);
        var second = await provisioning.EnsureProfileAsync(principal, cancellationToken);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(UserProfileStatus.PendingSetup, (UserProfileStatus)first.Status);
        Assert.Equal("jit@acme.test", second.Email);
        // pi-lens-ignore: lsp:CS1061
        Assert.Equal(2, await context.UserProfiles.CountAsync(cancellationToken));
    }

    [Fact]
    public async Task Concurrent_first_requests_create_one_pending_profile()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var sqlServer = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest")
            .WithPassword("ProcureToPay_test_2026!")
            .Build();
        await sqlServer.StartAsync(cancellationToken);
        var connectionString = sqlServer.GetConnectionString();
        var options = new DbContextOptionsBuilder<ProcureToPayDbContext>()
            .UseSqlServer(connectionString)
            .Options;
        await using (var migrationContext = new ProcureToPayDbContext(options))
        {
            await migrationContext.Database.MigrateAsync(cancellationToken);
            using var loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Warning));
            var bootstrapper = new OrganizationBootstrapper(
                migrationContext, loggerFactory.CreateLogger<OrganizationBootstrapper>());
            await bootstrapper.InitializeAsync(CreateOptions("admin-1", "Initial bootstrap"), cancellationToken);
        }

        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim("iss", "https://keycloak.test/realms/procure-to-pay"),
            new Claim("sub", "concurrent-jit-user")
        ], "Bearer"));
        var ids = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
        {
            await using var requestContext = new ProcureToPayDbContext(options);
            return (await new CurrentUserProvisioningService(requestContext)
                .EnsureProfileAsync(principal, cancellationToken)).Id;
        }));

        Assert.Equal(ids[0], ids[1]);
        await using var assertionContext = new ProcureToPayDbContext(options);
        Assert.Equal(2, await assertionContext.UserProfiles.CountAsync(cancellationToken));
    // pi-lens-ignore: lsp:CS0246
    }

    private static async Task RunOrganizationOperationAsync(
        string connectionString,
        string operation,
        string subject,
        CancellationToken cancellationToken)
    {
        var apiAssembly = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "../../../../../src/ProcureToPay.Api/bin/Debug/net10.0/ProcureToPay.Api.dll"));
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add(apiAssembly);
        process.StartInfo.ArgumentList.Add(operation);
        process.StartInfo.Environment["ConnectionStrings__SqlServer"] = connectionString;
        process.StartInfo.Environment["Authentication__JwtBearer__Authority"] = "https://issuer.invalid";
        process.StartInfo.Environment["Authentication__JwtBearer__Audience"] = "procure-to-pay-tests";
        process.StartInfo.Environment["AWS__Region"] = "us-east-1";
        process.StartInfo.Environment["Storage__S3__BucketName"] = "procure-to-pay-tests";
        process.StartInfo.Environment["Organization__Code"] = "ACME";
        process.StartInfo.Environment["Organization__Name"] = "Acme Corporation";
        process.StartInfo.Environment["Organization__BaseCurrency"] = "PEN";
        process.StartInfo.Environment["Organization__TimeZoneId"] = "America/Lima";
        process.StartInfo.Environment["Organization__FiscalYearStartMonth"] = "1";
        process.StartInfo.Environment["Organization__LegalEntityCode"] = "ACME-PE";
        process.StartInfo.Environment["Organization__LegalEntityName"] = "Acme Peru S.A.C.";
        process.StartInfo.Environment["Organization__InitialDepartmentCode"] = "IT";
        process.StartInfo.Environment["Organization__InitialDepartmentName"] = "Software / IT";
        process.StartInfo.Environment["Organization__AdminIssuer"] = "https://keycloak.test/realms/procure-to-pay";
        process.StartInfo.Environment["Organization__AdminSubject"] = subject;
        process.StartInfo.Environment["Organization__Reason"] = $"CLI operation {operation}";
        Assert.True(process.Start());
        await process.WaitForExitAsync(cancellationToken);
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = await process.StandardError.ReadToEndAsync(cancellationToken);
        Assert.True(process.ExitCode == 0, $"CLI failed: {output}\n{error}");
    }

    private static OrganizationBootstrapOptions CreateOptions(string subject, string reason) => new(
        "ACME",
        "Acme Corporation",
        "PEN",
        "America/Lima",
        1,
        "ACME-PE",
        "Acme Peru S.A.C.",
        "IT",
        "Software / IT",
        "https://keycloak.test/realms/procure-to-pay",
        subject,
        reason);
}
