using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ProcureToPay.Application.Abstractions;
using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Domain.Modules.Organization;
using ProcureToPay.Domain.Modules.Suppliers;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Infrastructure.Persistence.Suppliers;

/// <summary>Plaintext banking content of one version (REQ-05). Never persisted in clear.</summary>
public sealed record SupplierBankingContent(
    string AccountHolder,
    string BankName,
    string BankCountryCode,
    string Currency,
    SupplierBankingAccountType AccountType,
    string? AccountNumber,
    string? Iban,
    string? SwiftBic,
    bool IsDefault)
{
    /// <summary>Canonical document of <c>supplier-banking-details/v1</c>; the plaintext preimage.</summary>
    public string CanonicalJson()
    {
        var root = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["account_holder"] = AccountHolder,
            ["account_number"] = AccountNumber,
            ["account_type"] = AccountType.ToString().ToUpperInvariant(),
            ["bank_country_code"] = BankCountryCode,
            ["bank_name"] = BankName,
            ["canonicalization_version"] = ProcureToPay.Domain.Modules.Policy.PolicyCanonicalizer.Version,
            ["contract_version"] = SupplierCodes.SupplierBankingDetailsContract,
            ["currency"] = Currency,
            ["iban"] = Iban,
            ["is_default"] = IsDefault,
            ["swift_bic"] = SwiftBic
        };
        return ProcureToPay.Domain.Modules.Policy.PolicyCanonicalizer.SerializeCanonical(root);
    }

    public static SupplierBankingContent Parse(string canonicalJson)
    {
        using var document = JsonDocument.Parse(canonicalJson);
        var root = document.RootElement;
        if (!root.TryGetProperty("contract_version", out var contract) ||
            contract.GetString() != SupplierCodes.SupplierBankingDetailsContract)
        {
            throw new SupplierDependencyUnavailableException("The stored banking document is not readable.");
        }

        return new SupplierBankingContent(
            root.GetProperty("account_holder").GetString() ?? string.Empty,
            root.GetProperty("bank_name").GetString() ?? string.Empty,
            root.GetProperty("bank_country_code").GetString() ?? string.Empty,
            root.GetProperty("currency").GetString() ?? string.Empty,
            Enum.Parse<SupplierBankingAccountType>(
                root.GetProperty("account_type").GetString() ?? string.Empty, ignoreCase: true),
            Optional(root, "account_number"),
            Optional(root, "iban"),
            Optional(root, "swift_bic"),
            root.GetProperty("is_default").GetBoolean());
    }

    private static string? Optional(JsonElement root, string property) =>
        root.TryGetProperty(property, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;
}

