using System.Buffers;
using System.Globalization;
using System.Text;
using Xbox360MemoryCarver.Core.Utils;

namespace Xbox360MemoryCarver.Core.Minidump;

/// <summary>
///     Information about a module loaded in the minidump.
/// </summary>
public class MinidumpModule
{
    public required string Name { get; init; }
    public long BaseAddress { get; init; }
    public int Size { get; init; }
    public uint Checksum { get; init; }
    public uint TimeDateStamp { get; init; }

    /// <summary>
    ///     Get the 32-bit base address (Xbox 360 uses 32-bit addresses).
    /// </summary>
    public uint BaseAddress32 => (uint)(BaseAddress & 0xFFFFFFFF);
}

/// <summary>
///     Represents a memory region in the minidump.
/// </summary>
public class MinidumpMemoryRegion
{
    public long VirtualAddress { get; init; }
    public long Size { get; init; }
    public long FileOffset { get; init; }
}

/// <summary>
///     Parsed minidump header and directory information.
/// </summary>
public class MinidumpInfo
{
    public bool IsValid { get; init; }
    public ushort ProcessorArchitecture { get; set; }
    public uint NumberOfStreams { get; init; }
    public List<MinidumpModule> Modules { get; init; } = [];
    public List<MinidumpMemoryRegion> MemoryRegions { get; init; } = [];

    /// <summary>
    ///     True if this is an Xbox 360 (PowerPC) minidump.
    /// </summary>
    public bool IsXbox360 => ProcessorArchitecture == 0x03; // PowerPC

    /// <summary>
    ///     Find a module by virtual address.
    /// </summary>
    public MinidumpModule? FindModuleByVirtualAddress(long virtualAddress)
    {
        return Modules.FirstOrDefault(m =>
            virtualAddress >= m.BaseAddress &&
            virtualAddress < m.BaseAddress + m.Size);
    }

    /// <summary>
    ///     Convert a file offset to a virtual address using memory regions.
    /// </summary>
    public long? FileOffsetToVirtualAddress(long fileOffset)
    {
        foreach (var region in MemoryRegions)
        {
            if (fileOffset >= region.FileOffset &&
                fileOffset < region.FileOffset + region.Size)
            {
                var offsetInRegion = fileOffset - region.FileOffset;
                return region.VirtualAddress + offsetInRegion;
            }
        }
        return null;
    }

    /// <summary>
    ///     Find a module by file offset (converts to virtual address first).
    /// </summary>
    public MinidumpModule? FindModuleByFileOffset(long fileOffset)
    {
        var virtualAddress = FileOffsetToVirtualAddress(fileOffset);
        return virtualAddress.HasValue ? FindModuleByVirtualAddress(virtualAddress.Value) : null;
    }

    /// <summary>
    ///     Get the file offset range for a module (if its memory is captured in the dump).
    ///     Returns the total contiguous captured size starting from the module base.
    /// </summary>
    public (long fileOffset, long size)? GetModuleFileRange(MinidumpModule module)
    {
        // Find the first memory region that contains the module's base address
        var moduleStart = module.BaseAddress;

        foreach (var region in MemoryRegions)
        {
            var regionStart = region.VirtualAddress;
            var regionEnd = region.VirtualAddress + region.Size;

            // Check if module base falls within this region
            if (moduleStart >= regionStart && moduleStart < regionEnd)
            {
                var offsetInRegion = moduleStart - regionStart;
                var fileOffset = region.FileOffset + offsetInRegion;

                // Calculate how much of the module is captured in this and subsequent contiguous regions
                var capturedSize = CalculateContiguousCapturedSize(module, region);

                return (fileOffset, capturedSize);
            }
        }

        return null;
    }

