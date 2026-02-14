using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Frozen;
using System.IO.MemoryMappedFiles;
using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm;

/// <summary>
///     Scans memory dumps for ESM record headers and subrecords.
///     Supports both PC (little-endian) and Xbox 360 (big-endian) formats.
/// </summary>
internal static class EsmRecordScanner
{
    #region Constants and Static Fields

    private const uint SigEdid = 0x44494445;
    private const uint SigGmst = 0x54534D47;
    private const uint SigSctx = 0x58544353;
    private const uint SigScro = 0x4F524353;
    private const uint SigName = 0x454D414E;
    private const uint SigData = 0x41544144;
    private const uint SigAcbs = 0x53424341;
    private const uint SigNam1 = 0x314D414E;
    private const uint SigTrdt = 0x54445254;
    private const uint SigFull = 0x4C4C5546;
    private const uint SigDesc = 0x43534544;
    private const uint SigModl = 0x4C444F4D;
    private const uint SigIcon = 0x4E4F4349;
    private const uint SigMico = 0x4F43494D;
    private const uint SigScri = 0x49524353;
    private const uint SigEnam = 0x4D414E45;
    private const uint SigSnam = 0x4D414E53;
    private const uint SigQnam = 0x4D414E51;
    private const uint SigCtda = 0x41445443;
    private const uint SigVhgt = 0x54474856;
    private const uint SigTghv = 0x56484754;
    private const uint SigXclc = 0x434C4358;
    private const uint SigClcx = 0x58434C43;

    private static readonly string[] RuntimeRecordTypes =
    [
        // Placed objects (most common in runtime memory)
        "REFR", // Placed Object
        "ACHR", // Placed NPC
        "ACRE", // Placed Creature
        "PMIS", // Placed Missile
        "PGRE", // Placed Grenade

        // Actor definitions (loaded for nearby NPCs)
        "NPC_", // NPC
        "CREA", // Creature

        // Item definitions (player inventory, nearby loot)
        "WEAP", // Weapon
        "ARMO", // Armor
        "AMMO", // Ammo
        "ALCH", // Ingestible
        "MISC", // Misc Item
        "NOTE", // Note
        "KEYM", // Key
        "BOOK", // Book
        "CONT", // Container
        "DOOR", // Door
        "LIGH", // Light
        "STAT", // Static
        "TERM", // Terminal
        "FURN", // Furniture

        // World structure
        "CELL", // Cell
        "WRLD", // Worldspace
        "LAND", // Landscape
        "NAVM", // Navigation Mesh
        "NAVI", // Navigation Mesh Info Map
        "PGRD", // Pathgrid (legacy)

        // Quest/Dialog (active gameplay)
        "QUST", // Quest
        "DIAL", // Dialog Topic
        "INFO", // Dialog Response
        "PACK", // AI Package

        // Scripts
        "SCPT", // Script

        // Effects and sounds
        "MGEF", // Magic Effect
        "ENCH", // Enchantment
        "SPEL", // Actor Effect
        "SOUN", // Sound
        "MUSC", // Music Type

        // Factions and relationships
        "FACT", // Faction
        "RACE", // Race
        "CLAS", // Class

        // Leveled lists
        "LVLI", // Leveled Item
        "LVLN", // Leveled NPC
        "LVLC", // Leveled Creature

        // Game settings and globals
        "GMST", // Game Setting
        "GLOB", // Global Variable
        "WTHR", // Weather
        "CLMT", // Climate
        "REGN", // Region
        "IMAD", // Image Space Adapter
        "IMGS", // Image Space

        // FNV-specific gameplay records
        "IMOD", // Weapon Mod
        "RCPE", // Recipe
        "CHAL", // Challenge
        "REPU", // Reputation
        "PROJ", // Projectile
        "EXPL", // Explosion
        "MESG", // Message

        // Perks (already reconstructed, ensure scanned)
        "PERK" // Perk
    ];

