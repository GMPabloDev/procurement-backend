using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ProcureToPay.Application.Abstractions;
using ProcureToPay.Domain.Modules.Budget;
using ProcureToPay.Domain.Modules.Organization;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Infrastructure.Persistence.PurchaseRequests;

/// <summary>
/// Real in-process builder of the budget demands of one confirmed Purchase Request version
/// (SPEC 08 REQ-04, REQ-05). It reads the immutable line versions and the references frozen by the
/// SPEC 06 attestation, so neither the precheck nor the Approval projection can be fed with a
/// caller-supplied position, amount or currency.
/// </summary>
public sealed class PurchaseRequestBudgetDemandBuilder(ProcureToPayDbContext dbContext) : IBudgetDemandBuilder
{
    public string SourceType => BudgetCodes.PurchaseRequestSourceType;

    public string ContractVersion => "v1";

    public async Task<BudgetDemandBuildResult> BuildAsync(
        BudgetDemandBuildRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var organizationId = BudgetCodes.RequireIdentity(request.OrganizationId, "Organization");
        var requestId = BudgetCodes.RequireIdentity(request.RequestId, "Request");
        if (request.RequestVersion < 1)
        {
            throw new DomainValidationException("The request version must be positive.");
        }

        var manifest = await dbContext.PurchaseRequestCompletenessManifests
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.OrganizationId == organizationId &&
                          record.RequestId == requestId &&
                          record.RequestVersion == request.RequestVersion,
                cancellationToken)
            ?? throw new BudgetReferenceInvalidException(
                "The purchase request version has no confirmed completeness manifest.");
        // The manifest is authoritative: a caller that already knows the request content digest must
        // still match it, and a caller that does not carries no way to substitute it (REQ-04).
        if ((request.RequestContentDigest is not null &&
             !string.Equals(
                 manifest.RequestContentDigest, request.RequestContentDigest, StringComparison.Ordinal)) ||
            !string.Equals(manifest.PolicyManifestDigest, request.ManifestDigest, StringComparison.Ordinal))
        {
            throw new BudgetReferenceInvalidException(
                "The confirmed purchase request manifest does not match the requested identity.");
        }

        var lineRefs = PurchaseRequestSerialization.ReadLineRefs(manifest.LinesJson);
        var targetSet = request.Targets is null or { Count: 0 }
            ? null
            : request.Targets
                .GroupBy(target => target.Id)
                .ToDictionary(group => group.Key, group => group.ToArray());
        if (targetSet is not null)
        {
            foreach (var group in targetSet.Values)
            {
                if (group.Length > 1)
                {
                    throw new BudgetReferenceInvalidException("A budget target cannot be requested twice.");
                }
            }
        }

        var baseCurrency = await dbContext.Organizations
            .AsNoTracking()
            .Where(organization => organization.Id == organizationId)
            .Select(organization => organization.BaseCurrency)
            .SingleAsync(cancellationToken);
        var demands = new List<BudgetDemand>();
        foreach (var lineRef in lineRefs.OrderBy(reference => reference.Id))
        {
            if (targetSet is not null &&
                (!targetSet.TryGetValue(lineRef.Id, out var requested) ||
                 requested[0].Version != lineRef.Version))
            {
                continue;
            }

            var line = await dbContext.PurchaseRequestLineVersions
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    record => record.OrganizationId == organizationId &&
                              record.RequestId == requestId &&
                              record.LineId == lineRef.Id &&
                              record.LineVersion == lineRef.Version,
                    cancellationToken)
                ?? throw new BudgetReferenceInvalidException(
                    "The purchase request line version confirmed by the manifest is missing.");
            var spendCategory = ReadSpendCategory(line.SpendCategoryJson);
            var costCenter = ReadCostCenter(line.CostCenterJson);
            var payload = BudgetPositionPayload.For(
                costCenter.Id,
                BudgetCodes.RequireFiscalYear(line.FiscalYear),
                costCenter,
                spendCategory);
            if (!string.Equals(line.BaseCurrency, baseCurrency, StringComparison.Ordinal))
            {
                throw new BudgetReferenceInvalidException(
                    "The purchase request line is not expressed in the organization base currency.");
            }

            demands.Add(new BudgetDemand(
                line.BaseAmount,
                payload,
                line.LineId,
                line.LineVersion,
                lineRef.ContentDigest,
                request.Targets is null
                    ? null
                    : new BudgetTarget(
                        lineRef.Id,
                        lineRef.Version,
                        request.Targets.Single(target => target.Id == lineRef.Id).MaterialSnapshotDigest,
                        request.Targets.Single(target => target.Id == lineRef.Id).Type)));
        }

        if (demands.Count == 0)
        {
            throw new BudgetReferenceInvalidException(
                "The purchase request version covers no line for a budget check.");
        }

        return new BudgetDemandBuildResult(
            ContractVersion,
            organizationId,
            requestId,
            request.RequestVersion,
            manifest.RequestContentDigest,
            manifest.PolicyManifestDigest,
            baseCurrency,
            demands);
    }

    private static BudgetCostCenterRef ReadCostCenter(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        return new BudgetCostCenterRef(
            root.GetProperty("id").GetGuid(),
            root.GetProperty("version").GetInt32());
    }

    private static BudgetSpendCategoryRef ReadSpendCategory(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        return new BudgetSpendCategoryRef(
            root.GetProperty("code").GetString() ?? string.Empty,
            root.GetProperty("version").GetInt32(),
            root.GetProperty("digest").GetString() ?? string.Empty);
    }
}

/// <summary>
/// Exact-one registry of budget demand builders (REQ-04): zero or several matches for the same
/// <c>source_type + contract_version</c> fail closed before any budget operation exists.
/// </summary>
public sealed class BudgetDemandBuilderRegistry(IEnumerable<IBudgetDemandBuilder> builders)
{
    private readonly IReadOnlyList<IBudgetDemandBuilder> registered = builders.ToArray();

    public IBudgetDemandBuilder ResolveExactlyOne(string sourceType, string contractVersion)
    {
        ArgumentNullException.ThrowIfNull(builders);
        var normalizedSource = BudgetCodes.RequireBoundedToken(sourceType, "Source type");
        var normalizedVersion = BudgetCodes.RequireKey(contractVersion, "contract_version");
        var matches = registered
            .Where(builder =>
                string.Equals(builder.SourceType, normalizedSource, StringComparison.Ordinal) &&
                string.Equals(builder.ContractVersion, normalizedVersion, StringComparison.Ordinal))
            .ToArray();
        if (matches.Length == 0)
        {
            throw new BudgetDependencyUnavailableException(
                "No budget demand builder is registered for the requested source.");
        }

        if (matches.Length > 1)
        {
            throw new BudgetDependencyUnavailableException(
                "More than one budget demand builder matches the requested source.");
        }

        return matches[0];
    }
}

/// <summary>Current organization of the request, used by the API to derive the tenant (REQ-10).</summary>
public sealed class OrganizationContext(ProcureToPayDbContext dbContext)
{
    public Task<Guid> OrganizationIdAsync(CancellationToken cancellationToken = default) =>
        dbContext.Organizations.AsNoTracking().Select(record => record.Id).SingleAsync(cancellationToken);
}
