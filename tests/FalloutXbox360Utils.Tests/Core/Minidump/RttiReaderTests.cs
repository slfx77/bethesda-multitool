using System.Buffers.Binary;
using System.Text;
using FalloutXbox360Utils.Core.Minidump;
using FalloutXbox360Utils.Core.Utils;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Minidump;

/// <summary>
///     Tests for RttiReader — MSVC RTTI chain resolution from synthetic memory dumps.
/// </summary>
public class RttiReaderTests
{
    // All synthetic data uses VAs in module range (0x82xxxxxx).
    // MinidumpInfo regions use sign-extended VAs to match Xbox360MemoryUtils.VaToLong().
    private const uint BaseVA = 0x82000000;

    #region ResolveVtable Tests

    [Fact]
    public void ResolveVtable_ValidChain_ReturnsClassName()
    {
        // Arrange
        var (info, stream) = BuildSyntheticDump(
            className: ".?AVTESIdleForm@@",
            baseClassName: ".?AVTESForm@@",
            objectOffset: 0);

        var reader = new RttiReader(info, stream);

        // Act — vtable is at VA 0x82000004
        var result = reader.ResolveVtable(BaseVA + 4);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("TESIdleForm", result.ClassName);
        Assert.Equal(".?AVTESIdleForm@@", result.MangledName);
        Assert.Equal((uint)0, result.ObjectOffset);
        Assert.Equal(BaseVA + 4, result.VtableVA);
    }

    [Fact]
    public void ResolveVtable_SecondaryVtable_ReturnsNonZeroOffset()
    {
        // Arrange — objectOffset=272 means this is a secondary vtable
        var (info, stream) = BuildSyntheticDump(
            className: ".?AVActorValueOwner@@",
            baseClassName: null,
            objectOffset: 272);

        var reader = new RttiReader(info, stream);

        // Act
        var result = reader.ResolveVtable(BaseVA + 4);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("ActorValueOwner", result.ClassName);
        Assert.Equal((uint)272, result.ObjectOffset);
    }

    [Fact]
    public void ResolveVtable_WithBaseClasses_ReturnsHierarchy()
    {
        // Arrange
        var (info, stream) = BuildSyntheticDump(
            className: ".?AVTESIdleForm@@",
            baseClassName: ".?AVTESForm@@",
            objectOffset: 0);

        var reader = new RttiReader(info, stream);

        // Act
        var result = reader.ResolveVtable(BaseVA + 4);

        // Assert
        Assert.NotNull(result);
        Assert.NotNull(result.BaseClasses);
        Assert.Equal(2, result.BaseClasses.Count);
        Assert.Equal("TESIdleForm", result.BaseClasses[0].ClassName);
        Assert.Equal("TESForm", result.BaseClasses[1].ClassName);
    }

