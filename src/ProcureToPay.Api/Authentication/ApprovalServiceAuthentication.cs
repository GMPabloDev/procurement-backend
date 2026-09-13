namespace ProcureToPay.Api.Authentication;

/// <summary>
/// Dedicated service authentication of the policy exception verifier (SPEC 05 REQ-06): the
/// workflow endpoint requires a service JWT whose audience is <c>approval-workflow</c>, so the
/// interactive bearer scheme (organization users) can never satisfy it. The issuer and client id
/// must additionally be in the approval workload allowlist.
/// </summary>
public static class ApprovalServiceAuthentication
{
    public const string Scheme = "ApprovalServiceJwt";
    public const string DefaultAudience = "approval-workflow";
}
