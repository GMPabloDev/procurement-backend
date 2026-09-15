using Microsoft.EntityFrameworkCore;
using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Domain.Modules.Budget;
using ProcureToPay.Domain.Modules.Organization;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Infrastructure.Persistence.Budget;

/// <summary>
/// Immutable outcome of one transition batch, as the workload receives it (REQ-09).
/// </summary>
public sealed record BudgetTransitionResult(
    Guid OperationId,
    bool Replayed,
    IReadOnlyList<BudgetTransitionMovement> Movements);

/// <summary>One posted movement referenced by the transition response.</summary>
public sealed record BudgetTransitionMovement(
    Guid MovementId,
    Guid? ParentMovementId,
    string PositionKeyDigest,
    string Type,
    decimal Amount);

/// <summary>One registered producer of a financial transition (REQ-09).</summary>
public sealed record BudgetMovementProducerEntry(
    string Operation,
    string ContractVersion,
    string SourceType,
    string ProducerId,
    string WorkloadIssuer,
    string WorkloadClientId,
    bool IsEnabled);

/// <summary>
/// Exact-one registry of the workloads allowed to post COMMIT, CONSUME and REVERSE (SPEC 08 REQ-09,
/// DEC-09). Registrations come from configuration and are closed by operation+contract+source; a
/// missing, duplicated, disabled or partially matching entry fails closed, and no administrative
/// role ever acquires this authority.
/// </summary>
public sealed class BudgetMovementProducerRegistry
{
    public const string TransitionCommandContractVersion = "v1";

    private static readonly IReadOnlyDictionary<string, string> OperationSources =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["COMMIT"] = BudgetCodes.PurchaseOrderSourceType,
            ["CONSUME"] = BudgetCodes.InvoiceSourceType
        };

    private readonly IReadOnlyList<BudgetMovementProducerEntry> entries;

    public BudgetMovementProducerRegistry(Microsoft.Extensions.Configuration.IConfiguration configuration)
    {
        entries = configuration
            .GetSection("Budget:MovementProducers")
            .GetChildren()
            .Select(child => new BudgetMovementProducerEntry(
                child["Operation"] ?? string.Empty,
                child["ContractVersion"] ?? string.Empty,
                child["SourceType"] ?? string.Empty,
                child["ProducerId"] ?? string.Empty,
                child["Issuer"] ?? string.Empty,
                child["ClientId"] ?? string.Empty,
                !string.Equals(child["Enabled"], "false", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(child["Issuer"]) &&
                !string.IsNullOrWhiteSpace(child["ClientId"])))
            .ToArray();
    }

    /// <summary>Registrations that could never resolve, reported by health without exposing secrets.</summary>
    public IReadOnlyList<string> DiagnosticCodes()
    {
        var codes = new List<string>();
        foreach (var operation in OperationSources.Keys.Append("REVERSE"))
        {
            var matches = entries
                .Where(entry => string.Equals(entry.Operation, operation, StringComparison.Ordinal))
                .ToArray();
            if (matches.Length > 1)
            {
                codes.Add($"BUDGET_PRODUCER_AMBIGUOUS:{operation}");
            }
        }

        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.ProducerId) ||
                string.IsNullOrWhiteSpace(entry.WorkloadIssuer) ||
                string.IsNullOrWhiteSpace(entry.WorkloadClientId) ||
                entry.Operation is not ("COMMIT" or "CONSUME" or "REVERSE") ||
                (OperationSources.TryGetValue(entry.Operation, out var expected) &&
                 !string.Equals(entry.SourceType, expected, StringComparison.Ordinal)))
            {
                codes.Add($"BUDGET_PRODUCER_INVALID:{entry.Operation}");
            }
        }

        return codes;
    }

    /// <summary>
    /// Resolves the producer of one transition. A registration is usable only when it is enabled and
    /// its operation, contract version, source type and full workload identity match exactly.
    /// </summary>
    public BudgetMovementProducerEntry ResolveExactlyOne(
        string operation,
        string contractVersion,
        string sourceType,
        ApprovalWorkloadIdentity workload)
    {
        ArgumentNullException.ThrowIfNull(workload);
        var normalizedOperation = BudgetCodes.RequireBoundedToken(operation, "Operation");
        if (normalizedOperation is not ("COMMIT" or "CONSUME" or "REVERSE"))
        {
            throw new DomainValidationException("The budget transition operation is not recognized.");
        }

        var normalizedVersion = BudgetCodes.RequireKey(contractVersion, "contract_version");
        var normalizedSource = BudgetCodes.RequireBoundedToken(sourceType, "Source type");
        var matches = entries
            .Where(entry =>
                string.Equals(entry.Operation, normalizedOperation, StringComparison.Ordinal) &&
                string.Equals(entry.ContractVersion, normalizedVersion, StringComparison.Ordinal) &&
                string.Equals(entry.SourceType, normalizedSource, StringComparison.Ordinal))
            .ToArray();
        if (matches.Length == 0)
        {
            throw new BudgetDependencyUnavailableException(
                "No budget movement producer is registered for the requested operation.");
        }

        if (matches.Length > 1)
        {
            throw new BudgetDependencyUnavailableException(
                "More than one budget movement producer matches the requested operation.");
        }

        var producer = matches[0];
        if (!producer.IsEnabled)
        {
            throw new BudgetDependencyUnavailableException(
                "The budget movement producer is not enabled in this deployment.");
        }

        if (!string.Equals(producer.WorkloadIssuer, workload.Issuer, StringComparison.Ordinal) ||
            !string.Equals(producer.WorkloadClientId, workload.ClientId, StringComparison.Ordinal))
        {
            throw new DomainForbiddenException(
                "The workload is not the registered producer of this budget transition.");
        }

        return producer;
    }

    /// <summary>Every configured producer, for the administrative reads that need the closed table.</summary>
    public IReadOnlyList<BudgetMovementProducerEntry> Entries => entries;
}
