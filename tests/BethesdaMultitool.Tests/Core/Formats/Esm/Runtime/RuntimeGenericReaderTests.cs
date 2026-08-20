using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Esm.Runtime;
using BethesdaMultitool.Core.Minidump;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Runtime;

public sealed class RuntimeGenericReaderTests
{
    private const byte MovableStaticFormType = 0x22;
    private const int MovableStaticTesFormInteriorOffset = 20;

    [Theory]
    [InlineData("float32")]
    [InlineData("float")]
    public void ReadFieldValue_RecognizesPdbFloat32AndLegacyAlias(string kind)
    {
        var reader = new RuntimeGenericReader(RuntimeReaderTestFixture.Default().BuildContext());
        var field = new PdbFieldLayout("Value", 1, sizeof(float), kind, "Test", "float");
        var data = new byte[1 + sizeof(float)];
        BinaryPrimitives.WriteSingleBigEndian(data.AsSpan(1), -123.5f);

        var value = reader.ReadFieldValue(data, field, 0);

        Assert.Equal(-123.5f, Assert.IsType<float>(value));
    }

    [Theory]
    [InlineData(0x7FC00000u)] // NaN
    [InlineData(0x7F800000u)] // +Infinity
    [InlineData(0xFF800000u)] // -Infinity
    public void ReadFieldValue_RejectsNonFiniteFloat32(uint bits)
    {
        var reader = new RuntimeGenericReader(RuntimeReaderTestFixture.Default().BuildContext());
        var field = new PdbFieldLayout("Value", 0, sizeof(float), "float32", "Test", "float");
        var data = new byte[sizeof(float)];
        BinaryPrimitives.WriteUInt32BigEndian(data, bits);

        Assert.Null(reader.ReadFieldValue(data, field, 0));
    }

    [Fact]
    public void ReadFieldValue_RejectsSubnormalFloat32()
    {
        var reader = new RuntimeGenericReader(RuntimeReaderTestFixture.Default().BuildContext());
        var field = new PdbFieldLayout("Value", 0, sizeof(float), "float32", "Test", "float");
        var data = new byte[sizeof(float)];
        BinaryPrimitives.WriteUInt32BigEndian(data, 1u);

        Assert.Null(reader.ReadFieldValue(data, field, 0));
    }

    [Fact]
    public void EmbeddedPdbLayouts_UseFloat32Kind()
    {
        var floatFields = PdbStructLayouts.Layouts.Values
            .SelectMany(layout => layout.Fields)
            .Where(field => field.Kind == "float32")
            .ToArray();

        Assert.Equal(160, floatFields.Length);
        Assert.DoesNotContain(
            PdbStructLayouts.Layouts.Values.SelectMany(layout => layout.Fields),
            field => field.Kind == "float");
        Assert.All(floatFields, field =>
            Assert.Equal(RuntimeReaderFieldProbe.FieldCheck.NormalFloat,
                RuntimeGenericReader.GetFieldProbeCheck(field)));
    }

    [Fact]
    public void ReadGenericRecord_StitchesVaContiguousStructAndKeepsMappedObjectBase()
    {
        const long objectVa = 0x5000;
        const long firstFileOffset = 24;
        const long secondFileOffset = 240;
        const uint modelStringVa = 0x7000;
        const long modelStringFileOffset = 360;
        const int splitOffset = 92;
        const string expectedModelPath = "models/cross-region.nif";
        var layout = PdbStructLayouts.Get(MovableStaticFormType)!;
        var objectData = new byte[layout.StructSize + 8];
        var modelPathBytes = Encoding.ASCII.GetBytes(expectedModelPath);
        // cModel begins at +88, so this header itself crosses the VA-contiguous region split.
        BinaryPrimitives.WriteUInt32BigEndian(objectData.AsSpan(88), modelStringVa);
        BinaryPrimitives.WriteUInt16BigEndian(objectData.AsSpan(92), checked((ushort)modelPathBytes.Length));
        BinaryPrimitives.WriteUInt16BigEndian(objectData.AsSpan(94), checked((ushort)modelPathBytes.Length));
        objectData[104] = 0x7A; // TESModel.cFlags, beyond the first region.
        var fileData = new byte[480];
        Array.Copy(objectData, 0, fileData, firstFileOffset, splitOffset);
        Array.Copy(objectData, splitOffset, fileData, secondFileOffset, objectData.Length - splitOffset);
        Array.Copy(modelPathBytes, 0, fileData, modelStringFileOffset, modelPathBytes.Length);
        var context = CreateContext(
            new ByteArrayMemoryAccessor(fileData),
            fileData.Length,
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
                VirtualAddress = modelStringVa,
                Size = modelPathBytes.Length,
                FileOffset = modelStringFileOffset
            });
        var reader = new RuntimeGenericReader(context);
        var entry = new RuntimeEditorIdEntry
        {
            EditorId = "CrossRegionMovableStatic",
            FormId = 0x00123456,
            FormType = MovableStaticFormType,
            TesFormOffset = firstFileOffset + MovableStaticTesFormInteriorOffset,
            TesFormPointer = objectVa + MovableStaticTesFormInteriorOffset
        };

