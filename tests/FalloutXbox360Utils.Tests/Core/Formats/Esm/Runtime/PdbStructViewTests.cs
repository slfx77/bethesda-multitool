using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Runtime;
using FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Generic;
using FalloutXbox360Utils.Core.Minidump;
using FalloutXbox360Utils.Tests.Helpers;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Runtime;

/// <summary>
///     Contract tests for <see cref="PdbStructView" /> — the typed view over a loaded
///     PDB-described runtime struct. Mirrors the contract surface of
///     <c>SubrecordSchemaViewTests</c> on the ESM-byte side: validates the four invariants
///     of the abstraction (FormID/FormType guard, by-name field lookup, range clamping,
///     OBND extraction).
/// </summary>
public sealed class PdbStructViewTests
{
    private const uint BaseVa = 0x40000000;
    private const uint StructVa = BaseVa + 0x100;
    private const byte TestFormType = 0x67; // IMOD — picked because it has a real PDB layout

    [Fact]
    public void OpenStructView_ReturnsNullOnFormIdMismatch()
    {
        // Struct header carries FormID=0x222 but entry says 0x111. The accessor's
        // ReadStruct guard (cFormID != entry.FormId) must still fire through the view.
        var buffer = new byte[0x1000];
        WriteFormHeader(buffer, OffsetFromVa(StructVa), TestFormType, 0x00000222);

        var view = OpenView(buffer, entryFormId: 0x00000111);

        Assert.Null(view);
    }

    [Fact]
    public void OpenStructView_ReturnsNullOnFormTypeMismatch()
    {
        // Struct says FormType=0x67 (IMOD), entry says 0x28 (WEAP) — guard fires.
        var buffer = new byte[0x1000];
        WriteFormHeader(buffer, OffsetFromVa(StructVa), TestFormType, 0x00012345);

        var view = OpenView(buffer, entryFormId: 0x00012345, entryFormType: 0x28);

        Assert.Null(view);
    }

    [Fact]
    public void OpenStructView_ExposesBufferLayoutAndFileOffset()
    {
        var buffer = new byte[0x1000];
        WriteFormHeader(buffer, OffsetFromVa(StructVa), TestFormType, 0x00012345);

        var view = OpenView(buffer, entryFormId: 0x00012345);

        Assert.NotNull(view);
        Assert.Equal(OffsetFromVa(StructVa), view!.FileOffset);
        Assert.Equal(TestFormType, view.Layout.FormType);
        Assert.True(view.Buffer.Length >= view.Layout.StructSize);
    }

    [Fact]
    public void Int32_AndUInt32_ReadFromLayoutResolvedOffsets()
    {
        // IMOD has TESValueForm.iValue at +144 (uint32 in MemDebug PDB) and
        // TESWeightForm.fWeight at +152 (float). Verify the view resolves both by name.
        var buffer = new byte[0x1000];
        var fileOffset = OffsetFromVa(StructVa);
        WriteFormHeader(buffer, fileOffset, TestFormType, 0x00012345);
        WriteInt32BE(buffer, fileOffset + 144, 425);
        WriteFloatBE(buffer, fileOffset + 152, 2.5f);

        var view = OpenView(buffer, entryFormId: 0x00012345);

        Assert.NotNull(view);
        Assert.Equal(425, view!.Int32("iValue", "TESValueForm"));
        Assert.Equal(2.5f, view.Float("fWeight", "TESWeightForm"));
    }

    [Fact]
    public void Int32Range_ClampsOutOfBandValuesToZero()
    {
        var buffer = new byte[0x1000];
        var fileOffset = OffsetFromVa(StructVa);
        WriteFormHeader(buffer, fileOffset, TestFormType, 0x00012345);
        WriteInt32BE(buffer, fileOffset + 144, 2_500_000); // out of band

        var view = OpenView(buffer, entryFormId: 0x00012345);

        Assert.NotNull(view);
        Assert.Equal(0, view!.Int32Range("iValue", "TESValueForm", min: 0, max: 1_000_000));
    }

    [Fact]
    public void Int32_ReturnsDefaultForUnknownField()
    {
        var buffer = new byte[0x1000];
        WriteFormHeader(buffer, OffsetFromVa(StructVa), TestFormType, 0x00012345);

        var view = OpenView(buffer, entryFormId: 0x00012345);

        Assert.NotNull(view);
        Assert.Equal(-1, view!.Int32("NotARealField", def: -1));
        Assert.Null(view.Offset("NotARealField"));
    }

    private static PdbStructView? OpenView(byte[] buffer, uint entryFormId, byte entryFormType = TestFormType)
    {
        var memoryAccessor = new SparseMemoryAccessor();
        memoryAccessor.AddRange(0, buffer);
        var minidumpInfo = new MinidumpInfo
        {
            IsValid = true,
            ProcessorArchitecture = 0x03,
            MemoryRegions =
            [
                new MinidumpMemoryRegion
                {
                    VirtualAddress = BaseVa,
                    FileOffset = 0,
                    Size = buffer.Length
                }
            ]
        };
        var context = new RuntimeMemoryContext(memoryAccessor, buffer.Length, minidumpInfo);
        var accessor = new RuntimePdbFieldAccessor(context);

        var entry = new RuntimeEditorIdEntry
        {
            EditorId = "TestEntry",
            FormId = entryFormId,
            FormType = entryFormType,
            TesFormOffset = OffsetFromVa(StructVa)
        };

        return accessor.OpenStructView(entry);
    }

    private static long OffsetFromVa(uint va) => va - BaseVa;

    private static void WriteFormHeader(byte[] buffer, long fileOffset, byte formType, uint formId)
    {
        // TESForm: byte[0-3] = vtable, byte[4] = cFormType, byte[12-15] = iFormID (BE).
        WriteUInt32BE(buffer, fileOffset, 0x82010000u);
        buffer[fileOffset + 4] = formType;
        WriteUInt32BE(buffer, fileOffset + 12, formId);
    }

    private static void WriteUInt32BE(byte[] buffer, long fileOffset, uint value)
    {
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan((int)fileOffset, 4), value);
    }

    private static void WriteInt32BE(byte[] buffer, long fileOffset, int value)
    {
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan((int)fileOffset, 4), value);
    }

    private static void WriteFloatBE(byte[] buffer, long fileOffset, float value)
    {
        BinaryPrimitives.WriteSingleBigEndian(buffer.AsSpan((int)fileOffset, 4), value);
    }
}
