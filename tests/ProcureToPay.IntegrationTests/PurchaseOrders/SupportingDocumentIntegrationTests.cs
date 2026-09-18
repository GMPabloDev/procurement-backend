using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Domain.Modules.PurchaseOrders;
using ProcureToPay.Domain.SharedKernel;
using ProcureToPay.Infrastructure.Persistence;
using ProcureToPay.Infrastructure.Persistence.Approval;
using ProcureToPay.Infrastructure.Persistence.Organization;
using ProcureToPay.Infrastructure.Persistence.PurchaseOrders;

namespace ProcureToPay.IntegrationTests.PurchaseOrders;

/// <summary>
/// SQL Server evidence of SPEC 11 REQ-08 (CA-08): the productive supporting document owner counts
/// unique confirmed bytes per target inside the admitted business type union, emits one reproducible
/// <c>SATISFIED</c> signal only when every target reaches the minimum, and keeps its lease and fencing
/// so a restart, an obsolete lease or a lost signal never duplicates the effect.
/// </summary>
public sealed class SupportingDocumentIntegrationTests
{
    [Fact]
    public async Task Unique_bytes_of_the_admitted_union_satisfy_the_prerequisite()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await PurchaseOrderHarness.StartAsync(cancellationToken);
        await using var context = harness.CreateContext();
        var prerequisiteId = await SeedAsync(
            harness, context, ["INVOICE", "RECEIPT"], minimumCount: 2, cancellationToken);
        var documents = new SupportingDocumentService(context, harness.Sourcing.Storage);

        // Two distinct bytes under different roots and admitted types reach the minimum of two.
        await ConfirmAsync(documents, harness, "invoice.pdf", "INVOICE", [1, 2, 3], cancellationToken);
        await ConfirmAsync(documents, harness, "receipt.pdf", "RECEIPT", [4, 5, 6], cancellationToken);
        // The same bytes under another root, name and admitted type still count once per target.
        await ConfirmAsync(documents, harness, "copy-invoice.pdf", "INVOICE", [1, 2, 3], cancellationToken);

        var processor = CreateProcessor(harness, context, "worker-1");
        var outcomes = await processor.ProcessDueAsync(DateTimeOffset.UtcNow, cancellationToken);