    private static readonly HashSet<uint> RuntimeRecordMagicLE = BuildMagicSet(false);
    private static readonly HashSet<uint> RuntimeRecordMagicBE = BuildMagicSet(true);

    // GRUP magic values (structural container, not a main record — separate validation)
    private const uint SigGrupLE = 0x50555247; // "GRUP" as LE uint32
    private const uint SigGrupBE = 0x47525550; // "GRUP" as BE uint32

    private static readonly string[] KnownFalsePositivePatterns =
    [
        "VGT_", // GPU Vertex Grouper/Tessellator debug (VGT_DEBUG_*)
        "SX_D", // Shader export debug
        "SQ_D", // Sequencer debug
        "DB_D", // Depth buffer debug
        "CB_D", // Color buffer debug
        "PA_D", // Primitive Assembly debug
        "PA_S", // Primitive Assembly state
        "SC_D", // Scan converter debug
        "SPI_", // Shader Processor Interpolator
        "TA_D", // Texture Addressing debug
        "TD_D", // Texture Data debug
        "TCP_", // Texture Cache Pipe
        "TCA_" // Texture Cache Address
    ];

    private readonly struct ScanDedup(
        HashSet<string> seenEdids,
        HashSet<uint> seenFormIds,
        HashSet<long> seenMainRecordOffsets)
    {
        public readonly HashSet<string> SeenEdids = seenEdids;
        public readonly HashSet<uint> SeenFormIds = seenFormIds;
        public readonly HashSet<long> SeenMainRecordOffsets = seenMainRecordOffsets;
    }

    private delegate void SubrecordHandler(
        byte[] buffer, int index, int length, long offset,
        EsmRecordScanResult result, HashSet<string> seenEdids, HashSet<uint> seenFormIds);

