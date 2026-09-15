using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Domain.Modules.Budget;

/// <summary>
/// Canonical preimages of the Budget module (SPEC 08 Canonicalización y evidencia). Every preimage
/// uses properties with exact names, always present, and the writer rules of
/// <c>policy-canonical-json/v1</c>; the published vectors in the contract are reproduced by these
/// builders, so a change here is a contract change and requires a new canonicalization version.
/// </summary>
public static class BudgetFingerprints
{
    /// <summary>
    /// <c>budget-position-key/v1</c>: stable identity of one position. Renaming a Cost Center or
    /// revising a Spend Category never changes this value (REQ-01, DEC-02).
    /// </summary>
    public static CanonicalValue PositionKeyPreimage(
        Guid organizationId,
        Guid costCenterId,
        int fiscalYear,
        string spendCategoryCode) =>
        ApprovalCanonicalJson.Object(
            ("canonicalization_version", ApprovalCanonicalJson.String(BudgetCanonicalJson.CanonicalizationVersion)),
            ("contract_version", ApprovalCanonicalJson.String(BudgetCodes.PositionKeyContractVersion)),
            ("cost_center_id", ApprovalCanonicalJson.String(
                BudgetCodes.RequireIdentity(costCenterId, "Cost center"))),
            ("fiscal_year", ApprovalCanonicalJson.Number(BudgetCodes.RequireFiscalYear(fiscalYear))),
            ("organization_id", ApprovalCanonicalJson.String(
                BudgetCodes.RequireIdentity(organizationId, "Organization"))),
            ("spend_category_code", ApprovalCanonicalJson.String(
                RequireSpendCategoryCode(spendCategoryCode))));

    public static string PositionKeyDigest(
        Guid organizationId,
        Guid costCenterId,
        int fiscalYear,
        string spendCategoryCode) =>
        ApprovalCanonicalJson.Digest(
            PositionKeyPreimage(organizationId, costCenterId, fiscalYear, spendCategoryCode));

    /// <summary>The nested position object shared by the allocation preimage (REQ-01).</summary>
    public static CanonicalValue PositionValue(BudgetPositionPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return ApprovalCanonicalJson.Object(
            ("cost_center_ref", ApprovalCanonicalJson.Object(
                ("id", ApprovalCanonicalJson.String(payload.CostCenter.Id)),
                ("version", ApprovalCanonicalJson.Number(payload.CostCenter.Version)))),
            ("fiscal_year", ApprovalCanonicalJson.Number(payload.Position.FiscalYear)),
            ("spend_category_ref", SpendCategoryRefValue(payload.SpendCategory)));
    }

    /// <summary>Public <c>{catalog,code,digest,version}</c> form of a Spend Category reference.</summary>
    public static CanonicalValue SpendCategoryRefValue(BudgetSpendCategoryRef reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        return ApprovalCanonicalJson.Object(
            ("catalog", ApprovalCanonicalJson.String(BudgetCodes.SpendCategoryCatalog)),
            ("code", ApprovalCanonicalJson.String(reference.Code)),
            ("digest", ApprovalCanonicalJson.String(reference.Digest)),
            ("version", ApprovalCanonicalJson.Number(reference.Version)));
    }

    /// <summary>Versioned Cost Center reference used inside a demand (REQ-05).</summary>
    public static CanonicalValue CostCenterRefValue(BudgetCostCenterRef reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        return ApprovalCanonicalJson.Object(
            ("id", ApprovalCanonicalJson.String(reference.Id)),
            ("version", ApprovalCanonicalJson.Number(reference.Version)));
    }

    /// <summary>Full Approval target of a movement or evidence row (SPEC 03 target schema).</summary>
    public static CanonicalValue TargetValue(BudgetTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return ApprovalCanonicalJson.Object(
            ("id", ApprovalCanonicalJson.String(target.Id)),
            ("material_snapshot_digest", ApprovalCanonicalJson.String(target.MaterialSnapshotDigest)),
            ("type", ApprovalCanonicalJson.String(target.Type)),
            ("version", ApprovalCanonicalJson.Number(target.Version)));
    }

