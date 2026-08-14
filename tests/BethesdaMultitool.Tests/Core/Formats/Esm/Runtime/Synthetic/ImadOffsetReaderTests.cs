using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Esm.Runtime;
using BethesdaMultitool.Core.Minidump;
using Xunit;
using static BethesdaMultitool.Tests.Helpers.BinaryTestWriter;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Runtime.Synthetic;

[CollectionDefinition("RuntimeImageSpaceModifierDiagnostics", DisableParallelization = true)]
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix",
    Justification = "xUnit [CollectionDefinition] marker type; the 'Collection' suffix is idiomatic for these.")]
public sealed class RuntimeImageSpaceModifierDiagnosticsCollection;

[Collection("RuntimeImageSpaceModifierDiagnostics")]
public sealed class ImadOffsetReaderTests
{
    private const uint BaseVa = 0x40000000;
    private const uint FormId = 0x000CDA79;

    private static readonly TestLayout Early = new(
        0x740, 0x28, 0x65C,
        0x704, 0x738, false);

    private static readonly TestLayout Final = new(
        0x748, 0x30, 0x664,
        0x70C, 0x740, true);

    private static readonly Dictionary<string, int> NamedPointerOrdinals =
        new(StringComparer.Ordinal)
        {
            ["BNAM"] = 0,
            ["VNAM"] = 1,
            ["TNAM"] = 2,
            ["NAM3"] = 3,
            ["RNAM"] = 4,
            ["SNAM"] = 5,
            ["UNAM"] = 6,
            ["NAM1"] = 7,
            ["NAM2"] = 8,
            ["WNAM"] = 9,
            ["XNAM"] = 10,
            ["YNAM"] = 11,
            ["NAM4"] = 12
        };

    [Fact]
    public void EarlyLayout_ReconstructsXex21ShapeAndPackedDnamInCanonicalLittleEndian()
    {
        var fixture = Build(Early, _ => 2);
        var result = new RuntimeImageSpaceModifierReader(fixture.Context, true)
            .ReadRuntimeImageSpaceModifier(fixture.Entry);

        Assert.NotNull(result);
        Assert.True(result.FromRuntime);
        Assert.False(result.IsBigEndian);
        Assert.Null(result.IntroSoundFormId);
        Assert.Null(result.OutroSoundFormId);
        Assert.Equal(57, result.OrderedSubrecords.Count); // EDID + DNAM + 55 count=2 tables
        Assert.All(ImageSpaceModifierCaptureValidator.FrameTableLayouts, layout =>
        {
            var table = Assert.Single(result.OrderedSubrecords, sub => sub.Signature == layout.Signature);
            Assert.Equal(2 * layout.ElementSize, table.Data.Length);
        });

        var dnam = Assert.Single(result.OrderedSubrecords, sub => sub.Signature == "DNAM").Data;
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(dnam.AsSpan(0, 4)));
        Assert.Equal(4.25f, BinaryPrimitives.ReadSingleLittleEndian(dnam.AsSpan(4, 4)));
        Assert.Equal(new byte[] { 1, 0, 0, 0 }, dnam[200..204]);
        Assert.Equal(10.5f, BinaryPrimitives.ReadSingleLittleEndian(dnam.AsSpan(204, 4)));
        Assert.Equal(-2.25f, BinaryPrimitives.ReadSingleLittleEndian(dnam.AsSpan(208, 4)));
        Assert.Equal(new byte[] { 1, 0x15, 0, 0 }, dnam[224..228]);

