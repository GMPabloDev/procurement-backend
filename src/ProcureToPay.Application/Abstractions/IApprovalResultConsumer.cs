namespace ProcureToPay.Application.Abstractions;

/// <summary>
/// One durable approval result handed to a consumer (REQ-10). The payload is delivered exactly
/// as it was persisted; consumers deduplicate by <c>event_id + contract_version</c>, so the
/// dispatcher never rewrites or re-serializes it.
/// </summary>
public sealed record ApprovalResultDelivery(
    Guid EventId,
    string ContractVersion,
    string PayloadJson,
    string CorrelationReference);

/// <summary>
/// Consumer of approval results. Implementations must be idempotent per
/// <c>event_id + contract_version</c> because delivery is at-least-once.
/// </summary>
public interface IApprovalResultConsumer
{
    string ContractVersion { get; }

    Task DeliverAsync(
        ApprovalResultDelivery delivery,
        CancellationToken cancellationToken = default);
}

/// <summary>Exactly-one consumer per result contract version; ambiguous or absent fails closed.</summary>
public interface IApprovalResultConsumerRegistry
{
    IApprovalResultConsumer ResolveExactlyOne(string contractVersion);
}
