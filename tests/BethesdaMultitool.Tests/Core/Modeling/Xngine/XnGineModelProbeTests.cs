using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Modeling;
using BethesdaMultitool.Core.Modeling.Nif;
using BethesdaMultitool.Core.Modeling.Redguard;
using BethesdaMultitool.Core.Modeling.Xngine;
using BethesdaMultitool.Tests.Core.Modeling.Nif;
using BethesdaMultitool.Tests.Helpers;
using Slfx77.Multitool.Core.Assets;
using Slfx77.Multitool.Core.Models.Sources;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Modeling.Xngine;

/// <summary>
///     The <c>bmt.xngine.3d</c> probe (cut-1c plan section 6.1, slice 5): Supported and Confirmed for a complete record
///     under either plane-header size, Tentative when the record extends past the prefix, NotAModel for a <c>.3DC</c>
///     (the shape test), for strays and empty payloads, Unsupported for the 3dfx tags, and exactly one reader of the
///     registry recognizing each file. Every positive answer is paired with a control that flips it by changing the one
///     thing the rule reads.
/// </summary>
public sealed class XnGineModelProbeTests
{
    [Fact]
    public void CompleteEightByteRecord_IsSupportedAndConfirmed_WithTheCountsAsEvidence()
    {
        var bytes = XnGineTestMeshBuilder.EightByteRecord(headerPlus20: 1714);

        var result = XnGineModelTestSupport.Probe(bytes);

        Assert.Equal(ModelProbeKind.Supported, result.Kind);
        Assert.Equal(ModelProbeConfidence.Confirmed, result.Confidence);
        Assert.Equal("XnGine v2.7 mesh, 8-byte plane headers, 4 points, 1 planes", result.Evidence!.Description);
        Assert.Equal(0, result.Evidence.ByteOffset);
        Assert.Equal(bytes.Length, result.Evidence.ByteLength);
    }

    [Fact]
    public void CompleteTenByteRecord_IsSupported_WithTenBytePlaneHeaders()
    {
        var result = XnGineModelTestSupport.Probe(XnGineTestMeshBuilder.TenByteRecord());

        Assert.Equal(ModelProbeKind.Supported, result.Kind);
        Assert.Equal("XnGine v2.7 mesh, 10-byte plane headers, 4 points, 1 planes", result.Evidence!.Description);
        Assert.Contains(XnGineModelFormatMetadata.DescribeVariant("v2.7", 10),
            XnGineModelFormatMetadata.Description.SupportedVariants);
    }

    /// <summary>
    ///     The probe's evidence, without its counts, is exactly one of the format metadata's variants for every mesh tag
    ///     and plane-header size (the slice-3 description promised it; slice 5 keeps it true). Control: a variant the
    ///     probe never produces (another header size) is not the evidence.
    /// </summary>
    [Theory]
    [InlineData("v2.5", 8)]
    [InlineData("v2.6", 8)]
    [InlineData("v2.7", 8)]
    [InlineData("v2.5", 10)]
    [InlineData("v2.6", 10)]
    [InlineData("v2.7", 10)]
    public void Evidence_IsAMetadataVariantWithTheCounts(string tag, int planeHeaderLength)
    {
        var builder = new XnGineTestMeshBuilder(tag, planeHeaderLength) { HeaderPlus20 = 1714 };
        builder.AddPoint(0, 0, 0);
        builder.AddPoint(256, 0, 0);
        builder.AddPoint(0, 0, 256);
        builder.AddPlane(0x0182, [(0, 0, 0), (1, 4096, 0), (2, 0, 4096)], (0, -256, 0));

        var result = XnGineModelTestSupport.Probe(builder.Build());

        var variant = XnGineModelFormatMetadata.DescribeVariant(tag, planeHeaderLength);
        Assert.Equal(ModelProbeKind.Supported, result.Kind);
        Assert.Equal(variant + ", 3 points, 1 planes", result.Evidence!.Description);
        Assert.Contains(variant, XnGineModelFormatMetadata.Description.SupportedVariants);
        Assert.DoesNotContain(XnGineModelFormatMetadata.DescribeVariant(tag, 18 - planeHeaderLength),
            result.Evidence.Description, StringComparison.Ordinal);
    }

