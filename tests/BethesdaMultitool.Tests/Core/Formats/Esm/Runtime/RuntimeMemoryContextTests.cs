using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Runtime;
using BethesdaMultitool.Core.Minidump;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Runtime;

public sealed class RuntimeMemoryContextTests
{
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
        var info = new MinidumpInfo
        {
            IsValid = true,
            MemoryRegions = [.. regions]
        };
        return new RuntimeMemoryContext(new ByteArrayMemoryAccessor(data), data.Length, info);
    }

    private static void WriteUInt32BigEndian(byte[] data, int offset, uint value)
    {
        data[offset] = (byte)(value >> 24);
        data[offset + 1] = (byte)(value >> 16);
        data[offset + 2] = (byte)(value >> 8);
        data[offset + 3] = (byte)value;
    }
}