using Microsoft.EntityFrameworkCore;
using ProcureToPay.Domain.Modules.Sourcing;
using ProcureToPay.Infrastructure.Persistence;
using ProcureToPay.Infrastructure.Persistence.Sourcing;

namespace ProcureToPay.IntegrationTests.Sourcing;

/// <summary>
/// Shared SQL scenario of the sourcing suites: one process, one open RFQ and, when the test needs it,
/// one current <c>ON_TIME+VALID</c> quotation with confirmed evidence. It never bypasses the services,
/// so a change in the contract breaks the scenario instead of silently passing.
/// </summary>
public static class SourcingScenario
{
    /// <summary>Opens an RFQ weighted on price only, so a valid quotation is enough to evaluate it.</summary>
    public static async Task<(SourcingProcessView Process, SourcingRfqOutcome Rfq)> OpenRfqAsync(
        SourcingHarness harness,
        ProcureToPayDbContext context,
        CancellationToken cancellationToken) =>
        await OpenAsync(harness, context, priceOnly: true, quotation: true, cancellationToken);

    /// <summary>Opens an RFQ weighted on price and payment terms: the manual input is missing.</summary>
    public static async Task<(SourcingProcessView Process, SourcingRfqOutcome Rfq)> OpenRfqWithoutEvidenceAsync(
        SourcingHarness harness,
        ProcureToPayDbContext context,
        CancellationToken cancellationToken) =>
        await OpenAsync(harness, context, priceOnly: false, quotation: true, cancellationToken);

    /// <summary>Opens an RFQ that received no response yet, to prove the zero-quote rules (REQ-05).</summary>
    public static async Task<(SourcingProcessView Process, SourcingRfqOutcome Rfq)> OpenRfqWithoutQuotationAsync(
        SourcingHarness harness,
        ProcureToPayDbContext context,
        CancellationToken cancellationToken) =>
        await OpenAsync(harness, context, priceOnly: true, quotation: false, cancellationToken);

    /// <summary>
    /// Publishes the quotation allowance of the seeded prerequisite: the minimum the case demands and
    /// the floor a waiver can never cross (REQ-05).
    /// </summary>
    public static async Task SetQuotationAllowanceAsync(
        SourcingHarness harness,
        ProcureToPayDbContext context,
        int minimum,
        int? floor,
        CancellationToken cancellationToken)
    {
        var parameters = floor is null
            ? $"{{\"minimum_allowed_quotations\":null,\"minimum_quotations\":{minimum}}}"
            : $"{{\"minimum_allowed_quotations\":{floor.Value},\"minimum_quotations\":{minimum}}}";
        await context.Database.ExecuteSqlRawAsync(
            "UPDATE [Approval].[ApprovalPrerequisites] SET [ParametersJson] = {0} WHERE [Key] = {1}",
            [parameters, "QUOTES"],
            cancellationToken);
    }

    /// <summary>Persists one evaluated version of an RFQ that already carries a valid quotation.</summary>
    public static async Task<SourcingEvaluationView> EvaluateAsync(
        SourcingHarness harness,
        ProcureToPayDbContext context,
        SourcingRfqOutcome rfq,
        CancellationToken cancellationToken) =>
        await harness.CreateEvaluationService(context).EvaluateAsync(
            new EvaluateRfqCommand(harness.OrganizationId, rfq.RfqId, $"evaluate-{Guid.NewGuid():N}"),
            harness.BuyerId,
            "corr-evaluate",
            DateTimeOffset.UtcNow,
            cancellationToken);

    /// <summary>
    /// Full module scenario up to a built proposal: open RFQ with a valid quotation, evaluation,
    /// selection and proposal over the seeded request manifest and policy bundle.
    /// </summary>
    public static async Task<(SourcingProcessView Process, SourcingRfqOutcome Rfq, SourcingProposalView Proposal)>
        BuildProposalAsync(
            SourcingHarness harness,
            ProcureToPayDbContext context,
            CancellationToken cancellationToken)
    {
        var (created, rfq) = await OpenRfqAsync(harness, context, cancellationToken);
        var process = await harness.CreateProcessService(context)
            .GetProcessAsync(harness.OrganizationId, created.ProcessId, cancellationToken);
        await EvaluateAsync(harness, context, rfq, cancellationToken);
        await SelectAsync(harness, context, rfq, cancellationToken);
        await harness.SeedProposalFixturesAsync(context, cancellationToken);
        var proposal = await harness.CreateProposalService(context).BuildAsync(
            new BuildProposalCommand(
                harness.OrganizationId, rfq.RfqId, harness.SupplierId, $"proposal-{Guid.NewGuid():N}"),
            harness.BuyerId,
            "corr-proposal",
            DateTimeOffset.UtcNow,
            cancellationToken);
        // The approval evidence is seeded explicitly by the tests that need it, so the negative case
        // (a proposal without a completed case) stays testable.
        return (process, rfq, proposal);
    }

