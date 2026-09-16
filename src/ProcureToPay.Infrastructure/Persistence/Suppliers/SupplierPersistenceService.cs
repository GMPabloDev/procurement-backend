using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Domain.Modules.Organization;
using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Domain.Modules.Suppliers;
using ProcureToPay.Domain.SharedKernel;
namespace ProcureToPay.Infrastructure.Persistence.Suppliers;

/// <summary>
/// Versioned persistence of the Supplier Master (SPEC 09 REQ-01, REQ-02, REQ-11). Roots keep the
/// approved pointer and the single open proposal; versions are append-only and every mutation of one
/// supplier is serialized on the same application lock, so a race can only produce one successor.
/// </summary>
public sealed class SupplierPersistenceService(
    ProcureToPayDbContext dbContext,
    ILogger<SupplierPersistenceService> logger)
{
    /// <summary>Serializes every writer of one supplier on the same application lock (REQ-01, NFR-03).</summary>
    public static async Task AcquireSupplierLockAsync(
        ProcureToPayDbContext context,
        Guid organizationId,
        Guid supplierId,
        CancellationToken cancellationToken = default)
    {
        if (context.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                "The supplier lock requires an open transaction that owns the critical section.");
        }

        var resource = $"supplier:root:{organizationId:D}:{supplierId:D}";
        var result = new SqlParameter("@result", System.Data.SqlDbType.Int)
        {
            Direction = System.Data.ParameterDirection.Output
        };
        await context.Database.ExecuteSqlRawAsync(
            "EXEC @result = sp_getapplock @Resource = {0}, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 15000",
            [resource, result],
            cancellationToken);
        if (result.Value is not int code || code < 0)
        {
            throw new SupplierDependencyUnavailableException(
                "The supplier critical section could not be acquired.");
        }
    }

    /// <summary>Commits the critical section, reporting constraint races as contract conflicts (409).</summary>
    public async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception is not DbUpdateConcurrencyException)
        {
            throw new DomainConflictException(
                "The supplier entry was modified concurrently or violates a uniqueness rule.");
        }
    }

    /// <summary>Loads one exact append-only version, verifying that its content digest still reproduces.</summary>
    public async Task<SupplierVersionView> LoadVersionAsync(
        Guid supplierId,
        int version,
        CancellationToken cancellationToken = default)
    {
        var record = await dbContext.SupplierVersions
            .AsNoTracking()
            .SingleOrDefaultAsync(
                row => row.SupplierId == supplierId && row.Version == version,
                cancellationToken)
            ?? throw new SupplierDependencyUnavailableException("The supplier version does not exist.");
        var view = SupplierSerialization.ReadView(record);
        if (!SupplierCanonicalizer.MatchesContentDigest(view))
        {
            throw new SupplierDependencyUnavailableException(
                "The stored supplier version content digest is not reproducible.");
        }

        return view;
    }

    /// <summary>Approved operational version of one supplier, or null when none was ever approved.</summary>
    public async Task<SupplierVersionView?> FindOperationalAsync(
        Guid organizationId,
        Guid supplierId,
        CancellationToken cancellationToken = default)
    {
        var operationalVersion = await dbContext.Suppliers
            .AsNoTracking()
            .Where(supplier => supplier.OrganizationId == organizationId && supplier.Id == supplierId)
            .Select(supplier => supplier.OperationalVersion)
            .SingleOrDefaultAsync(cancellationToken);
        return operationalVersion is null
            ? null
            : await LoadVersionAsync(supplierId, operationalVersion.Value, cancellationToken);
    }

    /// <summary>Open proposal of one supplier, if any (REQ-03: at most one is open).</summary>
    public async Task<SupplierProposalView?> FindOpenProposalAsync(
        Guid organizationId,
        Guid supplierId,
        CancellationToken cancellationToken = default)
    {
        var record = await dbContext.SupplierChangeProposals
            .AsNoTracking()
            .SingleOrDefaultAsync(
                row => row.OrganizationId == organizationId &&
                       row.SupplierId == supplierId &&
                       (row.State == (int)SupplierProposalState.Draft ||
                        row.State == (int)SupplierProposalState.Pending),
                cancellationToken);
        return record is null ? null : ReadProposal(record);
    }

    /// <summary>Minimal, non-sensitive projection of the suppliers usable by a Purchase Request (REQ-11).</summary>
    public async Task<IReadOnlyList<SupplierReferenceView>> ListActiveAsync(
        Guid organizationId,
        CancellationToken cancellationToken = default)
    {
        var rows = await dbContext.Suppliers
            .AsNoTracking()
            .Where(supplier => supplier.OrganizationId == organizationId && supplier.OperationalVersion != null)
            .Select(supplier => new
            {
                supplier.Id,
                Version = supplier.OperationalVersion!.Value,
                supplier.FiscalIdentity.TaxId
            })
            .ToArrayAsync(cancellationToken);
        var references = new List<SupplierReferenceView>(rows.Length);
        foreach (var row in rows)
        {
            var view = await LoadVersionAsync(row.Id, row.Version, cancellationToken);
            if (!SupplierStatusCodes.IsUsable(view.Content.Status))
            {
                continue;
            }

            references.Add(new SupplierReferenceView(
                view.SupplierId,
                view.Version,
                view.Content.LegalName,
                view.Content.TradeName,
                view.Content.SupportedCurrencies,
                view.Content.CategoriesSupplied));
        }

        return references
            .OrderBy(reference => reference.LegalName, StringComparer.Ordinal)
            .ThenBy(reference => reference.Id)
            .ToArray();
    }

    /// <summary>Full administrative history of one supplier: versions, proposals and audit identifiers.</summary>
    public async Task<IReadOnlyList<SupplierVersionView>> ReadHistoryAsync(
        Guid organizationId,
        Guid supplierId,
        CancellationToken cancellationToken = default)
    {
        var exists = await dbContext.Suppliers
            .AsNoTracking()
            .AnyAsync(
                supplier => supplier.OrganizationId == organizationId && supplier.Id == supplierId,
                cancellationToken);
        if (!exists)
        {
            throw new DomainNotFoundException("The supplier does not exist in this organization.");
        }

        var rows = await dbContext.SupplierVersions
            .AsNoTracking()
            .Where(row => row.SupplierId == supplierId)
            .OrderBy(row => row.Version)
            .ToArrayAsync(cancellationToken);
        return [.. rows.Select(SupplierSerialization.ReadView)];
    }

    /// <summary>Transactional write of one immutable version plus its audit entry (REQ-01, REQ-11).</summary>
    public SupplierVersionRecord AppendVersion(
        Guid organizationId,
        Guid supplierId,
        int version,
        int? predecessorVersion,
        SupplierVersionContent content,
        Guid? proposalId,
        string changeKind,
        string changeKey,
        Guid actorUserId,
        string reason,
        DateTimeOffset occurredAt,
        string correlationReference)
    {
        ArgumentNullException.ThrowIfNull(content);
        var digest = SupplierCanonicalizer.ContentDigest(supplierId, version, content);
        var record = new SupplierVersionRecord
        {
            SupplierId = supplierId,
            Version = version,
            OrganizationId = organizationId,
            PredecessorVersion = predecessorVersion,
            LegalName = content.LegalName,
            TradeName = content.TradeName,
            CountryCode = content.CountryCode,
            TaxId = content.TaxId,
            AddressesJson = SupplierSerialization.Addresses(content.Addresses),
            ContactsJson = SupplierSerialization.Contacts(content.Contacts),
            PaymentTermsJson = SupplierSerialization.PaymentTerms(content.PaymentTerms),
            SupportedCurrenciesJson = SupplierSerialization.Currencies(content.SupportedCurrencies),
            CategoriesSuppliedJson = SupplierSerialization.Categories(content.CategoriesSupplied),
            PerformanceJson = SupplierSerialization.Performance(content.PerformanceScore),
            BankingRefsJson = SupplierSerialization.BankingRefs(content.BankingRefs),
            Status = (int)content.Status,
            RiskStatus = (int)content.RiskStatus,
            ContentDigest = digest,
            ProposalId = proposalId,
            ChangeKey = changeKey,
            ActorUserId = actorUserId,
            OccurredAt = occurredAt,
            Reason = reason
        };
        dbContext.SupplierVersions.Add(record);
        logger.LogInformation(
            "Supplier {SupplierId} version {Version} appended ({ChangeKind}).",
            supplierId,
            version,
            changeKind);
        return record;
    }

    /// <summary>
    /// Appends one audit row with its causal identity and an effect key that is unique per
    /// organization, action and effect, so a redelivery can never duplicate the record (REQ-11).
    /// </summary>
    public void AddAudit(
        Guid organizationId,
        string action,
        string actorJson,
        string? causeJson,
        IEnumerable<string> changedFields,
        string correlationReference,
        string effectKey,
        string targetJson,
        DateTimeOffset occurredAt)
    {
        dbContext.SupplierAuditRecords.Add(new SupplierAuditRecord
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            Action = action,
            ActorJson = actorJson,
            CauseJson = causeJson,
            ChangedFieldsJson = System.Text.Json.JsonSerializer.Serialize(
                (changedFields ?? []).Distinct(StringComparer.Ordinal).OrderBy(field => field, StringComparer.Ordinal)),
            CorrelationReference = correlationReference,
            EffectKey = effectKey,
            OccurredAt = occurredAt,
            TargetJson = targetJson
        });
    }

    /// <summary>Diagnostic counts of the module used by readiness: never exposes business data.</summary>
    public async Task<SupplierDiagnostics> DiagnoseAsync(CancellationToken cancellationToken = default)
    {
        var suppliers = await dbContext.Suppliers
            .AsNoTracking()
            .Select(row => new { row.Id, row.OrganizationId, row.OperationalVersion })
            .ToArrayAsync(cancellationToken);
        var corruptedPointers = 0;
        foreach (var supplier in suppliers)
        {
            if (supplier.OperationalVersion is null)
            {
                continue;
            }

            var matches = await dbContext.SupplierVersions
                .AsNoTracking()
                .CountAsync(
                    row => row.SupplierId == supplier.Id && row.Version == supplier.OperationalVersion,
                    cancellationToken);
            if (matches != 1)
            {
                corruptedPointers++;
            }
        }

        return new SupplierDiagnostics(
            suppliers.Length,
            corruptedPointers,
            await dbContext.SupplierAgreementAttachments
                .AsNoTracking()
                .CountAsync(row => row.State == (int)SupplierAgreementAttachmentState.Staged, cancellationToken));
    }

    internal static SupplierProposalView ReadProposal(SupplierChangeProposalRecord record) => new(
        record.Id,
        record.SupplierId,
        record.BaseVersion,
        record.CandidateVersion,
        (SupplierChangeKind)record.ChangeKind,
        ReadSensitiveFields(record.SensitiveFieldsJson),
        record.RequestedStatus is null ? null : (SupplierOperationalStatus)record.RequestedStatus.Value,
        (SupplierProposalState)record.State,
        record.EditorUserId,
        record.ApprovalCaseId,
        record.RequirementKey,
        record.CaseContractVersion,
        record.CreatedAt,
        record.UpdatedAt);

    internal static IReadOnlySet<string> ReadSensitiveFields(string json)
    {
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<string[]>(json)
                       ?.ToHashSet(StringComparer.Ordinal)
                   ?? new HashSet<string>(StringComparer.Ordinal);
        }
        catch (System.Text.Json.JsonException)
        {
            throw new SupplierDependencyUnavailableException("A stored supplier proposal is corrupted.");
        }
    }

    internal static string WriteSensitiveFields(IEnumerable<string> fields) =>
        System.Text.Json.JsonSerializer.Serialize(
            (fields ?? []).Distinct(StringComparer.Ordinal).OrderBy(field => field, StringComparer.Ordinal).ToArray());

    /// <summary>
    /// Approver requirement of the governance flows (REQ-03): <c>PROCUREMENT_APPROVER</c> plus a
    /// single SUPPLIER_MASTER grant. The minimum rank comes from configuration so the organization
    /// can raise the bar without a code change; the default admits every active grant.
    /// </summary>
    public static ApprovalRequirementDefinition ApprovalRequirement(
        string requirementKey,
        string stageCode,
        DecisionScopeDescriptor scope,
        int minimumRank,
        Guid editorUserId,
        Guid originatorId,
        IEnumerable<ApprovalTarget> targets) =>
        new(
            requirementKey,
            stageCode,
            SystemRole.ProcurementApprover,
            AuthorityRequirement.Required(ApprovalAuthorityType.SupplierMaster, minimumRank, null, null),
            scope,
            new[]
            {
                ApprovalDecisionAction.Approve, ApprovalDecisionAction.Reject, ApprovalDecisionAction.RequestChanges
            },
            new[] { editorUserId, originatorId }.Distinct(),
            targets,
            Array.Empty<ApprovalDependencyRef>());
}

/// <summary>Read-only diagnostic of the Supplier module used by readiness (REQ-12).</summary>
public sealed record SupplierDiagnostics(int Suppliers, int CorruptedPointers, int StagedAttachments);