    /// <summary>The wire actor union: exactly one complete variant, nullable properties present.</summary>
    public static CanonicalValue ActorValue(BudgetActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return ApprovalCanonicalJson.Object(
            ("system_id", ApprovalCanonicalJson.StringOrNull(actor.SystemId)),
            ("type", ApprovalCanonicalJson.String(actor.Type)),
            ("user_id", ApprovalCanonicalJson.StringOrNull(actor.UserId)),
            ("workload_client_id", ApprovalCanonicalJson.StringOrNull(actor.WorkloadClientId)),
            ("workload_issuer", ApprovalCanonicalJson.StringOrNull(actor.WorkloadIssuer)));
    }

    /// <summary>The typed source of an operation, movement or release (REQ-03, REQ-09).</summary>
    public static CanonicalValue SourceValue(BudgetSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return ApprovalCanonicalJson.Object(
            ("digest", ApprovalCanonicalJson.String(source.Digest)),
            ("id", ApprovalCanonicalJson.String(source.Id)),
            ("type", ApprovalCanonicalJson.String(source.Type)),
            ("version", ApprovalCanonicalJson.Number(source.Version)));
    }

    /// <summary>One line of a budget check: the attested position plus the pending amount (REQ-05).</summary>
    public static CanonicalValue DemandValue(BudgetDemand demand)
    {
        ArgumentNullException.ThrowIfNull(demand);
        return ApprovalCanonicalJson.Object(
            ("amount_base", ApprovalCanonicalJson.String(
                ApprovalCanonicalJson.FormatDecimal(BudgetCodes.RequirePositiveAmount(demand.AmountBase, "amount_base")))),
            ("cost_center_ref", ApprovalCanonicalJson.Object(
                ("id", ApprovalCanonicalJson.String(demand.Payload.CostCenter.Id)),
                ("version", ApprovalCanonicalJson.Number(demand.Payload.CostCenter.Version)))),
            ("fiscal_year", ApprovalCanonicalJson.Number(demand.Payload.Position.FiscalYear)),
            ("source_line", ApprovalCanonicalJson.Object(
                ("content_digest", ApprovalCanonicalJson.String(demand.SourceLineContentDigest)),
                ("id", ApprovalCanonicalJson.String(demand.SourceLineId)),
                ("version", ApprovalCanonicalJson.Number(demand.SourceLineVersion)))),
            ("spend_category_ref", SpendCategoryRefValue(demand.Payload.SpendCategory)),
            ("target", demand.Target is null ? ApprovalCanonicalJson.Null() : TargetValue(demand.Target)));
    }

    public static CanonicalValue DemandSet(IEnumerable<BudgetDemand> demands) =>
        ApprovalCanonicalJson.Set((demands ?? throw new DomainValidationException("Demands are required."))
            .Select(DemandValue));

    /// <summary><c>allocation_fingerprint</c>: administrative revision of the ALLOCATED amount (REQ-01).</summary>
    public static CanonicalValue AllocationPreimage(
        Guid organizationId,
        BudgetActor actor,
        BudgetPositionPayload payload,
        decimal allocatedAmount,
        string currency,
        int? expectedVersion,
        string allocationKey,
        string reason) =>
        ApprovalCanonicalJson.Object(
            ("actor_user_id", ApprovalCanonicalJson.String(RequireUserActor(actor))),
            ("allocated_amount", ApprovalCanonicalJson.String(
                ApprovalCanonicalJson.FormatDecimal(BudgetCodes.RequireAmount(allocatedAmount, "Allocated amount")))),
            ("allocation_key", ApprovalCanonicalJson.String(BudgetCodes.RequireKey(allocationKey, "allocation_key"))),
            ("canonicalization_version", ApprovalCanonicalJson.String(BudgetCanonicalJson.CanonicalizationVersion)),
            ("currency", ApprovalCanonicalJson.String(BudgetCodes.RequireBaseCurrency(currency))),
            ("expected_version", ApprovalCanonicalJson.NumberOrNull(expectedVersion)),
            ("organization_id", ApprovalCanonicalJson.String(
                BudgetCodes.RequireIdentity(organizationId, "Organization"))),
            ("position", PositionValue(payload)),
            ("reason", ApprovalCanonicalJson.String(BudgetCodes.RequireReason(reason))));

