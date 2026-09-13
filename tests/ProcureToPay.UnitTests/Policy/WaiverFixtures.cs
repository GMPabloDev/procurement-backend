using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Domain.Modules.Policy;

namespace ProcureToPay.UnitTests.Policy;

/// <summary>Contract-shaped waiver fixtures for SPEC 05 aware tests.</summary>
internal static class WaiverFixtures
{
    public static readonly Guid OrganizationId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid BundleId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    public static readonly Guid PolicyVersionId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    public static readonly Guid ReferenceId = Guid.Parse("eeeeeeee-1111-1111-1111-111111111111");
    public static readonly Guid RequesterId = Guid.Parse("77777777-7777-7777-7777-777777777777");
    public static readonly Guid OriginatorId = Guid.Parse("88888888-8888-8888-8888-888888888888");
    public static readonly Guid WorkloadSubjectId = Guid.Parse("99999999-9999-9999-9999-999999999999");
    public static readonly Guid DecisionId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

    public static PolicyExceptionBindingRequest Binding(
        Guid organizationId,
        string subjectType,
        Guid subjectId,
        int subjectVersion,
        string baseResultDigest,
        string policyContentDigest,
        string targetRequirementKey,
        IEnumerable<PolicyExceptionTarget> coveredLines,
        int from = 3,
        int to = 2,
        int floor = 1,
        string manifestDigest = null!,
        string nonce = "nonce-1") => new(
        organizationId,
        subjectType,
        subjectId,
        subjectVersion,
        BundleId,
        baseResultDigest,
        PolicyVersionId,
        policyContentDigest,
        manifestDigest ?? new string('9', 64),
        targetRequirementKey,
        coveredLines,
        from,
        to,
        floor,
        ReferenceId,
        RequesterId,
        OriginatorId,
        WorkloadSubjectId,
        DateTimeOffset.Parse("2026-09-13T11:59:00.0000000Z"),
        DateTimeOffset.Parse("2026-10-01T12:00:00.0000000Z"),
        nonce);

    public static QuotationWaiverRequest Request(
        PolicyExceptionBindingRequest binding,
        string evidenceDigest,
        Guid? decisionId = null,
        int decisionVersion = 1) => new(
        PolicyExceptionType.ReduceMinValidQuotations,
        binding,
        decisionId ?? DecisionId,
        decisionVersion,
        evidenceDigest,
        "corr-1");

    public static PolicyExceptionTarget Target(Guid lineId, int version, string digest) =>
        new("PURCHASE_REQUEST_LINE", lineId, version, digest);
}
