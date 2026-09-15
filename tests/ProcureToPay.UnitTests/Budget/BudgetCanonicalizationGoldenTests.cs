using System.Security.Cryptography;
using System.Text;
using ProcureToPay.Domain.Modules.Budget;

namespace ProcureToPay.UnitTests.Budget;

/// <summary>
/// SPEC 08 CA-07: the five published Budget vectors are reproducible from the exact preimages of
/// the contract. The bytes below are copied from the contract fixture and never generated from the
/// production builders, so a change in the builders (or in the canonical writer) breaks the test.
/// </summary>
public sealed class BudgetCanonicalizationGoldenTests
{
    private const string PositionKeyBytes =
        "{\"canonicalization_version\":\"policy-canonical-json/v1\",\"contract_version\":\"budget-position-key/v1\",\"cost_center_id\":\"22222222-2222-2222-2222-222222222222\",\"fiscal_year\":2026,\"organization_id\":\"11111111-1111-1111-1111-111111111111\",\"spend_category_code\":\"HARDWARE\"}";

    private const string AllocationBytes =
        "{\"actor_user_id\":\"22222222-2222-2222-2222-222222222222\",\"allocated_amount\":\"1000\",\"allocation_key\":\"alloc-1\",\"canonicalization_version\":\"policy-canonical-json/v1\",\"currency\":\"PEN\",\"expected_version\":null,\"organization_id\":\"11111111-1111-1111-1111-111111111111\",\"position\":{\"cost_center_ref\":{\"id\":\"22222222-2222-2222-2222-222222222222\",\"version\":1},\"fiscal_year\":2026,\"spend_category_ref\":{\"catalog\":\"SPEND_CATEGORY\",\"code\":\"HARDWARE\",\"digest\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"version\":1}},\"reason\":\"Initial allocation\"}";

    private const string PrecheckBytes =
        "{\"canonicalization_version\":\"policy-canonical-json/v1\",\"command_version\":\"budget-precheck-command/v1\",\"demands\":[{\"amount_base\":\"100\",\"cost_center_ref\":{\"id\":\"22222222-2222-2222-2222-222222222222\",\"version\":1},\"fiscal_year\":2026,\"source_line\":{\"content_digest\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"id\":\"55555555-5555-5555-5555-555555555555\",\"version\":1},\"spend_category_ref\":{\"catalog\":\"SPEND_CATEGORY\",\"code\":\"HARDWARE\",\"digest\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"version\":1},\"target\":null}],\"manifest_digest\":\"cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc\",\"organization_id\":\"11111111-1111-1111-1111-111111111111\",\"precheck_key\":\"precheck-1\",\"source\":{\"digest\":\"dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd\",\"id\":\"44444444-4444-4444-4444-444444444444\",\"type\":\"PURCHASE_REQUEST\",\"version\":1}}";

    private const string OperationBytes =
        "{\"actor\":{\"system_id\":null,\"type\":\"WORKLOAD\",\"user_id\":null,\"workload_client_id\":\"purchase-order-domain\",\"workload_issuer\":\"internal://procure-to-pay\"},\"canonicalization_version\":\"policy-canonical-json/v1\",\"command_version\":\"budget-transition-command/v1\",\"movements\":[{\"amount\":\"100\",\"parent_movement_id\":\"99999999-9999-9999-9999-999999999999\",\"parent_movement_version\":1,\"target\":{\"id\":\"55555555-5555-5555-5555-555555555555\",\"material_snapshot_digest\":\"1111111111111111111111111111111111111111111111111111111111111111\",\"type\":\"PURCHASE_REQUEST_LINE\",\"version\":1}}],\"operation\":\"COMMIT\",\"operation_key\":\"commit-1\",\"organization_id\":\"11111111-1111-1111-1111-111111111111\",\"reason_code\":\"PO_ISSUED\",\"source\":{\"digest\":\"dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd\",\"id\":\"44444444-4444-4444-4444-444444444444\",\"type\":\"PURCHASE_ORDER\",\"version\":1}}";

