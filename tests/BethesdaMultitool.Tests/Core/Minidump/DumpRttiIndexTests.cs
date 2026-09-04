using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Runtime;
using BethesdaMultitool.Core.Minidump;
using BethesdaMultitool.Core.Utils;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Minidump;

/// <summary>
///     Forward RTTI walk over a synthetic module image: type-descriptor strings, the complete
///     object locators that back-reference them, and the vtables that back-reference those.
/// </summary>
public sealed class DumpRttiIndexTests
{
    private const uint ModuleVa = 0x82000000;
    private const int ModuleSize = 0x1000;

    // Layout inside the module image, as offsets from ModuleVa.
    private const uint TdBase = 0x0100; //  type descriptors (name at +8)
    private const uint ColBase = 0x0300; // complete object locators (20 bytes each)
    private const uint ChdBase = 0x0400; // class hierarchy descriptors (16 bytes each)
    private const uint BcdBase = 0x0500; // base class descriptors + their pointer arrays
    private const uint VtBase = 0x0600; //  vtables (preceded by their locator pointer)

    [Fact]
    public void Build_RecoversClassesVtablesAndHierarchy()
    {
        var index = BuildIndex();

        Assert.NotNull(index);
        Assert.Equal(2, index!.TypeDescriptorCount);
        Assert.Equal(2, index.CompleteObjectLocatorCount);
        Assert.Equal(2, index.VtableCount);
        Assert.Equal(1, index.PrimaryVtableCount);
        Assert.Equal(2, index.Classes.Count);

        Assert.True(index.ClassesByName.ContainsKey("TESForm"));
        Assert.True(index.ClassesByName.ContainsKey("TESObjectREFR"));
    }

    /// <summary>
    ///     A derived class must carry its base chain, and that chain is what marks it TESForm-derived.
    ///     This is the data <see cref="RttiReader" />'s minimal MMF-side twin never reads, and it is
    ///     what a later field-naming pass needs to generalise beyond an enumerated class list.
    /// </summary>
    [Fact]
    public void Build_CarriesBaseClasses_AndFlagsTesFormDescent()
    {
        var index = BuildIndex()!;

        var derived = index.ClassesByName["TESObjectREFR"];
        Assert.Contains("TESForm", derived.BaseClasses);
        Assert.True(derived.IsTesFormDerived);

        // The root itself counts as TESForm-derived; nothing else does by accident.
        Assert.True(index.ClassesByName["TESForm"].IsTesFormDerived);
    }

    /// <summary>
    ///     Roughly a fifth of the vtables in a retail dump belong to secondary (multiple-inheritance)
    ///     vtables, whose objects begin ObjectOffset bytes EARLIER than the vtable slot. Losing that
    ///     field would misplace every one of those objects.
    /// </summary>
    [Fact]
    public void Build_PreservesSecondaryVtableObjectOffset()
    {
        var index = BuildIndex()!;

        Assert.True(index.TryGetVtable(ModuleVa + VtBase, out var primary));
        Assert.Equal(0u, primary.ObjectOffset);
        Assert.Equal("TESForm", index.ClassFor(primary)?.ClassName);

        Assert.True(index.TryGetVtable(ModuleVa + VtBase + 0x40, out var secondary));
        Assert.Equal(0x10u, secondary.ObjectOffset);
        Assert.Equal("TESObjectREFR", index.ClassFor(secondary)?.ClassName);
    }

    /// <summary>
    ///     The vtable band is derived from the dump, and it is what makes a whole-dump object sweep
    ///     affordable: two comparisons reject a word before any hash probe.
    /// </summary>
    [Fact]
    public void Build_ReportsTheVtableBand()
    {
        var index = BuildIndex()!;

        Assert.Equal(ModuleVa + VtBase, index.MinVtableVa);
        Assert.Equal(ModuleVa + VtBase + 0x40, index.MaxVtableVa);
    }

    /// <summary>
    ///     A locator whose signature word is not zero is not a 32-bit MSVC locator. Without this the
    ///     back-reference scan would accept any word that happens to equal a type-descriptor address
    ///     — base class descriptors hold exactly such a word at +0.
    /// </summary>
    [Fact]
    public void Build_RejectsLocatorWithNonZeroSignature()
    {
        var index = BuildIndex(corrupt: data =>
            WriteBe(data, (int)ColBase, 1)); // signature must be 0

        // The primary locator is gone, so only the secondary class survives.
        Assert.NotNull(index);
        Assert.Equal(1, index!.CompleteObjectLocatorCount);
        Assert.DoesNotContain("TESForm", index.ClassesByName.Keys);
    }

    /// <summary>
    ///     Type descriptors are 4-aligned — all 2,030 of them on xex44 — so an unaligned candidate is
    ///     a stray occurrence of the tag inside unrelated data, not a descriptor.
    /// </summary>
    [Fact]
    public void Build_IgnoresUnalignedTypeDescriptorCandidates()
    {
        var index = BuildIndex(corrupt: data =>
            // A ".?AV" tag whose implied descriptor address (name - 8) is not 4-aligned.
            WriteCString(data, 0x0A1 + 8, ".?AVBogusUnaligned@@"));

        Assert.NotNull(index);
        Assert.Equal(2, index!.TypeDescriptorCount);
        Assert.DoesNotContain("BogusUnaligned", index.ClassesByName.Keys);
    }