        var firstParameter = Assert.Single(result.OrderedSubrecords, sub => sub.Signature == "\0IAD");
        Assert.Equal(0f, BinaryPrimitives.ReadSingleLittleEndian(firstParameter.Data.AsSpan(0, 4)));
        Assert.Equal(13.5f, BinaryPrimitives.ReadSingleLittleEndian(firstParameter.Data.AsSpan(4, 4)));
    }

    [Fact]
    public void RuntimeNameHeader_StitchesVaContiguousObjectAcrossFileDiscontinuity()
    {
        var contiguous = Build(Early, _ => 0);
        const string editorId = "HVSimISFX";
        const int namePayloadSourceOffset = 0x0F00;
        const int objectSuffixFileOffset = 0x3000;
        const int namePayloadFileOffset = 0x6000;
        var split = Early.Name + 3;
        var bytes = new byte[0x7000];
        Array.Copy(contiguous.Bytes, 0, bytes, 0, split);
        Array.Copy(contiguous.Bytes, split, bytes, objectSuffixFileOffset, Early.Size - split);
        Array.Copy(contiguous.Bytes, namePayloadSourceOffset, bytes, namePayloadFileOffset, editorId.Length);
        var context = new RuntimeMemoryContext(
            new ByteArrayMemoryAccessor(bytes),
            bytes.Length,
            new MinidumpInfo
            {
                IsValid = true,
                ProcessorArchitecture = 0x03,
                MemoryRegions =
                [
                    new MinidumpMemoryRegion
                    {
                        VirtualAddress = BaseVa,
                        FileOffset = 0,
                        Size = split,
                    },
                    new MinidumpMemoryRegion
                    {
                        VirtualAddress = BaseVa + (uint)split,
                        FileOffset = objectSuffixFileOffset,
                        Size = Early.Size - split,
                    },
                    new MinidumpMemoryRegion
                    {
                        VirtualAddress = BaseVa + (uint)namePayloadSourceOffset,
                        FileOffset = namePayloadFileOffset,
                        Size = editorId.Length,
                    },
                ],
            });

        var result = new RuntimeImageSpaceModifierReader(context, true)
            .ReadRuntimeImageSpaceModifier(contiguous.Entry);

        Assert.NotNull(result);
        Assert.Equal(editorId, result.EditorId);
    }

    [Fact]
    public void FinalPdbLoadGroundedLayout_MapsIntroAndOutroSoundPointersInCorrectDirection()
    {
        const uint introFormId = 0x00010001;
        const uint outroFormId = 0x00010002;
        var fixture = Build(Final, _ => 2, introFormId, outroFormId);

        var result = new RuntimeImageSpaceModifierReader(fixture.Context, false)
            .ReadRuntimeImageSpaceModifier(fixture.Entry);

        Assert.NotNull(result);
        Assert.Equal(introFormId, result.IntroSoundFormId);
        Assert.Equal(outroFormId, result.OutroSoundFormId);
        Assert.Equal(introFormId, BinaryPrimitives.ReadUInt32LittleEndian(
            Assert.Single(result.OrderedSubrecords, sub => sub.Signature == "RDSD").Data));
        Assert.Equal(outroFormId, BinaryPrimitives.ReadUInt32LittleEndian(
            Assert.Single(result.OrderedSubrecords, sub => sub.Signature == "RDSI").Data));
    }

    [Fact]
    public void ZeroCounts_WithNullPointers_AreCompleteAndOmitAllFrameTables()
    {
        var fixture = Build(Early, _ => 0);

        var result = new RuntimeImageSpaceModifierReader(fixture.Context, true)
            .ReadRuntimeImageSpaceModifier(fixture.Entry);

        Assert.NotNull(result);
        Assert.Equal(["EDID", "DNAM"], result.OrderedSubrecords.Select(sub => sub.Signature));
        Assert.True(ImageSpaceModifierCaptureValidator.IsCompleteNewCapture(result, out var reason), reason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DualValidLayouts_AreAmbiguousAndFailClosedRegardlessOfBuildHint(bool preferEarlyLayout)
    {
        var fixture = Build(Early, _ => 0, animatable: false);
        Array.Clear(
            fixture.Bytes,
            Early.Data,
            Final.Data + 244 - Early.Data);

        const string editorId = "HVSimISFX";
        const int finalNameDataOffset = 0x0F20;
        WriteUInt32BE(fixture.Bytes, Final.Name, BaseVa + finalNameDataOffset);
        WriteUInt16BE(fixture.Bytes, Final.Name + 4, (ushort)editorId.Length);
        WriteUInt16BE(fixture.Bytes, Final.Name + 6, (ushort)editorId.Length);
        Encoding.ASCII.GetBytes(
            editorId,
            fixture.Bytes.AsSpan(finalNameDataOffset, editorId.Length));

        var result = new RuntimeImageSpaceModifierReader(fixture.Context, preferEarlyLayout)
            .ReadRuntimeImageSpaceModifier(fixture.Entry);

        Assert.Null(result);

        // Prove the rejection above is ambiguity, not a fixture that happens to fail
        // both probes: removing the non-hinted candidate leaves the hinted layout valid.
        WriteUInt32BE(fixture.Bytes, preferEarlyLayout ? Final.Name : Early.Name, 0);
        var unambiguous = new RuntimeImageSpaceModifierReader(fixture.Context, preferEarlyLayout)
            .ReadRuntimeImageSpaceModifier(fixture.Entry);
        Assert.NotNull(unambiguous);
    }

    [Fact]
    public void AlternateLayoutProbe_DoesNotPolluteBsStringDiagnostics()
    {
        BSStringDiagnostics.Reset();
        try
        {
            var fixture = Build(Early, _ => 0);

            var result = new RuntimeImageSpaceModifierReader(fixture.Context, true)
                .ReadRuntimeImageSpaceModifier(fixture.Entry);

            Assert.NotNull(result);
            Assert.Contains(
                "strName: 1/1 succeeded (0 failed)",
                BSStringDiagnostics.GetReport(),
                StringComparison.Ordinal);
        }
        finally
        {
            BSStringDiagnostics.Reset();
        }
    }

    [Fact]
    public void PositiveCount_WithNullPointer_RejectsEntireRuntimeRecord()
    {
        var fixture = Build(Early, layout => layout.Signature == "BNAM" ? 1u : 0u);
        WriteUInt32BE(fixture.Bytes, Early.NamedPointers, 0);

        var result = new RuntimeImageSpaceModifierReader(fixture.Context, true)
            .ReadRuntimeImageSpaceModifier(fixture.Entry);

        Assert.Null(result);
    }

    [Fact]
    public void PositiveCount_WithTruncatedCapturedArray_RejectsEntireRuntimeRecord()
    {
        var fixture = Build(Early, layout => layout.Signature == "BNAM" ? 2u : 0u);
        WriteUInt32BE(fixture.Bytes, Early.NamedPointers, BaseVa + (uint)fixture.Bytes.Length - 4);

        var result = new RuntimeImageSpaceModifierReader(fixture.Context, true)
            .ReadRuntimeImageSpaceModifier(fixture.Entry);

        Assert.Null(result);
    }

    [Fact]
    public void NonFiniteKey_RejectsEntireRuntimeRecord()
    {
        var fixture = Build(Early, layout => layout.Signature == "BNAM" ? 2u : 0u);
        var pointer = BinaryPrimitives.ReadUInt32BigEndian(
            fixture.Bytes.AsSpan(Early.NamedPointers, 4));
        WriteUInt32BE(fixture.Bytes, checked((int)(pointer - BaseVa)), 0x7FC00000);

        var result = new RuntimeImageSpaceModifierReader(fixture.Context, true)
            .ReadRuntimeImageSpaceModifier(fixture.Entry);

        Assert.Null(result);
    }

    [Fact]
    public void MismatchedRuntimeName_RejectsCandidate()
    {
        var fixture = Build(Early, _ => 0);
        var namePointer = BinaryPrimitives.ReadUInt32BigEndian(
            fixture.Bytes.AsSpan(Early.Name, 4));
        fixture.Bytes[checked((int)(namePointer - BaseVa))] = (byte)'X';

        var result = new RuntimeImageSpaceModifierReader(fixture.Context, true)
            .ReadRuntimeImageSpaceModifier(fixture.Entry);

        Assert.Null(result);
    }

    [Fact]
    public void AnimatablePositiveTable_WithOneKey_RejectsCandidate()
    {
        var fixture = Build(Early, layout => layout.Signature == "BNAM" ? 1u : 0u);

        var result = new RuntimeImageSpaceModifierReader(fixture.Context, true)
            .ReadRuntimeImageSpaceModifier(fixture.Entry);

        Assert.Null(result);
    }

    [Fact]
    public void AnimatablePositiveTable_WithNonzeroFirstTime_RejectsCandidate()
    {
        var fixture = Build(Early, layout => layout.Signature == "BNAM" ? 2u : 0u);
        WriteFloatBE(fixture.Bytes, KeyTableOffset(fixture.Bytes, Early, "BNAM"), 0.25f);

        var result = new RuntimeImageSpaceModifierReader(fixture.Context, true)
            .ReadRuntimeImageSpaceModifier(fixture.Entry);

        Assert.Null(result);
    }

    [Fact]
    public void AnimatablePositiveTable_WithNonunitLastTime_RejectsCandidate()
    {
        var fixture = Build(Early, layout => layout.Signature == "BNAM" ? 2u : 0u);
        WriteFloatBE(fixture.Bytes, KeyTableOffset(fixture.Bytes, Early, "BNAM") + 8, 0.75f);

        var result = new RuntimeImageSpaceModifierReader(fixture.Context, true)
            .ReadRuntimeImageSpaceModifier(fixture.Entry);

        Assert.Null(result);
    }

    [Fact]
    public void AnimatablePositiveTable_WithNonincreasingTimes_RejectsCandidate()
    {
        var fixture = Build(Early, layout => layout.Signature == "BNAM" ? 3u : 0u);
        var keyOffset = KeyTableOffset(fixture.Bytes, Early, "BNAM");
        WriteFloatBE(fixture.Bytes, keyOffset + 8, 0f);

        var result = new RuntimeImageSpaceModifierReader(fixture.Context, true)
            .ReadRuntimeImageSpaceModifier(fixture.Entry);

        Assert.Null(result);
    }

    [Fact]
    public void NonanimatablePositiveTable_AllowsOneKeyAndArbitraryTime()
    {
        var fixture = Build(
            Early,
            layout => layout.Signature == "BNAM" ? 1u : 0u,
            animatable: false);

        var result = new RuntimeImageSpaceModifierReader(fixture.Context, true)
            .ReadRuntimeImageSpaceModifier(fixture.Entry);

        Assert.NotNull(result);
        var blur = Assert.Single(result.OrderedSubrecords, sub => sub.Signature == "BNAM");
        Assert.Equal(0.25f, BinaryPrimitives.ReadSingleLittleEndian(blur.Data.AsSpan(0, 4)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(201)]
    [InlineData(226)]
    public void NonzeroDataPadding_RejectsCandidate(int relativeOffset)
    {
        var fixture = Build(Early, _ => 0);
        fixture.Bytes[Early.Data + relativeOffset] = 1;

        var result = new RuntimeImageSpaceModifierReader(fixture.Context, true)
            .ReadRuntimeImageSpaceModifier(fixture.Entry);

        Assert.Null(result);
    }

    [Fact]
    public void CountAboveSafetyCap_RejectsBeforeFollowingPointer()
    {
        var fixture = Build(Early, _ => 0);
        var blurLayout = Assert.Single(ImageSpaceModifierCaptureValidator.FrameTableLayouts,
            layout => layout.Signature == "BNAM");
        WriteUInt32BE(fixture.Bytes, Early.Data + 8 + blurLayout.CountIndex * 4, 4097);

        var result = new RuntimeImageSpaceModifierReader(fixture.Context, true)
            .ReadRuntimeImageSpaceModifier(fixture.Entry);

        Assert.Null(result);
    }

    [Fact]
    public void ScalarSignatureCountMap_MatchesLoadDisassembly()
    {
        AssertLayout("NAM1", 55);
        AssertLayout("NAM2", 56);
        AssertLayout("WNAM", 51);
        AssertLayout("XNAM", 52);
        AssertLayout("YNAM", 53);
    }

    private static void AssertLayout(string signature, int countIndex)
    {
        var layout = Assert.Single(ImageSpaceModifierCaptureValidator.FrameTableLayouts,
            candidate => candidate.Signature == signature);
        Assert.Equal(countIndex, layout.CountIndex);
        Assert.Equal(8, layout.ElementSize);
    }

    private static RuntimeFixture Build(
        TestLayout layout,
        Func<ImageSpaceModifierCaptureValidator.FrameTableLayout, uint> countSelector,
        uint? introSound = null,
        uint? outroSound = null,
        bool animatable = true)
    {
        var bytes = new byte[0x8000];
        WriteUInt32BE(bytes, 0, 0x82010000);
        bytes[4] = 0x54;
        WriteUInt32BE(bytes, 12, FormId);

        bytes[layout.Data] = animatable ? (byte)1 : (byte)0;
        WriteFloatBE(bytes, layout.Data + 4, 4.25f);
        bytes[layout.Data + 200] = 1;
        WriteFloatBE(bytes, layout.Data + 204, 10.5f);
        WriteFloatBE(bytes, layout.Data + 208, -2.25f);
        bytes[layout.Data + 224] = 1;
        bytes[layout.Data + 225] = 0x15;

        const string editorId = "HVSimISFX";
        const int nameDataOffset = 0x0F00;
        WriteUInt32BE(bytes, layout.Name, BaseVa + nameDataOffset);
        WriteUInt16BE(bytes, layout.Name + 4, (ushort)editorId.Length);
        WriteUInt16BE(bytes, layout.Name + 6, (ushort)editorId.Length);
        Encoding.ASCII.GetBytes(editorId, bytes.AsSpan(nameDataOffset, editorId.Length));

        var keyCursor = 0x1000;
        var rank = 0;
        foreach (var table in ImageSpaceModifierCaptureValidator.FrameTableLayouts)
        {
            var count = countSelector(table);
            WriteUInt32BE(bytes, layout.Data + 8 + table.CountIndex * 4, count);
            if (count == 0)
            {
                rank++;
                continue;
            }

            var pointerOffset = table.CountIndex < 42
                ? layout.ParameterPointers + table.CountIndex * 4
                : layout.NamedPointers + NamedPointerOrdinals[table.Signature] * 4;
            WriteUInt32BE(bytes, pointerOffset, BaseVa + (uint)keyCursor);
            for (var row = 0; row < count; row++)
            {
                for (var word = 0; word < table.ElementSize / 4; word++)
                {
                    float value;
                    if (word == 0 && animatable)
                    {
                        value = count == 1 ? 0f : row / (float)(count - 1);
                    }
                    else
                    {
                        value = rank + 0.25f + word * 0.25f + row * 100f;
                    }

                    WriteFloatBE(bytes, keyCursor, value);
                    keyCursor += 4;
                }
            }

            rank++;
        }

        if (layout.HasSounds)
        {
            if (outroSound.HasValue)
            {
                WriteUInt32BE(bytes, 0x28, BaseVa + (uint)keyCursor);
                WriteTesForm(bytes, keyCursor, 0x0D, outroSound.Value);
                keyCursor += 24;
            }

            if (introSound.HasValue)
            {
                WriteUInt32BE(bytes, 0x2C, BaseVa + (uint)keyCursor);
                WriteTesForm(bytes, keyCursor, 0x0D, introSound.Value);
            }
        }

        var context = CreateContext(bytes);
        return new RuntimeFixture(
            bytes,
            context,
            new RuntimeEditorIdEntry
            {
                EditorId = "HVSimISFX",
                FormId = FormId,
                FormType = 0x54,
                TesFormOffset = 0,
                TesFormPointer = BaseVa
            });
    }

    private static int KeyTableOffset(byte[] bytes, TestLayout layout, string signature)
    {
        var table = Assert.Single(ImageSpaceModifierCaptureValidator.FrameTableLayouts,
            candidate => candidate.Signature == signature);
        var pointerOffset = table.CountIndex < 42
            ? layout.ParameterPointers + table.CountIndex * 4
            : layout.NamedPointers + NamedPointerOrdinals[table.Signature] * 4;
        var pointer = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(pointerOffset, 4));
        return checked((int)(pointer - BaseVa));
    }

    private static RuntimeMemoryContext CreateContext(byte[] bytes)
    {
        return new RuntimeMemoryContext(
            new ByteArrayMemoryAccessor(bytes),
            bytes.Length,
            new MinidumpInfo
            {
                IsValid = true,
                ProcessorArchitecture = 0x03,
                MemoryRegions =
                [
                    new MinidumpMemoryRegion
                    {
                        VirtualAddress = BaseVa,
                        FileOffset = 0,
                        Size = bytes.Length
                    }
                ]
            });
    }

    private static void WriteTesForm(byte[] bytes, int offset, byte formType, uint formId)
    {
        WriteUInt32BE(bytes, offset, 0x82010000);
        bytes[offset + 4] = formType;
        WriteUInt32BE(bytes, offset + 12, formId);
    }

    private sealed record TestLayout(
        int Size,
        int Data,
        int ParameterPointers,
        int NamedPointers,
        int Name,
        bool HasSounds);

    private sealed record RuntimeFixture(
        byte[] Bytes,
        RuntimeMemoryContext Context,
        RuntimeEditorIdEntry Entry);
}
