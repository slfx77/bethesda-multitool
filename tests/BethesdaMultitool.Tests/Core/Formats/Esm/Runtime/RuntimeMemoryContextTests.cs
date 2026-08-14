using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Runtime;
using BethesdaMultitool.Core.Formats.Esm.Runtime.Readers.Generic;
using BethesdaMultitool.Core.Minidump;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Runtime;

public sealed class RuntimeMemoryContextTests
{
    [Theory]
    [InlineData(0x00000000u, true)]  // +0
    [InlineData(0x80000000u, true)]  // -0
    [InlineData(0x3F800000u, true)]  // 1
    [InlineData(0x00000001u, true)]  // smallest positive subnormal remains a finite runtime value
    [InlineData(0x80000001u, true)]  // smallest negative subnormal remains a finite runtime value
    [InlineData(0x7F800000u, false)] // +Infinity
    [InlineData(0x7FC00000u, false)] // NaN
    public void IsNormalFloat_AcceptsEveryFiniteValue(uint bits, bool expected)
    {
        Assert.Equal(expected, RuntimeMemoryContext.IsNormalFloat(BitConverter.UInt32BitsToSingle(bits)));
    }

    [Theory]
    [InlineData(0x00000000u, true)]
    [InlineData(0x80000000u, true)]
    [InlineData(0x3F800000u, true)]
    [InlineData(0x00000001u, false)]
    [InlineData(0x80000001u, false)]
    [InlineData(0x7F800000u, false)]
    [InlineData(0x7FC00000u, false)]
    public void IsNormalOrZeroFloat_RejectsSubnormalAndNonFiniteValues(uint bits, bool expected)
    {
        Assert.Equal(expected,
            RuntimeMemoryContext.IsNormalOrZeroFloat(BitConverter.UInt32BitsToSingle(bits)));
    }

    [Fact]
    public void ReadValidatedFloat_RejectsSubnormalGarbage_ButKeepsNormalAndZero()
    {
        var buffer = new byte[16];
        // Offset 0: a subnormal float (~3.67e-40) — the misread signature (a pointer's low bytes
        // decoded as a float when a struct offset is wrong for the captured build).
        BinaryPrimitives.WriteSingleBigEndian(buffer.AsSpan(0, 4), 3.67348e-40f);
        // Offset 4: a normal, in-range value. Offset 8: exact zero (a legitimate reading).
        BinaryPrimitives.WriteSingleBigEndian(buffer.AsSpan(4, 4), 1200f);
        BinaryPrimitives.WriteSingleBigEndian(buffer.AsSpan(8, 4), 0f);

        Assert.True(float.IsSubnormal(3.67348e-40f));
        Assert.Equal(0f, RuntimeMemoryContext.ReadValidatedFloat(buffer, 0, 0f, 100000f));
        Assert.Equal(1200f, RuntimeMemoryContext.ReadValidatedFloat(buffer, 4, 0f, 100000f));
        Assert.Equal(0f, RuntimeMemoryContext.ReadValidatedFloat(buffer, 8, 0f, 100000f));
    }