    /// <summary>
    ///     Calculate the total contiguous captured size for a module starting from a given region.
    /// </summary>
    private long CalculateContiguousCapturedSize(MinidumpModule module, MinidumpMemoryRegion startRegion)
    {
        var moduleStart = module.BaseAddress;
        var moduleEnd = module.BaseAddress + module.Size;

        // Start with the portion in the first region
        var regionEnd = startRegion.VirtualAddress + startRegion.Size;
        var capturedEnd = Math.Min(regionEnd, moduleEnd);
        var totalCaptured = capturedEnd - moduleStart;

        // Look for contiguous regions that continue the module
        var currentVA = regionEnd;
        foreach (var region in MemoryRegions.Where(r => r.VirtualAddress >= regionEnd).OrderBy(r => r.VirtualAddress))
        {
            // Check if this region is contiguous with what we've captured
            if (region.VirtualAddress != currentVA)
                break; // Gap in coverage

            // Check if this region overlaps with the module
            if (region.VirtualAddress >= moduleEnd)
                break; // Past the module

            // Add the portion of this region that's within the module
            var regionCapturedEnd = Math.Min(region.VirtualAddress + region.Size, moduleEnd);
            totalCaptured += regionCapturedEnd - region.VirtualAddress;

            currentVA = region.VirtualAddress + region.Size;

            if (currentVA >= moduleEnd)
                break; // Captured the whole module
        }

        return totalCaptured;
    }

    /// <summary>
    ///     Generate a diagnostic report of the minidump structure.
    /// </summary>
    public string GenerateDiagnosticReport()
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"=== Minidump Diagnostic Report ===");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Valid: {IsValid}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Architecture: 0x{ProcessorArchitecture:X4} ({(IsXbox360 ? "Xbox 360 / PowerPC" : "Other")})");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Streams: {NumberOfStreams}");
        sb.AppendLine();

        sb.AppendLine(CultureInfo.InvariantCulture, $"=== Modules ({Modules.Count}) ===");
        foreach (var module in Modules.OrderBy(m => m.BaseAddress32))
        {
            var fileRange = GetModuleFileRange(module);
            var fileName = Path.GetFileName(module.Name);
            sb.Append(CultureInfo.InvariantCulture, $"  {fileName,-30} Base: 0x{module.BaseAddress32:X8} Size: {module.Size,12:N0}");
            if (fileRange.HasValue)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $" -> File: 0x{fileRange.Value.fileOffset:X8} ({fileRange.Value.size:N0} bytes captured)");
            }
            else
            {
                sb.AppendLine(" -> NOT IN DUMP");
            }
        }
        sb.AppendLine();

        sb.AppendLine(CultureInfo.InvariantCulture, $"=== Memory Regions ({MemoryRegions.Count}) ===");
        var totalCaptured = MemoryRegions.Sum(r => r.Size);
        sb.AppendLine(CultureInfo.InvariantCulture, $"Total memory captured: {totalCaptured:N0} bytes ({totalCaptured / (1024.0 * 1024.0):F2} MB)");

        // Show first and last few regions
        var regionsToShow = MemoryRegions.Take(5).Concat(MemoryRegions.TakeLast(5)).Distinct().ToList();
        foreach (var region in regionsToShow.OrderBy(r => r.FileOffset))
        {
            var va32 = (uint)(region.VirtualAddress & 0xFFFFFFFF);
            sb.AppendLine(CultureInfo.InvariantCulture, $"  VA: 0x{va32:X8} Size: {region.Size,12:N0} File: 0x{region.FileOffset:X8}");
        }
        if (MemoryRegions.Count > 10)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"  ... ({MemoryRegions.Count - 10} more regions) ...");
        }

        return sb.ToString();
    }
}

/// <summary>
///     Parses Microsoft Minidump files to extract module information.
///     Reference: https://docs.microsoft.com/en-us/windows/win32/api/minidumpapiset/
/// </summary>
public static class MinidumpParser
{
    // Minidump signature "MDMP"
    private static readonly byte[] MinidumpSignature = [0x4D, 0x44, 0x4D, 0x50];

    // Stream types
    private const uint ModuleListStream = 4;      // MINIDUMP_MODULE_LIST
    private const uint SystemInfoStream = 7;      // MINIDUMP_SYSTEM_INFO
    private const uint Memory64ListStream = 9;    // MINIDUMP_MEMORY64_LIST

    /// <summary>
    ///     Parse a minidump file to extract module information.
    /// </summary>
    public static MinidumpInfo Parse(string filePath)
    {
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Parse(fs);
    }

