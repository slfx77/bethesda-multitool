using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Runtime;
using BethesdaMultitool.Core.Minidump;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Runtime;

public sealed class RuntimePdbFieldAccessorRebaseTests
{
    [Fact]
    public void OpenStructView_RebasesSignExtendedMsttInteriorPointerBeforeStitchingObject()
    {
        const byte formType = 0x22;
        const uint formId = 0x0013579B;
        const long objectVa = unchecked((int)0x82008000u);
        const long objectPrefixFileOffset = 240;
        const long tesFormFileOffset = 40;
        const uint fullNameVa = 0x18000;
        const uint modelVa = 0x19000;
        const string fullName = "Stitched MSTT full name";
        const string modelPath = "meshes/stitched/mstt_model.nif";
        var layout = PdbStructLayouts.Get(formType)!;
        var interiorOffset = PdbStructLayouts.GetTesFormInteriorOffset(layout);
        var objectData = CreateObjectData(layout, formType, formId);
        WriteBsStringHeader(objectData, 4, fullNameVa, fullName.Length);
        WriteBsStringHeader(objectData, 88, modelVa, modelPath.Length);
        var data = new byte[600];
        Array.Copy(objectData, 0, data, objectPrefixFileOffset, interiorOffset);
        Array.Copy(objectData, interiorOffset, data, tesFormFileOffset,
            objectData.Length - interiorOffset);
        WriteSplitAscii(data, 360, 400, fullName, 5);
        WriteSplitAscii(data, 460, 520, modelPath, 7);
        var context = CreateContext(
            data,
            new MinidumpMemoryRegion
            {
                VirtualAddress = objectVa,
                Size = interiorOffset,
                FileOffset = objectPrefixFileOffset
            },
            new MinidumpMemoryRegion
            {
                VirtualAddress = objectVa + interiorOffset,
                Size = objectData.Length - interiorOffset,
                FileOffset = tesFormFileOffset
            },
            Region(fullNameVa, 360, 5),
            Region(fullNameVa + 5, 400, fullName.Length - 5),
            Region(modelVa, 460, 7),
            Region(modelVa + 7, 520, modelPath.Length - 7));
        var entry = MakeEntry(
            "MsttInterior",
            formId,
            formType,
            tesFormFileOffset,
            objectVa + interiorOffset);

        var view = new RuntimePdbFieldAccessor(context).OpenStructView(entry);

        Assert.NotNull(view);
        Assert.True(objectVa < 0);
        Assert.Equal(20, interiorOffset);
        Assert.Equal(objectVa + interiorOffset, entry.TesFormPointer);
        Assert.Equal(objectPrefixFileOffset, view.FileOffset);
        Assert.Equal(objectData, view.Buffer);
        Assert.Equal(formType, view.Byte("cFormType", "TESForm"));
        Assert.Equal(formId, view.UInt32("iFormID", "TESForm"));
        Assert.Equal(fullName, view.BsString("cFullName", "TESFullName"));
        Assert.Equal(modelPath, view.BsString("cModel", "TESModel"));
        Assert.Null(context.ReadBSStringTDiag(view.FileOffset, 88, out _));
    }

    [Fact]
    public void OpenStructView_RebasesFlorInteriorPointerWithoutConfusingObjectFields()
    {
        const byte formType = 0x26;
        const uint formId = 0x002468AC;
        const long objectVa = 0x9000;
        const long objectPrefixFileOffset = 300;
        const long tesFormFileOffset = 60;
        const uint fullNameVa = 0x1A000;
        const uint modelVa = 0x1B000;
        const string fullName = "Stitched FLOR full name";
        const string modelPath = "meshes/stitched/flor_model.nif";
        var layout = PdbStructLayouts.Get(formType)!;
        var interiorOffset = PdbStructLayouts.GetTesFormInteriorOffset(layout);
        var objectData = CreateObjectData(layout, formType, formId);
        WriteBsStringHeader(objectData, 80, fullNameVa, fullName.Length);
        WriteBsStringHeader(objectData, 92, modelVa, modelPath.Length);
        var data = new byte[760];
        Array.Copy(objectData, 0, data, objectPrefixFileOffset, interiorOffset);
        Array.Copy(objectData, interiorOffset, data, tesFormFileOffset,
            objectData.Length - interiorOffset);
        WriteSplitAscii(data, 450, 500, fullName, 5);
        WriteSplitAscii(data, 570, 640, modelPath, 7);
        var context = CreateContext(
            data,
            new MinidumpMemoryRegion
            {
                VirtualAddress = objectVa,
                Size = interiorOffset,
                FileOffset = objectPrefixFileOffset
            },
            new MinidumpMemoryRegion
            {
                VirtualAddress = objectVa + interiorOffset,
                Size = objectData.Length - interiorOffset,
                FileOffset = tesFormFileOffset
            },
            Region(fullNameVa, 450, 5),
            Region(fullNameVa + 5, 500, fullName.Length - 5),
            Region(modelVa, 570, 7),
            Region(modelVa + 7, 640, modelPath.Length - 7));
        var entry = MakeEntry(
            "FlorInterior",
            formId,
            formType,
            tesFormFileOffset,
            null); // Exercise VA recovery from the mapped TESForm-subobject file offset.

        var view = new RuntimePdbFieldAccessor(context).OpenStructView(entry);

        Assert.NotNull(view);
        Assert.Equal(12, interiorOffset);
        Assert.Equal(objectPrefixFileOffset, view.FileOffset);
        Assert.Equal(objectData, view.Buffer);
        Assert.Equal(fullName, view.BsString("cFullName", "TESFullName"));
        Assert.Equal(modelPath, view.BsString("cModel", "TESModel"));
        Assert.Equal(formId, view.UInt32("iFormID", "TESForm"));
        // The old PdbStructView path reopened both headers from FileOffset + PDB offset.
        // Those physical bytes are deliberately unrelated to the VA-stitched object buffer.
        Assert.Null(context.ReadBSStringTDiag(view.FileOffset, 80, out _));
        Assert.Null(context.ReadBSStringTDiag(view.FileOffset, 92, out _));
    }

