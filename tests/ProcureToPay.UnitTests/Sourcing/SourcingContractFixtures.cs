using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Domain.Modules.Sourcing;
namespace ProcureToPay.UnitTests.Sourcing;

/// <summary>
/// One versioned contractual fixture of SPEC 10: the canonical document and the SHA-256 published
/// under <c>tests/ContractFixtures/Sourcing/v1/</c> (SPEC 10 Datos y contratos).
/// </summary>
internal sealed record ContractFixture(string FileName, string ContractVersion, string Document, string Digest);

/// <summary>
/// Deterministic builders of every golden document SPEC 10 requires. They construct the real domain
/// documents instead of hand-written JSON, so a schema or preimage change breaks the fixture test
/// instead of silently diverging.
/// </summary>
internal static class SourcingContractFixtures
{
    internal static readonly Guid OrganizationId = Guid.Parse("0f000000-0000-4000-8000-000000000001");
    internal static readonly Guid ProcessId = Guid.Parse("0f000000-0000-4000-8000-000000000002");
    internal static readonly Guid RequestId = Guid.Parse("0f000000-0000-4000-8000-000000000003");
    internal static readonly Guid RfqId = Guid.Parse("0f000000-0000-4000-8000-000000000004");
    internal static readonly Guid LineAId = Guid.Parse("1f000000-0000-4000-8000-000000000001");
    internal static readonly Guid LineBId = Guid.Parse("1f000000-0000-4000-8000-000000000002");
    internal static readonly Guid SupplierId = Guid.Parse("2f000000-0000-4000-8000-000000000001");
    internal static readonly Guid QuotationId = Guid.Parse("3f000000-0000-4000-8000-000000000001");
    internal static readonly Guid QuotationAttachmentId = Guid.Parse("4f000000-0000-4000-8000-000000000001");
    internal static readonly Guid FxId = Guid.Parse("5f000000-0000-4000-8000-000000000001");
    internal static readonly Guid ProposalId = Guid.Parse("6f000000-0000-4000-8000-000000000001");
    internal static readonly Guid EvaluationId = Guid.Parse("6f000000-0000-4000-8000-000000000002");
    internal static readonly Guid AwardId = Guid.Parse("6f000000-0000-4000-8000-000000000003");
    internal static readonly Guid RequestBundleId = Guid.Parse("7f000000-0000-4000-8000-000000000001");
    internal static readonly Guid PrerequisiteId = Guid.Parse("8f000000-0000-4000-8000-000000000001");
    internal static readonly Guid AttemptId = Guid.Parse("9f000000-0000-4000-8000-000000000001");
    internal static readonly Guid ActorId = Guid.Parse("af000000-0000-4000-8000-000000000001");
    internal static readonly Guid CaseId = Guid.Parse("bf000000-0000-4000-8000-000000000001");
    internal static readonly Guid ReviewerId = Guid.Parse("cf000000-0000-4000-8000-000000000001");
    internal static readonly Guid CatalogSnapshotId = Guid.Parse("df000000-0000-4000-8000-000000000001");

    private const string LineADigest = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string LineBDigest = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string RequestDigest = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    private const string FactsDigest = "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";
    private const string InputDigest = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
    private const string ManifestDigest = "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff";
    private const string PolicyContentDigest = "1111111111111111111111111111111111111111111111111111111111111111";
    private const string RequestResultDigest = "2222222222222222222222222222222222222222222222222222222222222222";
    private const string AttachmentDigest = "3333333333333333333333333333333333333333333333333333333333333333";
    private const string CaseDigest = "4444444444444444444444444444444444444444444444444444444444444444";
    private const string MaterialSnapshotDigest = "5555555555555555555555555555555555555555555555555555555555555555";
    private const string CatalogContentDigest = "6666666666666666666666666666666666666666666666666666666666666666";
    private const string CatalogSnapshotRefDigest = "7777777777777777777777777777777777777777777777777777777777777777";
    private const string SignalKey = "sourcing-domain:owner-attempt:9f000000000040008000000000000001";

