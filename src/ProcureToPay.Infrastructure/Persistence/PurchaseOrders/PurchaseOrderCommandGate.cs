namespace ProcureToPay.Infrastructure.Persistence.PurchaseOrders;

/// <summary>
/// Operational gate of the Purchase Orders commands (SPEC 11 REQ-10/REQ-12, CA-10). It is open only
/// while the takeover preflight of the deployment is satisfied: a database that cannot be read or a
/// legacy fence that is not fully expanded keeps every command failing closed with <c>503</c> until a
/// later preflight succeeds.
/// </summary>
public sealed class PurchaseOrderCommandGate
{
    private volatile bool open;

    /// <summary>True once a preflight verified the deployment; commands are refused while false.</summary>
    public bool IsOpen => open;

    public void Open() => open = true;

    public void Close() => open = false;
}
