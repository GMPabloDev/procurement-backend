using ProcureToPay.Infrastructure.Persistence.Organization;

namespace ProcureToPay.Infrastructure.Persistence.ReferenceCatalogs;

/// <summary>
/// Durable root of one Cost Center (REQ-01): stable identity plus the current version pointer.
/// Historical versions are append-only rows; the pointer never rewrites them.
/// </summary>
public sealed class CostCenterRecord
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public string Code { get; set; } = null!;
    public int CurrentVersion { get; set; }
    public byte[] RowVersion { get; set; } = [];

    public OrganizationRecord Organization { get; set; } = null!;
    public ICollection<CostCenterVersionRecord> Versions { get; } = [];
}

/// <summary>
/// Append-only version of a Cost Center (REQ-01, REQ-02). The stored relation is the stable
/// <c>department_id</c>; the owning department version is resolved when projecting or attesting.
/// </summary>
public sealed class CostCenterVersionRecord
{
    public Guid CostCenterId { get; set; }
    public int Version { get; set; }
    public Guid OrganizationId { get; set; }
    public string Name { get; set; } = null!;
    public Guid DepartmentId { get; set; }
    public int Status { get; set; }
    public int? PredecessorVersion { get; set; }
    public Guid ActorUserId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public string Reason { get; set; } = null!;

    public CostCenterRecord CostCenter { get; set; } = null!;
    public DepartmentRecord Department { get; set; } = null!;
}

/// <summary>Durable root of one Spend Category (REQ-01, REQ-03): the code is its public identity.</summary>
public sealed class SpendCategoryRecord
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public string Code { get; set; } = null!;
    public int CurrentVersion { get; set; }
    public byte[] RowVersion { get; set; } = [];

    public OrganizationRecord Organization { get; set; } = null!;
    public ICollection<SpendCategoryVersionRecord> Versions { get; } = [];
}

/// <summary>Append-only version of a Spend Category with its reproducible digest (REQ-03).</summary>
public sealed class SpendCategoryVersionRecord
{
    public Guid SpendCategoryId { get; set; }
    public int Version { get; set; }
    public Guid OrganizationId { get; set; }
    public string Code { get; set; } = null!;
    public string Name { get; set; } = null!;
    public string Digest { get; set; } = null!;
    public int Status { get; set; }
    public int? PredecessorVersion { get; set; }
    public Guid ActorUserId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public string Reason { get; set; } = null!;

    public SpendCategoryRecord SpendCategory { get; set; } = null!;
}