    internal static readonly DateTimeOffset OpenedAt = DateTimeOffset.Parse("2026-09-20T15:00:00Z");
    internal static readonly DateTimeOffset ResponseDeadline = DateTimeOffset.Parse("2026-10-01T15:00:00Z");
    internal static readonly DateTimeOffset ReceivedAt = DateTimeOffset.Parse("2026-09-25T10:15:00Z");
    internal static readonly DateTimeOffset RegisteredAt = DateTimeOffset.Parse("2026-09-25T10:20:00Z");
    internal static readonly DateTimeOffset ReviewedAt = DateTimeOffset.Parse("2026-09-25T11:00:00Z");
    internal static readonly DateTimeOffset FxEffectiveAt = DateTimeOffset.Parse("2026-09-24T00:00:00Z");
    internal static readonly DateTimeOffset CheckedAt = DateTimeOffset.Parse("2026-10-02T12:30:00Z");
    internal static readonly DateTimeOffset PublishedAt = DateTimeOffset.Parse("2026-10-05T16:45:00Z");

    public static IReadOnlyList<ContractFixture> All()
    {
        var rfq = RfqDocument();
        var rfqDigest = PolicyCanonicalizer.Hash(rfq);
        var draft = RfqDraftDocument();
        var quotation = QuotationDocument(rfqDigest);
        var quotationDigest = PolicyCanonicalizer.Hash(quotation);
        var fx = FxSnapshotObject();
        var evaluation = Evaluation(
            rfqDigest, quotationDigest);
        var evaluationDigest = PolicyCanonicalizer.Hash(evaluation.CanonicalDocument());
        var requestBundle = Bundle();
        var rfqProposal = RfqProposal(requestBundle, quotationDigest, evaluationDigest);
        var rfqProposalDigest = PolicyCanonicalizer.Hash(rfqProposal);
        var catalogProposal = CatalogProposal(requestBundle);
        var catalogProposalDigest = PolicyCanonicalizer.Hash(catalogProposal);
        var rfqCandidate = RfqCandidate(fx);
        var catalogCandidate = CatalogCandidate();
        var rfqManifest = Manifest(
            [LineA(), LineB()],
            QuotationTerms(),
            evaluationDigest,
            SourcingCanonicalizer.RefSetDigest([QuotationRef(quotationDigest)]),
            SourcingCatalogSnapshotSet.ComputeDigest(OrganizationId, ProposalId, 1, []),
            rfqCandidate.ComputeDigest());
        var catalogManifest = Manifest(
            [LineA()],
            RfqTerms(),
            null,
            SourcingCanonicalizer.RefSetDigest([]),
            SourcingCatalogSnapshotSet.ComputeDigest(OrganizationId, ProposalId, 1, [Snapshot()]),
            catalogCandidate.ComputeDigest());
        var rfqAward = RfqAward(requestBundle, quotationDigest, evaluationDigest, rfqProposalDigest, fx);
        var catalogAward = CatalogAward(requestBundle, catalogProposalDigest);
        var quotationEvidence = QuotationEvidence(quotationDigest);
        var procurementEvidence = ProcurementEvidence(rfqAward.ComputeDigest());

        return
        [
            new ContractFixture("rfq-version.json", SourcingCodes.RfqVersionContract, rfq, rfqDigest),
            new ContractFixture("rfq-version-draft.json", SourcingCodes.RfqVersionContract, draft,
                PolicyCanonicalizer.Hash(draft)),
            new ContractFixture("quotation-version.json", SourcingCodes.QuotationVersionContract, quotation,
                quotationDigest),
            new ContractFixture("sourcing-fx-snapshot.json", SourcingCodes.FxSnapshotContract, fx.CanonicalDocument(),
                fx.Digest),
            new ContractFixture("quote-evaluation-version.json", SourcingCodes.QuoteEvaluationContract,
                evaluation.CanonicalDocument(), evaluationDigest),
            new ContractFixture("sourcing-proposal-version.json", SourcingCodes.ProposalVersionContract,
                rfqProposal, rfqProposalDigest),
            new ContractFixture("sourcing-proposal-version-catalog.json", SourcingCodes.ProposalVersionContract,
                catalogProposal, catalogProposalDigest),
            new ContractFixture("sourcing-completeness-manifest.json", SourcingCodes.CompletenessManifestContract,
                rfqManifest.CanonicalDocument(), rfqManifest.ComputeDigest()),
            new ContractFixture("sourcing-completeness-manifest-catalog.json",
                SourcingCodes.CompletenessManifestContract, catalogManifest.CanonicalDocument(),
                catalogManifest.ComputeDigest()),
            new ContractFixture("award-version.json", SourcingCodes.AwardVersionContract,
                rfqAward.CanonicalDocument(), rfqAward.ComputeDigest()),
            new ContractFixture("award-version-catalog.json", SourcingCodes.AwardVersionContract,
                catalogAward.CanonicalDocument(), catalogAward.ComputeDigest()),
            new ContractFixture("quotation-status-evidence.json", SourcingCodes.QuotationStatusEvidenceContract,
                quotationEvidence.CanonicalDocument(), quotationEvidence.ComputeDigest()),
            new ContractFixture("procurement-stage-evidence.json", SourcingCodes.ProcurementStageEvidenceContract,
                procurementEvidence.CanonicalDocument(), procurementEvidence.ComputeDigest())
        ];
    }