    [Fact]
    public void RuntimeWorldObjectReader_UsesStitchedObjectBufferForFullNameAndModel()
    {
        const byte formType = 0x15;
        const uint formId = 0x00357ACE;
        const long objectVa = 0xC000;
        const long objectPrefixFileOffset = 320;
        const long objectTailFileOffset = 40;
        const int objectSplit = 72;
        const uint fullNameVa = 0x1C000;
        const uint modelVa = 0x1D000;
        const string fullName = "Sparse activator full name";
        const string modelPath = "meshes/stitched/activator_model.nif";
        var layout = PdbStructLayouts.Get(formType)!;
        var fullNameOffset = RuntimePdbFieldAccessor.FindFieldOffset(layout, "cFullName", "TESFullName")!.Value;
        var modelOffset = RuntimePdbFieldAccessor.FindFieldOffset(layout, "cModel", "TESModel")!.Value;
        var objectData = CreateObjectData(layout, formType, formId);
        WriteBsStringHeader(objectData, fullNameOffset, fullNameVa, fullName.Length);
        WriteBsStringHeader(objectData, modelOffset, modelVa, modelPath.Length);
        var data = new byte[900];
        Array.Copy(objectData, 0, data, objectPrefixFileOffset, objectSplit);
        Array.Copy(objectData, objectSplit, data, objectTailFileOffset, objectData.Length - objectSplit);
        WriteSplitAscii(data, 500, 550, fullName, 6);
        WriteSplitAscii(data, 650, 720, modelPath, 8);
        var context = CreateContext(
            data,
            Region(objectVa, objectPrefixFileOffset, objectSplit),
            Region(objectVa + objectSplit, objectTailFileOffset, objectData.Length - objectSplit),
            Region(fullNameVa, 500, 6),
            Region(fullNameVa + 6, 550, fullName.Length - 6),
            Region(modelVa, 650, 8),
            Region(modelVa + 8, 720, modelPath.Length - 8));
        var entry = MakeEntry("SparseActivator", formId, formType, objectPrefixFileOffset, objectVa);

        var result = new RuntimeWorldObjectReader(context).ReadRuntimeActivator(entry);

        Assert.NotNull(result);
        Assert.Equal(68, fullNameOffset);
        Assert.Equal(80, modelOffset);
        Assert.Equal(fullName, result.FullName);
        Assert.Equal(modelPath, result.ModelPath);
        Assert.Equal(objectPrefixFileOffset, result.Offset);
        Assert.Null(context.ReadBSStringTDiag(objectPrefixFileOffset, fullNameOffset, out _));
        Assert.Null(context.ReadBSStringTDiag(objectPrefixFileOffset, modelOffset, out _));
    }

    [Fact]
    public void OpenStructView_TesFormFirstLayoutKeepsPointerAsObjectBase()
    {
        const byte formType = 0x11;
        const uint formId = 0x003579BD;
        const long objectVa = 0xA000;
        const long objectFileOffset = 24;
        var layout = PdbStructLayouts.Get(formType)!;
        var objectData = CreateObjectData(layout, formType, formId);
        var data = new byte[checked((int)(objectFileOffset + objectData.Length))];
        Array.Copy(objectData, 0, data, objectFileOffset, objectData.Length);
        var context = CreateContext(
            data,
            new MinidumpMemoryRegion
            {
                VirtualAddress = objectVa,
                Size = objectData.Length,
                FileOffset = objectFileOffset
            });
        var entry = MakeEntry(
            "TesFormFirst",
            formId,
            formType,
            objectFileOffset,
            objectVa);

        var view = new RuntimePdbFieldAccessor(context).OpenStructView(entry);

        Assert.NotNull(view);
        Assert.Equal(0, PdbStructLayouts.GetTesFormInteriorOffset(layout));
        Assert.Equal(objectFileOffset, view.FileOffset);
        Assert.Equal(objectData, view.Buffer);
    }

