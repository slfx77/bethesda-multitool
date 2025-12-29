using System.Globalization;
using System.Text;

namespace Xbox360MemoryCarver.Core.Minidump;

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
            if (fileOffset >= region.FileOffset && fileOffset < region.FileOffset + region.Size)
            {
                var offsetInRegion = fileOffset - region.FileOffset;
                return region.VirtualAddress + offsetInRegion;
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
    /// </summary>
    public (long fileOffset, long size)? GetModuleFileRange(MinidumpModule module)
    {
        var moduleStart = module.BaseAddress;

        foreach (var region in MemoryRegions)
        {
            var regionStart = region.VirtualAddress;
            var regionEnd = region.VirtualAddress + region.Size;

            if (moduleStart >= regionStart && moduleStart < regionEnd)
            {
                var offsetInRegion = moduleStart - regionStart;
                var fileOffset = region.FileOffset + offsetInRegion;
                var capturedSize = CalculateContiguousCapturedSize(module, region);
                return (fileOffset, capturedSize);
            }
        }

        return null;
    }

    private long CalculateContiguousCapturedSize(MinidumpModule module, MinidumpMemoryRegion startRegion)
    {
        var moduleStart = module.BaseAddress;
        var moduleEnd = module.BaseAddress + module.Size;

        var regionEnd = startRegion.VirtualAddress + startRegion.Size;
        var capturedEnd = Math.Min(regionEnd, moduleEnd);
        var totalCaptured = capturedEnd - moduleStart;

        var currentVA = regionEnd;
        foreach (var region in MemoryRegions.Where(r => r.VirtualAddress >= regionEnd).OrderBy(r => r.VirtualAddress))
        {
            if (region.VirtualAddress != currentVA) break;
            if (region.VirtualAddress >= moduleEnd) break;

            var regionCapturedEnd = Math.Min(region.VirtualAddress + region.Size, moduleEnd);
            totalCaptured += regionCapturedEnd - region.VirtualAddress;
            currentVA = region.VirtualAddress + region.Size;

            if (currentVA >= moduleEnd) break;
        }

        return totalCaptured;
    }

    /// <summary>
    ///     Generate a diagnostic report of the minidump structure.
    /// </summary>
    public string GenerateDiagnosticReport()
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== Minidump Diagnostic Report ===");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Valid: {IsValid}");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"Architecture: 0x{ProcessorArchitecture:X4} ({(IsXbox360 ? "Xbox 360 / PowerPC" : "Other")})");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Streams: {NumberOfStreams}");
        sb.AppendLine();

        sb.AppendLine(CultureInfo.InvariantCulture, $"=== Modules ({Modules.Count}) ===");
        foreach (var module in Modules.OrderBy(m => m.BaseAddress32))
        {
            var fileRange = GetModuleFileRange(module);
            var fileName = Path.GetFileName(module.Name);
            sb.Append(CultureInfo.InvariantCulture,
                $"  {fileName,-30} Base: 0x{module.BaseAddress32:X8} Size: {module.Size,12:N0}");
            if (fileRange.HasValue)
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $" -> File: 0x{fileRange.Value.fileOffset:X8} ({fileRange.Value.size:N0} bytes captured)");
            else
                sb.AppendLine(" -> NOT IN DUMP");
        }

        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"=== Memory Regions ({MemoryRegions.Count}) ===");
        var totalCaptured = MemoryRegions.Sum(r => r.Size);
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"Total memory captured: {totalCaptured:N0} bytes ({totalCaptured / (1024.0 * 1024.0):F2} MB)");

        var regionsToShow = MemoryRegions.Take(5).Concat(MemoryRegions.TakeLast(5)).Distinct().ToList();
        foreach (var region in regionsToShow.OrderBy(r => r.FileOffset))
        {
            var va32 = (uint)(region.VirtualAddress & 0xFFFFFFFF);
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"  VA: 0x{va32:X8} Size: {region.Size,12:N0} File: 0x{region.FileOffset:X8}");
        }

        if (MemoryRegions.Count > 10)
            sb.AppendLine(CultureInfo.InvariantCulture, $"  ... ({MemoryRegions.Count - 10} more regions) ...");

        return sb.ToString();
    }
}
