using static EsmAnalyzer.Conversion.Schema.SubrecordField;

namespace EsmAnalyzer.Conversion.Schema;

/// <summary>
///     Registry of all subrecord conversion schemas.
///     This replaces the large switch statement in EsmSubrecordConverter with declarative definitions.
/// </summary>
public static class SubrecordSchemaRegistry
{
    /// <summary>
    ///     All defined subrecord schemas, ordered by specificity (most specific first).
    ///     More specific rules (with RecordTypes/DataLength constraints) should come before generic ones.
    /// </summary>
    public static readonly SubrecordSchema[] Schemas =
    [
        // ============================================================
        // STRING SUBRECORDS (no byte swapping needed)
        // ============================================================

        // Universal strings (apply to all/most record types)
        SubrecordSchema.String("EDID"), // Editor ID - always string
        SubrecordSchema.String("FULL"), // Full name - always string
        SubrecordSchema.String("DESC"), // Description
        SubrecordSchema.String("ICON"), // Inventory icon path
        SubrecordSchema.String("MICO"), // Small icon path
        SubrecordSchema.String("ICO2"), // Female icon path
        SubrecordSchema.String("MIC2"), // Female small icon
        SubrecordSchema.String("TX00"), // Texture path 0
        SubrecordSchema.String("TX01"), // Texture path 1
        SubrecordSchema.String("TX02"), // Texture path 2
        SubrecordSchema.String("TX03"), // Texture path 3
        SubrecordSchema.String("TX04"), // Texture path 4
        SubrecordSchema.String("TX05"), // Texture path 5
        SubrecordSchema.String("TX06"), // Texture path 6
        SubrecordSchema.String("TX07"), // Texture path 7

        // Model paths
        SubrecordSchema.String("MODL"), // Model filename
        SubrecordSchema.String("MOD2"), // Female body model
        SubrecordSchema.String("MOD3"), // Male 3rd person model
        SubrecordSchema.String("MOD4"), // Female 3rd person model

        // Biped model paths
        SubrecordSchema.String("MO2B"),
        SubrecordSchema.String("MO3B"),
        SubrecordSchema.String("MO4B"),
        SubrecordSchema.String("MOD5"), // Hair addon path

        // Script source
        SubrecordSchema.String("SCTX", ["SCPT", "QUST", "INFO"]), // Script source text

        // Result script embedded source (INFO only, and SCDA which has it inline)
        SubrecordSchema.String("RNAM", ["INFO"]), // Result name - string in INFO
        SubrecordSchema.String("NAM1", ["INFO"]), // INFO response text

        // Faction/quest-specific strings
        SubrecordSchema.String("MNAM", ["FACT"]), // Male rank title
        SubrecordSchema.String("FNAM", ["FACT"]), // Female rank title
        SubrecordSchema.String("FNAM", ["WTHR"]), // Cloud layer filename
        SubrecordSchema.String("NNAM", ["QUST"]), // Quest stage log entry

        // TES4 header subrecords (author/description are strings)
        SubrecordSchema.String("CNAM", ["TES4"]), // Author
        SubrecordSchema.String("SNAM", ["TES4"]), // Description

        // WTHR subrecords that are strings (texture paths)
        SubrecordSchema.String("ANAM", ["WTHR"]),
        SubrecordSchema.String("BNAM", ["WTHR"]),
        SubrecordSchema.String("CNAM", ["WTHR"]),
        SubrecordSchema.String("DNAM", ["WTHR"]),

        // LIGH-specific numeric FNAM (4 bytes)
        new("FNAM", [UInt32(0)], ["LIGH"], DataLength: 4),

        // RACE-specific numeric subrecords
        new("SNAM", [UInt16(0)], ["RACE"], DataLength: 2),
        new("VTCK",
        [
            UInt32(0),
            UInt32(4)
        ], ["RACE"], DataLength: 8),

        // FACT subrecords
        new("WMI1", [UInt32(0)], ["FACT"], DataLength: 4),
        new("XNAM", [UInt32Array()], ["FACT"], DataLength: 12),

        // WEAP subrecords
        new("CRDT", [UInt16(0), UInt32(4), UInt32(12)], ["WEAP"], DataLength: 16),
        SubrecordSchema.Custom("VATS", "ConvertVats", ["WEAP"], 20),
        new("NAM0", [UInt32(0)], ["WEAP"], DataLength: 4),
        new("NAM6", [UInt32(0)], ["WEAP"], DataLength: 4),
        new("WMI1", [UInt32(0)], ["WEAP"], DataLength: 4),
        new("WMI2", [UInt32(0)], ["WEAP"], DataLength: 4),
        new("WMI3", [UInt32(0)], ["WEAP"], DataLength: 4),
        new("WMS1", [UInt32(0)], ["WEAP"], DataLength: 4),
        new("WMS2", [UInt32(0)], ["WEAP"], DataLength: 4),
        new("WNM1", [UInt32(0)], ["WEAP"], DataLength: 4),
        new("WNM2", [UInt32(0)], ["WEAP"], DataLength: 4),
        new("WNM3", [UInt32(0)], ["WEAP"], DataLength: 4),
        new("WNM4", [UInt32(0)], ["WEAP"], DataLength: 4),
        new("WNM5", [UInt32(0)], ["WEAP"], DataLength: 4),
        new("WNM6", [UInt32(0)], ["WEAP"], DataLength: 4),
        new("WNM7", [UInt32(0)], ["WEAP"], DataLength: 4),
        new("XNAM", [UInt32(0)], ["WEAP"], DataLength: 4),

        // AMMO subrecords
        new("DAT2", [UInt32(8), UInt32(16)], ["AMMO"], DataLength: 20),

        // ALCH subrecords - Effect Item structure
        // EFIT: Magnitude(4) + Area(4) + Duration(4) + Type(4) + ActorValue(4) = 20 bytes
        new("EFIT", [UInt32(0), UInt32(4), UInt32(8), UInt32(12), UInt32(16)], DataLength: 20),
        // ENIT: Value(4) + Flags(1) + Unused(3) + WithdrawEffect(4) + AddictionChance(4) + ConsumeSound(4) = 20 bytes
        new("ENIT", [UInt32(0), UInt32(8), UInt32(12), UInt32(16)], DataLength: 20),

        // NPC_ subrecords
        new("EAMT", [UInt16(0)], ["NPC_"], DataLength: 2),
        new("ENAM", [UInt32(0)], ["NPC_"], DataLength: 4),
        new("HCLR", [UInt32(0)], ["NPC_"], DataLength: 4),
        new("LNAM", [UInt32(0)], ["NPC_"], DataLength: 4),
        new("NAM4", [UInt32(0)], ["NPC_"], DataLength: 4),
        new("NAM5", [UInt16(0)], ["NPC_"], DataLength: 2),
        new("NAM6", [UInt32(0)], ["NPC_"], DataLength: 4),

        // CREA subrecords
        new("CSCR", [UInt32(0)], ["CREA"], DataLength: 4),
        new("EAMT", [UInt16(0)], ["CREA"], DataLength: 2),
        new("LNAM", [UInt32(0)], ["CREA"], DataLength: 4),
        new("NAM4", [UInt32(0)], ["CREA"], DataLength: 4),
        new("NAM5", [UInt32(0)], ["CREA"], DataLength: 4),
        new("COED", [UInt32(0), UInt32(4), UInt32(8)], ["CREA"], DataLength: 12),
        new("NIFT", [UInt32(0)], ["CREA"]), // First 4 bytes are uint32, rest is string data

        // COED - Common owner data (12 bytes): 3 x uint32 (e.g., owner, global, value)
        new("COED", [UInt32(0), UInt32(4), UInt32(8)], DataLength: 12),

        // Challenge/Terminal strings
        SubrecordSchema.String("RNAM", ["CHAL", "TERM"]), // Challenge description/results, Terminal result text

        // REFR/ACHR strings
        SubrecordSchema.String("RCLR"), // Linked reference color
        SubrecordSchema.String("NAM4", ["REFR"]), // Map marker name (REFR)

        // RACE-specific strings
        SubrecordSchema.String("MPAI"), // Male addon path index
        SubrecordSchema.String("FPAI"), // Female addon path index

        // World strings
        SubrecordSchema.String("NNAM", ["WRLD"]), // World name map marker

        // MGEF strings
        SubrecordSchema.String("DNAM", ["MGEF"]), // Magic effect description

        // TXST subrecords
        new("DNAM", [UInt16(0)], ["TXST"], DataLength: 2), // Texture flags (uint16)

        // ARMO subrecords
        // DNAM is 12 bytes: int16 DR (0-1), unused (2-3), float DT (4-7), uint16 Flags (8-9), unused (10-11)
        // Note: Only bytes 0-1 and 4-7 need endian swap; bytes 8-9 (flags) are already LE on Xbox
        new("DNAM", [UInt16(0), UInt32(4)], ["ARMO"], DataLength: 12),

        // ARMA subrecords
        // DNAM is 12 bytes: int16 DR (0-1), uint16 Flags (2-3), float DT (4-7), unused (8-11)
        new("DNAM", [UInt16(0), UInt16(2), UInt32(4)], ["ARMA"], DataLength: 12),

        // PROJ strings
        SubrecordSchema.String("NAM1", ["PROJ"]), // Projectile muzzle flash model path

        // BPTD strings
        SubrecordSchema.String("NAM1", ["BPTD"]), // Body part name (e.g., Gore)

        // CELL strings
        SubrecordSchema.String("XNAM", ["CELL"]), // Water noise texture

        // Note/Terminal strings
        SubrecordSchema.String("TNAM", ["NOTE"]), // Note text

        // AMMO strings
        SubrecordSchema.String("ONAM", ["AMMO"]), // Ammo short name
        SubrecordSchema.String("QNAM", ["AMMO"]), // Ammo abbreviation

        // CELL subrecords
        new("LTMP", [UInt32(0)], ["CELL"], DataLength: 4),
        new("LNAM", [UInt32(0)], ["CELL"], DataLength: 4),
        new("XCLW", [UInt32(0)], ["CELL"], DataLength: 4),
        new("XCLL",
        [
            UInt32(0),
            UInt32(4),
            UInt32(8),
            UInt32(12),
            UInt32(16),
            UInt32(20),
            UInt32(24),
            UInt32(28),
            UInt32(32),
            UInt32(36)
        ], ["CELL"], DataLength: 40),
        new("XCAS", [UInt32(0)], ["CELL"], DataLength: 4),
        new("XCIM", [UInt32(0)], ["CELL"], DataLength: 4),
        new("XCMO", [UInt32(0)], ["CELL"], DataLength: 4),
        new("XEZN", [UInt32(0)], ["CELL"], DataLength: 4),
        new("XCCM", [UInt32(0)], ["CELL"], DataLength: 4),

        // ============================================================
        // SIMPLE FORMID SUBRECORDS (single 4-byte swap at offset 0)
        // ============================================================

        // Extended-size marker
        new("XXXX", [UInt32(0)], DataLength: 4),

        // Universal FormID subrecords
        SubrecordSchema.FormId("NAME"), // Base object FormID
        SubrecordSchema.FormId("TPLT"), // Template
        SubrecordSchema.FormId("VTCK"), // Voice type
        SubrecordSchema.FormId("INAM"), // Various FormID references
        SubrecordSchema.FormId("WNAM"), // Worn armor / water type
        SubrecordSchema.FormId("ONAM"), // Open sound
        SubrecordSchema.FormId("QNAM"), // Close sound
        SubrecordSchema.FormId("SNAM"), // Sound
        SubrecordSchema.FormId("ANAM"), // Ambient sound
        new("PNAM", [UInt32(0)], ["DIAL"], DataLength: 4), // DIAL priority (float)
        SubrecordSchema.FormId("BNAM"), // Talk idle
        SubrecordSchema.FormId("YNAM"), // Sound - Pick up
        SubrecordSchema.FormId("ZNAM"), // Sound - Drop
        SubrecordSchema.FormId("SCRI"), // Script
        SubrecordSchema.FormId("EITM"), // Enchantment item
        SubrecordSchema.FormId("ETYP"), // Equipment type
        SubrecordSchema.FormId("SCHR", dataLength: 4), // Script header when only 4 bytes (FormID)
        SubrecordSchema.FormId("REPL"), // Repair list
        SubrecordSchema.FormId("BIPL"), // Biped model list
        SubrecordSchema.FormId("EAMT"), // Enchantment amount (FormID)
        SubrecordSchema.FormId("NAM1"), // Various FormIDs
        SubrecordSchema.String("NAM2", ["INFO"]), // INFO speaker notes (text)
        SubrecordSchema.FormId("NAM2"), // Various FormIDs
        SubrecordSchema.FormId("NAM7"), // WRLD FormID ref
        SubrecordSchema.FormId("NAM8"), // Various FormIDs
        SubrecordSchema.FormId("NAM9", excludeRecordTypes: ["WRLD"]), // Various FormIDs
        SubrecordSchema.FormId("CNAM"), // Climate/color
        SubrecordSchema.FormId("GNAM"), // Grass
        SubrecordSchema.FormId("HNAM", dataLength: 4), // Hair FormID when 4 bytes
        SubrecordSchema.FormId("TNAM", excludeRecordTypes: ["NOTE", "STAT"]), // Target FormID (except NOTE text)
        SubrecordSchema.FormId("UNAM"), // Use sound
        SubrecordSchema.FormId("VNAM"), // Voice type / various
        SubrecordSchema.FormId("MNAM", excludeRecordTypes: ["FACT", "IDLE", "DIAL"]), // Male variant / map
        SubrecordSchema.FormId("NNAM", excludeRecordTypes: ["QUST", "WRLD", "IDLE"]), // FormID ref (except strings)
        SubrecordSchema.FormId("PNAM"), // Previous IDLE / parent
        SubrecordSchema.FormId("DNAM",
            excludeRecordTypes:
            [
                "MGEF", "AMMO", "WEAP", "ARMO", "ARMA", "PROJ", "EXPL", "CREA", "NPC_", "WRLD", "RACE", "LSCR", "WATR",
                "TERM"
            ]),

        // Combat style / class
        SubrecordSchema.FormId("ZNAM", ["CREA", "NPC_"]), // Combat style
        SubrecordSchema.FormId("CNAM", ["CREA", "NPC_"]), // Class

        // Worldspace references
        SubrecordSchema.FormId("XCWT"), // Water type
        SubrecordSchema.FormId("XPWR"), // Water reflection
        SubrecordSchema.FormId("XLTW"), // Lit water
        SubrecordSchema.FormId("XOWN"), // Owner
        SubrecordSchema.FormId("XGLB"), // Global variable
        SubrecordSchema.FormId("XESP"), // Enable parent (first FormID at offset 0)
        SubrecordSchema.FormId("XPOD"), // Portal destinations
        SubrecordSchema.FormId("XPTL"), // Portal destination link
        SubrecordSchema.FormId("XLCM"), // Level modifier
        SubrecordSchema.FormId("XMRC"), // Merchant container
        SubrecordSchema.FormId("XRGD"), // Ragdoll data FormID
        SubrecordSchema.FormId("XLCN"), // Location
        SubrecordSchema.FormId("TNAM", ["STAT"], dataLength: 4), // STAT target when 4 bytes

        // Package FormIDs
        SubrecordSchema.FormId("PKID"), // Package ID

        // Quest FormIDs
        SubrecordSchema.FormId("QSTI"), // Quest item
        SubrecordSchema.FormId("QSTR"), // Quest target

        // Script references
        SubrecordSchema.FormId("SCRO"), // Script object reference

        // Effect references
        SubrecordSchema.FormId("EFID"), // Effect ID

        // Perk references
        SubrecordSchema.FormId("PRKR"), // Perk reference

        // ============================================================
        // FORMID ARRAY SUBRECORDS
        // ============================================================

        SubrecordSchema.FormIdArray("KWDA"), // Keywords array
        SubrecordSchema.FormIdArray("SPLO"), // Spell list
        SubrecordSchema.FormIdArray("PKCU"), // Package content union
        SubrecordSchema.FormIdArray("CITC"), // Condition item count array
        SubrecordSchema.FormIdArray("ENAM", ["RACE"]), // Eyes
        SubrecordSchema.FormIdArray("HNAM", ["RACE"]), // Hairs
        SubrecordSchema.FormIdArray("XCLR", ["CELL"]), // Regions

        // ============================================================
        // STRUCTURED SUBRECORDS (specific field layouts)
        // ============================================================

        // TES4 header data (HEDR) - 12 bytes: float version + uint32 numRecords + uint32 nextObjectId
        new("HEDR",
        [
            UInt32(0),
            UInt32(4),
            UInt32(8)
        ], ["TES4"], DataLength: 12),

        // OBND - Object bounds: 6 x int16
        new("OBND",
        [
            UInt16(0), UInt16(2), UInt16(4), // Min X,Y,Z
            UInt16(6), UInt16(8), UInt16(10) // Max X,Y,Z
        ], DataLength: 12),

        // DEST - Destruction header (8 bytes): int32 health + 4 bytes of flags/count
        new("DEST",
        [
            UInt32(0) // Health
        ], DataLength: 8),

        // DSTD - Destruction stage data (20 bytes)
        new("DSTD",
        [
            UInt32(4), // Self Damage per Second
            UInt32(8), // Explosion FormID
            UInt32(12), // Debris FormID
            UInt32(16) // Debris Count
        ], DataLength: 20),

        // BMDT - Biped model data (ARMO): 4B swap + 4B same
        new("BMDT",
        [
            UInt32(0)
        ], ["ARMO", "ARMA"], DataLength: 8),

        // RACE-specific subrecords
        new("INDX", [UInt32(0)], ["RACE"], DataLength: 4),

        // DATA subrecords - require record type context (handled by custom)
        // CLAS - class data (28 bytes)
        // PDB: CLASS_DATA { eTagSkills[4x int32] @0, cClassFlags @16, iServiceFlags @20, cTrainingSkill @24, cTrainingLevel @25 }
        // Swap the tag skill indices and service flags; byte fields remain unchanged.
        new("DATA", [UInt32Array(0, 4), UInt32(20)], ["CLAS"], DataLength: 28),

        // MGEF - magic effect data (72 bytes)
        // Pattern: 5×4-byte swaps, 8 bytes unchanged, 11×4-byte swaps
        new("DATA",
        [
            UInt32Array(0, 5),
            UInt32Array(28, 11)
        ], ["MGEF"], DataLength: 72),

        SubrecordSchema.Custom("DATA", "ConvertDataSubrecord"),

        // ACBS - Actor base stats (24 bytes)
        // uint32 flags, uint16 fatigue, uint16 barterGold, int16 level, uint16 calcMinLevel,
        // uint16 calcMaxLevel, uint16 speedMult, float karma, uint16 dispBase, uint16 templateFlags
        new("ACBS",
        [
            UInt32(0), // Flags
            UInt16(4), // Fatigue
            UInt16(6), // Barter Gold
            UInt16(8), // Level (signed)
            UInt16(10), // Calc min level
            UInt16(12), // Calc max level
            UInt16(14), // Speed multiplier
            UInt32(16), // Karma (float)
            UInt16(20), // Disposition base
            UInt16(22) // Template flags
        ], DataLength: 24),

        // AIDT - AI data (20 bytes)
        // 5 bytes (aggression, confidence, energy, responsibility, mood),
        // 3 bytes padding, uint32 services, int8 trainSkill, int8 trainLevel, int8 assistance,
        // int8 aggro behavior, int32 aggro radius
        SubrecordSchema.Custom("AIDT", "ConvertAidt", dataLength: 20),

        // CNTO - Container item (8 bytes): FormID + int32 count
        new("CNTO",
        [
            UInt32(0), // Item FormID
            UInt32(4) // Count
        ], DataLength: 8),

        // CTDA - Condition data (28 bytes) - complex structure
        SubrecordSchema.Custom("CTDA", "ConvertCtda", dataLength: 28),

        // DNAM - various structured data depending on record type
        SubrecordSchema.Custom("DNAM", "ConvertDnam",
            ["AMMO", "WEAP", "ARMO", "ARMA", "PROJ", "EXPL", "CREA", "NPC_", "WRLD", "RACE", "LSCR", "WATR"]),

        // ENIT - Enchantment/ingestible data (ENCH uses 16 bytes: 3×4B swap + 4B same)
        new("ENIT",
        [
            UInt32(0), // Enchantment value
            UInt32(4), // Flags
            UInt32(8) // Charge amount
            // Bytes 12-15: Unknown/float? (no swap)
        ], ["ENCH"], DataLength: 16),

        // ENIT - Enchantment/ingestible data (20 bytes)
        new("ENIT",
        [
            UInt32(0), // Value/Type
            UInt32(4), // Flags
            UInt32(8), // Amount (if potion) or other
            UInt32(12), // Other field
            UInt32(16) // Sound/FormID
        ], DataLength: 20),

        // EFIT - Effect item (20 bytes in ENCH): 5 x 4-byte fields
        new("EFIT",
        [
            UInt32(0),
            UInt32(4),
            UInt32(8),
            UInt32(12),
            UInt32(16)
        ], ["ENCH"], DataLength: 20),

        // EFIT - Effect item (20 bytes in SPEL): 5 x 4-byte fields
        new("EFIT",
        [
            UInt32(0),
            UInt32(4),
            UInt32(8),
            UInt32(12),
            UInt32(16)
        ], ["SPEL"], DataLength: 20),

        // EFIT - Effect item (12 bytes)
        new("EFIT",
        [
            UInt32(0), // Magnitude
            UInt32(4), // Area
            UInt32(8) // Duration
        ], DataLength: 12),

        // SPIT - Spell data (16 bytes): 3 x 4-byte fields + 4 bytes unchanged
        new("SPIT",
        [
            UInt32(0),
            UInt32(4),
            UInt32(8)
            // Bytes 12-15 are platform-specific or already in PC order
        ], ["SPEL"], DataLength: 16),

        // SNDD - Sound data (36 bytes): SOUN record
        // Layout: 4B same (unused?) + 4B swap + 8×2B swap + 3×4B swap
        new("SNDD",
        [
            UInt32(4), // Minimum attentuation distance
            UInt16(8), // Frequency adjustment %
            UInt16(10), // Unknown
            UInt16(12), // Attenuation curve 1
            UInt16(14), // Attenuation curve 2
            UInt16(16), // Attenuation curve 3
            UInt16(18), // Attenuation curve 4
            UInt16(20), // Attenuation curve 5
            UInt16(22), // Reverb attenuation control
            UInt32(24), // Priority
            UInt32(28), // Static attenuation
            UInt32(32) // Max play count / random offset
        ], DataLength: 36),

        // SCHR - Script header (20 bytes with compiled size)
        new("SCHR",
        [
            UInt32(4), // RefCount
            UInt32(8), // CompiledSize
            UInt32(12) // VariableCount
            // Type (bytes 16-17) and Flags (bytes 18-19) are already little-endian in Xbox 360 files
        ], DataLength: 20),

        // TRDT - Training data / response data (24 bytes)
        new("TRDT",
        [
            UInt32(0), // Emotion type
            UInt32(4), // Emotion value
            // Bytes 8-23 are NOT big-endian in Xbox ESM - they match PC exactly
            // Likely: bytes 8-11 = unknown/padding, bytes 12-15 and 16-19 = sound file hashes or other non-numeric data,
            // bytes 20-23 = flags. These are already in the correct format.
        ], DataLength: 24),

        // PKDT - Package data (12 bytes)
        SubrecordSchema.Custom("PKDT", "ConvertPkdt", ["PACK"], 12),

        // PSDT - Package Schedule Data (8 bytes)
        // PDB: PACK_SCHED_DATA - eMonth(char@0), eDayOfWeek(char@1), cDate(char@2), cTime(char@3), iDuration(int32@4)
        new("PSDT", [UInt32(4)], ["PACK"], DataLength: 8), // Only swap iDuration at offset 4

        // IDLT - Idle Timer (4 bytes) - FormID reference
        new("IDLT", [UInt32(0)], ["PACK"], DataLength: 4),

        // IDLA - Idle Animation List (12 bytes) - 3 FormIDs
        new("IDLA", [UInt32(0), UInt32(4), UInt32(8)], ["PACK"], DataLength: 12),

        // PKDD - Package data (24 bytes)
        new("PKDD", [UInt32Array()], ["PACK"], DataLength: 24),

        // LVLO - Leveled list entry (12 bytes)
        // PDB: LEVELED_OBJECT_FILE { uint16 sLevel @0, uint32 iFormID @4, uint16 sCount @8 }
        // Bytes 2-3 and 10-11 appear to be padding/unused in file layout.
        new("LVLO",
        [
            UInt16(0), // sLevel
            UInt32(4), // iFormID
            UInt16(8) // sCount
        ], DataLength: 12),

        // XCLC - Cell grid (12 bytes): int32 x, int32 y, uint32 flags
        new("XCLC",
        [
            UInt32(0), // X
            UInt32(4), // Y
            UInt32(8) // Flags
        ], DataLength: 12),

        // XSCL - Scale (4 bytes float)
        new("XSCL", [UInt32(0)], DataLength: 4),

        // XLOC - Lock data (20 bytes)
        new("XLOC",
        [
            UInt32(4), // Key FormID
            UInt32(8), // Flags
            UInt32(12) // Unknown
            // Bytes 0-3: Lock level (byte) + unused (3 bytes)
            // Bytes 16-19: Unknown
        ], DataLength: 20),

        // XTEL - Teleport destination (32 bytes)
        new("XTEL",
        [
            UInt32(0), // Door FormID
            UInt32(4), // Pos X
            UInt32(8), // Pos Y
            UInt32(12), // Pos Z
            UInt32(16), // Rot X
            UInt32(20), // Rot Y
            UInt32(24), // Rot Z
            UInt32(28) // Flags
        ], DataLength: 32),

        // HNAM when 8 bytes (HDPT head parts): 2 FormIDs
        new("HNAM",
        [
            UInt32(0), // FormID 1
            UInt32(4) // FormID 2
        ], DataLength: 8),

        // RNAM - when not INFO/CHAL/TERM, it's a FormID
        SubrecordSchema.FormId("RNAM", excludeRecordTypes: ["INFO", "CHAL", "TERM"]),

        // ============================================================
        // FLOAT ARRAY SUBRECORDS
        // ============================================================

        // FGGS - FaceGen geometry symmetric (50 floats = 200 bytes)
        new("FGGS", [UInt32Array()], DataLength: 200),

        // FGGA - FaceGen geometry asymmetric (30 floats = 120 bytes)
        new("FGGA", [UInt32Array()], DataLength: 120),

        // FGTS - FaceGen texture symmetric (50 floats = 200 bytes)
        new("FGTS", [UInt32Array()], DataLength: 200),

        // ENAM - when it's a float array (EYES record)
        new("ENAM", [UInt32Array()], ["EYES"]),

        // ============================================================
        // MODEL DATA SUBRECORDS
        // ============================================================

        // MODT/MO2T/MO3T/MO4T/MO5T - Model texture hashes (raw bytes; already match PC)
        new("MODT", []),
        new("MO2T", []),
        new("MO3T", []),
        new("MO4T", []),
        new("MO5T", []),

        // DMDT - Dismember data (8-byte entries, raw hash data like MODT - no conversion needed)
        new("DMDT", []),

        // ============================================================
        // COORDINATE/POSITION SUBRECORDS
        // ============================================================

        // XPRD - Patrol data (4 bytes float)
        new("XPRD", [UInt32(0)], DataLength: 4),

        // XPRM - Primitive data (24 bytes: 6 floats)
        new("XPRM", [UInt32Array()], DataLength: 24),

        // XRMR - Room marker (could be various)
        SubrecordSchema.FormId("XRMR"),

        // ============================================================
        // NAVMESH SUBRECORDS (complex - custom handlers)
        // ============================================================

        SubrecordSchema.Custom("NVTR", "ConvertNvtr"), // Navmesh triangles (16 bytes each: 3×uint16 verts, 3×uint16 edges, uint32 flags)
        SubrecordSchema.Custom("NVVX", "ConvertNvvx"), // Navmesh vertices (12 bytes each: 3×float)
        new("NVER", [UInt32(0)], DataLength: 4), // Navmesh version (uint32)
        SubrecordSchema.Custom("NVDP", "ConvertNvdp"), // Navmesh door portals (array of 8-byte entries: FormID + 2×uint16)
        new("NVCA", [UInt16Array()]), // Navmesh cover array (array of uint16)
        SubrecordSchema.Custom("NVMI", "ConvertNvmi"), // Navmesh info
        SubrecordSchema.Custom("NVCI", "ConvertNvci"), // Navmesh connection info
        SubrecordSchema.Custom("NVGD", "ConvertNvgd"), // Navmesh grid

        // ============================================================
        // RAGDOLL/PHYSICS SUBRECORDS (custom)
        // ============================================================

        SubrecordSchema.Custom("RAFD", "ConvertRafd"), // Ragdoll feedback data
        SubrecordSchema.Custom("RAFB", "ConvertRafb"), // Ragdoll feedback bones
        SubrecordSchema.Custom("RAPS", "ConvertRaps"), // Ragdoll pose data
        SubrecordSchema.Custom("XRGD", "ConvertXrgd"), // Ragdoll data

        // ============================================================
        // CELL/WORLDSPACE SUBRECORDS
        // ============================================================

        // WRLD - Worldspace bounds min/max (2 floats each)
        new("NAM0", [UInt32Array(count: 2)], ["WRLD"], DataLength: 8),
        new("NAM9", [UInt32Array(count: 2)], ["WRLD"], DataLength: 8),

        // WRLD - climate/water references (4-byte values)
        new("NAM3", [UInt32(0)], ["WRLD"], DataLength: 4),
        new("NAM4", [UInt32(0)], ["WRLD"], DataLength: 4),

        // WRLD - World Map Data (16 bytes)
        new("MNAM",
        [
            UInt32(0), // Usable X
            UInt32(4), // Usable Y
            UInt16(8), // NW Cell X
            UInt16(10), // NW Cell Y
            UInt16(12), // SE Cell X
            UInt16(14) // SE Cell Y
        ], ["WRLD"], DataLength: 16),

        // LAND - Height map (first 4 bytes are float offset; rest are signed byte deltas + 3 trailing bytes)
        SubrecordSchema.Custom("VHGT", "ConvertVhgt", ["LAND"], 1096),

        // LAND - Base data (uint32)
        new("DATA", [UInt32(0)], ["LAND"], DataLength: 4),

        // LAND - Texture layers (8 bytes): FormID + byte + byte + uint16
        // Byte 5 is platform-specific (Xbox=0x00, PC=0x88)
        new("ATXT", [UInt32(0), PlatformByte(5, 0x88), UInt16(6)], ["LAND"], DataLength: 8),
        new("BTXT", [UInt32(0), PlatformByte(5, 0x88), UInt16(6)], ["LAND"], DataLength: 8),

        // RPLI - Region point list (FormID + count prefix)
        SubrecordSchema.Custom("RPLI", "ConvertRpli"),

        // RPLD - Region point list data (array of float pairs)
        new("RPLD", [UInt32Array()]),

        // OFST - Worldspace offset table (uint32 offsets)
        new("OFST", [UInt32Array()], ["WRLD"]),

        // RDAT - Region data (complex header + data) - REGN records only
        SubrecordSchema.Custom("RDAT", "ConvertRdat", ["REGN"]),

        // RDAT - Reverb data (FormID reference) - ASPC records only (4 bytes)
        new("RDAT", [UInt32(0)], ["ASPC"], DataLength: 4),

        // ============================================================
        // IMAGE SPACE MODIFIER SUBRECORDS
        // ============================================================

        // IAD* subrecords - all floats
        new("\x00IAD", [UInt32Array()]),
        new("\x40IAD", [UInt32Array()]),
        new("\x01IAD", [UInt32Array()]),
        new("\x41IAD", [UInt32Array()]),
        new("\x02IAD", [UInt32Array()]),
        new("\x42IAD", [UInt32Array()]),
        new("\x03IAD", [UInt32Array()]),
        new("\x43IAD", [UInt32Array()]),
        new("\x04IAD", [UInt32Array()]),
        new("\x44IAD", [UInt32Array()]),
        new("\x05IAD", [UInt32Array()]),
        new("\x45IAD", [UInt32Array()]),
        new("\x06IAD", [UInt32Array()]),
        new("\x46IAD", [UInt32Array()]),
        new("\x07IAD", [UInt32Array()]),
        new("\x47IAD", [UInt32Array()]),
        new("\x08IAD", [UInt32Array()]),
        new("\x48IAD", [UInt32Array()]),
        new("\x09IAD", [UInt32Array()]),
        new("\x49IAD", [UInt32Array()]),
        new("\x0AIAD", [UInt32Array()]),
        new("\x4AIAD", [UInt32Array()]),
        new("\x0BIAD", [UInt32Array()]),
        new("\x4BIAD", [UInt32Array()]),
        new("\x0CIAD", [UInt32Array()]),
        new("\x4CIAD", [UInt32Array()]),
        new("\x0DIAD", [UInt32Array()]),
        new("\x4DIAD", [UInt32Array()]),
        new("\x0EIAD", [UInt32Array()]),
        new("\x4EIAD", [UInt32Array()]),
        new("\x0FIAD", [UInt32Array()]),
        new("\x4FIAD", [UInt32Array()]),
        new("\x10IAD", [UInt32Array()]),
        new("\x50IAD", [UInt32Array()]),
        new("\x11IAD", [UInt32Array()]),
        new("\x51IAD", [UInt32Array()]),
        new("\x12IAD", [UInt32Array()]),
        new("\x52IAD", [UInt32Array()]),
        new("\x13IAD", [UInt32Array()]),
        new("\x53IAD", [UInt32Array()]),
        new("\x14IAD", [UInt32Array()]),
        new("\x54IAD", [UInt32Array()]),

        // BNAM/CNAM/DNAM/ENAM/FNAM/GNAM/HNAM/INAM/JNAM/KNAM/LNAM/MNAM/NNAM/ONAM/PNAM/QNAM/RNAM/SNAM/TNAM/UNAM
        // when in IMAD record - all float arrays
        new("BNAM", [UInt32Array()], ["IMAD"]),
        new("CNAM", [UInt32Array()], ["IMAD"]),
        new("DNAM", [UInt32Array()], ["IMAD"]),
        new("ENAM", [UInt32Array()], ["IMAD"]),
        new("FNAM", [UInt32Array()], ["IMAD"]),
        new("GNAM", [UInt32Array()], ["IMAD"]),
        new("HNAM", [UInt32Array()], ["IMAD"]),
        new("INAM", [UInt32Array()], ["IMAD"]),
        new("JNAM", [UInt32Array()], ["IMAD"]),
        new("KNAM", [UInt32Array()], ["IMAD"]),
        new("LNAM", [UInt32Array()], ["IMAD"]),
        new("MNAM", [UInt32Array()], ["IMAD"]),
        new("NNAM", [UInt32Array()], ["IMAD"]),
        new("ONAM", [UInt32Array()], ["IMAD"]),
        new("PNAM", [UInt32Array()], ["IMAD"]),
        new("QNAM", [UInt32Array()], ["IMAD"]),
        new("RNAM", [UInt32Array()], ["IMAD"]),
        new("SNAM", [UInt32Array()], ["IMAD"]),
        new("TNAM", [UInt32Array()], ["IMAD"]),
        new("UNAM", [UInt32Array()], ["IMAD"]),

        // ============================================================
        // VTXT - Vertex texture blending (custom due to complex structure)
        // ============================================================
        SubrecordSchema.Custom("VTXT", "ConvertVtxt"),

        // ============================================================
        // IDLE/ANIMATION SUBRECORDS
        // ============================================================

        // MNAM in IDLE - string (sibling animations)
        SubrecordSchema.String("MNAM", ["IDLE"]),
        SubrecordSchema.String("DNAM", ["IDLE"]),

        // MNAM in DIAL - FormID
        SubrecordSchema.FormId("MNAM", ["DIAL"]),

        // ============================================================
        // PACKAGE LOCATION SUBRECORDS
        // ============================================================

        // PLDT - Package location target (12 bytes)
        new("PLDT",
        [
            UInt32(0), // Type
            UInt32(4), // Location FormID
            UInt32(8) // Radius
        ], DataLength: 12),

        // PTDT - Package target data (16 bytes)
        new("PTDT",
        [
            UInt32(0), // Type
            UInt32(4), // Target FormID/count
            UInt32(8), // Count
            UInt32(12) // Unknown
        ], DataLength: 16),

        // ============================================================
        // XRDO - Radio data (20 bytes)
        // ============================================================
        new("XRDO",
        [
            UInt32(0), // Range radius (float)
            UInt32(4), // Broadcast range type
            UInt32(8), // Static percentage (float)
            UInt32(12), // Position reference FormID
            UInt32(16) // Flags? / Unknown
        ], MinLength: 16),

        // ============================================================
        // RACE SUBRECORDS
        // ============================================================

        // NAM0/NAM1 in RACE - face data (array of FormIDs)
        SubrecordSchema.FormIdArray("NAM0", ["RACE"]),
        SubrecordSchema.FormIdArray("NAM1", ["RACE"]),

        // PNAM in RACE - FaceGen main clamp (float)
        new("PNAM", [UInt32(0)], ["RACE"], DataLength: 4),

        // UNAM in RACE - FaceGen face clamp (float)
        new("UNAM", [UInt32(0)], ["RACE"], DataLength: 4),

        // ============================================================
        // MISC SUBRECORDS
        // ============================================================

        // MODS in HDPT uses mixed layouts (variable length) - custom handler
        SubrecordSchema.Custom("MODS", "ConvertModsHdpt", ["HDPT"]),

        // ARMO alternate texture lists - custom handler
        SubrecordSchema.Custom("MODS", "ConvertModsList", ["ARMO"]),
        SubrecordSchema.Custom("MO2S", "ConvertModsList", ["ARMO"]),
        SubrecordSchema.Custom("MO3S", "ConvertModsList", ["ARMO"]),
        SubrecordSchema.Custom("MO4S", "ConvertModsList", ["ARMO"]),
        SubrecordSchema.Custom("MO5S", "ConvertModsList", ["ARMO"]),

        // MODS/MO2S/MO3S/MO4S/MO5S - model alternate textures (FormID at start)
        // Alternate texture arrays: only the leading count is BE on Xbox
        new("MODS", [UInt32(0)]),
        new("MO2S", [UInt32(0)]),
        new("MO3S", [UInt32(0)]),
        new("MO4S", [UInt32(0)]),
        new("MO5S", [UInt32(0)]),

        // SLSD - Script local variable data (24 bytes)
        new("SLSD",
        [
            UInt32(0) // Index
        ], DataLength: 24),

        // SCVR - Script variable name (string)
        SubrecordSchema.String("SCVR"),

        // SCRV - Script reference variable (uint32)
        new("SCRV", [UInt32(0)], DataLength: 4),

        // INTV - Integer value (4 bytes)
        new("INTV", [UInt32(0)], DataLength: 4),

        // FLTV - Float value (4 bytes)
        new("FLTV", [UInt32(0)], DataLength: 4),

        // XNDP - Navigation door link (8 bytes: 2 FormIDs)
        new("XNDP",
        [
            UInt32(0), // Navmesh FormID
            UInt32(4) // Unknown
        ], DataLength: 8),

        // XPPA - Portal parent activation (8 bytes)
        new("XPPA", [UInt32Array()], DataLength: 8),

        // XAPD - Activate parent flags (1 byte - no swap needed, just placeholder)
        new("XAPD", [], DataLength: 1),

        // XAPR - Activate parent reference (8 bytes: FormID + delay float)
        new("XAPR",
        [
            UInt32(0), // Reference FormID
            UInt32(4) // Delay (float)
        ], DataLength: 8),

        // XATO - Activation prompt override (string)
        SubrecordSchema.String("XATO"),

        // XCNT - Item count
        new("XCNT", [UInt32(0)], DataLength: 4),

        // XHLP - Health percent
        new("XHLP", [UInt32(0)], DataLength: 4),

        // XLRL - Location ref list (FormID)
        SubrecordSchema.FormId("XLRL"),

        // XRDS - Radius (float)
        new("XRDS", [UInt32(0)], DataLength: 4),

        // XEMI - Emittance (FormID)
        SubrecordSchema.FormId("XEMI"),

        // XLIG - Light data (multiple floats)
        new("XLIG", [UInt32Array()]),

        // XALP - Alpha (2 bytes, each a single byte - no swap)
        new("XALP", [], DataLength: 2),

        // XACT - Action flag
        new("XACT", [UInt32(0)], DataLength: 4),

        // XMBR - Multibound reference
        SubrecordSchema.FormId("XMBR"),

        // ONAM when FormID array in worldspace/etc
        SubrecordSchema.FormIdArray("ONAM", ["WRLD"])

        // ============================================================
        // FALLBACK: UNKNOWN SUBRECORDS
        // ============================================================

        // If nothing else matches and it's multiple of 4, try swapping as uint32 array
        // This is handled specially in the converter logic, not as a schema
    ];