    [Fact]
    public void FieldProbe_RejectsSubnormalForNormalAndRangedChecks()
    {
        const uint structVa = RuntimeReaderTestFixture.HeapBaseVa + 0x100;
        const uint formId = 0x0013579B;
        var buffer = new byte[20];
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(12), formId);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(16), 1u);
        var context = RuntimeReaderTestFixture.Default()
            .WithStruct(buffer, structVa)
            .BuildContext();
        var entry = RuntimeReaderTestFixture.MakeEntry(formId, 0x11, structVa);
        RuntimeReaderFieldProbe.FieldSpec[] fields =
        [
            new("Normal", 16, 0, RuntimeReaderFieldProbe.FieldCheck.NormalFloat),
            new("Ranged", 16, 0, RuntimeReaderFieldProbe.FieldCheck.RangedFloat,
                CheckArg: (-1f, 1f))
        ];

        var score = RuntimeReaderFieldProbe.ScoreSample(
            context, entry, fields, [0], buffer.Length);

        Assert.Equal(0, score.Points);
        Assert.Equal(2, score.MaxPoints);
    }

    [Fact]
    public void ReadBytesAtVa_StitchesVaContiguousRegionsWithNonContiguousFileOffsets()
    {
        var data = new byte[128];
        data[10] = 1;
        data[11] = 2;
        data[12] = 3;
        data[13] = 4;
        data[80] = 5;
        data[81] = 6;
        data[82] = 7;
        data[83] = 8;
        var context = CreateContext(
            data,
            new MinidumpMemoryRegion { VirtualAddress = 0x1000, Size = 4, FileOffset = 10 },
            new MinidumpMemoryRegion { VirtualAddress = 0x1004, Size = 4, FileOffset = 80 });

        var result = context.ReadBytesAtVa(0x1002, 4);

        Assert.Equal([3, 4, 5, 6], result);
    }

    [Fact]
    public void ReadBytesAtVa_ReturnsNullAcrossVaGapEvenWhenFileOffsetsAreContiguous()
    {
        var data = new byte[32];
        data[10] = 1;
        data[11] = 2;
        data[12] = 3;
        data[13] = 4;
        data[14] = 5;
        data[15] = 6;
        data[16] = 7;
        data[17] = 8;
        var context = CreateContext(
            data,
            new MinidumpMemoryRegion { VirtualAddress = 0x1000, Size = 4, FileOffset = 10 },
            new MinidumpMemoryRegion { VirtualAddress = 0x1005, Size = 4, FileOffset = 14 });

        var result = context.ReadBytesAtVa(0x1002, 4);

        Assert.Null(result);
    }

    [Fact]
    public void FormPointerReaders_StitchVaContiguousHeaderAtNonContiguousFileOffsets()
    {
        const uint targetVa = 0x4000;
        const uint formId = 0x0013579B;
        var data = new byte[160];
        var targetHeader = new byte[24];
        targetHeader[4] = 0x28;
        WriteUInt32BigEndian(targetHeader, 12, formId);
        Array.Copy(targetHeader, 0, data, 10, 8);
        Array.Copy(targetHeader, 8, data, 100, 16);
        var context = CreateContext(
            data,
            new MinidumpMemoryRegion { VirtualAddress = targetVa, Size = 8, FileOffset = 10 },
            new MinidumpMemoryRegion { VirtualAddress = targetVa + 8, Size = 16, FileOffset = 100 });
        var pointerField = new byte[4];
        WriteUInt32BigEndian(pointerField, 0, targetVa);

        Assert.Equal<uint?>(formId, context.FollowPointerToFormId(pointerField, 0));
        Assert.Equal<uint?>(formId, context.FollowPointerVaToFormId(targetVa));
    }

    [Fact]
    public void FormPointerReaders_ReturnNullAcrossVaGapEvenWhenFlatHeaderLooksValid()
    {
        const uint targetVa = 0x5000;
        const uint formId = 0x002468AC;
        var data = new byte[64];
        var targetHeader = new byte[24];
        targetHeader[4] = 0x28;
        WriteUInt32BigEndian(targetHeader, 12, formId);
        Array.Copy(targetHeader, 0, data, 10, targetHeader.Length);
        var context = CreateContext(
            data,
            new MinidumpMemoryRegion { VirtualAddress = targetVa, Size = 8, FileOffset = 10 },
            new MinidumpMemoryRegion { VirtualAddress = targetVa + 9, Size = 16, FileOffset = 18 });
        var pointerField = new byte[4];
        WriteUInt32BigEndian(pointerField, 0, targetVa);

        Assert.Null(context.FollowPointerToFormId(pointerField, 0));
        Assert.Null(context.FollowPointerVaToFormId(targetVa));
    }

    [Fact]
    public void BufferedBsStringRead_StitchesVaContiguousPayloadAtNonContiguousFileOffsets()
    {
        const uint stringVa = 0x6000;
        const string expected = "CrossRegionText";
        const int splitOffset = 5;
        var stringBytes = System.Text.Encoding.ASCII.GetBytes(expected);
        var data = new byte[160];
        Array.Copy(stringBytes, 0, data, 10, splitOffset);
        Array.Copy(stringBytes, splitOffset, data, 100, stringBytes.Length - splitOffset);
        var context = CreateContext(
            data,
            new MinidumpMemoryRegion { VirtualAddress = stringVa, Size = splitOffset, FileOffset = 10 },
            new MinidumpMemoryRegion
            {
                VirtualAddress = stringVa + splitOffset,
                Size = stringBytes.Length - splitOffset,
                FileOffset = 100
            });
        var header = new byte[8];
        WriteUInt32BigEndian(header, 0, stringVa);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(4), checked((ushort)stringBytes.Length));

        var result = context.ReadBSStringTDiag(header, 0, out var failure);

        Assert.Equal(expected, result);
        Assert.Equal(RuntimeMemoryContext.BSStringFailure.None, failure);
    }

    [Fact]
    public void BufferedBsStringRead_ReturnsNullAcrossVaGapEvenWhenFlatPayloadLooksValid()
    {
        const uint stringVa = 0x7000;
        const string expected = "GappedText";
        const int splitOffset = 5;
        var stringBytes = System.Text.Encoding.ASCII.GetBytes(expected);
        var data = new byte[64];
        Array.Copy(stringBytes, 0, data, 10, stringBytes.Length);
        var context = CreateContext(
            data,
            new MinidumpMemoryRegion { VirtualAddress = stringVa, Size = splitOffset, FileOffset = 10 },
            new MinidumpMemoryRegion
            {
                VirtualAddress = stringVa + splitOffset + 1,
                Size = stringBytes.Length - splitOffset,
                FileOffset = 10 + splitOffset
            });
        var header = new byte[8];
        WriteUInt32BigEndian(header, 0, stringVa);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(4), checked((ushort)stringBytes.Length));

        var result = context.ReadBSStringTDiag(header, 0, out var failure);

        Assert.Null(result);
        Assert.Equal(RuntimeMemoryContext.BSStringFailure.DataBeyondFile, failure);
    }

    [Fact]
    public void GenericFieldProbe_RebasesTesFormInteriorAndStitchesObjectAcrossRegions()
    {
        const long objectVa = 0x8000;
        const long firstFileOffset = 12;
        const long secondFileOffset = 120;
        const int splitOffset = 24;
        const uint formId = 0x003579BD;
        const uint stringVa = 0xA000;
        const long stringFileOffset = 180;
        const string probeText = "ProbeText";
        var layout = PdbStructLayouts.Get(0x22)!; // MSTT: TESForm starts at object +20.
        var interiorOffset = PdbStructLayouts.GetTesFormInteriorOffset(layout);
        var objectData = new byte[48];
        WriteUInt32BigEndian(objectData, interiorOffset + 12, formId);
        WriteUInt32BigEndian(objectData, 40, stringVa);
        BinaryPrimitives.WriteUInt16BigEndian(
            objectData.AsSpan(44), checked((ushort)probeText.Length));
        var probeTextBytes = System.Text.Encoding.ASCII.GetBytes(probeText);
        var data = new byte[220];
        Array.Copy(objectData, 0, data, firstFileOffset, splitOffset);
        Array.Copy(objectData, splitOffset, data, secondFileOffset, objectData.Length - splitOffset);
        Array.Copy(probeTextBytes, 0, data, stringFileOffset, probeTextBytes.Length);
        var context = CreateContext(
            data,
            new MinidumpMemoryRegion
            {
                VirtualAddress = objectVa,
                Size = splitOffset,
                FileOffset = firstFileOffset
            },
            new MinidumpMemoryRegion
            {
                VirtualAddress = objectVa + splitOffset,
                Size = objectData.Length - splitOffset,
                FileOffset = secondFileOffset
            },
            new MinidumpMemoryRegion
            {
                VirtualAddress = stringVa,
                Size = probeTextBytes.Length,
                FileOffset = stringFileOffset
            });
        var entry = new RuntimeEditorIdEntry
        {
            EditorId = "ProbeMstt",
            FormId = formId,
            FormType = 0x22,
            TesFormOffset = firstFileOffset + interiorOffset,
            TesFormPointer = objectVa + interiorOffset
        };
        RuntimeReaderFieldProbe.FieldSpec[] fields =
        [
            new("ObjectString", 40, 1, RuntimeReaderFieldProbe.FieldCheck.BSStringT)
        ];

        var score = RuntimeReaderFieldProbe.ScoreSample(
            context, entry, fields, [0, 0], objectData.Length,
            tesFormInteriorOffset: interiorOffset);

        Assert.Equal(20, interiorOffset);
        Assert.Equal(1, score.Points);
        Assert.Equal(1, score.MaxPoints);
    }

    [Fact]
    public void GenericFieldProbe_FailsClosedAcrossObjectVaGapWithoutFlatRead()
    {
        const long objectVa = 0x9000;
        const long firstFileOffset = 12;
        const int splitOffset = 24;
        const uint formId = 0x00468ACE;
        var layout = PdbStructLayouts.Get(0x22)!;
        var interiorOffset = PdbStructLayouts.GetTesFormInteriorOffset(layout);
        var objectData = new byte[48];
        WriteUInt32BigEndian(objectData, interiorOffset + 12, formId);
        WriteUInt32BigEndian(objectData, 40, 1);
        var data = new byte[96];
        Array.Copy(objectData, 0, data, firstFileOffset, objectData.Length);
        var accessor = new TrackingMemoryAccessor(data);
        var context = CreateContext(
            accessor,
            data.Length,
            new MinidumpMemoryRegion
            {
                VirtualAddress = objectVa,
                Size = splitOffset,
                FileOffset = firstFileOffset
            },
            new MinidumpMemoryRegion
            {
                VirtualAddress = objectVa + splitOffset + 1,
                Size = objectData.Length - splitOffset,
                FileOffset = firstFileOffset + splitOffset
            });
        var entry = new RuntimeEditorIdEntry
        {
            EditorId = "GappedProbeMstt",
            FormId = formId,
            FormType = 0x22,
            TesFormOffset = firstFileOffset + interiorOffset
        };
        RuntimeReaderFieldProbe.FieldSpec[] fields =
        [
            new("ObjectField", 40, 1, RuntimeReaderFieldProbe.FieldCheck.NonZeroUInt32)
        ];

        var score = RuntimeReaderFieldProbe.ScoreSample(
            context, entry, fields, [0, 0], objectData.Length,
            tesFormInteriorOffset: interiorOffset);

        Assert.Equal(0, score.Points);
        Assert.Empty(accessor.Reads);
    }

    [Fact]
    public void PdbStructRead_UsesVaStitchingAcrossNonContiguousFileOffsets()
    {
        const long structVa = 0x2008;
        const long firstFileOffset = 24;
        const uint formId = 0x00123456;
        var data = new byte[320];
        var expected = Enumerable.Range(0, 100).Select(i => (byte)i).ToArray();
        expected[4] = 0x11;
        WriteUInt32BigEndian(expected, 12, formId);
        Array.Copy(expected, 0, data, firstFileOffset, 24);
        Array.Copy(expected, 24, data, 192, expected.Length - 24);
        var context = CreateContext(
            data,
            new MinidumpMemoryRegion { VirtualAddress = 0x2000, Size = 32, FileOffset = 16 },
            new MinidumpMemoryRegion { VirtualAddress = 0x2020, Size = 96, FileOffset = 192 });
        var accessor = new RuntimePdbFieldAccessor(context);
        var entry = new RuntimeEditorIdEntry
        {
            EditorId = "CrossRegionScript",
            FormId = formId,
            FormType = 0x11,
            TesFormOffset = firstFileOffset,
            TesFormPointer = structVa
        };

        var result = accessor.ReadStruct(entry);

        Assert.NotNull(result);
        Assert.Equal(firstFileOffset, result.Value.FileOffset);
        Assert.Equal(expected, result.Value.Buffer);
    }

    [Fact]
    public void PdbStructRead_ReturnsNullAcrossVaGapEvenWhenFlatFileBytesLookValid()
    {
        const long structVa = 0x3008;
        const long firstFileOffset = 24;
        const uint formId = 0x00654321;
        var data = new byte[160];
        var flatStruct = new byte[100];
        flatStruct[4] = 0x11;
        WriteUInt32BigEndian(flatStruct, 12, formId);
        Array.Copy(flatStruct, 0, data, firstFileOffset, flatStruct.Length);
        var context = CreateContext(
            data,
            new MinidumpMemoryRegion { VirtualAddress = 0x3000, Size = 32, FileOffset = 16 },
            new MinidumpMemoryRegion { VirtualAddress = 0x3021, Size = 96, FileOffset = 48 });
        var accessor = new RuntimePdbFieldAccessor(context);
        var entry = new RuntimeEditorIdEntry
        {
            EditorId = "GappedScript",
            FormId = formId,
            FormType = 0x11,
            TesFormOffset = firstFileOffset,
            TesFormPointer = structVa
        };

        var result = accessor.ReadStruct(entry);

        Assert.Null(result);
    }

    private static RuntimeMemoryContext CreateContext(
        byte[] data,
        params MinidumpMemoryRegion[] regions)
    {
        return CreateContext(new ByteArrayMemoryAccessor(data), data.Length, regions);
    }

    private static RuntimeMemoryContext CreateContext(
        IMemoryAccessor accessor,
        long fileSize,
        params MinidumpMemoryRegion[] regions)
    {
        var info = new MinidumpInfo
        {
            IsValid = true,
            MemoryRegions = [.. regions]
        };
        return new RuntimeMemoryContext(accessor, fileSize, info);
    }

    private static void WriteUInt32BigEndian(byte[] data, int offset, uint value)
    {
        data[offset] = (byte)(value >> 24);
        data[offset + 1] = (byte)(value >> 16);
        data[offset + 2] = (byte)(value >> 8);
        data[offset + 3] = (byte)value;
    }

    private sealed class TrackingMemoryAccessor(byte[] data) : IMemoryAccessor
    {
        public List<(long Position, int Count)> Reads { get; } = [];

        public int ReadArray(long position, byte[] array, int offset, int count)
        {
            Reads.Add((position, count));
            return new ByteArrayMemoryAccessor(data).ReadArray(position, array, offset, count);
        }
    }
}
