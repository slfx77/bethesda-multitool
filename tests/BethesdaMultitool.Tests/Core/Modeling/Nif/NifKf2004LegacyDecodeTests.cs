using BethesdaMultitool.Core.Formats.Nif.Decoding;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;
using BethesdaMultitool.Core.Formats.Nif.Schema;
using BethesdaMultitool.Core.Modeling.Nif;
using BethesdaMultitool.Tests.Core.Formats.Nif.Decoding;
using Slfx77.Multitool.Core.Models.Sources;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Modeling.Nif;

/// <summary>
///     Cut-2 preparation (TestOutput/cut2-prep-20260928/kf2004): the one decode path walks the little-endian
///     20.0.0.4 user-10/11 BS-11 <c>.kf</c> identity. Over the synthetic <see cref="NifKf2004Fixtures" /> file:
///     <see cref="NifHeaderLayout" /> reads the legacy header (no Block Size array, no string table; sizes from the
///     legacy measure walk), <see cref="NifBlockDecoder" /> decodes every block strictly and the layout tiles to the
///     footer, and <see cref="NifControllerSequenceNameTrackReader.TryReadOblivionSequenceView" /> reads the sequence
///     with its palette-resolved targets. The real five FNV-shipped files are pinned by
///     <c>RealAsset/NifKf2004RetailDecodeTests</c>.
/// </summary>
/// <remarks>
///     Controls, each able to fail alone: the 20.2.0.7 sequence view must still refuse the legacy file and the
///     Oblivion view must refuse a 20.2.0.7 <c>.kf</c> (the version keying discriminates); every other pre-20.2.0.5
///     identity must keep the decoder's <see cref="NotSupportedException" /> (BS 12, user 9, and 20.0.0.5, which the
///     renderer's clip reader accepts but the decoder deliberately does not until a shipped file exists); a corrupted
///     sequence body and a truncated file must abort the measure walk and refuse the whole block list
///     (<see cref="InvalidDataException" />), never emit wrong offsets; and the model probe admits the identity as a
///     <c>.kf</c> stream (cut 2) while a 20.0.0.4 scene graph of the same identity stays <c>later-cut(2)</c>.
/// </remarks>
public sealed class NifKf2004LegacyDecodeTests
{
    /// <summary>The legacy header layout: identity fields, type table, measured sizes, empty string table.</summary>
    [Fact]
    public void LegacyHeaderLayout_ReadsTheKfIdentity()
    {
        var bytes = NifKf2004Fixtures.Build();
        var decoder = NifDecodingTestSupport.Open(bytes);
        var header = decoder.Header;
        Assert.Equal(0x14000004u, header.Version);
        Assert.False(header.IsBigEndian);
        Assert.Equal(11u, header.UserVersion);
        Assert.Equal(11u, header.BsVersion);
        Assert.Equal(5, header.BlockCount);
        Assert.Equal(NifKf2004Fixtures.BlockTypes, header.BlockTypeNames);
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, header.BlockTypeIndices.Select(static i => (int)i).ToArray());
        Assert.Equal(0, header.Strings.Count);
        Assert.Empty(header.Groups);