    private static CommercialTerms RfqTerms() => new(15, "EXW", "NET30", 365);

    private static CommercialTerms QuotationTerms() => new(20, "FOB", "NET45", 730);

    internal static EvaluationWeightSet Weights() => new(
    [
        new EvaluationWeight(SourcingEvaluationCriterion.Price, 40),
        new EvaluationWeight(SourcingEvaluationCriterion.DeliveryTime, 10),
        new EvaluationWeight(SourcingEvaluationCriterion.Warranty, 10),
        new EvaluationWeight(SourcingEvaluationCriterion.PaymentTerms, 20),
        new EvaluationWeight(SourcingEvaluationCriterion.TechnicalCompliance, 15),
        new EvaluationWeight(SourcingEvaluationCriterion.SupplierPerformance, 5)
    ]);

    private static SourcingContentRef LineA() => new(LineAId, 1, LineADigest);

    private static SourcingContentRef LineB() => new(LineBId, 1, LineBDigest);

    private static SourcingContentRef QuotationRef(string quotationDigest) =>
        new(QuotationId, 1, quotationDigest);

    private static SourcingAttachmentRef Attachment() => new(
        "application/pdf", QuotationAttachmentId, "quotation.pdf", 2048, AttachmentDigest, 1);

    private static string RfqDocument() => Rfq(RfqStatus.Open, OpenedAt).CanonicalDocument();

    private static string RfqDraftDocument() => Rfq(RfqStatus.Draft, null).CanonicalDocument();

    private static RfqVersionView Rfq(RfqStatus status, DateTimeOffset? openedAt) => new(
        RfqId,
        1,
        OrganizationId,
        ProcessId,
        RequestId,
        1,
        status,
        "PEN",
        RfqTerms(),
        Weights(),
        openedAt,
        ResponseDeadline,
        null,
        [
            new RfqLine(LineA(), 10m, "EA"),
            new RfqLine(LineB(), 5m, "EA")
        ],
        new string('0', 64),
        ActorId,
        OpenedAt,
        "Open the RFQ");

    private static string QuotationDocument(string rfqDigest) => new QuotationVersionView(
        QuotationId,
        1,
        new QuotationVersionContent(
            OrganizationId,
            new SourcingContentRef(RfqId, 1, rfqDigest),
            new SourcingEntityRef(SupplierId, 1),
            "USD",
            QuotationTerms(),
            QuotationTimeliness.OnTime,
            ReceivedAt,
            RegisteredAt,
            [
                new QuotationLine(
                    "10", "5", "123", LineA(), "10", "100", "18",
                    "Delivered with calibration certificate", "EA", "10"),
                new QuotationLine(
                    "0", "0", "55", LineB(), "5", "50", "5",
                    "Delivered in two weekly batches", "EA", "10")
            ],
            [Attachment()],
            QuotationReview.Decided(QuotationReviewStatus.Valid, [], null, ReviewerId, ReviewedAt)),
        new string('0', 64),
        null,
        ActorId,
        RegisteredAt,
        "Register the supplier answer").CanonicalDocument();

    private static SourcingFxSnapshot FxSnapshotObject() => new(
        FxId, 1, "PEN", "USD", 3.750000000000m, FxEffectiveAt,
        "BCRP reference 2026-09-24", Attachment());

