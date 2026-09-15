namespace ProcureToPay.Domain.Modules.Budget;

/// <summary>
/// A Budget dependency (owner, catalog, storage, payload or evidence) is absent, ambiguous or
/// corrupted. SPEC 08 NFR-04: it never degrades to an approval or a balance change, and it maps to
/// <c>503 /problems/budget-dependency-unavailable</c>.
/// </summary>
public sealed class BudgetDependencyUnavailableException : Exception
{
    public BudgetDependencyUnavailableException(string message) : base(message)
    {
    }

    public BudgetDependencyUnavailableException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>
/// The business outcome of a budget check: the position cannot cover the requested amount. It maps
/// to <c>422 /problems/budget-insufficient</c> and never creates a partial reservation (REQ-06).
/// </summary>
public sealed class BudgetInsufficientException(string message) : Exception(message);

/// <summary>
/// A budget reference is invalid, stale, inactive or belongs to another organization. It maps to
/// <c>422 /problems/budget-reference-invalid</c> (REQ-10).
/// </summary>
public sealed class BudgetReferenceInvalidException(string message) : Exception(message);
