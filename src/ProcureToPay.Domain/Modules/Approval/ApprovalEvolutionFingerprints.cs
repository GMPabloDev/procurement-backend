using ProcureToPay.Domain.Modules.Organization;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Domain.Modules.Approval;

/// <summary>
/// Exact preimages of the SPEC 04 contracts (Datos y contratos): delegation, supersession,
/// revocation, the requirement contract digest and the carry-forward proof and derived decision
/// digest. All of them use <c>approval-canonical-json/v2</c> bytes, ordinal key order, NFC
/// strings, lowercase-D UUIDs, UTC timestamps with seven decimals and canonical sets.
/// </summary>
public static partial class ApprovalFingerprints
{
    public const string DelegationActionCreate = "CREATE";
    public const string DelegationActionRevoke = "REVOKE";
    public const string CarryForwardReason = "CARRY_FORWARD";

    /// <summary>
    /// <c>requirement_type</c> has no discriminator in the SPEC 03 requirement schema; the
    /// canonical entity-source code keeps the property present without weakening equality, which
    /// is enforced by the remaining descriptor fields.
    /// </summary>
    public const string RequirementTypeCode = "APPROVAL_REQUIREMENT";

    public static string DelegationFingerprint(
        string action,
        ApprovalDelegationActorType actorType,
        Guid actorUserId,
        Guid delegatorUserId,
        Guid delegateeUserId,
        SystemRole role,
        CanonicalValue scope,
        string delegationCommandKey,
        Guid? delegationId,
        int? expectedVersion,
        Guid organizationId,
        string reason,
        DateTimeOffset validFrom,
        DateTimeOffset validTo)
    {
        if (action is not (DelegationActionCreate or DelegationActionRevoke))
        {
            throw new DomainValidationException("The delegation action is invalid.");
        }

        return ApprovalCanonicalJson.Digest(ApprovalCanonicalJson.Object(
            ("action", ApprovalCanonicalJson.String(action)),
            ("actor_type", ApprovalCanonicalJson.String(ApprovalDelegationCodes.Code(actorType))),
            ("actor_user_id", ApprovalCanonicalJson.String(actorUserId)),
            ("canonicalization_version", ApprovalCanonicalJson.String(ApprovalCanonicalJson.CanonicalizationVersion)),
            ("delegatee_user_id", ApprovalCanonicalJson.String(delegateeUserId)),
            ("delegation_command_key", ApprovalCanonicalJson.String(
                ApprovalLimits.RequireKey(delegationCommandKey, "delegation_command_key"))),
            ("delegation_id", ApprovalCanonicalJson.StringOrNull(delegationId)),
            ("delegator_user_id", ApprovalCanonicalJson.String(delegatorUserId)),
            ("expected_version", ApprovalCanonicalJson.NumberOrNull(expectedVersion)),
            ("organization_id", ApprovalCanonicalJson.String(organizationId)),
            ("reason", ApprovalCanonicalJson.String(ApprovalLimits.RequireReason(reason, "Delegation reason"))),
            ("role", ApprovalCanonicalJson.String(role)),
            ("scope", scope ?? throw new DomainValidationException("A delegation fingerprint needs its scope.")),
            ("valid_from", ApprovalCanonicalJson.String(validFrom)),
            ("valid_to", ApprovalCanonicalJson.String(validTo))));
    }

    public static string SupersessionFingerprint(
        string newSubmissionFingerprint,
        ApprovalWorkloadIdentity workload,
        Guid previousCaseId,
        int previousCaseVersion,
        string supersessionKey,
        IEnumerable<ApprovalSupersessionMapping> targetMapping)
    {
        ArgumentNullException.ThrowIfNull(workload);
        return ApprovalCanonicalJson.Digest(ApprovalCanonicalJson.Object(
            ("canonicalization_version", ApprovalCanonicalJson.String(ApprovalCanonicalJson.CanonicalizationVersion)),
            ("new_submission_fingerprint", ApprovalCanonicalJson.String(
                ApprovalLimits.RequireSha256(newSubmissionFingerprint, "new submission fingerprint"))),
            ("owner_workload_client_id", ApprovalCanonicalJson.String(workload.ClientId)),
            ("owner_workload_issuer", ApprovalCanonicalJson.String(workload.Issuer)),
            ("previous_case_id", ApprovalCanonicalJson.String(previousCaseId)),
            ("previous_case_version", ApprovalCanonicalJson.Number(previousCaseVersion)),
            ("supersession_key", ApprovalCanonicalJson.String(
                ApprovalLimits.RequireKey(supersessionKey, "supersession_key"))),
            ("target_mapping", ApprovalCanonicalJson.Set(
                (targetMapping ?? throw new DomainValidationException("A supersession needs its mapping."))
                .Select(MappingValue)))));
    }

