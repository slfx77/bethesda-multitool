using System.Buffers.Binary;
using BethesdaMultitool.Core.Modeling;
using BethesdaMultitool.Core.Modeling.Redguard;
using BethesdaMultitool.Core.Modeling.Xngine;
using BethesdaMultitool.Tests.Core.Modeling.Xngine;
using BethesdaMultitool.Tests.Helpers;
using Slfx77.Multitool.Core.Models.Sources;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Modeling.Redguard;

/// <summary>
///     The Redguard <c>.3DC</c> probe (cut-1c plan section 6.1) and the registry's one-recognition rule, on synthetic
///     stacks from <see cref="Redguard3DcTestStackBuilder" />: Confirmed when a complete stack tiles, Tentative when the
///     prefix is incomplete or a complete stack does not tile, Unsupported for v2.5, NotAModel for everything the
///     <c>.3D</c> reader owns. Each rule has a control that flips it by the one field the rule reads.
/// </summary>
public sealed class Redguard3DcModelProbeTests
{
    /// <summary>A narrow 2-frame stack of three points and one triangle.</summary>
    private static byte[] Stack(string tag = "v2.6", bool wide = false)
    {
        var builder = new Redguard3DcTestStackBuilder(tag, wide);
        builder.AddPoint(0, 0, 0);
        builder.AddPoint(256, 0, 0);
        builder.AddPoint(0, 0, 256);
        builder.AddPlane(0x0182, [(0, 0, 0), (1, 64, 0), (2, 0, 64)]);
        builder.AddFrame(wide ? [(0, 1, 0), (256, 1, 0), (0, 1, 256)] : [(0, 1, 0), (0, 1, 0), (0, 1, 0)]);
        return builder.Build();
    }

    [Theory]
    [InlineData(false, 3)]
    [InlineData(true, 4)]
    public void CompleteTilingStack_IsConfirmed_WithTheCountsAsEvidence(bool wide, int recordDwords)
    {
        var bytes = Stack(wide: wide);

        var probe = Redguard3DcModelTestSupport.Probe(bytes);

        Assert.Equal(ModelProbeKind.Supported, probe.Kind);
        Assert.Equal(ModelProbeConfidence.Confirmed, probe.Confidence);
        Assert.Equal($"Redguard .3DC v2.6 frame stack, {recordDwords}-dword frame records, 3 points, 1 planes, 2 frames",
            probe.Evidence!.Description);
        Assert.Contains(Redguard3DcModelFormatMetadata.DescribeVariant("v2.6", recordDwords),
            Redguard3DcModelFormatMetadata.Description.SupportedVariants);
    }

    /// <summary>
    ///     An incomplete prefix that holds the plane list is Tentative; a complete stack with one stray byte is Tentative
    ///     with the tiling failure named (the read then refuses it). Control: the intact complete stack is Confirmed.
    /// </summary>
    [Fact]
    public void IncompleteOrNonTilingStacks_AreTentative()
    {
        var bytes = Stack();

        var partial = Redguard3DcModelTestSupport.Probe(bytes, prefixLength: bytes.Length - 1, isComplete: false);
        Assert.Equal(ModelProbeKind.Supported, partial.Kind);
        Assert.Equal(ModelProbeConfidence.Tentative, partial.Confidence);
        Assert.EndsWith(Redguard3DcModelProbe.IncompleteNote, partial.Evidence!.Description, StringComparison.Ordinal);

        var stray = Redguard3DcModelTestSupport.Probe(bytes.Append((byte)0).ToArray());
        Assert.Equal(ModelProbeKind.Supported, stray.Kind);
        Assert.Equal(ModelProbeConfidence.Tentative, stray.Confidence);
        Assert.Contains(Redguard3DcModelProbe.NotTilingNote, stray.Evidence!.Description, StringComparison.Ordinal);

        Assert.Equal(ModelProbeConfidence.Confirmed, Redguard3DcModelTestSupport.Probe(bytes).Confidence);
    }