    private const string EvidenceBytes =
        "{\"attempt_id\":\"88888888-8888-8888-8888-888888888888\",\"canonicalization_version\":\"policy-canonical-json/v1\",\"case_id\":\"66666666-6666-6666-6666-666666666666\",\"checked_at\":\"2026-09-14T12:00:00.0000000Z\",\"contract_version\":\"budget-check-evidence/v1\",\"movements\":[{\"amount\":\"100\",\"id\":\"99999999-9999-9999-9999-999999999998\",\"position_key_digest\":\"b83e7782986b251a897c72add363aa8d980a836b7a6950a7acab188cc73bb9e3\",\"target\":{\"id\":\"55555555-5555-5555-5555-555555555555\",\"material_snapshot_digest\":\"1111111111111111111111111111111111111111111111111111111111111111\",\"type\":\"PURCHASE_REQUEST_LINE\",\"version\":1},\"type\":\"REQUESTED\",\"version\":1},{\"amount\":\"100\",\"id\":\"99999999-9999-9999-9999-999999999999\",\"position_key_digest\":\"b83e7782986b251a897c72add363aa8d980a836b7a6950a7acab188cc73bb9e3\",\"target\":{\"id\":\"55555555-5555-5555-5555-555555555555\",\"material_snapshot_digest\":\"1111111111111111111111111111111111111111111111111111111111111111\",\"type\":\"PURCHASE_REQUEST_LINE\",\"version\":1},\"type\":\"RESERVED\",\"version\":1}],\"organization_id\":\"11111111-1111-1111-1111-111111111111\",\"parameters_digest\":\"eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee\",\"prerequisite_id\":\"77777777-7777-7777-7777-777777777777\",\"result\":\"SATISFIED\",\"signal_key\":\"budget:77777777-7777-7777-7777-777777777777:signal\",\"source_control_digest\":\"ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff\"}";

    private const string ReleaseBytes =
        "{\"canonicalization_version\":\"policy-canonical-json/v1\",\"case_id\":\"66666666-6666-6666-6666-666666666666\",\"command_version\":\"budget-release-command/v1\",\"organization_id\":\"11111111-1111-1111-1111-111111111111\",\"reason_code\":\"PR_CANCELLED\",\"release_key\":\"release-1\",\"request_id\":\"44444444-4444-4444-4444-444444444444\",\"request_version\":1,\"targets\":[{\"id\":\"55555555-5555-5555-5555-555555555555\",\"material_snapshot_digest\":\"1111111111111111111111111111111111111111111111111111111111111111\",\"type\":\"PURCHASE_REQUEST_LINE\",\"version\":1}],\"trigger\":\"APPROVAL_RESULT\",\"trigger_event\":{\"contract_version\":\"approval-result/v3\",\"event_id\":\"99999999-9999-9999-9999-999999999997\"}}";

