using Microsoft.EntityFrameworkCore;
using ProcureToPay.Application.Abstractions;
using ProcureToPay.Domain.Modules.Budget;
using ProcureToPay.Infrastructure.Persistence.PurchaseRequests;
using ProcureToPay.Domain.Modules.Organization;
using ProcureToPay.Domain.Modules.PurchaseRequests;
using ProcureToPay.Domain.SharedKernel;
using ProcureToPay.Infrastructure.Persistence.BudgetLedger;

namespace ProcureToPay.Infrastructure.Persistence.BudgetLedger;

/// <summary>Minimal precheck answer published to the requester (REQ-04).</summary>
public sealed record BudgetPrecheckResultView(
    string ContractVersion,
    Guid OperationId,
    Guid RequestId,
    int RequestVersion,
    string ManifestDigest,
    string Result,
    IReadOnlyList<BudgetPrecheckTargetView> Targets);

/// <summary>
/// Server-side orchestration of one precheck by reference (SPEC 08 REQ-04). The requester only
/// supplies the Purchase Request version and an idempotency key; the manifest, the demands and the
/// answer are derived server-side and never expose allocations, buckets or another position.
/// </summary>
public sealed class BudgetPrecheckService(
    ProcureToPayDbContext dbContext,
    BudgetDemandBuilderRegistry builders,
    BudgetLedgerService ledger,
    PurchaseRequests.PurchaseRequestAttestationService attestations)
{
    public async Task<BudgetPrecheckResultView> PrecheckAsync(
        Guid organizationId,
        Guid requestId,
        int requestVersion,
        string? precheckKey,
        string correlationReference,
        CancellationToken cancellationToken = default)
    {
        var key = BudgetCodes.RequireKey(precheckKey, "precheck_key");
        var request = await dbContext.PurchaseRequests
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.Id == requestId && record.OrganizationId == organizationId,
                cancellationToken)
            ?? throw new DomainNotFoundException("The purchase request is not visible.");
        if (requestVersion != request.CurrentVersion)
        {
            throw new DomainConflictException("Only the current version of the purchase request can be prechecked.");
        }

        // A precheck may run before the version is presented; the attestation of SPEC 06 is
        // idempotent, so an already confirmed version reuses its frozen manifest (REQ-04).
        var manifest = await attestations.EnsureAttestedAsync(
            requestId, requestVersion, organizationId, DateTimeOffset.UtcNow, cancellationToken);
        var builder = builders.ResolveExactlyOne(BudgetCodes.PurchaseRequestSourceType, "v1");
        var build = await builder.BuildAsync(
            new BudgetDemandBuildRequest(
                BudgetCodes.DemandBuildVersion,
                organizationId,
                requestId,
                requestVersion,
                manifest.RequestContentDigest,
                manifest.PolicyManifestDigest,
                Targets: null),
            cancellationToken);
        var outcome = await ledger.PrecheckAsync(
            organizationId,
            key,
            build.ManifestDigest,
            new BudgetSource(BudgetCodes.PurchaseRequestSourceType, requestId, requestVersion, build.RequestContentDigest),
            build.Demands,
            correlationReference,
            cancellationToken);
        return new BudgetPrecheckResultView(
            BudgetCodes.PrecheckResponseVersion,
            outcome.OperationId,
            requestId,
            requestVersion,
            build.ManifestDigest,
            outcome.Result,
            outcome.Targets);
    }
}
