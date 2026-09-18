using ProcureToPay.Domain.Modules.PurchaseOrders;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.UnitTests.PurchaseOrders;

/// <summary>
/// Contract tests of the supporting documents domain (SPEC 11 CA-08): the published documents carry
/// exactly their documented properties without any location, the evidence counts one reference per
/// target and byte identity, and the prerequisite parameters fail closed.
/// </summary>
public sealed class SupportingDocumentContractTests
{
    private static readonly Guid OrganizationId = Guid.Parse("aaaaaaaa-1111-1111-1111-111111111111");
    private static readonly Guid RequestId = Guid.Parse("11111111-7777-7777-7777-777777777777");
    private static readonly Guid DocumentId = Guid.Parse("bbbbbbbb-2222-2222-2222-222222222222");
    private static readonly Guid FileId = Guid.Parse("cccccccc-3333-3333-3333-333333333333");
    private static readonly Guid LineId = Guid.Parse("33333333-9999-9999-9999-999999999999");
    private static readonly Guid ActorId = Guid.Parse("55555555-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid PrerequisiteId = Guid.Parse("dddddddd-4444-4444-4444-444444444444");
    private static readonly Guid AttemptId = Guid.Parse("eeeeeeee-5555-5555-5555-555555555555");
    private static readonly DateTimeOffset OccurredAt = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_document_publishes_its_properties_and_never_a_location()
    {
        var document = Document(SupportingDocumentState.Staged);

        using var json = System.Text.Json.JsonDocument.Parse(document.CanonicalDocument());
        var properties = json.RootElement.EnumerateObject().Select(property => property.Name).ToArray();
        Assert.Equal(
            [
                "business_type", "canonicalization_version", "contract_version", "covered_targets",
                "file_ref", "organization_id", "request_ref", "state", "version"
            ],
            properties);
        var file = json.RootElement.GetProperty("file_ref").EnumerateObject()
            .Select(property => property.Name).ToArray();
        Assert.Equal(["content_type", "file_id", "file_name", "length", "sha256", "version"], file);
        Assert.DoesNotContain("object", document.CanonicalDocument(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("http", document.CanonicalDocument(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(document.Digest, PurchaseOrderCanonicalizer.Hash(document.CanonicalDocument()));
    }

    [Fact]
    public void A_confirmation_appends_a_sealed_successor()
    {
        var staged = Document(SupportingDocumentState.Staged);

        var confirmed = staged.Confirm(ActorId, OccurredAt.AddMinutes(1));

        Assert.Equal(2, confirmed.Version);
        Assert.Equal(1, confirmed.PredecessorVersion);
        Assert.Equal(SupportingDocumentState.Confirmed, confirmed.State);
        Assert.NotNull(confirmed.ConfirmedAt);
        Assert.Equal(staged.FileRef.Sha256, confirmed.FileRef.Sha256);
        Assert.NotEqual(staged.Digest, confirmed.Digest);
    }

    [Fact]
    public void The_evidence_counts_one_reference_per_target_and_byte_identity()
    {
        var target = Target();
        var evidence = new SupportingDocumentEvidence(
            AttemptId,
            OrganizationId,
            PrerequisiteId,
            Digest('a'),
            "supporting-document:1:signal",
            [target],
            [
                Reference(target, Digest('b'), 10),
                Reference(target, Digest('c'), 10),
                Reference(target, Digest('b'), 20)
            ],
            OccurredAt);

        Assert.Equal(3, evidence.DocumentRefs.Count);
        Assert.Equal(3, evidence.CountsByTarget()[target.CanonicalIdentity]);
        Assert.Equal("SATISFIED", evidence.Result);
        Assert.Equal(evidence.Digest, PurchaseOrderCanonicalizer.Hash(evidence.CanonicalDocument()));
        using var json = System.Text.Json.JsonDocument.Parse(evidence.CanonicalDocument());
        Assert.True(json.RootElement.TryGetProperty("counts_by_target", out var counts));
        Assert.True(counts.TryGetProperty(target.CanonicalIdentity, out _));

        // REQ-08: the very same bytes of one target are admitted once, never twice.
        Assert.Throws<DomainConflictException>(() => new SupportingDocumentEvidence(
            AttemptId,
            OrganizationId,
            PrerequisiteId,
            Digest('a'),
            "supporting-document:1:signal",
            [target],
            [Reference(target, Digest('b'), 10), Reference(target, Digest('b'), 10)],
            OccurredAt));
    }

    [Fact]
    public void The_prerequisite_parameters_fail_closed_without_a_type_or_a_minimum()
    {
        var parameters = SupportingDocumentOwnerParameters.Parse(
            "{\"document_types\":[\"INVOICE\",\"RECEIPT\"],\"minimum_count\":2}");

        Assert.Equal(["INVOICE", "RECEIPT"], parameters.DocumentTypes);
        Assert.Equal(2, parameters.MinimumCount);
        Assert.Throws<PurchaseOrderDependencyUnavailableException>(() =>
            SupportingDocumentOwnerParameters.Parse("{\"document_types\":[],\"minimum_count\":1}"));
        Assert.Throws<DomainValidationException>(() =>
            SupportingDocumentOwnerParameters.Parse("{\"document_types\":[\"INVOICE\"],\"minimum_count\":0}"));
    }

    private static ProcurementSupportingDocumentVersion Document(SupportingDocumentState state) =>
        new(
            DocumentId,
            1,
            null,
            OrganizationId,
            new PurchaseOrderContentRef(RequestId, 1, Digest('1')),
            [Target()],
            PurchaseOrderCodes.DocumentTypeInvoice,
            new SupportingDocumentFileRef("application/pdf", FileId, "invoice.pdf", 128L, Digest('b'), 1),
            state,
            ActorId,
            OccurredAt,
            state == SupportingDocumentState.Confirmed ? OccurredAt : null);

    private static PurchaseOrderContentRef Target() => new(LineId, 1, Digest('2'));

    private static SupportingDocumentEvidenceRef Reference(
        PurchaseOrderContentRef target,
        string sha256,
        long length) =>
        new(new PurchaseOrderContentRef(DocumentId, 1, Digest('3')), target, sha256, length);

    private static string Digest(char value) => new(value, 64);
}
