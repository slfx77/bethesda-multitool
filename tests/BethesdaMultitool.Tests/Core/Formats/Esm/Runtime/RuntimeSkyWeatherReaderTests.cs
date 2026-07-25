using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Runtime;
using BethesdaMultitool.Core.Minidump;
using BethesdaMultitool.Core.Utils;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Runtime;

public sealed class RuntimeSkyWeatherReaderTests
{
    private const uint ModuleVa = 0x82000000;
    private const uint HeapVa = 0x40000000;
    private const int ModuleSize = 0x2000;
    private const int HeapSize = 0x2000;

    [Fact]
    public void Parser_ReadsBigEndianPointersAndCurrentWeight()
    {
        var bytes = new byte[WeatherTransitionSnapshotParser.RequiredSize];
        BinaryPrimitives.WriteUInt32BigEndian(
            bytes.AsSpan(WeatherTransitionSnapshotParser.CurrentWeatherOffset), 0x41000100);
        BinaryPrimitives.WriteUInt32BigEndian(
            bytes.AsSpan(WeatherTransitionSnapshotParser.OutgoingWeatherOffset), 0x41000200);
        WriteFloat(bytes, WeatherTransitionSnapshotParser.CurrentWeatherWeightOffset, 0.25f, true);

        var snapshot = WeatherTransitionSnapshotParser.Parse(
            bytes,
            true,
            0x41000000,
            pointer => pointer switch
            {
                0x41000100 => 0x00012345,
                0x41000200 => 0x0006789A,
                _ => null
            });

        Assert.NotNull(snapshot);
        Assert.Equal(0x41000000u, snapshot.SkyVirtualAddress);
        Assert.Equal(0x41000100u, snapshot.CurrentWeatherPointer);
        Assert.Equal(0x00012345u, snapshot.CurrentWeatherFormId);
        Assert.Equal(0x41000200u, snapshot.OutgoingWeatherPointer);
        Assert.Equal(0x0006789Au, snapshot.OutgoingWeatherFormId);
        Assert.Equal(0.25f, snapshot.CurrentWeatherWeight);
        Assert.Null(snapshot.ModifierElapsedSeconds);
    }