    /// <summary>
    /// <c>supersession_fingerprint</c> of <c>approval-supersession-delta/v1</c> (SPEC 06 REQ-08):
    /// exact preimage of the widened supersession contract under <c>approval-canonical-json/v3</c>.
    /// The historical v2 preimage stays untouched and verifiable above.
    /// </summary>
    public static string SupersessionFingerprintV3(
        string newSubmissionFingerprint,
        ApprovalWorkloadIdentity workload,
        Guid previousCaseId,
        int previousCaseVersion,
        string supersessionKey,
        IEnumerable<ApprovalSupersessionDeltaEntry> targetDelta) =>
        ApprovalCanonicalJson.Digest(SupersessionPreimageV3(
            newSubmissionFingerprint, workload, previousCaseId, previousCaseVersion, supersessionKey, targetDelta));

    /// <summary>Exact canonical preimage of <c>supersession_fingerprint</c> under v3 (SPEC 06 REQ-08).</summary>
    public static CanonicalValue SupersessionPreimageV3(
        string newSubmissionFingerprint,
        ApprovalWorkloadIdentity workload,
        Guid previousCaseId,
        int previousCaseVersion,
        string supersessionKey,
        IEnumerable<ApprovalSupersessionDeltaEntry> targetDelta)
    {
        ArgumentNullException.ThrowIfNull(workload);
        var delta = (targetDelta ?? throw new DomainValidationException("A supersession needs its target delta."))
            .ToArray();
        ApprovalSupersessionDeltaRules.Validate(delta);
        return ApprovalCanonicalJson.Object(
            ("canonicalization_version", ApprovalCanonicalJson.String(ApprovalCanonicalJson.CanonicalizationVersionV3)),
            ("new_submission_fingerprint", ApprovalCanonicalJson.String(
                ApprovalLimits.RequireSha256(newSubmissionFingerprint, "new submission fingerprint"))),
            ("owner_workload_client_id", ApprovalCanonicalJson.String(workload.ClientId)),
            ("owner_workload_issuer", ApprovalCanonicalJson.String(workload.Issuer)),
            ("previous_case_id", ApprovalCanonicalJson.String(previousCaseId)),
            ("previous_case_version", ApprovalCanonicalJson.Number(previousCaseVersion)),
            ("supersession_key", ApprovalCanonicalJson.String(
                ApprovalLimits.RequireKey(supersessionKey, "supersession_key"))),
            ("target_delta", ApprovalCanonicalJson.Set(delta.Select(entry => entry.ToCanonicalValue()))));
    }

    public static string RevocationFingerprint(
        EvidenceRevocationActorType actorType,
        Guid? actorUserId,
        ApprovalWorkloadIdentity? actorWorkload,
        Guid evidenceId,
        int evidenceVersion,
        Guid organizationId,
        string reason,
        string revocationKey)
    {
        if (evidenceVersion < 1)
        {
            throw new DomainValidationException("A revocation fingerprint needs the evidence version.");
        }

        return ApprovalCanonicalJson.Digest(ApprovalCanonicalJson.Object(
            ("actor_type", ApprovalCanonicalJson.String(ApprovalEvolutionCodes.Code(actorType))),
            ("actor_user_id", ApprovalCanonicalJson.StringOrNull(actorUserId)),
            ("actor_workload_client_id", ApprovalCanonicalJson.StringOrNull(actorWorkload?.ClientId)),
            ("actor_workload_issuer", ApprovalCanonicalJson.StringOrNull(actorWorkload?.Issuer)),
            ("canonicalization_version", ApprovalCanonicalJson.String(ApprovalCanonicalJson.CanonicalizationVersion)),
            ("evidence_id", ApprovalCanonicalJson.String(evidenceId)),
            ("evidence_version", ApprovalCanonicalJson.Number(evidenceVersion)),
            ("organization_id", ApprovalCanonicalJson.String(organizationId)),
            ("reason", ApprovalCanonicalJson.String(ApprovalLimits.RequireReason(reason, "Revocation reason"))),
            ("revocation_key", ApprovalCanonicalJson.String(
                ApprovalLimits.RequireKey(revocationKey, "revocation_key")))));
    }

