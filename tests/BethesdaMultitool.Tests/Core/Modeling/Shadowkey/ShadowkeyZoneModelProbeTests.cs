using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Travels.Shadowkey;
using BethesdaMultitool.Core.Modeling.Shadowkey;
using BethesdaMultitool.Tests.Helpers;
using Slfx77.Multitool.Core.Models.Sources;
using Xunit;
using static BethesdaMultitool.Tests.Core.Modeling.Shadowkey.ShadowkeyModelTestSupport;

namespace BethesdaMultitool.Tests.Core.Modeling.Shadowkey;

/// <summary>
///     The <c>bmt.shadowkey.zone</c> probe (cut-2 plan section 5.2, slice 3) on builder <c>.zmp</c> files: the envelope,
///     the name and grid arithmetic, the exact-stream rule (the trailing Adler-32), the stem note, and the refusals of
///     the Python oracle's selfcheck.
/// </summary>
public class ShadowkeyZoneModelProbeTests
{
    private static byte[] Zmp(string name = "testzone", int width = 2, int height = 2, int? declared = null)
    {
        var payload = new byte[132 + 6 * width * height];
        ShadowkeyTestBuilder.Field(name, 32, 0xCD).CopyTo(payload, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(128), (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(130), (ushort)height);
        var file = ShadowkeyTestBuilder.Envelope(payload);
        if (declared is { } length)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(file, (uint)length);
        }

        return file;
    }

    private static ModelProbeResult Probe(byte[] bytes, string path = "zones/testzone.zmp", int? prefixLength = null,
        bool? isComplete = null)
    {
        return new ShadowkeyZoneModelReader().Probe(Candidate(bytes, path, prefixLength, isComplete));
    }

    [Fact]
    public void ACompleteZmp_IsConfirmed_WithTheStemNoteOnlyWhenTheNameMatches()
    {
        var bytes = Zmp();

        var matching = Probe(bytes);
        var other = Probe(bytes, "zones/elsewhere.zmp");

        Assert.Equal(ModelProbeKind.Supported, matching.Kind);
        Assert.Equal(ModelProbeConfidence.Confirmed, matching.Confidence);
        Assert.Equal("Shadowkey zone grid 'testzone' 2x2 (name equals the file stem)", matching.Evidence!.Description);
        Assert.Equal("Shadowkey zone grid 'testzone' 2x2", other.Evidence!.Description);
    }

    [Fact]
    public void AnIncompletePrefix_IsTentative()
    {
        var bytes = Zmp();

        var result = Probe(bytes, isComplete: false);

        Assert.Equal(ModelProbeConfidence.Tentative, result.Confidence);
        Assert.EndsWith(ShadowkeyZoneModelProbe.IncompleteNote, result.Evidence!.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryTruncation_AndOneTrailingByte_AreNotAModel_ThoughTheInflaterAcceptsTheTrailingByte()
    {
        var bytes = Zmp();

        for (var cut = 0; cut < bytes.Length; cut++)
        {
            Assert.Equal(ModelProbeKind.NotAModel, Probe(bytes[..cut]).Kind);
        }

        byte[] trailing = [.. bytes, 0];
        Assert.Equal(ModelProbeKind.NotAModel, Probe(trailing).Kind);
        // Control: .NET's inflater alone ignores the byte after the stream, so only the Adler-32 rule refuses it.
        Assert.Equal(132 + 24, ShadowkeyCompressedFile.Inflate(trailing, "trailing.zmp").Length);
    }

    [Fact]
    public void TheGridArithmetic_AndTheNameField_AreChecked()
    {
        Assert.Equal(ModelProbeKind.NotAModel, Probe(Zmp(declared: 132 + 6 * 3)).Kind);
        Assert.Equal(ModelProbeKind.NotAModel, Probe(Zmp(name: new string('x', 32))).Kind);
        Assert.Equal(ModelProbeKind.NotAModel, Probe(Zmp(name: "bad\u0007name")).Kind);
        Assert.Equal(ModelProbeKind.NotAModel, Probe(Zmp(name: "")).Kind);
        Assert.Equal(ModelProbeKind.NotAModel, Probe(Zmp(width: 0)).Kind);
    }

    [Fact]
    public void OtherZoneFamilies_AndMeshRecords_AreNotAModel()
    {
        var files = new ShadowkeyTestBuilder.Zone().Build();

        Assert.Equal(ModelProbeKind.NotAModel, Probe(files["testzone.zcp"], "zones/testzone.zcp").Kind);
        Assert.Equal(ModelProbeKind.NotAModel, Probe(files["testzone.zsk"], "zones/testzone.zsk").Kind);
        Assert.Equal(ModelProbeKind.NotAModel, Probe(files["testzone.ztx"], "zones/testzone.ztx").Kind);
        Assert.Equal(ModelProbeKind.NotAModel, Probe(ShadowkeyTestBuilder.GoldenStatic(), "slot.bin").Kind);
        Assert.Equal(ModelProbeConfidence.Confirmed, Probe(files["testzone.zmp"]).Confidence);
        // The mesh probe refuses the .zmp in turn, so each file resolves to one reader.
        Assert.Equal(ModelProbeKind.NotAModel,
            new ShadowkeyMeshModelReader().Probe(Candidate(files["testzone.zmp"], "zones/testzone.zmp")).Kind);
    }

    /// <summary>
    ///     A complete file whose header claims the largest grid the probe admits (4,096 x 4,096 cells, about 96 MiB
    ///     inflated) while its stream holds only the 132-byte header is NotAModel, and probing it allocates a bounded
    ///     amount (cut-2 review finding 13): the payload streams through a fixed buffer instead of one sized from the
    ///     claim. The bound discriminates: a buffer of the claimed size alone is 100,663,429 bytes.
    /// </summary>
    [Fact]
    public void AHugeHeaderClaim_IsNotAModel_AndTheProbeAllocatesABoundedAmount()
    {
        var head = new byte[132];
        ShadowkeyTestBuilder.Field("big", 32, 0xCD).CopyTo(head, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(128), 4096);
        BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(130), 4096);
        var file = ShadowkeyTestBuilder.Envelope(head);
        BinaryPrimitives.WriteUInt32LittleEndian(file, 132u + 6u * 4096u * 4096u);
        var candidate = Candidate(file, "zones/big.zmp");
        Assert.True(candidate.IsComplete);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var result = new ShadowkeyZoneModelReader().Probe(candidate);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(ModelProbeKind.NotAModel, result.Kind);
        Assert.True(allocated < 4L * 1024 * 1024, $"the probe allocated {allocated} bytes");
        // The streamed Adler-32 is the one-shot value, so a real zone still ends exactly at its last byte.
        byte[] data = [1, 2, 3, 4, 5, 6, 7, 8, 9];
        Assert.Equal(ShadowkeyZoneModelProbe.Adler32(data),
            ShadowkeyZoneModelProbe.Adler32(data.AsSpan(4), ShadowkeyZoneModelProbe.Adler32(data.AsSpan(0, 4))));
        Assert.Equal(0x00AE_002Eu, ShadowkeyZoneModelProbe.Adler32(data));
    }
}
