using Microsoft.EntityFrameworkCore;
using ProcureToPay.Domain.Modules.Organization;
using ProcureToPay.Infrastructure.Persistence;
using ProcureToPay.Infrastructure.Persistence.Organization;
using ProcureToPay.Infrastructure.Persistence.ReferenceCatalogs;
using Testcontainers.MsSql;

namespace ProcureToPay.IntegrationTests.ReferenceCatalogs;

/// <summary>
/// SQL Server harness for the SPEC 07 catalog tests: one organization, two departments and (lazily)
/// one active user. Tests may parameterize a second organization to prove isolation.
/// </summary>
internal sealed class ReferenceCatalogHarness : IAsyncDisposable
{
    private MsSqlContainer container = null!;
    private string connectionString = string.Empty;
    private ProcureToPayDbContext serviceContext = null!;
    private Guid userProfileId;

    public Guid OrganizationId { get; private init; }
    public Guid OtherOrganizationId { get; private init; }
    public Guid DepartmentId { get; private init; }
    public Guid OtherDepartmentId { get; private init; }
    public Guid OtherOrganizationDepartmentId { get; private init; } = Guid.Parse("55555555-5555-5555-5555-555555555555");
    public Guid ActorId { get; private init; } = Guid.Parse("44444444-4444-4444-4444-444444444444");

    public ReferenceCatalogPersistenceService Service { get; private set; } = null!;

    public static async Task<ReferenceCatalogHarness> StartAsync(
        Guid organizationId,
        Guid otherOrganizationId,
        Guid departmentId,
        Guid otherDepartmentId,
        CancellationToken cancellationToken)
    {
        var container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest")
            .WithPassword("ProcureToPay_test_2026!")
            .Build();
        await container.StartAsync(cancellationToken);
        var harness = new ReferenceCatalogHarness
        {
            container = container,
            connectionString = container.GetConnectionString(),
            OrganizationId = organizationId,
            OtherOrganizationId = otherOrganizationId,
            DepartmentId = departmentId,
            OtherDepartmentId = otherDepartmentId
        };
        await using var context = harness.CreateContext();
        await context.Database.MigrateAsync(cancellationToken);
        context.Organizations.AddRange(
            new OrganizationRecord
            {
                Id = organizationId,
                Code = "CATALOG-TEST",
                Name = "Catalog Test",
                BaseCurrency = "PEN",
                TimeZoneId = "America/Lima",
                FiscalYearStartMonth = 1,
                Version = 1
            },
            new OrganizationRecord
            {
                Id = otherOrganizationId,
                Code = "CATALOG-OTHER",
                Name = "Other",
                BaseCurrency = "PEN",
                TimeZoneId = "America/Lima",
                FiscalYearStartMonth = 1,
                Version = 1,
                SingletonKey = 2
            });
        context.Departments.AddRange(
            new DepartmentRecord
            {
                Id = departmentId,
                OrganizationId = organizationId,
                Code = "IT",
                Name = "Information Technology",
                Status = (int)EntityStatus.Active,
                Version = 1
            },
            new DepartmentRecord
            {
                Id = otherDepartmentId,
                OrganizationId = organizationId,
                Code = "OPS",
                Name = "Operations",
                Status = (int)EntityStatus.Active,
                Version = 1
            },
            new DepartmentRecord
            {
                Id = harness.OtherOrganizationDepartmentId,
                OrganizationId = otherOrganizationId,
                Code = "IT",
                Name = "Other Organization IT",
                Status = (int)EntityStatus.Active,
                Version = 1
            });
        await context.SaveChangesAsync(cancellationToken);
        harness.serviceContext = harness.CreateContext();
        harness.Service = new ReferenceCatalogPersistenceService(harness.serviceContext);
        return harness;
    }

    public ProcureToPayDbContext CreateContext() => new(
        new DbContextOptionsBuilder<ProcureToPayDbContext>()
            .UseSqlServer(connectionString)
            .Options);

    public async Task<EntityStatus> StatusOfAsync(
        Guid costCenterId,
        int version,
        CancellationToken cancellationToken)
    {
        await using var context = CreateContext();
        var status = await context.CostCenterVersions
            .Where(record => record.CostCenterId == costCenterId && record.Version == version)
            .Select(record => record.Status)
            .SingleAsync(cancellationToken);
        return (EntityStatus)status;
    }

    public async Task<Guid> EnsureUserAsync(CancellationToken cancellationToken)
    {
        if (userProfileId != Guid.Empty)
        {
            return userProfileId;
        }

        await using var context = CreateContext();
        var userId = Guid.NewGuid();
        context.UserProfiles.Add(new UserProfileRecord
        {
            Id = userId,
            OrganizationId = OrganizationId,
            Issuer = "https://issuer.test",
            Subject = "catalog-user",
            DepartmentId = DepartmentId,
            JobTitle = "Analyst",
            Status = (int)UserProfileStatus.Active,
            Version = 1
        });
        await context.SaveChangesAsync(cancellationToken);
        userProfileId = userId;
        return userId;
    }

    public async ValueTask DisposeAsync()
    {
        await serviceContext.DisposeAsync();
        await container.DisposeAsync();
    }
}