    /// <summary>
    ///     NotAModel: a static mesh, a stack whose frame count is zeroed (the shape test's first field; control for the
    ///     shape rule), a 3dfx tag (the <c>.3D</c> reader declines it as Unsupported instead) and a stack whose corner
    ///     addresses no point. Unsupported: a v2.5 stack (see <see cref="V25Stack_IsUnsupported_DecidedBeforeTheWalk" />).
    /// </summary>
    [Fact]
    public void Declines_AreTheRulesTheMetadataStates()
    {
        var bytes = Stack();

        Assert.Equal(ModelProbeKind.NotAModel,
            Redguard3DcModelTestSupport.Probe(XnGineTestMeshBuilder.EightByteRecord(headerPlus20: 1714)).Kind);

        var noFrames = bytes.ToArray();
        noFrames[16] = 0;
        Assert.Equal(ModelProbeKind.NotAModel, Redguard3DcModelTestSupport.Probe(noFrames).Kind);

        var fxart = Redguard3DcModelReaderTests.Retagged(bytes, "v4.0");
        Assert.Equal(ModelProbeKind.NotAModel, Redguard3DcModelTestSupport.Probe(fxart).Kind);
        Assert.Equal(ModelProbeKind.Unsupported, XnGineModelTestSupport.Probe(fxart).Kind);

        var badCorner = bytes.ToArray();
        var planeList = BinaryPrimitives.ReadInt32LittleEndian(badCorner.AsSpan(60));
        BinaryPrimitives.WriteInt32LittleEndian(badCorner.AsSpan(planeList + 8), 3 * 12);
        Assert.Equal(ModelProbeKind.NotAModel, Redguard3DcModelTestSupport.Probe(badCorner).Kind);

        var v25 = Redguard3DcModelTestSupport.Probe(Stack("v2.5"));
        Assert.Equal(ModelProbeKind.Unsupported, v25.Kind);
        Assert.Equal(Redguard3DcModelFormatMetadata.V25UnsupportedReason, v25.Reason);
    }

