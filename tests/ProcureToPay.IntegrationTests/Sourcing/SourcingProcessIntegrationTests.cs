using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ProcureToPay.Domain.Modules.Sourcing;
using ProcureToPay.Domain.SharedKernel;
using ProcureToPay.Infrastructure.Persistence.PurchaseRequests;
using ProcureToPay.Infrastructure.Persistence.Sourcing;

namespace ProcureToPay.IntegrationTests.Sourcing;

/// <summary>
/// SQL Server evidence of SPEC 10 REQ-01..REQ-04 (CA-01, CA-02, CA-03): takeover uniqueness and
/// release, append-only RFQ history, deadline-in-force timeliness and the per-line valid count that
/// never aggregates across lines.
/// </summary>
public sealed class SourcingProcessIntegrationTests
{
    [Fact]
    public async Task A_draft_process_does_not_consume_the_request()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await SourcingHarness.StartAsync(cancellationToken);
        await using var context = harness.CreateContext();
        var processes = harness.CreateProcessService(context);
        var process = await CreateProcessAsync(harness, processes, cancellationToken);
        Assert.False(process.TakeoverHeld);
        Assert.Equal("DRAFT", process.State);

        // REQ-01: only opening the first RFQ takes the request over, so a draft is harmless.
        var requests = new PurchaseRequestPersistenceService(
            context, NullLogger<PurchaseRequestPersistenceService>.Instance);
        await requests.CancelAsync(
            harness.RequestId, 1, $"cancel-{Guid.NewGuid():N}", "Draft has no takeover", harness.OrganizationId,
            harness.RequesterId, "corr-cancel", DateTimeOffset.UtcNow, cancellationToken);
        await using var verification = harness.CreateContext();
        var status = await verification.PurchaseRequests
            .AsNoTracking()
            .Where(record => record.Id == harness.RequestId)
            .Select(record => record.Status)
            .SingleAsync(cancellationToken);
        Assert.Equal(8, status);
    }

    [Fact]
    public async Task Opening_the_rfq_registers_the_takeover_and_cancelling_the_process_releases_it()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await SourcingHarness.StartAsync(cancellationToken);
        await using var context = harness.CreateContext();
        var processes = harness.CreateProcessService(context);
        var process = await CreateProcessAsync(harness, processes, cancellationToken);
        var rfq = await CreateAndOpenRfqAsync(harness, processes, process.ProcessId, cancellationToken);

        var active = await processes.GetProcessAsync(harness.OrganizationId, process.ProcessId, cancellationToken);
        Assert.Equal("ACTIVE", active.State);
        Assert.True(active.TakeoverHeld);
        Assert.True(await processes.HasLiveTakeoverAsync(
            harness.OrganizationId, harness.RequestId, cancellationToken));

        // Once the takeover is live the request is consumed and cannot be cancelled or revised.
        await using var blocked = harness.CreateContext();
        await Assert.ThrowsAsync<DomainConflictException>(() => new PurchaseRequestPersistenceService(
                blocked, NullLogger<PurchaseRequestPersistenceService>.Instance)
            .CancelAsync(
                harness.RequestId, 1, $"cancel-{Guid.NewGuid():N}", "Consumed", harness.OrganizationId,
                harness.RequesterId, "corr-cancel", DateTimeOffset.UtcNow, cancellationToken));

        // Cancelling the pre-award process releases the takeover and keeps the RFQ as history.
        var cancelled = await processes.CancelProcessAsync(
            new CancelSourcingProcessCommand(
                harness.OrganizationId, process.ProcessId, active.Version, "Buyer aborted sourcing",
                $"cancel-process-{Guid.NewGuid():N}"),
            harness.BuyerId, "corr-process-cancel", DateTimeOffset.UtcNow, cancellationToken);
        Assert.Equal("CANCELLED", cancelled.State);
        Assert.False(cancelled.TakeoverHeld);
        Assert.False(await processes.HasLiveTakeoverAsync(
            harness.OrganizationId, harness.RequestId, cancellationToken));
        var history = await processes.GetRfqAsync(harness.OrganizationId, rfq.RfqId, cancellationToken);
        Assert.Equal(RfqStatus.Cancelled, history.Status);
        Assert.True(history.Versions.Count >= 3);

        await using var released = harness.CreateContext();
        await new PurchaseRequestPersistenceService(released, NullLogger<PurchaseRequestPersistenceService>.Instance)
            .CancelAsync(
                harness.RequestId, 1, $"cancel-{Guid.NewGuid():N}", "Takeover released", harness.OrganizationId,
                harness.RequesterId, "corr-cancel", DateTimeOffset.UtcNow, cancellationToken);
    }

    [Fact]
    public async Task The_rfq_versions_and_their_audit_are_append_only_in_sql()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await SourcingHarness.StartAsync(cancellationToken);
        await using var context = harness.CreateContext();
        var processes = harness.CreateProcessService(context);
        var process = await CreateProcessAsync(harness, processes, cancellationToken);
        var rfq = await CreateAndOpenRfqAsync(harness, processes, process.ProcessId, cancellationToken);
        var extended = await processes.ExtendDeadlineAsync(
            new TransitionRfqCommand(
                harness.OrganizationId, rfq.RfqId, rfq.Version, $"extend-{Guid.NewGuid():N}",
                "Supplier asked for more time", DateTimeOffset.UtcNow.AddDays(10)),
            harness.BuyerId, "corr-extend", DateTimeOffset.UtcNow, cancellationToken);
        Assert.Equal(rfq.Version + 1, extended.Version);

        await using var tampering = harness.CreateContext();
        var update = await Assert.ThrowsAnyAsync<Exception>(() => tampering.Database.ExecuteSqlRawAsync(
            "UPDATE [Sourcing].[RfqVersions] SET [ResponseDeadline] = DATEADD(day, -30, [ResponseDeadline]) " +
            "WHERE [Version] = 1",
            cancellationToken));
        Assert.Contains("append-only", update.Message, StringComparison.OrdinalIgnoreCase);
        var delete = await Assert.ThrowsAnyAsync<Exception>(() => tampering.Database.ExecuteSqlRawAsync(
            "DELETE FROM [Sourcing].[RfqVersions] WHERE [Version] = 1",
            cancellationToken));
        Assert.Contains("append-only", delete.Message, StringComparison.OrdinalIgnoreCase);
        var auditDelete = await Assert.ThrowsAnyAsync<Exception>(() => tampering.Database.ExecuteSqlRawAsync(
            "DELETE FROM [Sourcing].[SourcingAuditRecords]",
            cancellationToken));
        Assert.Contains("append-only", auditDelete.Message, StringComparison.OrdinalIgnoreCase);

        var projection = await processes.GetRfqAsync(harness.OrganizationId, rfq.RfqId, cancellationToken);
        Assert.Equal(extended.Version, projection.CurrentVersion);
        Assert.Single(projection.Extensions);
        Assert.Equal(projection.ResponseDeadline, projection.Extensions[0].NewDeadline);
        Assert.True(projection.Extensions[0].PreviousDeadline < projection.Extensions[0].NewDeadline);
    }

    [Fact]
    public async Task An_extension_never_reclassifies_an_answer_received_before_it()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await SourcingHarness.StartAsync(cancellationToken);
        await using var context = harness.CreateContext();
        var clock = DateTimeOffset.UtcNow;
        var deadline = clock.AddHours(2);
        var processes = harness.CreateProcessService(context);
        var quotations = harness.CreateQuotationService(context);
        var process = await CreateProcessAsync(harness, processes, cancellationToken);
        var rfq = await CreateAndOpenRfqAsync(
            harness, processes, process.ProcessId, cancellationToken, deadline, clock);

        var onTime = await RegisterQuotationAsync(
            harness, quotations, rfq.RfqId, harness.SupplierId, deadline.AddMinutes(-1),
            deadline.AddMinutes(-1).AddSeconds(1), cancellationToken);
        Assert.Equal("ON_TIME", onTime.Timeliness);

        // The extension is confirmed two minutes after the answer was received, so it was not the
        // deadline in force back then and the recorded fact must not change (REQ-04, DEC-02).
        await processes.ExtendDeadlineAsync(
            new TransitionRfqCommand(
                harness.OrganizationId, rfq.RfqId, rfq.Version, $"extend-{Guid.NewGuid():N}",
                "Extended after receipt", deadline.AddHours(4)),
            harness.BuyerId, "corr-extend", deadline.AddMinutes(2), cancellationToken);
        var replayed = await quotations.GetQuotationAsync(
            harness.OrganizationId, onTime.QuotationId, cancellationToken);
        Assert.Equal(
            "ON_TIME",
            replayed.Versions.Single(version => version.Version == onTime.Version).Timeliness);

        // An answer received one minute after the original deadline stays LATE after the extension.
        var late = await RegisterQuotationAsync(
            harness, quotations, rfq.RfqId, harness.SecondSupplierId, deadline.AddMinutes(1),
            deadline.AddMinutes(3), cancellationToken);
        Assert.Equal("LATE", late.Timeliness);
    }

    [Fact]
    public async Task Only_the_current_on_time_valid_version_counts_once_per_line_and_supplier()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await SourcingHarness.StartAsync(cancellationToken);
        await using var context = harness.CreateContext();
        var clock = DateTimeOffset.UtcNow;
        var deadline = clock.AddHours(2);
        var processes = harness.CreateProcessService(context);
        var quotations = harness.CreateQuotationService(context);
        var process = await CreateProcessAsync(harness, processes, cancellationToken);
        var rfq = await CreateAndOpenRfqAsync(
            harness, processes, process.ProcessId, cancellationToken, deadline, clock);

        var first = await RegisterQuotationAsync(
            harness, quotations, rfq.RfqId, harness.SupplierId, deadline.AddMinutes(-30),
            deadline.AddMinutes(-29), cancellationToken, [harness.LineId]);
        await ReviewAsync(harness, quotations, first, "oracle-valid-1", cancellationToken);
        var lateSupplier = await RegisterQuotationAsync(
            harness, quotations, rfq.RfqId, harness.SecondSupplierId, deadline.AddMinutes(5),
            deadline.AddMinutes(6), cancellationToken, [harness.LineId, harness.OtherLineId]);
        await ReviewAsync(harness, quotations, lateSupplier, "oracle-valid-2", cancellationToken);

        var counts = await quotations.CountValidQuotationsAsync(
            harness.OrganizationId, rfq.RfqId, cancellationToken);
        Assert.Equal(1, counts[harness.LineId]);
        Assert.False(counts.ContainsKey(harness.OtherLineId));

        // A successor resets the review to PENDING, so the previous version stops counting at once.
        var successor = await RegisterQuotationAsync(
            harness, quotations, rfq.RfqId, harness.SupplierId, deadline.AddMinutes(-10),
            deadline.AddMinutes(-9), cancellationToken, [harness.LineId]);
        // The review already appended version 2, so the new answer is the third version.
        Assert.Equal(first.Version + 2, successor.Version);
        counts = await quotations.CountValidQuotationsAsync(harness.OrganizationId, rfq.RfqId, cancellationToken);
        Assert.False(counts.ContainsKey(harness.LineId));

        // A withdrawn answer never counts, not even for the line it covered.
        await ReviewWithdrawAsync(harness, quotations, successor, cancellationToken);
        counts = await quotations.CountValidQuotationsAsync(harness.OrganizationId, rfq.RfqId, cancellationToken);
        Assert.False(counts.ContainsKey(harness.LineId));
    }

    [Fact]
    public async Task A_replayed_command_returns_its_version_and_a_different_payload_conflicts()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await SourcingHarness.StartAsync(cancellationToken);
        await using var context = harness.CreateContext();
        var processes = harness.CreateProcessService(context);
        var key = $"process-{Guid.NewGuid():N}";
        var command = new CreateSourcingProcessCommand(
            harness.OrganizationId,
            harness.RequestId,
            1,
            [new SourcingLineDraft(harness.LineId, 1, 5m, "EA")],
            key);
        var first = await processes.CreateProcessAsync(
            command, harness.BuyerId, "corr-1", DateTimeOffset.UtcNow, cancellationToken);
        var replay = await processes.CreateProcessAsync(
            command, harness.BuyerId, "corr-2", DateTimeOffset.UtcNow, cancellationToken);
        Assert.Equal(first.ProcessId, replay.ProcessId);

        await Assert.ThrowsAsync<DomainConflictException>(() => processes.CreateProcessAsync(
            command with { Lines = [new SourcingLineDraft(harness.LineId, 1, 99m, "EA")] },
            harness.BuyerId, "corr-3", DateTimeOffset.UtcNow, cancellationToken));
    }

    [Fact]
    public async Task A_confirmed_attachment_is_immutable_and_a_valid_review_requires_it()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await SourcingHarness.StartAsync(cancellationToken);
        await using var context = harness.CreateContext();
        var processes = harness.CreateProcessService(context);
        var quotations = harness.CreateQuotationService(context);
        var process = await CreateProcessAsync(harness, processes, cancellationToken);
        var rfq = await CreateAndOpenRfqAsync(harness, processes, process.ProcessId, cancellationToken);

        var attachment = await quotations.StageAttachmentAsync(
            harness.OrganizationId,
            rfq.RfqId,
            harness.BuyerId,
            "offer.pdf",
            "application/pdf",
            11,
            new MemoryStream("hello offer"u8.ToArray()),
            DateTimeOffset.UtcNow,
            cancellationToken);
        Assert.Equal("STAGED", attachment.State);
        await Assert.ThrowsAsync<DomainValidationException>(() => quotations.ConfirmAttachmentAsync(
            harness.OrganizationId, attachment.FileId, attachment.Version, new string('0', 64),
            harness.BuyerId, DateTimeOffset.UtcNow, cancellationToken));
        var confirmed = await quotations.ConfirmAttachmentAsync(
            harness.OrganizationId, attachment.FileId, attachment.Version, attachment.Sha256,
            harness.BuyerId, DateTimeOffset.UtcNow, cancellationToken);
        Assert.Equal("CONFIRMED", confirmed.State);

        // The sealed row cannot be reopened nor deleted.
        await using var tampering = harness.CreateContext();
        var update = await Assert.ThrowsAnyAsync<Exception>(() => tampering.Database.ExecuteSqlRawAsync(
            "UPDATE [Sourcing].[SourcingAttachments] SET [State] = 1 WHERE [Id] = {0}",
            [attachment.FileId],
            cancellationToken));
        Assert.Contains("immutable", update.Message, StringComparison.OrdinalIgnoreCase);

        var quotation = await RegisterQuotationAsync(
            harness, quotations, rfq.RfqId, harness.SupplierId, DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow, cancellationToken, [harness.LineId], attachment);
        Assert.Equal("PENDING", quotation.ReviewStatus);
        await ReviewAsync(harness, quotations, quotation, "oracle-valid-3", cancellationToken);
        var view = await quotations.GetQuotationAsync(
            harness.OrganizationId, quotation.QuotationId, cancellationToken);
        // The review appends its own version: the newest one carries the VALID decision.
        Assert.Equal(quotation.Version + 1, view.CurrentVersion);
        Assert.Equal(
            "VALID",
            view.Versions.Single(version => version.Version == view.CurrentVersion).Content.Review.StatusCode);

        // An unknown attachment reference is refused before any version exists.
        await Assert.ThrowsAsync<DomainValidationException>(() => quotations.RegisterQuotationAsync(
            new RegisterQuotationCommand(
                harness.OrganizationId,
                rfq.RfqId,
                harness.SecondSupplierId,
                1,
                "PEN",
                Terms(),
                DateTimeOffset.UtcNow.AddMinutes(-1),
                [new QuotationLineDraft(harness.LineId, "1", "10", "10", "0", "0", "0", "10", "delivered")],
                [new SourcingAttachmentRef("application/pdf", Guid.NewGuid(), "ghost.pdf", 10, new string('a', 64), 1)],
                $"quote-{Guid.NewGuid():N}"),
            harness.BuyerId,
            "corr-ghost",
            DateTimeOffset.UtcNow,
            cancellationToken));
    }

    private static async Task<SourcingProcessView> CreateProcessAsync(
        SourcingHarness harness,
        SourcingProcessService processes,
        CancellationToken cancellationToken) =>
        await processes.CreateProcessAsync(
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

    private static async Task<SourcingRfqOutcome> CreateAndOpenRfqAsync(
        SourcingHarness harness,
        SourcingProcessService processes,
        Guid processId,
        CancellationToken cancellationToken,
        DateTimeOffset? deadline = null,
        DateTimeOffset? now = null)
    {
        var clock = now ?? DateTimeOffset.UtcNow;
        var draft = await processes.CreateRfqAsync(
            new CreateRfqCommand(
                harness.OrganizationId,
                processId,
                1,
                "PEN",
                Terms(),
                Weights(),
                deadline ?? clock.AddDays(7),
                $"rfq-{Guid.NewGuid():N}"),
            harness.BuyerId,
            "corr-rfq",
            clock,
            cancellationToken);
        return await processes.OpenRfqAsync(
            new TransitionRfqCommand(
                harness.OrganizationId, draft.RfqId, draft.Version, $"open-{Guid.NewGuid():N}"),
            harness.BuyerId,
            "corr-open",
            clock,
            cancellationToken);
    }

    private static async Task<SourcingQuotationOutcome> RegisterQuotationAsync(
        SourcingHarness harness,
        SourcingQuotationService quotations,
        Guid rfqId,
        Guid supplierId,
        DateTimeOffset receivedAt,
        DateTimeOffset now,
        CancellationToken cancellationToken,
        IReadOnlyList<Guid>? lineIds = null,
        SourcingAttachmentView? attachment = null)
    {
        var staged = attachment ?? await quotations.StageAttachmentAsync(
            harness.OrganizationId,
            rfqId,
            harness.BuyerId,
            $"{supplierId:N}.pdf",
            "application/pdf",
            9,
            new MemoryStream("evidence!"u8.ToArray()),
            now,
            cancellationToken);
        if (attachment is null)
        {
            staged = await quotations.ConfirmAttachmentAsync(
                harness.OrganizationId, staged.FileId, staged.Version, staged.Sha256, harness.BuyerId,
                now, cancellationToken);
        }

        return await quotations.RegisterQuotationAsync(
            new RegisterQuotationCommand(
                harness.OrganizationId,
                rfqId,
                supplierId,
                1,
                "PEN",
                Terms(),
                receivedAt,
                (lineIds ?? [harness.LineId, harness.OtherLineId])
                    .Select(lineId => new QuotationLineDraft(
                        lineId, "2", "10", "20", "0", "0", "0", "20", "delivered as offered"))
                    .ToArray(),
                [new SourcingAttachmentRef(
                    staged.ContentType, staged.FileId, staged.FileName, staged.Length, staged.Sha256,
                    staged.Version)],
                $"quote-{Guid.NewGuid():N}"),
            harness.BuyerId,
            "corr-quote",
            now,
            cancellationToken);
    }

    private static async Task ReviewAsync(
        SourcingHarness harness,
        SourcingQuotationService quotations,
        SourcingQuotationOutcome outcome,
        string key,
        CancellationToken cancellationToken) =>
        await quotations.ReviewQuotationAsync(
            new ReviewQuotationCommand(
                harness.OrganizationId, outcome.QuotationId, outcome.Version, QuotationReviewStatus.Valid, [], null,
                key),
            harness.BuyerId,
            "corr-review",
            DateTimeOffset.UtcNow,
            cancellationToken);

    private static async Task ReviewWithdrawAsync(
        SourcingHarness harness,
        SourcingQuotationService quotations,
        SourcingQuotationOutcome outcome,
        CancellationToken cancellationToken) =>
        await quotations.WithdrawQuotationAsync(
            new ReviewQuotationCommand(
                harness.OrganizationId,
                outcome.QuotationId,
                outcome.Version,
                QuotationReviewStatus.Withdrawn,
                [],
                "Supplier withdrew",
                $"withdraw-{Guid.NewGuid():N}"),
            harness.BuyerId,
            "corr-withdraw",
            DateTimeOffset.UtcNow,
            cancellationToken);

    [Fact]
    public async Task An_attachment_over_the_contractual_limit_is_a_payload_condition()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await SourcingHarness.StartAsync(cancellationToken);
        await using var context = harness.CreateContext();
        var quotations = harness.CreateQuotationService(context);

        // REQ-14: 20 MiB is the contractual maximum for one attachment; the limit is a payload
        // condition (413) and is decided before any row exists.
        var exception = await Assert.ThrowsAsync<SourcingPayloadTooLargeException>(() =>
            quotations.StageAttachmentAsync(
                harness.OrganizationId,
                Guid.NewGuid(),
                harness.BuyerId,
                "huge.pdf",
                "application/pdf",
                SourcingCodes.MaxAttachmentBytes + 1,
                new MemoryStream(new byte[8]),
                DateTimeOffset.UtcNow,
                cancellationToken));
        Assert.Contains("bytes", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static CommercialTerms Terms() => new(15, "EXW", "NET30", 365);

    private static EvaluationWeightSet Weights() => new(
    [
        new EvaluationWeight(SourcingEvaluationCriterion.Price, 50),
        new EvaluationWeight(SourcingEvaluationCriterion.DeliveryTime, 20),
        new EvaluationWeight(SourcingEvaluationCriterion.Warranty, 10),
        new EvaluationWeight(SourcingEvaluationCriterion.PaymentTerms, 10),
        new EvaluationWeight(SourcingEvaluationCriterion.TechnicalCompliance, 10),
        new EvaluationWeight(SourcingEvaluationCriterion.SupplierPerformance, 0)
    ]);
}
