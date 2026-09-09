using BethesdaMultitool.Core.Recovery;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Recovery;

/// <summary>
///     The 2026-08 recovery probe tried zlib on every dump gap and failed corpus-wide with
///     "unsupported compression method" — the console uses XMemCompress/LZX, not zlib, so that
///     result measured the decoder, not the dump. These tests pin the replacement probe against
///     streams built to the framing the real decoder consumes.
///     <para>
///         Chunk framing mirrored from <c>DDXConv.Tests/Support/SyntheticDdx.cs</c>, which is
///         internal to that project: a 0xFF explicit-size header, a 4-byte bitstream seed encoding
///         an uncompressed (type 3) block, the R0/R1/R2 repeat offsets, the raw payload, and pad.
///     </para>
/// </summary>
public class LzxGapProbeTests
{
    [Fact]
    public void Probe_FindsAnEmbeddedStreamAndReportsItsInflatedSize()
    {
        var payload = StampedPayload(8192);
        "BSA\0"u8.CopyTo(payload);
        var stream = BuildUncompressedLzxStream(payload);

        // Bury it in noise so the probe has to locate it, not just decode at offset 0.
        var region = new byte[4096 + stream.Length + 4096];
        FillPseudoRandom(region);
        stream.CopyTo(region.AsSpan(4096));

        var hits = LzxGapProbe.Probe(region, 0x1000, cancellationToken: TestContext.Current.CancellationToken);

        var hit = Assert.Single(hits, h => h.InflatedBytes == payload.Length);
        Assert.Equal(0x1000 + 4096, hit.Offset);
        Assert.True(hit.CompressedBytes > 0);
    }

    [Fact]
    public void Probe_WithoutTheContentGate_ReportsCoincidentalInflationsOfPureNoise()
    {
        // Documents WHY the gate exists rather than asserting the gate's own output: the decoder
        // will inflate random bytes, so an unfiltered probe reports offsets that hold no data.
        var noise = new byte[256 * 1024];
        FillPseudoRandom(noise);

        var unfiltered = LzxGapProbe.Probe(noise, requireKnownContent: false,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotEmpty(unfiltered);
        Assert.All(unfiltered, hit => Assert.Equal("binary", hit.ContentSniff));
    }

    [Fact]
    public void Probe_SniffsAKnownContainerMagicInTheInflatedPayload()
    {
        var payload = StampedPayload(8192);
        "BSA\0"u8.CopyTo(payload);

        var hits = LzxGapProbe.Probe(BuildUncompressedLzxStream(payload),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(hits, h => h.ContentSniff == "bsa");
    }

    [Fact]
    public void Probe_ReportsNothingForNoiseThatMerelyLooksLikeAChunkHeader()
    {
        // Every 2-byte value is a syntactically valid chunk header, so the guard against false
        // positives has to be the decode itself — this is the case that would flood the report.
        var noise = new byte[256 * 1024];
        FillPseudoRandom(noise);

        Assert.Empty(LzxGapProbe.Probe(noise, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Probe_DoesNotReportOverlappingHitsInsideOneStream()
    {
        var payload = StampedPayload(8192);
        "DDS "u8.CopyTo(payload);

        // A stream's own bytes contain many offsets that would decode as truncated sub-streams;
        // consuming the whole stream on a hit is what keeps one archive from becoming hundreds.
        Assert.Single(LzxGapProbe.Probe(BuildUncompressedLzxStream(payload),
            cancellationToken: TestContext.Current.CancellationToken));
    }

    /// <summary>
    ///     Leading bytes recorded by the first real xex44 sweep (2026-09-03). Two of the three
    ///     reported hits began with plainly binary bytes and were still classed "text" by a
    ///     90%-printable average over 512 bytes; only the third is a genuine string payload.
    ///     A recovered file starts with its own content, so the sniff now requires the lead to
    ///     read as text too.
    /// </summary>
    [Theory]
    [InlineData(new byte[] { 0, 0, 0, 0, 0, 0, 0x45, 0x30, 0x94, 0xBC }, false)]
    [InlineData(new byte[] { 0x61, 0x04, 0x24, 0x00, 0x04, 0x22, 0x02, 0x00 }, false)]
    [InlineData(new byte[] { 0x2C, 0x39, 0x3D, 0x61, 0x6D, 0x62, 0x5F, 0x76, 0x61, 0x75, 0x6C, 0x74 }, true)]
    public void Probe_ClassifiesRealSweepPayloadsByTheirLeadingBytes(byte[] lead, bool expectText)
    {
        var payload = new byte[8192];
        // Printable filler so only the lead decides — this is the case the old average missed.
        payload.AsSpan().Fill((byte)'A');
        lead.CopyTo(payload, 0);

        var hits = LzxGapProbe.Probe(BuildUncompressedLzxStream(payload), requireKnownContent: false,
            cancellationToken: TestContext.Current.CancellationToken);

        var hit = Assert.Single(hits);
        Assert.Equal(expectText ? "text" : "binary", hit.ContentSniff);
    }

    [Fact]
    public void ToCsv_EmitsOneRowPerHitWithTheCompressionRatio()
    {
        var hits = new List<LzxProbeHit> { new(0x2000, 100, 400, "bsa", "42534100") };

        var csv = LzxGapProbe.ToCsv("xex44.dmp", hits);

        Assert.Contains("dump,offset,compressed_bytes,inflated_bytes,ratio,content_sniff,leading_hex",
            csv, StringComparison.Ordinal);
        Assert.Contains("xex44.dmp,0x00002000,100,400,4.00,bsa,42534100", csv, StringComparison.Ordinal);
    }

    /// <summary>Deterministic non-constant payload — a run of zeros would sniff misleadingly.</summary>
    private static byte[] StampedPayload(int length)
    {
        var data = new byte[length];
        for (var i = 0; i < length; i++)
        {
            data[i] = (byte)(i * 31 + 7);
        }

        return data;
    }

    private static void FillPseudoRandom(Span<byte> buffer)
    {
        var state = 0x9E3779B9;
        for (var i = 0; i < buffer.Length; i++)
        {
            state = state * 1664525 + 1013904223;
            buffer[i] = (byte)(state >> 24);
        }
    }

    /// <summary>One XMemCompress stream carrying a single uncompressed LZX block.</summary>
    private static byte[] BuildUncompressedLzxStream(byte[] payload)
    {
        using var buffer = new MemoryStream();
        var content = payload.Length + 20; // seed(4) + R0R1R2(12) + payload + terminator pad(4)

        buffer.WriteByte(0xFF);
        buffer.WriteByte((byte)(payload.Length >> 8));
        buffer.WriteByte((byte)payload.Length);
        buffer.WriteByte((byte)(content >> 8));
        buffer.WriteByte((byte)content);

        // 0b0_011_<24-bit size>_0000: intel-E8 off, block type 3 (uncompressed), block size.
        var seed = (3u << 28) | ((uint)payload.Length << 4);
        var word0 = (ushort)(seed >> 16);
        var word1 = (ushort)seed;
        buffer.WriteByte((byte)word0);
        buffer.WriteByte((byte)(word0 >> 8));
        buffer.WriteByte((byte)word1);
        buffer.WriteByte((byte)(word1 >> 8));

        buffer.Write([1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0]);
        buffer.Write(payload);
        buffer.Write([0, 0, 0, 0]);
        buffer.Write([0, 0, 0, 0, 0]);

        return buffer.ToArray();
    }
}