    private static readonly FrozenDictionary<uint, SubrecordHandler> SubrecordDispatch =
        new Dictionary<uint, SubrecordHandler>
        {
            [SigEdid] = (buf, i, len, off, res, edids, _) =>
                EsmMiscDetector.TryAddEdidRecordWithOffset(buf, i, len, off, res.EditorIds, edids),
            [SigGmst] = (buf, i, len, off, res, _, _) =>
                EsmMiscDetector.TryAddGmstRecordWithOffset(buf, i, len, off, res.GameSettings),
            [SigSctx] = (buf, i, len, off, res, _, _) =>
                EsmMiscDetector.TryAddSctxRecordWithOffset(buf, i, len, off, res.ScriptSources),
            [SigScro] = (buf, i, len, off, res, _, fids) =>
                EsmMiscDetector.TryAddScroRecordWithOffset(buf, i, len, off, res.FormIdReferences, fids),
            [SigName] = (buf, i, len, off, res, _, _) =>
                EsmMiscDetector.TryAddNameSubrecordWithOffset(buf, i, len, off, res.NameReferences),
            [SigData] = (buf, i, len, off, res, _, _) =>
                EsmWorldExtractor.TryAddPositionSubrecordWithOffset(buf, i, len, off, res.Positions),
            [SigAcbs] = (buf, i, len, off, res, _, _) =>
                EsmActorDetector.TryAddActorBaseSubrecordWithOffset(buf, i, len, off, res.ActorBases),
            [SigNam1] = (buf, i, len, off, res, _, _) =>
                EsmDialogueDetector.TryAddResponseTextSubrecordWithOffset(buf, i, len, off, res.ResponseTexts),
            [SigTrdt] = (buf, i, len, off, res, _, _) =>
                EsmDialogueDetector.TryAddResponseDataSubrecordWithOffset(buf, i, len, off, res.ResponseData),
            [SigFull] = (buf, i, len, off, res, _, _) =>
                EsmMiscDetector.TryAddTextSubrecordWithOffset(buf, i, len, off, "FULL", res.FullNames),
            [SigDesc] = (buf, i, len, off, res, _, _) =>
                EsmMiscDetector.TryAddTextSubrecordWithOffset(buf, i, len, off, "DESC", res.Descriptions),
            [SigModl] = (buf, i, len, off, res, _, _) =>
                EsmMiscDetector.TryAddPathSubrecordWithOffset(buf, i, len, off, "MODL", res.ModelPaths),
            [SigIcon] = (buf, i, len, off, res, _, _) =>
                EsmMiscDetector.TryAddPathSubrecordWithOffset(buf, i, len, off, "ICON", res.IconPaths),
            [SigMico] = (buf, i, len, off, res, _, _) =>
                EsmMiscDetector.TryAddPathSubrecordWithOffset(buf, i, len, off, "MICO", res.IconPaths),
            [SigScri] = (buf, i, len, off, res, _, _) =>
                EsmMiscDetector.TryAddFormIdSubrecordWithOffset(buf, i, len, off, "SCRI", res.ScriptRefs),
            [SigEnam] = (buf, i, len, off, res, _, _) =>
                EsmMiscDetector.TryAddFormIdSubrecordWithOffset(buf, i, len, off, "ENAM", res.EffectRefs),
            [SigSnam] = (buf, i, len, off, res, _, _) =>
                EsmMiscDetector.TryAddFormIdSubrecordWithOffset(buf, i, len, off, "SNAM", res.SoundRefs),
            [SigQnam] = (buf, i, len, off, res, _, _) =>
                EsmMiscDetector.TryAddFormIdSubrecordWithOffset(buf, i, len, off, "QNAM", res.QuestRefs),
            [SigCtda] = (buf, i, len, off, res, _, _) =>
                EsmActorDetector.TryAddConditionSubrecordWithOffset(buf, i, len, off, res.Conditions),
            [SigVhgt] = (buf, i, len, off, res, _, _) =>
                EsmWorldExtractor.TryAddVhgtHeightmapWithOffset(buf, i, len, off, false, res.Heightmaps),
            [SigTghv] = (buf, i, len, off, res, _, _) =>
                EsmWorldExtractor.TryAddVhgtHeightmapWithOffset(buf, i, len, off, true, res.Heightmaps),
            [SigXclc] = (buf, i, len, off, res, _, _) =>
                EsmWorldExtractor.TryAddXclcSubrecordWithOffset(buf, i, len, off, false, res.CellGrids),
            [SigClcx] = (buf, i, len, off, res, _, _) =>
                EsmWorldExtractor.TryAddXclcSubrecordWithOffset(buf, i, len, off, true, res.CellGrids),
        }.ToFrozenDictionary();

    #endregion

    #region Record Scanning

