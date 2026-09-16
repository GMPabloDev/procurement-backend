using Microsoft.EntityFrameworkCore;
using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Domain.Modules.Organization;
using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Domain.Modules.Suppliers;
using ProcureToPay.Domain.SharedKernel;
using ProcureToPay.Infrastructure.Persistence;
using ProcureToPay.Infrastructure.Persistence.Suppliers;

namespace ProcureToPay.IntegrationTests.Suppliers;

/// <summary>
/// SQL Server evidence of the hardening the independent review demanded (SPEC 09 CA-01, CA-03,
/// CA-04, CA-05, CA-07, CA-08): concurrency, durable idempotency, catalogue selector/overlap
/// invariants, replay of a delivered approval, terminal attempts and corruption detection.
/// </summary>
public sealed class SupplierHardeningIntegrationTests
{
    [Fact]
    public async Task Two_concurrent_saves_leave_exactly_one_successor()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await SupplierHarness.StartAsync(cancellationToken);
        var supplierId = await ApprovedSupplierAsync(harness, cancellationToken);

        await using var first = harness.CreateContext();
        await using var second = harness.CreateContext();
        var firstServices = harness.CreateServices(first);
        var secondServices = harness.CreateServices(second);
        var version = (await firstServices.Persistence.FindOperationalAsync(
            harness.OrganizationId, supplierId, cancellationToken))!.Version;

        var start = new TaskCompletionSource();
        var one = Task.Run(async () =>
        {
            await start.Task;
            try
            {
                return await firstServices.Governance.SaveAsync(
                    harness.OrganizationId, harness.BuyerId, supplierId, version,
                    Content(harness, tradeName: "Concurrent A"), SupplierOperationalStatus.Active,
                    "Race A", $"race-a-{Guid.NewGuid():N}", "corr-race-a", cancellationToken);
            }
            catch (DomainConflictException)
            {
                return null;
            }
        }, cancellationToken);
        var other = Task.Run(async () =>
        {
            await start.Task;
            try
            {
                return await secondServices.Governance.SaveAsync(
                    harness.OrganizationId, harness.BuyerId, supplierId, version,
                    Content(harness, tradeName: "Concurrent B"), SupplierOperationalStatus.Active,
                    "Race B", $"race-b-{Guid.NewGuid():N}", "corr-race-b", cancellationToken);
            }
            catch (DomainConflictException)
            {
                return null;
            }
        }, cancellationToken);
        start.SetResult();
        var outcomes = await Task.WhenAll(one, other);

