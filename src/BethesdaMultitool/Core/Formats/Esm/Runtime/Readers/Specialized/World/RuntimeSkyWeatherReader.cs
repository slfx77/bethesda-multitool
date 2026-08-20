using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Minidump;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Esm.Runtime.Readers.Specialized.World;

/// <summary>
///     Locates the FNV <c>Sky</c> singleton through its MSVC RTTI and projects the authored
///     weather-transition state. The supported layout is grounded in every available FNV PDB
///     configuration (Debug, Release Beta, Release MemDebug, and Final):
///     <c>pCurrentWeather +0x10</c>, <c>pLastWeather +0x14</c>,
///     <c>pDefaultWeather +0x18</c>, and <c>fCurrentWeatherPct +0xF4</c> in a 312-byte Sky.
///     Real Debug, Release Beta, and Release MemDebug DMPs independently validate the same
///     offsets and TESWeather FormType 0x35.
/// </summary>
internal sealed class RuntimeSkyWeatherReader
{
    private const int SearchChunkSize = 1024 * 1024;
    private const int SkySnapshotSize = 0xF8;
    private const int DefaultWeatherOffset = 0x18;
    private const byte WeatherFormType = 0x35;

    private static readonly byte[] SkyRttiName = Encoding.ASCII.GetBytes(".?AVSky@@\0");

    private static readonly HashSet<string> SupportedFNVModules = new(StringComparer.OrdinalIgnoreCase)
    {
        "Fallout_Debug.exe",
        "Fallout_Release_Beta.exe",
        "Fallout_Release_MemDebug.exe"
    };

    private readonly RuntimeMemoryContext _context;
    private readonly Lazy<WeatherTransitionSnapshot?> _snapshot;

    public RuntimeSkyWeatherReader(RuntimeMemoryContext context)
    {
        _context = context;
        _snapshot = new Lazy<WeatherTransitionSnapshot?>(ReadCore,
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public WeatherTransitionSnapshot? Read()
    {
        return _snapshot.Value;
    }

    private WeatherTransitionSnapshot? ReadCore()
    {
        if (!_context.MinidumpInfo.IsXbox360)
        {
            return null;
        }

        var module = _context.MinidumpInfo.FindGameModule();
        if (module == null || !SupportedFNVModules.Contains(Path.GetFileName(module.Name)))
        {
            // FO3, Oblivion, Skyrim, and modern games have not had their Sky layouts and
            // singleton paths validated. Fail closed instead of applying the FNV offsets.
            return null;
        }

        var skyVtable = LocateUniqueSkyVtable(module);
        if (skyVtable is not { } vtable)
        {
            return null;
        }

        var vtablePattern = GetBigEndianBytes(vtable);
        var candidates = FindPatternVirtualAddresses(
                vtablePattern,
                Xbox360MemoryUtils.HeapBase,
                Xbox360MemoryUtils.HeapEnd)
            .Where(va => (va & 3) == 0)
            .Distinct()
            .ToList();

        WeatherTransitionSnapshot? result = null;
        foreach (var skyVa in candidates)
        {
            var bytes = _context.ReadBytesAtVa(Xbox360MemoryUtils.VaToLong(skyVa), SkySnapshotSize);
            if (bytes == null || BinaryPrimitives.ReadUInt32BigEndian(bytes) != vtable)
            {
                continue;
            }

            // The current weather is legitimately null in some interior/debug captures. The
            // default weather gives us an independent structural check that this is a live,
            // initialized Sky object rather than another occurrence of the vtable value.
            var currentPtr = BinaryPrimitives.ReadUInt32BigEndian(
                bytes.AsSpan(WeatherTransitionSnapshotParser.CurrentWeatherOffset));
            var defaultPtr = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(DefaultWeatherOffset));
            if (currentPtr == 0 && defaultPtr == 0)
            {
                continue;
            }

            if (defaultPtr != 0 && ResolveWeatherFormId(defaultPtr, module) == null)
            {
                continue;
            }

            var snapshot = WeatherTransitionSnapshotParser.Parse(
                bytes,
                true,
                skyVa,
                pointer => ResolveWeatherFormId(pointer, module));
            if (snapshot == null)
            {
                continue;
            }

            // Ambiguity is unsafe: a singleton must produce exactly one structurally valid hit.
            if (result != null)
            {
                return null;
            }

            result = snapshot;
        }

        return result;
    }

    private uint? LocateUniqueSkyVtable(MinidumpModule module)
    {
        var rttiNames = FindPatternVirtualAddresses(
                SkyRttiName,
                module.BaseAddress32,
                ModuleEnd(module))
            .Distinct()
            .ToList();

        var typeDescriptors = new HashSet<uint>();
        foreach (var nameVa in rttiNames)
        {
            if (nameVa < 8)
            {
                continue;
            }

            var typeDescriptorVa = nameVa - 8;
            if (!IsRangeInModule(typeDescriptorVa, 8 + SkyRttiName.Length, module))
            {
                continue;
            }

            var header = ReadAtVa(typeDescriptorVa, 8);
            if (header == null)
            {
                continue;
            }

            var typeInfoVtable = BinaryPrimitives.ReadUInt32BigEndian(header);
            var spare = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4));
            if (spare == 0 && IsAddressInModule(typeInfoVtable, module))
            {
                typeDescriptors.Add(typeDescriptorVa);
            }
        }