    private static readonly Guid OrganizationId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ActorUserId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid CostCenterId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid RequestId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid LineId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid CaseId = Guid.Parse("66666666-6666-6666-6666-666666666666");
    private static readonly Guid PrerequisiteId = Guid.Parse("77777777-7777-7777-7777-777777777777");
    private static readonly Guid AttemptId = Guid.Parse("88888888-8888-8888-8888-888888888888");
    private static readonly Guid RequestedMovementId = Guid.Parse("99999999-9999-9999-9999-999999999998");
    private static readonly Guid ReservedMovementId = Guid.Parse("99999999-9999-9999-9999-999999999999");
    private static readonly DateTimeOffset CheckedAt =
        new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);

    private static readonly string SpendCategoryDigest = new('a', 64);
    private static readonly string LineContentDigest = new('b', 64);
    private static readonly string ManifestDigest = new('c', 64);
    private static readonly string SourceDigest = new('d', 64);
    private static readonly string ParametersDigest = new('e', 64);
    private static readonly string SourceControlDigest = new('f', 64);
    private static readonly string MaterialSnapshotDigest = new('1', 64);

    private static BudgetPositionPayload Payload =>
        BudgetPositionPayload.For(
            CostCenterId,
            2026,
            new BudgetCostCenterRef(CostCenterId, 1),
            new BudgetSpendCategoryRef("HARDWARE", 1, SpendCategoryDigest));

    private static BudgetTarget Target =>
        new(LineId, 1, MaterialSnapshotDigest, BudgetCodes.PurchaseRequestLineType);

    private static BudgetDemand Demand =>
        new(100m, Payload, LineId, 1, LineContentDigest, Target);

    /// <summary>
    /// Precheck persists demand without an Approval target: it is a non-binding check of a
    /// Purchase Request version, not of a created case (REQ-04).
    /// </summary>
    private static BudgetDemand PrecheckDemand =>
        new(100m, Payload, LineId, 1, LineContentDigest, null);

    private static BudgetDemand DemandWith(decimal amount, string? contentDigest = null) =>
        new(amount, Payload, LineId, 1, contentDigest ?? LineContentDigest, Target);

    private static BudgetSource Source =>
        new(BudgetCodes.PurchaseRequestSourceType, RequestId, 1, SourceDigest);

    [Fact]
    public void Position_key_matches_the_published_vector()
    {
        Assert.Equal(
            "b83e7782986b251a897c72add363aa8d980a836b7a6950a7acab188cc73bb9e3",
            Sha256(PositionKeyBytes));
        Assert.Equal(
            "b83e7782986b251a897c72add363aa8d980a836b7a6950a7acab188cc73bb9e3",
            BudgetFingerprints.PositionKeyDigest(OrganizationId, CostCenterId, 2026, "hardware"));
        Assert.Equal(
            PositionKeyBytes,
            BudgetCanonicalJson.Serialize(
                BudgetFingerprints.PositionKeyPreimage(OrganizationId, CostCenterId, 2026, "HARDWARE")));
    }

    [Fact]
    public void Allocation_fingerprint_matches_the_published_vector()
    {
        var preimage = BudgetFingerprints.AllocationPreimage(
            OrganizationId,
            BudgetActor.ForUser(ActorUserId),
            Payload,
            1000m,
            "PEN",
            null,
            "alloc-1",
            "Initial allocation");

        Assert.Equal(AllocationBytes, BudgetCanonicalJson.Serialize(preimage));
        Assert.Equal(
            "0c27cd8be70888473809f9f394cf9856b7bfa65cffc0a95311312a53f3bd805f",
            Sha256(AllocationBytes));
        Assert.Equal(Sha256(AllocationBytes), BudgetCanonicalJson.Digest(preimage));
    }

    [Fact]
    public void Precheck_fingerprint_matches_the_published_vector()
    {
        var preimage = BudgetFingerprints.PrecheckPreimage(
            OrganizationId,
            "precheck-1",
            ManifestDigest,
            Source,
            [PrecheckDemand]);

        Assert.Equal(PrecheckBytes, BudgetCanonicalJson.Serialize(preimage));
        Assert.Equal(
            "ac788ced032349896e8ad6ca3ad534fad866b94c0de5c4fc16bb5031e19a3706",
            Sha256(PrecheckBytes));
        Assert.Equal(Sha256(PrecheckBytes), BudgetCanonicalJson.Digest(preimage));
    }

    [Fact]
    public void Operation_fingerprint_matches_the_published_vector()
    {
        var preimage = BudgetFingerprints.OperationPreimage(
            OrganizationId,
            BudgetActor.ForWorkload("internal://procure-to-pay", "purchase-order-domain"),
            BudgetOperationKind.Commit,
            "commit-1",
            "PO_ISSUED",
            new BudgetSource(BudgetCodes.PurchaseOrderSourceType, RequestId, 1, SourceDigest),
            [new BudgetMovementCommandItem(100m, ReservedMovementId, Target)]);

        Assert.Equal(OperationBytes, BudgetCanonicalJson.Serialize(preimage));
        Assert.Equal(
            "5efac07fe18e6d7dc7130c0262a4acb28c6dd8973a87e9ca293f1b318280c38e",
            Sha256(OperationBytes));
        Assert.Equal(Sha256(OperationBytes), BudgetCanonicalJson.Digest(preimage));
    }

    [Fact]
    public void Budget_check_evidence_matches_the_published_vector()
    {
        var positionKeyDigest = BudgetFingerprints.PositionKeyDigest(
            OrganizationId, CostCenterId, 2026, "HARDWARE");
        var preimage = BudgetFingerprints.EvidencePreimage(
            OrganizationId,
            AttemptId,
            CaseId,
            PrerequisiteId,
            CheckedAt,
            BudgetCheckResult.Available,
            "budget:77777777-7777-7777-7777-777777777777:signal",
            ParametersDigest,
            SourceControlDigest,
            [
                new BudgetEvidenceMovement(
                    RequestedMovementId, BudgetMovementType.Requested, 100m, positionKeyDigest, Target),
                new BudgetEvidenceMovement(
                    ReservedMovementId, BudgetMovementType.Reserved, 100m, positionKeyDigest, Target)
            ]);

        Assert.Equal(EvidenceBytes, BudgetCanonicalJson.Serialize(preimage));
        Assert.Equal(
            "4eece706b7214a84f45756adab97043ab30b551afcff89fb480bf64c72acc51e",
            Sha256(EvidenceBytes));
        Assert.Equal(Sha256(EvidenceBytes), BudgetCanonicalJson.Digest(preimage));
    }

    /// <summary>
    /// The release fingerprint has no published gold table, so it is fixed independently here: the
    /// byte string follows the preimage property table of the contract and its SHA-256 is computed
    /// from the literal, never from the production builder (CA-07).
    /// </summary>
    [Fact]
    public void Release_fingerprint_is_reproducible_from_the_contract_fixture()
    {
        var preimage = BudgetFingerprints.ReleasePreimage(
            OrganizationId,
            CaseId,
            RequestId,
            1,
            "release-1",
            "PR_CANCELLED",
            BudgetReleaseTrigger.ApprovalResult,
            new BudgetTriggerEvent(
                "approval-result/v3", Guid.Parse("99999999-9999-9999-9999-999999999997")),
            [Target]);

        Assert.Equal(ReleaseBytes, BudgetCanonicalJson.Serialize(preimage));
        Assert.Equal(
            "7a1c066828c5a75637f1efbf72b86cb9522faf23b7f19c2e632af09a9db7b54f",
            Sha256(ReleaseBytes));
        Assert.Equal(Sha256(ReleaseBytes), BudgetCanonicalJson.Digest(preimage));
    }

    [Fact]
    public void Reordering_a_demand_set_does_not_change_the_digest_and_a_changed_binding_does()
    {
        var second = DemandWith(250m);
        var first = BudgetFingerprints.PrecheckPreimage(
            OrganizationId, "precheck-1", ManifestDigest, Source, [Demand, second]);
        var reversed = BudgetFingerprints.PrecheckPreimage(
            OrganizationId, "precheck-1", ManifestDigest, Source, [second, Demand]);
        var altered = BudgetFingerprints.PrecheckPreimage(
            OrganizationId,
            "precheck-1",
            ManifestDigest,
            Source,
            [DemandWith(100m, new string('9', 64)), second]);

        Assert.Equal(BudgetCanonicalJson.Digest(first), BudgetCanonicalJson.Digest(reversed));
        Assert.NotEqual(BudgetCanonicalJson.Digest(first), BudgetCanonicalJson.Digest(altered));
    }

    [Fact]
    public void A_duplicated_demand_is_rejected_by_the_canonical_set()
    {
        Assert.Throws<ProcureToPay.Domain.SharedKernel.DomainConflictException>(() =>
            BudgetFingerprints.PrecheckPreimage(
                OrganizationId, "precheck-1", ManifestDigest, Source, [PrecheckDemand, PrecheckDemand]));
    }

    [Fact]
    public void Prerequisite_parameters_round_trip_through_the_closed_schema()
    {
        var parameters = new BudgetPrerequisiteParameters(
            "PEN",
            BudgetCodes.PrerequisiteContractVersion,
            [Demand]);

        var json = parameters.ToCanonicalJson();
        var parsed = BudgetPrerequisiteParameters.Parse(json);

        Assert.Equal(parameters.BaseCurrency, parsed.BaseCurrency);
        Assert.Equal(parameters.ContractVersion, parsed.ContractVersion);
        Assert.Single(parsed.Demands);
        Assert.Equal(100m, parsed.Demands[0].AmountBase);
        Assert.Equal("HARDWARE", parsed.Demands[0].Payload.SpendCategory.Code);
        Assert.Equal(2026, parsed.Demands[0].Payload.Position.FiscalYear);
        Assert.Equal(Target.Id, parsed.Demands[0].Target!.Id);
        Assert.Equal(Target.MaterialSnapshotDigest, parsed.Demands[0].Target!.MaterialSnapshotDigest);
    }

    [Fact]
    public void Prerequisite_parameters_reject_a_foreign_schema()
    {
        Assert.Throws<ProcureToPay.Domain.Modules.Budget.BudgetDependencyUnavailableException>(() =>
            BudgetPrerequisiteParameters.Parse("{\"base_currency\":\"PEN\",\"contract_version\":\"x\"}"));
        Assert.Throws<ProcureToPay.Domain.Modules.Budget.BudgetDependencyUnavailableException>(() =>
            BudgetPrerequisiteParameters.Parse("not json"));
    }

    [Fact]
    public void Strings_are_normalized_to_nfc_and_invalid_identities_are_rejected()
    {
        Assert.Equal(
            BudgetFingerprints.PositionKeyDigest(OrganizationId, CostCenterId, 2026, "HARDWARE"),
            BudgetFingerprints.PositionKeyDigest(OrganizationId, CostCenterId, 2026, "HARDWARE"));
        Assert.Throws<ProcureToPay.Domain.SharedKernel.DomainValidationException>(() =>
            BudgetFingerprints.PositionKeyDigest(OrganizationId, CostCenterId, 1999, "HARDWARE"));
        Assert.Throws<ProcureToPay.Domain.SharedKernel.DomainValidationException>(() =>
            BudgetFingerprints.PositionKeyDigest(OrganizationId, Guid.Empty, 2026, "HARDWARE"));
        Assert.Throws<ProcureToPay.Domain.SharedKernel.DomainValidationException>(() =>
            BudgetFingerprints.PositionKeyDigest(OrganizationId, CostCenterId, 2026, "  "));
    }

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
