using System.Collections.Immutable;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ProcureToPay.Application.Abstractions.Files;
using ProcureToPay.Infrastructure.Persistence;
using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Domain.Modules.Organization;
using ProcureToPay.Domain.Modules.PurchaseRequests;
using ProcureToPay.Domain.Modules.Sourcing;
using ProcureToPay.Domain.Modules.Suppliers;
using ProcureToPay.Infrastructure.Persistence.Approval;
using ProcureToPay.Infrastructure.Persistence.Organization;
using ProcureToPay.Infrastructure.Persistence.PurchaseRequests;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ProcureToPay.Infrastructure.Persistence.Policy;
using ProcureToPay.Infrastructure.Persistence.Sourcing;
using ProcureToPay.Infrastructure.Persistence.Suppliers;
using Testcontainers.MsSql;

namespace ProcureToPay.IntegrationTests.Sourcing;

/// <summary>
/// SQL Server harness of the Sourcing module (SPEC 10): real migrations, real persistence services
/// and the real object-storage contract replaced by an in-memory implementation, so the suite proves
/// the contractual behavior of REQ-01..REQ-04 against SQL Server instead of a product double.
/// </summary>
public sealed class SourcingHarness : IAsyncDisposable
{
    private MsSqlContainer container = null!;

    public Guid OrganizationId { get; } = Guid.Parse("aaaaaaaa-1111-1111-1111-111111111111");

    public Guid LegalEntityId { get; } = Guid.Parse("bbbbbbbb-2222-2222-2222-222222222222");

    public Guid BuyerId { get; } = Guid.Parse("cccccccc-3333-3333-3333-333333333333");

    public Guid AuditorId { get; } = Guid.Parse("dddddddd-4444-4444-4444-444444444444");

    public Guid RequesterId { get; } = Guid.Parse("eeeeeeee-5555-5555-5555-555555555555");

    public Guid RequestId { get; } = Guid.Parse("ffffffff-6666-6666-6666-666666666666");

    public Guid LineId { get; } = Guid.Parse("11111111-7777-7777-7777-777777777777");

    public Guid OtherLineId { get; } = Guid.Parse("22222222-8888-8888-8888-888888888888");

    public Guid SupplierId { get; } = Guid.Parse("33333333-9999-9999-9999-999999999999");

    public Guid SecondSupplierId { get; } = Guid.Parse("44444444-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    public string ConnectionString { get; private set; } = string.Empty;

    public InMemorySourcingStorage Storage { get; } = new();

    public static async Task<SourcingHarness> StartAsync(CancellationToken cancellationToken)
    {
        var container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest")
            .WithPassword("ProcureToPay_test_2026!")
            .Build();
        await container.StartAsync(cancellationToken);
        var harness = new SourcingHarness
        {
            container = container,
            ConnectionString = container.GetConnectionString()
        };
        await harness.SeedAsync(cancellationToken);
        return harness;
    }

    public ProcureToPayDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<ProcureToPayDbContext>()
            .UseSqlServer(ConnectionString)
            .Options);