    [Fact]
    public void ResolveVtable_InvalidVA_ReturnsNull()
    {
        // Arrange — VA not in any captured region
        var info = new MinidumpInfo
        {
            IsValid = true,
            MemoryRegions =
            [
                new MinidumpMemoryRegion
                {
                    VirtualAddress = Xbox360MemoryUtils.VaToLong(BaseVA),
                    Size = 16,
                    FileOffset = 0
                }
            ]
        };

        using var stream = new MemoryStream(new byte[16]);
        var reader = new RttiReader(info, stream);

        // Act — query a VA outside the captured region
        var result = reader.ResolveVtable(0x90000000);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ResolveVtable_BadCOLSignature_ReturnsNull()
    {
        // Arrange — COL has non-zero signature
        var data = new byte[256];
        var colVA = BaseVA + 0x10;

        // vtable[-1] at offset 0x00 → points to COL
        WriteBE(data, 0x00, colVA);
        // COL at offset 0x10: signature = 0x12345678 (invalid, should be 0)
        WriteBE(data, 0x10, 0x12345678u);

        var info = CreateMinidumpInfoForRange(BaseVA, data.Length);
        using var stream = new MemoryStream(data);
        var reader = new RttiReader(info, stream);

        // Act — vtable at VA 0x82000004
        var result = reader.ResolveVtable(BaseVA + 4);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ResolveVtable_InvalidTypeName_ReturnsNull()
    {
        // Arrange — TypeDescriptor has a name that doesn't start with ".?A"
        var data = new byte[256];
        var colVA = BaseVA + 0x10;
        var tdVA = BaseVA + 0x30;

        // vtable[-1] → COL
        WriteBE(data, 0x00, colVA);
        // COL: signature=0, offset=0, cdOffset=0, pTD, pCHD=0
        WriteBE(data, 0x10, 0u); // signature
        WriteBE(data, 0x14, 0u); // offset
        WriteBE(data, 0x18, 0u); // cdOffset
        WriteBE(data, 0x1C, tdVA); // pTypeDescriptor
        WriteBE(data, 0x20, BaseVA + 0xF0); // pCHD (valid ptr but won't be reached)
        // TypeDescriptor: pVFTable, spare, name
        WriteBE(data, 0x30, 0x82FFFFFFu); // pVFTable
        WriteBE(data, 0x34, 0u); // spare
        WriteString(data, 0x38, "NotAValidMangledName");

        var info = CreateMinidumpInfoForRange(BaseVA, data.Length);
        using var stream = new MemoryStream(data);
        var reader = new RttiReader(info, stream);

        // Act
        var result = reader.ResolveVtable(BaseVA + 4);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ResolveVtable_StructPrefix_ReturnsClassName()
    {
        // Arrange — .?AU prefix (struct instead of class)
        var (info, stream) = BuildSyntheticDump(
            className: ".?AUBaseFormComponent@@",
            baseClassName: null,
            objectOffset: 0);

        var reader = new RttiReader(info, stream);

        // Act
        var result = reader.ResolveVtable(BaseVA + 4);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("BaseFormComponent", result.ClassName);
    }

    [Fact]
    public void ResolveVtable_ZeroVtableVA_ReturnsNull()
    {
        var info = CreateMinidumpInfoForRange(BaseVA, 256);
        using var stream = new MemoryStream(new byte[256]);
        var reader = new RttiReader(info, stream);

        // Act — VA too small (< 4)
        var result = reader.ResolveVtable(0);

        // Assert
        Assert.Null(result);
    }

    #endregion

    #region DemangleName Tests

    [Theory]
    [InlineData(".?AVTESIdleForm@@", "TESIdleForm")]
    [InlineData(".?AVTESForm@@", "TESForm")]
    [InlineData(".?AUBaseFormComponent@@", "BaseFormComponent")]
    [InlineData(".?AVNiObject@@", "NiObject")]
    [InlineData(".?AVBSAnimGroupSequence@@", "BSAnimGroupSequence")]
    public void DemangleName_ValidNames_ReturnsDemangledName(string mangled, string expected)
    {
        var result = RttiReader.DemangleName(mangled);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("NotMangled")]
    [InlineData(".?AX")]
    [InlineData("")]
    public void DemangleName_InvalidNames_ReturnsNull(string mangled)
    {
        var result = RttiReader.DemangleName(mangled);
        Assert.Null(result);
    }

    [Fact]
    public void DemangleName_EmptyBetweenPrefixAndSuffix_ReturnsNull()
    {
        // ".?AV@@" has nothing between prefix and suffix
        var result = RttiReader.DemangleName(".?AV@@");
        Assert.Null(result);
    }

    #endregion

    #region ScanRange Tests

    [Fact]
    public void ScanRange_FindsUniqueVtables()
    {
        // Arrange — two different vtable pointers alternating in a region
        var vtable1VA = BaseVA + 4;
        var vtable2VA = BaseVA + 0x0104;

        var data = new byte[0x300];

        // Build RTTI chain for vtable1 (at offset 0x04)
        BuildRttiChainAt(data, vtableOffset: 0x00, colOffset: 0x10, tdOffset: 0x30, ".?AVClassA@@");

        // Build RTTI chain for vtable2 (at offset 0x0104)
        BuildRttiChainAt(data, vtableOffset: 0x100, colOffset: 0x110, tdOffset: 0x130, ".?AVClassB@@");

        // Simulate a striped array: alternating vtable pointers
        // At offset 0x200: vtable1 pointer, at 0x204: vtable2 pointer, repeating
        WriteBE(data, 0x200, vtable1VA);
        WriteBE(data, 0x204, vtable2VA);
        WriteBE(data, 0x208, vtable1VA);
        WriteBE(data, 0x20C, vtable2VA);

        var info = CreateMinidumpInfoForRange(BaseVA, data.Length);
        using var stream = new MemoryStream(data);
        var reader = new RttiReader(info, stream);

        // Act — scan the striped region
        var results = reader.ScanRange(BaseVA + 0x200, BaseVA + 0x210, stride: 4);

        // Assert
        Assert.Equal(2, results.Count);
        var classNames = results.Select(r => r.ClassName).OrderBy(n => n).ToList();
        Assert.Equal("ClassA", classNames[0]);
        Assert.Equal("ClassB", classNames[1]);
    }

    [Fact]
    public void ScanRange_DeduplicatesVtables()
    {
        // Arrange — same vtable pointer repeated
        var vtableVA = BaseVA + 4;
        var data = new byte[0x200];

        BuildRttiChainAt(data, vtableOffset: 0x00, colOffset: 0x10, tdOffset: 0x30, ".?AVMyClass@@");

        // Same pointer at multiple positions
        WriteBE(data, 0x100, vtableVA);
        WriteBE(data, 0x104, vtableVA);
        WriteBE(data, 0x108, vtableVA);

        var info = CreateMinidumpInfoForRange(BaseVA, data.Length);
        using var stream = new MemoryStream(data);
        var reader = new RttiReader(info, stream);

        // Act
        var results = reader.ScanRange(BaseVA + 0x100, BaseVA + 0x10C, stride: 4);

        // Assert — should only return 1 result despite 3 occurrences
        Assert.Single(results);
        Assert.Equal("MyClass", results[0].ClassName);
    }

    #endregion

    #region Helper Methods

    /// <summary>
    ///     Build a complete synthetic dump with a single RTTI chain.
    /// </summary>
    private static (MinidumpInfo info, MemoryStream stream) BuildSyntheticDump(
        string className, string? baseClassName, uint objectOffset)
    {
        var data = new byte[0x200];

        // Layout:
        // 0x00: vtable[-1] → COL pointer
        // 0x04: vtable[0] (the vtable VA we query)
        // 0x10: COL struct (20 bytes)
        // 0x30: TypeDescriptor for main class
        // 0x60: ClassHierarchyDescriptor
        // 0x80: BaseClassArray
        // 0x90: BCD[0] (main class)
        // 0xA0: BCD[1] (base class)
        // 0xB0: TypeDescriptor for base class

        var colVA = BaseVA + 0x10;
        var tdVA = BaseVA + 0x30;
        var chdVA = BaseVA + 0x60;
        var bcaVA = BaseVA + 0x80;

        // vtable[-1] → COL
        WriteBE(data, 0x00, colVA);

        // COL
        WriteBE(data, 0x10, 0u); // signature
        WriteBE(data, 0x14, objectOffset); // offset
        WriteBE(data, 0x18, 0u); // cdOffset
        WriteBE(data, 0x1C, tdVA); // pTypeDescriptor
        WriteBE(data, 0x20, chdVA); // pClassHierarchyDescriptor

        // TypeDescriptor for main class
        WriteBE(data, 0x30, 0x82FFFFFFu); // pVFTable
        WriteBE(data, 0x34, 0u); // spare
        WriteString(data, 0x38, className);

        // ClassHierarchyDescriptor
        var numBases = baseClassName != null ? 2u : 1u;
        WriteBE(data, 0x60, 0u); // signature
        WriteBE(data, 0x64, 0u); // attributes
        WriteBE(data, 0x68, numBases); // numBaseClasses
        WriteBE(data, 0x6C, bcaVA); // pBaseClassArray

        // BaseClassArray
        WriteBE(data, 0x80, BaseVA + 0x90); // BCD[0]
        if (baseClassName != null)
        {
            WriteBE(data, 0x84, BaseVA + 0xA0); // BCD[1]
        }

        // BCD[0] — main class
        WriteBE(data, 0x90, tdVA); // pTypeDescriptor
        WriteBE(data, 0x94, baseClassName != null ? 1u : 0u); // numContainedBases
        WriteBE(data, 0x98, 0); // mdisp

        // BCD[1] — base class
        if (baseClassName != null)
        {
            var baseTdVA = BaseVA + 0xB0;
            WriteBE(data, 0xA0, baseTdVA); // pTypeDescriptor
            WriteBE(data, 0xA4, 0u); // numContainedBases
            WriteBE(data, 0xA8, 0); // mdisp

            // TypeDescriptor for base class
            WriteBE(data, 0xB0, 0x82FFFFFFu); // pVFTable
            WriteBE(data, 0xB4, 0u); // spare
            WriteString(data, 0xB8, baseClassName);
        }

        var info = CreateMinidumpInfoForRange(BaseVA, data.Length);
        return (info, new MemoryStream(data));
    }

    /// <summary>
    ///     Build a minimal RTTI chain at specific offsets within a byte array.
    ///     Creates vtable[-1], COL, and TypeDescriptor (no hierarchy).
    /// </summary>
    private static void BuildRttiChainAt(byte[] data, int vtableOffset, int colOffset, int tdOffset, string className)
    {
        var colVA = BaseVA + (uint)colOffset;
        var tdVA = BaseVA + (uint)tdOffset;

        // vtable[-1] → COL
        WriteBE(data, vtableOffset, colVA);

        // COL (no hierarchy — pCHD points to invalid area)
        WriteBE(data, colOffset, 0u); // signature
        WriteBE(data, colOffset + 4, 0u); // offset
        WriteBE(data, colOffset + 8, 0u); // cdOffset
        WriteBE(data, colOffset + 12, tdVA); // pTypeDescriptor
        WriteBE(data, colOffset + 16, 0x82FFFFFEu); // pCHD (valid module ptr but no data)

        // TypeDescriptor
        WriteBE(data, tdOffset, 0x82FFFFFFu); // pVFTable
        WriteBE(data, tdOffset + 4, 0u); // spare
        WriteString(data, tdOffset + 8, className);
    }

    private static MinidumpInfo CreateMinidumpInfoForRange(uint startVA, int size)
    {
        return new MinidumpInfo
        {
            IsValid = true,
            MemoryRegions =
            [
                new MinidumpMemoryRegion
                {
                    VirtualAddress = Xbox360MemoryUtils.VaToLong(startVA),
                    Size = size,
                    FileOffset = 0
                }
            ]
        };
    }

    private static void WriteBE(byte[] data, int offset, uint value)
    {
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(offset), value);
    }

    private static void WriteBE(byte[] data, int offset, int value)
    {
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(offset), value);
    }

    private static void WriteString(byte[] data, int offset, string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        bytes.CopyTo(data, offset);
        data[offset + bytes.Length] = 0; // null terminator
    }

    #endregion
}