    /// <summary>A record the prefix does not hold completely is Tentative; the same bytes complete are Confirmed (the control).</summary>
    [Fact]
    public void RecordPastThePrefix_IsTentative()
    {
        var bytes = XnGineTestMeshBuilder.EightByteRecord(headerPlus20: 1714);

        var partial = XnGineModelTestSupport.Probe(bytes, bytes.Length - 3, isComplete: false);
        var complete = XnGineModelTestSupport.Probe(bytes);

        Assert.Equal(ModelProbeKind.Supported, partial.Kind);
        Assert.Equal(ModelProbeConfidence.Tentative, partial.Confidence);
        Assert.EndsWith(XnGineModelProbe.IncompleteNote, partial.Evidence!.Description, StringComparison.Ordinal);
        Assert.Equal(ModelProbeConfidence.Confirmed, complete.Confidence);

        // Completeness is judged by what the helper saw, never by the declared length: the whole record handed over
        // as incomplete stays Tentative.
        Assert.Equal(ModelProbeConfidence.Tentative,
            XnGineModelTestSupport.Probe(bytes, bytes.Length, isComplete: false).Confidence);
    }

    /// <summary>A record satisfying the .3DC shape test belongs to the .3DC reader; with +16 = 0 the same bytes are a .3D.</summary>
    [Fact]
    public void AnimatedShape_IsNotAModel_AndTheFrameCountIsWhatDeclinedIt()
    {
        var animated = XnGineModelTestSupport.AnimatedShapeRecord(frames: 1);
        var control = XnGineModelTestSupport.AnimatedShapeRecord(frames: 0);

        Assert.True(XnGineModelProbe.SatisfiesAnimatedShape(animated));
        Assert.True(XnGineContentFacts.Measure(animated).WalksWithDaggerfallLayout);
        Assert.Equal(ModelProbeKind.NotAModel, XnGineModelTestSupport.Probe(animated).Kind);

        Assert.False(XnGineModelProbe.SatisfiesAnimatedShape(control));
        Assert.Equal(ModelProbeKind.Supported, XnGineModelTestSupport.Probe(control).Kind);
    }