        var record = Assert.IsType<GenericEsmRecord>(reader.ReadGenericRecord(entry));

        Assert.Equal(firstFileOffset, record.Offset);
        Assert.Equal(expectedModelPath, record.ModelPath);
        Assert.Equal((byte)0x7A, Assert.IsType<byte>(record.Fields["TESModel.cFlags"]));
    }

    [Fact]
    public void ReadGenericRecord_FailsClosedAcrossVaGapBeforeShiftProbeReadsFlatBytes()
    {
        const long objectVa = 0x6000;
        const long firstFileOffset = 24;
        const int splitOffset = 64;
        var layout = PdbStructLayouts.Get(MovableStaticFormType)!;
        var objectData = new byte[layout.StructSize + 8];
        var fileData = new byte[256];
        // The flat file bytes look like one complete struct, but the mapped VA has a one-byte gap.
        Array.Copy(objectData, 0, fileData, firstFileOffset, objectData.Length);
        var accessor = new TrackingMemoryAccessor(fileData);
        var context = CreateContext(
            accessor,
            fileData.Length,
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
        var reader = new RuntimeGenericReader(context);
        var entry = new RuntimeEditorIdEntry
        {
            EditorId = "GappedMovableStatic",
            FormId = 0x00654321,
            FormType = MovableStaticFormType,
            TesFormOffset = firstFileOffset + MovableStaticTesFormInteriorOffset
        };

        Assert.Null(reader.ReadGenericRecord(entry));
        Assert.Empty(accessor.Reads);
    }

    [Fact]
    public void ReadGenericRecord_UsesFlatFallbackWithoutVaMapping()
    {
        const long objectBase = 40;
        var layout = PdbStructLayouts.Get(MovableStaticFormType)!;
        var objectData = new byte[layout.StructSize + 8];
        objectData[104] = 0x5C;
        var fileData = new byte[256];
        Array.Copy(objectData, 0, fileData, objectBase, objectData.Length);
        var context = CreateContext(new ByteArrayMemoryAccessor(fileData), fileData.Length);
        var reader = new RuntimeGenericReader(context);
        var entry = new RuntimeEditorIdEntry
        {
            EditorId = "FlatMovableStatic",
            FormId = 0x0000ABCD,
            FormType = MovableStaticFormType,
            TesFormOffset = objectBase + MovableStaticTesFormInteriorOffset
        };

        var record = Assert.IsType<GenericEsmRecord>(reader.ReadGenericRecord(entry));

        Assert.Equal(objectBase, record.Offset);
        Assert.Equal((byte)0x5C, Assert.IsType<byte>(record.Fields["TESModel.cFlags"]));
    }

    private static RuntimeMemoryContext CreateContext(
        IMemoryAccessor accessor,
        long fileSize,
        params MinidumpMemoryRegion[] regions)
    {
        return new RuntimeMemoryContext(
            accessor,
            fileSize,
            new MinidumpInfo
            {
                IsValid = true,
                MemoryRegions = [.. regions]
            });
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