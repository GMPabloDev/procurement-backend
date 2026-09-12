using ProcureToPay.Domain.Modules.Organization;

namespace ProcureToPay.Domain.Modules.Approval;

/// <summary>
/// Canonical form of <see cref="EligibilityEvidence"/> (SPEC 03 REQ-08, NFR-01): the same
/// resolver inputs and clock must produce the same bytes, so the evidence is serialized with
/// <c>approval-canonical-json/v2</c> instead of a reflection-based serializer whose set order
/// is not contractual.
/// </summary>
public static class ApprovalEligibilityEvidence
{
    public static CanonicalValue ToCanonicalValue(EligibilityEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        return ApprovalCanonicalJson.Object(
            ("assignment_assigned_at", ApprovalCanonicalJson.String(evidence.AssignmentAssignedAt)),
            ("assignment_revoked_at", ApprovalCanonicalJson.StringOrNull(evidence.AssignmentRevokedAt)),
            ("assignment_scope", ApprovalCanonicalJson.String(evidence.AssignmentScopeSnapshot)),
            ("authority_grant_base_currency", ApprovalCanonicalJson.StringOrNull(evidence.AuthorityGrantBaseCurrency)),
            ("authority_grant_id", ApprovalCanonicalJson.StringOrNull(evidence.AuthorityGrantId)),
            ("authority_grant_max_amount_base", ApprovalCanonicalJson.NumberOrNull(evidence.AuthorityGrantMaxAmountBase)),
            ("authority_grant_scope", ApprovalCanonicalJson.StringOrNull(evidence.AuthorityGrantScopeSnapshot)),
            ("authority_grant_valid_from", ApprovalCanonicalJson.StringOrNull(evidence.AuthorityGrantValidFrom)),
            ("authority_grant_valid_to", ApprovalCanonicalJson.StringOrNull(evidence.AuthorityGrantValidTo)),
            ("authority_grant_version", ApprovalCanonicalJson.NumberOrNull(evidence.AuthorityGrantVersion)),
            ("authority_level_code", ApprovalCanonicalJson.StringOrNull(evidence.AuthorityLevelCode)),
            ("authority_level_id", ApprovalCanonicalJson.StringOrNull(evidence.AuthorityLevelId)),
            ("authority_level_rank", ApprovalCanonicalJson.NumberOrNull(evidence.AuthorityLevelRank)),
            ("authority_level_version", ApprovalCanonicalJson.NumberOrNull(evidence.AuthorityLevelVersion)),
            ("authority_requirement", ApprovalRequirementDefinition.AuthorityValue(evidence.AuthorityRequirement)),
            ("evaluated_at", ApprovalCanonicalJson.String(evidence.EvaluatedAt)),
            ("exclusions", ApprovalCanonicalJson.Set(
                evidence.ExcludedUserIds.Select(userId => ApprovalCanonicalJson.String(userId)))),
            ("required_role", ApprovalCanonicalJson.String(evidence.RequiredRole)),
            ("required_scope", ApprovalCanonicalJson.String(evidence.RequiredScopeSnapshot)),
            ("role_assignment_id", ApprovalCanonicalJson.String(evidence.RoleAssignmentId)),
            ("role_assignment_version", ApprovalCanonicalJson.Number(evidence.RoleAssignmentVersion)),
            ("user_id", ApprovalCanonicalJson.String(evidence.UserId)),
            ("user_profile_version", ApprovalCanonicalJson.Number(evidence.UserProfileVersion)));
    }

    /// <summary>Canonical JSON string persisted as the assignment or decision evidence.</summary>
    public static string Canonicalize(EligibilityEvidence evidence) =>
        ApprovalCanonicalJson.Serialize(ToCanonicalValue(evidence));
}
