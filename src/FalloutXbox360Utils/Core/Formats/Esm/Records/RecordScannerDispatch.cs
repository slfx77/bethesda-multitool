using System.Buffers.Binary;
using System.Collections.Frozen;
using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Models;

namespace FalloutXbox360Utils.Core.Formats.Esm;

/// <summary>
///     Constants, dispatch tables, and types used by the ESM record scanner.
///     Centralizes the unified dispatch table that maps 4-byte magic values to
///     bit-packed action flags for main records, subrecords, GRUPs, and textures.
/// </summary>
internal static class RecordScannerDispatch
{
    #region Signature Constants

    internal const uint SigEdid = 0x44494445;
    internal const uint SigGmst = 0x54534D47;
    internal const uint SigSctx = 0x58544353;
    internal const uint SigScro = 0x4F524353;
    internal const uint SigName = 0x454D414E;
    internal const uint SigData = 0x41544144;
    internal const uint SigAcbs = 0x53424341;
    internal const uint SigNam1 = 0x314D414E;
    internal const uint SigTrdt = 0x54445254;
    internal const uint SigFull = 0x4C4C5546;
    internal const uint SigDesc = 0x43534544;
    internal const uint SigModl = 0x4C444F4D;
    internal const uint SigIcon = 0x4E4F4349;
    internal const uint SigMico = 0x4F43494D;
    internal const uint SigScri = 0x49524353;
    internal const uint SigEnam = 0x4D414E45;
    internal const uint SigSnam = 0x4D414E53;
    internal const uint SigQnam = 0x4D414E51;
    internal const uint SigCtda = 0x41445443;
    internal const uint SigVhgt = 0x54474856;
    internal const uint SigTghv = 0x56484754;
    internal const uint SigXclc = 0x434C4358;
    internal const uint SigClcx = 0x58434C43;

    #endregion

    #region Record Type Lists

    internal static readonly string[] RuntimeRecordTypes =
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

