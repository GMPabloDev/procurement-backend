using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ProcureToPay.Application.Abstractions;
using ProcureToPay.Domain.Modules.Organization;
using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Domain.Modules.PurchaseRequests;
using ProcureToPay.Domain.SharedKernel;
using ProcureToPay.Infrastructure.Persistence;
using ProcureToPay.Infrastructure.Persistence.Organization;
using ProcureToPay.Infrastructure.Persistence.Policy;
using ProcureToPay.Infrastructure.Persistence.PurchaseRequests;
using Testcontainers.MsSql;

namespace ProcureToPay.IntegrationTests.PurchaseRequests;

/// <summary>
/// Real traversal of the SPEC 06 core (CA-03, CA-04): immutable request version → owner
/// attestation → persisted completeness manifest → real in-process provider → SPEC 02 engine.
/// Only the external reference owners are controlled adapters, exactly as the contract allows.
/// </summary>
public sealed class PurchaseRequestPolicyIntegrationTests
{
    [Fact]
    public async Task Attested_purchase_request_is_evaluated_by_policy_with_every_line()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await Harness.StartAsync(cancellationToken);
        var (requestId, version) = await harness.CreateRequestAsync(lineCount: 2, cancellationToken);

        var manifest = await harness.AttestAsync(requestId, version, cancellationToken);
        Assert.Equal(2, manifest.Lines.Count);
        Assert.Equal(requestId, manifest.RequestId);