    /// <summary><c>precheck_fingerprint</c>: auditable, non-binding availability check (REQ-04).</summary>
    public static CanonicalValue PrecheckPreimage(
        Guid organizationId,
        string precheckKey,
        string manifestDigest,
        BudgetSource source,
        IEnumerable<BudgetDemand> demands) =>
        ApprovalCanonicalJson.Object(
            ("canonicalization_version", ApprovalCanonicalJson.String(BudgetCanonicalJson.CanonicalizationVersion)),
            ("command_version", ApprovalCanonicalJson.String(BudgetCodes.PrecheckCommandVersion)),
            ("demands", DemandSet(demands)),
            ("manifest_digest", ApprovalCanonicalJson.String(
                BudgetCodes.RequireDigest(manifestDigest, "manifest_digest"))),
            ("organization_id", ApprovalCanonicalJson.String(
                BudgetCodes.RequireIdentity(organizationId, "Organization"))),
            ("precheck_key", ApprovalCanonicalJson.String(BudgetCodes.RequireKey(precheckKey, "precheck_key"))),
            ("source", SourceValue(source)));

    /// <summary>One movement command item: amount, parent and optional Approval target (REQ-09).</summary>
    public static CanonicalValue MovementCommandItemValue(BudgetMovementCommandItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return ApprovalCanonicalJson.Object(
            ("amount", ApprovalCanonicalJson.String(
                ApprovalCanonicalJson.FormatDecimal(BudgetCodes.RequirePositiveAmount(item.Amount, "Movement amount")))),
            ("parent_movement_id", ApprovalCanonicalJson.StringOrNull(item.ParentMovementId)),
            ("parent_movement_version", item.ParentMovementId is null
                ? ApprovalCanonicalJson.Null()
                : ApprovalCanonicalJson.Number(BudgetCodes.ParentMovementVersion)),
            ("target", item.Target is null ? ApprovalCanonicalJson.Null() : TargetValue(item.Target)));
    }

    /// <summary><c>operation_fingerprint</c>: root of one all-or-nothing transition batch (REQ-09).</summary>
    public static CanonicalValue OperationPreimage(
        Guid organizationId,
        BudgetActor actor,
        BudgetOperationKind kind,
        string operationKey,
        string reasonCode,
        BudgetSource source,
        IEnumerable<BudgetMovementCommandItem> movements) =>
        ApprovalCanonicalJson.Object(
            ("actor", ActorValue(actor)),
            ("canonicalization_version", ApprovalCanonicalJson.String(BudgetCanonicalJson.CanonicalizationVersion)),
            ("command_version", ApprovalCanonicalJson.String(BudgetCodes.TransitionCommandVersion)),
            ("movements", ApprovalCanonicalJson.Set((movements ?? throw new DomainValidationException(
                "Movement items are required.")).Select(MovementCommandItemValue))),
            ("operation", ApprovalCanonicalJson.String(OperationCode(kind))),
            ("operation_key", ApprovalCanonicalJson.String(BudgetCodes.RequireKey(operationKey, "operation_key"))),
            ("organization_id", ApprovalCanonicalJson.String(
                BudgetCodes.RequireIdentity(organizationId, "Organization"))),
            ("reason_code", ApprovalCanonicalJson.String(BudgetCodes.RequireReasonCode(reasonCode))),
            ("source", SourceValue(source)));

