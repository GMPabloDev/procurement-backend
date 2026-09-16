using Microsoft.EntityFrameworkCore;
using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Domain.Modules.Organization;
using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Domain.Modules.ReferenceCatalogs;
using ProcureToPay.Domain.Modules.Suppliers;
using ProcureToPay.Domain.SharedKernel;
using ProcureToPay.Infrastructure.Persistence.Suppliers;

namespace ProcureToPay.IntegrationTests.Suppliers;

/// <summary>
/// SQL Server evidence of the Supplier Master (SPEC 09 REQ-01..REQ-05, REQ-11): the governed
/// lifecycle, the server-side classification, the segregation of duties, the fiscal identity
/// reservation and the encrypted banking data.
/// </summary>
public sealed class SupplierGovernanceIntegrationTests
{
    private static readonly DateTimeOffset Clock = DateTimeOffset.Parse("2026-09-15T12:00:00.0000000Z");

    [Fact]
    public async Task A_new_supplier_is_drafted_and_only_an_approved_decision_materializes_it()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await SupplierHarness.StartAsync(cancellationToken);
        Guid supplierId;
        await using (var context = harness.CreateContext())
        {
            var services = harness.CreateServices(context);
            var outcome = await services.Governance.CreateAsync(
                harness.OrganizationId,
                harness.BuyerId,
                Content(harness),
                SupplierOperationalStatus.Active,
                "New supplier",
                "create-1",
                "corr-create",
                cancellationToken);
            supplierId = outcome.Version.SupplierId;
            Assert.True(outcome.RequiresApproval);
            Assert.Equal("DRAFT", outcome.ProjectedStatus);
            Assert.Null(await services.Persistence.FindOperationalAsync(
                harness.OrganizationId, supplierId, cancellationToken));

            var submission = await services.Governance.SubmitAsync(
                harness.OrganizationId,
                harness.BuyerId,
                supplierId,
                outcome.Version.Version,
                "Submit for activation",
                "submit-1",
                "corr-submit",
                cancellationToken);
            Assert.NotEqual(Guid.Empty, submission.CaseId);
            var projection = await services.Governance.ReadProjectionAsync(
                harness.OrganizationId, supplierId, cancellationToken);
            Assert.Equal("PENDING_APPROVAL", projection.Status);
        }

        // The editor holds a SUPPLIER_MASTER grant too, yet the requirement excludes it (REQ-03).
        var decided = await harness.DecideFirstPendingAsync(
            ApprovalDecisionAction.Approve, cancellationToken: cancellationToken);
        Assert.NotEqual(Guid.Empty, decided);
        await using (var diagnostic = harness.CreateContext())
        {
            var outbox = await diagnostic.ApprovalOutboxEvents.AsNoTracking()
                .Select(record => new { record.State, record.LastError, record.ContractVersion })
                .ToArrayAsync(cancellationToken);
            Assert.True(
                outbox.Length > 0,
                $"No approval event was recorded. Decided={decided}; outbox={outbox.Length}; " +
                $"errors={string.Join(",", outbox.Select(record => $"{record.State}:{record.LastError}"))}");
        }

