using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProcureToPay.Domain.Modules.Organization;
using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Domain.Modules.Suppliers;
using ProcureToPay.Domain.SharedKernel;
using ProcureToPay.Infrastructure.Persistence;
using ProcureToPay.Infrastructure.Persistence.Organization;
using ProcureToPay.Infrastructure.Persistence.Suppliers;

namespace ProcureToPay.Api.Controllers;

/// <summary>
/// Supplier Master of the organization (SPEC 09 REQ-02, REQ-03, REQ-05, REQ-11). A PROCUREMENT_BUYER
/// drafts and submits governed changes, PROCUREMENT_APPROVER decides them through Approval Workflow,
/// AP reads what invoicing needs and any active user reads the minimal projection used to build a
/// Purchase Request. ADMIN never gains business approval or business read by its role.
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/suppliers")]
public sealed class SupplierController(
    ProcureToPayDbContext dbContext,
    CurrentUserProvisioningService provisioningService,
    SupplierGovernanceService governance,
    SupplierPersistenceService persistence,
    SupplierBankingService banking,
    ApprovedSupplierCatalogGovernanceService catalog) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<SupplierVersionResponse>> Create(
        SaveSupplierRequest request,
        CancellationToken cancellationToken)
    {
        var actor = await RequireRoleAsync(SystemRole.ProcurementBuyer, cancellationToken);
        var organizationId = await OrganizationIdAsync(cancellationToken);
        var outcome = await governance.CreateAsync(
            organizationId,
            actor.Id,
            request.ToContent(),
            request.RequestedStatus ?? SupplierOperationalStatus.Active,
            request.Reason,
            request.ChangeKey,
            HttpContext.TraceIdentifier,
            cancellationToken);
        return Created(
            $"/api/v1/suppliers/{outcome.Version.SupplierId}",
            ToResponse(outcome));
    }

    [HttpPut("{supplierId:guid}")]
    public async Task<ActionResult<SupplierVersionResponse>> Save(
        Guid supplierId,
        SaveSupplierRequest request,
        CancellationToken cancellationToken)
    {
        var actor = await RequireRoleAsync(SystemRole.ProcurementBuyer, cancellationToken);
        var organizationId = await OrganizationIdAsync(cancellationToken);
        if (request.ExpectedVersion is not int expectedVersion)
        {
            throw new DomainValidationException("Saving a supplier requires its expected version.");
        }

        var outcome = await governance.SaveAsync(
            organizationId,
            actor.Id,
            supplierId,
            expectedVersion,
            request.ToContent(),
            request.RequestedStatus,
            request.Reason,
            request.ChangeKey,
            HttpContext.TraceIdentifier,
            cancellationToken);
        return Ok(ToResponse(outcome));
    }

    [HttpPost("{supplierId:guid}/versions/{candidateVersion:int}/submit")]
    public async Task<ActionResult<SupplierSubmitResponse>> Submit(
        Guid supplierId,
        int candidateVersion,
        SubmitSupplierRequest request,
        CancellationToken cancellationToken)
    {
        var actor = await RequireRoleAsync(SystemRole.ProcurementBuyer, cancellationToken);
        var organizationId = await OrganizationIdAsync(cancellationToken);
        var outcome = await governance.SubmitAsync(
            organizationId,
            actor.Id,
            supplierId,
            candidateVersion,
            request.Reason,
            request.SubmissionKey,
            HttpContext.TraceIdentifier,
            cancellationToken);
        return Ok(new SupplierSubmitResponse(
            outcome.ProposalId, outcome.CandidateVersion, outcome.CaseId, outcome.Replayed));
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<SupplierReferenceView>>> List(CancellationToken cancellationToken)
    {
        await RequireActiveUserAsync(cancellationToken);
        var organizationId = await OrganizationIdAsync(cancellationToken);
        return Ok(await persistence.ListActiveAsync(organizationId, cancellationToken));
    }

    [HttpGet("{supplierId:guid}")]
    public async Task<ActionResult<SupplierDetailResponse>> Get(
        Guid supplierId,
        CancellationToken cancellationToken)
    {
        var actor = await RequireActiveUserAsync(cancellationToken);
        var organizationId = await OrganizationIdAsync(cancellationToken);
        var projection = await governance.ReadProjectionAsync(organizationId, supplierId, cancellationToken);
        var operational = projection.Operational;
        var role = await HighestBusinessRoleAsync(actor.Id, cancellationToken);
        var organizationScoped = await HasAnyOrganizationRoleAsync(
            actor.Id,
            [
                SystemRole.ApSpecialist, SystemRole.Auditor, SystemRole.ProcurementBuyer,
                SystemRole.ProcurementApprover
            ],
            cancellationToken);
        if (organizationScoped &&
            role is SystemRole.ApSpecialist or SystemRole.Auditor or SystemRole.ProcurementBuyer or
                SystemRole.ProcurementApprover)
        {
            return Ok(new SupplierDetailResponse(
                supplierId,
                projection.Status,
                operational?.Version,
                operational?.Content.LegalName,
                operational?.Content.TradeName,
                operational?.Content.CountryCode,
                operational?.Content.TaxId,
                operational?.Content.RiskStatus.ToString().ToUpperInvariant(),
                operational?.Content.PerformanceScore?.Score,
                operational?.Content.PaymentTerms.NetDays,
                projection.OpenProposal is null ? null : ToProposalResponse(projection.OpenProposal),
                await banking.ListMaskedAsync(organizationId, supplierId, cancellationToken)));
        }

        // SPEC 09 REQ-11: any other active business user only sees the minimal reference projection.
        var minimal = (await persistence.ListActiveAsync(organizationId, cancellationToken))
            .SingleOrDefault(reference => reference.Id == supplierId)
            ?? throw new DomainNotFoundException("The supplier is not visible.");
        return Ok(new SupplierDetailResponse(
            supplierId,
            projection.Status,
            minimal.Version,
            minimal.LegalName,
            minimal.TradeName,
            null,
            null,
            null,
            null,
            null,
            null,
            []));
    }

    [HttpGet("{supplierId:guid}/history")]
    public async Task<ActionResult<IReadOnlyList<SupplierHistoryEntry>>> History(
        Guid supplierId,
        CancellationToken cancellationToken)
    {
        var actor = await RequireActiveUserAsync(cancellationToken);
        var organizationId = await OrganizationIdAsync(cancellationToken);
        await RequireProcurementOrAuditorAsync(actor.Id, cancellationToken);
        var history = await persistence.ReadHistoryAsync(organizationId, supplierId, cancellationToken);
        return Ok(history
            .Select(view => new SupplierHistoryEntry(
                view.Version,
                view.Status,
                view.Content.LegalName,
                view.PredecessorVersion,
                view.OccurredAt,
                view.Reason))
            .ToArray());
    }

    [HttpPost("{supplierId:guid}/banking")]
    public async Task<ActionResult<SupplierBankingRef>> SaveBanking(
        Guid supplierId,
        SaveSupplierBankingRequest request,
        CancellationToken cancellationToken)
    {
        var actor = await RequireRoleAsync(SystemRole.ProcurementBuyer, cancellationToken);
        var organizationId = await OrganizationIdAsync(cancellationToken);
        var reference = await banking.SaveAsync(
            organizationId,
            supplierId,
            actor.Id,
            request.BankingDetailId,
            request.ToContent(),
            request.Reason,
            request.ChangeKey,
            HttpContext.TraceIdentifier,
            cancellationToken);
        return Ok(reference);
    }

    [HttpGet("{supplierId:guid}/banking")]
    public async Task<ActionResult<IReadOnlyList<SupplierBankingMaskedView>>> ListBanking(
        Guid supplierId,
        CancellationToken cancellationToken)
    {
        var actor = await RequireActiveUserAsync(cancellationToken);
        var organizationId = await OrganizationIdAsync(cancellationToken);
        await RequireProcurementApOrAuditorAsync(actor.Id, cancellationToken);
        return Ok(await banking.ListMaskedAsync(organizationId, supplierId, cancellationToken));
    }

    [HttpPost("{supplierId:guid}/banking/{bankingDetailId:guid}/versions/{version:int}/reveal")]
    public async Task<ActionResult<SupplierBankingContent>> RevealBanking(
        Guid supplierId,
        Guid bankingDetailId,
        int version,
        RevealBankingRequest request,
        CancellationToken cancellationToken)
    {
        var actor = await RequireActiveUserAsync(cancellationToken);
        var organizationId = await OrganizationIdAsync(cancellationToken);
        var content = await banking.RevealAsync(
            organizationId,
            actor.Id,
            bankingDetailId,
            version,
            request.Purpose,
            HttpContext.TraceIdentifier,
            cancellationToken);
        return Ok(content);
    }

    private static SupplierVersionResponse ToResponse(SupplierSaveOutcome outcome) => new(
        outcome.Version.SupplierId,
        outcome.Version.Version,
        outcome.ProjectedStatus,
        outcome.RequiresApproval,
        outcome.ProposalId,
        outcome.SensitiveFields.OrderBy(field => field, StringComparer.Ordinal).ToArray());

    private static SupplierProposalResponse ToProposalResponse(SupplierProposalView proposal) => new(
        proposal.Id,
        proposal.CandidateVersion,
        SupplierApprovalTargets.ProposalStateCode(proposal.State),
        proposal.SensitiveFields.OrderBy(field => field, StringComparer.Ordinal).ToArray(),
        proposal.RequestedStatus is null ? null : SupplierStatusCodes.Code(proposal.RequestedStatus.Value),
        proposal.ApprovalCaseId);

    private async Task<UserProfileRecord> RequireActiveUserAsync(CancellationToken cancellationToken)
    {
        var profile = await provisioningService.EnsureProfileAsync(User, cancellationToken);
        if (profile.Status != (int)UserProfileStatus.Active)
        {
            throw new DomainForbiddenException("The local user profile is not active.");
        }

        return profile;
    }

    private async Task<UserProfileRecord> RequireRoleAsync(
        SystemRole role,
        CancellationToken cancellationToken) =>
        await provisioningService.RequireRoleAsync(User, role, cancellationToken);

    private async Task RequireProcurementOrAuditorAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (await HasAnyOrganizationRoleAsync(
                userId,
                [SystemRole.ProcurementBuyer, SystemRole.ProcurementApprover, SystemRole.Auditor],
                cancellationToken))
        {
            return;
        }

        throw new DomainForbiddenException("The actor cannot read the supplier history.");
    }

    private async Task RequireProcurementApOrAuditorAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (await HasAnyOrganizationRoleAsync(
                userId,
                [
                    SystemRole.ProcurementBuyer, SystemRole.ProcurementApprover, SystemRole.ApSpecialist,
                    SystemRole.Auditor
                ],
                cancellationToken))
        {
            return;
        }

        throw new DomainForbiddenException("The actor cannot read the supplier banking projection.");
    }

    /// <summary>
    /// Role with an organization assignment (SPEC 09 REQ-11): the master and the banking projection
    /// are organization-scoped, so a narrower assignment never grants them.
    /// </summary>
    private Task<bool> HasAnyOrganizationRoleAsync(
        Guid userId,
        SystemRole[] roles,
        CancellationToken cancellationToken) =>
        dbContext.RoleAssignments
            .AsNoTracking()
            .AnyAsync(
                assignment => assignment.UserProfileId == userId &&
                              roles.Contains((SystemRole)assignment.Role) &&
                              assignment.Status == (int)AssignmentStatus.Active &&
                              assignment.ScopeJson == "[{\"dimension\":\"ORGANIZATION\",\"reference\":null}]",
                cancellationToken);

    private async Task<SystemRole> HighestBusinessRoleAsync(Guid userId, CancellationToken cancellationToken)
    {
        var roles = await dbContext.RoleAssignments
            .AsNoTracking()
            .Where(assignment => assignment.UserProfileId == userId &&
                                 assignment.Status == (int)AssignmentStatus.Active)
            .Select(assignment => assignment.Role)
            .ToArrayAsync(cancellationToken);
        foreach (var candidate in new[]
                 {
                     SystemRole.ProcurementApprover, SystemRole.ProcurementBuyer, SystemRole.ApSpecialist,
                     SystemRole.Auditor
                 })
        {
            if (roles.Contains((int)candidate))
            {
                return candidate;
            }
        }

        return SystemRole.Requester;
    }

    private Task<Guid> OrganizationIdAsync(CancellationToken cancellationToken) =>
        dbContext.Organizations.AsNoTracking().Select(record => record.Id).SingleAsync(cancellationToken);
}

