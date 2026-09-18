using System.Text.Json;
using ProcureToPay.Application.Abstractions;
using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Infrastructure.Persistence.Approval;

/// <summary>
/// Strict reader of the shared approval result payload (SPEC 03 REQ-09, SPEC 04 REQ-09). Every
/// consumer parses the delivered bytes through it, so an unreadable payload never becomes a partial
/// projection and the contract version is verified against its delivery.
/// </summary>
internal sealed record ApprovalResultEvent(
    Guid EventId,
    string ContractVersion,
    Guid CaseId,
    Guid OrganizationId,
    string SubjectType,
    Guid SubjectId,
    int SubjectVersion,
    string TargetType,
    Guid TargetId,
    int TargetVersion,
    string MaterialSnapshotDigest,
    string SourceType,
    Guid SourceId,
    string? SourceKey,
    string Result,
    Guid? DecisionId,
    Guid? PreviousCaseId,
    DateTimeOffset OccurredAt)
{
    public bool IsLifecycle =>
        string.Equals(ContractVersion, ApprovalEvolutionCodes.LifecycleContractVersion, StringComparison.Ordinal);

    public static ApprovalResultEvent Parse(ApprovalResultDelivery delivery)
    {
        using var document = JsonDocument.Parse(delivery.PayloadJson);
        var root = document.RootElement;
        var contract = root.GetProperty("contract_version").GetString() ?? string.Empty;
        if (!string.Equals(contract, delivery.ContractVersion, StringComparison.Ordinal))
        {
            throw new DomainValidationException("The payload contract version does not match its delivery.");
        }

        var target = root.GetProperty("target");
        string sourceType;
        Guid sourceId;
        string? sourceKey;
        if (root.TryGetProperty("result_source", out var sourceElement) &&
            sourceElement.ValueKind == JsonValueKind.Object)
        {
            sourceType = sourceElement.GetProperty("type").GetString() ?? string.Empty;
            sourceId = sourceElement.GetProperty("id").GetGuid();
            sourceKey = sourceElement.TryGetProperty("key", out var keyElement) &&
                        keyElement.ValueKind == JsonValueKind.String
                ? keyElement.GetString()
                : null;
        }
        else
        {
            // Lifecycle events carry the superseded case as their source (REQ-09).
            sourceType = ApprovalEntitySource.CodeOf(ApprovalEntitySourceType.ApprovalCase);
            sourceId = root.GetProperty("previous_case_id").GetGuid();
            sourceKey = null;
        }

        return new ApprovalResultEvent(
            root.GetProperty("event_id").GetGuid(),
            contract,
            root.GetProperty("case_id").GetGuid(),
            root.GetProperty("organization_id").GetGuid(),
            root.GetProperty("subject_type").GetString() ?? string.Empty,
            root.GetProperty("subject_id").GetGuid(),
            root.GetProperty("subject_version").GetInt32(),
            target.GetProperty("type").GetString() ?? string.Empty,
            target.GetProperty("id").GetGuid(),
            target.GetProperty("version").GetInt32(),
            target.GetProperty("material_snapshot_digest").GetString() ?? string.Empty,
            sourceType,
            sourceId,
            sourceKey,
            root.GetProperty("result").GetString() ?? string.Empty,
            ReadGuidOrNull(root, "decision_id"),
            ReadGuidOrNull(root, "previous_case_id"),
            DateTimeOffset.Parse(
                root.GetProperty("occurred_at").GetString() ?? string.Empty,
                System.Globalization.CultureInfo.InvariantCulture));
    }

    private static Guid? ReadGuidOrNull(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String &&
        Guid.TryParse(element.GetString(), out var value)
            ? value
            : null;
}