    /// <summary>
    ///     MENU.ROB's shape: header +16 non-zero (9) but no frame block, so the shape test fails and the record stays a
    ///     static mesh; the reader then carries the static +16 diagnostic.
    /// </summary>
    [Fact]
    public void NonZeroPlus16WithoutAFrameBlock_StaysStatic()
    {
        var bytes = XnGineTestMeshBuilder.EightByteRecord(headerPlus20: 0);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), 9);

        Assert.False(XnGineModelProbe.SatisfiesAnimatedShape(bytes));
        Assert.Equal(ModelProbeKind.Supported, XnGineModelTestSupport.Probe(bytes).Kind);
        var result = XnGineModelTestSupport.Read(bytes, XnGineModelTestSupport.Game("daggerfall"));
        Assert.Contains(result.Document.Diagnostics, d => d.Code == XnGineModelDiagnostics.StaticFrameCount);
    }

    [Theory]
    [InlineData("v5.0")]
    [InlineData("v4.0")]
    public void FxartTag_IsUnsupported_WithThePlanReason(string tag)
    {
        var bytes = XnGineTestMeshBuilder.EightByteRecord(headerPlus20: 1714);
        Encoding.ASCII.GetBytes(tag).CopyTo(bytes, 0);

        var result = XnGineModelTestSupport.Probe(bytes);

        Assert.Equal(ModelProbeKind.Unsupported, result.Kind);
        Assert.Equal(ModelProbeConfidence.Tentative, result.Confidence);
        Assert.Equal("Redguard 3dfx mesh (10-byte plane header, point indices) not decoded: later cut", result.Reason);
        Assert.Equal(XnGineModelFormatMetadata.FxartUnsupportedReason, result.Reason);

        // Control: the tag is the only difference from a supported record.
        Assert.Equal(ModelProbeKind.Supported,
            XnGineModelTestSupport.Probe(XnGineTestMeshBuilder.EightByteRecord(headerPlus20: 1714)).Kind);
    }

    [Fact]
    public void Strays_AreNotAModel()
    {
        var mz = new byte[512];
        "MZ"u8.CopyTo(mz);
        var tagOnly = new byte[40];
        "v2.7"u8.CopyTo(tagOnly);
        var brokenWalk = XnGineTestMeshBuilder.EightByteRecord(headerPlus20: 1714);
        BinaryPrimitives.WriteInt32LittleEndian(brokenWalk.AsSpan(60), brokenWalk.Length + 4);

        Assert.Equal(ModelProbeKind.NotAModel, XnGineModelTestSupport.Probe(mz).Kind);
        Assert.Equal(ModelProbeKind.NotAModel, XnGineModelTestSupport.Probe([]).Kind);
        Assert.Equal(ModelProbeKind.NotAModel, XnGineModelTestSupport.Probe(tagOnly).Kind);
        Assert.Equal(ModelProbeKind.NotAModel, XnGineModelTestSupport.Probe(brokenWalk).Kind);
    }

    /// <summary>
    ///     The registry holds the XnGine readers (the full list is pinned by <c>BethesdaModelRegistrationTests</c>) and each
    ///     file is recognized exactly once: XnGine content by the XnGine reader, NIF content by the NIF reader, and a
    ///     record satisfying the .3DC shape test by the .3DC reader alone (since slice 6; this one is Tentative because
    ///     its fake frame table, the record's normal (0, 256, 0) read as offsets, names a normal block at 256, past the
    ///     188-byte record, so the read would refuse it).
    /// </summary>
    [Fact]
    public void Registry_RecognizesEachFileExactlyOnce()
    {
        var registry = BethesdaModelRegistration.CreateReaders();
        var ids = registry.Registrations.Select(r => r.FormatId).ToList();
        Assert.Contains("bmt.xngine.3d", ids);
        Assert.Contains("bmt.redguard.3dc", ids);

        var xngine = registry.Probe(Candidate(XnGineTestMeshBuilder.EightByteRecord(headerPlus20: 1714)));
        Assert.Equal(ModelSourceSelectionKind.Supported, xngine.Kind);
        Assert.IsType<XnGineModelReader>(xngine.Reader);
        Assert.Single(xngine.Matches, m => m.Result.Kind != ModelProbeKind.NotAModel);

        var nifBuilder = new NifTestFileBuilder(false, 34);
        NifModelTestSupport.AddNode(nifBuilder, nifBuilder.AddString("Scene Root"), []);
        var nif = registry.Probe(Candidate(nifBuilder.Build()));
        Assert.Equal(ModelSourceSelectionKind.Supported, nif.Kind);
        Assert.IsType<NifModelReader>(nif.Reader);

        var animated = registry.Probe(Candidate(XnGineModelTestSupport.AnimatedShapeRecord()));
        Assert.Equal(ModelSourceSelectionKind.Supported, animated.Kind);
        Assert.IsType<Redguard3DcModelReader>(animated.Reader);
        var match = Assert.Single(animated.Matches, m => m.Result.Kind != ModelProbeKind.NotAModel);
        Assert.Equal(ModelProbeConfidence.Tentative, match.Result.Confidence);

        // Control: without the frame count the record fails the shape test and is the .3D reader's alone.
        var plain = registry.Probe(Candidate(XnGineModelTestSupport.AnimatedShapeRecord(frames: 0)));
        Assert.IsType<XnGineModelReader>(plain.Reader);
        Assert.Single(plain.Matches, m => m.Result.Kind != ModelProbeKind.NotAModel);
    }

    private static ModelSourceCandidate Candidate(byte[] bytes)
    {
        return new ModelSourceCandidate(new AssetEntry(new AssetReference("memory", "any/name.dat"), bytes.Length),
            bytes, true);
    }
}
