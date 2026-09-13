using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ProcureToPay.Api.Authentication;
using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Domain.SharedKernel;
using ProcureToPay.Infrastructure.Persistence.Approval;

namespace ProcureToPay.Api.Controllers;

/// <summary>
/// Service endpoint that verifies a quotation waiver against the persisted approval decision
/// (SPEC 05 REQ-06). It is intentionally outside <c>/api/v1/approval</c>: only the dedicated
/// <c>approval-workflow</c> service audience and an allowlisted workload can reach it, and no
/// request field is trusted for approver, authority, scope, validity or SoD.
/// </summary>
[ApiController]
[Route("v1/policy-exceptions")]
public sealed class PolicyExceptionVerificationController(
    PolicyExceptionVerificationService verificationService,
    ApprovalWorkloadAllowlist workloadAllowlist) : ControllerBase
{
    [HttpPost("verify")]
    [Authorize(AuthenticationSchemes = ApprovalServiceAuthentication.Scheme)]
    public async Task<ActionResult<WorkflowVerificationResponse>> Verify(
        WorkflowVerificationRequest request,
        CancellationToken cancellationToken)
    {
        var workload = ResolveWorkload();
        workloadAllowlist.EnsureAllowed(workload);
        var outcome = await verificationService.VerifyAsync(request, DateTimeOffset.UtcNow, cancellationToken);
        return Ok(outcome.Response);
    }

    private ApprovalWorkloadIdentity ResolveWorkload()
    {
        var clientId = User.FindFirst("client_id")?.Value ?? User.FindFirst("azp")?.Value;
        if (string.IsNullOrWhiteSpace(clientId))
        {
            throw new DomainForbiddenException("A service workload client identity is required.");
        }

        var issuer = User.FindFirst("iss")?.Value;
        if (string.IsNullOrWhiteSpace(issuer))
        {
            throw new DomainForbiddenException("A service workload issuer is required.");
        }

        return new ApprovalWorkloadIdentity(issuer, clientId);
    }
}