    public static EsmRecordScanResult ScanForRecords(byte[] data)
    {
        var result = new EsmRecordScanResult();
        var seenEdids = new HashSet<string>();
        var seenFormIds = new HashSet<uint>();
        var seenMainRecordOffsets = new HashSet<long>();

        for (var i = 0; i <= data.Length - 24; i++)
        {
            // Check for main record headers first (24 bytes minimum)
            TryAddMainRecordHeader(data, i, data.Length, result.MainRecords, seenMainRecordOffsets);

            // Then check for subrecords
            if (MatchesSignature(data, i, "EDID"u8))
            {
                EsmMiscDetector.TryAddEdidRecord(data, i, data.Length, result.EditorIds, seenEdids);
            }
            else if (MatchesSignature(data, i, "GMST"u8))
            {
                EsmMiscDetector.TryAddGmstRecord(data, i, data.Length, result.GameSettings);
            }
            else if (MatchesSignature(data, i, "SCTX"u8))
            {
                EsmMiscDetector.TryAddSctxRecord(data, i, data.Length, result.ScriptSources);
            }
            else if (MatchesSignature(data, i, "SCRO"u8))
            {
                EsmMiscDetector.TryAddScroRecord(data, i, data.Length, result.FormIdReferences, seenFormIds);
            }
            else if (MatchesSignature(data, i, "NAME"u8))
            {
                EsmMiscDetector.TryAddNameSubrecord(data, i, data.Length, result.NameReferences);
            }
            else if (MatchesSignature(data, i, "DATA"u8))
            {
                EsmWorldExtractor.TryAddPositionSubrecord(data, i, data.Length, result.Positions);
            }
            else if (MatchesSignature(data, i, "ACBS"u8))
            {
                EsmActorDetector.TryAddActorBaseSubrecord(data, i, data.Length, result.ActorBases);
            }
            else if (MatchesSignature(data, i, "NAM1"u8))
            {
                EsmDialogueDetector.TryAddResponseTextSubrecord(data, i, data.Length, result.ResponseTexts);
            }
            else if (MatchesSignature(data, i, "TRDT"u8))
            {
                EsmDialogueDetector.TryAddResponseDataSubrecord(data, i, data.Length, result.ResponseData);
            }
            // Text-containing subrecords
            else if (MatchesSignature(data, i, "FULL"u8))
            {
                EsmMiscDetector.TryAddTextSubrecord(data, i, data.Length, "FULL", result.FullNames);
            }
            else if (MatchesSignature(data, i, "DESC"u8))
            {
                EsmMiscDetector.TryAddTextSubrecord(data, i, data.Length, "DESC", result.Descriptions);
            }
            else if (MatchesSignature(data, i, "MODL"u8))
            {
                EsmMiscDetector.TryAddPathSubrecord(data, i, data.Length, "MODL", result.ModelPaths);
            }
            else if (MatchesSignature(data, i, "ICON"u8))
            {
                EsmMiscDetector.TryAddPathSubrecord(data, i, data.Length, "ICON", result.IconPaths);
            }
            else if (MatchesSignature(data, i, "MICO"u8))
            {
                EsmMiscDetector.TryAddPathSubrecord(data, i, data.Length, "MICO", result.IconPaths);
            }
            // Texture set paths (TX00-TX07)
            else if (MatchesTextureSignature(data, i))
            {
                var sig = Encoding.ASCII.GetString(data, i, 4);
                EsmMiscDetector.TryAddPathSubrecord(data, i, data.Length, sig, result.TexturePaths);
            }
            // FormID reference subrecords
            else if (MatchesSignature(data, i, "SCRI"u8))
            {
                EsmMiscDetector.TryAddFormIdSubrecord(data, i, data.Length, "SCRI", result.ScriptRefs);
            }
            else if (MatchesSignature(data, i, "ENAM"u8))
            {
                EsmMiscDetector.TryAddFormIdSubrecord(data, i, data.Length, "ENAM", result.EffectRefs);
            }
            else if (MatchesSignature(data, i, "SNAM"u8))
            {
                EsmMiscDetector.TryAddFormIdSubrecord(data, i, data.Length, "SNAM", result.SoundRefs);
            }
            else if (MatchesSignature(data, i, "QNAM"u8))
            {
                EsmMiscDetector.TryAddFormIdSubrecord(data, i, data.Length, "QNAM", result.QuestRefs);
            }
            // Condition data
            else if (MatchesSignature(data, i, "CTDA"u8))
            {
                EsmActorDetector.TryAddConditionSubrecord(data, i, data.Length, result.Conditions);
            }
        }

        return result;
    }