    /// <summary>
    /// Canonical signature of every contractual descriptor field of a requirement (REQ-05). The
    /// target set is compared separately by the carry-forward proof, so it is not part of this
    /// digest.
    /// </summary>
    public static string RequirementContractDigest(ApprovalRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        return ApprovalCanonicalJson.Digest(ApprovalCanonicalJson.Object(
            ("actions", ApprovalCanonicalJson.Set(requirement.Actions.Select(action => ApprovalCanonicalJson.String(action)))),
            ("authority", ApprovalRequirementDefinition.AuthorityValue(requirement.Authority)),
            ("canonicalization_version", ApprovalCanonicalJson.String(ApprovalCanonicalJson.CanonicalizationVersion)),
            ("dependencies", ApprovalCanonicalJson.Set(requirement.Dependencies.Select(DependencyValue))),
            ("exclusions", ApprovalCanonicalJson.Set(requirement.ExcludedUserIds.Select(ApprovalCanonicalJson.String))),
            ("requirement_type", ApprovalCanonicalJson.String(RequirementTypeCode)),
            ("role", ApprovalCanonicalJson.String(requirement.Role)),
            ("scope", DecisionScopeDescriptor.Parse(requirement.DecisionScopeJson).ToCanonicalValue()),
            ("stage_code", ApprovalCanonicalJson.String(requirement.StageCode))));
    }

    public static string CarryForwardProof(
        Guid newCaseId,
        string newRequirementContractDigest,
        string newRequirementKey,
        ApprovalTarget newTarget,
        Guid rootHumanDecisionId,
        string sourceDecisionDigest,
        Guid sourceDecisionId,
        Guid sourceEvidenceId,
        int sourceEvidenceVersion,
        string sourceRequirementContractDigest,
        ApprovalTarget sourceTarget)
    {
        ArgumentNullException.ThrowIfNull(newTarget);
        ArgumentNullException.ThrowIfNull(sourceTarget);
        if (sourceEvidenceVersion < 1)
        {
            throw new DomainValidationException("A carry-forward proof needs the evidence version.");
        }

        return ApprovalCanonicalJson.Digest(ApprovalCanonicalJson.Object(
            ("canonicalization_version", ApprovalCanonicalJson.String(ApprovalCanonicalJson.CanonicalizationVersion)),
            ("new_case_id", ApprovalCanonicalJson.String(newCaseId)),
            ("new_requirement_contract_digest", ApprovalCanonicalJson.String(
                ApprovalLimits.RequireSha256(newRequirementContractDigest, "new requirement contract digest"))),
            ("new_requirement_key", ApprovalCanonicalJson.String(
                ApprovalLimits.RequireKey(newRequirementKey, "workflow requirement key"))),
            ("new_target", ApprovalRequirementDefinition.TargetValue(newTarget)),
            ("root_human_decision_id", ApprovalCanonicalJson.String(rootHumanDecisionId)),
            ("source_decision_digest", ApprovalCanonicalJson.String(
                ApprovalLimits.RequireSha256(sourceDecisionDigest, "source decision digest"))),
            ("source_decision_id", ApprovalCanonicalJson.String(sourceDecisionId)),
            ("source_evidence_id", ApprovalCanonicalJson.String(sourceEvidenceId)),
            ("source_evidence_version", ApprovalCanonicalJson.Number(sourceEvidenceVersion)),
            ("source_requirement_contract_digest", ApprovalCanonicalJson.String(
                ApprovalLimits.RequireSha256(sourceRequirementContractDigest, "source requirement contract digest"))),
            ("source_target", ApprovalRequirementDefinition.TargetValue(sourceTarget))));
    }

