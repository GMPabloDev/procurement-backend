using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Domain.Modules.Sourcing;

/// <summary>
/// A dependency of the sourcing flow is absent, ambiguous, stale or corrupted. It maps to
/// <c>503 /problems/sourcing-dependency-unavailable</c> and never degrades to a valid quotation,
/// waiver, recommendation, signal or award (SPEC 10 REQ-14, NFR-04).
/// </summary>
public sealed class SourcingDependencyUnavailableException(string message) : DomainException(message);