    /// <summary>
    ///     Scan an entire memory dump for ESM records using memory-mapped access.
    ///     Processes in chunks to avoid loading the entire file into memory.
    /// </summary>
    public static EsmRecordScanResult ScanForRecordsMemoryMapped(
        MemoryMappedViewAccessor accessor,
        long fileSize,
        List<(long start, long end)>? excludeRanges = null,
        IProgress<(long bytesProcessed, long totalBytes, int recordsFound)>? progress = null)
    {
        const int chunkSize = 16 * 1024 * 1024; // 16MB chunks
        const int overlapSize = 1024; // Overlap to handle records at chunk boundaries

        var result = new EsmRecordScanResult();
        var dedup = new ScanDedup(new HashSet<string>(), new HashSet<uint>(), new HashSet<long>());
        var buffer = ArrayPool<byte>.Shared.Rent(chunkSize + overlapSize);

        try
        {
            long offset = 0;
            while (offset < fileSize)
            {
                var toRead = (int)Math.Min(chunkSize + overlapSize, fileSize - offset);
                progress?.Report((offset, fileSize, result.MainRecords.Count));
                accessor.ReadArray(offset, buffer, 0, toRead);

                // Only search up to chunkSize unless this is the last chunk
                var searchLimit = offset + chunkSize >= fileSize ? toRead - 24 : chunkSize;

                ScanChunkForSubrecords(buffer, toRead, searchLimit, offset,
                    result, dedup, excludeRanges);

                offset += chunkSize;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return result;
    }

    private static void ScanChunkForSubrecords(
        byte[] buffer, int toRead, int searchLimit, long offset,
        EsmRecordScanResult result, ScanDedup dedup, List<(long start, long end)>? excludeRanges)
    {
        var bufferSpan = buffer.AsSpan(0, toRead);

#pragma warning disable S127 // Loop counter modified in body - intentional skip-ahead in binary parsing
        for (var i = 0; i <= searchLimit; i++)
        {
            if (IsInExcludedRange(offset + i, excludeRanges))
            {
                continue;
            }

            // Check for main record headers first - returns record size for skip-ahead
            var recordSize = TryAddMainRecordHeaderWithOffset(buffer, i, toRead, offset,
                result.MainRecords, dedup.SeenMainRecordOffsets);

            if (recordSize > 24)
            {
                var skipAmount = Math.Min(recordSize - 1, searchLimit - i);
                if (skipAmount > 0)
                {
                    i += skipAmount;
                }

                continue;
            }

            // Single magic read + dispatch map replaces 22 individual signature checks
            if (i + 4 > toRead) continue;
            var magic = BinaryPrimitives.ReadUInt32LittleEndian(bufferSpan.Slice(i, 4));

            if (SubrecordDispatch.TryGetValue(magic, out var handler))
            {
                handler(buffer, i, toRead, offset, result, dedup.SeenEdids, dedup.SeenFormIds);
            }
            else if (MatchesTextureSignature(buffer, i))
            {
                var sig = Encoding.ASCII.GetString(buffer, i, 4);
                EsmMiscDetector.TryAddPathSubrecordWithOffset(buffer, i, toRead, offset, sig, result.TexturePaths);
            }
            else
            {
                EsmMiscDetector.TryAddGenericSubrecordWithOffset(buffer, i, toRead, offset, result.GenericSubrecords);
            }
        }
#pragma warning restore S127
    }

    #endregion

    #region Detection Helpers

    /// <summary>
    ///     Checks if the given offset falls within any excluded range (e.g., module memory).
    ///     Used to skip ESM detection inside executable module regions.
    /// </summary>
    private static bool IsInExcludedRange(long offset, List<(long start, long end)>? ranges)
    {
        if (ranges == null || ranges.Count == 0)
        {
            return false;
        }

        foreach (var (start, end) in ranges)
        {
            if (offset >= start && offset < end)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     Validates a record signature using the comprehensive MainRecordTypes dictionary.
    ///     This provides stricter validation than just checking if it's uppercase ASCII.
    /// </summary>
    private static bool IsValidRecordSignature(string signature)
    {
        // Primary check: known record types from comprehensive EsmRecordTypes dictionary
        if (EsmRecordTypes.MainRecordTypes.ContainsKey(signature))
        {
            return true;
        }

        // Secondary: allow uppercase-only 4-char for potential unknown types
        // (memory dumps may have record types not in the PC version dictionary)
        return signature.Length == 4 && signature.All(c => c is >= 'A' and <= 'Z' or '_');
    }

    /// <summary>
    ///     Check if bytes match a texture signature (TX00-TX07).
    /// </summary>
    private static bool MatchesTextureSignature(byte[] data, int i)
    {
        if (i + 4 > data.Length)
        {
            return false;
        }

        return data[i] == 'T' && data[i + 1] == 'X' && data[i + 2] == '0' &&
               data[i + 3] >= '0' && data[i + 3] <= '7';
    }

    private static bool MatchesSignature(byte[] data, int i, ReadOnlySpan<byte> sig)
    {
        return data[i] == sig[0] && data[i + 1] == sig[1] && data[i + 2] == sig[2] && data[i + 3] == sig[3];
    }

    internal static bool IsRecordTypeMarker(byte[] data, int offset)
    {
        for (var b = 0; b < 4; b++)
        {
            if (!char.IsAsciiLetterOrDigit((char)data[offset + b]) && data[offset + b] != '_')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    ///     Checks if the signature at the given offset matches a known false positive pattern.
    ///     GPU debug register dumps contain patterns like "VGT_DEBUG" that look like valid signatures.
    /// </summary>
    private static bool IsKnownFalsePositive(byte[] data, int offset)
    {
        if (offset + 4 > data.Length)
        {
            return false;
        }

        // Check against known false positive patterns (both LE and BE byte orders)
        foreach (var pattern in KnownFalsePositivePatterns)
        {
            // Check little-endian order (as stored in memory)
            if (data[offset] == pattern[0] && data[offset + 1] == pattern[1] &&
                data[offset + 2] == pattern[2] && data[offset + 3] == pattern[3])
            {
                return true;
            }

            // Check big-endian (reversed) order for Xbox 360
            if (data[offset + 3] == pattern[0] && data[offset + 2] == pattern[1] &&
                data[offset + 1] == pattern[2] && data[offset] == pattern[3])
            {
                return true;
            }
        }

        return false;
    }

    #endregion

    #region Main Record Header Validation

    private static bool IsValidMainRecordHeader(string recordType, uint dataSize, uint flags, uint formId)
    {
        // Validate record type using comprehensive MainRecordTypes dictionary
        // This provides stricter validation than just checking if it's uppercase ASCII
        if (!IsValidRecordSignature(recordType))
        {
            return false;
        }

        // Validate data size (reasonable range for game records)
        // Most records are under 100KB, very few exceed 1MB
        if (dataSize == 0 || dataSize > 10_000_000)
        {
            return false;
        }

        // Validate flags (common valid flags, reject obviously bad values)
        // Upper bits should not be set for most valid records
        if ((flags & 0xFFF00000) != 0 && (flags & 0x00040000) == 0) // Allow compressed flag
        {
            return false;
        }

        // Validate FormID
        // Plugin index should be 0x00-0xFF (usually 0x00-0x0F for base game)
        // FormID should not be 0 or 0xFFFFFFFF
        if (formId == 0 || formId == 0xFFFFFFFF)
        {
            return false;
        }

        // False positive prevention: check if FormID bytes are all printable ASCII
        // This indicates we're inside string data (e.g., "PrisonerSandBoxPACKAGE" triggering PACK detection)
        // Real FormIDs have structured values like 0x00XXXXXX with plugin index as first byte
        if (IsFormIdAllPrintableAscii(formId))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    ///     Check if a FormID value consists entirely of printable ASCII characters.
    ///     This indicates we're likely inside string data, not a real record header.
    /// </summary>
    private static bool IsFormIdAllPrintableAscii(uint formId)
    {
        var b0 = (byte)(formId & 0xFF);
        var b1 = (byte)((formId >> 8) & 0xFF);
        var b2 = (byte)((formId >> 16) & 0xFF);
        var b3 = (byte)((formId >> 24) & 0xFF);

        return IsPrintableAscii(b0) && IsPrintableAscii(b1) &&
               IsPrintableAscii(b2) && IsPrintableAscii(b3);
    }

    private static bool IsPrintableAscii(byte b)
    {
        return b >= 0x20 && b < 0x7F;
    }

    #endregion

    #region Main Record Header Parsing

    private static DetectedMainRecord? TryParseMainRecordHeader(byte[] data, int i, int dataLength, bool isBigEndian)
    {
        if (i + 24 > dataLength)
        {
            return null;
        }

        // Read the 4-char signature
        var sigBytes = new byte[4];
        if (isBigEndian)
        {
            // Xbox 360: bytes are reversed, so reverse them back
            sigBytes[0] = data[i + 3];
            sigBytes[1] = data[i + 2];
            sigBytes[2] = data[i + 1];
            sigBytes[3] = data[i];
        }
        else
        {
            sigBytes[0] = data[i];
            sigBytes[1] = data[i + 1];
            sigBytes[2] = data[i + 2];
            sigBytes[3] = data[i + 3];
        }

        var recordType = Encoding.ASCII.GetString(sigBytes);

        // Read header fields with appropriate endianness
        uint dataSize, flags, formId;
        if (isBigEndian)
        {
            dataSize = BinaryUtils.ReadUInt32BE(data, i + 4);
            flags = BinaryUtils.ReadUInt32BE(data, i + 8);
            formId = BinaryUtils.ReadUInt32BE(data, i + 12);
        }
        else
        {
            dataSize = BinaryUtils.ReadUInt32LE(data, i + 4);
            flags = BinaryUtils.ReadUInt32LE(data, i + 8);
            formId = BinaryUtils.ReadUInt32LE(data, i + 12);
        }

        // Validate the header
        if (!IsValidMainRecordHeader(recordType, dataSize, flags, formId))
        {
            return null;
        }

        return new DetectedMainRecord(recordType, dataSize, flags, formId, i, isBigEndian);
    }

    private static DetectedMainRecord? TryParseMainRecordHeaderWithOffset(byte[] data, int i, int dataLength,
        long baseOffset, bool isBigEndian)
    {
        var header = TryParseMainRecordHeader(data, i, dataLength, isBigEndian);
        if (header == null)
        {
            return null;
        }

        return header with { Offset = baseOffset + i };
    }

    /// <summary>
    ///     Try to parse a GRUP header at position i. GRUP has a different layout than main records:
    ///     offset 4 = total group size (including header), offset 8 = label, offset 12 = group type (0–10).
    ///     Returns a DetectedMainRecord with DataSize=0 (only the 24-byte header is highlighted).
    /// </summary>
    private static DetectedMainRecord? TryParseGrupHeader(byte[] data, int i, int dataLength, bool isBigEndian)
    {
        if (i + 24 > dataLength)
        {
            return null;
        }

        // Read group type at offset 12 (where FormID would be for main records)
        var groupType = isBigEndian
            ? BinaryUtils.ReadUInt32BE(data, i + 12)
            : BinaryUtils.ReadUInt32LE(data, i + 12);

        // Group type must be 0–10 (defined by the ESM format)
        if (groupType > 10)
        {
            return null;
        }

        // Read total group size at offset 4 — must be at least 24 (header-only group)
        var groupSize = isBigEndian
            ? BinaryUtils.ReadUInt32BE(data, i + 4)
            : BinaryUtils.ReadUInt32LE(data, i + 4);

        if (groupSize < 24)
        {
            return null;
        }

        // Read label at offset 8 (FormID, record type, or block coords depending on group type)
        var label = isBigEndian
            ? BinaryUtils.ReadUInt32BE(data, i + 8)
            : BinaryUtils.ReadUInt32LE(data, i + 8);

        // DataSize = 0: only the 24-byte GRUP header gets highlighted,
        // not the group contents (which contain separately-detected records)
        return new DetectedMainRecord("GRUP", 0, groupType, label, i, isBigEndian);
    }

    private static void TryAddMainRecordHeader(byte[] data, int i, int dataLength,
        List<DetectedMainRecord> records, HashSet<long> seenOffsets)
    {
        if (i + 24 > dataLength || seenOffsets.Contains(i))
        {
            return;
        }

        // Reject known GPU debug patterns BEFORE parsing header
        if (IsKnownFalsePositive(data, i))
        {
            return;
        }

        var magic = BinaryUtils.ReadUInt32LE(data, i);

        // Try little-endian (PC format)
        if (RuntimeRecordMagicLE.Contains(magic))
        {
            var header = TryParseMainRecordHeader(data, i, dataLength, false);
            if (header != null && seenOffsets.Add(i))
            {
                records.Add(header);
            }

            return;
        }

        // Try big-endian (Xbox 360 format) - signature bytes are reversed
        if (RuntimeRecordMagicBE.Contains(magic))
        {
            var header = TryParseMainRecordHeader(data, i, dataLength, true);
            if (header != null && seenOffsets.Add(i))
            {
                records.Add(header);
            }

            return;
        }

        // Try GRUP (structural container — different header layout than main records)
        if (magic is SigGrupLE or SigGrupBE)
        {
            var isBigEndian = magic == SigGrupBE;
            var grup = TryParseGrupHeader(data, i, dataLength, isBigEndian);
            if (grup != null && seenOffsets.Add(i))
            {
                records.Add(grup);
            }
        }
    }

    /// <summary>
    ///     Try to parse a main record header at position i. If successful, adds to records
    ///     and returns the total record size (24-byte header + data size) for skip-ahead optimization.
    /// </summary>
    /// <returns>Total record size if a valid record was found; 0 otherwise.</returns>
    private static int TryAddMainRecordHeaderWithOffset(byte[] data, int i, int dataLength, long baseOffset,
        List<DetectedMainRecord> records, HashSet<long> seenOffsets)
    {
        var globalOffset = baseOffset + i;
        if (i + 24 > dataLength || seenOffsets.Contains(globalOffset))
        {
            return 0;
        }

        // Reject known GPU debug patterns BEFORE parsing header
        // These ASCII patterns look like valid 4-char signatures but are GPU register names
        if (IsKnownFalsePositive(data, i))
        {
            return 0;
        }

        var magic = BinaryUtils.ReadUInt32LE(data, i);

        // Try little-endian (PC format)
        if (RuntimeRecordMagicLE.Contains(magic))
        {
            var header = TryParseMainRecordHeaderWithOffset(data, i, dataLength, baseOffset, false);
            if (header != null && seenOffsets.Add(globalOffset))
            {
                records.Add(header);
                // Return total record size: 24-byte header + data size
                return 24 + (int)header.DataSize;
            }

            return 0;
        }

        // Try big-endian (Xbox 360 format)
        if (RuntimeRecordMagicBE.Contains(magic))
        {
            var header = TryParseMainRecordHeaderWithOffset(data, i, dataLength, baseOffset, true);
            if (header != null && seenOffsets.Add(globalOffset))
            {
                records.Add(header);
                // Return total record size: 24-byte header + data size
                return 24 + (int)header.DataSize;
            }

            return 0;
        }

        // Try GRUP (structural container — different header layout than main records)
        if (magic is SigGrupLE or SigGrupBE)
        {
            var isBigEndian = magic == SigGrupBE;
            var grup = TryParseGrupHeader(data, i, dataLength, isBigEndian);
            if (grup != null)
            {
                var grupWithOffset = grup with { Offset = globalOffset };
                if (seenOffsets.Add(globalOffset))
                {
                    records.Add(grupWithOffset);
                    return 24; // GRUP header only (contents are separate records)
                }
            }
        }

        return 0;
    }

    #endregion

    #region Helper Methods

    private static HashSet<uint> BuildMagicSet(bool bigEndian)
    {
        var set = new HashSet<uint>();
        foreach (var type in RuntimeRecordTypes)
        {
            var bytes = Encoding.ASCII.GetBytes(type);
            var leValue = BitConverter.ToUInt32(bytes, 0);
            set.Add(bigEndian ? BinaryPrimitives.ReverseEndianness(leValue) : leValue);
        }

        return set;
    }

    #endregion
}
