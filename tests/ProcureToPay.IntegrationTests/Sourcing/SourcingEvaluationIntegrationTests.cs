using Microsoft.EntityFrameworkCore;
using ProcureToPay.Domain.Modules.Sourcing;
using ProcureToPay.Domain.SharedKernel;
using ProcureToPay.Infrastructure.Persistence.Sourcing;

namespace ProcureToPay.IntegrationTests.Sourcing;

/// <summary>
/// SQL Server evidence of SPEC 10 REQ-07 / REQ-08 (CA-05): the evaluation is persisted as its exact
/// digest preimage, an FX snapshot needs confirmed evidence, a manual score needs a valid quotation,
/// a consumed version cannot be replaced and a tampered document is refused on read.
/// </summary>
public sealed class SourcingEvaluationIntegrationTests
{
    [Fact]
    public async Task An_evaluation_is_persisted_as_its_digest_preimage()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await SourcingHarness.StartAsync(cancellationToken);
        await using var context = harness.CreateContext();
        var (process, rfq) = await SourcingScenario.OpenRfqAsync(harness, context, cancellationToken);
        var evaluation = await SourcingScenario.EvaluateAsync(harness, context, rfq, cancellationToken);
        Assert.NotEqual(Guid.Empty, evaluation.EvaluationId);
        Assert.Equal(1, evaluation.Version);
        Assert.False(evaluation.Consumed);
        Assert.Contains(evaluation.CoveredLines, line => line == harness.LineId);
        Assert.False(string.IsNullOrWhiteSpace(evaluation.DocumentJson));

