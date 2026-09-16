using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Domain.Modules.PurchaseRequests;
using ProcureToPay.Domain.Modules.Suppliers;

using ProcureToPay.Domain.SharedKernel;
using ProcureToPay.Infrastructure.Persistence;
using ProcureToPay.Infrastructure.Persistence.Approval;
using ProcureToPay.Infrastructure.Persistence.Policy;
using ProcureToPay.Infrastructure.Persistence.PurchaseRequests;

namespace ProcureToPay.IntegrationTests.Suppliers;

/// <summary>
/// Cross-module evidence of SPEC 09 REQ-08, REQ-09 and REQ-10 (CA-06, CA-07): a real Purchase Request
/// with declared suppliers is attested with the v2 manifest, served by the v2 provider with the
/// real approved-catalogue facts, projected by <c>policy-approval-adapter/v4</c> into one
/// <c>active-supplier-owner/v1</c> prerequisite per supplier, and signalled by the real processor.
/// </summary>
public sealed class SupplierActiveSupplierIntegrationTests
{
    [Fact]
    public async Task A_control_over_two_suppliers_partitions_into_two_prerequisites_and_signals_them()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await SupplierHarness.StartAsync(cancellationToken);
        var first = await ApprovedSupplierAsync(harness, "ABC Tech SAC", "20123456789", cancellationToken);
        var second = await ApprovedSupplierAsync(harness, "XYZ Supplies SAC", "20987654321", cancellationToken);

