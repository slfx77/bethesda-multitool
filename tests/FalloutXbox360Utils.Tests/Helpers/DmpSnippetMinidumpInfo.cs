using FalloutXbox360Utils.Core.Minidump;

namespace FalloutXbox360Utils.Tests.Helpers;

internal sealed class DmpSnippetMinidumpInfo
{
    public bool IsValid { get; init; }
    public ushort ProcessorArchitecture { get; init; }
    public required List<DmpSnippetModule> Modules { get; init; }
    public required List<DmpSnippetMemoryRegion> MemoryRegions { get; init; }

    public MinidumpInfo ToMinidumpInfo()
    {
        return new MinidumpInfo
        {
            IsValid = IsValid,
            ProcessorArchitecture = ProcessorArchitecture,
            Modules = Modules.Select(m => new MinidumpModule
            {
                Name = m.Name,
                BaseAddress = m.BaseAddress,
                Size = m.Size,
                Checksum = m.Checksum,
                TimeDateStamp = m.TimeDateStamp
            }).ToList(),
            MemoryRegions = MemoryRegions.Select(r => new MinidumpMemoryRegion
            {
                VirtualAddress = r.VirtualAddress,
                Size = r.Size,
                FileOffset = r.FileOffset
            }).ToList()
        };
    }
}