    /// <summary>Seeds the complete approval evidence of one proposal (REQ-11).</summary>
    public static async Task SeedApprovalAsync(
        SourcingHarness harness,
        ProcureToPayDbContext context,
        SourcingProposalView proposal,
        CancellationToken cancellationToken) =>
        await harness.SeedProposalApprovalAsync(
            context, proposal.ProposalId, proposal.Version, proposal.ContentDigest, proposal.ManifestDigest,
            withCase: true, cancellationToken);

    /// <summary>Selects the single valid supplier of the line with the recommendation (REQ-09).</summary>
    public static async Task<SourcingSelectionView> SelectAsync(
        SourcingHarness harness,
        ProcureToPayDbContext context,
        SourcingRfqOutcome rfq,
        CancellationToken cancellationToken) =>
        await harness.CreateSelectionService(context).SelectAsync(
            new SelectLineCommand(
                harness.OrganizationId, rfq.RfqId, harness.LineId, harness.SupplierId, null, null,
                $"select-{Guid.NewGuid():N}"),
            harness.BuyerId,
            "corr-select",
            DateTimeOffset.UtcNow,
            cancellationToken);

    /// <summary>
    /// Registers and validates a second supplier answer before the evaluation, so a supplier change
    /// after an award can be built from the same frozen evaluation (REQ-09, REQ-12).
    /// </summary>
    public static async Task RegisterSupplierQuotationAsync(
        SourcingHarness harness,
        ProcureToPayDbContext context,
        SourcingRfqOutcome rfq,
        Guid supplierId,
        string lineUnitPrice,
        string otherLineUnitPrice,
        CancellationToken cancellationToken)
    {
        var quotations = harness.CreateQuotationService(context);
        var evidence = await ConfirmedEvidenceAsync(harness, context, rfq.RfqId, cancellationToken);
        var registered = await quotations.RegisterQuotationAsync(
            new RegisterQuotationCommand(
                harness.OrganizationId,
                rfq.RfqId,
                supplierId,
                1,
                "PEN",
                new CommercialTerms(15, "EXW", "NET30", 365),
                DateTimeOffset.UtcNow.AddMinutes(-10),
                [
                    new QuotationLineDraft(
                        harness.LineId, "2", lineUnitPrice,
                        (2m * decimal.Parse(lineUnitPrice, System.Globalization.CultureInfo.InvariantCulture))
                        .ToString(System.Globalization.CultureInfo.InvariantCulture),
                        "0", "0", "0",
                        (2m * decimal.Parse(lineUnitPrice, System.Globalization.CultureInfo.InvariantCulture))
                        .ToString(System.Globalization.CultureInfo.InvariantCulture),
                        "second supplier offer"),
                    new QuotationLineDraft(
                        harness.OtherLineId, "1", otherLineUnitPrice, otherLineUnitPrice,
                        "0", "0", "0", otherLineUnitPrice, "second supplier offer")
                ],
                [evidence],
                $"quote-{Guid.NewGuid():N}"),
            harness.BuyerId,
            "corr-quote-second",
            DateTimeOffset.UtcNow.AddMinutes(-9),
            cancellationToken);
        await quotations.ReviewQuotationAsync(
            new ReviewQuotationCommand(
                harness.OrganizationId,
                registered.QuotationId,
                registered.Version,
                QuotationReviewStatus.Valid,
                [],
                null,
                $"review-{Guid.NewGuid():N}"),
            harness.BuyerId,
            "corr-review-second",
            DateTimeOffset.UtcNow.AddMinutes(-8),
            cancellationToken);
    }

    /// <summary>Stages and confirms one evidence file of the RFQ, returning its exact reference (REQ-03).</summary>
    public static async Task<SourcingAttachmentRef> ConfirmedEvidenceAsync(
        SourcingHarness harness,
        ProcureToPayDbContext context,
        Guid rfqId,
        CancellationToken cancellationToken)
    {
        var quotations = harness.CreateQuotationService(context);
        var staged = await quotations.StageAttachmentAsync(
            harness.OrganizationId,
            rfqId,
            harness.BuyerId,
            "evidence.pdf",
            "application/pdf",
            9,
            new MemoryStream("evidence!"u8.ToArray()),
            DateTimeOffset.UtcNow,
            cancellationToken);
        var confirmed = await quotations.ConfirmAttachmentAsync(
            harness.OrganizationId, staged.FileId, staged.Version, staged.Sha256, harness.BuyerId,
            DateTimeOffset.UtcNow, cancellationToken);
        return new SourcingAttachmentRef(
            confirmed.ContentType, confirmed.FileId, confirmed.FileName, confirmed.Length, confirmed.Sha256,
            confirmed.Version);
    }

