using System.Collections.Immutable;
using System.Globalization;
using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Domain.Modules.Sourcing;

/// <summary>
/// Evidence of one satisfied <c>quotation-status-owner/v1</c> prerequisite
/// (<c>quotation-status-evidence/v1</c>, REQ-13). It carries the recalculated count of every target,
/// the quotation versions counted and the waiver verification it used, so the signal is reproducible
/// from SQL instead of from the worker's word.
/// </summary>
public sealed record QuotationStatusEvidence
{
    public const string ContractVersion = SourcingCodes.QuotationStatusEvidenceContract;

    public QuotationStatusEvidence(
        Guid attemptId,
        Guid prerequisiteId,
        Guid organizationId,
        string parametersDigest,
        string signalKey,
        DateTimeOffset checkedAt,
        IReadOnlyDictionary<Guid, int> countsByTarget,
        int minimumValid,
        IReadOnlyList<SourcingContentRef> quotationVersions,
        IReadOnlyList<ApprovalTargetView> targets,
        string? waiverVerificationDigest)
    {
        if (attemptId == Guid.Empty || prerequisiteId == Guid.Empty || organizationId == Guid.Empty)
        {
            throw new DomainValidationException("A quotation evidence requires its complete identity.");
        }

        AttemptId = attemptId;
        PrerequisiteId = prerequisiteId;
        OrganizationId = organizationId;
        ParametersDigest = SourcingCodes.Digest(parametersDigest, "Parameters digest");
        SignalKey = SourcingCodes.Key(signalKey, "signal_key");
        CheckedAt = checkedAt.ToUniversalTime();
        var counts = new SortedDictionary<Guid, int>(
            (countsByTarget ?? new Dictionary<Guid, int>()).ToDictionary(pair => pair.Key, pair => pair.Value));
        if (counts.Count == 0 || counts.Any(pair => pair.Value < 0))
        {
            throw new DomainValidationException("Quotation evidence requires its per-target counts.");
        }

        CountsByTarget = counts;
        MinimumValid = minimumValid >= 1
            ? minimumValid
            : throw new DomainValidationException("Quotation evidence requires its minimum.");
        QuotationVersions = (quotationVersions ?? [])
            .OrderBy(reference => reference.Id)
            .ThenBy(reference => reference.Version)
            .ToImmutableArray();
        Targets = (targets ?? [])
            .OrderBy(target => target.Id)
            .ToImmutableArray();
        WaiverVerificationDigest = waiverVerificationDigest is null
            ? null
            : SourcingCodes.Digest(waiverVerificationDigest, "Waiver verification digest");
        if (Targets.Count == 0 ||
            !CountsByTarget.Select(pair => pair.Key).ToHashSet().SetEquals(Targets.Select(target => target.Id)))
        {
            throw new DomainConflictException("Quotation evidence must count exactly its covered targets.");
        }

        Digest = ComputeDigest();
    }

    public Guid AttemptId { get; }
    public Guid PrerequisiteId { get; }
    public Guid OrganizationId { get; }
    public string ParametersDigest { get; }
    public string SignalKey { get; }
    public DateTimeOffset CheckedAt { get; }
    public IReadOnlyDictionary<Guid, int> CountsByTarget { get; }
    public int MinimumValid { get; }
    public IReadOnlyList<SourcingContentRef> QuotationVersions { get; }
    public IReadOnlyList<ApprovalTargetView> Targets { get; }
    public string? WaiverVerificationDigest { get; }
    public string Digest { get; }

    public string ComputeDigest() => PolicyCanonicalizer.Hash(CanonicalDocument());

    public string CanonicalDocument()
    {
        var preimage = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["attempt_id"] = AttemptId.ToString("D"),
            ["canonicalization_version"] = PolicyCanonicalizer.Version,
            ["checked_at"] = SourcingCodes.FormatUtc(CheckedAt),
            ["contract_version"] = ContractVersion,
            ["counts_by_target"] = SourcingCanonicalizer.Set(CountsByTarget.Select(pair =>
                (object?)new SortedDictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["count"] = pair.Value,
                    ["target_id"] = pair.Key.ToString("D")
                })),
            ["minimum_valid"] = MinimumValid,
            ["organization_id"] = OrganizationId.ToString("D"),
            ["parameters_digest"] = ParametersDigest,
            ["prerequisite_id"] = PrerequisiteId.ToString("D"),
            ["quotation_versions"] = SourcingCanonicalizer.Set(QuotationVersions.Select(reference =>
                (object?)SourcingCanonicalizer.ContentRef(reference))),
            ["result"] = "SATISFIED",
            ["signal_key"] = SignalKey,
            ["targets"] = SourcingCanonicalizer.Set(Targets.Select(target => (object?)TargetDocument(target))),
            ["waiver_verification_digest"] = WaiverVerificationDigest
        };
        return PolicyCanonicalizer.SerializeCanonical(preimage);
    }

    internal static SortedDictionary<string, object?> TargetDocument(ApprovalTargetView target) =>
        new(StringComparer.Ordinal)
        {
            ["id"] = target.Id.ToString("D"),
            ["material_snapshot_digest"] = target.MaterialSnapshotDigest,
            ["type"] = target.Type,
            ["version"] = target.Version
        };
}

/// <summary>
/// Evidence of one satisfied <c>procurement-stage-owner/v1</c> prerequisite
/// (<c>procurement-stage-evidence/v1</c>, REQ-13): the published awards that cover its targets and the
/// Policy bundles that approved them.
/// </summary>
public sealed record ProcurementStageEvidence
{
    public const string ContractVersion = SourcingCodes.ProcurementStageEvidenceContract;

