using Microsoft.EntityFrameworkCore;
using ProcureToPay.Infrastructure.Persistence;
// pi-lens-ignore: lsp:CS0234
using ProcureToPay.Infrastructure.Persistence.Organization;

namespace ProcureToPay.IntegrationTests.Organization;

public sealed class OrganizationPersistenceModelTests
{
    [Fact]
    public void Organization_model_maps_module_schema_and_concurrency_tokens()
    {
        var options = new DbContextOptionsBuilder<ProcureToPayDbContext>()
            .UseSqlServer("Server=localhost;Database=ProcureToPay_ModelOnly;Integrated Security=True;TrustServerCertificate=True")
            .Options;
        using var context = new ProcureToPayDbContext(options);

        var organization = context.Model.FindEntityType(typeof(OrganizationRecord))
            ?? throw new InvalidOperationException("Organization model is missing.");
        var userProfile = context.Model.FindEntityType(typeof(UserProfileRecord))
            ?? throw new InvalidOperationException("User profile model is missing.");
        var audit = context.Model.FindEntityType(typeof(AdministrativeAuditRecord))
            ?? throw new InvalidOperationException("Audit model is missing.");

        Assert.Equal("Organization", organization!.GetSchema());
        Assert.Equal("Organizations", organization.GetTableName());
        Assert.Contains(
            organization.GetIndexes(),
            index => index.IsUnique && index.Properties.Any(property => property.Name == nameof(OrganizationRecord.SingletonKey)));
        Assert.Contains(
            userProfile!.GetIndexes(),
            index => index.IsUnique && index.Properties.Select(property => property.Name)
                .SequenceEqual([nameof(UserProfileRecord.Issuer), nameof(UserProfileRecord.Subject)]));
        Assert.True(userProfile.FindProperty(nameof(UserProfileRecord.RowVersion))!.IsConcurrencyToken);
        Assert.Equal("AdministrativeAuditRecords", audit!.GetTableName());
    }
}