        // Only the first supplier is homologated, so the two lines must reach different facts.
        await using (var context = harness.CreateContext())
        {
            var services = harness.CreateServices(context);
            var attachment = await services.Catalog.StageAttachmentAsync(
                harness.OrganizationId, first.SupplierId, harness.BuyerId, "agreement.pdf", "application/pdf", 8,
                new MemoryStream("12345678"u8.ToArray()), cancellationToken);
            await services.Catalog.ConfirmAttachmentAsync(
                harness.OrganizationId, attachment.Id, harness.BuyerId, attachment.Sha256, cancellationToken);
            var entry = await services.Catalog.SaveAsync(
                harness.OrganizationId, harness.BuyerId, null, null,
                new VersionedEntityRef("SUPPLIER", first.SupplierId, first.Version),
                new VersionedCodeRef(
                    "SPEND_CATEGORY", harness.SpendCategoryCode, harness.SpendCategoryVersion,
                    harness.SpendCategoryDigest),
                null, 900m, "PEN", "UNIT", "ACME-2026-001",
                DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1),
                ApprovedCatalogEntryStatus.Active, attachment.Id, attachment.Version,
                "Negotiated", "catalog-1", "corr-catalog", cancellationToken);
            await services.Catalog.SubmitAsync(
                harness.OrganizationId, harness.BuyerId, entry.Version.CatalogEntryId,
                entry.Version.Version, "submit-catalog-1", "corr-submit", cancellationToken);
            await harness.DecideAndDispatchAsync(ApprovalDecisionAction.Approve, cancellationToken);
        }

        Guid requestId;
        int requestVersion;
        await using (var context = harness.CreateContext())
        {
            var services = harness.CreateServices(context);
            var creation = await services.PurchaseRequests.Persistence.CreateAsync(
                new PurchaseRequestCreateCommand(
                    harness.OrganizationId,
                    harness.RequesterId,
                    new VersionedEntityRef("LEGAL_ENTITY", harness.LegalEntityId, 1),
                    "Cross-module supplier evidence",
                    $"create-{Guid.NewGuid():N}",
                    "Initial request",
                    [
                        new PurchaseRequestLineDraft(
                            "line-1", Line(harness, first.SupplierId, first.Version)),
                        new PurchaseRequestLineDraft(
                            "line-2", Line(harness, second.SupplierId, second.Version))
                    ]),
                harness.RequesterId,
                "corr-create-pr",
                DateTimeOffset.UtcNow,
                cancellationToken);
            requestId = creation.RequestId;
            requestVersion = creation.Version;

            // The v2 manifest freezes one supplier fact snapshot per line (REQ-08, REQ-09).
            var manifest = await services.PurchaseRequests.Attestation.EnsureAttestedAsync(
                requestId, requestVersion, harness.OrganizationId, DateTimeOffset.UtcNow, cancellationToken);
            Assert.Equal(PurchaseRequestCodes.ManifestVersionV2, manifest.ContractVersion);
            Assert.NotNull(manifest.SupplierFactSnapshotsDigest);
            var snapshots = await context.SupplierPolicyFactSnapshots
                .AsNoTracking()
                .Where(record => record.RequestId == requestId && record.RequestVersion == requestVersion)
                .ToArrayAsync(cancellationToken);
            Assert.Equal(2, snapshots.Length);
            var homologated = snapshots.Single(record => record.SupplierId == first.SupplierId);
            Assert.True(homologated.PreferredSupplier);
            Assert.Equal("ACTIVE", homologated.AgreementStatus);
            var plain = snapshots.Single(record => record.SupplierId == second.SupplierId);
            Assert.False(plain.PreferredSupplier);
            Assert.Equal("NONE", plain.AgreementStatus);

            await ActivatePolicyAsync(context, harness, cancellationToken);
            var evaluation = await services.PurchaseRequests.PolicyEvaluations
                .EvaluateEnterprisePurchaseRequestAsync(
                    new PolicyFactRequest(
                        PurchaseRequestCodes.SubjectType,
                        requestId,
                        requestVersion,
                        PurchaseRequestCodes.EvaluationOperation,
                        DateTimeOffset.UtcNow,
                        new PolicyWorkloadPrincipal(
                            SupplierHarness.SubmissionWorkload.Issuer,
                            SupplierHarness.SubmissionWorkload.ClientId),
                        "corr-policy")
                    {
                        OrganizationId = harness.OrganizationId
                    },
                    "evaluation-key-1",
                    cancellationToken);
            Assert.NotNull(evaluation);

            // The adapter v4 partitions the combined control by supplier instead of failing closed
            // on the two different references of one control (REQ-10).
            var submission = await services.Submissions.SubmitAsync(
                new ApprovalSubmissionCommand(
                    SupplierHarness.SubmissionWorkload,
                    harness.OrganizationId,
                    PurchaseRequestCodes.SubjectType,
                    requestId,
                    requestVersion,
                    PurchaseRequestCodes.SubmitOperation,
                    PolicyApprovalAdapter.ContractVersionV4,
                    $"submit-{Guid.NewGuid():N}",
                    harness.RequesterId,
                    harness.RequesterId,
                    "corr-submit-pr"),
                DateTimeOffset.UtcNow,
                cancellationToken);
            Assert.NotEqual(Guid.Empty, submission.CaseId);

            var prerequisites = await context.ApprovalPrerequisites
                .AsNoTracking()
                .Where(record => record.CaseId == submission.CaseId)
                .ToArrayAsync(cancellationToken);
            var supplierPrerequisites = prerequisites
                .Where(record => record.OwnerAdapterId == SupplierCodes.ActiveSupplierOwnerAdapterId)
                .ToArray();
            Assert.Equal(2, supplierPrerequisites.Length);
            Assert.All(supplierPrerequisites, record =>
            {
                Assert.Equal("v1", record.OwnerAdapterVersion);
                Assert.StartsWith("ACTIVE_SUPPLIER:", record.Key, StringComparison.Ordinal);
                Assert.Equal(SupplierCodes.ActiveSupplierProcessorId, record.OwnerWorkloadClientId);
            });
            var references = supplierPrerequisites
                .Select(record => JsonSupplierRef(record.ParametersJson))
                .OrderBy(reference => reference)
                .ToArray();
            Assert.Equal(
                new[] { first.SupplierId, second.SupplierId }.OrderBy(id => id).ToArray(),
                references);
            Assert.All(supplierPrerequisites, record =>
                Assert.Equal(1, ApprovalJsonPersistence.DeserializeTargets(record.TargetsJson).Count));
        }

        // The real processor re-checks the current operational version and signals each partition.
        await using (var context = harness.CreateContext())
        {
            var services = harness.CreateServices(context);
            Assert.True(await services.Processor.IsRegisteredAsync(cancellationToken));
            var prerequisites = await context.ApprovalPrerequisites
                .AsNoTracking()
                .Where(record => record.OwnerAdapterId == SupplierCodes.ActiveSupplierOwnerAdapterId)
                .Select(record => record.Id)
                .ToArrayAsync(cancellationToken);
            foreach (var prerequisiteId in prerequisites)
            {
                var outcome = await services.Processor.ProcessOneAsync(
                    prerequisiteId, DateTimeOffset.UtcNow, cancellationToken);
                Assert.NotNull(outcome);
                Assert.Equal("SATISFIED", outcome!.SignalResult);
                Assert.True(outcome.Eligible);
            }

            var attempts = await context.SupplierPrerequisiteAttempts
                .AsNoTracking()
                .ToArrayAsync(cancellationToken);
            Assert.Equal(2, attempts.Length);
            Assert.All(attempts, attempt =>
            {
                Assert.Equal("COMPLETED", attempt.State);
                Assert.Equal("SATISFIED", attempt.SignalResult);
                Assert.NotNull(attempt.EvidenceDigest);
                Assert.StartsWith($"supplier:{attempt.PrerequisiteId:D}:", attempt.CheckKey, StringComparison.Ordinal);
            });
            Assert.All(
                await context.ApprovalPrerequisites.AsNoTracking()
                    .Where(record => record.OwnerAdapterId == SupplierCodes.ActiveSupplierOwnerAdapterId)
                    .ToArrayAsync(cancellationToken),
                record => Assert.Equal((int)PrerequisiteStatus.Satisfied, record.Status));
        }
    }

    [Fact]
    public async Task A_supplier_blocked_after_submission_fails_its_prerequisite_without_breaking_the_other_one()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await SupplierHarness.StartAsync(cancellationToken);
        var active = await ApprovedSupplierAsync(harness, "ABC Tech SAC", "20123456789", cancellationToken);
        var soonBlocked = await ApprovedSupplierAsync(harness, "XYZ Supplies SAC", "20987654321", cancellationToken);

        Guid caseId;
        Guid blockedPrerequisiteId;
        await using (var context = harness.CreateContext())
        {
            var services = harness.CreateServices(context);
            var creation = await services.PurchaseRequests.Persistence.CreateAsync(
                new PurchaseRequestCreateCommand(
                    harness.OrganizationId, harness.RequesterId,
                    new VersionedEntityRef("LEGAL_ENTITY", harness.LegalEntityId, 1),
                    "Blocked supplier evidence", $"create-{Guid.NewGuid():N}", "Initial request",
                    [
                        new PurchaseRequestLineDraft("line-1", Line(harness, active.SupplierId, active.Version)),
                        new PurchaseRequestLineDraft(
                            "line-2", Line(harness, soonBlocked.SupplierId, soonBlocked.Version))
                    ]),
                harness.RequesterId, "corr-create-pr", DateTimeOffset.UtcNow, cancellationToken);
            await ActivatePolicyAsync(context, harness, cancellationToken);
            await services.PurchaseRequests.Attestation.EnsureAttestedAsync(
                creation.RequestId, creation.Version, harness.OrganizationId, DateTimeOffset.UtcNow, cancellationToken);
            _ = await services.PurchaseRequests.PolicyEvaluations.EvaluateEnterprisePurchaseRequestAsync(
                new PolicyFactRequest(
                    PurchaseRequestCodes.SubjectType,
                    creation.RequestId,
                    creation.Version,
                    PurchaseRequestCodes.EvaluationOperation,
                    DateTimeOffset.UtcNow,
                    new PolicyWorkloadPrincipal(
                        SupplierHarness.SubmissionWorkload.Issuer,
                        SupplierHarness.SubmissionWorkload.ClientId),
                    "corr-policy")
                {
                    OrganizationId = harness.OrganizationId
                },
                "evaluation-key-2",
                cancellationToken);
            var submission = await services.Submissions.SubmitAsync(
                new ApprovalSubmissionCommand(
                    SupplierHarness.SubmissionWorkload,
                    harness.OrganizationId,
                    PurchaseRequestCodes.SubjectType,
                    creation.RequestId,
                    creation.Version,
                    PurchaseRequestCodes.SubmitOperation,
                    PolicyApprovalAdapter.ContractVersionV4,
                    $"submit-{Guid.NewGuid():N}",
                    harness.RequesterId,
                    harness.RequesterId,
                    "corr-submit-pr"),
                DateTimeOffset.UtcNow,
                cancellationToken);
            caseId = submission.CaseId;
            var prerequisites = await context.ApprovalPrerequisites
                .AsNoTracking()
                .Where(record => record.CaseId == caseId &&
                                 record.OwnerAdapterId == SupplierCodes.ActiveSupplierOwnerAdapterId)
                .ToArrayAsync(cancellationToken);
            Assert.Equal(2, prerequisites.Length);
            blockedPrerequisiteId = prerequisites
                .Single(record => JsonSupplierRef(record.ParametersJson) == soonBlocked.SupplierId).Id;
        }

        // The supplier is blocked while its prerequisite is still waiting, so the owner must re-check
        // the current operational version instead of trusting the reference it saw at submission.
        await using (var context = harness.CreateContext())
        {
            var services = harness.CreateServices(context);
            var operational = await services.Persistence.FindOperationalAsync(
                harness.OrganizationId, soonBlocked.SupplierId, cancellationToken);
            var outcome = await services.Governance.SaveAsync(
                harness.OrganizationId, harness.BuyerId, soonBlocked.SupplierId, operational!.Version,
                Content(harness, "XYZ Supplies SAC", "20987654321"),
                SupplierOperationalStatus.Blocked, "Compliance hold", "block-1", "corr-block", cancellationToken);
            await services.Governance.SubmitAsync(
                harness.OrganizationId, harness.BuyerId, soonBlocked.SupplierId, outcome.Version.Version,
                "Block", "submit-block", "corr-submit-block", cancellationToken);
        }

        await harness.DecideAndDispatchAsync(ApprovalDecisionAction.Approve, cancellationToken);

        await using (var context = harness.CreateContext())
        {
            var services = harness.CreateServices(context);
            var prerequisites = await context.ApprovalPrerequisites
                .AsNoTracking()
                .Where(record => record.CaseId == caseId &&
                                 record.OwnerAdapterId == SupplierCodes.ActiveSupplierOwnerAdapterId)
                .ToArrayAsync(cancellationToken);
            foreach (var prerequisite in prerequisites)
            {
                var outcome = await services.Processor.ProcessOneAsync(
                    prerequisite.Id, DateTimeOffset.UtcNow, cancellationToken);
                Assert.NotNull(outcome);
                var expected = prerequisite.Id == blockedPrerequisiteId ? "FAILED" : "SATISFIED";
                Assert.Equal(expected, outcome!.SignalResult);
            }

            var blockedAttempt = await context.SupplierPrerequisiteAttempts
                .AsNoTracking()
                .SingleAsync(record => record.PrerequisiteId == blockedPrerequisiteId, cancellationToken);
            Assert.Equal("COMPLETED", blockedAttempt.State);
            Assert.Equal("FAILED", blockedAttempt.SignalResult);
            Assert.Equal(soonBlocked.SupplierId, blockedAttempt.SupplierId);
        }
    }

    private static async Task ActivatePolicyAsync(
        ProcureToPayDbContext context,
        SupplierHarness harness,
        CancellationToken cancellationToken)
    {
        // A line-scope rule requires an active supplier for every covered line, so the combined
        // control spans both suppliers and must be partitioned by the v4 adapter.
        var policy = new PolicySetVersion(
            Guid.NewGuid(), harness.OrganizationId, 1, [PolicyScope.Line, PolicyScope.Request]);
        policy.AddRule(new PolicyRule(
            "ACTIVE_SUPPLIER",
            PolicyScope.Line,
            [],
            [new PolicyEffect(PolicyEffectType.RequireActiveSupplier, "ACTIVE_SUPPLIER")]));
        policy.AddRule(new PolicyRule(
            "LINE_FALLBACK", PolicyScope.Line, [], [new PolicyEffect(PolicyEffectType.Allow, "LINE_ALLOW")],
            isFallback: true));
        policy.AddRule(new PolicyRule(
            "REQUEST_FALLBACK", PolicyScope.Request, [], [new PolicyEffect(PolicyEffectType.Allow, "REQUEST_ALLOW")],
            isFallback: true));
        policy.Publish(PolicyCanonicalizer.ComputePolicyDigest(policy));
        var persistence = new PolicyPersistenceService(context);
        var actor = new PolicyActor("USER", harness.BuyerId);
        var appended = await persistence.AppendVersionAsync(
            policy, DateTimeOffset.UtcNow, actor, "Cross-module supplier policy", "corr-policy-append",
            cancellationToken);
        await persistence.ActivateAsync(
            harness.OrganizationId, appended.Id, DateTimeOffset.UtcNow.AddSeconds(1), actor,
            "Activate cross-module supplier policy", "corr-policy-activate", cancellationToken);
        // Activation must not start in the past, so the suite waits for its inclusive instant
        // before evaluating against it.
        await Task.Delay(TimeSpan.FromMilliseconds(1_100), cancellationToken);
    }

    private static Guid JsonSupplierRef(string parametersJson)
    {
        using var document = System.Text.Json.JsonDocument.Parse(parametersJson);
        return document.RootElement.GetProperty("supplier_ref").GetProperty("id").GetGuid();
    }

    private static PurchaseRequestLineContent Line(SupplierHarness harness, Guid supplierId, int supplierVersion) =>
        new(
            100m, "PEN", 100m, "PEN", 2026, "GOOD",
            new VersionedCodeRef(
                "SPEND_CATEGORY", harness.SpendCategoryCode, harness.SpendCategoryVersion, harness.SpendCategoryDigest),
            new VersionedEntityRef("COST_CENTER", harness.CostCenterId, harness.CostCenterVersion),
            new VersionedEntityRef("DEPARTMENT", harness.DepartmentId, 1),
            new VersionedEntityRef("DEPARTMENT", harness.DepartmentId, 1),
            new VersionedEntityRef("USER", harness.RequestedForId, 1),
            supplierRef: new VersionedEntityRef("SUPPLIER", supplierId, supplierVersion),
            preferredProductRef: null,
            requiredProductRef: null,
            contractRequired: false,
            nonStandardTerms: false,
            agreementStatus: "NONE",
            needSummary: "Cross-module supplier evidence",
            riskAnswers: [],
            fxAttestationRef: null);

    private static SupplierVersionContent Content(SupplierHarness harness, string legalName, string taxId) => new(
        legalName, null, "PE", taxId,
        [
            new SupplierAddress(
                Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"), "HQ", "Av. Siempre Viva 742", null, "Lima",
                null, null, "PE")
        ],
        [new SupplierContact(Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"), "Ana", "ana@abc.test", null, null)],
        new SupplierPaymentTerms("NET30", 30),
        ["PEN"],
        [new VersionedCodeRef(
            "SPEND_CATEGORY", harness.SpendCategoryCode, harness.SpendCategoryVersion, harness.SpendCategoryDigest)],
        SupplierOperationalStatus.Draft,
        SupplierRiskStatus.Low,
        null,
        []);

    private static async Task<(Guid SupplierId, int Version)> ApprovedSupplierAsync(
        SupplierHarness harness,
        string legalName,
        string taxId,
        CancellationToken cancellationToken)
    {
        Guid supplierId;
        await using (var context = harness.CreateContext())
        {
            var services = harness.CreateServices(context);
            var outcome = await services.Governance.CreateAsync(
                harness.OrganizationId, harness.BuyerId, Content(harness, legalName, taxId),
                SupplierOperationalStatus.Active, "New supplier", $"create-{Guid.NewGuid():N}", "corr-create",
                cancellationToken);
            supplierId = outcome.Version.SupplierId;
            await services.Governance.SubmitAsync(
                harness.OrganizationId, harness.BuyerId, supplierId, outcome.Version.Version,
                "Submit", $"submit-{Guid.NewGuid():N}", "corr-submit", cancellationToken);
        }

        await harness.DecideAndDispatchAsync(ApprovalDecisionAction.Approve, cancellationToken);
        await using (var context = harness.CreateContext())
        {
            var view = await harness.CreateServices(context).Persistence.FindOperationalAsync(
                harness.OrganizationId, supplierId, cancellationToken);
            return (supplierId, view!.Version);
        }
    }
}