        var completeObjectLocators = new HashSet<uint>();
        foreach (var typeDescriptorVa in typeDescriptors)
        {
            foreach (var referenceVa in FindPatternVirtualAddresses(
                         GetBigEndianBytes(typeDescriptorVa),
                         module.BaseAddress32,
                         ModuleEnd(module)))
            {
                if (referenceVa < 12)
                {
                    continue;
                }

                var colVa = referenceVa - 12;
                if (!IsRangeInModule(colVa, 20, module))
                {
                    continue;
                }

                var col = ReadAtVa(colVa, 20);
                if (col == null ||
                    BinaryPrimitives.ReadUInt32BigEndian(col) != 0 ||
                    BinaryPrimitives.ReadUInt32BigEndian(col.AsSpan(4)) != 0 ||
                    BinaryPrimitives.ReadUInt32BigEndian(col.AsSpan(8)) != 0 ||
                    BinaryPrimitives.ReadUInt32BigEndian(col.AsSpan(12)) != typeDescriptorVa)
                {
                    continue;
                }

                var hierarchy = BinaryPrimitives.ReadUInt32BigEndian(col.AsSpan(16));
                if (IsAddressInModule(hierarchy, module))
                {
                    completeObjectLocators.Add(colVa);
                }
            }
        }

        var vtables = new HashSet<uint>();
        foreach (var colVa in completeObjectLocators)
        {
            foreach (var referenceVa in FindPatternVirtualAddresses(
                         GetBigEndianBytes(colVa),
                         module.BaseAddress32,
                         ModuleEnd(module)))
            {
                // The locator slot immediately precedes the vtable. Both the pointer-sized locator
                // reference and the first virtual entry must belong to the module; a captured memory
                // region can legitimately extend beyond the image boundary.
                if (!IsRangeInModule(referenceVa, 4, module))
                {
                    continue;
                }

                var vtableVa = referenceVa + 4;
                if (!IsRangeInModule(vtableVa, 4, module))
                {
                    continue;
                }

                var firstVirtual = ReadUInt32AtVa(vtableVa);
                if (firstVirtual is { } functionVa && IsAddressInModule(functionVa, module))
                {
                    vtables.Add(vtableVa);
                }
            }
        }

