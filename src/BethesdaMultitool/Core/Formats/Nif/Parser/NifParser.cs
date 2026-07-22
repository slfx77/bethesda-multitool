using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Nif.Conversion;
using BethesdaMultitool.Core.Formats.Nif.Schema;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Nif.Parser;

/// <summary>
///     Parses NIF file headers and block structures.
/// </summary>
internal static class NifParser
{
    private static readonly Logger Log = Logger.Instance;

    /// <summary>
    ///     Parse NIF file and extract header/block information.
    /// </summary>
    public static NifInfo? Parse(byte[] data)
    {
        if (data.Length < 50)
        {
            return null;
        }

        var info = new NifInfo();
        var pos = ParseHeaderString(data, info);
        if (pos < 0)
        {
            return null;
        }

        // Morrowind / pre-Gamebryo NetImmerse (4.0.0.2): version then Num Blocks directly — no endian
        // byte (< 20.0.0.3), no user version (< 10.0.1.8), no BS stream header, no Block Types table
        // (each block is prefixed by its type name as a SizedString). Handled on a dedicated path.
        if (pos + 4 <= data.Length &&
            BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos)) == 0x04000002)
        {
            return ParseMorrowindNif(data, pos, info);
        }

        pos = ParseVersionInfo(data, pos, info);
        if (pos < 0)
        {
            return null;
        }

        if (!IsBethesdaVersion(info.BinaryVersion,
                info.UserVersion))
        {
            return info; // Return minimal info for non-Bethesda files
        }

        // The BSStreamHeader is version-gated (nif.xml #BSSTREAMHEADER#): 10.0.1.2 has one, but 10.0.1.0
        // does NOT — its Num Block Types table follows Num Blocks directly. Parsing a stream header that
        // isn't there would consume block-type-table bytes as ExportStrings and lose all geometry.
        if (NifVersions.HasBsStreamHeader(info.BinaryVersion, info.UserVersion))
        {
            pos = ParseBethesdaHeader(data, pos, info);
            if (pos < 0)
            {
                return null;
            }
        }

        // This (modern) path locates blocks via the header's per-block "Block Size" array (added in
        // NIF 20.2.0.5) and reads block names from the header String table (added in 20.1.0.1). Older
        // Bethesda NIFs — Oblivion is 20.0.0.4/20.0.0.5 — have neither, so their block boundaries are
        // recovered by field-walking each block (the schema in measure mode). See ParseLegacyBlocks.
        if (info.BinaryVersion < 0x14020005)
        {
            return ParseLegacyBlocks(data, pos, info);
        }

        if (pos + 2 > data.Length)
        {
            return null;
        }

        var numBlockTypes = BinaryUtils.ReadUInt16(data, pos, info.IsBigEndian);
        pos += 2;

        pos = ParseBlockTypeNames(data, pos, numBlockTypes, info);
        if (pos < 0)
        {
            return null;
        }

        if (pos + info.BlockCount * 6 > data.Length)
        {
            return null;
        }

        var (blockTypeIndices, blockSizes) = ParseBlockMetadata(data, pos, info.BlockCount, info.IsBigEndian);
        pos += info.BlockCount * 6; // 2 bytes for type index + 4 bytes for size

        pos = ParseStringTable(data, pos, info.IsBigEndian, info.Strings);
        if (pos < 0)
        {
            return null;
        }

        pos = SkipGroups(data, pos, info.IsBigEndian);
        if (pos < 0)
        {
            return null;
        }

        BuildBlockList(info, blockTypeIndices, blockSizes, pos);
        return info;
    }

    /// <summary>
    ///     Parse a Morrowind-era NetImmerse NIF (4.0.0.2): after the version + Num Blocks, each block is
    ///     written as a SizedString type name followed by the block body (no header Block Types table,
    ///     no Block Size array, no String table). Block byte ranges and inline names are recovered with
    ///     the schema measure walk, exactly like the Oblivion path.
    /// </summary>
    private static NifInfo ParseMorrowindNif(byte[] data, int pos, NifInfo info)
    {
        info.BinaryVersion = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos));
        info.IsBigEndian = false; // Morrowind PC; the Endian-Type byte arrives only at 20.0.0.3.
        info.UserVersion = 0;
        info.BsVersion = 0; // no BS stream header
        info.HasInlineStrings = true; // < 20.1.0.1: names are inline SizedStrings
        pos += 4;

        if (pos + 4 > data.Length)
        {
            return info;
        }

        info.BlockCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos));
        pos += 4;
        if (info.BlockCount is < 0 or > 100000)
        {
            info.BlockCount = 0;
            return info;
        }

        var converter = new NifSchemaConverter(
            NifSchema.LoadEmbedded(), info.BinaryVersion, 0, 0, measure: true);

        for (var i = 0; i < info.BlockCount; i++)
        {
            // Each block is preceded by its type name as a uint32-length-prefixed string.
            if (pos + 4 > data.Length)
            {
                return AbortMorrowindBlocks(info);
            }

            var typeLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos));
            pos += 4;
            if (typeLen is <= 0 or > 256 || pos + typeLen > data.Length)
            {
                return AbortMorrowindBlocks(info);
            }

            var typeName = Encoding.ASCII.GetString(data, pos, typeLen);
            pos += typeLen;

            var (size, name) = converter.MeasureBlock(data, pos, data.Length, typeName);
            if (size < 0)
            {
                Log.Debug($"NIF Morrowind measure: cannot size block {i} ('{typeName}'); aborting block list.");
                return AbortMorrowindBlocks(info);
            }

            info.Blocks.Add(new BlockInfo
            {
                Index = i,
                TypeIndex = 0,
                TypeName = typeName,
                Size = size,
                DataOffset = pos
            });

            if (name != null)
            {
                info.BlockNames[i] = name;
            }

            pos += size;
        }

        return info;
    }

    private static NifInfo AbortMorrowindBlocks(NifInfo info)
    {
        info.Blocks.Clear();
        info.BlockNames.Clear();
        return info;
    }

    /// <summary>
    ///     Parse the block table for pre-20.2.0.5 NIFs (Oblivion 20.0.0.x): the header still has the
    ///     Block Types table + per-block Type Index (since 5.0.0.1) but NO Block Size array and NO
    ///     String table. Block byte ranges are recovered by field-walking each block with the schema
    ///     in measure mode; object names are read inline. NIFs older than 5.0.0.1 (Morrowind 4.0.0.2,
    ///     which has neither a Block Types table nor a BS stream header) are returned header-only here
    ///     and handled by the dedicated legacy-Gamebryo path.
    /// </summary>
    private static NifInfo? ParseLegacyBlocks(byte[] data, int pos, NifInfo info)
    {
        info.HasInlineStrings = info.BinaryVersion < 0x14010001;

        if (info.BinaryVersion < 0x05000001)
        {
            return info; // No Block Types table — not handled on this path.
        }

        if (pos + 2 > data.Length)
        {
            return info;
        }

        var numBlockTypes = BinaryUtils.ReadUInt16(data, pos, info.IsBigEndian);
        pos += 2;

        pos = ParseBlockTypeNames(data, pos, numBlockTypes, info);
        if (pos < 0)
        {
            return null;
        }

        // Per-block Type Index — but no Block Size array (that arrives at 20.2.0.5).
        var blockTypeIndices = new ushort[info.BlockCount];
        for (var i = 0; i < info.BlockCount; i++)
        {
            if (pos + 2 > data.Length)
            {
                return info;
            }

            blockTypeIndices[i] = BinaryUtils.ReadUInt16(data, pos, info.IsBigEndian);
            pos += 2;
        }

        // No String table (< 20.1.0.1). Groups follow (since 5.0.0.6; present in Oblivion), then data.
        pos = SkipGroups(data, pos, info.IsBigEndian);
        if (pos < 0)
        {
            return info; // truncated header — header-only, matching this path's other truncation exits
        }

        MeasureLegacyBlocks(data, pos, info, blockTypeIndices);
        return info;
    }

    /// <summary>
    ///     Field-walk every block (schema measure mode) to recover its byte offset/size and capture its
    ///     inline NiObjectNET.Name. With no per-block size array, one mis-sized block desyncs all that
    ///     follow — so an unsizable (schema-unknown) block type aborts the block list rather than
    ///     emitting wrong offsets (the file then renders as "no geometry", but never crashes).
    /// </summary>
    private static void MeasureLegacyBlocks(byte[] data, int dataStart, NifInfo info, ushort[] blockTypeIndices)
    {
        var converter = new NifSchemaConverter(
            NifSchema.LoadEmbedded(),
            info.BinaryVersion,
            (int)info.UserVersion,
            (int)info.BsVersion,
            measure: true);

        // Pre-10.2.0.0 Gamebryo/Bethesda-stream NIFs (Oblivion's bsVersion 4/5 architecture, e.g.
        // fort-ruins 10.1.0.106 rfcastlearchfront / rfcastleruinswall3way) precede EVERY data block with
        // a 4-byte word (zero in all sampled meshes) that nif.xml does not model — verified by decoding:
        // each NiObjectNET's Name SizedString sits 4 bytes past where the contiguous block layout expects
        // it. 10.2.0.0 / 20.0.0.x have no such per-block word. Skipping it advances DataOffset to the real
        // block start so the field-walk (and the render readers, which key off DataOffset) line up.
        var hasPerBlockWord = NifVersions.HasPerBlockLegacyWord(info.BinaryVersion);

        var pos = dataStart;
        for (var i = 0; i < info.BlockCount; i++)
        {
            var typeName = blockTypeIndices[i] < info.BlockTypeNames.Count
                ? info.BlockTypeNames[blockTypeIndices[i]]
                : "Unknown";

            if (hasPerBlockWord)
            {
                pos += 4;
            }

            if (pos > data.Length)
            {
                Log.Debug($"NIF legacy measure: block {i} ('{typeName}') starts past EOF; aborting block list.");
                info.Blocks.Clear();
                info.BlockNames.Clear();
                return;
            }

            var (size, name) = converter.MeasureBlock(data, pos, data.Length, typeName);
            if (size < 0)
            {
                Log.Debug($"NIF legacy measure: cannot size block {i} ('{typeName}'); aborting block list.");
                info.Blocks.Clear();
                info.BlockNames.Clear();
                return;
            }

            info.Blocks.Add(new BlockInfo
            {
                Index = i,
                TypeIndex = blockTypeIndices[i],
                TypeName = typeName,
                Size = size,
                DataOffset = pos
            });

            if (name != null)
            {
                info.BlockNames[i] = name;
            }

            pos += size;
        }
    }

    /// <summary>The header version identity of a NIF — read without parsing blocks. See
    /// <see cref="ProbeVersionInfo" />. <c>BsVersion</c> is the BSStreamHeader's BS Version (0 for
    /// NetImmerse/Morrowind and non-Bethesda Gamebryo, which have no stream header).</summary>
    public readonly record struct NifVersionProbe(
        string HeaderString, uint BinaryVersion, uint UserVersion, uint BsVersion, bool IsBigEndian);

    /// <summary>
    ///     Reads only a NIF's header version identity — binary version, user version, BS stream version —
    ///     without the (relatively expensive) block list / string-table / measure walk. Returns null when
    ///     the header can't be read (too short / no newline). Used by the version-census tooling to tally
    ///     NIF versions across an archive cheaply. Mirrors the field layout of <see cref="ParseVersionInfo" />
    ///     + <see cref="ParseBethesdaHeader" /> (Morrowind 4.0.0.2 has version then Num Blocks directly).
    /// </summary>
    public static NifVersionProbe? ProbeVersionInfo(byte[] data)
    {
        if (data.Length < 50)
        {
            return null;
        }

        var newlinePos = Array.IndexOf(data, (byte)0x0A, 0, Math.Min(60, data.Length));
        if (newlinePos < 0)
        {
            return null;
        }

        var headerString = Encoding.ASCII.GetString(data, 0, newlinePos);
        var pos = newlinePos + 1;
        if (pos + 4 > data.Length)
        {
            return null;
        }

        var version = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos));
        pos += 4;

        // Morrowind / pre-Gamebryo NetImmerse (4.0.0.2): version then Num Blocks directly — no endian
        // byte, no user version, no BS stream header.
        if (version == 0x04000002)
        {
            return new NifVersionProbe(headerString, version, 0, 0, false);
        }

        var bigEndian = false;
        if (version >= 0x14000003) // Endian-Type byte added at 20.0.0.3
        {
            if (pos >= data.Length)
            {
                return new NifVersionProbe(headerString, version, 0, 0, false);
            }

            bigEndian = data[pos] == 0;
            pos += 1;
        }

        // User Version (since 10.0.1.8) then Num Blocks. 10.0.1.0 / 10.0.1.2 have no User Version, so Num
        // Blocks follows Version directly — reading one there would mis-tally the version.
        var userVersion = 0u;
        if (NifVersions.HasUserVersion(version))
        {
            if (pos + 4 > data.Length)
            {
                return new NifVersionProbe(headerString, version, 0, 0, bigEndian);
            }

            userVersion = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos));
            pos += 4;
        }

        pos += 4; // Num Blocks (skip)

        // BS Version — the first field of the BSStreamHeader, present only when nif.xml's #BSSTREAMHEADER#
        // gate holds (e.g. 10.0.1.2 yes, 10.0.1.0 no). Where there's no stream header the next bytes are
        // the Block Types table, so reading them as a BS version would be meaningless.
        var bsVersion = NifVersions.HasBsStreamHeader(version, userVersion) && pos + 4 <= data.Length
            ? BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos))
            : 0u;

        return new NifVersionProbe(headerString, version, userVersion, bsVersion, bigEndian);
    }

    /// <summary>
    ///     Cheaply determines a NIF's endianness without a full parse (no block list, string table,
    ///     or group walk). Returns the same value <see cref="Parse" /> would compute for
    ///     <see cref="NifInfo.IsBigEndian" /> — it reads the exact same header string + Endian-Type
    ///     byte — or <c>null</c> when the header can't be read cheaply (too short / no newline / the
    ///     endian byte is past the end), in which case the caller should fall back to a full parse.
    ///     Lets the mesh-decode path skip parsing the source NIF just to decide whether it needs
    ///     endian conversion.
    /// </summary>
    public static bool? TryProbeEndianness(byte[] data)
    {
        if (data.Length < 50)
        {
            return null;
        }

        var newlinePos = Array.IndexOf(data, (byte)0x0A, 0, Math.Min(60, data.Length));
        if (newlinePos < 0)
        {
            return null;
        }

        // ParseVersionInfo reads IsBigEndian as the byte 4 past the header string (the version u32
        // occupies the first 4). Mirror that exact offset (newlinePos + 1 is the version start).
        var endianBytePos = newlinePos + 1 + 4;
        if (endianBytePos >= data.Length)
        {
            return null;
        }

        return data[endianBytePos] == 0;
    }

    private static int ParseHeaderString(byte[] data, NifInfo info)
    {
        var newlinePos = Array.IndexOf(data, (byte)0x0A, 0, Math.Min(60, data.Length));
        if (newlinePos < 0)
        {
            return -1;
        }

        info.HeaderString = Encoding.ASCII.GetString(data, 0, newlinePos);
        return newlinePos + 1;
    }

    /// <summary>Returns the position past the version fields, or -1 when the header is truncated
    /// or the block count is implausible (the caller maps -1 to a null parse result).</summary>
    private static int ParseVersionInfo(byte[] data, int pos, NifInfo info)
    {
        if (pos + 4 > data.Length)
        {
            return -1;
        }

        info.BinaryVersion = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos));
        pos += 4;

        // The Endian-Type byte was added in 20.0.0.3. Older Gamebryo NIFs — e.g. Oblivion's
        // version-10.2.0.0 architecture meshes (ICPalaceTower01) — don't have it, so reading it would
        // shift User Version / Num Blocks / BS version down by one byte and desync the whole header
        // (garbage block count → "no renderable geometry").
        if (info.BinaryVersion >= 0x14000003)
        {
            if (pos >= data.Length)
            {
                return -1;
            }

            info.IsBigEndian = data[pos] == 0;
            pos += 1;
        }
        else
        {
            info.IsBigEndian = false; // PC little-endian; no endian byte at this version
        }

        // User Version was added at 10.0.1.8. The oldest Oblivion Gamebryo meshes — 10.0.1.0 / 10.0.1.2
        // (groundcover, fort tiles such as rf1xhousingtiles) — predate it: Num Blocks follows Version
        // directly, so reading a User Version there would shift Num Blocks (and everything after) by 4.
        if (NifVersions.HasUserVersion(info.BinaryVersion))
        {
            if (pos + 4 > data.Length)
            {
                return -1;
            }

            info.UserVersion = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos));
            pos += 4;
        }
        else
        {
            info.UserVersion = 0;
        }

        if (pos + 4 > data.Length)
        {
            return -1;
        }

        info.BlockCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos));
        pos += 4;
        if (info.BlockCount is < 0 or > 100000) // same cap as the Morrowind path
        {
            return -1;
        }

        return pos;
    }

    private static int ParseBethesdaHeader(byte[] data, int pos, NifInfo info)
    {
        if (pos < 0 || pos + 4 > data.Length)
        {
            return -1;
        }

        var bsVersion = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos));
        info.BsVersion = bsVersion;
        pos += 4;

        // BSStreamHeader, per nif.xml — the export-metadata fields are version-gated, NOT a fixed set
        // of three ShortStrings. Getting the count wrong (e.g. FO4's Max Filepath, FO76's Unknown Int)
        // desyncs the block-types table that follows, so no geometry is found. An ExportString is a
        // 1-byte length prefix (including the null terminator) followed by that many bytes.
        pos = SkipExportString(data, pos);          // Author (all Bethesda)
        if (bsVersion > 130)
        {
            pos += 4;                               // Unknown Int (uint): FO4 131+, FO76, Starfield
        }

        if (bsVersion < 131)
        {
            pos = SkipExportString(data, pos);      // Process Script: Skyrim SE, FO4 130
        }

        pos = SkipExportString(data, pos);          // Export Script (all Bethesda)
        if (bsVersion is >= 103 and < 170)
        {
            pos = SkipExportString(data, pos);      // Max Filepath: FO4, FO76
        }

        // bsVersion >= 170 (Starfield) adds an "Unknown Data" (ExportDataSF) block here — not handled.
        return pos;
    }

    /// <summary>Advances past one BSStreamHeader ExportString (1-byte length incl. terminator + bytes).</summary>
    private static int SkipExportString(byte[] data, int pos)
        => pos >= 0 && pos < data.Length ? pos + 1 + data[pos] : pos;

    private static int ParseBlockTypeNames(byte[] data, int pos, int numBlockTypes, NifInfo info)
    {
        for (var i = 0; i < numBlockTypes; i++)
        {
            if (pos + 4 > data.Length)
            {
                return -1;
            }

            var strLen = BinaryUtils.ReadUInt32(data, pos, info.IsBigEndian);
            pos += 4;

            if (strLen > 256 || pos + strLen > data.Length)
            {
                return -1;
            }

            info.BlockTypeNames.Add(Encoding.ASCII.GetString(data, pos, (int)strLen));
            pos += (int)strLen;
        }

        return pos;
    }

    private static (ushort[] typeIndices, uint[] sizes) ParseBlockMetadata(
        byte[] data, int pos, int numBlocks, bool isBigEndian)
    {
        var blockTypeIndices = new ushort[numBlocks];
        for (var i = 0; i < numBlocks; i++)
        {
            blockTypeIndices[i] = BinaryUtils.ReadUInt16(data, pos + i * 2, isBigEndian);
        }

        var sizePos = pos + numBlocks * 2;
        var blockSizes = new uint[numBlocks];
        for (var i = 0; i < numBlocks; i++)
        {
            blockSizes[i] = BinaryUtils.ReadUInt32(data, sizePos + i * 4, isBigEndian);
        }

        return (blockTypeIndices, blockSizes);
    }

    /// <summary>Returns the position past the group list, or -1 when the count dword is missing or
    /// the declared groups run past EOF (callers treat -1 as a truncated header).</summary>
    private static int SkipGroups(byte[] data, int pos, bool isBigEndian)
    {
        if (pos < 0 || pos + 4 > data.Length)
        {
            return -1;
        }

        var numGroups = BinaryUtils.ReadUInt32(data, pos, isBigEndian);
        pos += 4;
        return numGroups <= (uint)((data.Length - pos) / 4) ? pos + (int)numGroups * 4 : -1;
    }

    private static void BuildBlockList(NifInfo info, ushort[] blockTypeIndices, uint[] blockSizes, int dataStart)
    {
        var pos = dataStart;
        for (var i = 0; i < info.BlockCount; i++)
        {
            info.Blocks.Add(new BlockInfo
            {
                Index = i,
                TypeIndex = blockTypeIndices[i],
                TypeName = blockTypeIndices[i] < info.BlockTypeNames.Count
                    ? info.BlockTypeNames[blockTypeIndices[i]]
                    : "Unknown",
                Size = (int)blockSizes[i],
                DataOffset = pos
            });
            pos += (int)blockSizes[i];
        }
    }

    /// <summary>Returns the position past the string table, or -1 when the declared counts/lengths
    /// don't fit the remaining bytes (the caller maps -1 to a null parse result).</summary>
    private static int ParseStringTable(byte[] data, int pos, bool isBigEndian, List<string> strings)
    {
        if (pos < 0 || pos + 8 > data.Length)
        {
            return -1;
        }

        // Num strings
        var numStrings = isBigEndian
            ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos))
            : BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos));
        pos += 4;

        // Max string length (skip)
        pos += 4;

        // Each string needs at least its 4-byte length prefix, so a count beyond remaining/4 is a lie.
        if (numStrings > (uint)((data.Length - pos) / 4))
        {
            return -1;
        }

        // Strings
        for (var i = 0; i < numStrings; i++)
        {
            if (pos + 4 > data.Length)
            {
                return -1;
            }

            var strLen = isBigEndian
                ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos))
                : BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos));
            pos += 4;

            // Header strings can legitimately be long: retail UPB (User Property Buffer) entries
            // carry multi-line Havok property dumps (e.g. nv_gs-saloon-sign.nif declares 265 bytes).
            // The EOF bounds check is the crash protection; the cap only rejects absurd lies.
            if (strLen > 65536 || pos + strLen > data.Length)
            {
                return -1;
            }

            var str = Encoding.ASCII.GetString(data, pos, (int)strLen);
            strings.Add(str);
            pos += (int)strLen;
        }

        return pos;
    }

    public static bool IsBethesdaVersion(uint binaryVersion, uint userVersion)
    {
        return binaryVersion switch
        {
            // FO3 / FNV / Skyrim / Fallout 4 / Starfield (NIF 20.2.0.7).
            0x14020007 => userVersion is 11 or 12,
            // Oblivion (NIF 20.0.0.4 / 20.0.0.5). Stock meshes ship with BOTH User Version 11 (most)
            // and User Version 10 (a large minority — e.g. clutter authored on an older exporter). The
            // header/legacy-block layout is identical for both, so rejecting 10 dropped ~a third of
            // Oblivion meshes to "no geometry".
            0x14000004 or 0x14000005 => userVersion is 10 or 11,
            // Oblivion also ships some architecture as older-exporter Gamebryo 10.2.0.0 (e.g.
            // ICPalaceTower01) — same legacy field-walked block layout, BSStreamHeader, user version 10/11.
            0x0A020000 => userVersion is 10 or 11,
            // ...and some as even older Gamebryo 10.1.0.101 (bsVersion 4) / 10.1.0.106 (bsVersion 5) —
            // e.g. fort-ruins rfcastlearchfront / rfcastleruinswall3way. The header is the legacy
            // field-walked form (no size array, no string table, inline names), but these pre-10.2.0.0
            // streams also (a) precede every data block with a 4-byte word and (b) omit the
            // NiGeometryData Group ID (added at 10.1.0.114) — both handled in MeasureLegacyBlocks and the
            // geometry readers. Rejecting them dropped those pieces to "no geometry".
            0x0A010065 or 0x0A01006A => userVersion is 10 or 11,
            // The oldest Gamebryo architecture Oblivion ships: NetImmerse-headed 10.0.1.0 (e.g. fort
            // tile rf1xhousingtiles, darkelf ears) and 10.0.1.2 (groundcover plants). These predate the
            // Header User Version field (added 10.0.1.8), so UserVersion is always 0; 10.0.1.0 also has
            // NO BSStreamHeader while 10.0.1.2 does (both handled at parse time). The block body is the
            // same legacy field-walked form as 10.1.0.10x (per-block word, no Group ID).
            0x0A000100 or 0x0A000102 => userVersion == 0,
            _ => false
        };
    }
}
