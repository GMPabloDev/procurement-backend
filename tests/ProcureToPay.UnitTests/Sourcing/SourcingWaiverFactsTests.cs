using ProcureToPay.Domain.Modules.Sourcing;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.UnitTests.Sourcing;

/// <summary>
/// SPEC 10 REQ-05 (CA-04): the facts bind the waiver to a recalculated shortage, never to a caller
/// number, and the reduction never covers a trace of zero offers nor falls below the persisted floor.
/// </summary>
public sealed class SourcingWaiverFactsTests
{
    private static readonly Guid OrganizationId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ProcessId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid RfqId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid LineId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid OtherLineId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly DateTimeOffset Clock = DateTimeOffset.Parse("2026-09-16T12:00:00.0000000Z");

    [Fact]
    public void The_facts_require_the_reduction_to_match_the_lowest_recalculated_count()
    {
        var exception = Assert.Throws<DomainConflictException>(() => Facts(
            from: 3, to: 2, floor: 1, quotes: 1));
        Assert.Contains("lowest recalculated count", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_waiver_never_covers_a_trace_with_zero_quotations()
    {
        var exception = Assert.Throws<DomainConflictException>(() =>
            Facts(from: 2, to: 0, floor: 1, quotes: 0));
        Assert.Contains("at least one valid quotation", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_reduction_never_falls_below_the_persisted_floor_or_reaches_the_minimum()
    {
        Assert.Throws<DomainConflictException>(() => Facts(from: 3, to: 1, floor: 2, quotes: 1));
        Assert.Throws<DomainConflictException>(() => Facts(from: 2, to: 2, floor: 1, quotes: 2));
        Assert.Throws<DomainConflictException>(() => Facts(from: 2, to: 1, floor: 0, quotes: 1));
    }

    [Fact]
    public void The_digest_is_stable_and_changes_with_the_facts()
    {
        var first = Facts(from: 3, to: 1, floor: 1, quotes: 1, lines: [LineId, OtherLineId]);
        var reordered = Facts(from: 3, to: 1, floor: 1, quotes: 1, lines: [OtherLineId, LineId]);
        Assert.Equal(first.Digest, reordered.Digest);
        Assert.Equal(first.Digest, first.ComputeDigest());
        Assert.Matches("^[0-9a-f]{64}$", first.Digest);

        var deeper = Facts(from: 4, to: 1, floor: 1, quotes: 1, lines: [LineId, OtherLineId], lowestIsOther: true);
        Assert.NotEqual(first.Digest, deeper.Digest);
    }

    [Fact]
    public void The_canonical_document_is_the_digest_preimage()
    {
        var facts = Facts(from: 3, to: 1, floor: 1, quotes: 1, lines: [LineId, OtherLineId]);

        var document = facts.CanonicalDocument();
        Assert.Equal(
            ProcureToPay.Domain.Modules.Policy.PolicyCanonicalizer.Hash(document), facts.Digest);
        Assert.Contains("\"from\":3", document, StringComparison.Ordinal);
        Assert.Contains("\"to\":1", document, StringComparison.Ordinal);
        Assert.Contains("\"floor\":1", document, StringComparison.Ordinal);
    }

    private static Guid Deterministic(Guid lineId, int index)
    {
        Span<byte> bytes = stackalloc byte[16];
        _ = lineId.TryWriteBytes(bytes, bigEndian: true, out _);
        bytes[0] = (byte)(bytes[0] ^ index);
        return new Guid(bytes, bigEndian: true);
    }

    private static SourcingWaiverFacts Facts(
        int from,
        int to,
        int floor,
        int quotes,
        Guid[]? lines = null,
        bool lowestIsOther = false)
    {
        var covered = lines ?? [LineId];
        var targets = covered.Select((lineId, index) =>
        {
            var count = index == 0 ? quotes : quotes + (lowestIsOther ? 1 : 0);
            return new SourcingWaiverTarget(
                new SourcingContentRef(lineId, 1, new string('a', 64)),
                Enumerable.Range(0, count)
                    // Deterministic quotation identities so a permutation is byte-identical.
                    .Select(index => new SourcingContentRef(
                        Deterministic(lineId, index), 1, new string('b', 64)))
                    .ToArray());
        }).ToArray();
        return new SourcingWaiverFacts(
            OrganizationId,
            ProcessId,
            new SourcingContentRef(RfqId, 2, new string('c', 64)),
            Guid.Parse("66666666-6666-6666-6666-666666666666"),
            "QUOTES",
            Guid.Parse("77777777-7777-7777-7777-777777777777"),
            new string('d', 64),
            Guid.Parse("88888888-8888-8888-8888-888888888888"),
            new string('e', 64),
            new string('f', 64),
            from,
            to,
            floor,
            targets,
            Clock);
    }
}