        // Perks (already parsed, ensure scanned)
        "PERK" // Perk
    ];

    internal static readonly string[] KnownFalsePositivePatterns =
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

    #endregion

    #region Dedup and Handler Types

    internal readonly struct ScanDedup(
        HashSet<string> seenEdids,
        HashSet<uint> seenFormIds,
        HashSet<long> seenMainRecordOffsets)
    {
        public readonly HashSet<string> SeenEdids = seenEdids;
        public readonly HashSet<uint> SeenFormIds = seenFormIds;
        public readonly HashSet<long> SeenMainRecordOffsets = seenMainRecordOffsets;
    }

    internal delegate void SubrecordHandler(
        byte[] buffer, int index, int length, long offset,
        EsmRecordScanResult result, HashSet<string> seenEdids, HashSet<uint> seenFormIds);

    #endregion

    #region Unified Dispatch

    // Bit-packed action flags in a single FrozenDictionary<uint, int>.
    // One lookup per candidate position replaces 4 separate lookups.
    //   Bit 24: main record LE
    //   Bit 25: main record BE
    //   Bit 26: GRUP (LE or BE, determined by magic value)
    //   Bit 27: texture path
    //   Bits 0-7: subrecord handler index (0xFF = no subrecord handler)
    internal const int ActionMainRecordLE = 1 << 24;
    internal const int ActionMainRecordBE = 1 << 25;
    internal const int ActionGrup = 1 << 26;
    internal const int ActionTexture = 1 << 27;
    internal const int NoHandler = 0xFF;

    // GRUP magic values (structural container, not a main record -- separate validation)
    internal const uint SigGrupLE = 0x50555247; // "GRUP" as LE uint32
    internal const uint SigGrupBE = 0x47525550; // "GRUP" as BE uint32

    internal static readonly SubrecordHandler[] SubrecordHandlers =
    [
        /* 0  */ (buf, i, len, off, res, edids, _) =>
            EsmMiscDetector.TryAddEdidRecordWithOffset(buf, i, len, off, res.EditorIds, edids),
        /* 1  */ (buf, i, len, off, res, _, _) =>
            EsmMiscDetector.TryAddGmstRecordWithOffset(buf, i, len, off, res.GameSettings),
        /* 2  */ (buf, i, len, off, res, _, _) =>
            EsmMiscDetector.TryAddSctxRecordWithOffset(buf, i, len, off, res.ScriptSources),
        /* 3  */ (buf, i, len, off, res, _, fids) =>
            EsmMiscDetector.TryAddScroRecordWithOffset(buf, i, len, off, res.FormIdReferences, fids),
        /* 4  */ (buf, i, len, off, res, _, _) =>
            EsmMiscDetector.TryAddNameSubrecordWithOffset(buf, i, len, off, res.NameReferences),
        /* 5  */ (buf, i, len, off, res, _, _) =>
            EsmWorldExtractor.TryAddPositionSubrecordWithOffset(buf, i, len, off, res.Positions),
        /* 6  */ (buf, i, len, off, res, _, _) =>
            EsmActorDetector.TryAddActorBaseSubrecordWithOffset(buf, i, len, off, res.ActorBases),
        /* 7  */ (buf, i, len, off, res, _, _) =>
            EsmDialogueDetector.TryAddResponseTextSubrecordWithOffset(buf, i, len, off, res.ResponseTexts),
        /* 8  */ (buf, i, len, off, res, _, _) =>
            EsmDialogueDetector.TryAddResponseDataSubrecordWithOffset(buf, i, len, off, res.ResponseData),
        /* 9  */ (buf, i, len, off, res, _, _) =>
            EsmMiscDetector.TryAddTextSubrecordWithOffset(buf, i, len, off, "FULL", res.FullNames),
        /* 10 */ (buf, i, len, off, res, _, _) =>
            EsmMiscDetector.TryAddTextSubrecordWithOffset(buf, i, len, off, "DESC", res.Descriptions),
        /* 11 */ (buf, i, len, off, res, _, _) =>
            EsmMiscDetector.TryAddPathSubrecordWithOffset(buf, i, len, off, "MODL", res.ModelPaths),
        /* 12 */ (buf, i, len, off, res, _, _) =>
            EsmMiscDetector.TryAddPathSubrecordWithOffset(buf, i, len, off, "ICON", res.IconPaths),
        /* 13 */ (buf, i, len, off, res, _, _) =>
            EsmMiscDetector.TryAddPathSubrecordWithOffset(buf, i, len, off, "MICO", res.IconPaths),
        /* 14 */ (buf, i, len, off, res, _, _) =>
            EsmMiscDetector.TryAddFormIdSubrecordWithOffset(buf, i, len, off, "SCRI", res.ScriptRefs),
        /* 15 */ (buf, i, len, off, res, _, _) =>
            EsmMiscDetector.TryAddFormIdSubrecordWithOffset(buf, i, len, off, "ENAM", res.EffectRefs),
        /* 16 */ (buf, i, len, off, res, _, _) =>
            EsmMiscDetector.TryAddFormIdSubrecordWithOffset(buf, i, len, off, "SNAM", res.SoundRefs),
        /* 17 */ (buf, i, len, off, res, _, _) =>
            EsmMiscDetector.TryAddFormIdSubrecordWithOffset(buf, i, len, off, "QNAM", res.QuestRefs),
        /* 18 */ (buf, i, len, off, res, _, _) =>
            EsmActorDetector.TryAddConditionSubrecordWithOffset(buf, i, len, off, res.Conditions),
        /* 19 */ (buf, i, len, off, res, _, _) =>
            EsmWorldExtractor.TryAddVhgtHeightmapWithOffset(buf, i, len, off, false, res.Heightmaps),
        /* 20 */ (buf, i, len, off, res, _, _) =>
            EsmWorldExtractor.TryAddVhgtHeightmapWithOffset(buf, i, len, off, true, res.Heightmaps),
        /* 21 */ (buf, i, len, off, res, _, _) =>
            EsmWorldExtractor.TryAddXclcSubrecordWithOffset(buf, i, len, off, false, res.CellGrids),
        /* 22 */ (buf, i, len, off, res, _, _) =>
            EsmWorldExtractor.TryAddXclcSubrecordWithOffset(buf, i, len, off, true, res.CellGrids)
    ];

    /// <summary>
    ///     Single unified dispatch table mapping every 4-byte magic (main records LE/BE,
    ///     subrecords, GRUP, textures TX00-TX07) to a bit-packed action int.
    ///     One FrozenDictionary lookup per candidate replaces 4+ separate lookups.
    /// </summary>
    internal static readonly FrozenDictionary<uint, int> UnifiedDispatch = BuildUnifiedDispatch();

    private static FrozenDictionary<uint, int> BuildUnifiedDispatch()
    {
        var dict = new Dictionary<uint, int>();

        // Subrecord handlers (indexed by position in SubrecordHandlers array)
        (uint magic, int index)[] subrecords =
        [
            (SigEdid, 0), (SigGmst, 1), (SigSctx, 2), (SigScro, 3),
            (SigName, 4), (SigData, 5), (SigAcbs, 6), (SigNam1, 7),
            (SigTrdt, 8), (SigFull, 9), (SigDesc, 10), (SigModl, 11),
            (SigIcon, 12), (SigMico, 13), (SigScri, 14), (SigEnam, 15),
            (SigSnam, 16), (SigQnam, 17), (SigCtda, 18), (SigVhgt, 19),
            (SigTghv, 20), (SigXclc, 21), (SigClcx, 22)
        ];

        foreach (var (magic, index) in subrecords)
        {
            dict[magic] = index;
        }

        // Main record types (LE and BE)
        foreach (var type in RuntimeRecordTypes)
        {
            var bytes = Encoding.ASCII.GetBytes(type);
            var leMagic = BitConverter.ToUInt32(bytes, 0);
            var beMagic = BinaryPrimitives.ReverseEndianness(leMagic);

            // LE entry: may overlap with subrecord (e.g., GMST) -- combine with OR
            dict[leMagic] = dict.GetValueOrDefault(leMagic, NoHandler) | ActionMainRecordLE;

            // BE entry: reversed bytes
            dict[beMagic] = dict.GetValueOrDefault(beMagic, NoHandler) | ActionMainRecordBE;
        }

        // GRUP
        dict[SigGrupLE] = dict.GetValueOrDefault(SigGrupLE, NoHandler) | ActionGrup;
        dict[SigGrupBE] = dict.GetValueOrDefault(SigGrupBE, NoHandler) | ActionGrup;

        // Texture signatures TX00-TX07 (8 patterns)
        for (var d = '0'; d <= '7'; d++)
        {
            var txMagic = (uint)('T' | ('X' << 8) | ('0' << 16) | (d << 24));
            dict[txMagic] = dict.GetValueOrDefault(txMagic, NoHandler) | ActionTexture;
        }

        return dict.ToFrozenDictionary();
    }

    #endregion
}