    /// <summary>
    ///     Module-range addresses that were not captured must fail closed. Here the image stops
    ///     short of the second vtable, which must then simply not resolve — never be read from
    ///     whatever bytes happen to sit at that file offset.
    /// </summary>
    [Fact]
    public void Build_FailsClosedOverAnUncapturedImageTail()
    {
        var data = BuildModuleBytes(null);

        // Cut between the descriptors/locators and the vtables, so the walk still finds the type
        // information but every vtable address lands in the uncaptured tail.
        var truncated = (int)VtBase - 0x80;

        var info = CreateMinidumpInfo(truncated);
        var index = DumpRttiIndex.Build(info, new ByteArrayMemoryAccessor(data), truncated);

        Assert.NotNull(index);
        Assert.Equal(2, index!.TypeDescriptorCount);
        Assert.Equal(0, index.VtableCount);
        Assert.Empty(index.Classes);
        Assert.Equal(truncated, index.ModuleBytesCaptured);
        Assert.Equal(ModuleSize, index.ModuleBytesDeclared);
    }

    [Fact]
    public void Build_ReturnsNull_WhenNoGameModuleIsPresent()
    {
        var data = BuildModuleBytes(null);
        var info = new MinidumpInfo
        {
            IsValid = true,
            ProcessorArchitecture = 0x03,
            Modules = [],
            MemoryRegions =
            [
                new MinidumpMemoryRegion
                {
                    VirtualAddress = Xbox360MemoryUtils.VaToLong(ModuleVa), FileOffset = 0, Size = ModuleSize
                }
            ]
        };

        Assert.Null(DumpRttiIndex.Build(info, new ByteArrayMemoryAccessor(data), data.Length));
    }

    private static DumpRttiIndex? BuildIndex(Action<byte[]>? corrupt = null)
    {
        var data = BuildModuleBytes(corrupt);
        return DumpRttiIndex.Build(
            CreateMinidumpInfo(ModuleSize), new ByteArrayMemoryAccessor(data), data.Length);
    }

    /// <summary>
    ///     Two classes: TESForm (primary vtable, no bases) and TESObjectREFR (secondary vtable at
    ///     object offset 0x10, deriving from TESForm).
    /// </summary>
    private static byte[] BuildModuleBytes(Action<byte[]>? corrupt)
    {
        var data = new byte[ModuleSize];

        // --- Type descriptors: pVFTable, spare, then the mangled name at +8 --------------------
        WriteCString(data, (int)TdBase + 8, ".?AVTESForm@@");
        WriteCString(data, (int)TdBase + 0x40 + 8, ".?AVTESObjectREFR@@");

        // --- Class hierarchy descriptors --------------------------------------------------------
        // TESForm: no bases.
        WriteBe(data, (int)ChdBase + 8, 0);
        // TESObjectREFR: one base, via a one-entry pointer array at BcdBase + 0x40.
        WriteBe(data, (int)ChdBase + 0x20 + 8, 1);
        WriteBe(data, (int)ChdBase + 0x20 + 12, ModuleVa + BcdBase + 0x40);
        WriteBe(data, (int)BcdBase + 0x40, ModuleVa + BcdBase); //  array[0] -> the BCD
        WriteBe(data, (int)BcdBase, ModuleVa + TdBase); //           BCD +0  -> TESForm's descriptor

        // --- Complete object locators (signature, objectOffset, cdOffset, pTD, pCHD) ------------
        WriteBe(data, (int)ColBase, 0);
        WriteBe(data, (int)ColBase + 4, 0); //                        primary vtable
        WriteBe(data, (int)ColBase + 12, ModuleVa + TdBase);
        WriteBe(data, (int)ColBase + 16, ModuleVa + ChdBase);

        WriteBe(data, (int)ColBase + 0x20, 0);
        WriteBe(data, (int)ColBase + 0x20 + 4, 0x10); //              secondary vtable at +0x10
        WriteBe(data, (int)ColBase + 0x20 + 12, ModuleVa + TdBase + 0x40);
        WriteBe(data, (int)ColBase + 0x20 + 16, ModuleVa + ChdBase + 0x20);

        // --- Vtables: locator pointer at vtable-4, a module-range function pointer at vtable ----
        WriteBe(data, (int)VtBase - 4, ModuleVa + ColBase);
        WriteBe(data, (int)VtBase, ModuleVa + 0x20);

        WriteBe(data, (int)VtBase + 0x40 - 4, ModuleVa + ColBase + 0x20);
        WriteBe(data, (int)VtBase + 0x40, ModuleVa + 0x24);

        corrupt?.Invoke(data);
        return data;
    }

    private static MinidumpInfo CreateMinidumpInfo(int capturedBytes)
    {
        return new MinidumpInfo
        {
            IsValid = true,
            ProcessorArchitecture = 0x03,
            Modules =
            [
                new MinidumpModule
                {
                    Name = "Fallout_Test.exe",
                    BaseAddress = Xbox360MemoryUtils.VaToLong(ModuleVa),
                    Size = ModuleSize
                }
            ],
            MemoryRegions =
            [
                new MinidumpMemoryRegion
                {
                    VirtualAddress = Xbox360MemoryUtils.VaToLong(ModuleVa),
                    FileOffset = 0,
                    Size = capturedBytes
                }
            ]
        };
    }

    private static void WriteBe(byte[] buffer, int offset, uint value)
    {
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(offset, 4), value);
    }

    private static void WriteCString(byte[] buffer, int offset, string text)
    {
        Encoding.ASCII.GetBytes(text + "\0").CopyTo(buffer, offset);
    }
}
