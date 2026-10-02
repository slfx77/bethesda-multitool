using System.Text;
using BethesdaMultitool.Core.Formats.Nif.Decoding;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Animation;

/// <summary>
///     The lossless block views beside the key groups, on synthetic blocks in both byte orders: the NiTimeController
///     header's raw clock and flag bits, the NiControllerSequence view (controlled blocks with Priority and string
///     indices, the raw clock, the BS-dependent anim-note tail), NiTextKeyExtraData labels as stored, the
///     NiTransformInterpolator statics, and the six NiBSpline*Interpolator layouts with NiBSplineData and
///     NiBSplineBasisData. Each block is written field by field with distinct values, so a field read from the wrong
///     place fails; each view is also shown keeping what the renderer's reader rejects or rewrites.
/// </summary>
public sealed class NifAnimationViewsTests
{
    private const uint PositiveFloatMax = 0x7F7FFFFF;
    private const uint NegativeFloatMax = 0xFF7FFFFF;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ControllerHeader_ExposesTheStoredClockBitsAndEveryFlag(bool bigEndian)
    {
        var file = new NifAnimationTestFile(bigEndian);
        var controller = file.AddBlock("NiTransformController", static writer => writer
            .I32(-1).U16(0x7F)
            .U32(0x7FC00001) // frequency: a NaN payload
            .U32(0x80000000) // phase: -0
            .U32(PositiveFloatMax).U32(NegativeFloatMax) // the sentinel clock
            .I32(4));
        var data = file.ToArray();

        Assert.True(NifTimeControllerReader.TryRead(data, controller, bigEndian, out var header));

        Assert.Equal(0x7Fu, (uint)header.Flags);
        Assert.Equal(3, header.RawCycle);
        Assert.True(header.IsAppInit);
        Assert.True(header.IsActive);
        Assert.True(header.PlayBackwards);
        Assert.True(header.IsManagerControlled);
        Assert.True(header.ComputeScaledTime);
        Assert.Equal(0x7FC00001u, header.FrequencyBits);
        Assert.Equal(0x80000000u, header.PhaseBits);
        Assert.Equal(PositiveFloatMax, header.StartTimeBits);
        Assert.Equal(NegativeFloatMax, header.StopTimeBits);
        Assert.Equal(-1, header.NextControllerRef);
        Assert.Equal(4, header.TargetRef);

        // 0x4C, the retail default: CLAMP, active, compute scaled time.
        var clamp = new NifTimeControllerHeader(-1, 0x4C, 1f, 0f, 0f, 1f, 0);
        Assert.Equal(2, clamp.RawCycle);
        Assert.Equal(NifCycleType.Clamp, clamp.CycleType);
        Assert.True(clamp.IsActive && clamp.ComputeScaledTime);
        Assert.False(clamp.IsAppInit || clamp.PlayBackwards || clamp.IsManagerControlled);
    }

    [Theory]
    [InlineData(false, 34u)]
    [InlineData(true, 34u)]
    [InlineData(false, 26u)]
    [InlineData(true, 26u)]
    [InlineData(false, 21u)]
    [InlineData(true, 0u)]
    public void SequenceView_KeepsEveryControlledBlockFieldAndTheRawTail(bool bigEndian, uint bsVersion)
    {
        var file = new NifAnimationTestFile(bigEndian, NifVersions.Gamebryo202007, bsVersion);
        var sequence = file.AddBlock("NiControllerSequence", writer => WriteSequence(writer, bsVersion, false));
        var padded = file.AddBlock("NiControllerSequence", writer => WriteSequence(writer, bsVersion, true));
        var data = file.ToArray();

        Assert.True(NifControllerSequenceNameTrackReader.TryReadSequenceView(data, file.Nif, sequence, out var view));

        var hasPriority = bsVersion > 0;
        var stride = hasPriority ? 29 : 28;
        Assert.Equal(0, view.NameIndex);
        Assert.Equal(1u, view.ArrayGrowBy);
        Assert.Equal(2, view.ControlledBlocks.Length);
        Assert.Equal(
            new NifControlledBlockView(sequence.DataOffset + 12, 5, -1, hasPriority ? (byte)23 : null, 1, -1, 2, -1,
                -1),
            view.ControlledBlocks[0]);

        // The same node twice: both blocks are kept, in file order.
        Assert.Equal(
            new NifControlledBlockView(sequence.DataOffset + 12 + stride, 6, 7, hasPriority ? (byte)91 : null, 1, 3,
                2, 4, -1),
            view.ControlledBlocks[1]);
        Assert.Equal(0x3F800000u, view.WeightBits);
        Assert.Equal(9, view.TextKeysRef);
        Assert.Equal(3u, view.RawCycle);
        Assert.Equal(0x40000000u, view.FrequencyBits);
        Assert.Equal(PositiveFloatMax, view.StartTimeBits);
        Assert.Equal(NegativeFloatMax, view.StopTimeBits);
        Assert.Equal(-1, view.ManagerRef);
        Assert.Equal(1, view.AccumRootNameIndex);
        int? expectedAnimNotes = bsVersion is >= 24 and <= 28 ? 11 : null;
        Assert.Equal(expectedAnimNotes, view.AnimNotesRef);
        if (bsVersion > 28)
        {
            Assert.Equal(new[] { 12, 13 }, view.AnimNoteArrayRefs);
        }
        else
        {
            Assert.Null(view.AnimNoteArrayRefs);
        }

        Assert.True(view.TailExact);

        // A trailing byte is reported, not refused.
        Assert.True(NifControllerSequenceNameTrackReader.TryReadSequenceView(data, file.Nif, padded, out var paddedView));
        Assert.False(paddedView.TailExact);
    }

