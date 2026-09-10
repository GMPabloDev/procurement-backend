using ProcureToPay.Infrastructure.Persistence.Organization;

namespace ProcureToPay.Infrastructure.Persistence.Policy;

public sealed class PolicySetVersionRecord
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public long Sequence { get; set; }
    public int Status { get; set; }
    public string ScopesJson { get; set; } = null!;
    public string ContentJson { get; set; } = null!;
    public string ContentDigest { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
    public byte[] RowVersion { get; set; } = [];

    public OrganizationRecord Organization { get; set; } = null!;
    public ICollection<PolicyActivationRecord> Activations { get; } = [];
    public ICollection<PolicyEvaluationBundleRecord> Evaluations { get; } = [];
}

public sealed class PolicyActivationRecord
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid PolicySetVersionId { get; set; }
    public DateTimeOffset EffectiveFrom { get; set; }
    public string ActorType { get; set; } = null!;
    public Guid? ActorUserId { get; set; }
    public string Reason { get; set; } = null!;
    public DateTimeOffset OccurredAt { get; set; }

    public PolicySetVersionRecord PolicySetVersion { get; set; } = null!;
    public PolicyRetirementRecord? Retirement { get; set; }
}

public sealed class PolicyRetirementRecord
{
    public Guid Id { get; set; }
    public Guid PolicyActivationId { get; set; }
    public DateTimeOffset EffectiveTo { get; set; }
    public string ActorType { get; set; } = null!;
    public Guid? ActorUserId { get; set; }
    public string Reason { get; set; } = null!;
    public DateTimeOffset OccurredAt { get; set; }

    public PolicyActivationRecord PolicyActivation { get; set; } = null!;
}

public sealed class PolicyEvaluationBundleRecord
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public string EvaluationKey { get; set; } = null!;
    public string WorkloadIssuer { get; set; } = null!;
    public string WorkloadClientId { get; set; } = null!;
    public string Operation { get; set; } = null!;
    public Guid SubjectId { get; set; }
    public int SubjectVersion { get; set; }
    public Guid PolicySetVersionId { get; set; }
    public DateTimeOffset EvaluatedAt { get; set; }
    public string PolicyContentDigest { get; set; } = null!;
    public string InputDigest { get; set; } = null!;
    public string Result { get; set; } = null!;
    public string ResultDigest { get; set; } = null!;
    public string BundleJson { get; set; } = null!;
    public string IdempotencyFingerprint { get; set; } = null!;
    public Guid? PreviousBundleId { get; set; }
    public string CorrelationReference { get; set; } = null!;
    public byte[] RowVersion { get; set; } = [];

    public PolicySetVersionRecord PolicySetVersion { get; set; } = null!;
}

public sealed class PolicyEvaluationReservationRecord
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public string WorkloadIssuer { get; set; } = null!;
    public string WorkloadClientId { get; set; } = null!;
    public string Operation { get; set; } = null!;
    public string EvaluationKey { get; set; } = null!;
    public Guid SubjectId { get; set; }
    public int SubjectVersion { get; set; }
    public string IdempotencyFingerprint { get; set; } = null!;
    public DateTimeOffset ReservedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

public sealed class PolicyExceptionVerificationRecord
{
    public Guid Id { get; set; }
    public Guid EvaluationBundleId { get; set; }
    public Guid BaseBundleId { get; set; }
    public string WorkflowDecisionId { get; set; } = null!;
    public string TargetRequirementKey { get; set; } = null!;
    public string Binding { get; set; } = null!;
    public string Nonce { get; set; } = null!;
    public string EvidenceDigest { get; set; } = null!;
    public Guid ApproverId { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public string VerifierReference { get; set; } = null!;
    public string SnapshotJson { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
    public byte[] RowVersion { get; set; } = [];
}
