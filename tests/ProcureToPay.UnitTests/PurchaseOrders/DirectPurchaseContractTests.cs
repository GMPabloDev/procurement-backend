using ProcureToPay.Domain.Modules.PurchaseOrders;
using ProcureToPay.Domain.Modules.Sourcing;
using ProcureToPay.Domain.Modules.Suppliers;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.UnitTests.PurchaseOrders;

/// <summary>
/// Contract tests of the Direct Purchase domain (SPEC 11 CA-07): the authorization publishes exactly
/// its documented properties, the fingerprint binds every server-derived evidence, and the terms and
/// responsibilities must restate the covered lines.
/// </summary>
public sealed class DirectPurchaseContractTests
{
    private static readonly Guid OrganizationId = Guid.Parse("aaaaaaaa-1111-1111-1111-111111111111");
    private static readonly Guid RequestId = Guid.Parse("11111111-7777-7777-7777-777777777777");
    private static readonly Guid CaseId = Guid.Parse("bbbbbbbb-2222-2222-2222-222222222222");
    private static readonly Guid EvidenceId = Guid.Parse("cccccccc-3333-3333-3333-333333333333");
    private static readonly Guid SupplierId = Guid.Parse("dddddddd-4444-4444-4444-444444444444");
    private static readonly Guid LineId = Guid.Parse("33333333-9999-9999-9999-999999999999");
    private static readonly Guid SecondLineId = Guid.Parse("44444444-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid UserId = Guid.Parse("55555555-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid AuthorityId = Guid.Parse("eeeeeeee-5555-5555-5555-555555555555");
    private static readonly DateTimeOffset OccurredAt = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_document_publishes_exactly_its_contract_properties_and_is_reproducible()
    {
        var authorization = Authorization([Line(LineId), Line(SecondLineId)]);

        using var document = System.Text.Json.JsonDocument.Parse(authorization.CanonicalDocument());
        var properties = document.RootElement.EnumerateObject().Select(property => property.Name).ToArray();

        Assert.Equal(
            [
                "acceptance_responsibilities", "authorization_id", "authorization_key", "authorized_at",
                "authorized_by_user_id", "canonicalization_version", "contract_version", "covered_lines",
                "document_evidence_refs", "expected_request_version", "fingerprint", "maximum_base_amount",
                "maximum_source_amount", "ordering_evidence_ref", "organization_id", "policy_bundle_ref",
                "request_approval_case_ref", "request_ref", "state", "supplier_ref", "terms_snapshot", "version"
            ],
            properties);
        Assert.Equal(
            authorization.Digest,
            PurchaseOrderCanonicalizer.Hash(authorization.CanonicalDocument()));
        Assert.Equal(DirectPurchaseState.Authorized, authorization.State);
    }

    [Fact]
    public void A_fingerprint_binds_the_server_derived_evidence_and_ignores_permutation()
    {
        var first = Fingerprint(450m, [Line(LineId), Line(SecondLineId)]);
        var permuted = Fingerprint(450m, [Line(SecondLineId), Line(LineId)]);
        var differentAmount = Fingerprint(451m, [Line(LineId), Line(SecondLineId)]);

        Assert.Equal(first, permuted);
        Assert.NotEqual(first, differentAmount);
    }

    [Fact]
    public void A_cancelled_successor_keeps_its_binding_and_advances_the_version()
    {
        var authorization = Authorization([Line(LineId)]);

        var cancelled = authorization.With(DirectPurchaseState.Cancelled, UserId, OccurredAt.AddMinutes(5));

        Assert.Equal(2, cancelled.Version);
        Assert.Equal(1, cancelled.PredecessorVersion);
        Assert.Equal(DirectPurchaseState.Cancelled, cancelled.State);
        Assert.Equal(authorization.Digest != cancelled.Digest, true);
        Assert.Equal(authorization.Fingerprint, cancelled.Fingerprint);
        Assert.Equal(authorization.MaximumSourceAmount, cancelled.MaximumSourceAmount);
        Assert.Equal(authorization.SupplierRef.Id, cancelled.SupplierRef.Id);
    }

    [Fact]
    public void An_authorization_without_covered_lines_is_rejected()
    {
        Assert.Throws<DomainValidationException>(() => Authorization([]));
    }

    [Fact]
    public void The_terms_must_be_a_direct_purchase_snapshot_of_the_supplier()
    {
        var awardTerms = VendorTermsSnapshot.ForAward(
            new SupplierContentSnapshot(
                SupplierId, 1, Digest('b'), new SupplierPaymentTerms("NET30", 30), ["PEN"]),
            new CommercialTerms(10, "EXW", "NET30", 365),
            Ref(AuthorityId, 1, Digest('c')),
            PolicyBundle(),
            RequestRef(),
            [],
            "PEN",
            OccurredAt);

        Assert.Throws<DomainValidationException>(() => Authorization([Line(LineId)], terms: awardTerms));
    }

    [Fact]
    public void A_responsibility_outside_the_covered_lines_is_rejected()
    {
        var foreign = new AcceptanceResponsibility(
            PurchaseOrderCodes.ResponsibilityGoodsReceipt,
            Ref(SecondLineId, 1, Digest('f')),
            new PurchaseOrderEntityRef(UserId, 1),
            new PurchaseOrderEntityRef(UserId, 1),
            UserId,
            AcceptanceResponsibilityBuilder.ReasonRequestedFor,
            OccurredAt,
            1);

        Assert.Throws<DomainValidationException>(() => Authorization(
            [Line(LineId)], responsibilities: [Responsibility(LineId), foreign]));
    }

    private static DirectPurchaseAuthorization Authorization(
        IReadOnlyList<PurchaseOrderContentRef> lines,
        VendorTermsSnapshot? terms = null,
        IReadOnlyList<AcceptanceResponsibility>? responsibilities = null)
    {
        var supplier = SupplierContent();
        return new DirectPurchaseAuthorization(
            Guid.NewGuid(),
            1,
            null,
            OrganizationId,
            "dp-key-1",
            Digest('a'),
            1,
            RequestRef(),
            Ref(CaseId, 2, Digest('d')),
            Ref(EvidenceId, 1, Digest('e')),
            PolicyBundle(),
            new PurchaseOrderEntityRef(SupplierId, 1),
            lines,
            450m,
            "PEN",
            450m,
            terms ?? VendorTermsSnapshot.ForDirectPurchase(
                supplier, RequestRef(), PolicyBundle(), "PEN", OccurredAt),
            responsibilities ?? lines
                .Select(line => Responsibility(line.Id == SecondLineId ? SecondLineId : LineId))
                .ToArray(),
            [],
            DirectPurchaseState.Authorized,
            UserId,
            OccurredAt);
    }

    private static string Fingerprint(decimal maximumSource, IReadOnlyList<PurchaseOrderContentRef> lines)
    {
        var responsibilities = lines
            .Select(line => Responsibility(line.Id == SecondLineId ? SecondLineId : LineId))
            .ToArray();
        return DirectPurchaseFingerprints.AuthorizationFingerprint(
            OrganizationId,
            RequestId,
            1,
            "dp-key-1",
            UserId,
            lines,
            responsibilities,
            PolicyBundle(),
            Ref(EvidenceId, 1, Digest('e')),
            new PurchaseOrderEntityRef(SupplierId, 1),
            maximumSource,
            450m,
            VendorTermsSnapshot.ForDirectPurchase(
                SupplierContent(), RequestRef(), PolicyBundle(), "PEN", OccurredAt).Digest);
    }

    private static AcceptanceResponsibility Responsibility(Guid lineId) => new(
        PurchaseOrderCodes.ResponsibilityGoodsReceipt,
        Ref(lineId, 1, Digest('f')),
        new PurchaseOrderEntityRef(UserId, 1),
        new PurchaseOrderEntityRef(UserId, 1),
        UserId,
        AcceptanceResponsibilityBuilder.ReasonRequestedFor,
        OccurredAt,
        1);

    private static PurchaseOrderContentRef Line(Guid lineId) => Ref(lineId, 1, Digest('f'));

    private static SupplierContentSnapshot SupplierContent() =>
        new(SupplierId, 1, Digest('b'), new SupplierPaymentTerms("NET30", 30), ["PEN"]);

    private static PurchaseOrderContentRef RequestRef() => Ref(RequestId, 1, Digest('1'));

    private static SourcingPolicyEvaluationRef PolicyBundle() =>
        new(1, AuthorityId, Digest('2'), Digest('3'), Digest('4'), Digest('5'), Digest('6'));

    private static PurchaseOrderContentRef Ref(Guid id, int version, string digest) => new(id, version, digest);

    private static string Digest(char value) => new(value, 64);
}
