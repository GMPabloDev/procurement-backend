using Microsoft.EntityFrameworkCore;
using ProcureToPay.Infrastructure.Persistence;
using ProcureToPay.Infrastructure.Persistence.Policy;

namespace ProcureToPay.IntegrationTests.Policy;

public sealed class PolicyPersistenceModelTests
{
    [Fact]
    public void Policy_model_maps_append_only_tables_and_idempotency_index()
    {
        var options = new DbContextOptionsBuilder<ProcureToPayDbContext>()
            .UseSqlServer("Server=localhost;Database=ProcureToPay_ModelOnly;Integrated Security=True;TrustServerCertificate=True")
            .Options;
        using var context = new ProcureToPayDbContext(options);

        var version = context.Model.FindEntityType(typeof(PolicySetVersionRecord))
            ?? throw new InvalidOperationException("Policy version model is missing.");
        var activation = context.Model.FindEntityType(typeof(PolicyActivationRecord))
            ?? throw new InvalidOperationException("Policy activation model is missing.");
        var retirement = context.Model.FindEntityType(typeof(PolicyRetirementRecord))
            ?? throw new InvalidOperationException("Policy retirement model is missing.");
        var evaluation = context.Model.FindEntityType(typeof(PolicyEvaluationBundleRecord))
            ?? throw new InvalidOperationException("Policy evaluation model is missing.");

        Assert.Equal("Policy", version.GetSchema());
        Assert.Equal("PolicySetVersions", version.GetTableName());
        Assert.Equal("PolicyActivations", activation.GetTableName());
        Assert.Equal("PolicyRetirements", retirement.GetTableName());
        Assert.Equal("PolicyEvaluationBundles", evaluation.GetTableName());
        Assert.True(evaluation.FindProperty(nameof(PolicyEvaluationBundleRecord.RowVersion))!.IsConcurrencyToken);
        Assert.Contains(
            evaluation.GetIndexes(),
            index => index.IsUnique && index.Properties.Select(property => property.Name).SequenceEqual([
                nameof(PolicyEvaluationBundleRecord.OrganizationId),
                nameof(PolicyEvaluationBundleRecord.WorkloadIssuer),
                nameof(PolicyEvaluationBundleRecord.WorkloadClientId),
                nameof(PolicyEvaluationBundleRecord.Operation),
                nameof(PolicyEvaluationBundleRecord.EvaluationKey)]));
        Assert.Contains(
            retirement.GetIndexes(),
            index => index.IsUnique && index.Properties.Any(property =>
                property.Name == nameof(PolicyRetirementRecord.PolicyActivationId)));
    }
}