    /// <summary>
    /// Digest persisted for an <c>APPROVE/CARRY_FORWARD</c> decision (REQ-05): it never contains
    /// <c>actor_user_id</c>, <c>task_id</c> or <c>decision_key</c>, because the derived decision
    /// has none of them.
    /// </summary>
    public static string CarryForwardDecisionDigest(
        Guid decisionId,
        int decisionVersion,
        Guid caseId,
        string subjectType,
        Guid subjectId,
        int subjectVersion,
        string sourceSnapshotDigest,
        string requirementKey,
        string decisionScopeDigest,
        IEnumerable<ApprovalTarget> targets,
        DateTimeOffset decidedAt,
        string authorityEvidenceDigest,
        string carryForwardProofDigest,
        Guid evidenceId,
        int evidenceVersion,
        Guid? sourceDecisionId,
        Guid? rootHumanDecisionId,
        IEnumerable<Guid> excludedUserIds)
    {
        if (evidenceVersion < 1)
        {
            throw new DomainValidationException("A carry-forward decision needs the evidence version.");
        }

        return ApprovalCanonicalJson.Digest(ApprovalCanonicalJson.Object(
            ("action", ApprovalCanonicalJson.String(ApprovalDecisionAction.Approve)),
            ("actor_system_id", ApprovalCanonicalJson.String(ApprovalSystemActors.ApprovalWorkflow)),
            ("authority_evidence_digest", ApprovalCanonicalJson.String(
                ApprovalLimits.RequireSha256(authorityEvidenceDigest, "authority evidence digest"))),
            ("canonicalization_version", ApprovalCanonicalJson.String(ApprovalCanonicalJson.CanonicalizationVersion)),
            ("carry_forward_proof_digest", ApprovalCanonicalJson.String(
                ApprovalLimits.RequireSha256(carryForwardProofDigest, "carry-forward proof digest"))),
            ("case_id", ApprovalCanonicalJson.String(caseId)),
            ("decided_at", ApprovalCanonicalJson.String(decidedAt)),
            ("decision_id", ApprovalCanonicalJson.String(decisionId)),
            ("decision_scope_digest", ApprovalCanonicalJson.String(
                ApprovalLimits.RequireSha256(decisionScopeDigest, "decision scope digest"))),
            ("decision_version", ApprovalCanonicalJson.Number(decisionVersion)),
            ("evidence_id", ApprovalCanonicalJson.String(evidenceId)),
            ("evidence_version", ApprovalCanonicalJson.Number(evidenceVersion)),
            ("exclusions", ApprovalCanonicalJson.Set(excludedUserIds.Select(ApprovalCanonicalJson.String))),
            ("origin", ApprovalCanonicalJson.String("CARRY_FORWARD")),
            ("reason", ApprovalCanonicalJson.String(CarryForwardReason)),
            ("requirement_key", ApprovalCanonicalJson.String(
                ApprovalLimits.RequireKey(requirementKey, "workflow requirement key"))),
            ("root_human_decision_id", ApprovalCanonicalJson.String(
                RequireIdentity(rootHumanDecisionId, "root human decision id"))),
            ("segregation_satisfied", ApprovalCanonicalJson.Bool(true)),
            ("snapshot_digest", ApprovalCanonicalJson.String(
                ApprovalLimits.RequireSha256(sourceSnapshotDigest, "snapshot digest"))),
            ("source_decision_id", ApprovalCanonicalJson.String(
                RequireIdentity(sourceDecisionId, "source decision id"))),
            ("subject_id", ApprovalCanonicalJson.String(subjectId)),
            ("subject_type", ApprovalCanonicalJson.String(subjectType)),
            ("subject_version", ApprovalCanonicalJson.Number(subjectVersion)),
            ("targets", ApprovalRequirementDefinition.TargetsValue(targets))));
    }

    private static CanonicalValue MappingValue(ApprovalSupersessionMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        return ApprovalCanonicalJson.Object(
            ("materiality_digest", ApprovalCanonicalJson.String(mapping.MaterialityDigest)),
            ("materiality_schema_version", ApprovalCanonicalJson.String(mapping.MaterialitySchemaVersion)),
            ("previous", ApprovalRequirementDefinition.TargetValue(mapping.Previous)),
            ("replacement", ApprovalRequirementDefinition.TargetValue(mapping.Replacement)));
    }

    private static CanonicalValue DependencyValue(ApprovalDependencyRef dependency) => ApprovalCanonicalJson.Object(
        ("mode", ApprovalCanonicalJson.String(dependency.Mode)),
        ("predecessor_key", ApprovalCanonicalJson.String(dependency.PredecessorKey)),
        ("predecessor_kind", ApprovalCanonicalJson.String(dependency.PredecessorKind)),
        ("targets", ApprovalRequirementDefinition.TargetsValue(dependency.Targets)));

    private static Guid RequireIdentity(Guid? identity, string field) =>
        identity is null || identity == Guid.Empty
            ? throw new DomainValidationException($"A carry-forward decision requires its {field}.")
            : identity.Value;
}
