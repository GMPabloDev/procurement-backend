using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Domain.Modules.Sourcing;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Domain.Modules.PurchaseOrders;

/// <summary>
/// Reproducible digests of the Purchase Orders domain (SPEC 11 Canonicalización y digests). Every
/// preimage is an object under <c>policy-canonical-json/v1</c> with exactly the published properties
/// in ordinal order, NFC strings, lowercase UUIDs, seven-decimal UTC instants, decimals as invariant
/// strings and sets ordered by their canonical bytes without duplicates.
/// </summary>
public static class PurchaseOrderCanonicalizer
{
    public static string Hash(string canonicalJson) => PolicyCanonicalizer.Hash(canonicalJson);

    /// <summary>
    /// <c>purchase-order-version/v1</c> document: exactly the digest preimage, so a golden fixture can
    /// publish its bytes and SHA-256 (SPEC 11 Datos y contratos).
    /// </summary>
    public static string PurchaseOrderDocument(PurchaseOrderVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return PolicyCanonicalizer.SerializeCanonical(PurchaseOrderPreimage(version));
    }

    /// <summary>The canonical preimage of one PO version, shared by the digest and the golden tests.</summary>
    public static SortedDictionary<string, object?> PurchaseOrderPreimage(PurchaseOrderVersion version) =>
        new(StringComparer.Ordinal)
        {
            ["actor_user_id"] = version.ActorUserId.ToString("D"),
            ["amendment_ref"] = version.AmendmentRef is null ? null : ContentRef(version.AmendmentRef),
            ["approval_ref"] = version.ApprovalRef is null ? null : ContentRef(version.ApprovalRef),
            ["award_claim_ref"] = ContentRef(version.AwardClaimRef),
            ["award_ref"] = ContentRef(version.AwardRef),
            ["base_amount"] = PurchaseOrderCodes.Decimal(version.BaseAmount),
            ["base_currency"] = version.BaseCurrency,
            ["budget_operation_refs"] = Set(version.BudgetOperationRefs.Select(reference =>
                (object?)BudgetOperationRef(reference))),
            ["canonicalization_version"] = PolicyCanonicalizer.Version,
            ["contract_version"] = PurchaseOrderVersion.ContractVersion,
            ["delivery"] = version.Delivery is null ? null : DeliveryPreimage(version.Delivery),
            ["issued_at"] = version.IssuedAt is null ? null : PurchaseOrderCodes.FormatUtc(version.IssuedAt.Value),
            ["legal_entity_ref"] = EntityRef(version.LegalEntityRef),
            ["lines"] = Set(version.Lines.Select(line => (object?)LinePreimage(line))),
            ["ordering_evidence_ref"] = version.OrderingEvidenceRef is null
                ? null
                : ContentRef(version.OrderingEvidenceRef),
            ["organization_id"] = version.OrganizationId.ToString("D"),
            ["po_id"] = version.PoId.ToString("D"),
            ["po_number"] = version.PoNumber,
            ["predecessor_version"] = version.PredecessorVersion,
            ["proposal_ref"] = ContentRef(version.ProposalRef),
            ["purchase_order_version"] = version.PurchaseOrderVersionNumber,
            ["request_ref"] = ContentRef(version.RequestRef),
            ["source_amount"] = PurchaseOrderCodes.Decimal(version.SourceAmount),
            ["source_currency"] = version.SourceCurrency,
            ["state"] = PurchaseOrderStateCodes.Of(version.State),
            ["supplier_ref"] = EntityRef(version.SupplierRef),
            ["terms_snapshot"] = VendorTermsPreimage(version.TermsSnapshot)
        };

    /// <summary><c>purchase-order-line/v1</c> document of one line.</summary>
    public static string PurchaseOrderLineDocument(PurchaseOrderLine line) =>
        PolicyCanonicalizer.SerializeCanonical(LinePreimage(line));