        await using (var context = harness.CreateContext())
        {
            var services = harness.CreateServices(context);
            var projection = await services.Governance.ReadProjectionAsync(
                harness.OrganizationId, supplierId, cancellationToken);
            Assert.Equal("ACTIVE", projection.Status);
            Assert.NotNull(projection.Operational);
            Assert.True(SupplierStatusCodes.IsUsable(projection.Operational!.Content.Status));

            var events = await context.SupplierStatusOutbox
                .AsNoTracking()
                .Where(record => record.SupplierId == supplierId)
                .ToArrayAsync(cancellationToken);
            Assert.Single(events);
            Assert.Equal((int)SupplierOperationalStatus.Active, events[0].Status);
            Assert.Null(events[0].PreviousStatus);

            var references = await services.Persistence.ListActiveAsync(harness.OrganizationId, cancellationToken);
            Assert.Contains(references, reference => reference.Id == supplierId);
        }
    }

    [Fact]
    public async Task A_cosmetic_change_is_applied_by_the_buyer_without_an_approval_case()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await SupplierHarness.StartAsync(cancellationToken);
        var supplierId = await ApprovedSupplierAsync(harness, cancellationToken);

        await using var context = harness.CreateContext();
        var services = harness.CreateServices(context);
        var operational = await services.Persistence.FindOperationalAsync(
            harness.OrganizationId, supplierId, cancellationToken);
        var cosmetic = Content(harness, tradeName: "ABC Renewed");

        var outcome = await services.Governance.SaveAsync(
            harness.OrganizationId,
            harness.BuyerId,
            supplierId,
            operational!.Version,
            cosmetic,
            SupplierOperationalStatus.Active,
            "Trade name refresh",
            "cosmetic-1",
            "corr-cosmetic",
            cancellationToken);

        Assert.False(outcome.RequiresApproval);
        Assert.Empty(outcome.SensitiveFields);
        Assert.Equal("ACTIVE", outcome.ProjectedStatus);
        Assert.Equal(operational.Version + 1, outcome.Version.Version);
        Assert.Equal(0, await context.SupplierChangeProposals.CountAsync(
            record => record.SupplierId == supplierId &&
                      record.State == (int)SupplierProposalState.Pending, cancellationToken));
    }

    [Fact]
    public async Task A_sensitive_change_without_approval_keeps_the_approved_version_and_a_rejection_closes_it()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await SupplierHarness.StartAsync(cancellationToken);
        var supplierId = await ApprovedSupplierAsync(harness, cancellationToken);

        int approvedVersion;
        await using (var context = harness.CreateContext())
        {
            var services = harness.CreateServices(context);
            var operational = await services.Persistence.FindOperationalAsync(
                harness.OrganizationId, supplierId, cancellationToken);
            approvedVersion = operational!.Version;

            // Changing the legal name is a governed field: the server classifies it and refuses to
            // apply it without a decision (REQ-02).
            var outcome = await services.Governance.SaveAsync(
                harness.OrganizationId,
                harness.BuyerId,
                supplierId,
                operational.Version,
                Content(harness, legalName: "ABC Tech S.A.C."),
                SupplierOperationalStatus.Active,
                "Legal name correction",
                "sensitive-1",
                "corr-sensitive",
                cancellationToken);
            Assert.True(outcome.RequiresApproval);
            Assert.Contains("legal_name", outcome.SensitiveFields);
            await services.Governance.SubmitAsync(
                harness.OrganizationId,
                harness.BuyerId,
                supplierId,
                outcome.Version.Version,
                "Submit legal name correction",
                "submit-sensitive-1",
                "corr-submit-sensitive",
                cancellationToken);
        }

        await harness.DecideFirstPendingAsync(ApprovalDecisionAction.Reject, cancellationToken: cancellationToken);

        await using (var context = harness.CreateContext())
        {
            var services = harness.CreateServices(context);
            var operational = await services.Persistence.FindOperationalAsync(
                harness.OrganizationId, supplierId, cancellationToken);
            Assert.Equal(approvedVersion, operational!.Version);
            Assert.Equal("ABC Tech SAC", operational.Content.LegalName);
            var proposal = await context.SupplierChangeProposals
                .AsNoTracking()
                .Where(record => record.SupplierId == supplierId)
                .OrderByDescending(record => record.UpdatedAt)
                .FirstAsync(cancellationToken);
            Assert.Equal((int)SupplierProposalState.Rejected, proposal.State);
        }
    }

    [Fact]
    public async Task The_fiscal_identity_is_unique_per_country_and_tax_id()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await SupplierHarness.StartAsync(cancellationToken);
        await using var context = harness.CreateContext();
        var services = harness.CreateServices(context);
        await services.Governance.CreateAsync(
            harness.OrganizationId, harness.BuyerId, Content(harness), SupplierOperationalStatus.Active,
            "First", "identity-1", "corr-1", cancellationToken);

        await Assert.ThrowsAsync<DomainConflictException>(() => services.Governance.CreateAsync(
            harness.OrganizationId, harness.BuyerId, Content(harness, legalName: "Other SA"),
            SupplierOperationalStatus.Active, "Duplicate identity", "identity-2", "corr-2", cancellationToken));

        // The same tax id under another country is a different fiscal identity.
        var other = await services.Governance.CreateAsync(
            harness.OrganizationId, harness.BuyerId, Content(harness, country: "CL"),
            SupplierOperationalStatus.Active, "Other country", "identity-3", "corr-3", cancellationToken);
        Assert.NotEqual(Guid.Empty, other.Version.SupplierId);
    }

    [Fact]
    public async Task A_blocked_supplier_recovers_only_through_a_new_approved_activation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await SupplierHarness.StartAsync(cancellationToken);
        var supplierId = await ApprovedSupplierAsync(harness, cancellationToken);

        // Active -> Blocked is a governed status change that needs its own approved decision.
        await using (var context = harness.CreateContext())
        {
            var services = harness.CreateServices(context);
            var operational = await services.Persistence.FindOperationalAsync(
                harness.OrganizationId, supplierId, cancellationToken);
            var outcome = await services.Governance.SaveAsync(
                harness.OrganizationId, harness.BuyerId, supplierId, operational!.Version,
                Content(harness), SupplierOperationalStatus.Blocked,
                "Compliance hold", "block-1", "corr-block", cancellationToken);
            Assert.Contains("status", outcome.SensitiveFields);
            await services.Governance.SubmitAsync(
                harness.OrganizationId, harness.BuyerId, supplierId, outcome.Version.Version,
                "Block supplier", "submit-block", "corr-submit-block", cancellationToken);
        }

        await harness.DecideFirstPendingAsync(ApprovalDecisionAction.Approve, cancellationToken: cancellationToken);
        await using (var context = harness.CreateContext())
        {
            var services = harness.CreateServices(context);
            var projection = await services.Governance.ReadProjectionAsync(
                harness.OrganizationId, supplierId, cancellationToken);
            Assert.Equal("BLOCKED", projection.Status);
            Assert.Empty(await services.Persistence.ListActiveAsync(harness.OrganizationId, cancellationToken));
        }

        // Blocked -> Active requires another approved activation (REQ-03).
        await using (var context = harness.CreateContext())
        {
            var services = harness.CreateServices(context);
            var operational = await services.Persistence.FindOperationalAsync(
                harness.OrganizationId, supplierId, cancellationToken);
            var outcome = await services.Governance.SaveAsync(
                harness.OrganizationId, harness.BuyerId, supplierId, operational!.Version,
                Content(harness), SupplierOperationalStatus.Active,
                "Hold lifted", "unblock-1", "corr-unblock", cancellationToken);
            await services.Governance.SubmitAsync(
                harness.OrganizationId, harness.BuyerId, supplierId, outcome.Version.Version,
                "Reactivate supplier", "submit-unblock", "corr-submit-unblock", cancellationToken);
        }

        await harness.DecideFirstPendingAsync(ApprovalDecisionAction.Approve, cancellationToken: cancellationToken);
        await using (var context = harness.CreateContext())
        {
            var services = harness.CreateServices(context);
            var projection = await services.Governance.ReadProjectionAsync(
                harness.OrganizationId, supplierId, cancellationToken);
            Assert.Equal("ACTIVE", projection.Status);
            Assert.Single(await services.Persistence.ListActiveAsync(harness.OrganizationId, cancellationToken));
        }
    }

    [Fact]
    public async Task Banking_details_are_encrypted_and_revealed_only_to_the_authorized_actors()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await SupplierHarness.StartAsync(cancellationToken);
        var supplierId = await ApprovedSupplierAsync(harness, cancellationToken);

        Guid bankingDetailId;
        int bankingVersion;
        await using (var context = harness.CreateContext())
        {
            var services = harness.CreateServices(context);
            var reference = await services.Banking.SaveAsync(
                harness.OrganizationId,
                supplierId,
                harness.BuyerId,
                bankingDetailId: null,
                new SupplierBankingContent(
                    "ABC Tech SAC", "Banco de Prueba", "PE", "PEN", SupplierBankingAccountType.Checking,
                    "00123456789012345678", null, "BANKPEPL", IsDefault: true),
                "Initial banking detail",
                "banking-1",
                "corr-banking",
                cancellationToken);
            bankingDetailId = reference.Id;
            bankingVersion = reference.Version;

            // The raw column never stores the account number and the masked projection only keeps
            // the last four characters (REQ-05).
            var row = await context.SupplierBankingVersions
                .AsNoTracking()
                .SingleAsync(record => record.BankingDetailId == bankingDetailId, cancellationToken);
            var ciphertext = System.Text.Encoding.UTF8.GetString(row.Ciphertext);
            Assert.DoesNotContain("00123456789012345678", ciphertext, StringComparison.Ordinal);
            Assert.Equal("5678", row.MaskedSuffix);
            var masked = await services.Banking.ListMaskedAsync(
                harness.OrganizationId, supplierId, cancellationToken);
            Assert.Equal("5678", masked.Single().MaskedAccount);

            // The operational version does not reference the account yet, so even AP cannot reveal it.
            await Assert.ThrowsAsync<DomainForbiddenException>(() => services.Banking.RevealAsync(
                harness.OrganizationId, harness.ApSpecialistId, bankingDetailId, bankingVersion,
                "PAYMENT_EXECUTION", "corr-reveal-0", cancellationToken));

            // The governed change that makes it operational requires an approved decision.
            var operational = await services.Persistence.FindOperationalAsync(
                harness.OrganizationId, supplierId, cancellationToken);
            var outcome = await services.Governance.SaveAsync(
                harness.OrganizationId,
                harness.BuyerId,
                supplierId,
                operational!.Version,
                Content(harness, bankingRef: new SupplierBankingRef(bankingDetailId, bankingVersion)),
                SupplierOperationalStatus.Active,
                "Add banking detail",
                "banking-2",
                "corr-banking-2",
                cancellationToken);
            Assert.Contains("banking_details", outcome.SensitiveFields);

            // The approver of the live task may verify the pending account; nobody else can.
            await services.Governance.SubmitAsync(
                harness.OrganizationId, harness.BuyerId, supplierId, outcome.Version.Version,
                "Submit banking detail", "submit-banking", "corr-submit-banking", cancellationToken);
            var pending = await services.Banking.RevealAsync(
                harness.OrganizationId, harness.ApproverId, bankingDetailId, bankingVersion,
                "APPROVAL_VERIFICATION", "corr-reveal-approver", cancellationToken);
            Assert.Equal("00123456789012345678", pending.AccountNumber);
            await Assert.ThrowsAsync<DomainForbiddenException>(() => services.Banking.RevealAsync(
                harness.OrganizationId, harness.BuyerId, bankingDetailId, bankingVersion,
                "PAYMENT_EXECUTION", "corr-reveal-2", cancellationToken));
        }

        await harness.DecideAndDispatchAsync(ApprovalDecisionAction.Approve, cancellationToken);

        await using (var context = harness.CreateContext())
        {
            var services = harness.CreateServices(context);

            // The AP specialist reads the current operational version.
            var revealed = await services.Banking.RevealAsync(
                harness.OrganizationId, harness.ApSpecialistId, bankingDetailId, bankingVersion,
                "PAYMENT_EXECUTION", "corr-reveal", cancellationToken);
            Assert.Equal("00123456789012345678", revealed.AccountNumber);

            // Once the task is terminal the approver loses the reveal.
            await Assert.ThrowsAsync<DomainForbiddenException>(() => services.Banking.RevealAsync(
                harness.OrganizationId, harness.ApproverId, bankingDetailId, bankingVersion,
                "APPROVAL_VERIFICATION", "corr-reveal-4", cancellationToken));

            // The stored envelope cannot be rewritten: the append-only guard rejects the UPDATE, so
            // a ciphertext, nonce or tag can never be swapped in place (REQ-05, NFR-01).
            var rewrite = await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() =>
                context.Database.ExecuteSqlRawAsync(
                    "UPDATE [Supplier].[SupplierBankingVersions] SET [Tag] = 0x00 WHERE [BankingDetailId] = {0}",
                    [bankingDetailId],
                    cancellationToken));
            Assert.Contains("append-only", rewrite.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task The_append_only_triggers_reject_a_rewrite_of_an_approved_version()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await SupplierHarness.StartAsync(cancellationToken);
        var supplierId = await ApprovedSupplierAsync(harness, cancellationToken);

        await using var context = harness.CreateContext();
        var failure = await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() =>
            context.Database.ExecuteSqlRawAsync(
                "UPDATE [Supplier].[SupplierVersions] SET [LegalName] = 'Hacked' WHERE [SupplierId] = {0}",
                [supplierId],
                cancellationToken));
        Assert.Contains("append-only", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<Guid> ApprovedSupplierAsync(
        SupplierHarness harness,
        CancellationToken cancellationToken)
    {
        Guid supplierId;
        await using (var context = harness.CreateContext())
        {
            var services = harness.CreateServices(context);
            var outcome = await services.Governance.CreateAsync(
                harness.OrganizationId,
                harness.BuyerId,
                Content(harness),
                SupplierOperationalStatus.Active,
                "New supplier",
                $"create-{Guid.NewGuid():N}",
                "corr-create",
                cancellationToken);
            supplierId = outcome.Version.SupplierId;
            await services.Governance.SubmitAsync(
                harness.OrganizationId,
                harness.BuyerId,
                supplierId,
                outcome.Version.Version,
                "Submit for activation",
                $"submit-{Guid.NewGuid():N}",
                "corr-submit",
                cancellationToken);
        }

        await harness.DecideFirstPendingAsync(ApprovalDecisionAction.Approve, cancellationToken: cancellationToken);
        return supplierId;
    }

    private static SupplierVersionContent Content(
        SupplierHarness harness,
        string legalName = "ABC Tech SAC",
        string country = "PE",
        string? tradeName = null,
        SupplierBankingRef? bankingRef = null) => new(
        legalName,
        tradeName,
        country,
        "20123456789",
        [
            new SupplierAddress(
                Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"), "Headquarters", "Av. Siempre Viva 742",
                null, "Lima", "Lima", "15046", "PE")
        ],
        [
            new SupplierContact(
                Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"), "Ana Perez", "ventas@abc.test", null, null)
        ],
        new SupplierPaymentTerms("NET30", 30),
        ["PEN"],
        [new VersionedCodeRef("SPEND_CATEGORY", harness.SpendCategoryCode, harness.SpendCategoryVersion,
            harness.SpendCategoryDigest)],
        SupplierOperationalStatus.Draft,
        SupplierRiskStatus.Low,
        null,
        bankingRef is null ? [] : [bankingRef]);
}