    [Fact]
    public void SequenceView_RefusesTruncatedBlocksAndOtherVersions()
    {
        var file = new NifAnimationTestFile(false);
        var truncated = file.AddBlock("NiControllerSequence", static writer =>
            writer.I32(0).U32(3).U32(1).Words(new uint[20]));
        var data = file.ToArray();
        Assert.False(NifControllerSequenceNameTrackReader.TryReadSequenceView(data, file.Nif, truncated, out _));

        var oblivion = new NifAnimationTestFile(false, NifVersions.Gamebryo20004, 11);
        var legacy = oblivion.AddBlock("NiControllerSequence", static writer => WriteSequence(writer, 11, false));
        Assert.False(NifControllerSequenceNameTrackReader.TryReadSequenceView(
            oblivion.ToArray(), oblivion.Nif, legacy, out _));
    }

    [Fact]
    public void StringIndices_ResolveThroughTheRawTableAsLatin1()
    {
        var strings = new NifHeaderStringTable(0, 16,
        [
            Encoding.ASCII.GetBytes("Bip01"),
            [0x42, 0x69, 0x70, 0xE9] // "Bip" + 0xE9, which NifInfo.Strings' ASCII decode turns into '?'
        ]);

        Assert.True(NifAnimationStrings.TryGetLatin1(strings, 1, out var text));
        Assert.Equal("Bipé", text);
        Assert.True(NifAnimationStrings.TryGetLatin1(strings, NifAnimationStrings.NoString, out var none));
        Assert.Null(none);
        Assert.False(NifAnimationStrings.TryGetLatin1(strings, 2, out _));
        Assert.False(NifAnimationStrings.TryGetLatin1(strings, -2, out _));
        Assert.True(NifAnimationStrings.TryGetRaw(strings, 1, out var raw, out var isNone));
        Assert.False(isNone);
        Assert.Equal(new byte[] { 0x42, 0x69, 0x70, 0xE9 }, raw.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TextKeyView_KeepsIndexedLabelsThatTheRendererRejects(bool bigEndian)
    {
        var file = new NifAnimationTestFile(bigEndian);
        var block = file.AddBlock("NiTextKeyExtraData", static writer => writer
            .I32(-1).U32(3)
            .F32(0f).I32(0)
            .F32(0.5f).I32(1)
            .U32(0xBF800000).I32(-1));
        file.Nif.Strings.Add("start\r\nStartLoop");
        file.Nif.Strings.Add("  ");
        var data = file.ToArray();

        Assert.True(NifTextKeyReader.TryReadView(data, file.Nif, block, out var view));

        Assert.False(view.InlineStrings);
        Assert.Equal(-1, view.NameIndex);
        Assert.Null(view.LegacyNextExtraDataRef);
        Assert.Equal(
            new[]
            {
                new NifTextKeyView(0, 0, ReadOnlyMemory<byte>.Empty),
                new NifTextKeyView(0x3F000000, 1, ReadOnlyMemory<byte>.Empty),
                new NifTextKeyView(0xBF800000, -1, ReadOnlyMemory<byte>.Empty)
            },
            view.Keys);
        Assert.True(view.ConsumedExactly);

        // Control: the renderer's exact reader refuses the block (a padded label, a missing label), the view does not.
        Assert.False(NifTextKeyReader.TryReadExact(data, file.Nif, block, out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TextKeyView_InlineLabelsAreTheStoredBytes(bool bigEndian)
    {
        var file = new NifAnimationTestFile(bigEndian, NifVersions.Gamebryo20004, 11);
        var block = file.AddBlock("NiTextKeyExtraData", static writer => writer
            .U32(4).Raw(0x4B, 0x65, 0x79, 0x73) // name "Keys"
            .U32(2)
            .F32(0f).U32(0) // an empty label
            .F32(1f).U32(4).Raw(0x45, 0x6E, 0xE9, 0x0D)); // "En", 0xE9, CR
        var data = file.ToArray();

        Assert.True(NifTextKeyReader.TryReadView(data, file.Nif, block, out var view));

        Assert.True(view.InlineStrings);
        Assert.Equal("Keys", Encoding.Latin1.GetString(view.InlineName.Span));
        Assert.Equal(2, view.Keys.Length);
        Assert.Equal(0, view.Keys[0].InlineLabel.Length);
        Assert.Equal(0x3F800000u, view.Keys[1].TimeBits);
        Assert.Equal(new byte[] { 0x45, 0x6E, 0xE9, 0x0D }, view.Keys[1].InlineLabel.ToArray());
        Assert.True(view.ConsumedExactly);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TextKeyView_ReadsTheLegacyNetImmerseHead(bool bigEndian)
    {
        var file = new NifAnimationTestFile(bigEndian, NifVersions.NetImmerse4002, 0);
        var block = file.AddBlock("NiTextKeyExtraData", static writer => writer
            .I32(-1).U32(21)
            .U32(1)
            .F32(0.25f).U32(5).Raw(0x49, 0x64, 0x6C, 0x65, 0x3A)); // "Idle:"
        var data = file.ToArray();

        Assert.True(NifTextKeyReader.TryReadView(data, file.Nif, block, out var view));

        Assert.Equal(-1, view.LegacyNextExtraDataRef);
        Assert.Equal(21u, view.LegacyRecordSize);
        Assert.Equal(0, view.InlineName.Length);
        Assert.Equal("Idle:", Encoding.Latin1.GetString(Assert.Single(view.Keys).InlineLabel.Span));
    }

    [Theory]
    [InlineData(false, "NiTransformInterpolator")]
    [InlineData(true, "BSRotAccumTransfInterpolator")]
    public void TransformInterpolatorView_KeepsTheStaticsInFileOrder(bool bigEndian, string typeName)
    {
        var file = new NifAnimationTestFile(bigEndian);
        var block = file.AddBlock(typeName, static writer => writer
            .Words(0x3F800000, 0x40000000, 0x40400000) // translation
            .Words(0x3F000001, 0x3F000002, 0x3F000003, 0x3F000004) // rotation w, x, y, z
            .U32(PositiveFloatMax) // an undriven scale: the sentinel
            .I32(3));
        var data = file.ToArray();

        Assert.True(NifControllerSequenceNameTrackReader.TryReadTransformInterpolatorView(
            data, file.Nif, block, out var view));

        Assert.Equal(
            new NifTransformInterpolatorView(0x3F800000, 0x40000000, 0x40400000, 0x3F000001, 0x3F000002,
                0x3F000003, 0x3F000004, PositiveFloatMax, 3),
            view);
    }

    [Theory]
    [InlineData("NiBSplineFloatInterpolator")]
    [InlineData("NiBSplineCompFloatInterpolator")]
    [InlineData("NiBSplinePoint3Interpolator")]
    [InlineData("NiBSplineCompPoint3Interpolator")]
    [InlineData("NiBSplineTransformInterpolator")]
    [InlineData("NiBSplineCompTransformInterpolator")]
    public void BsplineInterpolatorView_ReadsEveryFieldOfEachLayout(string typeName)
    {
        var compact = typeName.Contains("Comp", StringComparison.Ordinal);
        var staticWords = typeName.Contains("Float", StringComparison.Ordinal) ? 1
            : typeName.Contains("Point3", StringComparison.Ordinal) ? 3
            : 8;
        var channels = staticWords == 8 ? 3 : 1;
        foreach (var bigEndian in new[] { false, true })
        {
            var statics = Enumerable.Range(0, staticWords).Select(static i => 0x3E000000u + (uint)i).ToArray();
            statics[^1] = PositiveFloatMax;
            var handles = Enumerable.Range(0, channels).Select(static i => i == 0 ? 0xFFFFu : (uint)(i * 100))
                .ToArray();
            var file = new NifAnimationTestFile(bigEndian);
            var block = file.AddBlock(typeName, writer =>
            {
                writer.U32(0).U32(0x40A00000).I32(1).I32(2).Words(statics).Words(handles);
                if (compact)
                {
                    for (var channel = 0; channel < channels; channel++)
                    {
                        writer.U32(0x3D000000u + (uint)channel).U32(0xBC000000u + (uint)channel); // a negative half range
                    }
                }
            });
            var longer = file.AddBlock(typeName, writer =>
            {
                writer.U32(0).U32(0x40A00000).I32(1).I32(2).Words(statics).Words(handles);
                if (compact)
                {
                    writer.Words(new uint[2 * channels]);
                }

                writer.U8(0);
            });
            var data = file.ToArray();

            Assert.True(NifBsplineTransformReader.TryReadInterpolatorView(data, file.Nif, block, out var view));

            Assert.Equal(typeName, view.TypeName);
            Assert.Equal(compact, view.Compact);
            Assert.Equal(0u, view.StartTimeBits);
            Assert.Equal(0x40A00000u, view.StopTimeBits);
            Assert.Equal(1, view.SplineDataRef);
            Assert.Equal(2, view.BasisDataRef);
            Assert.Equal(statics, view.StaticValueBits);
            Assert.Equal(handles, view.Handles);
            Assert.Equal(channels, view.ChannelCount);
            Assert.Equal(
                compact
                    ? Enumerable.Range(0, channels).Select(static c => 0x3D000000u + (uint)c).ToArray()
                    : Array.Empty<uint>(),
                view.OffsetBits);
            Assert.Equal(
                compact
                    ? Enumerable.Range(0, channels).Select(static c => 0xBC000000u + (uint)c).ToArray()
                    : Array.Empty<uint>(),
                view.HalfRangeBits);

            // The layout is fixed-size: a block one byte longer is not this type's layout.
            Assert.False(NifBsplineTransformReader.TryReadInterpolatorView(data, file.Nif, longer, out _));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BsplineDataAndBasisViews_KeepTheRawArrays(bool bigEndian)
    {
        var file = new NifAnimationTestFile(bigEndian);
        var store = file.AddBlock("NiBSplineData", static writer => writer
            .U32(2).Words(0x7FC00001, 0x80000000)
            .U32(4).I16(-32767).I16(32767).I16(-1).I16(0));
        var padded = file.AddBlock("NiBSplineData", static writer => writer.U32(0).U32(1).I16(5).U16(0));
        var truncated = file.AddBlock("NiBSplineData", static writer => writer.U32(0).U32(3).I16(5));
        var basis = file.AddBlock("NiBSplineBasisData", static writer => writer.U32(2)); // below the renderer's 4
        var data = file.ToArray();

        Assert.True(NifBsplineTransformReader.TryReadDataView(data, file.Nif, store, out var view));
        Assert.Equal(2, view.FloatControlPointCount);
        Assert.Equal(0x7FC00001u, view.FloatControlPointBits(0));
        Assert.Equal(0x80000000u, view.FloatControlPointBits(1));
        Assert.Equal(new short[] { -32767, 32767, -1, 0 },
            Enumerable.Range(0, view.CompactControlPointCount).Select(view.CompactControlPoint).ToArray());
        Assert.True(view.ConsumedExactly);

        Assert.True(NifBsplineTransformReader.TryReadDataView(data, file.Nif, padded, out var paddedView));
        Assert.False(paddedView.ConsumedExactly);
        Assert.False(NifBsplineTransformReader.TryReadDataView(data, file.Nif, truncated, out _));

        Assert.True(NifBsplineTransformReader.TryReadBasisView(data, file.Nif, basis, out var count));
        Assert.Equal(2u, count);
    }

    /// <summary>
    ///     A 20.2.0.7 sequence: name index 0, two controlled blocks naming node index 1 twice, weight 1, text keys 9, the
    ///     undefined cycle 3, frequency 2, the sentinel clock, no manager, accum root index 1, then the BS-dependent tail.
    /// </summary>
    private static void WriteSequence(NifAnimationByteWriter writer, uint bsVersion, bool trailingByte)
    {
        var hasPriority = bsVersion > 0;
        writer.I32(0).U32(2).U32(1);
        writer.I32(5).I32(-1);
        if (hasPriority)
        {
            writer.U8(23);
        }

        writer.I32(1).I32(-1).I32(2).I32(-1).I32(-1);
        writer.I32(6).I32(7);
        if (hasPriority)
        {
            writer.U8(91);
        }

        writer.I32(1).I32(3).I32(2).I32(4).I32(-1);
        writer.U32(0x3F800000).I32(9).U32(3).U32(0x40000000).U32(PositiveFloatMax).U32(NegativeFloatMax)
            .I32(-1).I32(1);
        if (bsVersion is >= 24 and <= 28)
        {
            writer.I32(11);
        }
        else if (bsVersion > 28)
        {
            writer.U16(2).I32(12).I32(13);
        }

        if (trailingByte)
        {
            writer.U8(0);
        }
    }
}
