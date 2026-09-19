using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using ProcureToPay.Api.Health;
using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Domain.Modules.PurchaseOrders;
using ProcureToPay.Infrastructure.Persistence;
using ProcureToPay.Infrastructure.Persistence.Approval;
using ProcureToPay.Infrastructure.Persistence.PurchaseOrders;

namespace ProcureToPay.IntegrationTests.PurchaseOrders;

/// <summary>
/// SQL Server evidence of SPEC 11 REQ-08/REQ-12 (CA-10): readiness degrades on an unavailable
/// storage, a 0/2 processor registration, a registration that is not bound to the owner workload the
/// prerequisites resolved, and a backlog older than the 60-second budget. The codes never expose a
/// supplier, a file, an amount or a digest.
/// </summary>
public sealed class SupportingDocumentHealthIntegrationTests
{
    [Fact]
    public async Task Readiness_requires_the_storage_and_exactly_one_bound_processor()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await PurchaseOrderHarness.StartAsync(cancellationToken);
        await using var context = harness.CreateContext();
        var check = new SupportingDocumentHealthCheck(context, harness.Sourcing.Storage);

        // 0 registrations: the owner cannot run, so readiness degrades.
        Assert.Equal(HealthStatus.Degraded, (await check.CheckHealthAsync(new HealthCheckContext(), cancellationToken)).Status);

        var registration = await RegisterAsync(harness, context, cancellationToken);
        context.ChangeTracker.Clear();
        // 1 registration and no prerequisite: the owner is ready.
        var ready = await check.CheckHealthAsync(new HealthCheckContext(), cancellationToken);
        Assert.Equal(HealthStatus.Healthy, ready.Status);

        // A registration of another adapter is a different pipeline and never degrades this owner.
        context.SupportingDocumentProcessorRegistrations.Add(
            new SupportingDocumentProcessorRegistrationRecord
            {
                AdapterId = "another-owner",
                AdapterVersion = "v1",
                ProcessorId = "another-processor",
                WorkloadIssuer = registration.WorkloadIssuer,
                WorkloadClientId = registration.WorkloadClientId
            });
        await context.SaveChangesAsync(cancellationToken);
        Assert.Equal(
            HealthStatus.Healthy,
            (await check.CheckHealthAsync(new HealthCheckContext(), cancellationToken)).Status);

        // A prerequisite resolved with another owner workload is not served by this processor.
        context.ApprovalPrerequisites.Add(new ApprovalPrerequisiteRecord
        {
            Id = Guid.NewGuid(),
            CaseId = Guid.NewGuid(),
            OrganizationId = harness.OrganizationId,
            Key = "DOCUMENTS",
            OwnerAdapterId = PurchaseOrderCodes.SupportingDocumentOwnerAdapterId,
            OwnerAdapterVersion = PurchaseOrderCodes.OwnerAdapterVersion,
            OwnerWorkloadIssuer = "internal://procure-to-pay",
            OwnerWorkloadClientId = "another-owner",
            SourceControlType = "PURCHASE_REQUEST",
            SourceControlDigest = new string('a', 64),
            ParametersJson = "{\"document_types\":[\"INVOICE\"],\"minimum_count\":1}",
            TargetsJson = "[]",
            Status = (int)PrerequisiteStatus.Waiting,
            Version = 1
        });
        await context.SaveChangesAsync(cancellationToken);
        Assert.Equal(
            HealthStatus.Degraded,
            (await check.CheckHealthAsync(new HealthCheckContext(), cancellationToken)).Status);

        // The storage probe is part of readiness.
        harness.Sourcing.Storage.Available = false;
        Assert.Equal(
            HealthStatus.Degraded,
            (await check.CheckHealthAsync(new HealthCheckContext(), cancellationToken)).Status);
        harness.Sourcing.Storage.Available = true;
    }

    [Fact]
    public async Task Readiness_degrades_on_a_backlog_older_than_the_budget()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await PurchaseOrderHarness.StartAsync(cancellationToken);
        await using var context = harness.CreateContext();
        await RegisterAsync(harness, context, cancellationToken);
        var check = new SupportingDocumentHealthCheck(context, harness.Sourcing.Storage);
        Assert.Equal(
            HealthStatus.Healthy,
            (await check.CheckHealthAsync(new HealthCheckContext(), cancellationToken)).Status);

        // REQ-08: a due attempt that no worker advanced within sixty seconds degrades readiness.
        context.SupportingDocumentOwnerAttempts.Add(new SupportingDocumentOwnerAttemptRecord
        {
            Id = Guid.NewGuid(),
            PrerequisiteId = Guid.NewGuid(),
            OrganizationId = harness.OrganizationId,
            CaseId = Guid.NewGuid(),
            SubjectType = "PURCHASE_REQUEST",
            OwnerAdapterId = PurchaseOrderCodes.SupportingDocumentOwnerAdapterId,
            OwnerAdapterVersion = PurchaseOrderCodes.OwnerAdapterVersion,
            RequestId = harness.RequestId,
            RequestVersion = 1,
            TargetsJson = "[]",
            ParametersDigest = new string('a', 64),
            AllowedDocumentTypesJson = "[\"INVOICE\"]",
            MinimumCount = 1,
            State = SupportingDocumentOwnerProcessor.Waiting,
            CheckKey = "check",
            SignalKey = "signal",
            DueAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-5)
        });
        await context.SaveChangesAsync(cancellationToken);
        var degraded = await check.CheckHealthAsync(new HealthCheckContext(), cancellationToken);

        Assert.Equal(HealthStatus.Degraded, degraded.Status);
        Assert.Contains("OVERDUE", degraded.Description, StringComparison.Ordinal);
    }

    private static async Task<SupportingDocumentProcessorRegistrationRecord> RegisterAsync(
        PurchaseOrderHarness harness,
        ProcureToPayDbContext context,
        CancellationToken cancellationToken) =>
        await new SupportingDocumentService(context, harness.Sourcing.Storage).RegisterAsync(
            new RegisterSupportingDocumentProcessorCommand(
                PurchaseOrderCodes.SupportingDocumentOwnerAdapterId,
                PurchaseOrderCodes.OwnerAdapterVersion,
                "supporting-document-owner",
                "internal://procure-to-pay",
                "supporting-document-owner"),
            cancellationToken);
}