/// <summary>Minimal, business-facing projection of one supplier version (REQ-11).</summary>
public sealed record SupplierVersionResponse(
    Guid SupplierId,
    int Version,
    string Status,
    bool RequiresApproval,
    Guid? ProposalId,
    IReadOnlyList<string> SensitiveFields);

public sealed record SupplierSubmitResponse(Guid ProposalId, int CandidateVersion, Guid CaseId, bool Replayed);

public sealed record SupplierProposalResponse(
    Guid ProposalId,
    int? CandidateVersion,
    string State,
    IReadOnlyList<string> SensitiveFields,
    string? RequestedStatus,
    Guid? ApprovalCaseId);

public sealed record SupplierDetailResponse(
    Guid SupplierId,
    string Status,
    int? Version,
    string? LegalName,
    string? TradeName,
    string? CountryCode,
    string? TaxId,
    string? RiskStatus,
    decimal? PerformanceScore,
    int? PaymentNetDays,
    SupplierProposalResponse? OpenProposal,
    IReadOnlyList<SupplierBankingMaskedView> Banking);

public sealed record SupplierHistoryEntry(
    int Version,
    string Status,
    string LegalName,
    int? PredecessorVersion,
    DateTimeOffset OccurredAt,
    string Reason);

/// <summary>Exact wire shape accepted by create and save (REQ-02, REQ-04).</summary>
public sealed record SaveSupplierRequest(
    string? LegalName,
    string? TradeName,
    string? CountryCode,
    string? TaxId,
    IReadOnlyList<SupplierAddressRequest>? Addresses,
    IReadOnlyList<SupplierContactRequest>? Contacts,
    SupplierPaymentTermsRequest? PaymentTerms,
    IReadOnlyList<string>? SupportedCurrencies,
    IReadOnlyList<SupplierCategoryRequest>? CategoriesSupplied,
    string? RiskStatus,
    decimal? PerformanceScore,
    string? PerformanceSource,
    DateTimeOffset? PerformanceMeasuredAt,
    IReadOnlyList<SupplierBankingRefRequest>? BankingRefs,
    SupplierOperationalStatus? RequestedStatus,
    int? ExpectedVersion,
    string? Reason,
    string? ChangeKey)
{
    private static SupplierRiskStatus ParseRiskStatus(string? value) =>
        Enum.TryParse<SupplierRiskStatus>(value, ignoreCase: true, out var parsed)
            ? parsed
            : throw new DomainValidationException("The supplier risk status is invalid.");

    public SupplierVersionContent ToContent() => new(
        LegalName ?? string.Empty,
        TradeName,
        CountryCode ?? string.Empty,
        TaxId ?? string.Empty,
        (Addresses ?? []).Select(address => new SupplierAddress(
            address.Id ?? Guid.NewGuid(),
            address.Label ?? string.Empty,
            address.Line1 ?? string.Empty,
            address.Line2,
            address.City ?? string.Empty,
            address.Region,
            address.PostalCode,
            address.CountryCode ?? string.Empty)),
        (Contacts ?? []).Select(contact => new SupplierContact(
            contact.Id ?? Guid.NewGuid(),
            contact.Name ?? string.Empty,
            contact.Email,
            contact.Phone,
            contact.JobTitle)),
        new SupplierPaymentTerms(
            PaymentTerms?.Code ?? string.Empty,
            PaymentTerms?.NetDays ?? 0),
        SupportedCurrencies ?? [],
        (CategoriesSupplied ?? []).Select(category => new VersionedCodeRef(
            "SPEND_CATEGORY",
            category.Code ?? string.Empty,
            category.Version,
            category.Digest ?? string.Empty)),
        // The server owns the materialized status: a candidate is always DRAFT and the requested
        // status travels as its own command field (REQ-02, REQ-03).
        SupplierOperationalStatus.Draft,
        ParseRiskStatus(RiskStatus),
        PerformanceScore is null || PerformanceMeasuredAt is null || string.IsNullOrWhiteSpace(PerformanceSource)
            ? null
            : new SupplierPerformanceScore(
                PerformanceScore.Value,
                PerformanceSource,
                PerformanceMeasuredAt.Value),
        (BankingRefs ?? []).Select(reference => new SupplierBankingRef(
            reference.Id ?? Guid.Empty,
            reference.Version)));
}