    public IConfiguration Configuration => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:S3:TemporaryUrlLifetimeMinutes"] = "15",
            ["Sourcing:Waiver:ValidityDays"] = "7",
            // The sourcing domain is the only workload allowed to open a quotation waiver (REQ-05).
            ["Approval:Workloads:0:Issuer"] = SourcingWaiverService.Workload.Issuer,
            ["Approval:Workloads:0:ClientId"] = SourcingWaiverService.Workload.ClientId,
            // SPEC 10 REQ-13: the two owners resolve to the sourcing processor workload.
            ["Approval:OwnerWorkloads:0:AdapterId"] = SourcingCodes.QuotationStatusOwnerAdapterId,
            ["Approval:OwnerWorkloads:0:AdapterVersion"] = SourcingCodes.OwnerAdapterVersion,
            ["Approval:OwnerWorkloads:0:Issuer"] = SourcingWaiverService.Workload.Issuer,
            ["Approval:OwnerWorkloads:0:ClientId"] = SourcingCodes.QuotationStatusProcessorId,
            ["Approval:OwnerWorkloads:1:AdapterId"] = SourcingCodes.ProcurementStageOwnerAdapterId,
            ["Approval:OwnerWorkloads:1:AdapterVersion"] = SourcingCodes.OwnerAdapterVersion,
            ["Approval:OwnerWorkloads:1:Issuer"] = SourcingWaiverService.Workload.Issuer,
            ["Approval:OwnerWorkloads:1:ClientId"] = SourcingCodes.ProcurementStageProcessorId,
            ["Approval:Workloads:1:Issuer"] = SourcingWaiverService.Workload.Issuer,
            ["Approval:Workloads:1:ClientId"] = SourcingCodes.QuotationStatusProcessorId,
            ["Approval:Workloads:2:Issuer"] = SourcingWaiverService.Workload.Issuer,
            ["Approval:Workloads:2:ClientId"] = SourcingCodes.ProcurementStageProcessorId
        })
        .Build();

    /// <summary>
    /// SPEC 11 REQ-10: the process service records the takeover per line, so the harness builds it
    /// with the real per-line takeover service instead of the legacy request-wide row.
    /// </summary>
    public SourcingProcessService CreateProcessService(ProcureToPayDbContext context) =>
        new(context, null, new ProcureToPay.Infrastructure.Persistence.PurchaseOrders
            .PurchaseRequestLineTakeoverService(context));

    public SourcingQuotationService CreateQuotationService(ProcureToPayDbContext context) =>
        new(context, new ServiceCollection().AddSingleton<IFileStorage>(Storage).BuildServiceProvider(),
            Configuration);

    public SourcingEvaluationService CreateEvaluationService(ProcureToPayDbContext context) => new(context);

    public SourcingSelectionService CreateSelectionService(ProcureToPayDbContext context) => new(context);

    public SourcingProposalService CreateProposalService(ProcureToPayDbContext context) => new(context);

    public SourcingAwardService CreateAwardService(ProcureToPayDbContext context) =>
        new(context, new ProcureToPay.Infrastructure.Persistence.PurchaseOrders
            .PurchaseRequestLineTakeoverService(context));

    /// <summary>
    /// Catalogue route over the SPEC 09 boundary: the approved supplier fact owner is a boundary
    /// double (the Supplier suite proves the real one), so this suite proves the sourcing governance
    /// rules of REQ-06.
    /// </summary>
    public SourcingCatalogRouteService CreateCatalogRouteService(
        ProcureToPayDbContext context,
        bool preferred = true,
        string agreementStatus = "ACTIVE",
        bool catalogEntry = true) =>
        new(context, new CatalogFactOwner(preferred, agreementStatus, catalogEntry));

    /// <summary>
    /// Publishes a fresh request evaluation carrying one <c>REQUIRE_QUOTATIONS</c> control over the
    /// line, which is exactly the prerequisite the catalogue route must not bypass (REQ-06, DEC-03).
    /// </summary>
    public async Task SeedQuotationControlAsync(
        ProcureToPayDbContext context,
        Guid requestId,
        Guid lineId,
        CancellationToken cancellationToken)
    {
        var control = new ProcureToPay.Domain.Modules.Policy.PolicyGeneratedControl(
            "RFQ",
            ProcureToPay.Domain.Modules.Policy.PolicyEffectType.RequireQuotations,
            [ProcureToPay.Domain.Modules.Policy.PolicyScope.Line],
            [lineId],
            "PROCUREMENT",
            null,
            2,
            [],
            [],
            "Seeded quotation requirement");
        var bundle = new ProcureToPay.Domain.Modules.Policy.PolicyEvaluationBundle(
            Guid.NewGuid(),
            "sourcing-catalog-seed",
            new ProcureToPay.Domain.Modules.Policy.PolicySubjectReference(requestId, 1),
            DateTimeOffset.UtcNow,
            Digest('a'),
            Digest('b'),
            [],
            [control],
            ProcureToPay.Domain.Modules.Policy.PolicyResult.RequirementsGenerated,
            Digest('c'))
        {
            Operation = "REQUEST_EVALUATE",
            FactsDigest = Digest('d'),
            ManifestDigest = Digest('e')
        };
        await new ProcureToPay.Infrastructure.Persistence.Policy.PolicyPersistenceService(context)
            .AppendEvaluationAsync(
                bundle,
                new ProcureToPay.Infrastructure.Persistence.Policy.PolicyEvaluationCaller(
                    OrganizationId,
                    "internal://procure-to-pay",
                    "purchase-request-domain",
                    "REQUEST_EVALUATE",
                    "sourcing-catalog-seed",
                    PolicySetVersionId,
                    "seed-catalog-control"),
                cancellationToken);
    }

    /// <summary>Declares the same supplier on every line of the presented request version (REQ-06).</summary>
    public async Task SetRequestSupplierAsync(
        ProcureToPayDbContext context,
        Guid supplierId,
        int supplierVersion,
        CancellationToken cancellationToken)
    {
        var supplierJson =
            $"{{\"entity_type\":\"SUPPLIER\",\"id\":\"{supplierId:D}\",\"version\":{supplierVersion}}}";
        await context.Database.ExecuteSqlRawAsync(
            "UPDATE [PurchaseRequest].[PurchaseRequestLineVersions] SET [SupplierJson] = {0}",
            [supplierJson],
            cancellationToken);
    }

    private sealed class CatalogFactOwner(bool preferred, string agreementStatus, bool catalogEntry) :
        ProcureToPay.Application.Abstractions.IApprovedSupplierFactOwner
    {
        public string OwnerId => SourcingCodes.PolicyFactProviderId;

        public string ContractVersion => "approved-supplier-catalog-db/v1";

        public Task<ProcureToPay.Domain.Modules.Suppliers.SupplierPolicyFactSnapshot> ResolveAsync(
            ProcureToPay.Application.Abstractions.ApprovedSupplierFactQuery query,
            CancellationToken cancellationToken = default)
        {
            var entryId = Guid.NewGuid();
            return Task.FromResult(new ProcureToPay.Domain.Modules.Suppliers.SupplierPolicyFactSnapshot(
                query.OrganizationId,
                query.SourceLine,
                query.SupplierRef,
                query.SpendCategoryRef,
                query.ProductRef,
                catalogEntry
                    ? new ProcureToPay.Domain.Modules.Suppliers.ApprovedCatalogEntryRef(entryId, 1)
                    : null,
                catalogEntry ? new string('e', 64) : null,
                preferred,
                agreementStatus,
                DateTimeOffset.UtcNow));
        }
    }

    /// <summary>Real owner processor with the owner workload registry of the harness configuration.</summary>
    public SourcingPrerequisiteProcessor CreatePrerequisiteProcessor(ProcureToPayDbContext context)
    {
        var configuration = Configuration;
        var allowlist = new ApprovalWorkloadAllowlist(configuration);
        return new SourcingPrerequisiteProcessor(
            context,
            new ApprovalWorkflowService(
                context,
                allowlist,
                new ApprovalAssignmentEngine(
                    context, new OrganizationEligibilityService(context), new ApprovalScopeResolver(context)),
                NullLogger<ApprovalWorkflowService>.Instance),
            new ApprovalOwnerWorkloadRegistry(configuration, allowlist),
            new SourcingProcessorIdentity(),
            NullLogger<SourcingPrerequisiteProcessor>.Instance);
    }

    /// <summary>Registers one owner processor exactly once, or twice to prove the ambiguity rule.</summary>
    public async Task RegisterOwnerAsync(
        ProcureToPayDbContext context,
        string adapterId,
        CancellationToken cancellationToken)
    {
        if (await context.SourcingPrerequisiteProcessorRegistrations.AnyAsync(
                record => record.AdapterId == adapterId, cancellationToken))
        {
            // A duplicated registration is exactly the ambiguity REQ-13 must refuse to claim through.
            context.SourcingPrerequisiteProcessorRegistrations.Add(
                new SourcingPrerequisiteProcessorRegistrationRecord
                {
                    AdapterId = adapterId,
                    AdapterVersion = $"{SourcingCodes.OwnerAdapterVersion}-duplicate",
                    ProcessorId = SourcingCodes.QuotationStatusProcessorId,
                    WorkloadIssuer = SourcingWaiverService.Workload.Issuer,
                    WorkloadClientId = adapterId
                });
            await context.SaveChangesAsync(cancellationToken);
            return;
        }

        context.SourcingPrerequisiteProcessorRegistrations.Add(
            new SourcingPrerequisiteProcessorRegistrationRecord
            {
                AdapterId = adapterId,
                AdapterVersion = SourcingCodes.OwnerAdapterVersion,
                ProcessorId = adapterId == SourcingCodes.QuotationStatusOwnerAdapterId
                    ? SourcingCodes.QuotationStatusProcessorId
                    : SourcingCodes.ProcurementStageProcessorId,
                WorkloadIssuer = SourcingWaiverService.Workload.Issuer,
                WorkloadClientId = adapterId == SourcingCodes.QuotationStatusOwnerAdapterId
                    ? SourcingCodes.QuotationStatusProcessorId
                    : SourcingCodes.ProcurementStageProcessorId
            });
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<Guid> QuotationPrerequisiteIdAsync(CancellationToken cancellationToken) =>
        await CreateContext().ApprovalPrerequisites
            .AsNoTracking()
            .Where(record => record.OwnerAdapterId == SourcingCodes.QuotationStatusOwnerAdapterId)
            .Select(record => record.Id)
            .FirstAsync(cancellationToken);

    public async Task<Guid> ProcurementPrerequisiteIdAsync(CancellationToken cancellationToken) =>
        await CreateContext().ApprovalPrerequisites
            .AsNoTracking()
            .Where(record => record.OwnerAdapterId == SourcingCodes.ProcurementStageOwnerAdapterId)
            .Select(record => record.Id)
            .FirstAsync(cancellationToken);

    /// <summary>
    /// Publishes a successor supplier version and points the root at it, which is the real way the
    /// awarded version stops being current (REQ-11). Historical rows are never rewritten.
    /// </summary>
    public async Task AdvanceSupplierVersionAsync(
        ProcureToPayDbContext context,
        CancellationToken cancellationToken,
        SupplierOperationalStatus? status = null)
    {
        var previous = await context.SupplierVersions
            .AsNoTracking()
            .SingleAsync(record => record.SupplierId == SupplierId && record.Version == 1, cancellationToken);
        context.SupplierVersions.Add(new SupplierVersionRecord
        {
            SupplierId = SupplierId,
            Version = 2,
            OrganizationId = OrganizationId,
            PredecessorVersion = 1,
            LegalName = previous.LegalName,
            CountryCode = previous.CountryCode,
            TaxId = previous.TaxId,
            AddressesJson = previous.AddressesJson,
            ContactsJson = previous.ContactsJson,
            PaymentTermsJson = previous.PaymentTermsJson,
            SupportedCurrenciesJson = previous.SupportedCurrenciesJson,
            CategoriesSuppliedJson = previous.CategoriesSuppliedJson,
            PerformanceJson = previous.PerformanceJson,
            BankingRefsJson = previous.BankingRefsJson,
            Status = (int)(status ?? (SupplierOperationalStatus)previous.Status),
            RiskStatus = previous.RiskStatus,
            ContentDigest = Digest('7'),
            ActorUserId = BuyerId,
            OccurredAt = DateTimeOffset.UtcNow,
            Reason = "Successor version of the integration fixture"
        });
        var root = await context.Suppliers.SingleAsync(record => record.Id == SupplierId, cancellationToken);
        root.OperationalVersion = 2;
        await context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>A blocked successor becomes current, which is the eligibility break REQ-11 refuses.</summary>
    public async Task BlockSupplierAsync(
        ProcureToPayDbContext context,
        CancellationToken cancellationToken) =>
        await AdvanceSupplierVersionAsync(context, cancellationToken, SupplierOperationalStatus.Blocked);

    /// <summary>
    /// Seeds one persisted quotation waiver and its approval case, so the owner processor can be
    /// proven to consume (or reject) the verified reduction without the SPEC 05 workflow (REQ-05).
    /// </summary>
    public async Task<SourcingWaiverFactsRecord> BuildWaiverFactsAsync(
        ProcureToPayDbContext context,
        SourcingRfqOutcome rfq,
        Guid prerequisiteId,
        int to,
        int recordedQuotations,
        bool approved,
        CancellationToken cancellationToken)
    {
        var current = await context.RfqVersions
            .AsNoTracking()
            .SingleAsync(
                record => record.RfqId == rfq.RfqId && record.Version == rfq.Version, cancellationToken);
        var lines = SourcingSerialization.ReadRfqLines(current.LinesJson);
        // The waiver is bound to the real case of its prerequisite; creating a second case for the
        // same subject would make the owner resolution ambiguous.
        var prerequisite = await context.ApprovalPrerequisites
            .AsNoTracking()
            .SingleAsync(record => record.Id == prerequisiteId, cancellationToken);
        var caseId = prerequisite.CaseId;
        var prerequisiteTargets = ApprovalJsonPersistence.DeserializeTargets(prerequisite.TargetsJson)
            .Select(target => target.Id)
            .ToHashSet();
        if (approved)
        {
            var caseRecord = await context.ApprovalCases
                .SingleAsync(record => record.Id == caseId, cancellationToken);
            caseRecord.Status = (int)ApprovalCaseStatus.Completed;
        }

        var quotations = new SourcingQuotationQueries(context);
        var targets = new List<SourcingWaiverTarget>();
        foreach (var line in lines.Where(line => prerequisiteTargets.Contains(line.LineRef.Id)))
        {
            var currentQuotations = (await quotations.ListValidAsync(
                    OrganizationId, current.RfqId, line.LineRef.Id, cancellationToken))
                .Select(quote => new SourcingContentRef(quote.QuotationId, quote.Version, quote.ContentDigest))
                .ToArray();
            if (currentQuotations.Length == 0 && recordedQuotations > 0)
            {
                // A historical trace whose answers are no longer valid; only the negative scenario
                // builds this shape.
                currentQuotations = Enumerable.Range(0, recordedQuotations)
                    .Select(index => new SourcingContentRef(
                        Guid.Parse($"{index + 1:x8}-0000-4000-8000-000000000001"), 1, Digest('f')))
                    .ToArray();
            }

            targets.Add(new SourcingWaiverTarget(line.LineRef, currentQuotations));
        }
        var facts = new SourcingWaiverFacts(
            OrganizationId,
            current.ProcessId,
            new SourcingContentRef(current.RfqId, current.Version, current.ContentDigest),
            prerequisiteId,
            "QUOTES",
            Guid.NewGuid(),
            Digest('c'),
            Guid.NewGuid(),
            Digest('d'),
            Digest('e'),
            2,
            to,
            1,
            targets,
            DateTimeOffset.UtcNow);
        await SeedPolicyVerificationAsync(context, facts, approved, cancellationToken);
        return new SourcingWaiverFactsRecord
        {
            Id = Guid.NewGuid(),
            OrganizationId = OrganizationId,
            ProcessId = current.ProcessId,
            RfqId = current.RfqId,
            RfqVersion = current.Version,
            PrerequisiteId = prerequisiteId,
            RequirementKey = "QUOTES",
            BaseBundleId = facts.BaseBundleId,
            BaseResultDigest = facts.BaseResultDigest,
            PolicyVersionId = facts.PolicyVersionId,
            PolicyContentDigest = facts.PolicyContentDigest,
            ManifestDigest = facts.ManifestDigest,
            From = facts.From,
            To = facts.To,
            Floor = facts.Floor,
            TargetsJson = SourcingSerialization.WaiverTargets(facts.Targets),
            DocumentJson = facts.CanonicalDocument(),
            ContentDigest = facts.Digest,
            CommandKey = $"waiver-fixture-{caseId:N}",
            ApprovalCaseId = caseId,
            ActorUserId = BuyerId,
            OccurredAt = DateTimeOffset.UtcNow
        };
    }

    /// <summary>
    /// Seeds the persisted Policy verification of one waiver and the reduced reevaluation it applied:
    /// the owner only accepts a reduction that is current, vigente and bound to the same bundle,
    /// requirement, targets and floor (REQ-05).
    /// </summary>
    private async Task SeedPolicyVerificationAsync(
        ProcureToPayDbContext context,
        SourcingWaiverFacts facts,
        bool approved,
        CancellationToken cancellationToken)
    {
        var policySetVersionId = await context.PolicySetVersions
            .AsNoTracking()
            .Select(record => (Guid?)record.Id)
            .FirstOrDefaultAsync(cancellationToken) ?? Guid.NewGuid();
        if (!await context.PolicySetVersions.AnyAsync(record => record.Id == policySetVersionId, cancellationToken))
        {
            context.PolicySetVersions.Add(new PolicySetVersionRecord
            {
                Id = policySetVersionId,
                OrganizationId = OrganizationId,
                Sequence = 1,
                ScopesJson = "[\"LINE\"]",
                ContentJson = "{}",
                ContentDigest = Digest('1')
            });
            await context.SaveChangesAsync(cancellationToken);
        }

        if (!approved)
        {
            return;
        }

        var control = new ProcureToPay.Domain.Modules.Policy.PolicyGeneratedControl(
            facts.RequirementKey,
            ProcureToPay.Domain.Modules.Policy.PolicyEffectType.RequireQuotations,
            [ProcureToPay.Domain.Modules.Policy.PolicyScope.Line],
            facts.Targets.Select(target => target.LineRef.Id).ToImmutableHashSet(),
            "PROCUREMENT",
            null,
            facts.To,
            [],
            [],
            "Waiver reduction fixture");
        var subjectReferences = facts.Targets
            .Select(target => new ProcureToPay.Domain.Modules.Policy.PolicySubjectReference(
                target.LineRef.Id, target.LineRef.Version))
            .ToArray();
        var scope = new ProcureToPay.Domain.Modules.Policy.PolicyScopeEvaluation(
            ProcureToPay.Domain.Modules.Policy.PolicyScope.Line,
            subjectReferences.Select(reference => reference.Id).ToImmutableHashSet(),
            [facts.RequirementKey],
            [control],
            ProcureToPay.Domain.Modules.Policy.PolicyResult.RequirementsGenerated)
        {
            SubjectReferences = subjectReferences
        };
        var verificationId = Guid.NewGuid();
        var reduced = new ProcureToPay.Domain.Modules.Policy.PolicyEvaluationBundle(
            Guid.NewGuid(),
            $"waiver-reduced-{Guid.NewGuid():N}",
            new ProcureToPay.Domain.Modules.Policy.PolicySubjectReference(RequestId, 1),
            DateTimeOffset.UtcNow,
            Digest('1'),
            Digest('2'),
            [scope],
            [control],
            ProcureToPay.Domain.Modules.Policy.PolicyResult.RequirementsGenerated,
            Digest('3'))
        {
            Operation = "PURCHASE_REQUEST_WAIVER",
            PreviousBundleId = facts.BaseBundleId
        };
        await new ProcureToPay.Infrastructure.Persistence.Policy.PolicyPersistenceService(context)
            .AppendEvaluationAsync(
                reduced,
                new ProcureToPay.Infrastructure.Persistence.Policy.PolicyEvaluationCaller(
                    OrganizationId,
                    "QUOTATION_WAIVER",
                    "WORKFLOW",
                    "PURCHASE_REQUEST_WAIVER",
                    reduced.EvaluationKey,
                    policySetVersionId,
                    "waiver-fixture")
                {
                    SubjectType = "PURCHASE_REQUEST",
                    Cause = "QUOTATION_WAIVER",
                    ExceptionReferenceIds = [verificationId.ToString("D")],
                    ExceptionTargetRequirementKey = facts.RequirementKey,
                    ExceptionFrom = facts.From,
                    ExceptionTo = facts.To
                },
                cancellationToken);
        context.PolicyExceptionVerifications.Add(
            new ProcureToPay.Infrastructure.Persistence.Policy.PolicyExceptionVerificationRecord
            {
                Id = verificationId,
                EvaluationBundleId = facts.BaseBundleId,
                BaseBundleId = facts.BaseBundleId,
                WorkflowDecisionId = $"waiver-decision-{verificationId:N}",
                TargetRequirementKey = facts.RequirementKey,
                Binding = Digest('f'),
                Nonce = $"waiver-nonce-{verificationId:N}",
                EvidenceDigest = Digest('e'),
                ApproverId = BuyerId,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
                VerifierReference = "workflow://sourcing-fixture",
                SnapshotJson = System.Text.Json.JsonSerializer.Serialize(new
                {
                    request = new
                    {
                        baseBundleId = facts.BaseBundleId.ToString("D"),
                        targetRequirementKey = facts.RequirementKey,
                        from = facts.From,
                        to = facts.To,
                        floor = facts.Floor,
                        coveredLines = facts.Targets.Select(target => new
                        {
                            type = "PURCHASE_REQUEST_LINE",
                            id = target.LineRef.Id,
                            version = target.LineRef.Version,
                            materialSnapshotDigest = Digest('b')
                        }).ToArray()
                    }
                }),
                CreatedAt = DateTimeOffset.UtcNow
            });
        await context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Policy version the seeded request bundle points at; reused by the award fixtures.</summary>
    public Guid PolicySetVersionId { get; private set; }

    /// <summary>
    /// Seeds the sourcing policy evaluation of a proposal and its completed approval case, which is
    /// the persisted state a published award must find (REQ-11, REQ-12).
    /// </summary>
    public async Task SeedProposalApprovalAsync(
        ProcureToPayDbContext context,
        Guid proposalId,
        int proposalVersion,
        string proposalDigest,
        string manifestDigest,
        bool withCase,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var line = new ProcureToPay.Domain.Modules.Policy.PolicySubjectReference(LineId, 1);
        var decisionScope = ProcureToPay.Domain.Modules.Approval.DecisionScopeDescriptor
            .Create(
                OrganizationId,
                [
                    new ProcureToPay.Domain.Modules.Approval.DecisionScopeEntry(
                        ProcureToPay.Domain.Modules.Organization.ScopeDimension.Organization, null, null)
                ])
            .ToCanonicalJson();
        var control = new ProcureToPay.Domain.Modules.Policy.PolicyGeneratedControl(
            "SOURCING_APPROVAL",
            ProcureToPay.Domain.Modules.Policy.PolicyEffectType.RequireApproval,
            [ProcureToPay.Domain.Modules.Policy.PolicyScope.SourcingPo],
            [LineId],
            "PROCUREMENT",
            new ProcureToPay.Domain.Modules.Policy.PolicyApprovalDescriptor(
                ProcureToPay.Domain.Modules.Organization.SystemRole.ProcurementApprover,
                ProcureToPay.Domain.Modules.Organization.ApprovalAuthorityType.Procurement,
                new ProcureToPay.Domain.Modules.Policy.PolicyAuthorityLevelSnapshot(Guid.NewGuid(), 1, "PROCUREMENT_L1", 1),
                1000m,
                "PEN",
                decisionScope),
            null,
            [],
            [],
            "Seeded sourcing approval");
        var bundle = new ProcureToPay.Domain.Modules.Policy.PolicyEvaluationBundle(
            Guid.NewGuid(),
            "sourcing-proposal-evaluation",
            new ProcureToPay.Domain.Modules.Policy.PolicySubjectReference(proposalId, proposalVersion),
            now,
            Digest('1'),
            Digest('2'),
            [],
            [control],
            ProcureToPay.Domain.Modules.Policy.PolicyResult.RequirementsGenerated,
            Digest('3'))
        {
            Operation = "SOURCING_PO",
            FactsDigest = Digest('4'),
            // The sourcing evaluation publishes the manifest digest of the proposal it evaluated.
            ManifestDigest = manifestDigest
        };
        await new ProcureToPay.Infrastructure.Persistence.Policy.PolicyPersistenceService(context)
            .AppendEvaluationAsync(
                bundle,
                new ProcureToPay.Infrastructure.Persistence.Policy.PolicyEvaluationCaller(
                    OrganizationId,
                    "internal://procure-to-pay",
                    "sourcing-domain",
                    "SOURCING_PO",
                    $"sourcing-proposal-{proposalId:N}-v{proposalVersion}",
                    PolicySetVersionId,
                    "seed-sourcing-evaluation"),
                cancellationToken);
        if (!withCase)
        {
            await context.SaveChangesAsync(cancellationToken);
            return;
        }

        context.ApprovalCases.Add(new ProcureToPay.Infrastructure.Persistence.Approval.ApprovalCaseRecord
        {
            Id = Guid.NewGuid(),
            OrganizationId = OrganizationId,
            SubjectType = SourcingCodes.ApprovalSubjectType,
            SubjectId = proposalId,
            SubjectVersion = proposalVersion,
            Operation = SourcingCodes.ApprovalOperation,
            SourceSnapshotDigest = proposalDigest,
            WorkloadIssuer = "internal://procure-to-pay",
            WorkloadClientId = SourcingCodes.QuotationStatusProcessorId,
            SubmissionKey = $"sourcing-proposal-{proposalId:N}-v{proposalVersion}",
            SubmissionFingerprint = Digest('6'),
            OriginatorId = BuyerId,
            RequesterId = BuyerId,
            Status = (int)ProcureToPay.Domain.Modules.Approval.ApprovalCaseStatus.Completed,
            Version = 1,
            CreatedAt = now,
            CorrelationReference = "seed-sourcing-approval"
        });
        await context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Provider over the real composition: it is wired to the same PR fact provider the production
    /// registry resolves, so its envelope is built from attested facts instead of test doubles.
    /// </summary>
    public SourcingPolicyFactProvider CreateSourcingPolicyFactProvider(ProcureToPayDbContext context)
    {
        var configuration = Configuration;
        var requestProvider = new ProcureToPay.Infrastructure.Persistence.PurchaseRequests
            .PurchaseRequestPolicyFactProvider(
                context,
                new ProcureToPay.Infrastructure.Persistence.PurchaseRequests.PurchaseRequestPersistenceService(
                    context, NullLogger<
                        ProcureToPay.Infrastructure.Persistence.PurchaseRequests
                            .PurchaseRequestPersistenceService>.Instance),
                NullLogger<ProcureToPay.Infrastructure.Persistence.PurchaseRequests
                    .PurchaseRequestPolicyFactProvider>.Instance);
        return new SourcingPolicyFactProvider(
            context, new PolicyFactProviderRegistry([requestProvider]));
    }

    /// <summary>
    /// Seeds the persisted facts the proposal builder consumes from other modules: the attested
    /// completeness manifest of the request version and the newest Policy evaluation bundle it binds.
    /// </summary>
    public async Task SeedProposalFixturesAsync(
        ProcureToPayDbContext context,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        PolicySetVersionId = Guid.NewGuid();
        context.PolicySetVersions.Add(new ProcureToPay.Infrastructure.Persistence.Policy.PolicySetVersionRecord
        {
            Id = PolicySetVersionId,
            OrganizationId = OrganizationId,
            Sequence = 1,
            ScopesJson = "[\"LINE\"]",
            ContentJson = "{}",
            ContentDigest = Digest('1')
        });
        context.PurchaseRequestCompletenessManifests.Add(
            new ProcureToPay.Infrastructure.Persistence.PurchaseRequests.PurchaseRequestCompletenessManifestRecord
            {
                RequestId = RequestId,
                RequestVersion = 1,
                OrganizationId = OrganizationId,
                RequestContentDigest = Digest('2'),
                ReferenceAttestationDigest = Digest('3'),
                PolicyManifestDigest = Digest('4'),
                DomainAttestationDigest = Digest('5'),
                LinesJson = "[]",
                ContractVersion = "purchase-request-completeness-manifest/v2"
            });
        await context.SaveChangesAsync(cancellationToken);
        var requestBundle = new ProcureToPay.Domain.Modules.Policy.PolicyEvaluationBundle(
            Guid.NewGuid(),
            "sourcing-request-seed",
            new ProcureToPay.Domain.Modules.Policy.PolicySubjectReference(RequestId, 1),
            now,
            Digest('6'),
            Digest('7'),
            [],
            [],
            ProcureToPay.Domain.Modules.Policy.PolicyResult.Passed,
            Digest('8'))
        {
            Operation = "REQUEST_EVALUATE",
            FactsDigest = Digest('9'),
            ManifestDigest = Digest('4')
        };
        await new ProcureToPay.Infrastructure.Persistence.Policy.PolicyPersistenceService(context)
            .AppendEvaluationAsync(
                requestBundle,
                new ProcureToPay.Infrastructure.Persistence.Policy.PolicyEvaluationCaller(
                    OrganizationId,
                    "internal://procure-to-pay",
                    "purchase-request-domain",
                    "REQUEST_EVALUATE",
                    "sourcing-request-seed",
                    PolicySetVersionId,
                    "seed-request-evaluation"),
                cancellationToken);
    }

    /// <summary>
    /// Real SPEC 05 submission service over the in-memory configuration: the waiver suite proves the
    /// recalculation and the fail-closed behaviour, not a fake submitter.
    /// </summary>
    public SourcingWaiverService CreateWaiverService(ProcureToPayDbContext context) =>
        new(context, WaiverSubmissionService(context), Configuration);

    /// <summary>
    /// Real approval submission service with the sourcing proposal adapter registered exactly once,
    /// so the suite proves the descriptor, the targets and the fingerprint of REQ-11.
    /// </summary>
    public ApprovalSubmissionService CreateApprovalSubmissionService(
        ProcureToPayDbContext context,
        IReadOnlyList<Application.Abstractions.IApprovalSubmissionAdapter>? adapters = null)
    {
        var configuration = Configuration;
        var allowlist = new ApprovalWorkloadAllowlist(configuration);
        return new ApprovalSubmissionService(
            context,
            new ApprovalSubmissionAdapterRegistry(adapters ??
            [
                new SourcingProposalApprovalAdapter(
                    context,
                    new PolicyApprovalAdapter(context, PolicyApprovalAdapter.ContractVersionV4))
            ]),
            new ApprovalOwnerWorkloadRegistry(configuration, allowlist),
            allowlist,
            new ApprovalAssignmentEngine(
                context, new OrganizationEligibilityService(context), new ApprovalScopeResolver(context)),
            NullLogger<ApprovalSubmissionService>.Instance);
    }

    private PolicyExceptionSubmissionService WaiverSubmissionService(ProcureToPayDbContext context)
    {
        var configuration = Configuration;
        var allowlist = new ApprovalWorkloadAllowlist(configuration);
        var ownerWorkloads = new ApprovalOwnerWorkloadRegistry(configuration, allowlist);
        var assignmentEngine = new ApprovalAssignmentEngine(
            context, new OrganizationEligibilityService(context), new ApprovalScopeResolver(context));
        var submissions = new ApprovalSubmissionService(
            context, new ApprovalSubmissionAdapterRegistry([]), ownerWorkloads, allowlist, assignmentEngine,
            NullLogger<ApprovalSubmissionService>.Instance);
        return new PolicyExceptionSubmissionService(
            context, submissions, allowlist, NullLogger<PolicyExceptionSubmissionService>.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        await container.DisposeAsync();
    }

    /// <summary>
    /// Seeds the exact persisted facts the contract consumes: one current Purchase Request version
    /// with two lines, the current approval case of that version with its satisfied predecessor and
    /// the two PROCUREMENT-stage prerequisites, and two active suppliers.
    /// </summary>
    private async Task SeedAsync(CancellationToken cancellationToken)
    {
        await using var context = CreateContext();
        await context.Database.MigrateAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        context.Organizations.Add(new OrganizationRecord
        {
            Id = OrganizationId,
            Code = "SOURCING-TEST",
            Name = "Sourcing Test",
            BaseCurrency = "PEN",
            TimeZoneId = "America/Lima",
            FiscalYearStartMonth = 1,
            Version = 1
        });
        context.LegalEntities.Add(new LegalEntityRecord
        {
            Id = LegalEntityId,
            OrganizationId = OrganizationId,
            Code = "LE-1",
            Name = "Legal Entity",
            Status = (int)EntityStatus.Active,
            Version = 1
        });
        foreach (var (id, email) in new[]
                 {
                     (BuyerId, "buyer@test"), (AuditorId, "auditor@test"), (RequesterId, "requester@test")
                 })
        {
            context.UserProfiles.Add(new UserProfileRecord
            {
                Id = id,
                OrganizationId = OrganizationId,
                Issuer = "https://issuer.test",
                Subject = id.ToString("D"),
                Email = email,
                DisplayName = email,
                Status = (int)UserProfileStatus.Active,
                Version = 1
            });
        }

        context.RoleAssignments.AddRange(
            new RoleAssignmentRecord
            {
                Id = Guid.NewGuid(),
                UserProfileId = BuyerId,
                Role = (int)SystemRole.ProcurementBuyer,
                ScopeJson = "[{\"dimension\":\"ORGANIZATION\",\"reference\":null}]",
                AssignedAt = now,
                AssignedBy = BuyerId,
                Status = (int)AssignmentStatus.Active,
                Version = 1
            },
            new RoleAssignmentRecord
            {
                Id = Guid.NewGuid(),
                UserProfileId = AuditorId,
                Role = (int)SystemRole.Auditor,
                ScopeJson = "[{\"dimension\":\"ORGANIZATION\",\"reference\":null}]",
                AssignedAt = now,
                AssignedBy = BuyerId,
                Status = (int)AssignmentStatus.Active,
                Version = 1
            });
        context.PurchaseRequests.Add(new PurchaseRequestRecord
        {
            Id = RequestId,
            OrganizationId = OrganizationId,
            RequesterId = RequesterId,
            LegalEntityId = LegalEntityId,
            LegalEntityVersion = 1,
            CurrentVersion = 1,
            Status = (int)PurchaseRequestStatus.InApproval,
            CreatedAt = now,
            UpdatedAt = now
        });
        context.PurchaseRequestVersions.Add(new PurchaseRequestVersionRecord
        {
            RequestId = RequestId,
            Version = 1,
            OrganizationId = OrganizationId,
            RequesterId = RequesterId,
            LegalEntityId = LegalEntityId,
            LegalEntityVersion = 1,
            BusinessJustification = "Sourcing integration seed",
            ContentDigest = Digest('1'),
            ActorUserId = RequesterId,
            CreatedAt = now
        });
        foreach (var (lineId, digest) in new[] { (LineId, Digest('2')), (OtherLineId, Digest('3')) })
        {
            context.PurchaseRequestLineVersions.Add(new PurchaseRequestLineVersionRecord
            {
                LineId = lineId,
                LineVersion = 1,
                RequestId = RequestId,
                OrganizationId = OrganizationId,
                EstimatedGrossAmount = 1_000m,
                TransactionCurrency = "PEN",
                BaseAmount = 1_000m,
                BaseCurrency = "PEN",
                FiscalYear = now.Year,
                PurchaseType = "GOOD",
                // The persisted shapes come from the real serializer, so the fixtures cannot drift.
                SpendCategoryJson = ProcureToPay.Infrastructure.Persistence.PurchaseRequests
                    .PurchaseRequestSerialization.CodeRef(new ProcureToPay.Domain.Modules.Policy.VersionedCodeRef(
                        "SPEND_CATEGORY", "HARDWARE", 1, Digest('4'))),
                // The line carries the cost center as a versioned entity reference (REQ-07), not as
                // a catalogued code.
                CostCenterJson = ProcureToPay.Infrastructure.Persistence.PurchaseRequests
                    .PurchaseRequestSerialization.EntityRef(
                        new ProcureToPay.Domain.Modules.Policy.VersionedEntityRef(
                            "COST_CENTER", Guid.Parse("66666666-bbbb-bbbb-bbbb-bbbbbbbbbbbb"), 1)),
                CostCenterDepartmentJson = ProcureToPay.Infrastructure.Persistence.PurchaseRequests
                    .PurchaseRequestSerialization.EntityRef(
                        new ProcureToPay.Domain.Modules.Policy.VersionedEntityRef(
                            "DEPARTMENT", Guid.Parse("55555555-bbbb-bbbb-bbbb-bbbbbbbbbbbb"), 1)),
                BeneficiaryDepartmentJson = ProcureToPay.Infrastructure.Persistence.PurchaseRequests
                    .PurchaseRequestSerialization.EntityRef(
                        new ProcureToPay.Domain.Modules.Policy.VersionedEntityRef(
                            "DEPARTMENT", Guid.Parse("55555555-bbbb-bbbb-bbbb-bbbbbbbbbbbb"), 1)),
                RequestedForUserJson = ProcureToPay.Infrastructure.Persistence.PurchaseRequests
                    .PurchaseRequestSerialization.EntityRef(
                        new ProcureToPay.Domain.Modules.Policy.VersionedEntityRef("USER", RequesterId, 1)),
                AgreementStatus = "NONE",
                NeedSummary = "Sourcing integration seed",
                RiskAnswersJson = "[]",
                ContentDigest = digest,
                ActorUserId = RequesterId,
                CreatedAt = now
            });
            context.PurchaseRequestVersionLines.Add(new PurchaseRequestVersionLineRecord
            {
                RequestId = RequestId,
                RequestVersion = 1,
                LineId = lineId,
                LineVersion = 1,
                ContentDigest = digest
            });
        }

        var caseId = Guid.NewGuid();
        context.ApprovalCases.Add(new ApprovalCaseRecord
        {
            Id = caseId,
            OrganizationId = OrganizationId,
            SubjectType = "PURCHASE_REQUEST",
            SubjectId = RequestId,
            SubjectVersion = 1,
            Operation = "SUBMIT_PURCHASE_REQUEST",
            SourceSnapshotDigest = Digest('6'),
            WorkloadIssuer = "internal://procure-to-pay",
            WorkloadClientId = "purchase-request-domain",
            SubmissionKey = "seed-submission",
            SubmissionFingerprint = Digest('7'),
            OriginatorId = RequesterId,
            RequesterId = RequesterId,
            Status = (int)ApprovalCaseStatus.Open,
            Version = 1,
            CreatedAt = now,
            CorrelationReference = "seed-case"
        });
        context.ApprovalPrerequisites.AddRange(
            Prerequisite(caseId, "BUDGET", "budget-check-owner", PrerequisiteStatus.Satisfied, now),
            Prerequisite(caseId, "QUOTES", "quotation-status-owner", PrerequisiteStatus.Waiting, now),
            Prerequisite(caseId, "PROC", "procurement-stage-owner", PrerequisiteStatus.Waiting, now));
        foreach (var (supplierId, digest) in new[] { (SupplierId, Digest('8')), (SecondSupplierId, Digest('e')) })
        {
            var identityId = Guid.NewGuid();
            context.SupplierFiscalIdentities.Add(new SupplierFiscalIdentityRecord
            {
                Id = identityId,
                OrganizationId = OrganizationId,
                SupplierId = supplierId,
                CountryCode = "PE",
                TaxIdKey = $"RUC{supplierId:N}",
                TaxId = $"RUC{supplierId:N}",
                IdentityKeyDigest = digest
            });
            context.Suppliers.Add(new SupplierRecord
            {
                Id = supplierId,
                OrganizationId = OrganizationId,
                FiscalIdentityId = identityId,
                OperationalVersion = 1
            });
            context.SupplierVersions.Add(new SupplierVersionRecord
            {
                SupplierId = supplierId,
                Version = 1,
                OrganizationId = OrganizationId,
                LegalName = $"Supplier {supplierId:N}",
                CountryCode = "PE",
                TaxId = $"RUC{supplierId:N}",
                AddressesJson = "[]",
                ContactsJson = "[]",
                PaymentTermsJson = "{\"code\":\"NET30\",\"net_days\":30}",
                SupportedCurrenciesJson = "[\"PEN\"]",
                CategoriesSuppliedJson = "[]",
                PerformanceJson = "null",
                BankingRefsJson = "[]",
                Status = (int)SupplierOperationalStatus.Active,
                RiskStatus = (int)SupplierRiskStatus.Low,
                ContentDigest = Digest('9'),
                ActorUserId = BuyerId,
                OccurredAt = now,
                Reason = "Seed"
            });
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    private ApprovalPrerequisiteRecord Prerequisite(
        Guid caseId,
        string key,
        string owner,
        PrerequisiteStatus status,
        DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        CaseId = caseId,
        OrganizationId = OrganizationId,
        Key = key,
        OwnerAdapterId = owner,
        OwnerAdapterVersion = "v1",
        OwnerWorkloadIssuer = SourcingWaiverService.Workload.Issuer,
        // The persisted owner workload is the sourcing processor that owns both prerequisites.
        OwnerWorkloadClientId = SourcingCodes.QuotationStatusProcessorId,
        SourceControlType = key,
        SourceControlDigest = Digest('a'),
        ParametersJson = "{\"minimum_allowed_quotations\":null,\"minimum_quotations\":2}",
        TargetsJson = "[{\"id\":\"" + LineId + "\",\"type\":\"PURCHASE_REQUEST_LINE\",\"version\":1," +
                      "\"materialSnapshotDigest\":\"" + Digest('b') + "\"}]",
        Status = (int)status,
        Version = 1,
        ResolvedAt = status == PrerequisiteStatus.Satisfied ? now : null
    };

    private static string Digest(char value) => new(value, 64);
}

/// <summary>In-memory agreement storage of the sourcing suites (REQ-03).</summary>
public sealed class InMemorySourcingStorage : IFileStorage
{
    public Dictionary<string, byte[]> Objects { get; } = new(StringComparer.Ordinal);

    public bool Available { get; set; } = true;

    public async Task UploadAsync(FileUploadRequest request, CancellationToken cancellationToken = default)
    {
        using var buffer = new MemoryStream();
        await request.Content.CopyToAsync(buffer, cancellationToken);
        Objects[request.ObjectKey] = buffer.ToArray();
    }

    public Uri GenerateTemporaryDownloadUrl(string objectKey) =>
        new($"https://quotes.test/{Uri.EscapeDataString(objectKey)}");

    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Available);
}