        var bundle = await harness.EvaluateAsync(requestId, version, "pr-eval-1", cancellationToken);
        Assert.Equal(PolicyResult.Passed, bundle.Result);
        Assert.Equal(2, bundle.ScopeEvaluations.Count(evaluation => evaluation.Scope == PolicyScope.Line));
        Assert.Single(bundle.ScopeEvaluations, evaluation => evaluation.Scope == PolicyScope.Request);
        Assert.Equal(2, manifest.Lines.Count);
        Assert.Equal(requestId, bundle.Subject.Id);
        Assert.Equal(version, bundle.Subject.Version);
        Assert.False(string.IsNullOrWhiteSpace(bundle.ManifestDigest));
    }

    [Fact]
    public async Task Missing_owner_registration_fails_closed_without_manifest()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await Harness.StartAsync(cancellationToken);
        var (requestId, version) = await harness.CreateRequestAsync(lineCount: 1, cancellationToken);
        harness.RemoveOwner(PurchaseRequestAssertionType.ActiveInOrganization, PurchaseRequestReferenceType.CostCenter);

        await Assert.ThrowsAsync<PurchaseRequestDependencyUnavailableException>(() =>
            harness.AttestAsync(requestId, version, cancellationToken));
        Assert.False(await harness.HasManifestAsync(requestId, version, cancellationToken));
    }

    [Fact]
    public async Task Inactive_reference_is_rejected_without_manifest()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await Harness.StartAsync(cancellationToken);
        var (requestId, version) = await harness.CreateRequestAsync(lineCount: 1, cancellationToken);
        harness.DeactivateOwner(PurchaseRequestAssertionType.ActiveInOrganization, PurchaseRequestReferenceType.Department);

        await Assert.ThrowsAsync<DomainValidationException>(() =>
            harness.AttestAsync(requestId, version, cancellationToken));
        Assert.False(await harness.HasManifestAsync(requestId, version, cancellationToken));
    }

    [Fact]
    public async Task Provider_fails_closed_when_the_stored_manifest_is_corrupted()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await Harness.StartAsync(cancellationToken);
        var (requestId, version) = await harness.CreateRequestAsync(lineCount: 1, cancellationToken);
        await harness.AttestAsync(requestId, version, cancellationToken);
        await harness.CorruptManifestAsync(requestId, version, cancellationToken);

        await Assert.ThrowsAsync<PolicyDependencyUnavailableException>(() =>
            harness.EvaluateAsync(requestId, version, "pr-eval-corrupt", cancellationToken));
    }

    [Fact]
    public async Task Revision_reuses_unchanged_line_versions_and_advances_changed_ones()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await Harness.StartAsync(cancellationToken);
        var (requestId, version) = await harness.CreateRequestAsync(lineCount: 2, cancellationToken);
        var before = await harness.Service.ReadAsync(requestId, harness.OrganizationId, cancellationToken);
        Assert.NotNull(before);
        var first = before!.Versions.Single(item => item.Version == version);
        var retained = first.Lines[0];
        var changed = first.Lines[1];

        var revised = await harness.Service.ReviseAsync(
            new PurchaseRequestRevisionCommand(
                requestId,
                version,
                "Justification v2",
                $"revision-{Guid.NewGuid():N}",
                "Reduce scope",
                [new PurchaseRequestLineRef(retained.LineId, retained.LineVersion, retained.ContentDigest)],
                [new PurchaseRequestLineChange(
                    changed.LineId,
                    changed.LineVersion,
                    changed.ContentDigest,
                    Harness.LineContent(harness.OrganizationId, 50m))],
                [],
                []),
            harness.OrganizationId,
            harness.RequesterId,
            "corr-revision",
            DateTimeOffset.UtcNow,
            cancellationToken);

        var after = await harness.Service.ReadAsync(requestId, harness.OrganizationId, cancellationToken);
        Assert.NotNull(after);
        Assert.Equal(2, revised.Version);
        var successor = after!.Versions.Single(item => item.Version == 2);
        Assert.Contains(successor.Lines, line => line.LineId == retained.LineId && line.LineVersion == retained.LineVersion);
        Assert.Contains(successor.Lines, line => line.LineId == changed.LineId && line.LineVersion == changed.LineVersion + 1);
        var delta = after.Deltas.Single();
        Assert.Single(delta.Retained);
        Assert.Single(delta.Changed);
        Assert.Empty(delta.Added);
        Assert.Empty(delta.Removed);
    }

    private sealed class Harness : IAsyncDisposable
    {
        private MsSqlContainer container = null!;
        private readonly Dictionary<(string, PurchaseRequestReferenceType?), IPurchaseRequestReferenceOwner> owners = [];

        public Guid OrganizationId { get; private set; }
        public Guid LegalEntityId { get; private set; }
        public Guid DepartmentId { get; private set; }
        public Guid CostCenterId { get; private set; }
        public Guid RequesterId { get; private set; }
        public Guid RequestedForId { get; private set; }
        public string ConnectionString { get; private set; } = string.Empty;
        public ProcureToPayDbContext Context { get; private set; } = null!;
        public PurchaseRequestPersistenceService Service { get; private set; } = null!;
        private PurchaseRequestAttestationService attestation = null!;
        private PurchaseRequestPolicyFactProvider provider = null!;
        private PolicySetVersion policy = null!;
        private DateTimeOffset activationAt;

        public static async Task<Harness> StartAsync(CancellationToken cancellationToken)
        {
            var harness = new Harness
            {
                container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest")
                    .WithPassword("ProcureToPay_test_2026!")
                    .Build()
            };
            await harness.container.StartAsync(cancellationToken);
            harness.ConnectionString = harness.container.GetConnectionString();
            var options = new DbContextOptionsBuilder<ProcureToPayDbContext>()
                .UseSqlServer(harness.ConnectionString)
                .Options;
            harness.Context = new ProcureToPayDbContext(options);
            await harness.Context.Database.MigrateAsync(cancellationToken);
            harness.Seed();
            harness.RegisterOwners();
            harness.Service = new PurchaseRequestPersistenceService(
                harness.Context, NullLogger<PurchaseRequestPersistenceService>.Instance);
            harness.attestation = new PurchaseRequestAttestationService(
                harness.Context,
                new PurchaseRequestReferenceOwnerRegistry(harness.owners.Values),
                harness.Service,
                NullLogger<PurchaseRequestAttestationService>.Instance);
            harness.provider = new PurchaseRequestPolicyFactProvider(
                harness.Context, harness.Service, NullLogger<PurchaseRequestPolicyFactProvider>.Instance);
            await harness.ActivatePolicyAsync(cancellationToken);
            return harness;
        }

        public async Task<(Guid RequestId, int Version)> CreateRequestAsync(
            int lineCount,
            CancellationToken cancellationToken)
        {
            var drafts = new List<PurchaseRequestLineDraft>();
            for (var index = 0; index < lineCount; index++)
            {
                drafts.Add(new PurchaseRequestLineDraft(
                    $"line-{index}",
                    LineContent(OrganizationId, 100m + index)));
            }

            var creation = await Service.CreateAsync(
                new PurchaseRequestCreateCommand(
                    OrganizationId,
                    RequesterId,
                    new VersionedEntityRef("LEGAL_ENTITY", LegalEntityId, 1),
                    "Business justification",
                    $"create-{Guid.NewGuid():N}",
                    "Initial request",
                    drafts),
                RequesterId,
                "corr-create",
                DateTimeOffset.UtcNow,
                cancellationToken);
            return (creation.RequestId, creation.Version);
        }

        public Task<PurchaseRequestCompletenessManifest> AttestAsync(
            Guid requestId,
            int version,
            CancellationToken cancellationToken) =>
            attestation.EnsureAttestedAsync(
                requestId, version, OrganizationId, DateTimeOffset.UtcNow, cancellationToken);

        public Task<bool> HasManifestAsync(Guid requestId, int version, CancellationToken cancellationToken) =>
            Context.PurchaseRequestCompletenessManifests.AnyAsync(
                record => record.RequestId == requestId && record.RequestVersion == version, cancellationToken);

        public async Task CorruptManifestAsync(Guid requestId, int version, CancellationToken cancellationToken)
        {
            var record = await Context.PurchaseRequestCompletenessManifests
                .SingleAsync(
                    candidate => candidate.RequestId == requestId && candidate.RequestVersion == version,
                    cancellationToken);
            record.PolicyManifestDigest = new string('a', 64);
            await Context.SaveChangesAsync(cancellationToken);
            Context.ChangeTracker.Clear();
        }

        public async Task<PolicyEvaluationBundle> EvaluateAsync(
            Guid requestId,
            int version,
            string evaluationKey,
            CancellationToken cancellationToken)
        {
            if (DateTimeOffset.UtcNow < activationAt)
            {
                await Task.Delay(activationAt - DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(250), cancellationToken);
            }

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Policy:Workloads:0:Issuer"] = "internal://procure-to-pay",
                    ["Policy:Workloads:0:ClientId"] = PurchaseRequestCodes.ProviderId
                })
                .Build();
            var catalogs = new PolicyReferenceCatalogRegistry(
            [
                new AcceptAllCatalog("DEPARTMENT"),
                new AcceptAllCatalog("LEGAL_ENTITY"),
                new AcceptAllCatalog("COST_CENTER"),
                new AcceptAllCatalog("SPEND_CATEGORY")
            ]);
            var service = new PolicyEvaluationService(
                new PolicyPersistenceService(Context),
                new PolicyWorkloadAllowlist(configuration),
                new PolicyFactProviderRegistry([provider]),
                new PolicyExceptionVerifierRegistry([]),
                catalogs,
                NullLogger<PolicyEvaluationService>.Instance);
            var factRequest = new PolicyFactRequest(
                PurchaseRequestCodes.SubjectType,
                requestId,
                version,
                PurchaseRequestCodes.EvaluationOperation,
                DateTimeOffset.UtcNow,
                new PolicyWorkloadPrincipal("internal://procure-to-pay", PurchaseRequestCodes.ProviderId),
                "corr-evaluate")
            {
                OrganizationId = OrganizationId
            };
            return await service.EvaluateEnterprisePurchaseRequestAsync(
                factRequest, evaluationKey, cancellationToken);
        }

        public void RemoveOwner(
            PurchaseRequestAssertionType assertionType,
            PurchaseRequestReferenceType referenceType)
        {
            owners.Remove((PurchaseRequestCodes.Code(assertionType), referenceType));
            // The registry snapshots its registrations: rebuild it so the owner is truly absent.
            attestation = new PurchaseRequestAttestationService(
                Context,
                new PurchaseRequestReferenceOwnerRegistry(owners.Values),
                Service,
                NullLogger<PurchaseRequestAttestationService>.Instance);
        }

        public void DeactivateOwner(
            PurchaseRequestAssertionType assertionType,
            PurchaseRequestReferenceType referenceType)
        {
            if (owners.TryGetValue((PurchaseRequestCodes.Code(assertionType), referenceType), out var owner) &&
                owner is ControlledOwner controlled)
            {
                controlled.Active = false;
            }
        }

        private void Seed()
        {
            OrganizationId = Guid.NewGuid();
            LegalEntityId = Guid.NewGuid();
            DepartmentId = Guid.NewGuid();
            CostCenterId = Guid.NewGuid();
            RequesterId = Guid.NewGuid();
            RequestedForId = Guid.NewGuid();
            Context.Organizations.Add(new OrganizationRecord
            {
                Id = OrganizationId,
                Code = "PR-TEST",
                Name = "Purchase Request Test",
                BaseCurrency = "PEN",
                TimeZoneId = "America/Lima",
                FiscalYearStartMonth = 1,
                Version = 1
            });
            Context.LegalEntities.Add(new LegalEntityRecord
            {
                Id = LegalEntityId,
                OrganizationId = OrganizationId,
                Code = "COMPANY-PE",
                Name = "Company Peru",
                Status = (int)EntityStatus.Active,
                Version = 1
            });
            Context.Departments.Add(new DepartmentRecord
            {
                Id = DepartmentId,
                OrganizationId = OrganizationId,
                Code = "IT",
                Name = "Information Technology",
                Status = (int)EntityStatus.Active,
                Version = 1
            });
            Context.UserProfiles.Add(new UserProfileRecord
            {
                Id = RequesterId,
                OrganizationId = OrganizationId,
                Issuer = "https://issuer.test",
                Subject = "requester",
                Status = (int)UserProfileStatus.Active,
                Version = 1
            });
            Context.UserProfiles.Add(new UserProfileRecord
            {
                Id = RequestedForId,
                OrganizationId = OrganizationId,
                Issuer = "https://issuer.test",
                Subject = "requested-for",
                Status = (int)UserProfileStatus.Active,
                Version = 1
            });
            Context.SaveChanges();
        }

        private void RegisterOwners()
        {
            owners[(PurchaseRequestCodes.Code(PurchaseRequestAssertionType.ActiveInOrganization),
                PurchaseRequestReferenceType.Department)] = new ControlledOwner(
                PurchaseRequestAssertionType.ActiveInOrganization,
                PurchaseRequestReferenceType.Department,
                "organization-domain",
                "organization-db/v1");
            owners[(PurchaseRequestCodes.Code(PurchaseRequestAssertionType.ActiveInOrganization),
                PurchaseRequestReferenceType.LegalEntity)] = new ControlledOwner(
                PurchaseRequestAssertionType.ActiveInOrganization,
                PurchaseRequestReferenceType.LegalEntity,
                "organization-domain",
                "organization-db/v1");
            owners[(PurchaseRequestCodes.Code(PurchaseRequestAssertionType.ActiveInOrganization),
                PurchaseRequestReferenceType.User)] = new ControlledOwner(
                PurchaseRequestAssertionType.ActiveInOrganization,
                PurchaseRequestReferenceType.User,
                "organization-domain",
                "organization-db/v1");
            owners[(PurchaseRequestCodes.Code(PurchaseRequestAssertionType.ActiveInOrganization),
                PurchaseRequestReferenceType.CostCenter)] = new ControlledOwner(
                PurchaseRequestAssertionType.ActiveInOrganization,
                PurchaseRequestReferenceType.CostCenter,
                "cost-center-domain",
                "cost-center-db/v1");
            owners[(PurchaseRequestCodes.Code(PurchaseRequestAssertionType.CostCenterOwnedByDepartment),
                PurchaseRequestReferenceType.CostCenter)] = new ControlledOwner(
                PurchaseRequestAssertionType.CostCenterOwnedByDepartment,
                PurchaseRequestReferenceType.CostCenter,
                "cost-center-domain",
                "cost-center-db/v1");
            owners[(PurchaseRequestCodes.Code(PurchaseRequestAssertionType.ActiveInOrganization),
                PurchaseRequestReferenceType.SpendCategory)] = new ControlledOwner(
                PurchaseRequestAssertionType.ActiveInOrganization,
                PurchaseRequestReferenceType.SpendCategory,
                "spend-category-domain",
                "spend-category-db/v1");
        }

        private async Task ActivatePolicyAsync(CancellationToken cancellationToken)
        {
            policy = new PolicySetVersion(Guid.NewGuid(), OrganizationId, 1, [PolicyScope.Line, PolicyScope.Request]);
            policy.AddRule(new PolicyRule(
                "LINE_DEFAULT",
                PolicyScope.Line,
                [],
                [new PolicyEffect(PolicyEffectType.Allow, "LINE_ALLOW")],
                isFallback: true));
            policy.AddRule(new PolicyRule(
                "REQUEST_DEFAULT",
                PolicyScope.Request,
                [],
                [new PolicyEffect(PolicyEffectType.Allow, "REQUEST_ALLOW")],
                isFallback: true));
            policy.Publish(PolicyCanonicalizer.ComputePolicyDigest(policy));
            var persistence = new PolicyPersistenceService(Context);
            var actor = new PolicyActor("USER", Guid.NewGuid());
            var version = await persistence.AppendVersionAsync(
                policy,
                DateTimeOffset.UtcNow,
                actor,
                "Policy for purchase request integration",
                "corr-policy-append",
                cancellationToken);
            activationAt = DateTimeOffset.UtcNow.AddSeconds(1);
            await persistence.ActivateAsync(
                OrganizationId,
                version.Id,
                activationAt,
                actor,
                "Activate policy for purchase request integration",
                "corr-policy-activate",
                cancellationToken);
        }

        public static PurchaseRequestLineContent LineContent(Guid organizationId, decimal amount) =>
            new(
                amount,
                "PEN",
                amount,
                "PEN",
                2026,
                "GOOD",
                new VersionedCodeRef("SPEND_CATEGORY", "HARDWARE", 1, new string('d', 64)),
                new VersionedEntityRef("COST_CENTER", Guid.Parse("99999999-9999-9999-9999-999999999999"), 1),
                new VersionedEntityRef("DEPARTMENT", Guid.Parse("88888888-8888-8888-8888-888888888888"), 1),
                new VersionedEntityRef("DEPARTMENT", Guid.Parse("88888888-8888-8888-8888-888888888888"), 1),
                new VersionedEntityRef("USER", Guid.Parse("77777777-7777-7777-7777-777777777777"), 1),
                null,
                null,
                null,
                false,
                false,
                "NONE",
                "Need for the integration test",
                [],
                null);

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await container.DisposeAsync();
        }

        private sealed class AcceptAllCatalog(string catalogId) : IPolicyReferenceCatalog
        {
            public string CatalogId => catalogId;

            public string ContractVersion => "test/v1";

            public Task<bool> ExistsAsync(PolicyReferenceLookup reference, CancellationToken cancellationToken = default) =>
                Task.FromResult(true);
        }

        private sealed class ControlledOwner(
            PurchaseRequestAssertionType assertionType,
            PurchaseRequestReferenceType referenceType,
            string ownerId,
            string contractVersion) : IPurchaseRequestReferenceOwner
        {
            public bool Active { get; set; } = true;

            public string AssertionType => PurchaseRequestCodes.Code(assertionType);

            public PurchaseRequestReferenceType? ReferenceType => referenceType;

            public string OwnerId => ownerId;

            public string ContractVersion => contractVersion;

            public Task<PurchaseRequestVerificationResponse> VerifyAsync(
                PurchaseRequestVerificationRequest request,
                CancellationToken cancellationToken = default) =>
                Task.FromResult(new PurchaseRequestVerificationResponse(
                    Active,
                    request.AssertionType,
                    request.OrganizationId,
                    ContractVersion,
                    OwnerId,
                    request.SourceRef,
                    Active ? PurchaseRequestAssertionCodes.StatusActive : PurchaseRequestAssertionCodes.StatusInactive,
                    request.TargetRef,
                    request.VerifiedAt));
        }
    }
}
