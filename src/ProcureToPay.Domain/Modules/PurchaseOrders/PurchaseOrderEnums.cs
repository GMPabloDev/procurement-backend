using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Domain.Modules.PurchaseOrders;

/// <summary>Business states of a Purchase Order version (SPEC 11 Estados y ownership).</summary>
public enum PurchaseOrderState
{
    Draft = 1,
    PendingApproval = 2,
    Approved = 3,
    Issued = 4,
    Cancelled = 5
}

/// <summary>States of one procurement supporting document version (REQ-08).</summary>
public enum SupportingDocumentState
{
    Staged = 1,
    Confirmed = 2
}

/// <summary>Durable states of one award consumption claim (REQ-01).</summary>
public enum AwardClaimState
{
    Claimed = 1,
    Issued = 2,
    Released = 3,
    ConsumedCancelled = 4
}

/// <summary>States of one line takeover (<c>purchase-request-line-takeover/v1</c>, REQ-10).</summary>
public enum TakeoverState
{
    Active = 1,
    Released = 2,
    Terminal = 3
}

/// <summary>Owner of one Purchase Request line takeover (REQ-10).</summary>
public enum PurchaseRequestLineOwner
{
    Sourcing = 1,
    PurchaseOrder = 2,
    DirectPurchase = 3,
    Fulfillment = 4,
    Invoice = 5
}

/// <summary>Projection of one Purchase Request line published by this module (REQ-10).</summary>
public enum PurchaseRequestLineProjection
{
    Ordered = 1,
    DirectPurchaseAuthorized = 2
}

/// <summary>States of one Purchase Order amendment version (REQ-05).</summary>
public enum AmendmentState
{
    Draft = 1,
    PendingApproval = 2,
    Approved = 3,
    Applying = 4,
    Applied = 5,
    Cancelled = 6
}

/// <summary>States of one Direct Purchase authorization (REQ-07).</summary>
public enum DirectPurchaseState
{
    Authorized = 1,
    Cancelled = 2
}

/// <summary>Durable states of one purchase order budget attempt (REQ-04, REQ-05).</summary>
public enum PurchaseOrderBudgetAttemptState
{
    Pending = 1,
    Committing = 2,
    Releasing = 3,
    Completed = 4
}

/// <summary>Operation of one durable purchase order budget attempt.</summary>
public enum PurchaseOrderBudgetAttemptOperation
{
    Commit = 1,
    Reverse = 2
}

/// <summary>
/// A payload over the contractual limits is rejected with <c>413</c> before the affected artefact is
/// persisted (REQ-11).
/// </summary>
public sealed class PurchaseOrderPayloadTooLargeException(string message) : DomainException(message);

/// <summary>
/// A missing, ambiguous, stale or corrupt dependency of the Purchase Orders module maps to
/// <c>503 /problems/purchase-order-dependency-unavailable</c> (REQ-11, NFR-04).
/// </summary>
public sealed class PurchaseOrderDependencyUnavailableException(string message) : DomainException(message);

/// <summary>
/// Ineligible business input that is not a schema violation maps to <c>422</c> (REQ-11): terms,
/// acceptance responsibilities, ordering evidence and amount limits.
/// </summary>
public sealed class PurchaseOrderUnprocessableException(string message) : DomainException(message);

/// <summary>Wire codes of the PO states; the CLR enum name never leaves the domain.</summary>
public static class PurchaseOrderStateCodes
{
    public static string Of(PurchaseOrderState state) => state switch
    {
        PurchaseOrderState.Draft => "DRAFT",
        PurchaseOrderState.PendingApproval => "PENDING_APPROVAL",
        PurchaseOrderState.Approved => "APPROVED",
        PurchaseOrderState.Issued => "ISSUED",
        PurchaseOrderState.Cancelled => "CANCELLED",
        _ => throw new DomainValidationException("The purchase order state is not recognized.")
    };

    public static PurchaseOrderState Parse(string? code) => code switch
    {
        "DRAFT" => PurchaseOrderState.Draft,
        "PENDING_APPROVAL" => PurchaseOrderState.PendingApproval,
        "APPROVED" => PurchaseOrderState.Approved,
        "ISSUED" => PurchaseOrderState.Issued,
        "CANCELLED" => PurchaseOrderState.Cancelled,
        _ => throw new DomainValidationException("The stored purchase order state is not recognized.")
    };
}