    private static QuoteEvaluation Evaluation(
        string rfqDigest,
        string quotationDigest) => new(
        OrganizationId,
        new SourcingContentRef(RfqId, 1, rfqDigest),
        Weights(),
        [
            new EvaluationLineResult(
                LineA(),
                [QuotationScore(quotationDigest, LineA(), 461.25m, warrantyScore: 100, paymentScore: 90)],
                [new SourcingEntityRef(SupplierId, 1)]),
            new EvaluationLineResult(
                LineB(),
                [QuotationScore(quotationDigest, LineB(), 206.25m, warrantyScore: 50, paymentScore: 80)],
                [new SourcingEntityRef(SupplierId, 1)])
        ],
        [
            new SourcingFxSnapshot(
                FxId, 1, "PEN", "USD", 3.750000000000m, FxEffectiveAt,
                "BCRP reference 2026-09-24", Attachment())
        ],
        [new SourcingContentRef(QuotationId, 1, quotationDigest)]);

    private static QuotationScore QuotationScore(
        string quotationDigest,
        SourcingContentRef line,
        decimal normalizedGross,
        int warrantyScore,
        int paymentScore)
    {
        var criteria = new[]
        {
            new CriterionScore(SourcingEvaluationCriterion.Price, 100, 40, "SYSTEM", normalizedGross, null),
            new CriterionScore(SourcingEvaluationCriterion.DeliveryTime, 100, 10, "SYSTEM", 20m, null),
            new CriterionScore(SourcingEvaluationCriterion.Warranty, warrantyScore, 10, "SYSTEM", 730m, null),
            new CriterionScore(
                SourcingEvaluationCriterion.PaymentTerms, paymentScore, 20, "BUYER", 45m,
                "Net 45 accepted by the buyer"),
            new CriterionScore(
                SourcingEvaluationCriterion.TechnicalCompliance, 80, 15, "BUYER", null,
                "Complies with the technical annex"),
            new CriterionScore(
                SourcingEvaluationCriterion.SupplierPerformance, 75, 5, "SUPPLIER_MASTER", null, null)
        };
        return new QuotationScore(
            new SourcingContentRef(QuotationId, 1, quotationDigest),
            new SourcingEntityRef(SupplierId, 1),
            line,
            normalizedGross,
            criteria);
    }

    private static SourcingPolicyEvaluationRef Bundle() => new(
        3, RequestBundleId, FactsDigest, InputDigest, ManifestDigest, PolicyContentDigest, RequestResultDigest);

    private static string RfqProposal(
        SourcingPolicyEvaluationRef bundle,
        string quotationDigest,
        string evaluationDigest) => new SourcingProposalVersion(
        1,
        null,
        OrganizationId,
        new SourcingContentRef(RequestId, 1, RequestDigest),
        bundle,
        SourcingSelectionBasis.Rfq,
        new SourcingEntityRef(SupplierId, 1),
        [LineA(), LineB()],
        new SourcingContentRef(EvaluationId, 1, evaluationDigest),
        [QuotationRef(quotationDigest)],
        [],
        null,
        QuotationTerms(),
        RfqCandidate(FxSnapshotObject())).CanonicalDocument();

    private static AwardCandidate RfqCandidate(SourcingFxSnapshot fx) => new(
        new SourcingEntityRef(SupplierId, 1),
        "USD",
        "PEN",
        QuotationTerms(),
        [
            new AwardLine(LineA(), 10m, "EA", 10m, "USD", 123m, "PEN", 461.25m,
                new SourcingContentRef(FxId, 1, fx.Digest)),
            new AwardLine(LineB(), 5m, "EA", 10m, "USD", 55m, "PEN", 206.25m,
                new SourcingContentRef(FxId, 1, fx.Digest))
        ]);

    private static SourcingCatalogSnapshot Snapshot() =>
        new(new SourcingContentRef(CatalogSnapshotId, 1, CatalogSnapshotRefDigest), LineAId, 1,
            new SourcingEntityRef(SupplierId, 1), CatalogContentDigest);

    private static string CatalogProposal(SourcingPolicyEvaluationRef bundle) => new SourcingProposalVersion(
        1,
        null,
        OrganizationId,
        new SourcingContentRef(RequestId, 1, RequestDigest),
        bundle,
        SourcingSelectionBasis.ApprovedCatalog,
        new SourcingEntityRef(SupplierId, 1),
        [LineA()],
        null,
        [],
        [Snapshot()],
        null,
        RfqTerms(),
        CatalogCandidate()).CanonicalDocument();

