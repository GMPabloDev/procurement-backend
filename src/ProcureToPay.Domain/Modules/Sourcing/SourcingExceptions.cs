using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Domain.Modules.Sourcing;

/// <summary>
/// A dependency of the sourcing flow is absent, ambiguous, stale or corrupted. It maps to
/// <c>503 /problems/sourcing-dependency-unavailable</c> and never degrades to a valid quotation,
/// waiver, recommendation, signal or award (SPEC 10 REQ-14, NFR-04).
/// </summary>
public sealed class SourcingDependencyUnavailableException(string message) : DomainException(message);

/// <summary>
/// One contractual limit of the sourcing surface was exceeded (lines per process, suppliers or
/// quotations per RFQ, attachment bytes or a canonical document). It maps to
/// <c>413 /problems/payload-too-large</c> and is rejected before anything is persisted (REQ-14).
/// </summary>
public sealed class SourcingPayloadTooLargeException(string message) : DomainException(message);
