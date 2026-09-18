using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Domain.Modules.PurchaseOrders;

/// <summary>
/// One requested assignment of a Purchase Order line (REQ-06): the responsibility kind and the user
/// the Buyer selected. The default candidate is the attested Requested For of the Purchase Request
/// line, and choosing another active user of the same organization requires a recorded reason.
/// </summary>
public sealed record AcceptanceAssignmentRequest(string Kind, Guid UserId, int UserVersion, string? Reason)
{
    public string Kind { get; } = PurchaseOrderCodes.Code(Kind, "Responsibility kind");

    public int UserVersion { get; } = UserVersion >= 1
        ? UserVersion
        : throw new DomainValidationException("A responsibility candidate requires a positive version.");
}

/// <summary>
/// Builder of the Acceptance Responsibility set of one line (REQ-06, DEC-06). It never invents a
/// responsibility: the required kinds come from the attested purchase type of the Purchase Request
/// line, and the candidate is the attested Requested For unless the Buyer selects another user.
/// </summary>
public static class AcceptanceResponsibilityBuilder
{
    /// <summary>Reasons recorded for the two admissible selections of one responsibility.</summary>
    public const string ReasonRequestedFor = "REQUESTED_FOR";

    public static IReadOnlyList<AcceptanceResponsibility> Build(
        PurchaseOrderContentRef lineRef,
        string purchaseType,
        PurchaseOrderEntityRef requestedForCandidate,
        IEnumerable<AcceptanceAssignmentRequest>? assignments,
        Guid assignedByUserId,
        DateTimeOffset assignedAt,
        int version = 1)
    {
        ArgumentNullException.ThrowIfNull(lineRef);
        ArgumentNullException.ThrowIfNull(requestedForCandidate);
        var required = PurchaseOrderCodes.RequiredResponsibilities(purchaseType);
        var requested = (assignments ?? []).ToArray();
        var byKind = new Dictionary<string, AcceptanceAssignmentRequest>(StringComparer.Ordinal);
        foreach (var assignment in requested)
        {
            if (!byKind.TryAdd(assignment.Kind, assignment))
            {
                throw new DomainConflictException("A line receives exactly one responsibility per kind.");
            }
        }

        foreach (var kind in required)
        {
            if (!byKind.ContainsKey(kind))
            {
                throw new PurchaseOrderUnprocessableException(
                    $"The line of type {purchaseType} requires the responsibility {kind}.");
            }
        }

        foreach (var kind in byKind.Keys)
        {
            if (!required.Contains(kind, StringComparer.Ordinal))
            {
                throw new PurchaseOrderUnprocessableException(
                    $"The responsibility {kind} does not apply to a line of type {purchaseType}.");
            }
        }

        return required
            .Select(kind =>
            {
                var assignment = byKind[kind];
                var principal = new PurchaseOrderEntityRef(assignment.UserId, assignment.UserVersion);
                var isCandidate = principal.Id == requestedForCandidate.Id &&
                                  principal.Version == requestedForCandidate.Version;
                if (!isCandidate && string.IsNullOrWhiteSpace(assignment.Reason))
                {
                    throw new PurchaseOrderUnprocessableException(
                        "Selecting another user than the Requested For requires a recorded reason.");
                }

                return new AcceptanceResponsibility(
                    kind,
                    lineRef,
                    principal,
                    requestedForCandidate,
                    assignedByUserId,
                    isCandidate ? ReasonRequestedFor : PurchaseOrderCodes.Reason(assignment.Reason),
                    assignedAt,
                    version);
            })
            .ToArray();
    }

    /// <summary>
    /// REQ-06: before issuing, every line carries exactly the responsibilities its purchase type
    /// requires, so a missing assignment fails with <c>422</c> instead of publishing an order.
    /// </summary>
    public static void RequireComplete(
        PurchaseOrderLine line,
        string purchaseType,
        string lineIdentity)
    {
        var required = PurchaseOrderCodes.RequiredResponsibilities(purchaseType);
        var present = line.AcceptanceResponsibilities
            .Select(responsibility => responsibility.Kind)
            .OrderBy(kind => kind, StringComparer.Ordinal)
            .ToArray();
        var expected = required.OrderBy(kind => kind, StringComparer.Ordinal).ToArray();
        if (!present.SequenceEqual(expected, StringComparer.Ordinal))
        {
            throw new PurchaseOrderUnprocessableException(
                $"The line '{lineIdentity}' does not carry exactly the responsibilities of its purchase type.");
        }
    }
}