    /// <summary>
    ///     Parse a minidump from a stream.
    /// </summary>
    public static MinidumpInfo Parse(Stream stream)
    {
        var headerBuffer = new byte[32];
        if (stream.Read(headerBuffer, 0, 32) < 32)
        {
            return new MinidumpInfo { IsValid = false };
        }

        // Validate signature
        if (!headerBuffer.AsSpan(0, 4).SequenceEqual(MinidumpSignature))
        {
            return new MinidumpInfo { IsValid = false };
        }

        // Parse header (all little-endian)
        // Offset 4: Version (uint16)
        // Offset 8: NumberOfStreams (uint32)
        // Offset 12: StreamDirectoryRva (uint32)
        var numberOfStreams = BinaryUtils.ReadUInt32LE(headerBuffer, 8);
        var streamDirectoryRva = BinaryUtils.ReadUInt32LE(headerBuffer, 12);

        if (numberOfStreams == 0 || numberOfStreams > 100 || streamDirectoryRva == 0)
        {
            return new MinidumpInfo { IsValid = false };
        }

        // Read stream directory
        var directorySize = (int)(numberOfStreams * 12); // Each entry is 12 bytes
        var directoryBuffer = ArrayPool<byte>.Shared.Rent(directorySize);
        try
        {
            stream.Seek(streamDirectoryRva, SeekOrigin.Begin);
            if (stream.Read(directoryBuffer, 0, directorySize) < directorySize)
            {
                return new MinidumpInfo { IsValid = false };
            }

            var result = new MinidumpInfo
            {
                IsValid = true,
                NumberOfStreams = numberOfStreams
            };

            // Find and parse streams
            for (var i = 0; i < numberOfStreams; i++)
            {
                var entryOffset = i * 12;
                var streamType = BinaryUtils.ReadUInt32LE(directoryBuffer, entryOffset);
                var dataSize = BinaryUtils.ReadUInt32LE(directoryBuffer, entryOffset + 4);
                var rva = BinaryUtils.ReadUInt32LE(directoryBuffer, entryOffset + 8);

                switch (streamType)
                {
                    case SystemInfoStream:
                        ParseSystemInfo(stream, rva, result);
                        break;
                    case ModuleListStream:
                        ParseModuleList(stream, rva, result);
                        break;
                    case Memory64ListStream:
                        ParseMemory64List(stream, rva, dataSize, result);
                        break;
                }
            }

            return result;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(directoryBuffer);
        }
    }

    private static void ParseSystemInfo(Stream stream, uint rva, MinidumpInfo result)
    {
        var buffer = new byte[4];
        stream.Seek(rva, SeekOrigin.Begin);
        if (stream.Read(buffer, 0, 4) < 4) return;

        // First 2 bytes are ProcessorArchitecture
        result.ProcessorArchitecture = BinaryUtils.ReadUInt16LE(buffer, 0);
    }