        Assert.Single(outcomes.Where(outcome => outcome is not null));
        await using var verification = harness.CreateContext();
        var versions = await verification.SupplierVersions
            .AsNoTracking()
            .Where(record => record.SupplierId == supplierId)
            .Select(record => record.Version)
            .ToArrayAsync(cancellationToken);
        Assert.Equal(versions.Length, versions.Distinct().Count());
    }

    [Fact]
    public async Task A_banking_command_replays_by_key_or_conflicts_with_another_payload()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await SupplierHarness.StartAsync(cancellationToken);
        var supplierId = await ApprovedSupplierAsync(harness, cancellationToken);
        await using var context = harness.CreateContext();
        var services = harness.CreateServices(context);
        var content = new SupplierBankingContent(
            "ABC Tech SAC", "Banco de Prueba", "PE", "PEN", SupplierBankingAccountType.Checking,
            "00123456789012345678", null, "BANKPEPL", IsDefault: true);

        var stored = await services.Banking.SaveAsync(
            harness.OrganizationId, supplierId, harness.BuyerId, null, content,
            "Initial banking", "banking-key-1", "corr-banking-1", cancellationToken);

        // The same payload with the same key replays the original version instead of creating another.
        var replayed = await services.Banking.SaveAsync(
            harness.OrganizationId, supplierId, harness.BuyerId, null, content,
            "Initial banking", "banking-key-1", "corr-banking-1", cancellationToken);
        Assert.Equal(stored, replayed);
        Assert.Equal(1, await context.SupplierBankingVersions.CountAsync(
            record => record.BankingDetailId == stored.Id, cancellationToken));

        // Another payload with the same key is a conflict, never a silent overwrite.
        await Assert.ThrowsAsync<DomainConflictException>(() => services.Banking.SaveAsync(
            harness.OrganizationId, supplierId, harness.BuyerId, null,
            content with { AccountNumber = "00999999999999999999" },
            "Different account", "banking-key-1", "corr-banking-1", cancellationToken));

        // A staged account is never the default of the supplier: the default follows the approved
        // version that references it.
        Assert.Empty(await context.SupplierBankingDefaults
            .AsNoTracking()
            .Where(record => record.SupplierId == supplierId)
            .ToArrayAsync(cancellationToken));
        await using (var revision = harness.CreateContext())
        {
            var revisionServices = harness.CreateServices(revision);
            var operational = await revisionServices.Persistence.FindOperationalAsync(
                harness.OrganizationId, supplierId, cancellationToken);
            var outcome = await revisionServices.Governance.SaveAsync(
                harness.OrganizationId, harness.BuyerId, supplierId, operational!.Version,
                Content(harness, bankingRef: new SupplierBankingRef(stored.Id, stored.Version)),
                SupplierOperationalStatus.Active,
                "Publish banking", "banking-publish-1", "corr-banking-publish", cancellationToken);
            await revisionServices.Governance.SubmitAsync(
                harness.OrganizationId, harness.BuyerId, supplierId, outcome.Version.Version,
                "Submit banking", "submit-banking-publish", "corr-submit-banking", cancellationToken);
        }

        await harness.DecideAndDispatchAsync(ApprovalDecisionAction.Approve, cancellationToken);

        await using var verification = harness.CreateContext();
        var defaults = await verification.SupplierBankingDefaults
            .AsNoTracking()
            .Where(record => record.SupplierId == supplierId)
            .ToArrayAsync(cancellationToken);
        var only = Assert.Single(defaults);
        Assert.Equal(stored.Id, only.BankingDetailId);
        Assert.Equal(stored.Version, only.Version);
    }

    [Fact]
    public async Task A_duplicate_general_entry_a_selector_change_and_an_overlap_are_rejected()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await SupplierHarness.StartAsync(cancellationToken);
        var supplierId = await ApprovedSupplierAsync(harness, cancellationToken);
        var supplierVersion = (await harness.CreateServices(harness.CreateContext())
            .Persistence.FindOperationalAsync(harness.OrganizationId, supplierId, cancellationToken))!.Version;
        await using var context = harness.CreateContext();
        var services = harness.CreateServices(context);
        var attachment = await StageAsync(harness, context, services, supplierId, cancellationToken);
        var from = DateTimeOffset.UtcNow.AddDays(-1);
        var to = DateTimeOffset.UtcNow.AddDays(30);

        var first = await services.Catalog.SaveAsync(
            harness.OrganizationId, harness.BuyerId, null, null,
            new VersionedEntityRef("SUPPLIER", supplierId, supplierVersion), Category(harness), null,
            900m, "PEN", "UNIT", "ACME-2026-001", from, to, ApprovedCatalogEntryStatus.Active,
            attachment.Id, attachment.Version, "First general entry", "catalog-a", "corr-a", cancellationToken);
        await services.Catalog.SubmitAsync(
            harness.OrganizationId, harness.BuyerId, first.Version.CatalogEntryId, first.Version.Version,
            "submit-a", "corr-submit-a", cancellationToken);
        await harness.DecideAndDispatchAsync(ApprovalDecisionAction.Approve, cancellationToken);

        // The approval ran in another context: this one must observe the committed pointer.
        context.ChangeTracker.Clear();

        // A second root for the exact same selector is a conflict, not a second effective entry.
        await Assert.ThrowsAsync<DomainConflictException>(() => services.Catalog.SaveAsync(
            harness.OrganizationId, harness.BuyerId, null, null,
            new VersionedEntityRef("SUPPLIER", supplierId, supplierVersion), Category(harness), null,
            950m, "PEN", "UNIT", "ACME-2026-002", from, to, ApprovedCatalogEntryStatus.Active,
            attachment.Id, attachment.Version, "Duplicate general entry", "catalog-b", "corr-b", cancellationToken));

        // A revision cannot move the version to another selector.
        await Assert.ThrowsAsync<DomainValidationException>(() => services.Catalog.SaveAsync(
            harness.OrganizationId, harness.BuyerId, first.Version.CatalogEntryId, 1,
            new VersionedEntityRef("SUPPLIER", supplierId, supplierVersion), Category(harness),
            new VersionedEntityRef("PRODUCT", Guid.NewGuid(), 1),
            950m, "PEN", "UNIT", "ACME-2026-003", from, to, ApprovedCatalogEntryStatus.Active,
            attachment.Id, attachment.Version, "Moved selector", "catalog-c", "corr-c", cancellationToken));

        // An ACTIVE window that overlaps the current version of the same selector is refused.
        await Assert.ThrowsAsync<DomainConflictException>(() => services.Catalog.SaveAsync(
            harness.OrganizationId, harness.BuyerId, first.Version.CatalogEntryId, 1,
            new VersionedEntityRef("SUPPLIER", supplierId, supplierVersion), Category(harness), null,
            950m, "PEN", "UNIT", "ACME-2026-004", DateTimeOffset.UtcNow, to.AddDays(10),
            ApprovedCatalogEntryStatus.Active, attachment.Id, attachment.Version,
            "Overlapping window", "catalog-d", "corr-d", cancellationToken));
    }

    [Fact]
    public async Task A_redelivered_approval_result_materializes_exactly_one_version()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await SupplierHarness.StartAsync(cancellationToken);
        Guid supplierId;
        int candidateVersion;
        await using (var context = harness.CreateContext())
        {
            var services = harness.CreateServices(context);
            var outcome = await services.Governance.CreateAsync(
                harness.OrganizationId, harness.BuyerId, Content(harness), SupplierOperationalStatus.Active,
                "New supplier", "create-1", "corr-create", cancellationToken);
            supplierId = outcome.Version.SupplierId;
            candidateVersion = outcome.Version.Version;
            await services.Governance.SubmitAsync(
                harness.OrganizationId, harness.BuyerId, supplierId, candidateVersion,
                "Submit for activation", "submit-1", "corr-submit", cancellationToken);
        }

        // The approval is decided once, but the result is delivered twice (at-least-once outbox).
        await harness.DecideFirstPendingAsyncCoreAsync(cancellationToken);
        var first = await harness.DispatchResultsAsync(cancellationToken);
        Assert.Equal(0, first.Failed + first.DeadLettered);
        var replay = await harness.RedispatchLastEventAsync(cancellationToken);
        Assert.Equal(0, replay.Failed + replay.DeadLettered);

        await using (var verification = harness.CreateContext())
        {
            var services = harness.CreateServices(verification);
            var projection = await services.Governance.ReadProjectionAsync(
                harness.OrganizationId, supplierId, cancellationToken);
            Assert.Equal("ACTIVE", projection.Status);
            var versions = await verification.SupplierVersions
                .AsNoTracking()
                .Where(record => record.SupplierId == supplierId && record.ProposalId == null)
                .ToArrayAsync(cancellationToken);
            Assert.Single(versions);
            Assert.Equal(1, await verification.SupplierStatusOutbox.CountAsync(
                record => record.SupplierId == supplierId, cancellationToken));
            Assert.Equal(1, await verification.SupplierApprovalResults.CountAsync(
                record => record.TargetId == supplierId && record.TargetVersion == candidateVersion,
                cancellationToken));
        }
    }

    [Fact]
    public async Task A_completed_attempt_is_terminal_and_an_unregistered_processor_claims_nothing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await SupplierHarness.StartAsync(cancellationToken);
        var supplierId = await ApprovedSupplierAsync(harness, cancellationToken);
        Guid prerequisiteId;
        await using (var context = harness.CreateContext())
        {
            var services = harness.CreateServices(context);
            prerequisiteId = await CreatePrerequisiteAsync(harness, context, services, supplierId, cancellationToken);
            var first = await services.Processor.ProcessOneAsync(
                prerequisiteId, DateTimeOffset.UtcNow, cancellationToken);
            Assert.NotNull(first);
            Assert.Equal("SATISFIED", first!.SignalResult);

            // A terminal attempt is never reclaimed.
            var again = await services.Processor.ProcessOneAsync(
                prerequisiteId, DateTimeOffset.UtcNow.AddMinutes(1), cancellationToken);
            Assert.Null(again);
            var attempt = await context.SupplierPrerequisiteAttempts
                .AsNoTracking()
                .SingleAsync(record => record.PrerequisiteId == prerequisiteId, cancellationToken);
            Assert.Equal("COMPLETED", attempt.State);
            Assert.Equal(0, attempt.Attempts - 1);
        }

        // Removing the single registration makes the processor claim nothing (REQ-10).
        await using (var context = harness.CreateContext())
        {
            await context.Database.ExecuteSqlRawAsync(
                "DELETE FROM [Supplier].[SupplierPrerequisiteProcessorRegistrations]", cancellationToken);
            var services = harness.CreateServices(context);
            Assert.False(await services.Processor.IsRegisteredAsync(cancellationToken));
            Assert.Empty(await services.Processor.ProcessDueAsync(
                DateTimeOffset.UtcNow.AddMinutes(5), cancellationToken));
        }
    }

    [Fact]
    public async Task Readiness_detects_a_corrupted_approved_version()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await SupplierHarness.StartAsync(cancellationToken);
        var supplierId = await ApprovedSupplierAsync(harness, cancellationToken);

        await using var context = harness.CreateContext();
        var services = harness.CreateServices(context);
        var operational = await services.Persistence.FindOperationalAsync(
            harness.OrganizationId, supplierId, cancellationToken);
        var clean = await services.Persistence.DiagnoseAsync(cancellationToken);
        Assert.Equal(0, clean.CorruptedDigests);

        // A forged successor with a valid predecessor and a bogus digest can be pointed at; the
        // readiness diagnostic must catch it instead of reporting a healthy supplier.
        await context.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO [Supplier].[SupplierVersions]
            ([SupplierId],[Version],[OrganizationId],[PredecessorVersion],[LegalName],[TradeName],
             [CountryCode],[TaxId],[AddressesJson],[ContactsJson],[PaymentTermsJson],
             [SupportedCurrenciesJson],[CategoriesSuppliedJson],[PerformanceJson],[BankingRefsJson],
             [Status],[RiskStatus],[ContentDigest],[ActorUserId],[OccurredAt],[Reason])
            SELECT [SupplierId],[Version] + 1,[OrganizationId],[Version],[LegalName],[TradeName],
             [CountryCode],[TaxId],[AddressesJson],[ContactsJson],[PaymentTermsJson],
             [SupportedCurrenciesJson],[CategoriesSuppliedJson],[PerformanceJson],[BankingRefsJson],
             [Status],[RiskStatus],REPLICATE('0', 64),[ActorUserId],SYSUTCDATETIME(),'forged'
            FROM [Supplier].[SupplierVersions] WHERE [SupplierId] = {0} AND [Version] = {1};
            """,
            [supplierId, operational!.Version],
            cancellationToken);
        await context.Database.ExecuteSqlRawAsync(
            "UPDATE [Supplier].[Suppliers] SET [OperationalVersion] = {1} WHERE [Id] = {0}",
            [supplierId, operational.Version + 1],
            cancellationToken);

        var corrupted = await services.Persistence.DiagnoseAsync(cancellationToken);
        Assert.Equal(1, corrupted.CorruptedDigests);
    }

    private static async Task<Guid> CreatePrerequisiteAsync(
        SupplierHarness harness,
        ProcureToPayDbContext context,
        Services services,
        Guid supplierId,
        CancellationToken cancellationToken)
    {
        var supplierVersion = (await services.Persistence.FindOperationalAsync(
            harness.OrganizationId, supplierId, cancellationToken))!.Version;
        var creation = await services.PurchaseRequests.Persistence.CreateAsync(
            new Domain.Modules.PurchaseRequests.PurchaseRequestCreateCommand(
                harness.OrganizationId, harness.RequesterId,
                new VersionedEntityRef("LEGAL_ENTITY", harness.LegalEntityId, 1),
                "Prerequisite evidence", $"create-{Guid.NewGuid():N}", "Initial request",
                [
                    new Domain.Modules.PurchaseRequests.PurchaseRequestLineDraft(
                        "line-1", Line(harness, supplierId, supplierVersion))
                ]),
            harness.RequesterId, "corr-create-pr", DateTimeOffset.UtcNow, cancellationToken);
        await harness.ActivatePolicyAsync(context, cancellationToken);
        await services.PurchaseRequests.Attestation.EnsureAttestedAsync(
            creation.RequestId, creation.Version, harness.OrganizationId, DateTimeOffset.UtcNow, cancellationToken);
        _ = await services.PurchaseRequests.PolicyEvaluations.EvaluateEnterprisePurchaseRequestAsync(
            new ProcureToPay.Infrastructure.Persistence.Policy.PolicyFactRequest(
                Domain.Modules.PurchaseRequests.PurchaseRequestCodes.SubjectType,
                creation.RequestId,
                creation.Version,
                Domain.Modules.PurchaseRequests.PurchaseRequestCodes.EvaluationOperation,
                DateTimeOffset.UtcNow,
                new ProcureToPay.Infrastructure.Persistence.Policy.PolicyWorkloadPrincipal(
                    SupplierHarness.SubmissionWorkload.Issuer, SupplierHarness.SubmissionWorkload.ClientId),
                "corr-policy")
            {
                OrganizationId = harness.OrganizationId
            },
            $"evaluation-{Guid.NewGuid():N}",
            cancellationToken);
        var submission = await services.Submissions.SubmitAsync(
            new ProcureToPay.Infrastructure.Persistence.Approval.ApprovalSubmissionCommand(
                SupplierHarness.SubmissionWorkload,
                harness.OrganizationId,
                Domain.Modules.PurchaseRequests.PurchaseRequestCodes.SubjectType,
                creation.RequestId,
                creation.Version,
                Domain.Modules.PurchaseRequests.PurchaseRequestCodes.SubmitOperation,
                ProcureToPay.Infrastructure.Persistence.Approval.PolicyApprovalAdapter.ContractVersionV4,
                $"submit-{Guid.NewGuid():N}",
                harness.RequesterId,
                harness.RequesterId,
                "corr-submit-pr"),
            DateTimeOffset.UtcNow,
            cancellationToken);
        return await context.ApprovalPrerequisites
            .AsNoTracking()
            .Where(record => record.CaseId == submission.CaseId &&
                             record.OwnerAdapterId == SupplierCodes.ActiveSupplierOwnerAdapterId)
            .Select(record => record.Id)
            .SingleAsync(cancellationToken);
    }

    private static async Task<SupplierAgreementAttachmentView> StageAsync(
        SupplierHarness harness,
        ProcureToPayDbContext context,
        Services services,
        Guid supplierId,
        CancellationToken cancellationToken)
    {
        var bytes = "agreement"u8.ToArray();
        var attachment = await services.Catalog.StageAttachmentAsync(
            harness.OrganizationId, supplierId, harness.BuyerId, "agreement.pdf", "application/pdf",
            bytes.Length, new MemoryStream(bytes), cancellationToken);
        return await services.Catalog.ConfirmAttachmentAsync(
            harness.OrganizationId, attachment.Id, harness.BuyerId, attachment.Sha256, cancellationToken);
    }

    private static VersionedCodeRef Category(SupplierHarness harness) =>
        new("SPEND_CATEGORY", harness.SpendCategoryCode, harness.SpendCategoryVersion, harness.SpendCategoryDigest);

    private static Domain.Modules.PurchaseRequests.PurchaseRequestLineContent Line(
        SupplierHarness harness,
        Guid supplierId,
        int supplierVersion) => new(
        100m, "PEN", 100m, "PEN", 2026, "GOOD",
        new VersionedCodeRef(
            "SPEND_CATEGORY", harness.SpendCategoryCode, harness.SpendCategoryVersion, harness.SpendCategoryDigest),
        new VersionedEntityRef("COST_CENTER", harness.CostCenterId, harness.CostCenterVersion),
        new VersionedEntityRef("DEPARTMENT", harness.DepartmentId, 1),
        new VersionedEntityRef("DEPARTMENT", harness.DepartmentId, 1),
        new VersionedEntityRef("USER", harness.RequestedForId, 1),
        new VersionedEntityRef("SUPPLIER", supplierId, supplierVersion),
        null,
        null,
        false,
        false,
        "NONE",
        "Hardening evidence",
        [],
        null);

    private static SupplierVersionContent Content(
        SupplierHarness harness,
        string? tradeName = null,
        SupplierBankingRef? bankingRef = null) => new(
        "ABC Tech SAC", tradeName, "PE", "20123456789",
        [
            new SupplierAddress(
                Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"), "HQ", "Av. Siempre Viva 742",
                null, "Lima", null, null, "PE")
        ],
        [new SupplierContact(Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"), "Ana", "ana@abc.test", null, null)],
        new SupplierPaymentTerms("NET30", 30),
        ["PEN"],
        [Category(harness)],
        SupplierOperationalStatus.Draft,
        SupplierRiskStatus.Low,
        null,
        bankingRef is null ? [] : [bankingRef]);

    private static async Task<Guid> ApprovedSupplierAsync(
        SupplierHarness harness,
        CancellationToken cancellationToken)
    {
        Guid supplierId;
        await using (var context = harness.CreateContext())
        {
            var services = harness.CreateServices(context);
            var outcome = await services.Governance.CreateAsync(
                harness.OrganizationId, harness.BuyerId, Content(harness), SupplierOperationalStatus.Active,
                "New supplier", $"create-{Guid.NewGuid():N}", "corr-create", cancellationToken);
            supplierId = outcome.Version.SupplierId;
            await services.Governance.SubmitAsync(
                harness.OrganizationId, harness.BuyerId, supplierId, outcome.Version.Version,
                "Submit", $"submit-{Guid.NewGuid():N}", "corr-submit", cancellationToken);
        }

        await harness.DecideAndDispatchAsync(ApprovalDecisionAction.Approve, cancellationToken);
        return supplierId;
    }
}