public sealed record SupplierAddressRequest(
    Guid? Id,
    string? Label,
    string? Line1,
    string? Line2,
    string? City,
    string? Region,
    string? PostalCode,
    string? CountryCode);

public sealed record SupplierContactRequest(
    Guid? Id,
    string? Name,
    string? Email,
    string? Phone,
    string? JobTitle);

public sealed record SupplierPaymentTermsRequest(string? Code, int NetDays);

public sealed record SupplierCategoryRequest(string? Code, int Version, string? Digest);

public sealed record SupplierBankingRefRequest(Guid? Id, int Version);

public sealed record SubmitSupplierRequest(string? Reason, string? SubmissionKey);

public sealed record SaveSupplierBankingRequest(
    Guid? BankingDetailId,
    string? AccountHolder,
    string? BankName,
    string? BankCountryCode,
    string? Currency,
    string? AccountType,
    string? AccountNumber,
    string? Iban,
    string? SwiftBic,
    bool IsDefault,
    string? Reason,
    string? ChangeKey)
{
    public SupplierBankingContent ToContent()
    {
        if (!Enum.TryParse<SupplierBankingAccountType>(AccountType, ignoreCase: true, out var accountType))
        {
            throw new DomainValidationException("The banking account type is invalid.");
        }

        return new SupplierBankingContent(
            AccountHolder ?? string.Empty,
            BankName ?? string.Empty,
            BankCountryCode ?? string.Empty,
            Currency ?? string.Empty,
            accountType,
            AccountNumber,
            Iban,
            SwiftBic,
            IsDefault);
    }
}

public sealed record RevealBankingRequest(string? Purpose);