/// <summary>
/// Encrypted persistence and minimal disclosure of supplier banking data (SPEC 09 REQ-05). Only the
/// masked projection is readable without the key provider and an explicit, audited reveal opened to
/// the AP specialist or to the approver who holds the exact pending task.
/// </summary>
public sealed class SupplierBankingService(
    ProcureToPayDbContext dbContext,
    SupplierPersistenceService persistence,
    ISupplierBankingKeyProvider keys,
    ILogger<SupplierBankingService> logger)
{
    /// <summary>Purpose codes admissible in a banking reveal audit (REQ-05).</summary>
    private static readonly IReadOnlySet<string> RevealPurposes = new HashSet<string>(StringComparer.Ordinal)
    {
        "PAYMENT_EXECUTION",
        "APPROVAL_VERIFICATION"
    };

    /// <summary>
    /// Appends one encrypted banking version and, when requested, makes it the default of its
    /// currency. The caller never supplies ciphertext, nonce, tag, key version or the masked suffix.
    /// </summary>
    public async Task<SupplierBankingRef> SaveAsync(
        Guid organizationId,
        Guid supplierId,
        Guid actorUserId,
        Guid? bankingDetailId,
        SupplierBankingContent content,
        string reason,
        string changeKey,
        string correlationReference,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var normalizedReason = SupplierCodes.Reason(reason);
        _ = SupplierCodes.Key(changeKey, "Change key");
        if (string.IsNullOrWhiteSpace(content.AccountNumber) && string.IsNullOrWhiteSpace(content.Iban))
        {
            throw new DomainValidationException("A banking detail requires an account number or an IBAN.");
        }

        if (content.AccountNumber is not null)
        {
            _ = SupplierCodes.DisplayName(content.AccountNumber, "Account number", 64);
        }

        if (content.Iban is not null)
        {
            _ = SupplierCodes.DisplayName(content.Iban, "IBAN", 64);
        }

        if (content.SwiftBic is not null)
        {
            _ = SupplierCodes.DisplayName(content.SwiftBic, "SWIFT/BIC", 16);
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        await SupplierPersistenceService.AcquireSupplierLockAsync(
            dbContext, organizationId, supplierId, cancellationToken);
        var supplierExists = await dbContext.Suppliers
            .AsNoTracking()
            .AnyAsync(
                supplier => supplier.OrganizationId == organizationId && supplier.Id == supplierId,
                cancellationToken);
        if (!supplierExists)
        {
            throw new DomainNotFoundException("The supplier does not exist in this organization.");
        }

        var detailId = bankingDetailId ?? Guid.NewGuid();
        var latest = await dbContext.SupplierBankingVersions
            .Where(row => row.BankingDetailId == detailId && row.OrganizationId == organizationId)
            .MaxAsync(row => (int?)row.Version, cancellationToken);
        var version = (latest ?? 0) + 1;
        if (bankingDetailId is null)
        {
            dbContext.SupplierBankingDetails.Add(new SupplierBankingDetailRecord
            {
                Id = detailId,
                OrganizationId = organizationId,
                SupplierId = supplierId,
                CreatedAt = DateTimeOffset.UtcNow
            });
        }
        else if (latest is null)
        {
            throw new DomainNotFoundException("The banking detail does not exist in this organization.");
        }

        var associatedData = AssociatedData(organizationId, supplierId, detailId, version);
        var plaintext = NormalizedForStorage(content);
        var ciphertext = keys.Encrypt(plaintext.CanonicalJson(), associatedData, out var nonce, out var tag);
        var record = new SupplierBankingVersionRecord
        {
            BankingDetailId = detailId,
            Version = version,
            OrganizationId = organizationId,
            SupplierId = supplierId,
            AccountHolder = plaintext.AccountHolder,
            BankName = plaintext.BankName,
            BankCountryCode = plaintext.BankCountryCode,
            Currency = plaintext.Currency,
            AccountType = (int)plaintext.AccountType,
            MaskedSuffix = Mask(plaintext),
            Ciphertext = ciphertext,
            Nonce = nonce,
            Tag = tag,
            KeyVersion = keys.KeyVersion,
            PredecessorVersion = latest,
            ActorUserId = actorUserId,
            OccurredAt = DateTimeOffset.UtcNow,
            Reason = normalizedReason,
            ChangeKey = changeKey
        };
        dbContext.SupplierBankingVersions.Add(record);
        if (plaintext.IsDefault)
        {
            var current = await dbContext.SupplierBankingDefaults
                .SingleOrDefaultAsync(
                    row => row.SupplierId == supplierId && row.Currency == plaintext.Currency,
                    cancellationToken);
            if (current is null)
            {
                dbContext.SupplierBankingDefaults.Add(new SupplierBankingDefaultRecord
                {
                    SupplierId = supplierId,
                    Currency = plaintext.Currency,
                    BankingDetailId = detailId,
                    Version = version
                });
            }
            else
            {
                current.BankingDetailId = detailId;
                current.Version = version;
            }
        }

        persistence.AddAudit(
            organizationId, SupplierCodes.ActionUpdated, Actor(actorUserId), null,
            ["banking_details"], correlationReference, $"{changeKey}:banking",
            $"{{\"id\":\"{supplierId:D}\",\"type\":\"{SupplierCodes.TargetSupplier}\",\"version\":{version}}}",
            record.OccurredAt);
        await persistence.SaveAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation(
            "Supplier {SupplierId} banking detail {DetailId} version {Version} stored.",
            supplierId, detailId, version);
        return new SupplierBankingRef(detailId, version);
    }

    /// <summary>Masked current projection of one supplier: never carries the account number.</summary>
    public async Task<IReadOnlyList<SupplierBankingMaskedView>> ListMaskedAsync(
        Guid organizationId,
        Guid supplierId,
        CancellationToken cancellationToken = default)
    {
        var defaults = await dbContext.SupplierBankingDefaults
            .AsNoTracking()
            .Where(row => row.SupplierId == supplierId)
            .ToArrayAsync(cancellationToken);
        var rows = await dbContext.SupplierBankingVersions
            .AsNoTracking()
            .Where(row => row.OrganizationId == organizationId && row.SupplierId == supplierId)
            .ToArrayAsync(cancellationToken);
        var latest = rows
            .GroupBy(row => row.BankingDetailId)
            .Select(group => group.OrderByDescending(row => row.Version).First())
            .OrderBy(row => row.BankName, StringComparer.Ordinal)
            .ThenBy(row => row.BankingDetailId)
            .ToArray();
        return latest.Select(row => new SupplierBankingMaskedView(
            row.BankingDetailId,
            row.Version,
            row.BankName,
            row.BankCountryCode,
            row.Currency,
            (SupplierBankingAccountType)row.AccountType,
            defaults.Any(item => item.BankingDetailId == row.BankingDetailId && item.Version == row.Version),
            row.MaskedSuffix)).ToArray();
    }

    /// <summary>
    /// Explicit, audited reveal of one exact version (REQ-05). It is limited to an AP specialist of
    /// the organization and to the approver who currently holds the pending task of the proposal
    /// that carries the version.
    /// </summary>
    public async Task<SupplierBankingContent> RevealAsync(
        Guid organizationId,
        Guid actorUserId,
        Guid bankingDetailId,
        int version,
        string purpose,
        string correlationReference,
        CancellationToken cancellationToken = default)
    {
        if (purpose is null || !RevealPurposes.Contains(purpose))
        {
            throw new DomainValidationException("The banking reveal purpose is invalid.");
        }

        var row = await dbContext.SupplierBankingVersions
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.BankingDetailId == bankingDetailId &&
                             candidate.Version == version &&
                             candidate.OrganizationId == organizationId,
                cancellationToken)
            ?? throw new DomainNotFoundException("The banking detail does not exist in this organization.");
        var allowed = await IsRevealAllowedAsync(
            organizationId, actorUserId, row.SupplierId, bankingDetailId, version, cancellationToken);
        if (!allowed)
        {
            throw new DomainForbiddenException(
                "The actor cannot reveal the banking details of this supplier.");
        }

        persistence.AddAudit(
            organizationId, SupplierCodes.ActionBankingRevealed, Actor(actorUserId), null,
            ["banking_details"], correlationReference,
            $"reveal:{bankingDetailId:D}:{version}:{actorUserId:D}",
            $"{{\"id\":\"{bankingDetailId:D}\",\"type\":\"{SupplierCodes.TargetSupplier}\",\"version\":{version}}}",
            DateTimeOffset.UtcNow);
        await persistence.SaveAsync(cancellationToken);
        var associatedData = AssociatedData(organizationId, row.SupplierId, bankingDetailId, version);
        var plaintext = keys.Decrypt(row.Ciphertext, row.Nonce, row.Tag, associatedData);
        var content = SupplierBankingContent.Parse(plaintext);
        logger.LogInformation(
            "Supplier {SupplierId} banking detail {DetailId} version {Version} revealed for {Purpose}.",
            row.SupplierId, bankingDetailId, version, purpose);
        return content;
    }

    /// <summary>Plaintext of the pending version for the approver that holds its exact task (REQ-05).</summary>
    private async Task<bool> IsRevealAllowedAsync(
        Guid organizationId,
        Guid actorUserId,
        Guid supplierId,
        Guid bankingDetailId,
        int version,
        CancellationToken cancellationToken)
    {
        var isApSpecialist = await dbContext.RoleAssignments
            .AsNoTracking()
            .AnyAsync(
                assignment => assignment.UserProfileId == actorUserId &&
                              assignment.Role == (int)SystemRole.ApSpecialist &&
                              assignment.Status == (int)AssignmentStatus.Active &&
                              assignment.ScopeJson == "[{\"dimension\":\"ORGANIZATION\",\"reference\":null}]",
                cancellationToken);
        if (isApSpecialist &&
            await IsOperationalVersionAsync(organizationId, supplierId, bankingDetailId, version, cancellationToken))
        {
            return true;
        }

        // The pending candidate is only revealed to the holder of the live task, so a reassignment
        // or a terminal decision revokes the next read.
        var proposal = await dbContext.SupplierChangeProposals
            .AsNoTracking()
            .SingleOrDefaultAsync(
                row => row.OrganizationId == organizationId &&
                       row.SupplierId == supplierId &&
                       row.State == (int)SupplierProposalState.Pending &&
                       row.ApprovalCaseId != null,
                cancellationToken);
        if (proposal?.ApprovalCaseId is null || proposal.CandidateVersion is null)
        {
            return false;
        }

        var carries = await VersionIsReferencedAsync(
            supplierId, proposal.CandidateVersion.Value, bankingDetailId, version, cancellationToken);
        if (!carries)
        {
            return false;
        }

        return await dbContext.ApprovalTasks
            .AsNoTracking()
            .AnyAsync(
                task => task.CaseId == proposal.ApprovalCaseId &&
                        task.CurrentAssigneeUserId == actorUserId &&
                        task.Status == (int)ApprovalTaskStatus.Pending,
                cancellationToken);
    }

    private async Task<bool> IsOperationalVersionAsync(
        Guid organizationId,
        Guid supplierId,
        Guid bankingDetailId,
        int version,
        CancellationToken cancellationToken)
    {
        var operational = await dbContext.Suppliers
            .AsNoTracking()
            .Where(supplier => supplier.OrganizationId == organizationId && supplier.Id == supplierId)
            .Select(supplier => supplier.OperationalVersion)
            .SingleOrDefaultAsync(cancellationToken);
        if (operational is null)
        {
            return false;
        }

        var isCurrent = await dbContext.SupplierBankingVersions
            .AsNoTracking()
            .AnyAsync(
                row => row.BankingDetailId == bankingDetailId && row.Version == version,
                cancellationToken);
        if (!isCurrent)
        {
            return false;
        }

        var referenced = await dbContext.SupplierVersions
            .AsNoTracking()
            .Where(row => row.SupplierId == supplierId && row.Version == operational.Value)
            .Select(row => row.BankingRefsJson)
            .SingleOrDefaultAsync(cancellationToken);
        return referenced is not null &&
               SupplierSerialization.ReadBankingRefs(referenced).Any(
                   reference => reference.Id == bankingDetailId && reference.Version == version);
    }

    private async Task<bool> VersionIsReferencedAsync(
        Guid supplierId,
        int candidateVersion,
        Guid bankingDetailId,
        int version,
        CancellationToken cancellationToken)
    {
        var record = await dbContext.SupplierVersions
            .AsNoTracking()
            .Where(row => row.SupplierId == supplierId && row.Version == candidateVersion)
            .Select(row => new { row.SupplierId, row.BankingRefsJson })
            .ToListAsync(cancellationToken);
        return record.Any(row => SupplierSerialization.ReadBankingRefs(row.BankingRefsJson).Any(
            reference => reference.Id == bankingDetailId && reference.Version == version));
    }

    public static byte[] AssociatedData(Guid organizationId, Guid supplierId, Guid bankingDetailId, int version) =>
        Encoding.UTF8.GetBytes(
            $"supplier-banking/{organizationId:D}/{supplierId:D}/{bankingDetailId:D}/{version}");

    public static string Mask(SupplierBankingContent content)
    {
        var value = string.IsNullOrWhiteSpace(content.Iban) ? content.AccountNumber! : content.Iban!;
        var normalized = value.Replace(" ", string.Empty, StringComparison.Ordinal);
        return normalized.Length <= 4 ? string.Empty : normalized[^4..];
    }

    private static SupplierBankingContent NormalizedForStorage(SupplierBankingContent content) => new(
        SupplierCodes.DisplayName(content.AccountHolder, "Account holder", 300),
        SupplierCodes.DisplayName(content.BankName, "Bank name", 300),
        SupplierCodes.CountryCode(content.BankCountryCode, "Bank country"),
        SupplierCodes.Currency(content.Currency, "Banking currency"),
        content.AccountType,
        content.AccountNumber,
        content.Iban,
        content.SwiftBic,
        content.IsDefault);

    private static string Actor(Guid userId) =>
        $"{{\"system_id\":null,\"type\":\"USER\",\"user_id\":\"{userId:D}\",\"workload_client_id\":null,\"workload_issuer\":null}}";
}