        var outcome = Assert.Single(outcomes);
        Assert.Equal(SupportingDocumentOwnerProcessor.Satisfied, outcome.State);
        Assert.NotNull(outcome.EvidenceDigest);
        await using var verification = harness.CreateContext();
        var prerequisite = await verification.ApprovalPrerequisites
            .AsNoTracking()
            .SingleAsync(record => record.Id == prerequisiteId, cancellationToken);
        Assert.Equal((int)PrerequisiteStatus.Satisfied, prerequisite.Status);
        var evidence = await verification.SupportingDocumentOwnerEvidence
            .AsNoTracking()
            .SingleAsync(record => record.PrerequisiteId == prerequisiteId, cancellationToken);
        Assert.Equal("SATISFIED", evidence.Result);
        Assert.Equal(
            evidence.ContentDigest,
            ProcureToPay.Domain.Modules.PurchaseOrders.PurchaseOrderCanonicalizer.Hash(evidence.DocumentJson));
        Assert.Equal(evidence.ContentDigest, outcome.EvidenceDigest);
        var counts = System.Text.Json.JsonDocument.Parse(evidence.DocumentJson).RootElement
            .GetProperty("counts_by_target").EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.GetInt32());
        Assert.Equal(2, counts[$"{harness.LineId:D}:1"]);
        var references = System.Text.Json.JsonDocument.Parse(evidence.DocumentJson).RootElement
            .GetProperty("document_refs").GetArrayLength();
        Assert.Equal(2, references);
        Assert.False(string.IsNullOrWhiteSpace(evidence.SignalKey));
    }

    [Fact]
    public async Task Staged_or_wrong_type_documents_never_satisfy_the_prerequisite()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await PurchaseOrderHarness.StartAsync(cancellationToken);
        await using var context = harness.CreateContext();
        var prerequisiteId = await SeedAsync(
            harness, context, ["INVOICE"], minimumCount: 1, cancellationToken);
        var documents = new SupportingDocumentService(context, harness.Sourcing.Storage);

        // A confirmed document of a type outside the admitted union does not count.
        await ConfirmAsync(documents, harness, "other.bin", "OTHER", [9, 9, 9], cancellationToken);
        // A staged document never counts.
        await StageAsync(documents, harness, "invoice.pdf", "INVOICE", [7, 7, 7], cancellationToken);

        var processor = CreateProcessor(harness, context, "worker-1");
        var outcomes = await processor.ProcessDueAsync(DateTimeOffset.UtcNow, cancellationToken);

        var outcome = Assert.Single(outcomes);
        Assert.Equal(SupportingDocumentOwnerProcessor.Waiting, outcome.State);
        await using var verification = harness.CreateContext();
        var prerequisite = await verification.ApprovalPrerequisites
            .AsNoTracking()
            .SingleAsync(record => record.Id == prerequisiteId, cancellationToken);
        Assert.Equal((int)PrerequisiteStatus.Waiting, prerequisite.Status);
        Assert.Equal(
            0,
            await verification.SupportingDocumentOwnerEvidence
                .AsNoTracking()
                .CountAsync(record => record.PrerequisiteId == prerequisiteId, cancellationToken));
    }

    [Fact]
    public async Task An_obsolete_lease_is_reclaimed_and_signals_exactly_once()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await PurchaseOrderHarness.StartAsync(cancellationToken);
        await using var context = harness.CreateContext();
        var prerequisiteId = await SeedAsync(
            harness, context, ["INVOICE"], minimumCount: 1, cancellationToken);
        var documents = new SupportingDocumentService(context, harness.Sourcing.Storage);
        await ConfirmAsync(documents, harness, "invoice.pdf", "INVOICE", [1, 2, 3], cancellationToken);

        // A worker that died mid-count leaves an expired lease and a fence behind.
        var abandoned = new SupportingDocumentOwnerAttemptRecord
        {
            Id = Guid.NewGuid(),
            PrerequisiteId = prerequisiteId,
            OrganizationId = harness.OrganizationId,
            CaseId = await CaseIdAsync(context, cancellationToken),
            SubjectType = "PURCHASE_REQUEST",
            OwnerAdapterId = PurchaseOrderCodes.SupportingDocumentOwnerAdapterId,
            OwnerAdapterVersion = PurchaseOrderCodes.OwnerAdapterVersion,
            RequestId = harness.RequestId,
            RequestVersion = 1,
            TargetsJson = TargetJson(harness),
            ParametersDigest = new string('a', 64),
            AllowedDocumentTypesJson = "[\"INVOICE\"]",
            MinimumCount = 1,
            State = SupportingDocumentOwnerProcessor.Waiting,
            CheckKey = $"supporting-document:{prerequisiteId:D}:check",
            SignalKey = $"supporting-document:{prerequisiteId:D}:signal",
            DueAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            Attempts = 1,
            LeaseOwner = "dead-worker",
            LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            FencingToken = 4,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-5)
        };
        context.SupportingDocumentOwnerAttempts.Add(abandoned);
        await context.SaveChangesAsync(cancellationToken);

        var processor = CreateProcessor(harness, context, "worker-2");
        var outcomes = await processor.ProcessDueAsync(DateTimeOffset.UtcNow, cancellationToken);

        var outcome = Assert.Single(outcomes);
        Assert.Equal(SupportingDocumentOwnerProcessor.Satisfied, outcome.State);
        await using var verification = harness.CreateContext();
        var attempt = await verification.SupportingDocumentOwnerAttempts
            .AsNoTracking()
            .SingleAsync(record => record.PrerequisiteId == prerequisiteId, cancellationToken);
        Assert.Equal(SupportingDocumentOwnerProcessor.Satisfied, attempt.State);
        Assert.Equal(5, attempt.FencingToken);
        Assert.Null(attempt.LeaseOwner);
        // The signal is emitted once: the recorded evidence of the attempt is unique.
        Assert.Equal(
            1,
            await verification.SupportingDocumentOwnerEvidence
                .AsNoTracking()
                .CountAsync(record => record.PrerequisiteId == prerequisiteId, cancellationToken));
        Assert.Equal(
            1,
            await verification.ApprovalPrerequisiteSignals
                .AsNoTracking()
                .CountAsync(record => record.PrerequisiteId == prerequisiteId, cancellationToken));
    }

    [Fact]
    public async Task A_cancelled_case_abandons_the_attempt_without_signalling()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await PurchaseOrderHarness.StartAsync(cancellationToken);
        await using var context = harness.CreateContext();
        var prerequisiteId = await SeedAsync(
            harness, context, ["INVOICE"], minimumCount: 1, cancellationToken);
        var documents = new SupportingDocumentService(context, harness.Sourcing.Storage);
        await ConfirmAsync(documents, harness, "invoice.pdf", "INVOICE", [1, 2, 3], cancellationToken);
        var caseId = await CaseIdAsync(context, cancellationToken);
        await context.Database.ExecuteSqlRawAsync(
            "UPDATE [Approval].[ApprovalCases] SET [Status] = {0} WHERE [Id] = {1}",
            [(int)ApprovalCaseStatus.Cancelled, caseId],
            cancellationToken);

        var processor = CreateProcessor(harness, context, "worker-1");
        var outcomes = await processor.ProcessDueAsync(DateTimeOffset.UtcNow, cancellationToken);

        var outcome = Assert.Single(outcomes);
        Assert.Equal(SupportingDocumentOwnerProcessor.Abandoned, outcome.State);
        await using var verification = harness.CreateContext();
        Assert.Equal(
            0,
            await verification.SupportingDocumentOwnerEvidence
                .AsNoTracking()
                .CountAsync(record => record.PrerequisiteId == prerequisiteId, cancellationToken));
    }

    private static SupportingDocumentOwnerProcessor CreateProcessor(
        PurchaseOrderHarness harness,
        ProcureToPayDbContext context,
        string instanceId)
    {
        var configuration = PurchaseOrderHarness.Configuration();
        var allowlist = new ApprovalWorkloadAllowlist(configuration);
        var workflow = new ApprovalWorkflowService(
            context,
            allowlist,
            new ApprovalAssignmentEngine(
                context, new OrganizationEligibilityService(context), new ApprovalScopeResolver(context)),
            NullLogger<ApprovalWorkflowService>.Instance);
        var identity = new ApprovalInstanceIdentity(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Approval:InstanceId"] = instanceId })
                .Build());
        return new SupportingDocumentOwnerProcessor(context, workflow, identity);
    }

    private static async Task<Guid> SeedAsync(
        PurchaseOrderHarness harness,
        ProcureToPayDbContext context,
        string[] documentTypes,
        int minimumCount,
        CancellationToken cancellationToken)
    {
        await harness.Sourcing.SeedProposalFixturesAsync(context, cancellationToken);
        await harness.SeedRequestEvidenceAsync(context, cancellationToken);
        var caseId = await CaseIdAsync(context, cancellationToken);
        var prerequisite = new ApprovalPrerequisiteRecord
        {
            Id = Guid.NewGuid(),
            CaseId = caseId,
            OrganizationId = harness.OrganizationId,
            Key = "DOCUMENTS",
            OwnerAdapterId = PurchaseOrderCodes.SupportingDocumentOwnerAdapterId,
            OwnerAdapterVersion = PurchaseOrderCodes.OwnerAdapterVersion,
            OwnerWorkloadIssuer = "internal://procure-to-pay",
            OwnerWorkloadClientId = "supporting-document-owner",
            SourceControlType = "PURCHASE_REQUEST",
            SourceControlDigest = new string('b', 64),
            ParametersJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                document_types = documentTypes,
                minimum_count = minimumCount
            }),
            TargetsJson = TargetJson(harness),
            Status = (int)PrerequisiteStatus.Waiting,
            Version = 1
        };
        context.ApprovalPrerequisites.Add(prerequisite);
        await context.SaveChangesAsync(cancellationToken);
        await new SupportingDocumentService(context, harness.Sourcing.Storage).RegisterAsync(
            new RegisterSupportingDocumentProcessorCommand(
                PurchaseOrderCodes.SupportingDocumentOwnerAdapterId,
                PurchaseOrderCodes.OwnerAdapterVersion,
                "supporting-document-owner",
                "internal://procure-to-pay",
                "supporting-document-owner"),
            cancellationToken);
        return prerequisite.Id;
    }

    private static Task<Guid> CaseIdAsync(ProcureToPayDbContext context, CancellationToken cancellationToken) =>
        context.ApprovalCases
            .AsNoTracking()
            .Where(record => record.SubjectType == "PURCHASE_REQUEST")
            .OrderByDescending(record => record.Version)
            .Select(record => record.Id)
            .FirstAsync(cancellationToken);

    private static string TargetJson(PurchaseOrderHarness harness) =>
        $"[{{\"type\":\"{PurchaseOrderCodes.ApprovalTargetType}\",\"id\":\"{harness.LineId:D}\",\"version\":1," +
        $"\"materialSnapshotDigest\":\"{PurchaseOrderHarness.TargetDigest}\"}}]";

    private static async Task<ProcurementSupportingDocumentVersion> StageAsync(
        SupportingDocumentService documents,
        PurchaseOrderHarness harness,
        string fileName,
        string businessType,
        byte[] bytes,
        CancellationToken cancellationToken) =>
        await documents.StageAsync(
            new StageSupportingDocumentCommand(
                harness.OrganizationId,
                harness.RequestId,
                1,
                Guid.NewGuid(),
                1,
                "application/pdf",
                fileName,
                bytes.LongLength,
                null,
                businessType,
                [new OrderingEvidenceTargetRef(harness.LineId, 1)],
                harness.RequesterId),
            new MemoryStream(bytes),
            cancellationToken);

    private static async Task<ProcurementSupportingDocumentVersion> ConfirmAsync(
        SupportingDocumentService documents,
        PurchaseOrderHarness harness,
        string fileName,
        string businessType,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        var staged = await StageAsync(documents, harness, fileName, businessType, bytes, cancellationToken);
        return await documents.ConfirmAsync(
            new ConfirmSupportingDocumentCommand(
                harness.OrganizationId, staged.DocumentId, staged.Version, harness.RequesterId),
            cancellationToken);
    }
}