    [Fact]
    public void Parser_ReadsLittleEndianFixtureWithoutOrdinalAmbiguity()
    {
        var bytes = new byte[WeatherTransitionSnapshotParser.RequiredSize];
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(WeatherTransitionSnapshotParser.CurrentWeatherOffset), 0x11223344);
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(WeatherTransitionSnapshotParser.OutgoingWeatherOffset), 0);
        WriteFloat(bytes, WeatherTransitionSnapshotParser.CurrentWeatherWeightOffset, 0.75f, false);

        var snapshot = WeatherTransitionSnapshotParser.Parse(
            bytes,
            false,
            0x55667788,
            pointer => pointer == 0x11223344 ? 0x00ABCDEF : null);

        Assert.NotNull(snapshot);
        Assert.Equal(0x11223344u, snapshot.CurrentWeatherPointer);
        Assert.Equal(0x00ABCDEFu, snapshot.CurrentWeatherFormId);
        Assert.Null(snapshot.OutgoingWeatherPointer);
        Assert.Null(snapshot.OutgoingWeatherFormId);
        Assert.Equal(0.75f, snapshot.CurrentWeatherWeight);
    }

    [Fact]
    public void Parser_RejectsUnresolvedNonNullWeatherPointer()
    {
        var bytes = new byte[WeatherTransitionSnapshotParser.RequiredSize];
        BinaryPrimitives.WriteUInt32BigEndian(
            bytes.AsSpan(WeatherTransitionSnapshotParser.CurrentWeatherOffset), 0x41000100);
        WriteFloat(bytes, WeatherTransitionSnapshotParser.CurrentWeatherWeightOffset, 0.5f, true);

        var snapshot = WeatherTransitionSnapshotParser.Parse(
            bytes, true, 0x41000000, _ => null);

        Assert.Null(snapshot);
    }

    [Fact]
    public void Parser_RejectsEveryNonFiniteOrOutOfRangeWeight()
    {
        var invalidWeights = new[]
        {
            -0.001f,
            1.001f,
            float.NaN,
            float.PositiveInfinity,
            float.NegativeInfinity
        };

        foreach (var weight in invalidWeights)
        {
            var bytes = new byte[WeatherTransitionSnapshotParser.RequiredSize];
            WriteFloat(bytes, WeatherTransitionSnapshotParser.CurrentWeatherWeightOffset, weight, true);

            Assert.Null(WeatherTransitionSnapshotParser.Parse(
                bytes, true, 0x41000000, _ => null));
        }
    }

    [Fact]
    public void Reader_LocatesUniqueFNVSkyThroughRttiAndResolvesWeatherForms()
    {
        var fixture = CreateRuntimeFixture("Fallout_Release_MemDebug.exe");
        var reader = CreateReader(fixture.Data, fixture.Info);

        var snapshot = reader.ReadWeatherTransitionSnapshot();

        Assert.NotNull(snapshot);
        Assert.Equal(HeapVa + 0x100, snapshot.SkyVirtualAddress);
        Assert.Equal(HeapVa + 0x400, snapshot.CurrentWeatherPointer);
        Assert.Equal(0x000FFC88u, snapshot.CurrentWeatherFormId);
        Assert.Equal(HeapVa + 0x440, snapshot.OutgoingWeatherPointer);
        Assert.Equal(0x001237D7u, snapshot.OutgoingWeatherFormId);
        Assert.Equal(0.625f, snapshot.CurrentWeatherWeight);
        Assert.Null(snapshot.ModifierElapsedSeconds);
    }

    [Fact]
    public void Reader_RejectsPointerWhoseTargetIsNotTesWeather()
    {
        var fixture = CreateRuntimeFixture("Fallout_Debug.exe");
        fixture.Data[ModuleSize + 0x400 + 4] = 0x34;
        var reader = CreateReader(fixture.Data, fixture.Info);

        Assert.Null(reader.ReadWeatherTransitionSnapshot());
    }

    [Fact]
    public void Reader_PreservesNullCurrentWeatherWhenDefaultWeatherInitializesSky()
    {
        var fixture = CreateRuntimeFixture("Fallout_Debug.exe");
        BinaryPrimitives.WriteUInt32BigEndian(
            fixture.Data.AsSpan(ModuleSize + 0x100 + 0x10), 0);
        var reader = CreateReader(fixture.Data, fixture.Info);

        var snapshot = reader.ReadWeatherTransitionSnapshot();

        Assert.NotNull(snapshot);
        Assert.Null(snapshot.CurrentWeatherPointer);
        Assert.Null(snapshot.CurrentWeatherFormId);
        Assert.Equal(0.625f, snapshot.CurrentWeatherWeight);
    }

    [Fact]
    public void Reader_LeavesUnsupportedGameFamilyAbsent()
    {
        var fixture = CreateRuntimeFixture("Fallout3.exe");
        var reader = CreateReader(fixture.Data, fixture.Info);

        Assert.Null(reader.ReadWeatherTransitionSnapshot());
    }

    [Fact]
    public void Reader_FailsClosedWhenTwoStructurallyValidSkyObjectsExist()
    {
        var fixture = CreateRuntimeFixture("Fallout_Release_Beta.exe", true);
        var reader = CreateReader(fixture.Data, fixture.Info);

        Assert.Null(reader.ReadWeatherTransitionSnapshot());
    }

    [Fact]
    public void Reader_LargeHeapScanUsesBoundedPooledStorage()
    {
        const int largeHeapSize = 32 * 1024 * 1024;
        var fixture = CreateRuntimeFixture("Fallout_Release_Beta.exe", heapSize: largeHeapSize);
        var reader = CreateReader(fixture.Data, fixture.Info);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var snapshot = reader.ReadWeatherTransitionSnapshot();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.NotNull(snapshot);
        Assert.True(allocated < 8 * 1024 * 1024,
            $"Expected a pooled, chunk-bounded scan; allocated {allocated:N0} bytes.");
    }

    [Fact]
    public void Reader_ModuleScanClipsStraddlingRegionAndIgnoresOutsideDecoys()
    {
        var fixture = CreateModuleStraddlingFixture(true);
        var reader = CreateReader(fixture.Data, fixture.Info);

        var snapshot = reader.ReadWeatherTransitionSnapshot();

        Assert.NotNull(snapshot);
        Assert.Equal(HeapVa + 0x100, snapshot.SkyVirtualAddress);
        Assert.Equal(0x000FFC88u, snapshot.CurrentWeatherFormId);
    }

    [Theory]
    [InlineData("type-descriptor")]
    [InlineData("complete-object-locator")]
    [InlineData("vtable-locator")]
    public void Reader_RejectsRttiStructuresThatCrossModuleBoundary(string decoyKind)
    {
        const int padding = 0x200;
        var fixture = CreateModuleStraddlingFixture(false);
        var data = fixture.Data;
        var moduleEnd = ModuleVa + ModuleSize;

        switch (decoyKind)
        {
            case "type-descriptor":
            {
                // The RTTI name begins inside the module, but its required 8-byte descriptor header
                // starts four bytes before the image. A containing memory region makes every byte
                // readable, so only an explicit full-range check rejects it.
                var typeDescriptorVa = ModuleVa - 4;
                var typeDescriptorOffset = padding - 4;
                WriteUInt32(data, typeDescriptorOffset, ModuleVa + 0x50, true);
                WriteUInt32(data, typeDescriptorOffset + 4, 0, true);
                ".?AVSky@@\0"u8.CopyTo(data.AsSpan(typeDescriptorOffset + 8));
                WriteRttiVtableCandidate(
                    data,
                    padding + 0x400,
                    ModuleVa + 0x400,
                    typeDescriptorVa,
                    padding + 0x480);
                break;
            }
            case "complete-object-locator":
            {
                // The type-descriptor reference is the final DWORD in the module, while the COL's
                // hierarchy DWORD falls in the captured suffix outside the image.
                var colVa = moduleEnd - 16;
                var colOffset = padding + ModuleSize - 16;
                WriteUInt32(data, colOffset, 0, true);
                WriteUInt32(data, colOffset + 4, 0, true);
                WriteUInt32(data, colOffset + 8, 0, true);
                WriteUInt32(data, colOffset + 12, ModuleVa + 0x100, true);
                WriteUInt32(data, colOffset + 16, ModuleVa + 0x180, true);
                WriteUInt32(data, padding + 0x480, colVa, true);
                WriteUInt32(data, padding + 0x484, ModuleVa + 0x300, true);
                break;
            }
            case "vtable-locator":
            {
                // The COL pointer fits at the image's final DWORD, but the first vtable entry is in
                // the captured suffix. It must not manufacture an out-of-module Sky vtable.
                var locatorOffset = padding + ModuleSize - 4;
                WriteUInt32(data, locatorOffset, ModuleVa + 0x140, true);
                WriteUInt32(data, locatorOffset + 4, ModuleVa + 0x300, true);
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(decoyKind));
        }

        var reader = CreateReader(data, fixture.Info);
        var snapshot = reader.ReadWeatherTransitionSnapshot();

        Assert.NotNull(snapshot);
        Assert.Equal(HeapVa + 0x100, snapshot.SkyVirtualAddress);
    }

    [Fact]
    public void Reader_HeapScanClipsRegionThatStartsBeforeHeapBase()
    {
        var fixture = CreateHeapStraddlingFixture();
        var reader = CreateReader(fixture.Data, fixture.Info);

        var snapshot = reader.ReadWeatherTransitionSnapshot();

        Assert.NotNull(snapshot);
        Assert.Equal(HeapVa + 0x100, snapshot.SkyVirtualAddress);
        Assert.Equal(0x001237D7u, snapshot.OutgoingWeatherFormId);
    }

    private static RuntimeStructReader CreateReader(byte[] data, MinidumpInfo info)
    {
        return new RuntimeStructReader(
            new ByteArrayMemoryAccessor(data), data.LongLength, info,
            false, null);
    }

    private static (byte[] Data, MinidumpInfo Info) CreateModuleStraddlingFixture(
        bool includeOutsideDecoys)
    {
        const int padding = 0x200;
        var source = CreateRuntimeFixture("Fallout_Release_MemDebug.exe");
        var moduleRegionSize = padding + ModuleSize + padding;
        var data = new byte[moduleRegionSize + HeapSize];
        source.Data.AsSpan(0, ModuleSize).CopyTo(data.AsSpan(padding));
        source.Data.AsSpan(ModuleSize, HeapSize).CopyTo(data.AsSpan(moduleRegionSize));

        if (includeOutsideDecoys)
        {
            var regionStart = ModuleVa - padding;
            WriteRttiVtableCandidate(
                data,
                0x20,
                regionStart + 0x20,
                ModuleVa + 0x100,
                0x60);
            WriteRttiVtableCandidate(
                data,
                padding + ModuleSize + 0x20,
                ModuleVa + ModuleSize + 0x20,
                ModuleVa + 0x100,
                padding + ModuleSize + 0x60);
        }

        var info = new MinidumpInfo
        {
            IsValid = true,
            ProcessorArchitecture = 0x03,
            NumberOfStreams = 1,
            Modules =
            [
                new MinidumpModule
                {
                    Name = "Fallout_Release_MemDebug.exe",
                    BaseAddress = Xbox360MemoryUtils.VaToLong(ModuleVa),
                    Size = ModuleSize
                }
            ],
            MemoryRegions =
            [
                new MinidumpMemoryRegion
                {
                    VirtualAddress = Xbox360MemoryUtils.VaToLong(ModuleVa - padding),
                    Size = moduleRegionSize,
                    FileOffset = 0
                },
                new MinidumpMemoryRegion
                {
                    VirtualAddress = HeapVa,
                    Size = HeapSize,
                    FileOffset = moduleRegionSize
                }
            ]
        };
        return (data, info);
    }

    private static (byte[] Data, MinidumpInfo Info) CreateHeapStraddlingFixture()
    {
        const int prefix = 0x200;
        var source = CreateRuntimeFixture("Fallout_Release_MemDebug.exe");
        var data = new byte[ModuleSize + prefix + HeapSize];
        source.Data.AsSpan(0, ModuleSize).CopyTo(data);
        source.Data.AsSpan(ModuleSize, HeapSize).CopyTo(data.AsSpan(ModuleSize + prefix));

        // A structurally valid duplicate Sky sits in the captured prefix below HeapBase. An overlap-only
        // scan would see both it and the real singleton; the clipped scan must begin exactly at HeapBase.
        const int decoyOffset = ModuleSize + 0x20;
        WriteUInt32(data, decoyOffset, ModuleVa + 0x204, true);
        WriteUInt32(data, decoyOffset + 0x10, HeapVa + 0x400, true);
        WriteUInt32(data, decoyOffset + 0x14, HeapVa + 0x440, true);
        WriteUInt32(data, decoyOffset + 0x18, HeapVa + 0x400, true);
        WriteFloat(data, decoyOffset + 0xF4, 0.625f, true);

        var info = new MinidumpInfo
        {
            IsValid = true,
            ProcessorArchitecture = 0x03,
            NumberOfStreams = 1,
            Modules =
            [
                new MinidumpModule
                {
                    Name = "Fallout_Release_MemDebug.exe",
                    BaseAddress = Xbox360MemoryUtils.VaToLong(ModuleVa),
                    Size = ModuleSize
                }
            ],
            MemoryRegions =
            [
                new MinidumpMemoryRegion
                {
                    VirtualAddress = Xbox360MemoryUtils.VaToLong(ModuleVa),
                    Size = ModuleSize,
                    FileOffset = 0
                },
                new MinidumpMemoryRegion
                {
                    VirtualAddress = HeapVa - prefix,
                    Size = prefix + HeapSize,
                    FileOffset = ModuleSize
                }
            ]
        };
        return (data, info);
    }

    private static void WriteRttiVtableCandidate(
        byte[] data,
        int colOffset,
        uint colVa,
        uint typeDescriptorVa,
        int locatorOffset)
    {
        WriteUInt32(data, colOffset, 0, true);
        WriteUInt32(data, colOffset + 4, 0, true);
        WriteUInt32(data, colOffset + 8, 0, true);
        WriteUInt32(data, colOffset + 12, typeDescriptorVa, true);
        WriteUInt32(data, colOffset + 16, ModuleVa + 0x180, true);
        WriteUInt32(data, locatorOffset, colVa, true);
        WriteUInt32(data, locatorOffset + 4, ModuleVa + 0x300, true);
    }

    private static (byte[] Data, MinidumpInfo Info) CreateRuntimeFixture(
        string moduleName,
        bool duplicateSky = false,
        int heapSize = HeapSize)
    {
        var data = new byte[ModuleSize + heapSize];

        const int typeDescriptorOffset = 0x100;
        const int completeObjectLocatorOffset = 0x140;
        const int hierarchyOffset = 0x180;
        const int vtableLocatorSlotOffset = 0x200;
        const int skyOffset = 0x100;

        WriteUInt32(data, typeDescriptorOffset, ModuleVa + 0x50, true);
        ".?AVSky@@\0"u8.CopyTo(data.AsSpan(typeDescriptorOffset + 8));

        WriteUInt32(data, completeObjectLocatorOffset + 12, ModuleVa + typeDescriptorOffset, true);
        WriteUInt32(data, completeObjectLocatorOffset + 16, ModuleVa + hierarchyOffset, true);

        WriteUInt32(data, vtableLocatorSlotOffset, ModuleVa + completeObjectLocatorOffset, true);
        var skyVtable = ModuleVa + vtableLocatorSlotOffset + 4;
        WriteUInt32(data, vtableLocatorSlotOffset + 4, ModuleVa + 0x300, true);

        WriteSky(ModuleSize + skyOffset);
        if (duplicateSky)
        {
            WriteSky(ModuleSize + 0x200);
        }

        WriteWeather(data, ModuleSize + 0x400, 0x000FFC88);
        WriteWeather(data, ModuleSize + 0x440, 0x001237D7);

        var info = new MinidumpInfo
        {
            IsValid = true,
            ProcessorArchitecture = 0x03,
            NumberOfStreams = 1,
            Modules =
            [
                new MinidumpModule
                {
                    Name = moduleName,
                    BaseAddress = Xbox360MemoryUtils.VaToLong(ModuleVa),
                    Size = ModuleSize
                }
            ],
            MemoryRegions =
            [
                new MinidumpMemoryRegion
                {
                    VirtualAddress = Xbox360MemoryUtils.VaToLong(ModuleVa),
                    Size = ModuleSize,
                    FileOffset = 0
                },
                new MinidumpMemoryRegion
                {
                    VirtualAddress = HeapVa,
                    Size = heapSize,
                    FileOffset = ModuleSize
                }
            ]
        };

        return (data, info);

        void WriteSky(int offset)
        {
            WriteUInt32(data, offset, skyVtable, true);
            WriteUInt32(data, offset + 0x10, HeapVa + 0x400, true);
            WriteUInt32(data, offset + 0x14, HeapVa + 0x440, true);
            WriteUInt32(data, offset + 0x18, HeapVa + 0x400, true);
            WriteFloat(data, offset + 0xF4, 0.625f, true);
        }

        void WriteWeather(byte[] target, int offset, uint formId)
        {
            WriteUInt32(target, offset, ModuleVa + 0x500, true);
            target[offset + 4] = 0x35;
            WriteUInt32(target, offset + 12, formId, true);
        }
    }

    private static void WriteFloat(byte[] data, int offset, float value, bool bigEndian)
    {
        WriteUInt32(data, offset, BitConverter.SingleToUInt32Bits(value), bigEndian);
    }

    private static void WriteUInt32(byte[] data, int offset, uint value, bool bigEndian)
    {
        if (bigEndian)
        {
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(offset), value);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), value);
        }
    }
}