    private static readonly object _lock = new();
    private static Dictionary<string, List<SubrecordSchema>>? _schemasBySignature;

    /// <summary>
    ///     Gets schemas indexed by signature for faster lookup (thread-safe lazy initialization).
    /// </summary>
    public static Dictionary<string, List<SubrecordSchema>> SchemasBySignature
    {
        get
        {
            if (_schemasBySignature is not null)
                return _schemasBySignature;

            lock (_lock)
            {
                if (_schemasBySignature is not null)
                    return _schemasBySignature;

                var dict = new Dictionary<string, List<SubrecordSchema>>();
                foreach (var schema in Schemas)
                {
                    if (!dict.TryGetValue(schema.Signature, out var list))
                    {
                        list = [];
                        dict[schema.Signature] = list;
                    }

                    list.Add(schema);
                }

                _schemasBySignature = dict;
                return _schemasBySignature;
            }
        }
    }

    /// <summary>
    ///     Finds the first matching schema for the given subrecord context.
    /// </summary>
    public static SubrecordSchema? FindSchema(string signature, string recordType, int dataLength)
    {
        if (!SchemasBySignature.TryGetValue(signature, out var candidates))
            return null;

        foreach (var schema in candidates)
            if (schema.Matches(signature, recordType, dataLength))
                return schema;

        return null;
    }
}