    /// <summary><c>release_fingerprint</c>: durable release of open reservations (REQ-08).</summary>
    public static CanonicalValue ReleasePreimage(
        Guid organizationId,
        Guid caseId,
        Guid requestId,
        int requestVersion,
        string releaseKey,
        string reasonCode,
        BudgetReleaseTrigger trigger,
        BudgetTriggerEvent? triggerEvent,
        IEnumerable<BudgetTarget> targets) =>
        ApprovalCanonicalJson.Object(
            ("case_id", ApprovalCanonicalJson.String(BudgetCodes.RequireIdentity(caseId, "Case"))),
            ("canonicalization_version", ApprovalCanonicalJson.String(BudgetCanonicalJson.CanonicalizationVersion)),
            ("command_version", ApprovalCanonicalJson.String(BudgetCodes.ReleaseCommandVersion)),
            ("organization_id", ApprovalCanonicalJson.String(
                BudgetCodes.RequireIdentity(organizationId, "Organization"))),
            ("reason_code", ApprovalCanonicalJson.String(BudgetCodes.RequireReasonCode(reasonCode))),
            ("release_key", ApprovalCanonicalJson.String(BudgetCodes.RequireKey(releaseKey, "release_key"))),
            ("request_id", ApprovalCanonicalJson.String(BudgetCodes.RequireIdentity(requestId, "Request"))),
            ("request_version", ApprovalCanonicalJson.Number(requestVersion >= 1
                ? requestVersion
                : throw new DomainValidationException("The request version must be positive."))),
            ("targets", ApprovalCanonicalJson.Set((targets ?? throw new DomainValidationException(
                "Release targets are required.")).Select(TargetValue))),
            ("trigger", ApprovalCanonicalJson.String(TriggerCode(trigger))),
            ("trigger_event", triggerEvent is null
                ? ApprovalCanonicalJson.Null()
                : ApprovalCanonicalJson.Object(
                    ("contract_version", ApprovalCanonicalJson.String(triggerEvent.ContractVersion)),
                    ("event_id", ApprovalCanonicalJson.String(triggerEvent.EventId)))));

    /// <summary>One movement as it appears inside the reproducible evidence (REQ-10).</summary>
    public static CanonicalValue EvidenceMovementValue(BudgetEvidenceMovement movement)
    {
        ArgumentNullException.ThrowIfNull(movement);
        return ApprovalCanonicalJson.Object(
            ("amount", ApprovalCanonicalJson.String(
                ApprovalCanonicalJson.FormatDecimal(BudgetCodes.RequirePositiveAmount(movement.Amount, "Movement amount")))),
            ("id", ApprovalCanonicalJson.String(movement.Id)),
            ("position_key_digest", ApprovalCanonicalJson.String(
                BudgetCodes.RequireDigest(movement.PositionKeyDigest, "position_key_digest"))),
            ("target", movement.Target is null ? ApprovalCanonicalJson.Null() : TargetValue(movement.Target)),
            ("type", ApprovalCanonicalJson.String(MovementTypeCode(movement.Type))),
            ("version", ApprovalCanonicalJson.Number(BudgetCodes.MovementVersion)));
    }

    /// <summary>
    /// <c>budget_check_evidence_digest</c>: reproducible proof that satisfies the Approval
    /// prerequisite (REQ-06, REQ-10).
    /// </summary>
    public static CanonicalValue EvidencePreimage(
        Guid organizationId,
        Guid attemptId,
        Guid caseId,
        Guid prerequisiteId,
        DateTimeOffset checkedAt,
        BudgetCheckResult result,
        string signalKey,
        string parametersDigest,
        string sourceControlDigest,
        IEnumerable<BudgetEvidenceMovement> movements) =>
        ApprovalCanonicalJson.Object(
            ("attempt_id", ApprovalCanonicalJson.String(BudgetCodes.RequireIdentity(attemptId, "Attempt"))),
            ("canonicalization_version", ApprovalCanonicalJson.String(BudgetCanonicalJson.CanonicalizationVersion)),
            ("case_id", ApprovalCanonicalJson.String(BudgetCodes.RequireIdentity(caseId, "Case"))),
            ("checked_at", ApprovalCanonicalJson.String(checkedAt)),
            ("contract_version", ApprovalCanonicalJson.String(BudgetCodes.EvidenceContractVersion)),
            ("movements", ApprovalCanonicalJson.Array((movements ?? throw new DomainValidationException(
                "Evidence movements are required.")).Select(EvidenceMovementValue))),
            ("organization_id", ApprovalCanonicalJson.String(
                BudgetCodes.RequireIdentity(organizationId, "Organization"))),
            ("parameters_digest", ApprovalCanonicalJson.String(
                BudgetCodes.RequireDigest(parametersDigest, "parameters_digest"))),
            ("prerequisite_id", ApprovalCanonicalJson.String(
                BudgetCodes.RequireIdentity(prerequisiteId, "Prerequisite"))),
            ("result", ApprovalCanonicalJson.String(EvidenceResultCode(result))),
            ("signal_key", ApprovalCanonicalJson.String(BudgetCodes.RequireKey(signalKey, "signal_key"))),
            ("source_control_digest", ApprovalCanonicalJson.String(
                BudgetCodes.RequireDigest(sourceControlDigest, "source_control_digest"))));