    public ProcurementStageEvidence(
        Guid attemptId,
        Guid prerequisiteId,
        Guid organizationId,
        string parametersDigest,
        string signalKey,
        DateTimeOffset checkedAt,
        IReadOnlyList<SourcingContentRef> awardRefs,
        IReadOnlyList<SourcingContentRef> policyBundleRefs,
        IReadOnlyList<ApprovalTargetView> targets)
    {
        if (attemptId == Guid.Empty || prerequisiteId == Guid.Empty || organizationId == Guid.Empty)
        {
            throw new DomainValidationException("A procurement evidence requires its complete identity.");
        }

        AttemptId = attemptId;
        PrerequisiteId = prerequisiteId;
        OrganizationId = organizationId;
        ParametersDigest = SourcingCodes.Digest(parametersDigest, "Parameters digest");
        SignalKey = SourcingCodes.Key(signalKey, "signal_key");
        CheckedAt = checkedAt.ToUniversalTime();
        AwardRefs = (awardRefs ?? []).OrderBy(reference => reference.Id).ToImmutableArray();
        PolicyBundleRefs = (policyBundleRefs ?? []).OrderBy(reference => reference.Id).ToImmutableArray();
        Targets = (targets ?? []).OrderBy(target => target.Id).ToImmutableArray();
        if (Targets.Count == 0)
        {
            throw new DomainValidationException("Procurement evidence requires its covered targets.");
        }

        Digest = ComputeDigest();
    }

    public Guid AttemptId { get; }
    public Guid PrerequisiteId { get; }
    public Guid OrganizationId { get; }
    public string ParametersDigest { get; }
    public string SignalKey { get; }
    public DateTimeOffset CheckedAt { get; }
    public IReadOnlyList<SourcingContentRef> AwardRefs { get; }
    public IReadOnlyList<SourcingContentRef> PolicyBundleRefs { get; }
    public IReadOnlyList<ApprovalTargetView> Targets { get; }
    public string Digest { get; }

    public string ComputeDigest() => PolicyCanonicalizer.Hash(CanonicalDocument());

    public string CanonicalDocument()
    {
        var preimage = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["attempt_id"] = AttemptId.ToString("D"),
            ["award_refs"] = SourcingCanonicalizer.Set(AwardRefs.Select(reference =>
                (object?)SourcingCanonicalizer.ContentRef(reference))),
            ["canonicalization_version"] = PolicyCanonicalizer.Version,
            ["checked_at"] = SourcingCodes.FormatUtc(CheckedAt),
            ["contract_version"] = ContractVersion,
            ["organization_id"] = OrganizationId.ToString("D"),
            ["parameters_digest"] = ParametersDigest,
            ["policy_bundle_refs"] = SourcingCanonicalizer.Set(PolicyBundleRefs.Select(reference =>
                (object?)SourcingCanonicalizer.ContentRef(reference))),
            ["prerequisite_id"] = PrerequisiteId.ToString("D"),
            ["result"] = "SATISFIED",
            ["signal_key"] = SignalKey,
            ["targets"] = SourcingCanonicalizer.Set(Targets.Select(target =>
                (object?)QuotationStatusEvidence.TargetDocument(target)))
        };
        return PolicyCanonicalizer.SerializeCanonical(preimage);
    }
}

/// <summary>One approval target of a prerequisite, as the owner reads it back (REQ-13).</summary>
public sealed record ApprovalTargetView
{
    public ApprovalTargetView(string type, Guid id, int version, string materialSnapshotDigest)
    {
        Type = SourcingCodes.Code(type, "Target type");
        if (id == Guid.Empty)
        {
            throw new DomainValidationException("An approval target requires its identity.");
        }

        if (version < 1)
        {
            throw new DomainValidationException("An approval target requires its version.");
        }

        Id = id;
        Version = version;
        MaterialSnapshotDigest = SourcingCodes.Digest(materialSnapshotDigest, "Material snapshot digest");
    }

    public string Type { get; }
    public Guid Id { get; }
    public int Version { get; }
    public string MaterialSnapshotDigest { get; }
}

/// <summary>Parsed quotation parameters of one prerequisite (SPEC 05 REQ-03 projection).</summary>
public sealed record QuotationPrerequisiteParameters(int MinimumQuotations, int? MinimumAllowedQuotations)
{
    public const string PendingOwnerAdapterId = SourcingCodes.QuotationStatusOwnerAdapterId;

    public static QuotationPrerequisiteParameters Parse(string parametersJson)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(parametersJson);
            var root = document.RootElement;
            var names = root.EnumerateObject().Select(property => property.Name)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
            if (!names.SequenceEqual(["minimum_allowed_quotations", "minimum_quotations"], StringComparer.Ordinal) ||
                !root.TryGetProperty("minimum_quotations", out var minimum) ||
                minimum.ValueKind != System.Text.Json.JsonValueKind.Number)
            {
                throw new DomainValidationException(
                    "The quotation prerequisite parameters have an unknown shape.");
            }

            var allowance = root.GetProperty("minimum_allowed_quotations");
            return new QuotationPrerequisiteParameters(
                minimum.GetInt32(),
                allowance.ValueKind == System.Text.Json.JsonValueKind.Number ? allowance.GetInt32() : null);
        }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or KeyNotFoundException or
                                              InvalidOperationException or FormatException)
        {
            throw new DomainValidationException("The quotation prerequisite parameters are not readable.");
        }
    }

    /// <summary>Persisted floor of the reduction, which a waiver can never cross (REQ-05).</summary>
    public int Floor => MinimumAllowedQuotations ?? 1;
}