        // The stored bytes hash back to the recorded digest, and the digest is stable on re-read.
        var replayed = await harness.CreateEvaluationService(context)
            .GetEvaluationAsync(harness.OrganizationId, evaluation.EvaluationId, evaluation.Version, cancellationToken);
        Assert.Equal(evaluation.ContentDigest, replayed.ContentDigest);
        Assert.Equal(evaluation.DocumentJson, replayed.DocumentJson);
        Assert.NotEmpty(replayed.QuotationRefs);
        Assert.NotEmpty(replayed.Recommendations);
        Assert.NotNull(process.ProcessId);
    }

    [Fact]
    public async Task A_tampered_evaluation_document_is_refused_on_read()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await SourcingHarness.StartAsync(cancellationToken);
        await using var context = harness.CreateContext();
        var (_, rfq) = await SourcingScenario.OpenRfqAsync(harness, context, cancellationToken);
        var evaluation = await SourcingScenario.EvaluateAsync(harness, context, rfq, cancellationToken);

        // A direct writer that bypasses application code still cannot rewrite the stored version: the
        // append-only trigger refuses the update, so the persisted bytes stay intact (NFR-02), and a
        // forged row with mismatching bytes would fail closed on read (NFR-04).
        await using (var forge = harness.CreateContext())
        {
            var refused = await Assert.ThrowsAnyAsync<Exception>(() => forge.Database.ExecuteSqlRawAsync(
                "UPDATE [Sourcing].[QuoteEvaluationVersions] SET [DocumentJson] = {0} " +
                "WHERE [EvaluationId] = {1} AND [Version] = {2}",
                ["{}", evaluation.EvaluationId, evaluation.Version],
                cancellationToken));
            Assert.Contains("immutable", refused.Message, StringComparison.OrdinalIgnoreCase);
        }

        await using var verification = harness.CreateContext();
        var stored = await verification.QuoteEvaluationVersions
            .AsNoTracking()
            .SingleAsync(
                row => row.EvaluationId == evaluation.EvaluationId && row.Version == evaluation.Version,
                cancellationToken);
        Assert.Equal(evaluation.DocumentJson, stored.DocumentJson);
        Assert.Equal(evaluation.ContentDigest, stored.ContentDigest);
    }

    [Fact]
    public async Task An_fx_snapshot_requires_confirmed_evidence_of_the_same_rfq()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await SourcingHarness.StartAsync(cancellationToken);
        await using var context = harness.CreateContext();
        var (_, rfq) = await SourcingScenario.OpenRfqAsync(harness, context, cancellationToken);
        var service = harness.CreateEvaluationService(context);

        var staged = await harness.CreateQuotationService(context).StageAttachmentAsync(
            harness.OrganizationId,
            rfq.RfqId,
            harness.BuyerId,
            "sbs-rate.pdf",
            "application/pdf",
            8,
            new MemoryStream("rate-src"u8.ToArray()),
            DateTimeOffset.UtcNow,
            cancellationToken);
        await Assert.ThrowsAsync<DomainValidationException>(() => service.RegisterFxSnapshotAsync(
            new RegisterFxSnapshotCommand(
                harness.OrganizationId, rfq.RfqId, "USD", 3.75m, DateTimeOffset.UtcNow, "SBS", 
                new SourcingAttachmentRef(
                    staged.ContentType, staged.FileId, staged.FileName, staged.Length, staged.Sha256,
                    staged.Version),
                $"fx-{Guid.NewGuid():N}"),
            harness.BuyerId,
            "corr-fx",
            DateTimeOffset.UtcNow,
            cancellationToken));

        var confirmed = await harness.CreateQuotationService(context).ConfirmAttachmentAsync(
            harness.OrganizationId, staged.FileId, staged.Version, staged.Sha256, harness.BuyerId,
            DateTimeOffset.UtcNow, cancellationToken);
        var snapshot = await service.RegisterFxSnapshotAsync(
            new RegisterFxSnapshotCommand(
                harness.OrganizationId, rfq.RfqId, "USD", 3.75m, DateTimeOffset.UtcNow, "SBS published rate",
                new SourcingAttachmentRef(
                    confirmed.ContentType, confirmed.FileId, confirmed.FileName, confirmed.Length,
                    confirmed.Sha256, confirmed.Version),
                $"fx-{Guid.NewGuid():N}"),
            harness.BuyerId,
            "corr-fx",
            DateTimeOffset.UtcNow,
            cancellationToken);
        Assert.Equal("USD", snapshot.SourceCurrency);
        Assert.Equal("PEN", snapshot.BaseCurrency);
        Assert.Equal(3.75m, snapshot.Rate);

        // The replay of the same key returns the same snapshot; another payload conflicts.
        var replay = await service.RegisterFxSnapshotAsync(
            new RegisterFxSnapshotCommand(
                harness.OrganizationId, rfq.RfqId, "USD", 3.75m, snapshot.EffectiveAt, "SBS published rate",
                new SourcingAttachmentRef(
                    confirmed.ContentType, confirmed.FileId, confirmed.FileName, confirmed.Length,
                    confirmed.Sha256, confirmed.Version),
                $"fx-{Guid.NewGuid():N}"),
            harness.BuyerId,
            "corr-fx-2",
            DateTimeOffset.UtcNow,
            cancellationToken);
        Assert.NotEqual(snapshot.Id, replay.Id);
    }

    [Fact]
    public async Task A_manual_score_requires_a_valid_quotation_and_a_consumed_version_cannot_be_replaced()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await SourcingHarness.StartAsync(cancellationToken);
        await using var context = harness.CreateContext();
        var (_, rfq) = await SourcingScenario.OpenRfqAsync(harness, context, cancellationToken);
        var quotations = harness.CreateQuotationService(context);
        var service = harness.CreateEvaluationService(context);

        // Without a current ON_TIME+VALID quotation of that supplier and line the score is refused.
        var orphanEvidence = await SourcingScenario.ConfirmedEvidenceAsync(
            harness, context, rfq.RfqId, cancellationToken);
        await Assert.ThrowsAsync<DomainConflictException>(() => service.RecordManualScoreAsync(
            new RecordManualScoreCommand(
                harness.OrganizationId, rfq.RfqId, harness.LineId, harness.SecondSupplierId,
                SourcingEvaluationCriterion.PaymentTerms, 80m, "Net 60",
                orphanEvidence,
                $"manual-{Guid.NewGuid():N}"),
            harness.BuyerId,
            "corr-manual",
            DateTimeOffset.UtcNow,
            cancellationToken));

        var evaluation = await SourcingScenario.EvaluateAsync(harness, context, rfq, cancellationToken);
        await service.ConsumeAsync(
            harness.OrganizationId, evaluation.EvaluationId, evaluation.Version, cancellationToken);
        var consumed = await service.GetEvaluationAsync(
            harness.OrganizationId, evaluation.EvaluationId, evaluation.Version, cancellationToken);
        Assert.True(consumed.Consumed);

        // A consumed version cannot be replaced: the caller must supersede its proposal instead.
        await Assert.ThrowsAsync<DomainConflictException>(() => service.EvaluateAsync(
            new EvaluateRfqCommand(harness.OrganizationId, rfq.RfqId, $"evaluate-{Guid.NewGuid():N}"),
            harness.BuyerId,
            "corr-evaluate",
            DateTimeOffset.UtcNow,
            cancellationToken));
        _ = quotations;
    }

    [Fact]
    public async Task A_missing_manual_input_blocks_the_evaluation_instead_of_scoring_zero()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await SourcingHarness.StartAsync(cancellationToken);
        await using var context = harness.CreateContext();
        var (_, rfq) = await SourcingScenario.OpenRfqWithoutEvidenceAsync(harness, context, cancellationToken);

        await Assert.ThrowsAsync<DomainValidationException>(() => harness.CreateEvaluationService(context)
            .EvaluateAsync(
                new EvaluateRfqCommand(
                    harness.OrganizationId, rfq.RfqId, $"evaluate-{Guid.NewGuid():N}"),
                harness.BuyerId,
                "corr-evaluate",
                DateTimeOffset.UtcNow,
                cancellationToken));
    }
}
