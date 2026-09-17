using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Domain.Modules.Sourcing;
using ProcureToPay.Domain.SharedKernel;
using ProcureToPay.Infrastructure.Persistence.Approval;
using ProcureToPay.Infrastructure.Persistence.Policy;

namespace ProcureToPay.Infrastructure.Persistence.Sourcing;

/// <summary>Command that asks to reduce the quotation minimum of one prerequisite (REQ-05).</summary>
public sealed record RequestQuotationWaiverCommand(
    Guid OrganizationId,
    Guid RfqId,
    string RequirementKey,
    string CommandKey);

/// <summary>
/// Quotation waiver requests (SPEC 10 REQ-05). The service recalculates the quotation versions, their
/// evidence digests and the valid counts straight from SQL, persists
/// <c>quotation-waiver-facts/v1</c> and only then uses the SPEC 05 submission contract to open the
/// approval case; the caller never supplies a count, a floor or a binding.
/// </summary>
public sealed class SourcingWaiverService(
    ProcureToPayDbContext dbContext,
    PolicyExceptionSubmissionService submissions,
    IConfiguration configuration)
{
    /// <summary>In-process workload of the sourcing domain; it is the only allowed submitter (REQ-13).</summary>
    public static readonly ApprovalWorkloadIdentity Workload =
        new("internal://procure-to-pay", SourcingCodes.QuotationStatusProcessorId);

    public const string DefaultValidityDays = "7";

    public async Task<SourcingWaiverView> RequestReductionAsync(
        RequestQuotationWaiverCommand command,
        Guid actorUserId,
        string correlationReference,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        _ = SourcingCodes.Key(command.CommandKey, "Command key");
        var requirementKey = SourcingCodes.Key(command.RequirementKey, "requirement_key");
        var (rfq, current, process) = await RequireLiveRfqAsync(
            command.OrganizationId, command.RfqId, cancellationToken);
        var prerequisite = await RequireQuotationPrerequisiteAsync(
            command.OrganizationId, process.RequestId, process.RequestVersion, requirementKey, cancellationToken);
        var parameters = ReadParameters(prerequisite.ParametersJson);
        var targets = ReadTargets(prerequisite.TargetsJson);
        if (parameters.MinimumQuotations is not int minimum || minimum < 1)
        {
            // Without a published minimum there is nothing to reduce; the waiver never invents one.
            throw new DomainConflictException(
                "The quotation prerequisite publishes no reducible minimum.");
        }

        if (parameters.MinimumAllowedQuotations is not int floor)
        {
            throw new DomainConflictException("The quotation requirement is NOT_EXCEPTIONABLE.");
        }

        var lines = SourcingSerialization.ReadRfqLines(current.LinesJson)
            .ToDictionary(line => line.LineRef.Id);
        var counts = await new SourcingQuotationQueries(dbContext).CountValidAsync(
            command.OrganizationId, command.RfqId, cancellationToken);
        var waiverTargets = new List<SourcingWaiverTarget>(targets.Count);
        foreach (var target in targets)
        {
            if (!lines.TryGetValue(target.Id, out var line))
            {
                throw new DomainConflictException(
                    "The prerequisite covers a line outside this RFQ; the waiver cannot be bound.");
            }

            var quotations = await new SourcingQuotationQueries(dbContext).ListValidAsync(
                command.OrganizationId, command.RfqId, target.Id, cancellationToken);
            waiverTargets.Add(new SourcingWaiverTarget(
                line.LineRef,
                quotations.Select(quote => new SourcingContentRef(
                    quote.QuotationId, quote.Version, quote.ContentDigest)).ToArray()));
        }

        var to = waiverTargets.Count == 0 ? 0 : waiverTargets.Min(target => target.ValidQuotations);
        if (counts.Count == 0 && waiverTargets.All(target => target.ValidQuotations == 0))
        {
            throw new DomainConflictException(
                "A waiver cannot cover a trace with zero valid quotations.");
        }

        // The reduction is decided by the recalculation, never by the caller: a shortage that does
        // not exist, or one deeper than the published floor, cannot become a waiver (REQ-05).
        if (to >= minimum)
        {
            throw new DomainConflictException(
                "The valid count already meets the published minimum; there is nothing to reduce.");
        }

        if (to < floor)
        {
            throw new DomainConflictException("The reduction would fall below the persisted floor.");
        }

        var bundle = await RequireBaseBundleAsync(
            command.OrganizationId, process.RequestId, process.RequestVersion, cancellationToken);
        var manifestDigest = await RequireManifestDigestAsync(
            process.RequestId, process.RequestVersion, cancellationToken);
        var facts = new SourcingWaiverFacts(
            command.OrganizationId,
            process.Id,
            new SourcingContentRef(current.RfqId, current.Version, current.ContentDigest),
            prerequisite.Id,
            requirementKey,
            bundle.Id,
            bundle.ResultDigest,
            bundle.PolicySetVersionId,
            bundle.PolicyContentDigest,
            manifestDigest,
            minimum,
            to,
            floor,
            waiverTargets,
            now);
        var replay = await FindFactsAsync(
            command.OrganizationId, actorUserId, command.CommandKey, cancellationToken);
        if (replay is not null)
        {
            if (!string.Equals(replay.ContentDigest, facts.Digest, StringComparison.Ordinal))
            {
                throw new DomainConflictException("The command key was reused with other waiver facts.");
            }

            return Read(replay);
        }

        var projection = Rehydrate(bundle);
        var exceptionTargets = waiverTargets.Select(target =>
        {
            var material = projection.MaterialProjection?.Targets.SingleOrDefault(candidate =>
                candidate.Id == target.LineRef.Id && candidate.Version == target.LineRef.Version)
                ?? throw new ApprovalDependencyUnavailableException(
                    "The base evaluation has no material projection for a covered line.");
            return new PolicyExceptionTarget(
                PolicyApprovalTargets.TargetType, target.LineRef.Id, target.LineRef.Version,
                material.MaterialSnapshotDigest);
        }).ToImmutableArray();
        var validityDays = int.TryParse(
            configuration["Sourcing:Waiver:ValidityDays"], out var configured) && configured > 0
            ? configured
            : int.Parse(DefaultValidityDays, CultureInfo.InvariantCulture);

        var factsDocument = facts.CanonicalDocument();
        SourcingCodes.RequireCanonicalDocument(factsDocument);

        // The facts are persisted before the case exists, so an unavailable Policy dependency leaves
        // audited evidence instead of a silent waiver (REQ-05).
        var record = new SourcingWaiverFactsRecord
        {
            Id = Guid.NewGuid(),
            OrganizationId = command.OrganizationId,
            ProcessId = process.Id,
            RfqId = current.RfqId,
            RfqVersion = current.Version,
            PrerequisiteId = prerequisite.Id,
            RequirementKey = requirementKey,
            BaseBundleId = bundle.Id,
            BaseResultDigest = bundle.ResultDigest,
            PolicyVersionId = bundle.PolicySetVersionId,
            PolicyContentDigest = bundle.PolicyContentDigest,
            ManifestDigest = manifestDigest,
            From = facts.From,
            To = facts.To,
            Floor = facts.Floor,
            TargetsJson = SourcingSerialization.WaiverTargets(facts.Targets),
            DocumentJson = factsDocument,
            ContentDigest = facts.Digest,
            CommandKey = command.CommandKey,
            ActorUserId = actorUserId,
            OccurredAt = now
        };
        dbContext.SourcingWaiverFacts.Add(record);
        await dbContext.SaveChangesAsync(cancellationToken);

        var outcome = await submissions.SubmitAsync(
            new PolicyExceptionSubmission(
                Workload,
                $"waiver:{process.Id:D}:{requirementKey}:{record.Id:D}",
                command.OrganizationId,
                "PURCHASE_REQUEST",
                process.RequestId,
                process.RequestVersion,
                requirementKey,
                exceptionTargets,
                facts.From,
                facts.To,
                facts.Floor,
                record.Id,
                RequesterId: null,
                actorUserId,
                actorUserId,
                now,
                now.AddDays(validityDays),
                Guid.NewGuid().ToString("N"),
                correlationReference,
                bundle.Id,
                bundle.ResultDigest,
                bundle.PolicySetVersionId,
                bundle.PolicyContentDigest,
                manifestDigest),
            now,
            cancellationToken);

        record.ApprovalCaseId = outcome.CaseId;
        record.ApprovalRequirementId = outcome.RequirementId;
        AddAudit(
            command.OrganizationId, SourcingCodes.ActionWaiverRequested, actorUserId, correlationReference,
            $"waiver:{record.Id:D}:requested", now);
        await dbContext.SaveChangesAsync(cancellationToken);
        return Read(record) with { CaseCreated = true };
    }

    public async Task<SourcingWaiverView> GetFactsAsync(
        Guid organizationId,
        Guid factsId,
        CancellationToken cancellationToken = default)
    {
        var record = await dbContext.SourcingWaiverFacts
            .AsNoTracking()
            .SingleOrDefaultAsync(
                row => row.Id == factsId && row.OrganizationId == organizationId,
                cancellationToken)
            ?? throw new DomainNotFoundException("The quotation waiver facts do not exist.");
        return Read(record);
    }

    private static SourcingWaiverView Read(SourcingWaiverFactsRecord record)
    {
        // The persisted bytes must hash back to the recorded digest: a tampered row is refused.
        var facts = SourcingSerialization.ReadWaiverFacts(record.DocumentJson);
        if (!string.Equals(facts.Digest, record.ContentDigest, StringComparison.Ordinal))
        {
            throw new SourcingDependencyUnavailableException("The stored waiver facts are corrupted.");
        }

        return new SourcingWaiverView(
            record.Id,
            record.RequirementKey,
            record.From,
            record.To,
            record.Floor,
            record.ContentDigest,
            record.ApprovalCaseId,
            record.ApprovalCaseId is not null,
            facts.Targets,
            record.ActorUserId,
            record.OccurredAt);
    }

    private async Task<ApprovalPrerequisiteRecord> RequireQuotationPrerequisiteAsync(
        Guid organizationId,
        Guid requestId,
        int requestVersion,
        string requirementKey,
        CancellationToken cancellationToken)
    {
        var caseId = await dbContext.ApprovalCases
            .AsNoTracking()
            .Where(record =>
                record.OrganizationId == organizationId &&
                record.SubjectType == "PURCHASE_REQUEST" &&
                record.SubjectId == requestId &&
                record.SubjectVersion == requestVersion)
            .OrderByDescending(record => record.Version)
            .Select(record => record.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (caseId == Guid.Empty)
        {
            throw new DomainConflictException(
                "The purchase request has no current approval case to reduce.");
        }

        return await dbContext.ApprovalPrerequisites
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.CaseId == caseId &&
                          record.OwnerAdapterId == SourcingCodes.QuotationStatusOwnerAdapterId &&
                          record.OwnerAdapterVersion == SourcingCodes.OwnerAdapterVersion &&
                          record.Key == requirementKey,
                cancellationToken)
            ?? throw new DomainNotFoundException("The quotation prerequisite does not exist in the case.");
    }

    /// <summary>
    /// Base evaluation the waiver reduces: the newest persisted Policy evaluation of the presented
    /// request version. Absence fails closed instead of binding a waiver to nothing (REQ-05).
    /// </summary>
    private async Task<PolicyEvaluationBundleRecord> RequireBaseBundleAsync(
        Guid organizationId,
        Guid requestId,
        int requestVersion,
        CancellationToken cancellationToken) =>
        await dbContext.PolicyEvaluationBundles
            .AsNoTracking()
            .Where(record =>
                record.OrganizationId == organizationId &&
                record.SubjectId == requestId &&
                record.SubjectVersion == requestVersion)
            .OrderByDescending(record => record.EvaluationSequence)
            .FirstOrDefaultAsync(cancellationToken)
        ?? throw new DomainConflictException(
            "The purchase request version has no persisted policy evaluation to reduce.");

    private async Task<string> RequireManifestDigestAsync(
        Guid requestId,
        int requestVersion,
        CancellationToken cancellationToken)
    {
        var manifest = await dbContext.PurchaseRequestCompletenessManifests
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.RequestId == requestId && record.RequestVersion == requestVersion,
                cancellationToken)
            ?? throw new DomainConflictException(
                "The purchase request version has no confirmed completeness manifest.");
        return manifest.PolicyManifestDigest;
    }

    private static PolicyEvaluationBundle Rehydrate(PolicyEvaluationBundleRecord record)
    {
        try
        {
            return PolicyEvaluationBundleRehydrator.FromJson(record.BundleJson);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or
                                             InvalidOperationException or FormatException)
        {
            throw new SourcingDependencyUnavailableException("The persisted policy evaluation is corrupted.");
        }
    }

    private static (int? MinimumQuotations, int? MinimumAllowedQuotations) ReadParameters(string parametersJson)
    {
        try
        {
            using var document = JsonDocument.Parse(parametersJson);
            return (
                ReadInt(document.RootElement, "minimum_quotations"),
                ReadInt(document.RootElement, "minimum_allowed_quotations"));
        }
        catch (JsonException)
        {
            throw new SourcingDependencyUnavailableException("The quotation prerequisite is corrupted.");
        }
    }

    private static int? ReadInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : null;

    private static IReadOnlyList<SourcingQuotationTarget> ReadTargets(string targetsJson)
    {
        try
        {
            using var document = JsonDocument.Parse(targetsJson);
            return document.RootElement.EnumerateArray()
                .Select(target => new SourcingQuotationTarget(
                    target.GetProperty("id").GetGuid(),
                    target.GetProperty("type").GetString()!,
                    target.GetProperty("version").GetInt32()))
                .ToArray();
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new SourcingDependencyUnavailableException("The quotation prerequisite targets are corrupted.");
        }
    }

    private async Task<(RfqRecord Rfq, RfqVersionRecord Current, SourcingProcessRecord Process)> RequireLiveRfqAsync(
        Guid organizationId,
        Guid rfqId,
        CancellationToken cancellationToken)
    {
        var rfq = await dbContext.Rfqs
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.Id == rfqId && record.OrganizationId == organizationId,
                cancellationToken)
            ?? throw new DomainNotFoundException("The RFQ does not exist in this organization.");
        var current = await dbContext.RfqVersions
            .AsNoTracking()
            .SingleAsync(record => record.RfqId == rfqId && record.Version == rfq.CurrentVersion, cancellationToken);
        if ((RfqStatus)current.Status is RfqStatus.Draft or RfqStatus.Cancelled)
        {
            throw new DomainConflictException("This RFQ cannot request a quotation waiver.");
        }

        var process = await dbContext.SourcingProcesses
            .AsNoTracking()
            .SingleOrDefaultAsync(record => record.Id == rfq.ProcessId, cancellationToken)
            ?? throw new SourcingDependencyUnavailableException("The RFQ has no sourcing process.");
        if (process.State == (int)SourcingProcessState.Cancelled)
        {
            throw new DomainConflictException("A cancelled sourcing process cannot request a waiver.");
        }

        return (rfq, current, process);
    }

    private async Task<SourcingWaiverFactsRecord?> FindFactsAsync(
        Guid organizationId,
        Guid actorUserId,
        string commandKey,
        CancellationToken cancellationToken) =>
        await dbContext.SourcingWaiverFacts
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.OrganizationId == organizationId &&
                          record.ActorUserId == actorUserId &&
                          record.CommandKey == commandKey,
                cancellationToken);

    private void AddAudit(
        Guid organizationId,
        string action,
        Guid actorUserId,
        string correlationReference,
        string effectKey,
        DateTimeOffset occurredAt) =>
        dbContext.SourcingAuditRecords.Add(new SourcingAuditRecord
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            Action = action,
            ActorJson = $"{{\"system_id\":null,\"type\":\"USER\",\"user_id\":\"{actorUserId:D}\"," +
                        "\"workload_client_id\":null,\"workload_issuer\":null}}",
            ChangedFieldsJson = "[\"waiver\"]",
            CorrelationReference = correlationReference,
            EffectKey = effectKey,
            OccurredAt = occurredAt,
            TargetJson = $"{{\"id\":\"{effectKey}\",\"type\":\"SourcingWaiverFacts\",\"version\":1}}"
        });
}
