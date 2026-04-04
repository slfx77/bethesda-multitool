using System.IO.MemoryMappedFiles;
using System.Text;
using FalloutXbox360Utils.Core.Minidump;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Records;

/// <summary>
///     Parses PE section headers from Xbox 360 module images loaded in memory dumps.
///     Used by <see cref="EsmEditorIdExtractor" /> for hash table detection and by
///     coverage analysis for section enumeration.
/// </summary>
internal static class EsmEditorIdPeParser
{
    /// <summary>
    ///     Enumerate all PE sections from a module's in-memory PE headers.
    ///     PE headers use little-endian format (standard PE convention), even on Xbox 360.
    /// </summary>
    internal static List<PeSectionInfo>? EnumeratePeSections(
        MemoryMappedViewAccessor accessor,
        long fileSize,
        MinidumpInfo minidumpInfo,
        MinidumpModule module)
    {
        var baseFileOffset = minidumpInfo.VirtualAddressToFileOffset(module.BaseAddress);
        if (!baseFileOffset.HasValue || baseFileOffset.Value + 0x40 > fileSize)
        {
            return null;
        }

        var dosHeader = new byte[64];
        accessor.ReadArray(baseFileOffset.Value, dosHeader, 0, 64);

        if (dosHeader[0] != 0x4D || dosHeader[1] != 0x5A) // "MZ"
        {
            return null;
        }

        var eLfanew = BinaryUtils.ReadUInt32LE(dosHeader, 0x3C);
        if (eLfanew > 0x10000)
        {
            return null;
        }

        var peOffset = baseFileOffset.Value + eLfanew;
        if (peOffset + 24 > fileSize)
        {
            return null;
        }

        var peHeader = new byte[24];
        accessor.ReadArray(peOffset, peHeader, 0, 24);

        if (peHeader[0] != 0x50 || peHeader[1] != 0x45 || peHeader[2] != 0 || peHeader[3] != 0)
        {
            return null;
        }

        var numberOfSections = BinaryUtils.ReadUInt16LE(peHeader, 6);
        var sizeOfOptionalHeader = BinaryUtils.ReadUInt16LE(peHeader, 20);

        var sectionTableOffset = peOffset + 24 + sizeOfOptionalHeader;
        var sections = new List<PeSectionInfo>(numberOfSections);

        for (var i = 0; i < numberOfSections; i++)
        {
            var sectionOffset = sectionTableOffset + i * 40;
            if (sectionOffset + 40 > fileSize)
            {
                break;
            }

            var sectionHeader = new byte[40];
            accessor.ReadArray(sectionOffset, sectionHeader, 0, 40);

            var name = Encoding.ASCII.GetString(sectionHeader, 0, 8).TrimEnd('\0');
            var virtualSize = BinaryUtils.ReadUInt32LE(sectionHeader, 8);
            var virtualAddress = BinaryUtils.ReadUInt32LE(sectionHeader, 12);
            var characteristics = BinaryUtils.ReadUInt32LE(sectionHeader, 36);

            sections.Add(new PeSectionInfo(i, name, virtualAddress, virtualSize, characteristics));
        }

        return sections;
    }
}