    [Fact]
    public void ReadStruct_ReturnsNullAcrossObjectVaGapDespiteContiguousFlatBait()
    {
        const byte formType = 0x22;
        const uint formId = 0x00468ACE;
        const long objectVa = 0xB000;
        const long objectFileOffset = 20;
        const int firstRegionSize = 60;
        var layout = PdbStructLayouts.Get(formType)!;
        var interiorOffset = PdbStructLayouts.GetTesFormInteriorOffset(layout);
        var objectData = CreateObjectData(layout, formType, formId);
        var data = new byte[checked((int)(objectFileOffset + objectData.Length))];
        // The complete object is physically contiguous and valid in the dump file. The region
        // metadata deliberately leaves one VA byte uncaptured, so a flat fallback would be wrong.
        Array.Copy(objectData, 0, data, objectFileOffset, objectData.Length);
        var context = CreateContext(
            data,
            new MinidumpMemoryRegion
            {
                VirtualAddress = objectVa,
                Size = firstRegionSize,
                FileOffset = objectFileOffset
            },
            new MinidumpMemoryRegion
            {
                VirtualAddress = objectVa + firstRegionSize + 1,
                Size = objectData.Length - firstRegionSize,
                FileOffset = objectFileOffset + firstRegionSize
            });
        var entry = MakeEntry(
            "GappedMstt",
            formId,
            formType,
            objectFileOffset + interiorOffset,
            objectVa + interiorOffset);
        var accessor = new RuntimePdbFieldAccessor(context);

        Assert.Null(accessor.ReadStruct(entry));
        Assert.Null(accessor.OpenStructView(entry));
    }

    [Fact]
    public void ReadStruct_FlatSyntheticFallbackStillRebasesInteriorFileOffset()
    {
        const byte formType = 0x26;
        const uint formId = 0x00579BDF;
        const long objectFileOffset = 100;
        var layout = PdbStructLayouts.Get(formType)!;
        var interiorOffset = PdbStructLayouts.GetTesFormInteriorOffset(layout);
        var objectData = CreateObjectData(layout, formType, formId);
        var data = new byte[checked((int)(objectFileOffset + objectData.Length))];
        Array.Copy(objectData, 0, data, objectFileOffset, objectData.Length);
        var context = CreateContext(data);
        var entry = MakeEntry(
            "FlatFlor",
            formId,
            formType,
            objectFileOffset + interiorOffset,
            null);

        var result = new RuntimePdbFieldAccessor(context).ReadStruct(entry);

        Assert.NotNull(result);
        Assert.Equal(objectFileOffset, result.Value.FileOffset);
        Assert.Equal(objectData, result.Value.Buffer);
    }

    private static byte[] CreateObjectData(PdbTypeLayout layout, byte formType, uint formId)
    {
        var data = Enumerable.Range(0, layout.StructSize)
            .Select(index => (byte)(index * 37 + 11))
            .ToArray();
        var formTypeOffset = RuntimePdbFieldAccessor.FindFieldOffset(layout, "cFormType", "TESForm")!.Value;
        var formIdOffset = RuntimePdbFieldAccessor.FindFieldOffset(layout, "iFormID", "TESForm")!.Value;
        data[formTypeOffset] = formType;
        WriteUInt32BigEndian(data, formIdOffset, formId);
        return data;
    }

    private static RuntimeEditorIdEntry MakeEntry(
        string editorId,
        uint formId,
        byte formType,
        long tesFormFileOffset,
        long? tesFormVa)
    {
        return new RuntimeEditorIdEntry
        {
            EditorId = editorId,
            FormId = formId,
            FormType = formType,
            TesFormOffset = tesFormFileOffset,
            TesFormPointer = tesFormVa
        };
    }

    private static RuntimeMemoryContext CreateContext(
        byte[] data,
        params MinidumpMemoryRegion[] regions)
    {
        return new RuntimeMemoryContext(
            new ByteArrayMemoryAccessor(data),
            data.Length,
            new MinidumpInfo
            {
                IsValid = true,
                MemoryRegions = [.. regions]
            });
    }

    private static MinidumpMemoryRegion Region(long va, long fileOffset, int size)
    {
        return new MinidumpMemoryRegion
        {
            VirtualAddress = va,
            FileOffset = fileOffset,
            Size = size
        };
    }

    private static void WriteBsStringHeader(byte[] data, int offset, uint stringVa, int length)
    {
        WriteUInt32BigEndian(data, offset, stringVa);
        data[offset + 4] = (byte)(length >> 8);
        data[offset + 5] = (byte)length;
        data[offset + 6] = (byte)(length >> 8);
        data[offset + 7] = (byte)length;
    }

    private static void WriteSplitAscii(
        byte[] data,
        int firstFileOffset,
        int secondFileOffset,
        string text,
        int split)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        Array.Copy(bytes, 0, data, firstFileOffset, split);
        Array.Copy(bytes, split, data, secondFileOffset, bytes.Length - split);
    }

    private static void WriteUInt32BigEndian(byte[] data, int offset, uint value)
    {
        data[offset] = (byte)(value >> 24);
        data[offset + 1] = (byte)(value >> 16);
        data[offset + 2] = (byte)(value >> 8);
        data[offset + 3] = (byte)value;
    }
}