    /// <summary>Contract code of one movement type; the wire never publishes the CLR enum name.</summary>
    public static string MovementTypeCode(BudgetMovementType type) => type switch
    {
        BudgetMovementType.Requested => "REQUESTED",
        BudgetMovementType.Reserved => "RESERVED",
        BudgetMovementType.Committed => "COMMITTED",
        BudgetMovementType.Consumed => "CONSUMED",
        BudgetMovementType.Reverse => "REVERSE",
        _ => throw new DomainValidationException("The budget movement type is not recognized.")
    };

    /// <summary>Contract code of one operation kind, as published by the transition command (REQ-09).</summary>
    public static string OperationCode(BudgetOperationKind kind) => kind switch
    {
        BudgetOperationKind.Precheck => "PRECHECK",
        BudgetOperationKind.ApprovalReserve => "APPROVAL_RESERVE",
        BudgetOperationKind.TransferReserve => "TRANSFER_RESERVE",
        BudgetOperationKind.Commit => "COMMIT",
        BudgetOperationKind.Consume => "CONSUME",
        BudgetOperationKind.Release => "RELEASE",
        BudgetOperationKind.Reverse => "REVERSE",
        _ => throw new DomainValidationException("The budget operation kind is not recognized.")
    };

    /// <summary>Result of a precheck as published to the requester (REQ-04).</summary>
    public static string ResultCode(BudgetCheckResult result) => result switch
    {
        BudgetCheckResult.Available => "AVAILABLE",
        BudgetCheckResult.Insufficient => "INSUFFICIENT",
        BudgetCheckResult.Unfunded => "UNFUNDED",
        _ => throw new DomainValidationException("The budget check result is not recognized.")
    };

    /// <summary>
    /// Result of the prerequisite check: the evidence published to Approval only carries the two
    /// signal outcomes of SPEC 03, because an unfunded position is reported as insufficient (REQ-06).
    /// </summary>
    public static string EvidenceResultCode(BudgetCheckResult result) => result switch
    {
        BudgetCheckResult.Available => "SATISFIED",
        BudgetCheckResult.Insufficient or BudgetCheckResult.Unfunded => "FAILED",
        _ => throw new DomainValidationException("The budget check result is not recognized.")
    };

    public static string TriggerCode(BudgetReleaseTrigger trigger) => trigger switch
    {
        BudgetReleaseTrigger.ApprovalResult => "APPROVAL_RESULT",
        BudgetReleaseTrigger.ApprovalSuperseded => "APPROVAL_SUPERSEDED",
        BudgetReleaseTrigger.PurchaseRequestCancelled => "PURCHASE_REQUEST_CANCELLED",
        _ => throw new DomainValidationException("The budget release trigger is not recognized.")
    };

    private static string RequireUserActor(BudgetActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return actor.Type == BudgetActor.UserType && actor.UserId is Guid userId
            ? userId.ToString("D").ToLowerInvariant()
            : throw new DomainValidationException("An allocation requires an authenticated user actor.");
    }

    private static string RequireSpendCategoryCode(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new DomainValidationException("Spend Category code is required.")
            : value.Trim().ToUpperInvariant();
}
