namespace ProcureToPay.Domain.Modules.Organization;

public static class OrganizationContractCodes
{
    private static readonly IReadOnlyDictionary<SystemRole, string> Roles = new Dictionary<SystemRole, string>
    {
        [SystemRole.Requester] = "REQUESTER", [SystemRole.DepartmentApprover] = "DEPARTMENT_APPROVER",
        [SystemRole.FinanceApprover] = "FINANCE_APPROVER", [SystemRole.ProcurementBuyer] = "PROCUREMENT_BUYER",
        [SystemRole.ProcurementApprover] = "PROCUREMENT_APPROVER", [SystemRole.ApSpecialist] = "AP_SPECIALIST",
        [SystemRole.PaymentApprover] = "PAYMENT_APPROVER", [SystemRole.ItReviewer] = "IT_REVIEWER",
        [SystemRole.ItProvisioner] = "IT_PROVISIONER", [SystemRole.LegalReviewer] = "LEGAL_REVIEWER",
        [SystemRole.Admin] = "ADMIN", [SystemRole.Auditor] = "AUDITOR"
    };

    private static readonly IReadOnlyDictionary<ApprovalAuthorityType, string> Authorities = new Dictionary<ApprovalAuthorityType, string>
    {
        [ApprovalAuthorityType.BusinessNeed] = "BUSINESS_NEED", [ApprovalAuthorityType.Financial] = "FINANCIAL",
        [ApprovalAuthorityType.Procurement] = "PROCUREMENT", [ApprovalAuthorityType.SupplierMaster] = "SUPPLIER_MASTER",
        [ApprovalAuthorityType.MatchException] = "MATCH_EXCEPTION", [ApprovalAuthorityType.Payment] = "PAYMENT"
    };

    private static readonly IReadOnlyDictionary<ScopeDimension, string> Scopes = new Dictionary<ScopeDimension, string>
    {
        [ScopeDimension.Organization] = "ORGANIZATION", [ScopeDimension.LegalEntity] = "LEGAL_ENTITY",
        [ScopeDimension.Department] = "DEPARTMENT", [ScopeDimension.CostCenter] = "COST_CENTER"
    };

    public static string Role(SystemRole value) => Roles[value];
    public static string Authority(ApprovalAuthorityType value) => Authorities[value];
    public static string Scope(ScopeDimension value) => Scopes[value];
    public static string Status(UserProfileStatus value) => value switch
    {
        UserProfileStatus.PendingSetup => "PENDING_SETUP",
        UserProfileStatus.Active => "ACTIVE",
        UserProfileStatus.Inactive => "INACTIVE",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };
    public static string Status(EntityStatus value) => value.ToString().ToUpperInvariant();
    public static string Status(AssignmentStatus value) => value.ToString().ToUpperInvariant();
    public static string Status(GrantStatus value) => value.ToString().ToUpperInvariant();

    public static bool TryRole(string? value, out SystemRole result) => TryGet(value, Roles, out result);
    public static bool TryAuthority(string? value, out ApprovalAuthorityType result) => TryGet(value, Authorities, out result);
    public static bool TryScope(string? value, out ScopeDimension result) => TryGet(value, Scopes, out result);

    private static bool TryGet<T>(string? value, IReadOnlyDictionary<T, string> values, out T result)
        where T : struct, Enum
    {
        result = default;
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsDigit))
        {
            return false;
        }
        var normalized = value.Trim().ToUpperInvariant();
        foreach (var pair in values)
        {
            if (pair.Value == normalized)
            {
                result = pair.Key;
                return true;
            }
        }
        return false;
    }
}