    private static async Task<(SourcingProcessView Process, SourcingRfqOutcome Rfq)> OpenAsync(
        SourcingHarness harness,
        ProcureToPayDbContext context,
        bool priceOnly,
        bool quotation,
        CancellationToken cancellationToken)
    {
        var processes = harness.CreateProcessService(context);
        var process = await processes.CreateProcessAsync(
            new CreateSourcingProcessCommand(
                harness.OrganizationId,
                harness.RequestId,
                1,
                [
                    new SourcingLineDraft(harness.LineId, 1, 10m, "EA"),
                    new SourcingLineDraft(harness.OtherLineId, 1, 5m, "EA")
                ],
                $"process-{Guid.NewGuid():N}"),
            harness.BuyerId,
            "corr-process",
            DateTimeOffset.UtcNow,
            cancellationToken);
        var draft = await processes.CreateRfqAsync(
            new CreateRfqCommand(
                harness.OrganizationId,
                process.ProcessId,
                1,
                "PEN",
                new CommercialTerms(15, "EXW", "NET30", 365),
                priceOnly ? PriceOnlyWeights() : PriceAndTermsWeights(),
                DateTimeOffset.UtcNow.AddDays(7),
                $"rfq-{Guid.NewGuid():N}"),
            harness.BuyerId,
            "corr-rfq",
            DateTimeOffset.UtcNow,
            cancellationToken);
        var rfq = await processes.OpenRfqAsync(
            new TransitionRfqCommand(
                harness.OrganizationId, draft.RfqId, draft.Version, $"open-{Guid.NewGuid():N}"),
            harness.BuyerId,
            "corr-open",
            DateTimeOffset.UtcNow,
            cancellationToken);
        if (quotation)
        {
            await RegisterValidQuotationAsync(harness, context, rfq, priceOnly, cancellationToken);
        }

        return (process, rfq);
    }

    private static async Task RegisterValidQuotationAsync(
        SourcingHarness harness,
        ProcureToPayDbContext context,
        SourcingRfqOutcome rfq,
        bool priceOnly,
        CancellationToken cancellationToken)
    {
        _ = priceOnly;
        var quotations = harness.CreateQuotationService(context);
        var evidence = await ConfirmedEvidenceAsync(harness, context, rfq.RfqId, cancellationToken);
        var registered = await quotations.RegisterQuotationAsync(
            new RegisterQuotationCommand(
                harness.OrganizationId,
                rfq.RfqId,
                harness.SupplierId,
                1,
                "PEN",
                new CommercialTerms(15, "EXW", "NET30", 365),
                DateTimeOffset.UtcNow.AddMinutes(-10),
                [
                    new QuotationLineDraft(
                        harness.LineId, "2", "10", "20", "0", "0", "0", "20", "delivered as offered"),
                    new QuotationLineDraft(
                        harness.OtherLineId, "1", "10", "10", "0", "0", "0", "10", "delivered as offered")
                ],
                [evidence],
                $"quote-{Guid.NewGuid():N}"),
            harness.BuyerId,
            "corr-quote",
            DateTimeOffset.UtcNow.AddMinutes(-9),
            cancellationToken);
        await quotations.ReviewQuotationAsync(
            new ReviewQuotationCommand(
                harness.OrganizationId,
                registered.QuotationId,
                registered.Version,
                QuotationReviewStatus.Valid,
                [],
                null,
                $"review-{Guid.NewGuid():N}"),
            harness.BuyerId,
            "corr-review",
            DateTimeOffset.UtcNow.AddMinutes(-8),
            cancellationToken);
    }

    private static EvaluationWeightSet PriceOnlyWeights() => new(
    [
        new EvaluationWeight(SourcingEvaluationCriterion.Price, 100),
        new EvaluationWeight(SourcingEvaluationCriterion.DeliveryTime, 0),
        new EvaluationWeight(SourcingEvaluationCriterion.Warranty, 0),
        new EvaluationWeight(SourcingEvaluationCriterion.PaymentTerms, 0),
        new EvaluationWeight(SourcingEvaluationCriterion.TechnicalCompliance, 0),
        new EvaluationWeight(SourcingEvaluationCriterion.SupplierPerformance, 0)
    ]);

    private static EvaluationWeightSet PriceAndTermsWeights() => new(
    [
        new EvaluationWeight(SourcingEvaluationCriterion.Price, 50),
        new EvaluationWeight(SourcingEvaluationCriterion.DeliveryTime, 0),
        new EvaluationWeight(SourcingEvaluationCriterion.Warranty, 0),
        new EvaluationWeight(SourcingEvaluationCriterion.PaymentTerms, 50),
        new EvaluationWeight(SourcingEvaluationCriterion.TechnicalCompliance, 0),
        new EvaluationWeight(SourcingEvaluationCriterion.SupplierPerformance, 0)
    ]);
}