        var info = NifParser.Parse(bytes);
        Assert.NotNull(info);
        Assert.Equal(info.Blocks[0].DataOffset, header.HeaderEnd);
        Assert.Equal(info.Blocks.Select(static b => (uint)b.Size).ToArray(), header.BlockSizes);
    }

    /// <summary>Every block decodes strictly, the blocks and footer tile the file, and the root is the sequence.</summary>
    [Fact]
    public void Decoder_DecodesEveryBlockStrictly_AndTheLayoutTiles()
    {
        var bytes = NifKf2004Fixtures.Build();
        var decoder = NifDecodingTestSupport.Open(bytes);
        var footer = decoder.ValidateLayout();
        Assert.Equal(new[] { 0 }, footer.Roots.ToArray());
        for (var i = 0; i < decoder.BlockCount; i++)
        {
            var block = decoder.Decode(i, NifDecodeMode.Strict);
            Assert.True(block.IsComplete, $"block {i} ({block.Type}) did not decode completely.");
        }

        var sequence = decoder.Decode(0, NifDecodeMode.Strict).Root;
        Assert.Equal(NifKf2004Fixtures.SequenceName, sequence.Get<NifSizedStringValue>("Name").Text);
        Assert.Equal(1, sequence.Get<NifIntegerValue>("Num Controlled Blocks").Value);
        Assert.Equal(NifKf2004Fixtures.TargetName, sequence.Get<NifSizedStringValue>("Accum Root Name").Text);
        var transformData = decoder.Decode(3, NifDecodeMode.Strict).Root;
        Assert.Equal(1, transformData.Get<NifIntegerValue>("Num Rotation Keys").Value);
        var textKeys = decoder.Decode(4, NifDecodeMode.Strict).Root;
        Assert.Equal(2, textKeys.Get<NifIntegerValue>("Num Text Keys").Value);
    }

    /// <summary>The Oblivion sequence view: inline names, palette-resolved target, raw clock bits, exact tail.</summary>
    [Fact]
    public void OblivionSequenceView_ReadsTheSequence()
    {
        var bytes = NifKf2004Fixtures.Build();
        var info = NifParser.Parse(bytes);
        Assert.NotNull(info);
        Assert.True(NifControllerSequenceNameTrackReader.TryReadOblivionSequenceView(
            bytes, info, info.Blocks[0], out var view));
        Assert.Equal(NifKf2004Fixtures.SequenceName, view.Name);
        Assert.Equal(NifKf2004Fixtures.TargetName, view.AccumRootName);
        Assert.Equal(1u, view.ArrayGrowBy);
        var controlled = Assert.Single(view.ControlledBlocks);
        Assert.Equal(1, controlled.InterpolatorRef);
        Assert.Equal(-1, controlled.ControllerRef);
        Assert.Equal((byte)7, controlled.Priority);
        Assert.Equal(2, controlled.StringPaletteRef);
        Assert.Equal(0u, controlled.NodeNameOffset);
        Assert.Equal(NifKf2004Fixtures.TargetName, controlled.NodeName);
        Assert.Equal(uint.MaxValue, controlled.PropertyTypeOffset);
        Assert.Null(controlled.PropertyType);
        Assert.Null(controlled.ControllerType);
        Assert.Equal(BitConverter.SingleToUInt32Bits(1f), view.WeightBits);
        Assert.Equal(4, view.TextKeysRef);
        Assert.Equal(2u, view.RawCycle);
        Assert.Equal(BitConverter.SingleToUInt32Bits(1f), view.FrequencyBits);
        Assert.Equal(BitConverter.SingleToUInt32Bits(0f), view.StartTimeBits);
        Assert.Equal(BitConverter.SingleToUInt32Bits(1f), view.StopTimeBits);
        Assert.Equal(-1, view.ManagerRef);
        Assert.Equal(2, view.StringPaletteRef);
        Assert.True(view.TailExact);
    }

    /// <summary>Control: the 20.2.0.7 sequence view must keep refusing the legacy file.</summary>
    [Fact]
    public void ModernSequenceView_StillRefusesTheLegacyFile()
    {
        var bytes = NifKf2004Fixtures.Build();
        var info = NifParser.Parse(bytes);
        Assert.NotNull(info);
        Assert.False(NifControllerSequenceNameTrackReader.TryReadSequenceView(bytes, info, info.Blocks[0], out _));
    }

    /// <summary>Control: the Oblivion view must refuse a 20.2.0.7 <c>.kf</c> (the version keying discriminates).</summary>
    [Fact]
    public void OblivionSequenceView_RefusesAModernKf()
    {
        var bytes = NifModelKfFixtures.Kf(false, 34, NifModelKfFixtures.PelvisName);
        var info = NifParser.Parse(bytes);
        Assert.NotNull(info);
        Assert.False(NifControllerSequenceNameTrackReader.TryReadOblivionSequenceView(
            bytes, info, info.Blocks[0], out _));
    }

    /// <summary>
    ///     Control: every other pre-20.2.0.5 identity keeps the decoder's refusal: a changed BS version, a user
    ///     version outside 10/11, and 20.0.0.5 (no shipped file; the decoder stays narrower than the clip reader).
    /// </summary>
    [Theory]
    [InlineData(0x14000004u, 11u, 12u)]
    [InlineData(0x14000004u, 9u, 11u)]
    [InlineData(0x14000005u, 11u, 11u)]
    public void Decoder_StillRefusesEveryOtherLegacyIdentity(uint version, uint userVersion, uint bsVersion)
    {
        var bytes = NifKf2004Fixtures.Build(version, userVersion, bsVersion);
        var info = NifParser.Parse(bytes);
        Assert.NotNull(info);
        Assert.Throws<NotSupportedException>(() =>
            new NifBlockDecoder(NifSchema.LoadEmbedded(), info, bytes));
    }

    /// <summary>
    ///     Control: an over-declared controlled-block count desyncs the measure walk, which must refuse the whole
    ///     block list (no header Block Size array exists to recover with), so the decoder refuses the file.
    /// </summary>
    [Fact]
    public void Decoder_RefusesACorruptedSequenceBody()
    {
        var bytes = NifKf2004Fixtures.Build(declaredControlledBlocks: 9999);
        var info = NifParser.Parse(bytes);
        Assert.NotNull(info);
        var error = Assert.Throws<InvalidDataException>(() =>
            new NifBlockDecoder(NifSchema.LoadEmbedded(), info, bytes));
        Assert.Contains("measure walk", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Control: a file truncated inside its first block aborts the measure walk and is refused whole.</summary>
    [Fact]
    public void Decoder_RefusesATruncatedFile()
    {
        var whole = NifKf2004Fixtures.Build();
        var info = NifParser.Parse(whole);
        Assert.NotNull(info);
        var truncated = whole[..(info.Blocks[0].DataOffset + 8)];
        var truncatedInfo = NifParser.Parse(truncated);
        Assert.NotNull(truncatedInfo);
        Assert.Throws<InvalidDataException>(() =>
            new NifBlockDecoder(NifSchema.LoadEmbedded(), truncatedInfo, truncated));
    }

    /// <summary>
    ///     Cut 2: the model probe admits the identity as a <c>.kf</c> animation stream (Supported, Confirmed, the .kf
    ///     evidence suffix), together with the gate-1a and gate-1b rows for the five files
    ///     (TestOutput/cut2-prep-20260928/kf2004-carry/IMPLEMENTATION.md). Control: the scene-graph fixture of the
    ///     same identity stays later-cut(2), and the header layout still reads it (the decline is the roots', not the
    ///     header's).
    /// </summary>
    [Fact]
    public void Probe_AdmitsTheLegacyKfIdentity_AndDeclinesItsSceneGraph()
    {
        var result = NifModelTestSupport.Probe(NifKf2004Fixtures.Build());
        Assert.Equal(ModelProbeKind.Supported, result.Kind);
        Assert.Equal(ModelProbeConfidence.Confirmed, result.Confidence);
        Assert.Null(result.Reason);
        Assert.Equal("NIF 20.0.0.4, user 11, BS 11, little-endian" + NifModelProbe.AnimationStreamSuffix,
            result.Evidence!.Description);

        var sceneGraph = NifKf2004Fixtures.BuildSceneGraph();
        var control = NifModelTestSupport.Probe(sceneGraph);
        Assert.Equal(ModelProbeKind.Unsupported, control.Kind);
        Assert.StartsWith(NifModelProbe.LaterCutTwoCategory, control.Reason, StringComparison.Ordinal);
        var decoder = NifDecodingTestSupport.Open(sceneGraph);
        Assert.Equal(new[] { "NiNode" }, decoder.Header.BlockTypeNames);
        Assert.Equal(new[] { 0 }, decoder.ValidateLayout().Roots.ToArray());
    }
}