    /// <summary>
    ///     Slice-6 review finding 1: a v2.5 stack stores its corners untripled, as point index x 4, so its second corner
    ///     (point 1) is stored as 4, which no v2.6/v2.7 byte offset can be. The probe answers Unsupported with the v2.5
    ///     reason because it decides the tag once the shape test holds, before the plane walk; the <c>.3D</c> probe
    ///     answers NotAModel (the shape test holds), so the <c>.3DC</c> reader is the one reader that recognizes it.
    ///     Control: the same bytes under a v2.6 tag go through the walk, which refuses the offset 4 (NotAModel), which is
    ///     what the v2.5 stack got while the walk ran first. (The earlier control retagged a stack whose corners were
    ///     written as index x 12, a layout no v2.5 record has, so it passed whatever the order.)
    /// </summary>
    [Fact]
    public void V25Stack_IsUnsupported_DecidedBeforeTheWalk()
    {
        var bytes = Stack("v2.5");
        var planeList = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(60));
        Assert.Equal(4, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(planeList + 8 + 8)));

        var probe = Redguard3DcModelTestSupport.Probe(bytes);

        Assert.Equal(ModelProbeKind.Unsupported, probe.Kind);
        Assert.Equal(Redguard3DcModelFormatMetadata.V25UnsupportedReason, probe.Reason);
        Assert.Equal(ModelProbeKind.NotAModel, XnGineModelTestSupport.Probe(bytes).Kind);
        Assert.Equal(ModelProbeKind.NotAModel,
            Redguard3DcModelTestSupport.Probe(Redguard3DcModelReaderTests.Retagged(bytes, "v2.6")).Kind);
    }

    /// <summary>
    ///     Slice-6 review finding 4: a complete candidate whose header declares a block outside the file is answered
    ///     Tentative with the declared-size failure BEFORE any parse, never thrown. The review's record (357,913,942
    ///     points, whose x 12 wraps to 8 in int32 so the blocks tile) goes only to the pure check; the probe gets the
    ///     same record with an unaccounted length of 4, which no longer tiles, so its reason tells the order apart (the
    ///     keyframe message comes from the check; the parse would have named the tiling) and a regressed check cannot
    ///     allocate the 4 GiB keyframe. A frame record whose normal offset is 2^31 - 4 wraps its block end below zero;
    ///     with a declared region matching the gap that leaves, the parser tiles it, so without the check the probe said
    ///     Confirmed. Control: the consistent three-point record is Confirmed and its check passes.
    /// </summary>
    [Fact]
    public void DeclaredSizesOutsideTheFile_AreTentativeWithTheReason_BeforeAnyParse()
    {
        var finding = Redguard3DcModelTestSupport.Record(0x15555556, (132, 140, 144), 0, 156);
        Assert.Equal(8, unchecked(0x15555556 * 12));
        var failure = Redguard3DcModelReader.DeclaredSizeFailure(finding);
        Assert.Equal("The header declares 357913942 points, a 4294967304-byte keyframe, more than the 156-byte file " +
                     "holds.", failure);

        var overflow = Redguard3DcModelTestSupport.Probe(
            Redguard3DcModelTestSupport.Record(0x15555556, (132, 140, 144), 4, 156));
        Assert.Equal(ModelProbeKind.Supported, overflow.Kind);
        Assert.Equal(ModelProbeConfidence.Tentative, overflow.Confidence);
        Assert.EndsWith(Redguard3DcModelProbe.NotTilingNote + failure, overflow.Evidence!.Description,
            StringComparison.Ordinal);

        const int wrapping = int.MaxValue - 3;
        var wrapped = Redguard3DcModelTestSupport.Record(3, (132, wrapping, 168), wrapping - 180, 180, (0, 0, 0),
            (256, 0, 0), (0, 0, 256));
        Assert.Equal(int.MinValue, unchecked(wrapping + 4));
        var offset = Redguard3DcModelTestSupport.Probe(wrapped);
        Assert.Equal(ModelProbeConfidence.Tentative, offset.Confidence);
        Assert.EndsWith(Redguard3DcModelProbe.NotTilingNote +
                        "Frame 0's normal block offset 2147483644 lies outside the 180-byte file.",
            offset.Evidence!.Description, StringComparison.Ordinal);

        var consistent = Redguard3DcModelTestSupport.ConsistentRecord();
        Assert.Null(Redguard3DcModelReader.DeclaredSizeFailure(consistent));
        Assert.Equal(ModelProbeConfidence.Confirmed, Redguard3DcModelTestSupport.Probe(consistent).Confidence);
    }

    /// <summary>
    ///     The registry holds the <c>.3D</c> reader and, after it, the <c>.3DC</c> reader (the full list is pinned by
    ///     <c>BethesdaModelRegistrationTests</c>), and each file is recognized exactly once among all the registered
    ///     readers: a stack by the <c>.3DC</c> reader, a static mesh by the <c>.3D</c> reader, a 3dfx-tagged stack by the
    ///     <c>.3D</c> reader alone (Unsupported), a v2.5 stack by the <c>.3DC</c> reader alone (Unsupported).
    /// </summary>
    [Fact]
    public void Registry_RecognizesEachFileExactlyOnce()
    {
        var registry = BethesdaModelRegistration.CreateReaders();
        var ids = registry.Registrations.Select(r => r.FormatId).ToList();
        Assert.InRange(ids.IndexOf("bmt.xngine.3d"), 0, ids.IndexOf("bmt.redguard.3dc") - 1);

        var stack = registry.Probe(Redguard3DcModelTestSupport.Candidate(Stack()));
        Assert.Equal(ModelSourceSelectionKind.Supported, stack.Kind);
        Assert.IsType<Redguard3DcModelReader>(stack.Reader);
        Assert.Single(stack.Matches, m => m.Result.Kind != ModelProbeKind.NotAModel);

        var staticMesh = registry.Probe(Redguard3DcModelTestSupport.Candidate(
            XnGineTestMeshBuilder.EightByteRecord(headerPlus20: 1714)));
        Assert.IsType<XnGineModelReader>(staticMesh.Reader);
        Assert.Single(staticMesh.Matches, m => m.Result.Kind != ModelProbeKind.NotAModel);

        var fxart = registry.Probe(Redguard3DcModelTestSupport.Candidate(
            Redguard3DcModelReaderTests.Retagged(Stack(), "v4.0")));
        var match = Assert.Single(fxart.Matches, m => m.Result.Kind != ModelProbeKind.NotAModel);
        Assert.Equal(ModelProbeKind.Unsupported, match.Result.Kind);
        Assert.Equal("bmt.xngine.3d", match.Registration.FormatId);

        var v25 = registry.Probe(Redguard3DcModelTestSupport.Candidate(Stack("v2.5")));
        var v25Match = Assert.Single(v25.Matches, m => m.Result.Kind != ModelProbeKind.NotAModel);
        Assert.Equal(ModelProbeKind.Unsupported, v25Match.Result.Kind);
        Assert.Equal("bmt.redguard.3dc", v25Match.Registration.FormatId);
    }
}