/// <summary>Wire codes of the amendment states (REQ-05).</summary>
public static class AmendmentStateCodes
{
    public static string Of(AmendmentState state) => state switch
    {
        AmendmentState.Draft => "DRAFT",
        AmendmentState.PendingApproval => "PENDING_APPROVAL",
        AmendmentState.Approved => "APPROVED",
        AmendmentState.Applying => "APPLYING",
        AmendmentState.Applied => "APPLIED",
        AmendmentState.Cancelled => "CANCELLED",
        _ => throw new DomainValidationException("The amendment state is not recognized.")
    };

    public static AmendmentState Parse(string? code) => code switch
    {
        "DRAFT" => AmendmentState.Draft,
        "PENDING_APPROVAL" => AmendmentState.PendingApproval,
        "APPROVED" => AmendmentState.Approved,
        "APPLYING" => AmendmentState.Applying,
        "APPLIED" => AmendmentState.Applied,
        "CANCELLED" => AmendmentState.Cancelled,
        _ => throw new DomainValidationException("The stored amendment state is not recognized.")
    };
}

/// <summary>Wire codes of the Direct Purchase states (REQ-07).</summary>
public static class DirectPurchaseStateCodes
{
    public static string Of(DirectPurchaseState state) => state switch
    {
        DirectPurchaseState.Authorized => "AUTHORIZED",
        DirectPurchaseState.Cancelled => "CANCELLED",
        _ => throw new DomainValidationException("The direct purchase state is not recognized.")
    };

    public static DirectPurchaseState Parse(string? code) => code switch
    {
        "AUTHORIZED" => DirectPurchaseState.Authorized,
        "CANCELLED" => DirectPurchaseState.Cancelled,
        _ => throw new DomainValidationException("The stored direct purchase state is not recognized.")
    };
}

/// <summary>Wire codes of the claim states (REQ-01).</summary>
public static class AwardClaimStateCodes
{
    public static string Of(AwardClaimState state) => state switch
    {
        AwardClaimState.Claimed => "CLAIMED",
        AwardClaimState.Issued => "ISSUED",
        AwardClaimState.Released => "RELEASED",
        AwardClaimState.ConsumedCancelled => "CONSUMED_CANCELLED",
        _ => throw new DomainValidationException("The award claim state is not recognized.")
    };

    public static AwardClaimState Parse(string? code) => code switch
    {
        "CLAIMED" => AwardClaimState.Claimed,
        "ISSUED" => AwardClaimState.Issued,
        "RELEASED" => AwardClaimState.Released,
        "CONSUMED_CANCELLED" => AwardClaimState.ConsumedCancelled,
        _ => throw new DomainValidationException("The stored award claim state is not recognized.")
    };
}

/// <summary>Wire codes of the takeover states (REQ-10).</summary>
public static class TakeoverStateCodes
{
    public static string Of(TakeoverState state) => state switch
    {
        TakeoverState.Active => "ACTIVE",
        TakeoverState.Released => "RELEASED",
        TakeoverState.Terminal => "TERMINAL",
        _ => throw new DomainValidationException("The takeover state is not recognized.")
    };

    public static TakeoverState Parse(string? code) => code switch
    {
        "ACTIVE" => TakeoverState.Active,
        "RELEASED" => TakeoverState.Released,
        "TERMINAL" => TakeoverState.Terminal,
        _ => throw new DomainValidationException("The stored takeover state is not recognized.")
    };
}

/// <summary>Wire codes of the supporting document states (REQ-08).</summary>
public static class SupportingDocumentStateCodes
{
    public static string Of(SupportingDocumentState state) => state switch
    {
        SupportingDocumentState.Staged => "STAGED",
        SupportingDocumentState.Confirmed => "CONFIRMED",
        _ => throw new DomainValidationException("The supporting document state is not recognized.")
    };

    public static SupportingDocumentState Parse(string? code) => code switch
    {
        "STAGED" => SupportingDocumentState.Staged,
        "CONFIRMED" => SupportingDocumentState.Confirmed,
        _ => throw new DomainValidationException("The stored supporting document state is not recognized.")
    };
}

/// <summary>Wire codes of the durable budget attempt states (REQ-04).</summary>
public static class BudgetAttemptStateCodes
{
    public static string Of(PurchaseOrderBudgetAttemptState state) => state switch
    {
        PurchaseOrderBudgetAttemptState.Pending => "PENDING",
        PurchaseOrderBudgetAttemptState.Committing => "COMMITTING",
        PurchaseOrderBudgetAttemptState.Releasing => "RELEASING",
        PurchaseOrderBudgetAttemptState.Completed => "COMPLETED",
        _ => throw new DomainValidationException("The purchase order budget attempt state is not recognized.")
    };
}
