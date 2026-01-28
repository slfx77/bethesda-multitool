using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.Text;
using FalloutXbox360Utils.Core.Converters.Esm.Schema;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.EsmRecord;

/// <summary>
///     ESM record fragment format module.
///     Scans memory dumps for main record headers and subrecords.
///     Detects both PC (little-endian) and Xbox 360 (big-endian) formats.
///     This format doesn't participate in normal carving (ShowInFilterUI = false)
///     but provides dump analysis capabilities.
/// </summary>
public sealed class EsmRecordFormat : FileFormatBase, IDumpScanner
{
    /// <summary>
    ///     High-priority record types likely to be in memory during gameplay.
    ///     These are placed objects, actors, and commonly accessed definitions.
    /// </summary>
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

        // Game settings and globals
        "GMST", // Game Setting
        "GLOB", // Global Variable
        "WTHR", // Weather
        "CLMT", // Climate
        "REGN", // Region
        "IMAD", // Image Space Adapter
        "IMGS" // Image Space
    ];

    /// <summary>
    ///     Little-endian magic values for runtime record types.
    /// </summary>
    private static readonly HashSet<uint> RuntimeRecordMagicLE = BuildMagicSet(false);

    /// <summary>
    ///     Big-endian (byte-reversed) magic values for Xbox 360 detection.
    /// </summary>
    private static readonly HashSet<uint> RuntimeRecordMagicBE = BuildMagicSet(true);

    /// <summary>
    ///     All schema-defined subrecord signatures for generic detection.
    ///     Lazy initialization to avoid circular dependencies.
    /// </summary>
    private static readonly Lazy<HashSet<string>> SchemaSignaturesLE =
        new(() => SubrecordSchemaRegistry.GetAllSignatures().ToHashSet());

    /// <summary>
    ///     Schema signatures in big-endian (reversed) form for Xbox 360 detection.
    /// </summary>
    private static readonly Lazy<HashSet<string>> SchemaSignaturesBE = new(() => SubrecordSchemaRegistry
        .GetAllSignatures()
        .Select(SubrecordSchemaRegistry.GetReversedSignature)
        .ToHashSet());

    /// <summary>
    ///     Signatures already handled by specific detection (to avoid duplicates).
    /// </summary>
    private static readonly HashSet<string> SpecificSignatures =
    [
        "EDID", "GMST", "SCTX", "SCRO", "NAME", "DATA", "ACBS",
        "NAM1", "TRDT", "FULL", "DESC", "MODL", "ICON", "MICO",
        "TX00", "TX01", "TX02", "TX03", "TX04", "TX05", "TX06", "TX07",
        "SCRI", "ENAM", "SNAM", "QNAM", "CTDA", "VHGT", "XCLC"
    ];

    /// <summary>
    ///     Patterns that indicate non-ESM data (GPU debug registers, etc.).
    ///     These ASCII strings could be misinterpreted as valid ESM signatures.
    ///     GPU debug register dumps from Xbox 360 memory contain these patterns.
    /// </summary>
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

    public override string FormatId => "esmrecord";
    public override string DisplayName => "ESM Records";
    public override string Extension => ".esm";
    public override FileCategory Category => FileCategory.EsmData;
    public override string OutputFolder => "esm_records";
    public override int MinSize => 8;
    public override int MaxSize => 64 * 1024;
    public override bool ShowInFilterUI => false;

    public override IReadOnlyList<FormatSignature> Signatures { get; } = [];

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

    public override ParseResult? Parse(ReadOnlySpan<byte> data, int offset = 0)
    {
        return null;
    }

    #region IDumpScanner

    public object ScanDump(MemoryMappedViewAccessor accessor, long fileSize)
    {
        return ScanForRecordsMemoryMapped(accessor, fileSize);
    }

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
                TryAddEdidRecord(data, i, data.Length, result.EditorIds, seenEdids);
            }
            else if (MatchesSignature(data, i, "GMST"u8))
            {
                TryAddGmstRecord(data, i, data.Length, result.GameSettings);
            }
            else if (MatchesSignature(data, i, "SCTX"u8))
            {
                TryAddSctxRecord(data, i, data.Length, result.ScriptSources);
            }
            else if (MatchesSignature(data, i, "SCRO"u8))
            {
                TryAddScroRecord(data, i, data.Length, result.FormIdReferences, seenFormIds);
            }
            else if (MatchesSignature(data, i, "NAME"u8))
            {
                TryAddNameSubrecord(data, i, data.Length, result.NameReferences);
            }
            else if (MatchesSignature(data, i, "DATA"u8))
            {
                TryAddPositionSubrecord(data, i, data.Length, result.Positions);
            }
            else if (MatchesSignature(data, i, "ACBS"u8))
            {
                TryAddActorBaseSubrecord(data, i, data.Length, result.ActorBases);
            }
            else if (MatchesSignature(data, i, "NAM1"u8))
            {
                TryAddResponseTextSubrecord(data, i, data.Length, result.ResponseTexts);
            }
            else if (MatchesSignature(data, i, "TRDT"u8))
            {
                TryAddResponseDataSubrecord(data, i, data.Length, result.ResponseData);
            }
            // Text-containing subrecords
            else if (MatchesSignature(data, i, "FULL"u8))
            {
                TryAddTextSubrecord(data, i, data.Length, "FULL", result.FullNames);
            }
            else if (MatchesSignature(data, i, "DESC"u8))
            {
                TryAddTextSubrecord(data, i, data.Length, "DESC", result.Descriptions);
            }
            else if (MatchesSignature(data, i, "MODL"u8))
            {
                TryAddPathSubrecord(data, i, data.Length, "MODL", result.ModelPaths);
            }
            else if (MatchesSignature(data, i, "ICON"u8))
            {
                TryAddPathSubrecord(data, i, data.Length, "ICON", result.IconPaths);
            }
            else if (MatchesSignature(data, i, "MICO"u8))
            {
                TryAddPathSubrecord(data, i, data.Length, "MICO", result.IconPaths);
            }
            // Texture set paths (TX00-TX07)
            else if (MatchesTextureSignature(data, i))
            {
                var sig = Encoding.ASCII.GetString(data, i, 4);
                TryAddPathSubrecord(data, i, data.Length, sig, result.TexturePaths);
            }
            // FormID reference subrecords
            else if (MatchesSignature(data, i, "SCRI"u8))
            {
                TryAddFormIdSubrecord(data, i, data.Length, "SCRI", result.ScriptRefs);
            }
            else if (MatchesSignature(data, i, "ENAM"u8))
            {
                TryAddFormIdSubrecord(data, i, data.Length, "ENAM", result.EffectRefs);
            }
            else if (MatchesSignature(data, i, "SNAM"u8))
            {
                TryAddFormIdSubrecord(data, i, data.Length, "SNAM", result.SoundRefs);
            }
            else if (MatchesSignature(data, i, "QNAM"u8))
            {
                TryAddFormIdSubrecord(data, i, data.Length, "QNAM", result.QuestRefs);
            }
            // Condition data
            else if (MatchesSignature(data, i, "CTDA"u8))
            {
                TryAddConditionSubrecord(data, i, data.Length, result.Conditions);
            }
        }

        return result;
    }

    /// <summary>
    ///     Scan an entire memory dump for ESM records using memory-mapped access.
    ///     Processes in chunks to avoid loading the entire file into memory.
    /// </summary>
    /// <param name="accessor">Memory-mapped file accessor.</param>
    /// <param name="fileSize">Total file size in bytes.</param>
    /// <param name="excludeRanges">Optional list of (start, end) ranges to skip (e.g., module memory).</param>
    public static EsmRecordScanResult ScanForRecordsMemoryMapped(
        MemoryMappedViewAccessor accessor,
        long fileSize,
        List<(long start, long end)>? excludeRanges = null)
    {
        const int chunkSize = 16 * 1024 * 1024; // 16MB chunks
        const int overlapSize = 1024; // Overlap to handle records at chunk boundaries

        var result = new EsmRecordScanResult();
        var seenEdids = new HashSet<string>();
        var seenFormIds = new HashSet<uint>();
        var seenMainRecordOffsets = new HashSet<long>();
        var buffer = ArrayPool<byte>.Shared.Rent(chunkSize + overlapSize);

        try
        {
            long offset = 0;
            while (offset < fileSize)
            {
                var toRead = (int)Math.Min(chunkSize + overlapSize, fileSize - offset);
                accessor.ReadArray(offset, buffer, 0, toRead);

                // Determine the search limit for this chunk
                // Only search up to chunkSize unless this is the last chunk
                var searchLimit = offset + chunkSize >= fileSize ? toRead - 24 : chunkSize;

                // Scan this chunk
                for (var i = 0; i <= searchLimit; i++)
                {
                    // Skip offsets inside excluded ranges (e.g., module memory)
                    var globalOffset = offset + i;
                    if (IsInExcludedRange(globalOffset, excludeRanges))
                    {
                        continue;
                    }

                    // Check for main record headers first
                    TryAddMainRecordHeaderWithOffset(buffer, i, toRead, offset, result.MainRecords,
                        seenMainRecordOffsets);

                    // Subrecord detection
                    if (MatchesSignature(buffer, i, "EDID"u8))
                    {
                        TryAddEdidRecordWithOffset(buffer, i, toRead, offset, result.EditorIds, seenEdids);
                    }
                    else if (MatchesSignature(buffer, i, "GMST"u8))
                    {
                        TryAddGmstRecordWithOffset(buffer, i, toRead, offset, result.GameSettings);
                    }
                    else if (MatchesSignature(buffer, i, "SCTX"u8))
                    {
                        TryAddSctxRecordWithOffset(buffer, i, toRead, offset, result.ScriptSources);
                    }
                    else if (MatchesSignature(buffer, i, "SCRO"u8))
                    {
                        TryAddScroRecordWithOffset(buffer, i, toRead, offset, result.FormIdReferences, seenFormIds);
                    }
                    else if (MatchesSignature(buffer, i, "NAME"u8))
                    {
                        TryAddNameSubrecordWithOffset(buffer, i, toRead, offset, result.NameReferences);
                    }
                    else if (MatchesSignature(buffer, i, "DATA"u8))
                    {
                        TryAddPositionSubrecordWithOffset(buffer, i, toRead, offset, result.Positions);
                    }
                    else if (MatchesSignature(buffer, i, "ACBS"u8))
                    {
                        TryAddActorBaseSubrecordWithOffset(buffer, i, toRead, offset, result.ActorBases);
                    }
                    else if (MatchesSignature(buffer, i, "NAM1"u8))
                    {
                        TryAddResponseTextSubrecordWithOffset(buffer, i, toRead, offset, result.ResponseTexts);
                    }
                    else if (MatchesSignature(buffer, i, "TRDT"u8))
                    {
                        TryAddResponseDataSubrecordWithOffset(buffer, i, toRead, offset, result.ResponseData);
                    }
                    // Text-containing subrecords
                    else if (MatchesSignature(buffer, i, "FULL"u8))
                    {
                        TryAddTextSubrecordWithOffset(buffer, i, toRead, offset, "FULL", result.FullNames);
                    }
                    else if (MatchesSignature(buffer, i, "DESC"u8))
                    {
                        TryAddTextSubrecordWithOffset(buffer, i, toRead, offset, "DESC", result.Descriptions);
                    }
                    else if (MatchesSignature(buffer, i, "MODL"u8))
                    {
                        TryAddPathSubrecordWithOffset(buffer, i, toRead, offset, "MODL", result.ModelPaths);
                    }
                    else if (MatchesSignature(buffer, i, "ICON"u8))
                    {
                        TryAddPathSubrecordWithOffset(buffer, i, toRead, offset, "ICON", result.IconPaths);
                    }
                    else if (MatchesSignature(buffer, i, "MICO"u8))
                    {
                        TryAddPathSubrecordWithOffset(buffer, i, toRead, offset, "MICO", result.IconPaths);
                    }
                    // Texture set paths (TX00-TX07)
                    else if (MatchesTextureSignature(buffer, i))
                    {
                        var sig = Encoding.ASCII.GetString(buffer, i, 4);
                        TryAddPathSubrecordWithOffset(buffer, i, toRead, offset, sig, result.TexturePaths);
                    }
                    // FormID reference subrecords
                    else if (MatchesSignature(buffer, i, "SCRI"u8))
                    {
                        TryAddFormIdSubrecordWithOffset(buffer, i, toRead, offset, "SCRI", result.ScriptRefs);
                    }
                    else if (MatchesSignature(buffer, i, "ENAM"u8))
                    {
                        TryAddFormIdSubrecordWithOffset(buffer, i, toRead, offset, "ENAM", result.EffectRefs);
                    }
                    else if (MatchesSignature(buffer, i, "SNAM"u8))
                    {
                        TryAddFormIdSubrecordWithOffset(buffer, i, toRead, offset, "SNAM", result.SoundRefs);
                    }
                    else if (MatchesSignature(buffer, i, "QNAM"u8))
                    {
                        TryAddFormIdSubrecordWithOffset(buffer, i, toRead, offset, "QNAM", result.QuestRefs);
                    }
                    // Condition data
                    else if (MatchesSignature(buffer, i, "CTDA"u8))
                    {
                        TryAddConditionSubrecordWithOffset(buffer, i, toRead, offset, result.Conditions);
                    }
                    // VHGT heightmap subrecords (LE and BE)
                    else if (MatchesSignature(buffer, i, "VHGT"u8))
                    {
                        TryAddVhgtHeightmapWithOffset(buffer, i, toRead, offset, false, result.Heightmaps);
                    }
                    else if (MatchesSignature(buffer, i, "TGHV"u8)) // BE reversed
                    {
                        TryAddVhgtHeightmapWithOffset(buffer, i, toRead, offset, true, result.Heightmaps);
                    }
                    // XCLC cell grid subrecords (LE and BE)
                    else if (MatchesSignature(buffer, i, "XCLC"u8))
                    {
                        TryAddXclcSubrecordWithOffset(buffer, i, toRead, offset, false, result.CellGrids);
                    }
                    else if (MatchesSignature(buffer, i, "CLCX"u8)) // BE reversed
                    {
                        TryAddXclcSubrecordWithOffset(buffer, i, toRead, offset, true, result.CellGrids);
                    }
                    // Generic schema-driven subrecord detection
                    else
                    {
                        TryAddGenericSubrecordWithOffset(buffer, i, toRead, offset, result.GenericSubrecords);
                    }
                }

                offset += chunkSize;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return result;
    }

    /// <summary>
    ///     Extract full LAND records with heightmap data from detected main records.
    ///     Call this after ScanForRecordsMemoryMapped to get detailed terrain data.
    /// </summary>
    public static void ExtractLandRecords(
        MemoryMappedViewAccessor accessor,
        long fileSize,
        EsmRecordScanResult scanResult)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(8192); // LAND records are typically 2-6KB

        try
        {
            var landRecords = scanResult.MainRecords.Where(r => r.RecordType == "LAND").ToList();

            foreach (var header in landRecords)
            {
                // Read the record data (after 24-byte header)
                var dataStart = header.Offset + 24;
                var dataSize = (int)Math.Min(header.DataSize, 8192);

                if (dataStart + dataSize > fileSize)
                {
                    continue;
                }

                accessor.ReadArray(dataStart, buffer, 0, dataSize);

                var land = ExtractLandFromBuffer(buffer, dataSize, header);
                if (land?.Heightmap != null)
                {
                    scanResult.LandRecords.Add(land);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    ///     Extract full REFR records with position and base object data.
    /// </summary>
    public static void ExtractRefrRecords(
        MemoryMappedViewAccessor accessor,
        long fileSize,
        EsmRecordScanResult scanResult,
        Dictionary<uint, string>? editorIdMap = null)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(1024); // REFR records are typically small

        try
        {
            var refrRecords = scanResult.MainRecords.Where(r => r.RecordType == "REFR").ToList();

            foreach (var header in refrRecords)
            {
                var dataStart = header.Offset + 24;
                var dataSize = (int)Math.Min(header.DataSize, 1024);

                if (dataStart + dataSize > fileSize)
                {
                    continue;
                }

                accessor.ReadArray(dataStart, buffer, 0, dataSize);

                var refr = ExtractRefrFromBuffer(buffer, dataSize, header, editorIdMap);
                if (refr != null)
                {
                    scanResult.RefrRecords.Add(refr);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static ExtractedLandRecord? ExtractLandFromBuffer(byte[] data, int dataSize, DetectedMainRecord header)
    {
        LandHeightmap? heightmap = null;
        var textureLayers = new List<LandTextureLayer>();

        // Iterate through subrecords using the standard subrecord header format
        foreach (var sub in IterateSubrecords(data, dataSize, header.IsBigEndian))
        {
            var subData = data.AsSpan(sub.DataOffset, sub.DataLength);

            if (sub.Signature == "VHGT")
            {
                // Use schema reader for VHGT heightmap
                var vhgt = SubrecordSchemaReader.ReadVhgtHeightmap(subData, header.IsBigEndian);
                if (vhgt.HasValue)
                {
                    heightmap = new LandHeightmap
                    {
                        HeightOffset = vhgt.Value.heightOffset,
                        HeightDeltas = vhgt.Value.deltas,
                        Offset = header.Offset + 24 + sub.DataOffset
                    };
                }
            }
            else if (sub.Signature is "ATXT" or "BTXT" && sub.DataLength >= 8)
            {
                // Use schema reader for texture layer FormID
                var textureFormId = SubrecordSchemaReader.ReadNameFormId(subData, header.IsBigEndian);
                if (textureFormId.HasValue)
                {
                    var quadrant = subData[4];
                    var layer = header.IsBigEndian
                        ? BinaryPrimitives.ReadInt16BigEndian(subData[6..])
                        : BinaryPrimitives.ReadInt16LittleEndian(subData[6..]);

                    textureLayers.Add(new LandTextureLayer(textureFormId.Value, quadrant, layer,
                        header.Offset + 24 + sub.DataOffset));
                }
            }
        }

        if (heightmap == null)
        {
            return null;
        }

        return new ExtractedLandRecord
        {
            Header = header,
            Heightmap = heightmap,
            TextureLayers = textureLayers
        };
    }

    private static ExtractedRefrRecord? ExtractRefrFromBuffer(
        byte[] data,
        int dataSize,
        DetectedMainRecord header,
        Dictionary<uint, string>? editorIdMap)
    {
        uint baseFormId = 0;
        PositionSubrecord? position = null;
        var scale = 1.0f;
        uint? ownerFormId = null;

        // Iterate through subrecords using the standard subrecord header format
        foreach (var sub in IterateSubrecords(data, dataSize, header.IsBigEndian))
        {
            var subData = data.AsSpan(sub.DataOffset, sub.DataLength);

            switch (sub.Signature)
            {
                case "NAME" when sub.DataLength == 4:
                    baseFormId = SubrecordSchemaReader.ReadNameFormId(subData, header.IsBigEndian) ?? 0;
                    break;

                case "DATA" when sub.DataLength == 24:
                    var pos = SubrecordSchemaReader.ReadDataPosition(subData, header.IsBigEndian);
                    if (pos.HasValue)
                    {
                        position = new PositionSubrecord(
                            pos.Value.x, pos.Value.y, pos.Value.z,
                            pos.Value.rotX, pos.Value.rotY, pos.Value.rotZ,
                            header.Offset + 24 + sub.DataOffset, header.IsBigEndian);
                    }

                    break;

                case "XSCL" when sub.DataLength == 4:
                    scale = SubrecordSchemaReader.ReadXsclScale(subData, header.IsBigEndian) ?? 1.0f;
                    break;

                case "XOWN" when sub.DataLength == 4:
                    ownerFormId = SubrecordSchemaReader.ReadNameFormId(subData, header.IsBigEndian);
                    break;
            }
        }

        if (baseFormId == 0)
        {
            return null;
        }

        return new ExtractedRefrRecord
        {
            Header = header,
            BaseFormId = baseFormId,
            Position = position,
            Scale = scale,
            OwnerFormId = ownerFormId,
            BaseEditorId = editorIdMap?.GetValueOrDefault(baseFormId)
        };
    }

    /// <summary>
    ///     Parsed subrecord information for iteration.
    /// </summary>
    private readonly record struct ParsedSubrecord(string Signature, int DataOffset, int DataLength);

    /// <summary>
    ///     Iterates through subrecords in a record's data section.
    ///     Returns (signature, data offset, data length) for each subrecord.
    /// </summary>
    private static IEnumerable<ParsedSubrecord> IterateSubrecords(byte[] data, int dataSize, bool bigEndian)
    {
        var offset = 0;

        while (offset + 6 <= dataSize)
        {
            // Read subrecord signature (4 bytes)
            var sig = bigEndian
                ? new string([
                    (char)data[offset + 3], (char)data[offset + 2], (char)data[offset + 1], (char)data[offset]
                ])
                : Encoding.ASCII.GetString(data, offset, 4);

            // Read subrecord size (2 bytes)
            var subSize = bigEndian
                ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset + 4))
                : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 4));

            if (offset + 6 + subSize > dataSize)
            {
                yield break;
            }

            yield return new ParsedSubrecord(sig, offset + 6, subSize);

            offset += 6 + subSize;
        }
    }

    public static Dictionary<uint, string> CorrelateFormIdsToNames(byte[] data,
        EsmRecordScanResult? existingScan = null)
    {
        var scan = existingScan ?? ScanForRecords(data);
        var correlations = new Dictionary<uint, string>();

        foreach (var edid in scan.EditorIds)
        {
            var formId = FindRecordFormId(data, (int)edid.Offset);
            if (formId != 0 && !correlations.ContainsKey(formId))
            {
                correlations[formId] = edid.Name;
            }
        }

        return correlations;
    }

    /// <summary>
    ///     Correlate FormIDs to names using memory-mapped access.
    /// </summary>
    public static Dictionary<uint, string> CorrelateFormIdsToNamesMemoryMapped(
        MemoryMappedViewAccessor accessor,
        long fileSize,
        EsmRecordScanResult existingScan)
    {
        // fileSize parameter kept for API consistency, not needed for correlation
        _ = fileSize;

        var correlations = new Dictionary<uint, string>();
        var buffer = ArrayPool<byte>.Shared.Rent(256); // Small buffer for searching backward

        try
        {
            foreach (var edid in existingScan.EditorIds)
            {
                var edidOffset = edid.Offset;
                var searchStart = Math.Max(0, edidOffset - 200);
                var toRead = (int)Math.Min(256, edidOffset - searchStart + 50);

                if (toRead <= 0)
                {
                    continue;
                }

                accessor.ReadArray(searchStart, buffer, 0, toRead);

                var localEdidOffset = (int)(edidOffset - searchStart);
                var formId = FindRecordFormIdInBuffer(buffer, localEdidOffset, toRead);

                if (formId != 0 && !correlations.ContainsKey(formId))
                {
                    correlations[formId] = edid.Name;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return correlations;
    }

    /// <summary>
    ///     Export ESM records to files. Delegates to EsmRecordExporter.
    /// </summary>
    public static Task ExportRecordsAsync(
        EsmRecordScanResult records,
        Dictionary<uint, string> formIdMap,
        string outputDir)
    {
        return EsmRecordExporter.ExportRecordsAsync(records, formIdMap, outputDir);
    }

    #endregion

    #region Private Implementation

    private static bool MatchesSignature(byte[] data, int i, ReadOnlySpan<byte> sig)
    {
        return data[i] == sig[0] && data[i + 1] == sig[1] && data[i + 2] == sig[2] && data[i + 3] == sig[3];
    }

    private static void TryAddEdidRecord(byte[] data, int i, int dataLength, List<EdidRecord> records,
        HashSet<string> seen)
    {
        if (i + 6 > dataLength)
        {
            return;
        }

        var len = BinaryUtils.ReadUInt16LE(data, i + 4);
        if (len == 0 || len >= 256 || i + 6 + len > dataLength)
        {
            return;
        }

        var name = ReadNullTermString(data, i + 6, len);
        if (IsValidEditorId(name) && seen.Add(name))
        {
            records.Add(new EdidRecord(name, i));
        }
    }

    private static void TryAddEdidRecordWithOffset(byte[] data, int i, int dataLength, long baseOffset,
        List<EdidRecord> records, HashSet<string> seen)
    {
        if (i + 6 > dataLength)
        {
            return;
        }

        var len = BinaryUtils.ReadUInt16LE(data, i + 4);
        if (len == 0 || len >= 256 || i + 6 + len > dataLength)
        {
            return;
        }

        var name = ReadNullTermString(data, i + 6, len);
        if (IsValidEditorId(name) && seen.Add(name))
        {
            records.Add(new EdidRecord(name, baseOffset + i));
        }
    }

    private static void TryAddGmstRecord(byte[] data, int i, int dataLength, List<GmstRecord> records)
    {
        if (i + 6 > dataLength)
        {
            return;
        }

        var len = BinaryUtils.ReadUInt16LE(data, i + 4);
        if (len == 0 || len >= 512 || i + 6 + len > dataLength)
        {
            return;
        }

        var name = ReadNullTermString(data, i + 6, len);
        if (IsValidSettingName(name))
        {
            records.Add(new GmstRecord(name, i, len));
        }
    }

    private static void TryAddGmstRecordWithOffset(byte[] data, int i, int dataLength, long baseOffset,
        List<GmstRecord> records)
    {
        if (i + 6 > dataLength)
        {
            return;
        }

        var len = BinaryUtils.ReadUInt16LE(data, i + 4);
        if (len == 0 || len >= 512 || i + 6 + len > dataLength)
        {
            return;
        }

        var name = ReadNullTermString(data, i + 6, len);
        if (IsValidSettingName(name))
        {
            records.Add(new GmstRecord(name, baseOffset + i, len));
        }
    }

    private static void TryAddSctxRecord(byte[] data, int i, int dataLength, List<SctxRecord> records)
    {
        if (i + 6 > dataLength)
        {
            return;
        }

        var len = BinaryUtils.ReadUInt16LE(data, i + 4);
        if (len <= 10 || len >= 65535 || i + 6 + len > dataLength)
        {
            return;
        }

        var text = Encoding.ASCII.GetString(data, i + 6, len).TrimEnd('\0');
        if (text.Length > 5 && ContainsScriptKeywords(text))
        {
            records.Add(new SctxRecord(text, i, len));
        }
    }

    private static void TryAddSctxRecordWithOffset(byte[] data, int i, int dataLength, long baseOffset,
        List<SctxRecord> records)
    {
        if (i + 6 > dataLength)
        {
            return;
        }

        var len = BinaryUtils.ReadUInt16LE(data, i + 4);
        if (len <= 10 || len >= 65535 || i + 6 + len > dataLength)
        {
            return;
        }

        var text = Encoding.ASCII.GetString(data, i + 6, len).TrimEnd('\0');
        if (text.Length > 5 && ContainsScriptKeywords(text))
        {
            records.Add(new SctxRecord(text, baseOffset + i, len));
        }
    }

    private static void TryAddScroRecord(byte[] data, int i, int dataLength, List<ScroRecord> records,
        HashSet<uint> seen)
    {
        if (i + 10 > dataLength)
        {
            return;
        }

        var len = BinaryUtils.ReadUInt16LE(data, i + 4);
        if (len != 4)
        {
            return;
        }

        var formId = BinaryUtils.ReadUInt32LE(data, i + 6);
        if (formId == 0 || formId == 0xFFFFFFFF || formId >> 24 > 0x0F)
        {
            return;
        }

        if (seen.Add(formId))
        {
            records.Add(new ScroRecord(formId, i));
        }
    }

    private static void TryAddScroRecordWithOffset(byte[] data, int i, int dataLength, long baseOffset,
        List<ScroRecord> records, HashSet<uint> seen)
    {
        if (i + 10 > dataLength)
        {
            return;
        }

        var len = BinaryUtils.ReadUInt16LE(data, i + 4);
        if (len != 4)
        {
            return;
        }

        var formId = BinaryUtils.ReadUInt32LE(data, i + 6);
        if (formId == 0 || formId == 0xFFFFFFFF || formId >> 24 > 0x0F)
        {
            return;
        }

        if (seen.Add(formId))
        {
            records.Add(new ScroRecord(formId, baseOffset + i));
        }
    }

    private static uint FindRecordFormId(byte[] data, int edidOffset)
    {
        var searchStart = Math.Max(0, edidOffset - 200);
        for (var checkOffset = edidOffset - 4; checkOffset >= searchStart; checkOffset--)
        {
            var formId = TryExtractFormIdFromRecordHeader(data, checkOffset, edidOffset, data.Length);
            if (formId != 0)
            {
                return formId;
            }
        }

        return 0;
    }

    private static uint FindRecordFormIdInBuffer(byte[] data, int edidLocalOffset, int dataLength)
    {
        var searchStart = Math.Max(0, edidLocalOffset - 200);
        for (var checkOffset = edidLocalOffset - 4; checkOffset >= searchStart; checkOffset--)
        {
            var formId = TryExtractFormIdFromRecordHeader(data, checkOffset, edidLocalOffset, dataLength);
            if (formId != 0)
            {
                return formId;
            }
        }

        return 0;
    }

    private static uint TryExtractFormIdFromRecordHeader(byte[] data, int checkOffset, int edidOffset, int dataLength)
    {
        if (checkOffset + 24 >= dataLength)
        {
            return 0;
        }

        if (!IsRecordTypeMarker(data, checkOffset))
        {
            return 0;
        }

        var formId = BinaryUtils.ReadUInt32LE(data, checkOffset + 12);
        if (formId == 0 || formId == 0xFFFFFFFF || formId >> 24 > 0x0F)
        {
            return 0;
        }

        var size = BinaryUtils.ReadUInt32LE(data, checkOffset + 4);
        if (size is > 0 and < 10_000_000 && edidOffset < checkOffset + 24 + size)
        {
            return formId;
        }

        return 0;
    }

    private static bool IsRecordTypeMarker(byte[] data, int offset)
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

    private static string ReadNullTermString(byte[] data, int offset, int maxLen)
    {
        var end = offset;
        while (end < offset + maxLen && end < data.Length && data[end] != 0)
        {
            end++;
        }

        return Encoding.ASCII.GetString(data, offset, end - offset);
    }

    private static bool IsValidEditorId(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length < 2 || name.Length > 200)
        {
            return false;
        }

        if (!char.IsLetter(name[0]))
        {
            return false;
        }

        // Direct iteration instead of LINQ Count to avoid delegate allocation
        var validChars = 0;
        foreach (var c in name)
        {
            if (char.IsLetterOrDigit(c) || c == '_')
            {
                validChars++;
            }
        }

        return validChars >= name.Length * 0.9;
    }

    private static bool IsValidSettingName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length < 2)
        {
            return false;
        }

        var firstChar = char.ToLower(name[0], CultureInfo.InvariantCulture);
        if (firstChar is not ('f' or 'i' or 's' or 'b'))
        {
            return false;
        }

        foreach (var c in name)
        {
            if (!char.IsLetterOrDigit(c) && c != '_')
            {
                return false;
            }
        }

        return true;
    }

    private static bool ContainsScriptKeywords(string text)
    {
        return text.Contains("Enable", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Disable", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("MoveTo", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("SetStage", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("GetStage", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("if ", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("endif", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("REF", StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    #region Main Record Detection

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
        }
    }

    private static void TryAddMainRecordHeaderWithOffset(byte[] data, int i, int dataLength, long baseOffset,
        List<DetectedMainRecord> records, HashSet<long> seenOffsets)
    {
        var globalOffset = baseOffset + i;
        if (i + 24 > dataLength || seenOffsets.Contains(globalOffset))
        {
            return;
        }

        // Reject known GPU debug patterns BEFORE parsing header
        // These ASCII patterns look like valid 4-char signatures but are GPU register names
        if (IsKnownFalsePositive(data, i))
        {
            return;
        }

        var magic = BinaryUtils.ReadUInt32LE(data, i);

        // Try little-endian (PC format)
        if (RuntimeRecordMagicLE.Contains(magic))
        {
            var header = TryParseMainRecordHeaderWithOffset(data, i, dataLength, baseOffset, false);
            if (header != null && seenOffsets.Add(globalOffset))
            {
                records.Add(header);
            }

            return;
        }

        // Try big-endian (Xbox 360 format)
        if (RuntimeRecordMagicBE.Contains(magic))
        {
            var header = TryParseMainRecordHeaderWithOffset(data, i, dataLength, baseOffset, true);
            if (header != null && seenOffsets.Add(globalOffset))
            {
                records.Add(header);
            }
        }
    }

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

        // Plugin index validation (relaxed - allow any valid index)
        var pluginIndex = formId >> 24;
        if (pluginIndex > 0xFF)
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

    private static bool IsPrintableAscii(byte b) => b >= 0x20 && b < 0x7F;

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

    #endregion

    #region Extended Subrecord Detection

    private static void TryAddNameSubrecord(byte[] data, int i, int dataLength, List<NameSubrecord> records)
    {
        if (i + 10 > dataLength)
        {
            return;
        }

        var len = BinaryUtils.ReadUInt16LE(data, i + 4);
        if (len != 4)
        {
            return;
        }

        // Try little-endian first
        var formId = BinaryUtils.ReadUInt32LE(data, i + 6);
        if (IsValidFormId(formId))
        {
            records.Add(new NameSubrecord(formId, i, false));
            return;
        }

        // Try big-endian
        formId = BinaryUtils.ReadUInt32BE(data, i + 6);
        if (IsValidFormId(formId))
        {
            records.Add(new NameSubrecord(formId, i, true));
        }
    }

    private static void TryAddNameSubrecordWithOffset(byte[] data, int i, int dataLength, long baseOffset,
        List<NameSubrecord> records)
    {
        if (i + 10 > dataLength)
        {
            return;
        }

        var len = BinaryUtils.ReadUInt16LE(data, i + 4);
        if (len != 4)
        {
            return;
        }

        // Try little-endian first
        var formId = BinaryUtils.ReadUInt32LE(data, i + 6);
        if (IsValidFormId(formId))
        {
            records.Add(new NameSubrecord(formId, baseOffset + i, false));
            return;
        }

        // Try big-endian
        formId = BinaryUtils.ReadUInt32BE(data, i + 6);
        if (IsValidFormId(formId))
        {
            records.Add(new NameSubrecord(formId, baseOffset + i, true));
        }
    }

    private static void TryAddPositionSubrecord(byte[] data, int i, int dataLength, List<PositionSubrecord> records)
    {
        if (i + 30 > dataLength) // 4 sig + 2 len + 24 data
        {
            return;
        }

        var len = BinaryUtils.ReadUInt16LE(data, i + 4);
        if (len != 24) // Position data is exactly 24 bytes (6 floats)
        {
            return;
        }

        // Try little-endian first
        var pos = TryParsePositionData(data, i + 6, false);
        if (pos != null)
        {
            records.Add(pos with { Offset = i });
            return;
        }

        // Try big-endian
        pos = TryParsePositionData(data, i + 6, true);
        if (pos != null)
        {
            records.Add(pos with { Offset = i });
        }
    }

    private static void TryAddPositionSubrecordWithOffset(byte[] data, int i, int dataLength, long baseOffset,
        List<PositionSubrecord> records)
    {
        if (i + 30 > dataLength)
        {
            return;
        }

        var len = BinaryUtils.ReadUInt16LE(data, i + 4);
        if (len != 24)
        {
            return;
        }

        // Try little-endian first
        var pos = TryParsePositionData(data, i + 6, false);
        if (pos != null)
        {
            records.Add(pos with { Offset = baseOffset + i });
            return;
        }

        // Try big-endian
        pos = TryParsePositionData(data, i + 6, true);
        if (pos != null)
        {
            records.Add(pos with { Offset = baseOffset + i });
        }
    }

    private static PositionSubrecord? TryParsePositionData(byte[] data, int offset, bool isBigEndian)
    {
        float x, y, z, rotX, rotY, rotZ;

        if (isBigEndian)
        {
            x = BinaryUtils.ReadFloatBE(data, offset);
            y = BinaryUtils.ReadFloatBE(data, offset + 4);
            z = BinaryUtils.ReadFloatBE(data, offset + 8);
            rotX = BinaryUtils.ReadFloatBE(data, offset + 12);
            rotY = BinaryUtils.ReadFloatBE(data, offset + 16);
            rotZ = BinaryUtils.ReadFloatBE(data, offset + 20);
        }
        else
        {
            x = BinaryUtils.ReadFloatLE(data, offset);
            y = BinaryUtils.ReadFloatLE(data, offset + 4);
            z = BinaryUtils.ReadFloatLE(data, offset + 8);
            rotX = BinaryUtils.ReadFloatLE(data, offset + 12);
            rotY = BinaryUtils.ReadFloatLE(data, offset + 16);
            rotZ = BinaryUtils.ReadFloatLE(data, offset + 20);
        }

        // Validate position values are reasonable for Fallout NV world
        // World coordinates typically range from -200000 to +200000
        // Rotation values are in radians, typically -2π to +2π
        if (!IsValidPosition(x, y, z) || !IsValidRotation(rotX, rotY, rotZ))
        {
            return null;
        }

        return new PositionSubrecord(x, y, z, rotX, rotY, rotZ, 0, isBigEndian);
    }

    private static void TryAddActorBaseSubrecord(byte[] data, int i, int dataLength, List<ActorBaseSubrecord> records)
    {
        if (i + 30 > dataLength) // 4 sig + 2 len + 24 data
        {
            return;
        }

        var len = BinaryUtils.ReadUInt16LE(data, i + 4);
        if (len != 24) // ACBS is exactly 24 bytes
        {
            return;
        }

        // Try little-endian first
        var acbs = TryParseActorBaseData(data, i + 6, i, false);
        if (acbs != null)
        {
            records.Add(acbs);
            return;
        }

        // Try big-endian
        acbs = TryParseActorBaseData(data, i + 6, i, true);
        if (acbs != null)
        {
            records.Add(acbs);
        }
    }

    private static void TryAddActorBaseSubrecordWithOffset(byte[] data, int i, int dataLength, long baseOffset,
        List<ActorBaseSubrecord> records)
    {
        if (i + 30 > dataLength)
        {
            return;
        }

        var len = BinaryUtils.ReadUInt16LE(data, i + 4);
        if (len != 24)
        {
            return;
        }

        // Try little-endian first
        var acbs = TryParseActorBaseData(data, i + 6, baseOffset + i, false);
        if (acbs != null)
        {
            records.Add(acbs);
            return;
        }

        // Try big-endian
        acbs = TryParseActorBaseData(data, i + 6, baseOffset + i, true);
        if (acbs != null)
        {
            records.Add(acbs);
        }
    }

    private static ActorBaseSubrecord? TryParseActorBaseData(byte[] data, int offset, long recordOffset,
        bool isBigEndian)
    {
        uint flags;
        ushort fatigueBase, barterGold, calcMin, calcMax, speedMultiplier, templateFlags;
        short level, dispositionBase;
        float karmaAlignment;

        if (isBigEndian)
        {
            flags = BinaryUtils.ReadUInt32BE(data, offset);
            fatigueBase = BinaryUtils.ReadUInt16BE(data, offset + 4);
            barterGold = BinaryUtils.ReadUInt16BE(data, offset + 6);
            level = (short)BinaryUtils.ReadUInt16BE(data, offset + 8);
            calcMin = BinaryUtils.ReadUInt16BE(data, offset + 10);
            calcMax = BinaryUtils.ReadUInt16BE(data, offset + 12);
            speedMultiplier = BinaryUtils.ReadUInt16BE(data, offset + 14);
            karmaAlignment = BinaryUtils.ReadFloatBE(data, offset + 16);
            dispositionBase = (short)BinaryUtils.ReadUInt16BE(data, offset + 20);
            templateFlags = BinaryUtils.ReadUInt16BE(data, offset + 22);
        }
        else
        {
            flags = BinaryUtils.ReadUInt32LE(data, offset);
            fatigueBase = BinaryUtils.ReadUInt16LE(data, offset + 4);
            barterGold = BinaryUtils.ReadUInt16LE(data, offset + 6);
            level = (short)BinaryUtils.ReadUInt16LE(data, offset + 8);
            calcMin = BinaryUtils.ReadUInt16LE(data, offset + 10);
            calcMax = BinaryUtils.ReadUInt16LE(data, offset + 12);
            speedMultiplier = BinaryUtils.ReadUInt16LE(data, offset + 14);
            karmaAlignment = BinaryUtils.ReadFloatLE(data, offset + 16);
            dispositionBase = (short)BinaryUtils.ReadUInt16LE(data, offset + 20);
            templateFlags = BinaryUtils.ReadUInt16LE(data, offset + 22);
        }

        // Validate actor base data
        if (!IsValidActorBaseData(flags, fatigueBase, level, speedMultiplier, karmaAlignment))
        {
            return null;
        }

        return new ActorBaseSubrecord(flags, fatigueBase, barterGold, level, calcMin, calcMax,
            speedMultiplier, karmaAlignment, dispositionBase, templateFlags, recordOffset, isBigEndian);
    }

    private static bool IsValidFormId(uint formId)
    {
        // FormID should not be 0 or 0xFFFFFFFF
        if (formId == 0 || formId == 0xFFFFFFFF)
        {
            return false;
        }

        // Plugin index should be reasonable (0x00-0xFF, typically 0x00-0x0F)
        var pluginIndex = formId >> 24;
        return pluginIndex <= 0xFF;
    }

    private static bool IsValidPosition(float x, float y, float z)
    {
        // Fallout NV world coordinates are typically in range -300000 to +300000
        const float maxCoord = 500000f;

        if (float.IsNaN(x) || float.IsInfinity(x) || Math.Abs(x) > maxCoord)
        {
            return false;
        }

        if (float.IsNaN(y) || float.IsInfinity(y) || Math.Abs(y) > maxCoord)
        {
            return false;
        }

        if (float.IsNaN(z) || float.IsInfinity(z) || Math.Abs(z) > maxCoord)
        {
            return false;
        }

        return true;
    }

    private static bool IsValidRotation(float rotX, float rotY, float rotZ)
    {
        // Rotation values in radians, typically -2π to +2π, but allow some margin
        const float maxRot = 10f;

        if (float.IsNaN(rotX) || float.IsInfinity(rotX) || Math.Abs(rotX) > maxRot)
        {
            return false;
        }

        if (float.IsNaN(rotY) || float.IsInfinity(rotY) || Math.Abs(rotY) > maxRot)
        {
            return false;
        }

        if (float.IsNaN(rotZ) || float.IsInfinity(rotZ) || Math.Abs(rotZ) > maxRot)
        {
            return false;
        }

        return true;
    }

    private static bool IsValidActorBaseData(uint flags, ushort fatigueBase, short level, ushort speedMultiplier,
        float karmaAlignment)
    {
        // Validate flags - some bits should not be set
        if ((flags & 0xFFF00000) != 0)
        {
            return false;
        }

        // Fatigue base should be reasonable (0-1000)
        if (fatigueBase > 1000)
        {
            return false;
        }

        // Level should be reasonable (-128 to 255 for leveled, 1-100 for fixed)
        if (level < -128 || level > 255)
        {
            return false;
        }

        // Speed multiplier should be reasonable (0-500)
        if (speedMultiplier > 500)
        {
            return false;
        }

        // Karma alignment is a float -1.0 to +1.0
        if (float.IsNaN(karmaAlignment) || float.IsInfinity(karmaAlignment) ||
            karmaAlignment < -2.0f || karmaAlignment > 2.0f)
        {
            return false;
        }

        return true;
    }

    #endregion

    #region INFO Subrecord Detection

    private static void TryAddResponseTextSubrecord(byte[] data, int i, int dataLength,
        List<ResponseTextSubrecord> records)
    {
        if (i + 6 > dataLength)
        {
            return;
        }

        // Try little-endian first (PC format), then big-endian (Xbox 360 format)
        var lenLe = BinaryUtils.ReadUInt16LE(data, i + 4);
        var lenBe = BinaryUtils.ReadUInt16BE(data, i + 4);

        ushort len;
        if (lenLe > 0 && lenLe <= 2048 && i + 6 + lenLe <= dataLength)
        {
            len = lenLe;
        }
        else if (lenBe > 0 && lenBe <= 2048 && i + 6 + lenBe <= dataLength)
        {
            len = lenBe;
        }
        else
        {
            return;
        }

        // Extract null-terminated string
        var text = ReadNullTermString(data, i + 6, len);
        if (IsValidDialogueText(text))
        {
            records.Add(new ResponseTextSubrecord(text, i));
        }
    }

    private static void TryAddResponseTextSubrecordWithOffset(byte[] data, int i, int dataLength, long baseOffset,
        List<ResponseTextSubrecord> records)
    {
        if (i + 6 > dataLength)
        {
            return;
        }

        // Try little-endian first (PC format), then big-endian (Xbox 360 format)
        var lenLe = BinaryUtils.ReadUInt16LE(data, i + 4);
        var lenBe = BinaryUtils.ReadUInt16BE(data, i + 4);

        ushort len;
        if (lenLe > 0 && lenLe <= 2048 && i + 6 + lenLe <= dataLength)
        {
            len = lenLe;
        }
        else if (lenBe > 0 && lenBe <= 2048 && i + 6 + lenBe <= dataLength)
        {
            len = lenBe;
        }
        else
        {
            return;
        }

        var text = ReadNullTermString(data, i + 6, len);
        if (IsValidDialogueText(text))
        {
            records.Add(new ResponseTextSubrecord(text, baseOffset + i));
        }
    }

    private static void TryAddResponseDataSubrecord(byte[] data, int i, int dataLength,
        List<ResponseDataSubrecord> records)
    {
        // TRDT is 20 bytes: emotionType(4) + emotionValue(4) + unused(4) + responseNumber(1) + unused(3) + soundFile(4)
        if (i + 26 > dataLength) // 4 sig + 2 len + 20 data
        {
            return;
        }

        // Check both endianness for length (20 = 0x0014)
        var lenLe = BinaryUtils.ReadUInt16LE(data, i + 4);
        var lenBe = BinaryUtils.ReadUInt16BE(data, i + 4);
        if (lenLe != 20 && lenBe != 20)
        {
            return;
        }

        var trdt = TryParseResponseData(data, i + 6, i);
        if (trdt != null)
        {
            records.Add(trdt);
        }
    }

    private static void TryAddResponseDataSubrecordWithOffset(byte[] data, int i, int dataLength, long baseOffset,
        List<ResponseDataSubrecord> records)
    {
        if (i + 26 > dataLength)
        {
            return;
        }

        // Check both endianness for length (20 = 0x0014)
        var lenLe = BinaryUtils.ReadUInt16LE(data, i + 4);
        var lenBe = BinaryUtils.ReadUInt16BE(data, i + 4);
        if (lenLe != 20 && lenBe != 20)
        {
            return;
        }

        var trdt = TryParseResponseData(data, i + 6, baseOffset + i);
        if (trdt != null)
        {
            records.Add(trdt);
        }
    }

    private static ResponseDataSubrecord? TryParseResponseData(byte[] data, int offset, long recordOffset)
    {
        // Try little-endian first (more common in memory)
        var emotionType = BinaryUtils.ReadUInt32LE(data, offset);
        var emotionValue = BinaryUtils.ReadInt32LE(data, offset + 4);
        var responseNumber = data[offset + 12];

        // Validate emotion type (0-8 are valid emotion types in Fallout NV)
        if (emotionType <= 8 && emotionValue >= -100 && emotionValue <= 100)
        {
            return new ResponseDataSubrecord(emotionType, emotionValue, responseNumber, recordOffset);
        }

        // Try big-endian
        emotionType = BinaryUtils.ReadUInt32BE(data, offset);
        emotionValue = (int)BinaryUtils.ReadUInt32BE(data, offset + 4);

        if (emotionType <= 8 && emotionValue >= -100 && emotionValue <= 100)
        {
            return new ResponseDataSubrecord(emotionType, emotionValue, responseNumber, recordOffset);
        }

        return null;
    }

    private static bool IsValidDialogueText(string text)
    {
        // For debugging: accept any non-empty text
        return !string.IsNullOrEmpty(text) && text.Length >= 2;
    }

    #endregion

    #region Generic Subrecord Detection

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

    /// <summary>
    ///     Add a text subrecord (FULL, DESC, etc.) - legacy version.
    /// </summary>
    private static void TryAddTextSubrecord(byte[] data, int i, int dataLength,
        string subrecordType, List<TextSubrecord> records)
    {
        if (i + 6 > dataLength)
        {
            return;
        }

        var len = GetSubrecordLength(data, i + 4, dataLength - i - 6);
        if (len == 0 || len > 512 || i + 6 + len > dataLength)
        {
            return;
        }

        var text = ReadNullTermString(data, i + 6, len);
        if (IsValidDisplayText(text))
        {
            records.Add(new TextSubrecord(subrecordType, text, i));
        }
    }

    /// <summary>
    ///     Add a text subrecord with offset.
    /// </summary>
    private static void TryAddTextSubrecordWithOffset(byte[] data, int i, int dataLength, long baseOffset,
        string subrecordType, List<TextSubrecord> records)
    {
        if (i + 6 > dataLength)
        {
            return;
        }

        var len = GetSubrecordLength(data, i + 4, dataLength - i - 6);
        if (len == 0 || len > 512 || i + 6 + len > dataLength)
        {
            return;
        }

        var text = ReadNullTermString(data, i + 6, len);
        if (IsValidDisplayText(text))
        {
            records.Add(new TextSubrecord(subrecordType, text, baseOffset + i));
        }
    }

    /// <summary>
    ///     Add a path subrecord (MODL, ICON, TX00, etc.) - legacy version.
    /// </summary>
    private static void TryAddPathSubrecord(byte[] data, int i, int dataLength,
        string subrecordType, List<TextSubrecord> records)
    {
        if (i + 6 > dataLength)
        {
            return;
        }

        var len = GetSubrecordLength(data, i + 4, dataLength - i - 6);
        if (len == 0 || len > 260 || i + 6 + len > dataLength)
        {
            return;
        }

        var text = ReadNullTermString(data, i + 6, len);
        if (IsValidPath(text))
        {
            records.Add(new TextSubrecord(subrecordType, text, i));
        }
    }

    /// <summary>
    ///     Add a path subrecord with offset.
    /// </summary>
    private static void TryAddPathSubrecordWithOffset(byte[] data, int i, int dataLength, long baseOffset,
        string subrecordType, List<TextSubrecord> records)
    {
        if (i + 6 > dataLength)
        {
            return;
        }

        var len = GetSubrecordLength(data, i + 4, dataLength - i - 6);
        if (len == 0 || len > 260 || i + 6 + len > dataLength)
        {
            return;
        }

        var text = ReadNullTermString(data, i + 6, len);
        if (IsValidPath(text))
        {
            records.Add(new TextSubrecord(subrecordType, text, baseOffset + i));
        }
    }

    /// <summary>
    ///     Add a FormID reference subrecord - legacy version.
    /// </summary>
    private static void TryAddFormIdSubrecord(byte[] data, int i, int dataLength,
        string subrecordType, List<FormIdSubrecord> records)
    {
        if (i + 10 > dataLength)
        {
            return;
        }

        var len = GetSubrecordLength(data, i + 4, 4);
        if (len != 4)
        {
            return;
        }

        var formId = GetFormId(data, i + 6);
        if (IsValidFormId(formId))
        {
            records.Add(new FormIdSubrecord(subrecordType, formId, i));
        }
    }

    /// <summary>
    ///     Add a FormID reference subrecord with offset.
    /// </summary>
    private static void TryAddFormIdSubrecordWithOffset(byte[] data, int i, int dataLength, long baseOffset,
        string subrecordType, List<FormIdSubrecord> records)
    {
        if (i + 10 > dataLength)
        {
            return;
        }

        var len = GetSubrecordLength(data, i + 4, 4);
        if (len != 4)
        {
            return;
        }

        var formId = GetFormId(data, i + 6);
        if (IsValidFormId(formId))
        {
            records.Add(new FormIdSubrecord(subrecordType, formId, baseOffset + i));
        }
    }

    /// <summary>
    ///     Add a condition subrecord (CTDA) - legacy version.
    /// </summary>
    private static void TryAddConditionSubrecord(byte[] data, int i, int dataLength,
        List<ConditionSubrecord> records)
    {
        // CTDA is typically 24 or 28 bytes
        if (i + 30 > dataLength)
        {
            return;
        }

        var len = GetSubrecordLength(data, i + 4, 28);
        if (len != 24 && len != 28)
        {
            return;
        }

        var condition = TryParseCondition(data, i + 6, i);
        if (condition != null)
        {
            records.Add(condition);
        }
    }

    /// <summary>
    ///     Add a condition subrecord with offset.
    /// </summary>
    private static void TryAddConditionSubrecordWithOffset(byte[] data, int i, int dataLength, long baseOffset,
        List<ConditionSubrecord> records)
    {
        if (i + 30 > dataLength)
        {
            return;
        }

        var len = GetSubrecordLength(data, i + 4, 28);
        if (len != 24 && len != 28)
        {
            return;
        }

        var condition = TryParseCondition(data, i + 6, baseOffset + i);
        if (condition != null)
        {
            records.Add(condition);
        }
    }

    /// <summary>
    ///     Try to add a VHGT heightmap subrecord.
    ///     VHGT structure: 4 bytes signature + 2 bytes size + 4 bytes HeightOffset + 1089 bytes deltas + 3 bytes padding
    /// </summary>
    private static void TryAddVhgtHeightmapWithOffset(byte[] data, int i, int dataLength, long baseOffset,
        bool isBigEndian, List<DetectedVhgtHeightmap> records)
    {
        // VHGT subrecord: sig(4) + size(2) + heightOffset(4) + deltas(1089) + padding(3) = 1102 bytes total
        // The size field should be 1096 (4 + 1089 + 3)
        if (i + 6 + 1096 > dataLength)
        {
            return;
        }

        // Read and validate size field
        var size = isBigEndian
            ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(i + 4))
            : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(i + 4));

        if (size != 1096)
        {
            return;
        }

        // Read height offset (float at offset 6)
        var heightOffset = isBigEndian
            ? BinaryPrimitives.ReadSingleBigEndian(data.AsSpan(i + 6))
            : BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(i + 6));

        // Validate height offset is reasonable (terrain heights typically -10000 to +10000)
        if (float.IsNaN(heightOffset) || float.IsInfinity(heightOffset) ||
            heightOffset < -100000 || heightOffset > 100000)
        {
            return;
        }

        // Read height deltas (1089 bytes starting at offset 10)
        var deltas = new sbyte[1089];
        for (var j = 0; j < 1089; j++)
        {
            deltas[j] = (sbyte)data[i + 10 + j];
        }

        // Validate deltas - they should have some variation but not be all zeros or extreme
        var hasVariation = false;
        var allZero = true;
        for (var j = 0; j < 100 && !hasVariation; j++) // Check first 100 values
        {
            if (deltas[j] != 0)
            {
                allZero = false;
            }

            if (j > 0 && deltas[j] != deltas[j - 1])
            {
                hasVariation = true;
            }
        }

        // Skip if all zeros (likely not real heightmap data)
        if (allZero)
        {
            return;
        }

        records.Add(new DetectedVhgtHeightmap
        {
            Offset = baseOffset + i,
            IsBigEndian = isBigEndian,
            HeightOffset = heightOffset,
            HeightDeltas = deltas
        });
    }

    /// <summary>
    ///     Try to add an XCLC cell grid subrecord.
    ///     XCLC structure: 4 bytes signature + 2 bytes size + 4 bytes X + 4 bytes Y + 1 byte flags + 3 bytes padding
    /// </summary>
    private static void TryAddXclcSubrecordWithOffset(byte[] data, int i, int dataLength, long baseOffset,
        bool isBigEndian, List<CellGridSubrecord> records)
    {
        // XCLC subrecord: sig(4) + size(2) + X(4) + Y(4) + flags(1) + padding(3) = 18 bytes total
        // The size field should be 12 (4 + 4 + 1 + 3)
        if (i + 6 + 12 > dataLength)
        {
            return;
        }

        // Read and validate size field
        var size = isBigEndian
            ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(i + 4))
            : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(i + 4));

        if (size != 12)
        {
            return;
        }

        // Read grid coordinates
        var gridX = isBigEndian
            ? BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(i + 6))
            : BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(i + 6));

        var gridY = isBigEndian
            ? BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(i + 10))
            : BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(i + 10));

        var landFlags = data[i + 14];

        // Validate coordinates are reasonable (Fallout NV worldspace is roughly -50 to +50)
        if (gridX < -200 || gridX > 200 || gridY < -200 || gridY > 200)
        {
            return;
        }

        records.Add(new CellGridSubrecord
        {
            GridX = gridX,
            GridY = gridY,
            LandFlags = landFlags,
            Offset = baseOffset + i,
            IsBigEndian = isBigEndian
        });
    }

    /// <summary>
    ///     Try to add a generic schema-defined subrecord.
    ///     Detects any subrecord signature defined in SubrecordSchemaRegistry.
    /// </summary>
    private static void TryAddGenericSubrecordWithOffset(byte[] data, int i, int dataLength, long baseOffset,
        List<DetectedSubrecord> records)
    {
        // Need at least 6 bytes for signature + size
        if (i + 6 > dataLength)
        {
            return;
        }

        // Check if bytes are valid ASCII (all uppercase letters or numbers)
        if (!IsValidSignatureChar(data[i]) || !IsValidSignatureChar(data[i + 1]) ||
            !IsValidSignatureChar(data[i + 2]) || !IsValidSignatureChar(data[i + 3]))
        {
            return;
        }

        // Read the 4-byte signature as a string
        var sigBytes = data.AsSpan(i, 4);
        var signature = Encoding.ASCII.GetString(sigBytes);

        // Check if this is a schema-defined signature
        bool isBigEndian;
        string normalizedSig;

        if (SchemaSignaturesLE.Value.Contains(signature))
        {
            isBigEndian = false;
            normalizedSig = signature;
        }
        else if (SchemaSignaturesBE.Value.Contains(signature))
        {
            isBigEndian = true;
            normalizedSig = SubrecordSchemaRegistry.GetReversedSignature(signature);
        }
        else
        {
            return; // Not a known schema signature
        }

        // Skip if already handled by specific detection
        if (SpecificSignatures.Contains(normalizedSig))
        {
            return;
        }

        // Read and validate size field
        var size = isBigEndian
            ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(i + 4))
            : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(i + 4));

        // Size must be reasonable (1-8192 bytes typical for most subrecords)
        if (size == 0 || size > 8192)
        {
            return;
        }

        // Verify we have enough data
        if (i + 6 + size > dataLength)
        {
            return;
        }

        // Extract raw data for potential further processing
        var rawData = new byte[size];
        Array.Copy(data, i + 6, rawData, 0, size);

        records.Add(new DetectedSubrecord
        {
            Signature = normalizedSig,
            Offset = baseOffset + i,
            DataSize = size,
            IsBigEndian = isBigEndian,
            RawData = rawData
        });
    }

    /// <summary>
    ///     Check if a byte is a valid signature character (A-Z, 0-9, underscore).
    /// </summary>
    private static bool IsValidSignatureChar(byte b)
    {
        return (b >= 'A' && b <= 'Z') || (b >= '0' && b <= '9') || b == '_';
    }

    /// <summary>
    ///     Get subrecord length, trying both endianness.
    /// </summary>
    private static ushort GetSubrecordLength(byte[] data, int offset, int maxLen)
    {
        var lenLe = BinaryUtils.ReadUInt16LE(data, offset);
        var lenBe = BinaryUtils.ReadUInt16BE(data, offset);

        // Prefer LE if it's valid
        if (lenLe > 0 && lenLe <= maxLen)
        {
            return lenLe;
        }

        if (lenBe > 0 && lenBe <= maxLen)
        {
            return lenBe;
        }

        return 0;
    }

    /// <summary>
    ///     Get FormID, trying both endianness.
    /// </summary>
    private static uint GetFormId(byte[] data, int offset)
    {
        var formIdLe = BinaryUtils.ReadUInt32LE(data, offset);
        var formIdBe = BinaryUtils.ReadUInt32BE(data, offset);

        // Valid FormIDs have plugin index 0x00-0x0F
        if (formIdLe >> 24 <= 0x0F && formIdLe != 0)
        {
            return formIdLe;
        }

        if (formIdBe >> 24 <= 0x0F && formIdBe != 0)
        {
            return formIdBe;
        }

        return 0;
    }

    /// <summary>
    ///     Validate display text (FULL, DESC).
    /// </summary>
    private static bool IsValidDisplayText(string text)
    {
        if (string.IsNullOrEmpty(text) || text.Length < 2)
        {
            return false;
        }

        // Should be mostly printable characters
        var printable = text.Count(c => c >= 32 && c < 127);
        return (double)printable / text.Length >= 0.8;
    }

    /// <summary>
    ///     Validate file path (MODL, ICON, TX00).
    /// </summary>
    private static bool IsValidPath(string text)
    {
        if (string.IsNullOrEmpty(text) || text.Length < 4)
        {
            return false;
        }

        // Should look like a file path (contains \ or / and an extension)
        return (text.Contains('\\') || text.Contains('/')) &&
               text.Contains('.') &&
               !text.Contains('\0');
    }

    /// <summary>
    ///     Parse a CTDA condition subrecord.
    /// </summary>
    private static ConditionSubrecord? TryParseCondition(byte[] data, int offset, long recordOffset)
    {
        var type = data[offset];
        var op = (byte)((type >> 5) & 0x07);
        var condType = (byte)(type & 0x1F);

        // Read comparison value (float)
        var compValue = BinaryUtils.ReadFloatLE(data, offset + 4);

        // Check for reasonable float value
        if (float.IsNaN(compValue) || float.IsInfinity(compValue))
        {
            compValue = BinaryUtils.ReadFloatBE(data, offset + 4);
            if (float.IsNaN(compValue) || float.IsInfinity(compValue))
            {
                return null;
            }
        }

        // Read function index
        var funcIndexLe = BinaryUtils.ReadUInt16LE(data, offset + 8);
        var funcIndexBe = BinaryUtils.ReadUInt16BE(data, offset + 8);

        // Valid function indices are typically 0-500+ in Fallout NV
        ushort funcIndex;
        if (funcIndexLe <= 1000)
        {
            funcIndex = funcIndexLe;
        }
        else if (funcIndexBe <= 1000)
        {
            funcIndex = funcIndexBe;
        }
        else
        {
            return null;
        }

        // Read parameters
        var param1 = GetFormId(data, offset + 12);
        var param2 = GetFormId(data, offset + 16);

        return new ConditionSubrecord(condType, op, compValue, funcIndex, param1, param2, recordOffset);
    }

    #endregion

    #region Asset String Pool Detection

    /// <summary>
    ///     Scan for runtime asset string pools in the memory dump.
    ///     These are contiguous regions of null-terminated path strings used by the asset loader.
    /// </summary>
    /// <param name="accessor">Memory-mapped file accessor.</param>
    /// <param name="fileSize">Total file size in bytes.</param>
    /// <param name="scanResult">The scan result to add detected assets to.</param>
    public static void ScanForAssetStrings(
        MemoryMappedViewAccessor accessor,
        long fileSize,
        EsmRecordScanResult scanResult,
        bool verbose = false)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        const int chunkSize = 4 * 1024 * 1024; // 4MB chunks
        const int minStringLength = 8; // Minimum path length (e.g., "a/b.nif")
        const int maxStringLength = 260; // MAX_PATH
        const int maxAssetStrings = 100000; // Limit to prevent runaway

        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var buffer = ArrayPool<byte>.Shared.Rent(chunkSize);
        var lastProgressMb = 0L;

        var log = Logger.Instance;
        log.Debug("AssetStrings: Starting scan of {0:N0} MB", fileSize / (1024 * 1024));

        try
        {
            long offset = 0;
            while (offset < fileSize && scanResult.AssetStrings.Count < maxAssetStrings)
            {
                var toRead = (int)Math.Min(chunkSize, fileSize - offset);
                accessor.ReadArray(offset, buffer, 0, toRead);

                // Scan for null-terminated strings that look like asset paths
                var i = 0;
                while (i < toRead - minStringLength && scanResult.AssetStrings.Count < maxAssetStrings)
                {
                    // Look for strings that start with printable ASCII
                    if (!IsPathStartChar(buffer[i]))
                    {
                        i++;
                        continue;
                    }

                    // Find the end of this potential string (null terminator)
                    var stringEnd = FindStringEnd(buffer, i, Math.Min(i + maxStringLength, toRead));
                    if (stringEnd < 0)
                    {
                        i++;
                        continue;
                    }

                    var stringLength = stringEnd - i;
                    if (stringLength < minStringLength)
                    {
                        i = stringEnd + 1;
                        continue;
                    }

                    // Extract the string and check if it's a valid path
                    var path = Encoding.ASCII.GetString(buffer, i, stringLength);
                    if (IsAssetPath(path) && seenPaths.Add(path))
                    {
                        var category = CategorizeAssetPath(path);
                        scanResult.AssetStrings.Add(new DetectedAssetString
                        {
                            Path = path,
                            Offset = offset + i,
                            Category = category
                        });
                    }

                    i = stringEnd + 1;
                }

                offset += toRead;

                // Progress every 100MB
                if (offset / (100 * 1024 * 1024) > lastProgressMb)
                {
                    lastProgressMb = offset / (100 * 1024 * 1024);
                    log.Debug("AssetStrings:   {0:N0} MB scanned, {1:N0} unique paths found ({2:N0} ms)",
                        offset / (1024 * 1024), scanResult.AssetStrings.Count, sw.ElapsedMilliseconds);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        sw.Stop();
        log.Debug("AssetStrings: Complete: {0:N0} unique asset paths in {1:N0} ms",
            scanResult.AssetStrings.Count, sw.ElapsedMilliseconds);
    }

    /// <summary>
    ///     Check if a character is a valid start for a path string.
    /// </summary>
    private static bool IsPathStartChar(byte b)
    {
        // Paths typically start with: a-z, A-Z, ., \, /
        return (b >= 'a' && b <= 'z') ||
               (b >= 'A' && b <= 'Z') ||
               b == '.' || b == '\\' || b == '/';
    }

    /// <summary>
    ///     Find the end of a null-terminated string.
    /// </summary>
    private static int FindStringEnd(byte[] buffer, int start, int maxEnd)
    {
        for (var i = start; i < maxEnd; i++)
        {
            if (buffer[i] == 0)
            {
                return i;
            }

            // Stop at non-printable characters (except path separators)
            if (buffer[i] < 0x20 || buffer[i] > 0x7E)
            {
                return -1;
            }
        }

        return -1; // No null terminator found
    }

    /// <summary>
    ///     Check if a string looks like a valid asset path.
    /// </summary>
    private static bool IsAssetPath(string path)
    {
        // Must contain a path separator or start with a known folder
        if (!path.Contains('\\') && !path.Contains('/'))
        {
            return false;
        }

        // Must have a file extension
        var lastDot = path.LastIndexOf('.');
        if (lastDot < 0 || lastDot >= path.Length - 1)
        {
            return false;
        }

        var extension = path[(lastDot + 1)..].ToLowerInvariant();

        // Check for known game asset extensions
        return extension is
            "nif" or "kf" or "hkx" or  // Models/animations
            "dds" or "ddx" or "tga" or "bmp" or // Textures
            "wav" or "mp3" or "ogg" or "lip" or // Sound/lipsync
            "psc" or "pex" or // Scripts
            "egm" or "egt" or "tri" or // FaceGen
            "spt" or "txt" or "xml" or // Misc data
            "esm" or "esp"; // Plugin files (references)
    }

    /// <summary>
    ///     Categorize an asset path by its extension.
    /// </summary>
    private static AssetCategory CategorizeAssetPath(string path)
    {
        var lastDot = path.LastIndexOf('.');
        if (lastDot < 0)
        {
            return AssetCategory.Other;
        }

        var extension = path[(lastDot + 1)..].ToLowerInvariant();

        return extension switch
        {
            "nif" or "egm" or "egt" or "tri" => AssetCategory.Model,
            "dds" or "ddx" or "tga" or "bmp" => AssetCategory.Texture,
            "wav" or "mp3" or "ogg" or "lip" => AssetCategory.Sound,
            "psc" or "pex" => AssetCategory.Script,
            "kf" or "hkx" => AssetCategory.Animation,
            _ => AssetCategory.Other
        };
    }

    #endregion

    #region Runtime EditorID Hash Table Extraction

    /// <summary>
    ///     Extract runtime Editor IDs from the game's EditorID hash table by following TESForm pointers.
    ///     This requires MinidumpInfo to convert Xbox virtual addresses to file offsets.
    /// </summary>
    /// <param name="accessor">Memory-mapped file accessor.</param>
    /// <param name="fileSize">Total file size in bytes.</param>
    /// <param name="minidumpInfo">Minidump info with memory region mappings.</param>
    /// <param name="scanResult">The scan result to add extracted Editor IDs to.</param>
    public static void ExtractRuntimeEditorIds(
        MemoryMappedViewAccessor accessor,
        long fileSize,
        Minidump.MinidumpInfo? minidumpInfo,
        EsmRecordScanResult scanResult,
        bool verbose = false)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var log = Logger.Instance;

        log.Debug("EditorIDs: Starting extraction...");

        if (minidumpInfo == null || minidumpInfo.MemoryRegions.Count == 0)
        {
            log.Debug("EditorIDs: No minidump info - skipping");
            return;
        }

        const int minEditorIdLength = 4; // Minimum meaningful EditorID
        const int maxEditorIdLength = 128;

        var seenEditorIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var buffer = ArrayPool<byte>.Shared.Rent(256 * 1024); // 256KB buffer for reading
        var tesFormBuffer = new byte[24]; // TESForm is 24 bytes
        long totalScanned = 0;
        int regionsProcessed = 0;
        int pointersFollowed = 0;
        int pointersResolved = 0;

        try
        {
            // Scan ALL memory regions for Editor ID string patterns
            var regionsToScan = minidumpInfo.MemoryRegions
                .OrderByDescending(r => r.Size) // Largest first for better progress indication
                .ToList();

            log.Debug("EditorIDs: Scanning all {0} regions", regionsToScan.Count);

            foreach (var region in regionsToScan)
            {
                regionsProcessed++;
                var regionStartCount = scanResult.RuntimeEditorIds.Count;

                // Process region in chunks
                var regionOffset = region.FileOffset;
                var regionEnd = region.FileOffset + region.Size;

                while (regionOffset < regionEnd)
                {
                    var toRead = (int)Math.Min(buffer.Length, regionEnd - regionOffset);
                    accessor.ReadArray(regionOffset, buffer, 0, toRead);

                    // Scan for Editor ID patterns
                    var i = 0;
                    while (i < toRead - maxEditorIdLength - 8)
                    {
                        // Look for valid Editor ID string followed by null terminator
                        if (!IsEditorIdStartChar(buffer[i]))
                        {
                            i++;
                            continue;
                        }

                        var stringEnd = FindEditorIdEnd(buffer, i, Math.Min(i + maxEditorIdLength, toRead));
                        if (stringEnd < 0)
                        {
                            i++;
                            continue;
                        }

                        var stringLength = stringEnd - i;
                        if (stringLength < minEditorIdLength)
                        {
                            i = stringEnd + 1;
                            continue;
                        }

                        var editorId = Encoding.ASCII.GetString(buffer, i, stringLength);

                        // Validate as Editor ID (alphanumeric + underscore, starts with letter)
                        if (!IsValidEditorId(editorId) || !seenEditorIds.Add(editorId))
                        {
                            i = stringEnd + 1;
                            continue;
                        }

                        // Create entry without pointer following for speed
                        var entry = new RuntimeEditorIdEntry
                        {
                            EditorId = editorId,
                            StringOffset = regionOffset + i
                        };

                        // Only try pointer following for every 10th EditorID to limit overhead
                        if (scanResult.RuntimeEditorIds.Count % 10 == 0)
                        {
                            pointersFollowed++;
                            var tesFormInfo = TryFollowNearbyTesFormPointer(
                                buffer, i, stringEnd, toRead,
                                regionOffset, accessor, minidumpInfo, tesFormBuffer);

                            if (tesFormInfo.HasValue)
                            {
                                pointersResolved++;
                                entry = entry with
                                {
                                    FormId = tesFormInfo.Value.formId,
                                    FormType = tesFormInfo.Value.formType,
                                    TesFormOffset = tesFormInfo.Value.fileOffset,
                                    TesFormPointer = tesFormInfo.Value.pointer
                                };
                            }
                        }

                        scanResult.RuntimeEditorIds.Add(entry);
                        i = stringEnd + 1;
                    }

                    totalScanned += toRead;
                    regionOffset += toRead;
                }

                totalScanned += region.Size;

                // Log progress per region if finding many IDs
                var regionCount = scanResult.RuntimeEditorIds.Count - regionStartCount;
                if (regionCount > 100)
                {
                    log.Debug("EditorIDs:   Region {0}: VA 0x{1:X8}, {2:N0} KB -> {3:N0} IDs",
                        regionsProcessed, region.VirtualAddress, region.Size / 1024, regionCount);
                }

                // Progress report every 500 regions
                if (regionsProcessed % 500 == 0)
                {
                    log.Debug("EditorIDs:   Progress: {0}/{1} regions, {2:N0} MB scanned, {3:N0} IDs found",
                        regionsProcessed, regionsToScan.Count, totalScanned / (1024 * 1024),
                        scanResult.RuntimeEditorIds.Count);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        sw.Stop();
        log.Debug("EditorIDs: Complete: {0:N0} unique EditorIDs in {1:N0} ms",
            scanResult.RuntimeEditorIds.Count, sw.ElapsedMilliseconds);
        log.Debug("EditorIDs:   Scanned {0:N0} MB across {1} regions",
            totalScanned / (1024 * 1024), regionsProcessed);
        log.Debug("EditorIDs:   Pointer following: {0} attempts, {1} resolved ({2:F1}%)",
            pointersFollowed, pointersResolved, 100.0 * pointersResolved / Math.Max(1, pointersFollowed));
    }

    /// <summary>
    ///     Check if a byte is a valid starting character for an Editor ID.
    /// </summary>
    private static bool IsEditorIdStartChar(byte b)
    {
        // Editor IDs start with a letter
        return (b >= 'A' && b <= 'Z') || (b >= 'a' && b <= 'z');
    }

    /// <summary>
    ///     Find the end of an Editor ID string (null terminator).
    /// </summary>
    private static int FindEditorIdEnd(byte[] buffer, int start, int maxEnd)
    {
        for (var i = start; i < maxEnd; i++)
        {
            if (buffer[i] == 0)
            {
                return i;
            }

            // Editor IDs contain only alphanumeric and underscore
            var b = buffer[i];
            if (!((b >= 'A' && b <= 'Z') || (b >= 'a' && b <= 'z') ||
                  (b >= '0' && b <= '9') || b == '_'))
            {
                return -1;
            }
        }

        return -1;
    }

    /// <summary>
    ///     Try to find and follow a TESForm pointer near an Editor ID string.
    /// </summary>
    private static (uint formId, byte formType, long fileOffset, long pointer)? TryFollowNearbyTesFormPointer(
        byte[] buffer,
        int stringStart,
        int stringEnd,
        int bufferLength,
        long baseOffset,
        MemoryMappedViewAccessor accessor,
        Minidump.MinidumpInfo minidumpInfo,
        byte[] tesFormBuffer)
    {
        // Look for Xbox 360 pointers (0x40-0x7F range) within 32 bytes before/after string
        // Xbox 360 uses big-endian, so pointers look like: XX XX XX XX where first byte is 0x40-0x7F

        var searchStart = Math.Max(0, stringStart - 32);
        var searchEnd = Math.Min(bufferLength - 4, stringEnd + 32);

        for (var i = searchStart; i < searchEnd; i += 4) // Pointers are typically 4-byte aligned
        {
            // Read as big-endian (Xbox 360)
            var pointer = (long)BinaryUtils.ReadUInt32BE(buffer, i);

            // Check if it looks like a valid Xbox 360 pointer (0x40000000 - 0x7FFFFFFF range)
            if (pointer < 0x40000000 || pointer > 0x7FFFFFFF)
            {
                continue;
            }

            // Try to convert to file offset
            var fileOffset = minidumpInfo.VirtualAddressToFileOffset(pointer);
            if (!fileOffset.HasValue)
            {
                continue;
            }

            // Read potential TESForm at this offset
            try
            {
                accessor.ReadArray(fileOffset.Value, tesFormBuffer, 0, 24);

                // Validate TESForm structure
                // Offset 4: cFormType (should be < 200 for valid types)
                // Offset 12: iFormID (read as big-endian)
                var formType = tesFormBuffer[4];
                if (formType > 200)
                {
                    continue;
                }

                var formId = BinaryUtils.ReadUInt32BE(tesFormBuffer, 12);

                // Basic validation: FormID should not be 0 or 0xFFFFFFFF
                if (formId == 0 || formId == 0xFFFFFFFF)
                {
                    continue;
                }

                // Plugin index validation (upper byte, typically 0x00-0xFF)
                var pluginIndex = formId >> 24;
                if (pluginIndex > 0xFF)
                {
                    continue;
                }

                return (formId, formType, fileOffset.Value, pointer);
            }
            catch
            {
                // Ignore read errors
            }
        }

        return null;
    }

    #endregion
}
