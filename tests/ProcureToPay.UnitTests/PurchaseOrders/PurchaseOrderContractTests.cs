using System.Text.Json;
using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Domain.Modules.PurchaseOrders;
using ProcureToPay.Domain.Modules.Sourcing;
using ProcureToPay.Domain.Modules.Suppliers;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.UnitTests.PurchaseOrders;

/// <summary>
/// Contract tests of the Purchase Orders domain (SPEC 11 CA-02, CA-03): the PO line reproduces the
/// award deterministically, a draft only accepts delivery/assignments, the vendor terms snapshot
/// restates its sources and every canonical document publishes exactly its documented property set.
/// </summary>
public sealed class PurchaseOrderContractTests
{
    private static readonly Guid OrganizationId = Guid.Parse("aaaaaaaa-1111-1111-1111-111111111111");
    private static readonly Guid PoId = Guid.Parse("bbbbbbbb-2222-2222-2222-222222222222");
    private static readonly Guid LegalEntityId = Guid.Parse("cccccccc-3333-3333-3333-333333333333");
    private static readonly Guid SupplierId = Guid.Parse("dddddddd-4444-4444-4444-444444444444");
    private static readonly Guid AwardId = Guid.Parse("eeeeeeee-5555-5555-5555-555555555555");
    private static readonly Guid ClaimId = Guid.Parse("ffffffff-6666-6666-6666-666666666666");
    private static readonly Guid RequestId = Guid.Parse("11111111-7777-7777-7777-777777777777");
    private static readonly Guid ProposalId = Guid.Parse("22222222-8888-8888-8888-888888888888");
    private static readonly Guid LineId = Guid.Parse("33333333-9999-9999-9999-999999999999");
    private static readonly Guid SecondLineId = Guid.Parse("44444444-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid RequesterId = Guid.Parse("55555555-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid OtherUserId = Guid.Parse("66666666-cccc-cccc-cccc-cccccccccccc");
    private static readonly DateTimeOffset OccurredAt = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_purchase_order_line_reproduces_the_award_without_reinventing_the_tax_breakdown()
    {
        var awardLine = AwardLine(LineId, quantity: 3m, unitPrice: 100m, sourceGross: 300m, baseGross: 300m);

        var line = PurchaseOrderLine.FromAwardLine(awardLine, LineId, RequestLine(LineId));

        Assert.Equal(300m, line.Subtotal);
        Assert.Equal(0m, line.Taxes);
        Assert.Equal(0m, line.AdditionalCharges);
        Assert.Equal(0m, line.Discounts);
        Assert.Equal(300m, line.GrossTotal);
        Assert.Equal(3m, line.Quantity);
        Assert.Equal(100m, line.UnitPrice);
        Assert.Equal("PEN", line.SourceCurrency);
        Assert.Equal(awardLine.LineRef.ContentDigest, line.AwardLineRef.ContentDigest);
        Assert.Empty(line.AcceptanceResponsibilities);
    }

    [Fact]
    public void A_line_rejects_a_gross_total_that_does_not_match_its_breakdown()
    {
        var exception = Assert.Throws<DomainValidationException>(() => new PurchaseOrderLine(
            LineId,
            ContentRef(LineId, 1, Digest('a')),
            RequestLine(LineId),
            1m,
            "UNIT",
            10m,
            "PEN",
            10m,
            "PEN",
            10m,
            subtotal: 9m,
            taxes: 0m,
            additionalCharges: 0m,
            discounts: 0m,
            fxSnapshotRef: null,
            acceptanceResponsibilities: null));
        Assert.Contains("gross_total", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_foreign_line_requires_its_fx_snapshot_reference()
    {
        var exception = Assert.Throws<DomainValidationException>(() => new PurchaseOrderLine(
            LineId,
            ContentRef(LineId, 1, Digest('a')),
            RequestLine(LineId),
            1m,
            "UNIT",
            10m,
            "USD",
            10m,
            "PEN",
            37m,
            subtotal: 10m,
            taxes: 0m,
            additionalCharges: 0m,
            discounts: 0m,
            fxSnapshotRef: null,
            acceptanceResponsibilities: null));
        Assert.Contains("FX snapshot", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("GOOD", "GOODS_RECEIPT")]
    [InlineData("SERVICE", "SERVICE_ACCEPTANCE")]
    public void The_required_responsibility_of_a_line_comes_from_its_purchase_type(string type, string kind)
    {
        var responsibilities = AcceptanceResponsibilityBuilder.Build(
            RequestLine(LineId),
            type,
            new PurchaseOrderEntityRef(RequesterId, 1),
            [new AcceptanceAssignmentRequest(kind, RequesterId, 1, null)],
            RequesterId,
            OccurredAt);

        var responsibility = Assert.Single(responsibilities);
        Assert.Equal(kind, responsibility.Kind);
        Assert.Equal(AcceptanceResponsibilityBuilder.ReasonRequestedFor, responsibility.Reason);
        Assert.Equal(1, responsibility.Version);
    }

    [Fact]
    public void A_subscription_line_requires_both_of_its_responsibilities()
    {
        var missing = Assert.Throws<PurchaseOrderUnprocessableException>(() =>
            AcceptanceResponsibilityBuilder.Build(
                RequestLine(LineId),
                "SUBSCRIPTION",
                new PurchaseOrderEntityRef(RequesterId, 1),
                [new AcceptanceAssignmentRequest(
                    PurchaseOrderCodes.ResponsibilitySubscriptionProvisioning, RequesterId, 1, null)],
                RequesterId,
                OccurredAt));
        Assert.Contains("SUBSCRIPTION_ACCESS_CONFIRMATION", missing.Message, StringComparison.Ordinal);

        var responsibilities = AcceptanceResponsibilityBuilder.Build(
            RequestLine(LineId),
            "SUBSCRIPTION",
            new PurchaseOrderEntityRef(RequesterId, 1),
            [
                new AcceptanceAssignmentRequest(
                    PurchaseOrderCodes.ResponsibilitySubscriptionProvisioning, RequesterId, 1, null),
                new AcceptanceAssignmentRequest(
                    PurchaseOrderCodes.ResponsibilitySubscriptionAccessConfirmation, RequesterId, 1, null)
            ],
            RequesterId,
            OccurredAt);
        Assert.Equal(2, responsibilities.Count);
    }

    [Fact]
    public void Choosing_another_user_requires_a_recorded_reason_and_the_candidate_stays_attested()
    {
        var withoutReason = Assert.Throws<PurchaseOrderUnprocessableException>(() =>
            AcceptanceResponsibilityBuilder.Build(
                RequestLine(LineId),
                "GOOD",
                new PurchaseOrderEntityRef(RequesterId, 1),
                [new AcceptanceAssignmentRequest(
                    PurchaseOrderCodes.ResponsibilityGoodsReceipt, OtherUserId, 1, null)],
                RequesterId,
                OccurredAt));
        Assert.Contains("reason", withoutReason.Message, StringComparison.OrdinalIgnoreCase);

        var responsibility = Assert.Single(AcceptanceResponsibilityBuilder.Build(
            RequestLine(LineId),
            "GOOD",
            new PurchaseOrderEntityRef(RequesterId, 1),
            [new AcceptanceAssignmentRequest(
                PurchaseOrderCodes.ResponsibilityGoodsReceipt, OtherUserId, 1, "Delegated receiver")],
            RequesterId,
            OccurredAt));
        Assert.Equal(OtherUserId, responsibility.Principal.Id);
        Assert.Equal(RequesterId, responsibility.CandidateUserRef.Id);
        Assert.Equal("Delegated receiver", responsibility.Reason);
    }

    [Fact]
    public void A_responsibility_of_another_purchase_type_is_refused()
    {
        Assert.Throws<PurchaseOrderUnprocessableException>(() => AcceptanceResponsibilityBuilder.Build(
            RequestLine(LineId),
            "GOOD",
            new PurchaseOrderEntityRef(RequesterId, 1),
            [new AcceptanceAssignmentRequest(
                PurchaseOrderCodes.ResponsibilityServiceAcceptance, RequesterId, 1, null)],
            RequesterId,
            OccurredAt));
    }

    [Fact]
    public void Vendor_terms_restate_the_award_and_expose_the_supplier_payment_days()
    {
        var terms = new CommercialTerms(10, "EXW", "NET30", 365);
        var snapshot = AwardTerms(terms, ["PEN"], "NET30", 30);

        Assert.Equal(PurchaseOrderCodes.TermsSourceAward, snapshot.SourceKind);
        Assert.Equal("NET30", snapshot.PaymentTerms.Code);
        Assert.Equal(30, snapshot.PaymentTerms.NetDays);
        Assert.Equal("PEN", snapshot.SourceCurrency);
        Assert.Equal(10, snapshot.AwardTerms!.DeliveryDays);
        Assert.Single(snapshot.CatalogSnapshotRefs);
        Assert.NotEqual(new string('0', 64), snapshot.Digest);
    }

    [Fact]
    public void A_contradictory_supplier_payment_code_is_a_business_conflict()
    {
        var terms = new CommercialTerms(10, "EXW", "NET30", 365);
        Assert.Throws<PurchaseOrderUnprocessableException>(() => AwardTerms(terms, ["PEN"], "NET60", 30));
    }

    [Fact]
    public void An_unsupported_currency_is_refused()
    {
        var terms = new CommercialTerms(10, "EXW", "NET30", 365);
        Assert.Throws<PurchaseOrderUnprocessableException>(() => AwardTerms(terms, ["USD"], "NET30", 30));
    }

    [Fact]
    public void Direct_purchase_terms_carry_no_award_and_derive_their_payment_terms()
    {
        var snapshot = VendorTermsSnapshot.ForDirectPurchase(
            Supplier(["PEN", "USD"], "NET45", 45),
            RequestRef(),
            PolicyBundleRef(),
            "USD",
            OccurredAt);

        Assert.Equal(PurchaseOrderCodes.TermsSourceDirectPurchase, snapshot.SourceKind);
        Assert.Null(snapshot.AwardRef);
        Assert.Null(snapshot.AwardTerms);
        Assert.Empty(snapshot.CatalogSnapshotRefs);
        Assert.Equal("NET45", snapshot.PaymentTerms.Code);
        Assert.Equal(45, snapshot.PaymentTerms.NetDays);
        Assert.Equal(["PEN", "USD"], snapshot.SupplierSupportedCurrencies);
    }

    [Fact]
    public void A_draft_only_admits_the_documented_nullable_shape()
    {
        var version = Draft(lines: [Line(LineId, responsibilities: null)]);

        Assert.Equal(PurchaseOrderState.Draft, version.State);
        Assert.Null(version.Delivery);
        Assert.Null(version.ApprovalRef);
        Assert.Null(version.OrderingEvidenceRef);
        Assert.Null(version.IssuedAt);
        Assert.Equal(1, version.PurchaseOrderVersionNumber);
        Assert.Null(version.PredecessorVersion);
    }

    [Fact]
    public void A_presented_version_requires_its_delivery_commitment()
    {
        var exception = Assert.Throws<PurchaseOrderUnprocessableException>(() => Draft(
            lines: [Line(LineId, responsibilities: null)],
            state: PurchaseOrderState.PendingApproval));
        Assert.Contains("delivery", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_issued_version_requires_its_approval_evidence_and_budget_operations()
    {
        var exception = Assert.Throws<PurchaseOrderUnprocessableException>(() => Draft(
            lines: [Line(LineId, responsibilities: [Responsibility(LineId)])],
            state: PurchaseOrderState.Issued,
            delivery: new DeliveryCommitment(new DateOnly(2026, 10, 1), "Lima HQ")));
        Assert.Contains("issued", exception.Message, StringComparison.OrdinalIgnoreCase);

        var issued = Draft(
            lines: [Line(LineId, responsibilities: [Responsibility(LineId)])],
            state: PurchaseOrderState.Issued,
            delivery: new DeliveryCommitment(new DateOnly(2026, 10, 1), "Lima HQ"),
            approvalRef: ContentRef(ClaimId, 1, Digest('c')),
            orderingEvidenceRef: ContentRef(RequestId, 1, Digest('d')),
            issuedAt: OccurredAt,
            budgetOperationRefs: [BudgetRef()]);
        Assert.Equal(PurchaseOrderState.Issued, issued.State);
        Assert.NotNull(issued.IssuedAt);
    }

    [Fact]
    public void A_stored_draft_document_is_round_trippable()
    {
        var version = Draft(
            lines: [Line(LineId, responsibilities: null)],
            delivery: new DeliveryCommitment(new DateOnly(2026, 10, 1), "Lima HQ"));
        var document = version.CanonicalDocument();
        var read = ProcureToPay.Infrastructure.Persistence.PurchaseOrders.PurchaseOrderSerialization
            .ReadDocument(document, version.Digest);
        Assert.Equal(version.Digest, read.Digest);
        Assert.Equal("Lima HQ", read.Delivery!.DeliveryLocation);
    }

    [Fact]
    public void The_same_sources_produce_the_same_digest_and_a_permuted_line_set_does_not_change_it()
    {
        var first = Draft(lines:
        [
            Line(LineId, responsibilities: null),
            Line(SecondLineId, responsibilities: null)
        ]);
        var permuted = Draft(lines:
        [
            Line(SecondLineId, responsibilities: null),
            Line(LineId, responsibilities: null)
        ]);

        Assert.Equal(first.Digest, permuted.Digest);
        Assert.Equal(first.CanonicalDocument(), permuted.CanonicalDocument());
    }

    [Fact]
    public void Changing_a_binding_changes_the_digest()
    {
        var baseline = Draft(lines: [Line(LineId, responsibilities: null)]);
        var changed = Draft(
            lines: [Line(LineId, responsibilities: [Responsibility(LineId)])],
            delivery: new DeliveryCommitment(new DateOnly(2026, 10, 1), "Lima HQ"));

        Assert.NotEqual(baseline.Digest, changed.Digest);
    }

    [Fact]
    public void The_published_document_contains_exactly_its_documented_properties()
    {
        var version = Draft(
            lines: [Line(LineId, responsibilities: [Responsibility(LineId)])],
            delivery: new DeliveryCommitment(new DateOnly(2026, 10, 1), "Lima HQ"));

        var document = JsonDocument.Parse(version.CanonicalDocument());
        var properties = document.RootElement.EnumerateObject().Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal).ToArray();
        Assert.Equal(
            new[]
            {
                "actor_user_id", "amendment_ref", "approval_ref", "award_claim_ref", "award_ref", "base_amount",
                "base_currency", "budget_operation_refs", "canonicalization_version", "contract_version",
                "delivery", "issued_at", "legal_entity_ref", "lines", "ordering_evidence_ref", "organization_id",
                "po_id", "po_number", "predecessor_version", "proposal_ref", "purchase_order_version",
                "request_ref", "source_amount", "source_currency", "state", "supplier_ref", "terms_snapshot"
            },
            properties);

        var line = JsonDocument.Parse(PurchaseOrderCanonicalizer.PurchaseOrderLineDocument(version.Lines[0]));
        Assert.Equal(
            new[]
            {
                "acceptance_responsibilities", "additional_charges", "award_line_ref", "base_currency",
                "base_gross_total", "discounts", "fx_snapshot_ref", "gross_total", "line_id", "quantity",
                "request_line_ref", "source_currency", "subtotal", "taxes", "unit_code", "unit_price"
            },
            line.RootElement.EnumerateObject().Select(property => property.Name)
                .OrderBy(name => name, StringComparer.Ordinal).ToArray());

        var delivery = JsonDocument.Parse(PurchaseOrderCanonicalizer.DeliveryDocument(version.Delivery!));
        Assert.Equal(
            new[] { "delivery_date", "delivery_location" },
            delivery.RootElement.EnumerateObject().Select(property => property.Name).ToArray());

        var responsibility = JsonDocument.Parse(
            PurchaseOrderCanonicalizer.ResponsibilityDocument(version.Lines[0].AcceptanceResponsibilities[0]));
        Assert.Equal(
            new[]
            {
                "assigned_at", "assigned_by_user_id", "candidate_user_ref", "kind", "line_ref", "principal",
                "reason", "version"
            },
            responsibility.RootElement.EnumerateObject().Select(property => property.Name)
                .OrderBy(name => name, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void The_vendor_terms_document_contains_exactly_its_documented_properties()
    {
        var snapshot = AwardTerms(new CommercialTerms(10, "EXW", "NET30", 365), ["PEN"], "NET30", 30);
        var document = JsonDocument.Parse(PurchaseOrderCanonicalizer.VendorTermsDocument(snapshot));
        Assert.Equal(
            new[]
            {
                "award_ref", "award_terms", "canonicalization_version", "catalog_snapshot_refs",
                "contract_version", "evaluated_at", "payment_terms", "policy_bundle_ref", "request_ref",
                "source_currency", "source_kind", "supplier_content_ref", "supplier_supported_currencies"
            },
            document.RootElement.EnumerateObject().Select(property => property.Name)
                .OrderBy(name => name, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void The_claim_fingerprint_ignores_the_requested_instant_and_covers_every_binding()
    {
        var baseline = ClaimRequest(OccurredAt);
        var laterInstant = ClaimRequest(OccurredAt.AddHours(3));
        Assert.Equal(baseline.Fingerprint, laterInstant.Fingerprint);

        var otherKey = new AwardConsumptionClaimRequest(
            baseline.OrganizationId,
            baseline.PoId,
            baseline.AwardRef,
            baseline.CoveredLines,
            "claim-2",
            baseline.RequestedAt,
            baseline.WorkloadIssuer,
            baseline.WorkloadClientId);
        Assert.NotEqual(baseline.Fingerprint, otherKey.Fingerprint);

        var otherLines = new AwardConsumptionClaimRequest(
            baseline.OrganizationId,
            baseline.PoId,
            baseline.AwardRef,
            [ContentRef(SecondLineId, 1, Digest('b'))],
            baseline.ClaimKey,
            baseline.RequestedAt,
            baseline.WorkloadIssuer,
            baseline.WorkloadClientId);
        Assert.NotEqual(baseline.Fingerprint, otherLines.Fingerprint);
    }

    [Fact]
    public void The_issue_fingerprint_covers_the_budget_command_set()
    {
        var commands = new (string, string)[] { ("COMMIT", "commit-key") };
        var baseline = PurchaseOrderFingerprints.PoIssueFingerprint(
            ContentRef(ClaimId, 1, Digest('c')), ContentRef(ClaimId, 1, Digest('d')), commands, 3, "issue-key", Digest('e'), Digest('f'));
        var changed = PurchaseOrderFingerprints.PoIssueFingerprint(
            ContentRef(ClaimId, 1, Digest('c')), ContentRef(ClaimId, 1, Digest('d')),
            [("COMMIT", "commit-key"), ("REVERSE", "release-key")], 3, "issue-key", Digest('e'), Digest('f'));

        Assert.NotEqual(baseline, changed);
    }

    [Fact]
    public void A_payload_over_the_line_limit_is_refused()
    {
        var lines = Enumerable.Range(0, PurchaseOrderCodes.MaxLines + 1)
            .Select(index => Line(Guid.NewGuid(), responsibilities: null))
            .ToArray();
        Assert.Throws<PurchaseOrderPayloadTooLargeException>(() => Draft(lines: lines));
    }

    private static PurchaseOrderVersion Draft(
        IReadOnlyList<PurchaseOrderLine> lines,
        PurchaseOrderState state = PurchaseOrderState.Draft,
        DeliveryCommitment? delivery = null,
        PurchaseOrderContentRef? approvalRef = null,
        PurchaseOrderContentRef? orderingEvidenceRef = null,
        DateTimeOffset? issuedAt = null,
        IReadOnlyList<PurchaseOrderBudgetOperationRef>? budgetOperationRefs = null) =>
        new(
            PoId,
            1,
            null,
            OrganizationId,
            "PO-00000001",
            state,
            new PurchaseOrderEntityRef(LegalEntityId, 1),
            new PurchaseOrderEntityRef(SupplierId, 1),
            RequestRef(),
            new PurchaseOrderContentRef(ProposalId, 1, Digest('7')),
            AwardRef(),
            ContentRef(ClaimId, 1, Digest('8')),
            orderingEvidenceRef,
            approvalRef,
            amendmentRef: null,
            AwardTerms(new CommercialTerms(10, "EXW", "NET30", 365), ["PEN"], "NET30", 30),
            delivery,
            lines,
            budgetOperationRefs,
            lines.Sum(line => line.GrossTotal),
            "PEN",
            lines.Sum(line => line.BaseGrossTotal),
            "PEN",
            RequesterId,
            OccurredAt,
            issuedAt);

    private static PurchaseOrderLine Line(Guid lineId, IReadOnlyList<AcceptanceResponsibility>? responsibilities) =>
        PurchaseOrderLine.FromAwardLine(
            AwardLine(lineId, 1m, 100m, 100m, 100m),
            lineId,
            RequestLine(lineId),
            responsibilities);

    private static AwardLine AwardLine(
        Guid lineId,
        decimal quantity,
        decimal unitPrice,
        decimal sourceGross,
        decimal baseGross) =>
        new(
            new SourcingContentRef(lineId, 1, Digest('a')),
            quantity,
            "UNIT",
            unitPrice,
            "PEN",
            sourceGross,
            "PEN",
            baseGross,
            null);

    private static AcceptanceResponsibility Responsibility(Guid lineId) =>
        new(
            PurchaseOrderCodes.ResponsibilityGoodsReceipt,
            RequestLine(lineId),
            new PurchaseOrderEntityRef(RequesterId, 1),
            new PurchaseOrderEntityRef(RequesterId, 1),
            RequesterId,
            AcceptanceResponsibilityBuilder.ReasonRequestedFor,
            OccurredAt,
            1);

    private static VendorTermsSnapshot AwardTerms(
        CommercialTerms terms,
        IReadOnlyList<string> currencies,
        string supplierPaymentCode,
        int netDays) =>
        VendorTermsSnapshot.ForAward(
            new SupplierContentSnapshot(
                SupplierId, 1, Digest('b'), new SupplierPaymentTerms(supplierPaymentCode, netDays), currencies),
            terms,
            AwardRef(),
            PolicyBundleRef(),
            RequestRef(),
            [
                new SourcingCatalogSnapshot(
                    new SourcingContentRef(LineId, 1, Digest('c')), LineId, 1,
                    new SourcingEntityRef(SupplierId, 1), Digest('d'))
            ],
            "PEN",
            OccurredAt);

    private static SupplierContentSnapshot Supplier(IReadOnlyList<string> currencies, string code, int netDays) =>
        new(SupplierId, 1, Digest('b'), new SupplierPaymentTerms(code, netDays), currencies);

    private static AwardConsumptionClaimRequest ClaimRequest(DateTimeOffset requestedAt) => new(
        OrganizationId,
        PoId,
        AwardRef(),
        [ContentRef(LineId, 1, Digest('a'))],
        "claim-1",
        requestedAt,
        PurchaseOrderCodes.DomainWorkloadIssuer,
        PurchaseOrderCodes.DomainWorkloadClientId);

    private static PurchaseOrderBudgetOperationRef BudgetRef() => new(
        "COMMIT", ClaimId, "commit-key", PoId, 1, Digest('9'));

    private static PurchaseOrderContentRef RequestLine(Guid lineId) => ContentRef(lineId, 1, Digest('b'));

    private static PurchaseOrderContentRef RequestRef() => ContentRef(RequestId, 1, Digest('1'));

    private static PurchaseOrderContentRef AwardRef() => ContentRef(AwardId, 1, Digest('2'));

    private static SourcingPolicyEvaluationRef PolicyBundleRef() => new(
        1, Guid.Parse("99999999-9999-9999-9999-999999999999"), Digest('3'), Digest('4'), Digest('5'), Digest('6'),
        Digest('7'));

    private static PurchaseOrderContentRef ContentRef(Guid id, int version, string digest) =>
        new(id, version, digest);

    private static string Digest(char value) => new(value, 64);
}