    /// <summary>The canonical preimage of one PO line.</summary>
    public static SortedDictionary<string, object?> LinePreimage(PurchaseOrderLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        return new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["acceptance_responsibilities"] = Set(line.AcceptanceResponsibilities.Select(responsibility =>
                (object?)ResponsibilityPreimage(responsibility))),
            ["additional_charges"] = PurchaseOrderCodes.Decimal(line.AdditionalCharges),
            ["award_line_ref"] = ContentRef(line.AwardLineRef),
            ["base_currency"] = line.BaseCurrency,
            ["base_gross_total"] = PurchaseOrderCodes.Decimal(line.BaseGrossTotal),
            ["discounts"] = PurchaseOrderCodes.Decimal(line.Discounts),
            ["fx_snapshot_ref"] = line.FxSnapshotRef is null ? null : ContentRef(line.FxSnapshotRef),
            ["gross_total"] = PurchaseOrderCodes.Decimal(line.GrossTotal),
            ["line_id"] = line.LineId.ToString("D"),
            ["quantity"] = PurchaseOrderCodes.Decimal(line.Quantity),
            ["request_line_ref"] = ContentRef(line.RequestLineRef),
            ["source_currency"] = line.SourceCurrency,
            ["subtotal"] = PurchaseOrderCodes.Decimal(line.Subtotal),
            ["taxes"] = PurchaseOrderCodes.Decimal(line.Taxes),
            ["unit_code"] = line.UnitCode,
            ["unit_price"] = PurchaseOrderCodes.Decimal(line.UnitPrice)
        };
    }

    /// <summary><c>delivery-commitment/v1</c> document.</summary>
    public static string DeliveryDocument(DeliveryCommitment delivery) =>
        PolicyCanonicalizer.SerializeCanonical(DeliveryPreimage(delivery));

    /// <summary>The canonical preimage of one delivery commitment.</summary>
    public static SortedDictionary<string, object?> DeliveryPreimage(DeliveryCommitment delivery)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        return new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["delivery_date"] = PurchaseOrderCodes.FormatDate(delivery.DeliveryDate),
            ["delivery_location"] = delivery.DeliveryLocation
        };
    }

    /// <summary><c>acceptance-responsibility/v1</c> document of one assignment.</summary>
    public static string ResponsibilityDocument(AcceptanceResponsibility responsibility) =>
        PolicyCanonicalizer.SerializeCanonical(ResponsibilityPreimage(responsibility));

    /// <summary>The canonical preimage of one acceptance responsibility.</summary>
    public static SortedDictionary<string, object?> ResponsibilityPreimage(AcceptanceResponsibility responsibility)
    {
        ArgumentNullException.ThrowIfNull(responsibility);
        return new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["assigned_at"] = PurchaseOrderCodes.FormatUtc(responsibility.AssignedAt),
            ["assigned_by_user_id"] = responsibility.AssignedByUserId.ToString("D"),
            ["candidate_user_ref"] = EntityRef(responsibility.CandidateUserRef),
            ["kind"] = responsibility.Kind,
            ["line_ref"] = ContentRef(responsibility.LineRef),
            ["principal"] = Principal(responsibility.Principal),
            ["reason"] = responsibility.Reason,
            ["version"] = responsibility.Version
        };
    }

    /// <summary>
    /// <c>vendor-terms-snapshot/v1</c> document: exactly the <c>vendor_terms_snapshot_digest</c>
    /// preimage (REQ-09).
    /// </summary>
    public static string VendorTermsDocument(VendorTermsSnapshot snapshot) =>
        PolicyCanonicalizer.SerializeCanonical(VendorTermsPreimage(snapshot));

    /// <summary>The canonical preimage of one vendor terms snapshot.</summary>
    public static SortedDictionary<string, object?> VendorTermsPreimage(VendorTermsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["award_ref"] = snapshot.AwardRef is null ? null : ContentRef(snapshot.AwardRef),
            ["award_terms"] = snapshot.AwardTerms is null ? null : TermsPreimage(snapshot.AwardTerms),
            ["canonicalization_version"] = PolicyCanonicalizer.Version,
            ["catalog_snapshot_refs"] = Set(snapshot.CatalogSnapshotRefs.Select(reference =>
                (object?)new SortedDictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["digest"] = reference.Digest,
                    ["line_ref"] = ContentRef(reference.LineRef)
                })),
            ["contract_version"] = VendorTermsSnapshot.ContractVersion,
            ["evaluated_at"] = PurchaseOrderCodes.FormatUtc(snapshot.EvaluatedAt),
            ["payment_terms"] = new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["code"] = snapshot.PaymentTerms.Code,
                ["net_days"] = snapshot.PaymentTerms.NetDays
            },
            ["policy_bundle_ref"] = SourcingProposalVersion.EvaluationRefDocument(snapshot.PolicyBundleRef),
            ["request_ref"] = ContentRef(snapshot.RequestRef),
            ["source_currency"] = snapshot.SourceCurrency,
            ["source_kind"] = snapshot.SourceKind,
            ["supplier_content_ref"] = ContentRef(snapshot.SupplierContentRef),
            ["supplier_supported_currencies"] = Set(snapshot.SupplierSupportedCurrencies
                .Select(currency => (object?)currency))
        };
    }

    /// <summary>
    /// Request of <c>award-consumption-claim/v1</c>: the digest preimage of
    /// <c>award_claim_fingerprint</c> plus <c>requested_at</c>, which never decides eligibility.
    /// </summary>
    public static string AwardClaimRequestDocument(AwardConsumptionClaimRequest request) =>
        PolicyCanonicalizer.SerializeCanonical(AwardClaimRequestPreimage(request));

    /// <summary>The canonical preimage of one claim request without <c>requested_at</c>.</summary>
    public static SortedDictionary<string, object?> AwardClaimRequestPreimage(AwardConsumptionClaimRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["award_ref"] = ContentRef(request.AwardRef),
            ["claim_key"] = request.ClaimKey,
            ["contract_version"] = PurchaseOrderCodes.AwardConsumptionClaimContract,
            ["covered_lines"] = Set(request.CoveredLines.Select(line => (object?)ContentRef(line))),
            ["organization_id"] = request.OrganizationId.ToString("D"),
            ["po_id"] = request.PoId.ToString("D"),
            ["requested_at"] = PurchaseOrderCodes.FormatUtc(request.RequestedAt),
            ["workload_client_id"] = request.WorkloadClientId,
            ["workload_issuer"] = request.WorkloadIssuer
        };
    }

    /// <summary>Response of <c>award-consumption-claim/v1</c>, including the verified award snapshot.</summary>
    public static string AwardClaimResponseDocument(AwardConsumptionClaimResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return PolicyCanonicalizer.SerializeCanonical(new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["award_snapshot"] = AwardConsumptionDocument(response.AwardSnapshot),
            ["claim_ref"] = ContentRef(response.ClaimRef),
            ["claimed_at"] = PurchaseOrderCodes.FormatUtc(response.ClaimedAt),
            ["contract_version"] = response.ContractVersion,
            ["po_ref"] = ContentRef(response.PoRef),
            ["replayed"] = response.Replayed,
            ["takeover_refs"] = Set(response.TakeoverRefs.Select(reference => (object?)ContentRef(reference)))
        });
    }

    /// <summary>Canonical JSON of the verified <c>award-consumption/v1</c> response of a claim.</summary>
    public static string AwardConsumptionDocumentJson(AwardConsumptionResponse response) =>
        PolicyCanonicalizer.SerializeCanonical(AwardConsumptionDocument(response));

    /// <summary>
    /// Verified <c>award-consumption/v1</c> response as the claim publishes it. The nested shape is
    /// not part of any digest table of this spec, so it only has to be stable and complete.
    /// </summary>
    public static SortedDictionary<string, object?> AwardConsumptionDocument(AwardConsumptionResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["award_lines"] = Set(response.AwardLines.Select(line => (object?)SourcingProposalVersion.AwardLineDocument(line))),
            ["award_ref"] = ContentRef(new PurchaseOrderContentRef(
                response.AwardRef.Id, response.AwardRef.Version, response.AwardRef.ContentDigest)),
            ["base_amount"] = PurchaseOrderCodes.Decimal(response.BaseAmount),
            ["base_currency"] = response.BaseCurrency,
            ["catalog_snapshot_digest"] = response.CatalogSnapshotsDigest,
            ["catalog_snapshots"] = Set(response.CatalogSnapshots.Select(snapshot =>
                (object?)SourcingAwardVersion.SourcingCatalogSnapshotDocument(snapshot))),
            ["covered_lines"] = Set(response.CoveredLines.Select(line => (object?)new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = line.Id.ToString("D"),
                ["version"] = line.Version
            })),
            ["eligible_at"] = PurchaseOrderCodes.FormatUtc(response.EligibleAt),
            ["fx_snapshots"] = Set(response.FxSnapshots.Select(reference =>
                (object?)SourcingProposalVersion.EvaluationRefDocument(reference))),
            ["policy_bundle_ref"] = SourcingProposalVersion.EvaluationRefDocument(response.PolicyBundleRef),
            ["proposal_ref"] = ContentRef(new PurchaseOrderContentRef(
                response.ProposalRef.Id, response.ProposalRef.Version, response.ProposalRef.ContentDigest)),
            ["request_ref"] = ContentRef(new PurchaseOrderContentRef(
                response.RequestRef.Id, response.RequestRef.Version, response.RequestRef.ContentDigest)),
            ["source_amount"] = PurchaseOrderCodes.Decimal(response.SourceAmount),
            ["source_currency"] = response.SourceCurrency,
            ["supplier_ref"] = EntityRef(new PurchaseOrderEntityRef(
                response.SupplierRef.Id, response.SupplierRef.Version)),
            ["terms"] = TermsPreimage(response.Terms)
        };
    }

    /// <summary>
    /// <c>purchase-order-amendment/v1</c> document: the <c>purchase_order_amendment_digest</c>
    /// preimage of REQ-05.
    /// </summary>
    public static string AmendmentDocument(PurchaseOrderAmendmentVersion amendment) =>
        PolicyCanonicalizer.SerializeCanonical(AmendmentPreimage(amendment));

    /// <summary>The canonical preimage of one amendment version.</summary>
    public static SortedDictionary<string, object?> AmendmentPreimage(PurchaseOrderAmendmentVersion amendment)
    {
        ArgumentNullException.ThrowIfNull(amendment);
        return new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["amendment_id"] = amendment.AmendmentId.ToString("D"),
            ["approval_ref"] = amendment.ApprovalRef is null ? null : ContentRef(amendment.ApprovalRef),
            ["base_po_ref"] = ContentRef(amendment.BasePoRef),
            ["canonicalization_version"] = PolicyCanonicalizer.Version,
            ["contract_version"] = PurchaseOrderAmendmentVersion.ContractVersion,
            ["evidence_refs"] = Set(amendment.EvidenceRefs.Select(reference => (object?)ContentRef(reference))),
            ["expected_po_version"] = amendment.ExpectedPoVersion,
            ["line_deltas"] = Set(amendment.LineDeltas.Select(delta => (object?)new SortedDictionary<string, object?>(
                StringComparer.Ordinal)
            {
                ["change_kind"] = delta.ChangeKind,
                ["line_ref"] = ContentRef(delta.LineRef),
                ["previous_line"] = LinePreimage(delta.PreviousLine),
                ["replacement_line"] = delta.ReplacementLine is null ? null : LinePreimage(delta.ReplacementLine)
            })),
            ["predecessor_version"] = amendment.PredecessorVersion,
            ["reason"] = amendment.Reason,
            ["replacement_delivery"] = amendment.ReplacementDelivery is null
                ? null
                : DeliveryPreimage(amendment.ReplacementDelivery),
            ["responsibility_changes"] = Set(amendment.ResponsibilityChanges.Select(change =>
                (object?)new SortedDictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["kind"] = change.Kind,
                    ["line_ref"] = ContentRef(change.LineRef),
                    ["previous_assignment"] = ResponsibilityPreimage(change.Previous),
                    ["replacement_assignment"] = ResponsibilityPreimage(change.Replacement)
                })),
            ["state"] = AmendmentStateCodes.Of(amendment.State),
            ["successor_award_ref"] = amendment.SuccessorAwardRef is null
                ? null
                : ContentRef(amendment.SuccessorAwardRef),
            ["version"] = amendment.Version
        };
    }

    /// <summary>
    /// <c>procurement-supporting-document/v1</c> document: the <c>supporting_document_digest</c>
    /// preimage of REQ-08. The file reference never publishes an object key or a URL.
    /// </summary>
    public static string SupportingDocumentDocument(ProcurementSupportingDocumentVersion document) =>
        PolicyCanonicalizer.SerializeCanonical(SupportingDocumentPreimage(document));

    /// <summary>The canonical preimage of one supporting document version.</summary>
    public static SortedDictionary<string, object?> SupportingDocumentPreimage(
        ProcurementSupportingDocumentVersion document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["business_type"] = document.BusinessType,
            ["canonicalization_version"] = PolicyCanonicalizer.Version,
            ["contract_version"] = ProcurementSupportingDocumentVersion.ContractVersion,
            ["covered_targets"] = Set(document.CoveredTargets.Select(target => (object?)ContentRef(target))),
            ["file_ref"] = FileRefPreimage(document.FileRef),
            ["organization_id"] = document.OrganizationId.ToString("D"),
            ["request_ref"] = ContentRef(document.RequestRef),
            ["state"] = SupportingDocumentStateCodes.Of(document.State),
            ["version"] = document.Version
        };
    }

    /// <summary><c>file_ref</c> preimage of one stored file: metadata only, never a location.</summary>
    public static SortedDictionary<string, object?> FileRefPreimage(SupportingDocumentFileRef fileRef) =>
        new(StringComparer.Ordinal)
        {
            ["content_type"] = fileRef.ContentType,
            ["file_id"] = fileRef.FileId.ToString("D"),
            ["file_name"] = fileRef.FileName,
            ["length"] = fileRef.Length,
            ["sha256"] = fileRef.Sha256,
            ["version"] = fileRef.Version
        };

    /// <summary>
    /// <c>supporting-document-evidence/v1</c> document: the
    /// <c>supporting_document_evidence_digest</c> preimage of REQ-08.
    /// </summary>
    public static string SupportingDocumentEvidenceDocument(SupportingDocumentEvidence evidence) =>
        PolicyCanonicalizer.SerializeCanonical(SupportingDocumentEvidencePreimage(evidence));

    /// <summary>The canonical preimage of one supporting document evidence.</summary>
    public static SortedDictionary<string, object?> SupportingDocumentEvidencePreimage(
        SupportingDocumentEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var counts = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (target, count) in evidence.CountsByTarget())
        {
            counts[target] = count;
        }

        return new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["attempt_id"] = evidence.AttemptId.ToString("D"),
            ["canonicalization_version"] = PolicyCanonicalizer.Version,
            ["checked_at"] = PurchaseOrderCodes.FormatUtc(evidence.CheckedAt),
            ["contract_version"] = SupportingDocumentEvidence.ContractVersion,
            ["counts_by_target"] = counts,
            ["document_refs"] = Set(evidence.DocumentRefs.Select(reference =>
                (object?)new SortedDictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["document_ref"] = ContentRef(reference.DocumentRef),
                    ["length"] = reference.Length,
                    ["sha256"] = reference.Sha256,
                    ["target"] = ContentRef(reference.TargetRef)
                })),
            ["organization_id"] = evidence.OrganizationId.ToString("D"),
            ["parameters_digest"] = evidence.ParametersDigest,
            ["prerequisite_id"] = evidence.PrerequisiteId.ToString("D"),
            ["result"] = evidence.Result,
            ["signal_key"] = evidence.SignalKey,
            ["targets"] = Set(evidence.Targets.Select(target => (object?)ContentRef(target)))
        };
    }

    /// <summary>
    /// <c>direct-purchase-authorization/v1</c> document: the <c>direct_purchase_authorization_digest</c>
    /// preimage of REQ-07.
    /// </summary>
    public static string DirectPurchaseDocument(DirectPurchaseAuthorization authorization) =>
        PolicyCanonicalizer.SerializeCanonical(DirectPurchasePreimage(authorization));

    /// <summary>The canonical preimage of one Direct Purchase authorization version.</summary>
    public static SortedDictionary<string, object?> DirectPurchasePreimage(DirectPurchaseAuthorization authorization)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        return new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["acceptance_responsibilities"] = Set(authorization.AcceptanceResponsibilities
                .Select(responsibility => (object?)ResponsibilityPreimage(responsibility))),
            ["authorization_id"] = authorization.AuthorizationId.ToString("D"),
            ["authorization_key"] = authorization.AuthorizationKey,
            ["authorized_at"] = PurchaseOrderCodes.FormatUtc(authorization.AuthorizedAt),
            ["authorized_by_user_id"] = authorization.AuthorizedByUserId.ToString("D"),
            ["canonicalization_version"] = PolicyCanonicalizer.Version,
            ["contract_version"] = DirectPurchaseAuthorization.ContractVersion,
            ["covered_lines"] = Set(authorization.CoveredLines.Select(line => (object?)ContentRef(line))),
            ["document_evidence_refs"] = Set(authorization.DocumentEvidenceRefs
                .Select(reference => (object?)ContentRef(reference))),
            ["expected_request_version"] = authorization.ExpectedRequestVersion,
            ["fingerprint"] = authorization.Fingerprint,
            ["maximum_base_amount"] = PurchaseOrderCodes.Decimal(authorization.MaximumBaseAmount),
            ["maximum_source_amount"] = PurchaseOrderCodes.Decimal(authorization.MaximumSourceAmount),
            ["ordering_evidence_ref"] = ContentRef(authorization.OrderingEvidenceRef),
            ["organization_id"] = authorization.OrganizationId.ToString("D"),
            ["policy_bundle_ref"] = SourcingProposalVersion.EvaluationRefDocument(authorization.PolicyBundleRef),
            ["request_approval_case_ref"] = ContentRef(authorization.RequestApprovalCaseRef),
            ["request_ref"] = ContentRef(authorization.RequestRef),
            ["state"] = DirectPurchaseStateCodes.Of(authorization.State),
            ["supplier_ref"] = EntityRef(authorization.SupplierRef),
            ["terms_snapshot"] = VendorTermsPreimage(authorization.TermsSnapshot),
            ["version"] = authorization.Version
        };
    }

    /// <summary>
    /// <c>amendment-line-delta/v1</c> document. The delta reuses the complete
    /// <c>purchase-order-line/v1</c> of both sides, so a golden fixture reproduces its bytes.
    /// </summary>
    public static string AmendmentLineDeltaDocument(
        AmendmentLineDelta delta,
        PurchaseOrderLine previous) =>
        PolicyCanonicalizer.SerializeCanonical(new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["change_kind"] = delta.ChangeKind,
            ["line_ref"] = ContentRef(delta.LineRef),
            ["previous_line"] = LinePreimage(previous),
            ["replacement_line"] = delta.ReplacementLine is null ? null : LinePreimage(delta.ReplacementLine)
        });

    /// <summary><c>responsibility-change/v1</c> document of one assignment replacement.</summary>
    public static string ResponsibilityChangeDocument(ResponsibilityChange change) =>
        PolicyCanonicalizer.SerializeCanonical(new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["kind"] = change.Kind,
            ["line_ref"] = ContentRef(change.LineRef),
            ["previous_assignment"] = ResponsibilityPreimage(change.Previous),
            ["replacement_assignment"] = ResponsibilityPreimage(change.Replacement)
        });

    /// <summary>
    /// <c>purchase-request-ordering-evidence/v1</c> document: the <c>ordering_evidence_digest</c>
    /// preimage of REQ-03.
    /// </summary>
    public static string OrderingEvidenceDocument(PurchaseRequestOrderingEvidence evidence) =>
        PolicyCanonicalizer.SerializeCanonical(new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["budget_evidence_refs"] = Set(evidence.BudgetEvidenceRefs.Select(reference =>
                (object?)new SortedDictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["evidence_digest"] = reference.EvidenceDigest,
                    ["evidence_reference"] = reference.EvidenceReference,
                    ["key"] = reference.Key,
                    ["prerequisite_id"] = reference.PrerequisiteId.ToString("D")
                })),
            ["case_ref"] = CaseRef(evidence.CaseRef),
            ["checked_at"] = PurchaseOrderCodes.FormatUtc(evidence.CheckedAt),
            ["contract_version"] = PurchaseRequestOrderingEvidence.ContractVersion,
            ["covered_targets"] = Set(evidence.CoveredTargets.Select(target => (object?)Target(target))),
            ["organization_id"] = evidence.OrganizationId.ToString("D"),
            ["policy_bundle_ref"] = SourcingProposalVersion.EvaluationRefDocument(evidence.PolicyBundleRef),
            ["request_ref"] = ContentRef(evidence.RequestRef),
            ["requirements"] = Set(evidence.Requirements.Select(requirement =>
                (object?)new SortedDictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["authority"] = new SortedDictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["amount_base"] = requirement.AuthorityAmountBase is null
                            ? null
                            : PurchaseOrderCodes.Decimal(requirement.AuthorityAmountBase.Value),
                        ["currency"] = requirement.AuthorityCurrency,
                        ["level"] = requirement.AuthorityLevel,
                        ["type"] = requirement.AuthorityType
                    },
                    ["key"] = requirement.Key,
                    ["role"] = requirement.Role,
                    ["scope"] = requirement.Scope,
                    ["targets"] = Set(requirement.Targets.Select(target => (object?)Target(target)))
                })),
            ["result_refs"] = Set(evidence.ResultRefs.Select(reference =>
                (object?)new SortedDictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["decision_digest"] = reference.DecisionDigest,
                    ["decision_id"] = reference.DecisionId?.ToString("D"),
                    ["decision_key"] = reference.DecisionKey,
                    ["requirement_id"] = reference.RequirementId.ToString("D"),
                    ["result"] = reference.Result,
                    ["targets"] = Set(reference.Targets.Select(target => (object?)Target(target)))
                }))
        });

    private static SortedDictionary<string, object?> CaseRef(OrderingEvidenceCaseRef caseRef) =>
        new(StringComparer.Ordinal)
        {
            ["case_id"] = caseRef.CaseId.ToString("D"),
            ["case_version"] = caseRef.CaseVersion,
            ["content_digest"] = caseRef.ContentDigest,
            ["operation"] = caseRef.Operation,
            ["subject_id"] = caseRef.SubjectId.ToString("D"),
            ["subject_type"] = caseRef.SubjectType,
            ["subject_version"] = caseRef.SubjectVersion
        };

    private static SortedDictionary<string, object?> Target(OrderingEvidenceTarget target) =>
        new(StringComparer.Ordinal)
        {
            ["material_snapshot_digest"] = target.MaterialSnapshotDigest,
            ["target_id"] = target.TargetId.ToString("D"),
            ["target_version"] = target.TargetVersion
        };

    /// <summary><c>purchase-request-line-takeover/v1</c> document (REQ-10).</summary>
    public static string TakeoverDocument(PurchaseRequestLineTakeover takeover) =>
        PolicyCanonicalizer.SerializeCanonical(TakeoverPreimage(takeover));

    /// <summary>The canonical preimage of one line takeover.</summary>
    public static SortedDictionary<string, object?> TakeoverPreimage(PurchaseRequestLineTakeover takeover)
    {
        ArgumentNullException.ThrowIfNull(takeover);
        return new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["canonicalization_version"] = PolicyCanonicalizer.Version,
            ["consumer_ref"] = ContentRef(takeover.ConsumerRef),
            ["contract_version"] = PurchaseRequestLineTakeover.ContractVersion,
            ["line_ref"] = ContentRef(takeover.LineRef),
            ["organization_id"] = takeover.OrganizationId.ToString("D"),
            ["owner"] = takeover.OwnerCode,
            ["predecessor_ref"] = takeover.PredecessorRef is null ? null : ContentRef(takeover.PredecessorRef),
            ["request_ref"] = ContentRef(takeover.RequestRef),
            ["state"] = TakeoverStateCodes.Of(takeover.State),
            ["version"] = takeover.Version
        };
    }

    public static SortedDictionary<string, object?> ContentRef(PurchaseOrderContentRef reference) =>
        new(StringComparer.Ordinal)
        {
            ["content_digest"] = reference.ContentDigest,
            ["id"] = reference.Id.ToString("D"),
            ["version"] = reference.Version
        };

    public static SortedDictionary<string, object?> EntityRef(PurchaseOrderEntityRef reference) =>
        new(StringComparer.Ordinal)
        {
            ["id"] = reference.Id.ToString("D"),
            ["version"] = reference.Version
        };

    /// <summary><c>principal={id,type,version}</c> of one acceptance responsibility (REQ-06).</summary>
    public static SortedDictionary<string, object?> Principal(PurchaseOrderEntityRef principal) =>
        new(StringComparer.Ordinal)
        {
            ["id"] = principal.Id.ToString("D"),
            ["type"] = PurchaseOrderCodes.PrincipalTypeUser,
            ["version"] = principal.Version
        };

    private static SortedDictionary<string, object?> TermsPreimage(CommercialTerms terms) =>
        new(StringComparer.Ordinal)
        {
            ["delivery_days"] = terms.DeliveryDays,
            ["incoterm_code"] = terms.IncotermCode,
            ["payment_terms_code"] = terms.PaymentTermsCode,
            ["warranty_days"] = terms.WarrantyDays
        };

    private static SortedDictionary<string, object?> BudgetOperationRef(PurchaseOrderBudgetOperationRef reference) =>
        new(StringComparer.Ordinal)
        {
            ["operation"] = reference.Operation,
            ["operation_id"] = reference.OperationId.ToString("D"),
            ["operation_key"] = reference.OperationKey,
            ["source_digest"] = reference.SourceDigest,
            ["source_id"] = reference.SourceId.ToString("D"),
            ["source_version"] = reference.SourceVersion
        };

    /// <summary>Canonical set: ordered by the serialized bytes of each element, duplicates rejected.</summary>
    public static object?[] Set(IEnumerable<object?> values)
    {
        var materialized = (values ?? []).ToArray();
        var ordered = materialized
            .OrderBy(
                value => PolicyCanonicalizer.SerializeCanonical(value!),
                Comparer<string>.Create(PolicyCanonicalizer.CompareCanonical))
            .ToArray();
        var serialized = ordered
            .Select(value => PolicyCanonicalizer.SerializeCanonical(value!))
            .ToArray();
        if (serialized.Distinct(StringComparer.Ordinal).Count() != serialized.Length)
        {
            throw new DomainConflictException("A canonical set cannot repeat an element.");
        }

        return ordered;
    }
}