    private static void ParseModuleList(Stream stream, uint rva, MinidumpInfo result)
    {
        stream.Seek(rva, SeekOrigin.Begin);

        // Read number of modules (first 4 bytes)
        var countBuffer = new byte[4];
        if (stream.Read(countBuffer, 0, 4) < 4) return;

        var numberOfModules = BinaryUtils.ReadUInt32LE(countBuffer, 0);
        if (numberOfModules == 0 || numberOfModules > 1000) return;

        // Each MINIDUMP_MODULE is 108 bytes
        const int moduleEntrySize = 108;
        var modulesBuffer = ArrayPool<byte>.Shared.Rent((int)(numberOfModules * moduleEntrySize));

        try
        {
            var bytesToRead = (int)(numberOfModules * moduleEntrySize);
            if (stream.Read(modulesBuffer, 0, bytesToRead) < bytesToRead) return;

            for (var i = 0; i < numberOfModules; i++)
            {
                var offset = i * moduleEntrySize;
                var module = ParseModule(stream, modulesBuffer, offset);
                if (module != null)
                {
                    result.Modules.Add(module);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(modulesBuffer);
        }
    }

#pragma warning disable S1172 // Unused parameters should be removed
    private static void ParseMemory64List(Stream stream, uint rva, uint _, MinidumpInfo result)
#pragma warning restore S1172
    {
        stream.Seek(rva, SeekOrigin.Begin);

        // MINIDUMP_MEMORY64_LIST:
        // 0x00: NumberOfMemoryRanges (uint64)
        // 0x08: BaseRva (uint64) - file offset where memory data starts
        // 0x10: Array of MINIDUMP_MEMORY_DESCRIPTOR64
        var headerBuffer = new byte[16];
        if (stream.Read(headerBuffer, 0, 16) < 16) return;

        var numberOfRanges = BinaryUtils.ReadUInt64LE(headerBuffer, 0);
        var baseRva = (long)BinaryUtils.ReadUInt64LE(headerBuffer, 8);

        if (numberOfRanges == 0 || numberOfRanges > 10000) return;

        // Each MINIDUMP_MEMORY_DESCRIPTOR64 is 16 bytes
        const int descriptorSize = 16;
        var descriptorsSize = (int)(numberOfRanges * descriptorSize);
        var descriptorsBuffer = ArrayPool<byte>.Shared.Rent(descriptorsSize);

        try
        {
            if (stream.Read(descriptorsBuffer, 0, descriptorsSize) < descriptorsSize) return;

            var currentFileOffset = baseRva;

            for (var i = 0; i < (int)numberOfRanges; i++)
            {
                var offset = i * descriptorSize;

                // MINIDUMP_MEMORY_DESCRIPTOR64:
                // 0x00: StartOfMemoryRange (uint64) - virtual address
                // 0x08: DataSize (uint64)
                var virtualAddress = (long)BinaryUtils.ReadUInt64LE(descriptorsBuffer, offset);
                var regionSize = (long)BinaryUtils.ReadUInt64LE(descriptorsBuffer, offset + 8);

                result.MemoryRegions.Add(new MinidumpMemoryRegion
                {
                    VirtualAddress = virtualAddress,
                    Size = regionSize,
                    FileOffset = currentFileOffset
                });

                currentFileOffset += regionSize;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(descriptorsBuffer);
        }
    }

    private static MinidumpModule? ParseModule(Stream stream, byte[] buffer, int offset)
    {
        // MINIDUMP_MODULE structure:
        // 0x00: BaseOfImage (uint64)
        // 0x08: SizeOfImage (uint32)
        // 0x0C: CheckSum (uint32)
        // 0x10: TimeDateStamp (uint32)
        // 0x14: ModuleNameRva (uint32) - points to MINIDUMP_STRING

        var baseAddress = (long)BinaryUtils.ReadUInt64LE(buffer, offset);
        var size = (int)BinaryUtils.ReadUInt32LE(buffer, offset + 0x08);
        var checksum = BinaryUtils.ReadUInt32LE(buffer, offset + 0x0C);
        var timestamp = BinaryUtils.ReadUInt32LE(buffer, offset + 0x10);
        var nameRva = BinaryUtils.ReadUInt32LE(buffer, offset + 0x14);

        if (nameRva == 0 || size == 0) return null;

        var name = ReadMinidumpString(stream, nameRva);
        if (string.IsNullOrEmpty(name)) return null;

        return new MinidumpModule
        {
            Name = name,
            BaseAddress = baseAddress,
            Size = size,
            Checksum = checksum,
            TimeDateStamp = timestamp
        };
    }

    private static string? ReadMinidumpString(Stream stream, uint rva)
    {
        // MINIDUMP_STRING:
        // 0x00: Length (uint32) - length in bytes (not including null terminator)
        // 0x04: Buffer (wchar[]) - Unicode string

        var currentPos = stream.Position;
        try
        {
            stream.Seek(rva, SeekOrigin.Begin);

            var lengthBuffer = new byte[4];
            if (stream.Read(lengthBuffer, 0, 4) < 4) return null;

            var length = (int)BinaryUtils.ReadUInt32LE(lengthBuffer, 0);
            if (length == 0 || length > 520) return null; // Max path * 2 for unicode

            var stringBuffer = new byte[length];
            if (stream.Read(stringBuffer, 0, length) < length) return null;

            // Decode as UTF-16LE (Windows Unicode)
            return Encoding.Unicode.GetString(stringBuffer).TrimEnd('\0');
        }
        finally
        {
            stream.Seek(currentPos, SeekOrigin.Begin);
        }
    }
}
