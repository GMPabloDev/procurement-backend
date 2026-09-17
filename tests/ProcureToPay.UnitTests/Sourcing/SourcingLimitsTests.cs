using System.Text;
using ProcureToPay.Domain.Modules.Sourcing;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.UnitTests.Sourcing;

/// <summary>
/// SPEC 10 REQ-14 limits: keys, motives/reasons and canonical documents have contractual maxima and
/// an exceeded limit is a payload condition (<c>413</c>), never a silent truncation.
/// </summary>
public sealed class SourcingLimitsTests
{
    [Fact]
    public void A_key_admits_exactly_one_hundred_and_twenty_eight_characters()
    {
        Assert.Equal(128, SourcingCodes.Key(new string('k', 128), "key").Length);
        Assert.Throws<DomainValidationException>(() => SourcingCodes.Key(new string('k', 129), "key"));
        Assert.Throws<DomainValidationException>(() => SourcingCodes.Key(string.Empty, "key"));
        Assert.Throws<DomainValidationException>(() => SourcingCodes.Key("key with spaces", "key"));
        Assert.Equal("A-b_1.2:3", SourcingCodes.Key("A-b_1.2:3", "key"));
    }

    [Fact]
    public void A_reason_admits_one_thousand_unicode_scalars()
    {
        Assert.Equal(1_000, SourcingCodes.Reason(new string('r', 1_000)).Length);
        Assert.Throws<DomainValidationException>(() => SourcingCodes.Reason(new string('r', 1_001)));
        Assert.Throws<DomainValidationException>(() => SourcingCodes.Reason("   "));
        Assert.Throws<DomainValidationException>(() => SourcingCodes.Justification(new string('j', 1_001)));
        Assert.Throws<DomainValidationException>(() => SourcingCodes.Motive(new string('m', 1_001)));
        Assert.Throws<DomainValidationException>(() =>
            SourcingCodes.TechnicalResponse(new string('t', SourcingCodes.MaxTechnicalResponseScalars + 1)));
    }

    [Fact]
    public void A_canonical_document_admits_at_most_five_mebibytes()
    {
        var boundary = new string('x', (int)SourcingCodes.MaxCanonicalDocumentBytes);
        SourcingCodes.RequireCanonicalDocument(boundary);
        var oversized = new string('x', (int)SourcingCodes.MaxCanonicalDocumentBytes + 1);
        var exception = Assert.Throws<SourcingPayloadTooLargeException>(() =>
            SourcingCodes.RequireCanonicalDocument(oversized));
        Assert.Contains("bytes", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Throws<SourcingPayloadTooLargeException>(() => SourcingCodes.RequireCanonicalDocument(null));
        // The size is measured in UTF-8 bytes, never in UTF-16 characters.
        var multibyte = new string('\u00e9', (int)(SourcingCodes.MaxCanonicalDocumentBytes / 2 + 1));
        Assert.True(Encoding.UTF8.GetByteCount(multibyte) > SourcingCodes.MaxCanonicalDocumentBytes);
        Assert.Throws<SourcingPayloadTooLargeException>(() =>
            SourcingCodes.RequireCanonicalDocument(multibyte));
    }

    [Fact]
    public void The_contractual_maxima_are_the_published_ones()
    {
        Assert.Equal(500, SourcingCodes.MaxLinesPerProcess);
        Assert.Equal(100, SourcingCodes.MaxSuppliersPerRfq);
        Assert.Equal(200, SourcingCodes.MaxQuotationsPerRfq);
        Assert.Equal(20L * 1024 * 1024, SourcingCodes.MaxAttachmentBytes);
        Assert.Equal(5L * 1024 * 1024, SourcingCodes.MaxCanonicalDocumentBytes);
    }
}