        return vtables.Count == 1 ? vtables.Single() : null;
    }

    private uint? ResolveWeatherFormId(uint pointer, MinidumpModule module)
    {
        var form = ReadAtVa(pointer, 16);
        if (form == null || form[4] != WeatherFormType)
        {
            return null;
        }

        var vtable = BinaryPrimitives.ReadUInt32BigEndian(form);
        if (!IsAddressInModule(vtable, module))
        {
            return null;
        }

        var formId = BinaryPrimitives.ReadUInt32BigEndian(form.AsSpan(12));
        return formId is 0 or 0xFFFFFFFF ? null : formId;
    }

    private List<uint> FindPatternVirtualAddresses(
        byte[] pattern,
        uint rangeStart,
        ulong rangeEndExclusive)
    {
        var results = new List<uint>();
        const ulong addressSpaceEnd = 1UL << 32;
        var boundedRangeEnd = Math.Min(rangeEndExclusive, addressSpaceEnd);
        if (pattern.Length == 0 || boundedRangeEnd <= rangeStart)
        {
            return results;
        }

        // A real FNV dump contains hundreds of megabytes of captured heap. Reusing one pooled
        // chunk keeps RTTI/vtable discovery bounded instead of allocating one byte[] per megabyte
        // for every independent pattern scan.
        var buffer = ArrayPool<byte>.Shared.Rent(SearchChunkSize + pattern.Length - 1);
        try
        {
            foreach (var region in _context.MinidumpInfo.MemoryRegions)
            {
                if (region.Size <= 0)
                {
                    continue;
                }

                var regionStart = (ulong)unchecked((uint)region.VirtualAddress);
                var regionEnd = Math.Min(regionStart + (ulong)region.Size, addressSpaceEnd);
                var scanStart = Math.Max(regionStart, rangeStart);
                var scanEnd = Math.Min(regionEnd, boundedRangeEnd);
                if (scanEnd <= scanStart)
                {
                    continue;
                }

                // Scan only the intersection. In particular, the pattern overlap at each chunk edge
                // must not read through the requested module/heap boundary into adjacent captured data.
                var scanLength = checked((long)(scanEnd - scanStart));
                long consumed = 0;
                while (consumed < scanLength)
                {
                    var remaining = scanLength - consumed;
                    var logicalLength = (int)Math.Min(SearchChunkSize, remaining);
                    var overlap = (int)Math.Min(pattern.Length - 1, remaining - logicalLength);
                    var readLength = logicalLength + overlap;
                    var startVa = checked((uint)(scanStart + (ulong)consumed));
                    var startVaLong = Xbox360MemoryUtils.VaToLong(startVa);
                    if (!_context.MinidumpInfo.IsVaRangeCaptured(startVaLong, readLength))
                    {
                        break;
                    }

                    var fileOffset = _context.MinidumpInfo.VirtualAddressToFileOffset(startVaLong);
                    if (!fileOffset.HasValue || fileOffset.Value < 0
                                             || fileOffset.Value + readLength > _context.FileSize)
                    {
                        break;
                    }

                    try
                    {
                        _context.Accessor.ReadArray(fileOffset.Value, buffer, 0, readLength);
                    }
                    catch
                    {
                        break;
                    }

                    var search = buffer.AsSpan(0, readLength);
                    var searchBase = 0;
                    while (searchBase <= logicalLength - 1)
                    {
                        var relative = search[searchBase..].IndexOf(pattern);
                        if (relative < 0)
                        {
                            break;
                        }

                        var index = searchBase + relative;
                        if (index < logicalLength)
                        {
                            results.Add(startVa + checked((uint)index));
                        }

                        searchBase = index + 1;
                    }

                    consumed += logicalLength;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return results;
    }

    private byte[]? ReadAtVa(uint va, int count)
    {
        return _context.ReadBytesAtVa(Xbox360MemoryUtils.VaToLong(va), count);
    }

    private uint? ReadUInt32AtVa(uint va)
    {
        var bytes = ReadAtVa(va, 4);
        return bytes == null ? null : BinaryPrimitives.ReadUInt32BigEndian(bytes);
    }

    private static bool IsAddressInModule(uint address, MinidumpModule module)
    {
        return IsRangeInModule(address, 1, module);
    }

    private static bool IsRangeInModule(uint address, int length, MinidumpModule module)
    {
        var start = (ulong)address;
        var end = start + (uint)length;
        var moduleStart = (ulong)module.BaseAddress32;
        var moduleEnd = ModuleEnd(module);
        return start >= moduleStart && end <= moduleEnd;
    }

    private static ulong ModuleEnd(MinidumpModule module)
    {
        return (ulong)module.BaseAddress32 + (uint)Math.Max(module.Size, 0);
    }

    private static byte[] GetBigEndianBytes(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return bytes;
    }
}

/// <summary>
///     Endian-explicit parser for the recovered Sky fields. Keeping this independent from the
///     DMP locator makes the recovered layout testable against both Xenon and host-order fixture
///     bytes, and keeps expected FormID resolution outside the production parser.
/// </summary>
internal static class WeatherTransitionSnapshotParser
{
    internal const int CurrentWeatherOffset = 0x10;
    internal const int OutgoingWeatherOffset = 0x14;
    internal const int CurrentWeatherWeightOffset = 0xF4;
    internal const int RequiredSize = 0xF8;

    public static WeatherTransitionSnapshot? Parse(
        ReadOnlySpan<byte> sky,
        bool isBigEndian,
        uint skyVirtualAddress,
        Func<uint, uint?> resolveWeatherFormId)
    {
        ArgumentNullException.ThrowIfNull(resolveWeatherFormId);
        if (sky.Length < RequiredSize)
        {
            return null;
        }

        var currentPointer = ReadUInt32(sky[CurrentWeatherOffset..], isBigEndian);
        var outgoingPointer = ReadUInt32(sky[OutgoingWeatherOffset..], isBigEndian);
        var weightBits = ReadUInt32(sky[CurrentWeatherWeightOffset..], isBigEndian);
        var currentWeight = BitConverter.UInt32BitsToSingle(weightBits);
        if (!float.IsFinite(currentWeight) || currentWeight < 0f || currentWeight > 1f)
        {
            return null;
        }

        var currentFormId = Resolve(currentPointer, resolveWeatherFormId);
        if (currentPointer != 0 && currentFormId == null)
        {
            return null;
        }

        var outgoingFormId = Resolve(outgoingPointer, resolveWeatherFormId);
        if (outgoingPointer != 0 && outgoingFormId == null)
        {
            return null;
        }

        return new WeatherTransitionSnapshot(
            skyVirtualAddress,
            currentPointer == 0 ? null : currentPointer,
            currentFormId,
            outgoingPointer == 0 ? null : outgoingPointer,
            outgoingFormId,
            currentWeight,
            null);
    }

    private static uint? Resolve(uint pointer, Func<uint, uint?> resolveWeatherFormId)
    {
        return pointer == 0 ? null : resolveWeatherFormId(pointer);
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> bytes, bool isBigEndian)
    {
        return isBigEndian
            ? BinaryPrimitives.ReadUInt32BigEndian(bytes)
            : BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }
}