    private static AwardCandidate CatalogCandidate() => new(
        new SourcingEntityRef(SupplierId, 1),
        "PEN",
        "PEN",
        RfqTerms(),
        [new AwardLine(LineA(), 10m, "EA", 10m, "PEN", 100m, "PEN", 100m, null)]);

    private static SourcingCompletenessManifest Manifest(
        IReadOnlyList<SourcingContentRef> coveredLines,
        CommercialTerms terms,
        string? evaluationDigest,
        string quotationSetDigest,
        string catalogSnapshotsDigest,
        string candidateDigest) => new(
        SourcingCodes.PolicyFactProviderId,
        SourcingCodes.PolicyFactProviderContractVersion,
        RequestId,
        1,
        ManifestDigest,
        RequestResultDigest,
        RequestBundleId,
        ProposalId,
        1,
        coveredLines,
        evaluationDigest,
        quotationSetDigest,
        catalogSnapshotsDigest,
        candidateDigest,
        PolicyCanonicalizer.Hash(PolicyCanonicalizer.SerializeCanonical(SourcingCanonicalizer.Terms(terms))));

    private static SourcingAwardVersion RfqAward(
        SourcingPolicyEvaluationRef bundle,
        string quotationDigest,
        string evaluationDigest,
        string proposalDigest,
        SourcingFxSnapshot fx) => new(
        1,
        null,
        OrganizationId,
        ActorId,
        PublishedAt,
        new SourcingEntityRef(ProcessId, 4),
        new SourcingContentRef(ProposalId, 1, proposalDigest),
        new SourcingContentRef(RequestId, 1, RequestDigest),
        SourcingSelectionBasis.Rfq,
        new SourcingEntityRef(SupplierId, 1),
        new SourcingContentRef(EvaluationId, 1, evaluationDigest),
        [QuotationRef(quotationDigest)],
        [],
        null,
        [new SourcingPolicyApprovalRef(bundle, CaseId, 3, CaseDigest)],
        QuotationTerms(),
        RfqCandidate(fx),
        "Award the selected supplier");

    private static SourcingAwardVersion CatalogAward(
        SourcingPolicyEvaluationRef bundle,
        string proposalDigest) => new(
        1,
        null,
        OrganizationId,
        ActorId,
        PublishedAt,
        new SourcingEntityRef(ProcessId, 4),
        new SourcingContentRef(ProposalId, 1, proposalDigest),
        new SourcingContentRef(RequestId, 1, RequestDigest),
        SourcingSelectionBasis.ApprovedCatalog,
        new SourcingEntityRef(SupplierId, 1),
        null,
        [],
        [Snapshot()],
        null,
        [new SourcingPolicyApprovalRef(bundle, CaseId, 3, CaseDigest)],
        RfqTerms(),
        CatalogCandidate(),
        "Award the approved catalogue supplier");

    private static QuotationStatusEvidence QuotationEvidence(string quotationDigest) => new(
        AttemptId,
        PrerequisiteId,
        OrganizationId,
        InputDigest,
        SignalKey,
        CheckedAt,
        new Dictionary<Guid, int> { [LineAId] = 2, [LineBId] = 1 },
        2,
        [QuotationRef(quotationDigest)],
        [Target(LineAId), Target(LineBId)],
        null);

    private static ProcurementStageEvidence ProcurementEvidence(string awardDigest) => new(
        AttemptId,
        PrerequisiteId,
        OrganizationId,
        InputDigest,
        SignalKey,
        CheckedAt,
        [new SourcingContentRef(AwardId, 1, awardDigest)],
        [new SourcingContentRef(RequestBundleId, 1, RequestResultDigest)],
        [Target(LineAId), Target(LineBId)]);

    private static ApprovalTargetView Target(Guid lineId) =>
        new(SourcingCodes.ApprovalTargetType, lineId, 1, MaterialSnapshotDigest);

    /// <summary>Writes every fixture and its SHA-256 sidecar under one directory.</summary>
    public static void WriteAll(string directory)
    {
        Directory.CreateDirectory(directory);
        foreach (var fixture in All())
        {
            File.WriteAllText(Path.Combine(directory, fixture.FileName), fixture.Document);
            File.WriteAllText(Path.Combine(directory, fixture.FileName + ".sha256"), fixture.Digest + "\n");
        }
    }

    /// <summary>Finds the repository root by walking up from the test binary.</summary>
    public static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ProcureToPay.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("The repository root could not be located.");
    }
}
