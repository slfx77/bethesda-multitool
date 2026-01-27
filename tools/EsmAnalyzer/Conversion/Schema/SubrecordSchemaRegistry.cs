using System.Collections.Concurrent;
using F = EsmAnalyzer.Conversion.Schema.SubrecordField;

namespace EsmAnalyzer.Conversion.Schema;

/// <summary>
///     Defines subrecord schemas by key (signature + optional record type + optional data length).
///     This is the single source of truth for subrecord conversion rules.
/// </summary>
public static class SubrecordSchemaRegistry
{
    /// <summary>
    ///     Constant indicating this string subrecord applies to all record types.
    ///     Used in the string subrecords registry for clarity.
    /// </summary>
    private const string? AnyRecordType = null;

    /// <summary>
    ///     All registered schemas indexed by key.
    /// </summary>
    private static readonly Dictionary<SchemaKey, SubrecordSchema> s_schemas = BuildSchemaRegistry();

    /// <summary>
    ///     String subrecords by (signature, recordType) for quick lookup.
    /// </summary>
    private static readonly HashSet<(string Signature, string? RecordType)>
        s_stringSubrecords = BuildStringSubrecords();

    /// <summary>
    ///     Tracks fallback usage during conversion for diagnostics.
    ///     Key: (RecordType, Subrecord, DataLength, FallbackType)
    /// </summary>
    private static readonly ConcurrentDictionary<(string RecordType, string Subrecord, int DataLength, string FallbackType), int>
        s_fallbackUsage = new();

    /// <summary>
    ///     Whether fallback logging is enabled.
    /// </summary>
    public static bool EnableFallbackLogging { get; set; }

    /// <summary>
    ///     Records a fallback usage for diagnostics.
    /// </summary>
    public static void RecordFallback(string recordType, string subrecord, int dataLength, string fallbackType)
    {
        if (!EnableFallbackLogging)
            return;

        var key = (recordType, subrecord, dataLength, fallbackType);
        s_fallbackUsage.AddOrUpdate(key, 1, (_, count) => count + 1);
    }

    /// <summary>
    ///     Clears all recorded fallback usage.
    /// </summary>
    public static void ClearFallbackLog() => s_fallbackUsage.Clear();

    /// <summary>
    ///     Gets the recorded fallback usage, grouped by type.
    /// </summary>
    public static IEnumerable<(string FallbackType, string RecordType, string Subrecord, int DataLength, int Count)> GetFallbackUsage()
    {
        return s_fallbackUsage
            .Select(kvp => (
                FallbackType: kvp.Key.FallbackType,
                RecordType: kvp.Key.RecordType,
                Subrecord: kvp.Key.Subrecord,
                DataLength: kvp.Key.DataLength,
                Count: kvp.Value))
            .OrderBy(x => x.FallbackType)
            .ThenByDescending(x => x.Count)
            .ThenBy(x => x.RecordType)
            .ThenBy(x => x.Subrecord);
    }

    /// <summary>
    ///     Gets whether any fallbacks were recorded.
    /// </summary>
    public static bool HasFallbackUsage => !s_fallbackUsage.IsEmpty;

    /// <summary>
    ///     Gets the schema for a subrecord, or null if no explicit schema exists.
    ///     Lookup priority:
    ///     1. Exact match (signature + recordType + dataLength)
    ///     2. Signature + recordType (any length)
    ///     3. Signature + dataLength (any record)
    ///     4. Signature only (default for that signature)
    /// </summary>
    public static SubrecordSchema? GetSchema(string signature, string recordType, int dataLength)
    {
        // IMAD records have special handling - most subrecords are float arrays
        if (recordType == "IMAD")
        {
            var imadSchema = GetImadSchema(signature);
            if (imadSchema != null)
            {
                return imadSchema;
            }
        }

        // Try exact match
        if (s_schemas.TryGetValue(new SchemaKey(signature, recordType, dataLength), out var schema))
        {
            return schema;
        }

        // Try signature + recordType (any length)
        if (s_schemas.TryGetValue(new SchemaKey(signature, recordType), out schema))
        {
            return schema;
        }

        // Try signature + dataLength (any record type)
        if (s_schemas.TryGetValue(new SchemaKey(signature, null, dataLength), out schema))
        {
            return schema;
        }

        // Try signature only
        if (s_schemas.TryGetValue(new SchemaKey(signature), out schema))
        {
            return schema;
        }

        // DATA fallback: mirror switch behavior for small fixed-size blocks
        if (signature == "DATA")
        {
            if (dataLength <= 2)
            {
                RecordFallback(recordType, signature, dataLength, "DATA-ByteArray-Small");
                return SubrecordSchema.ByteArray;
            }

            if (dataLength <= 64 && dataLength % 4 == 0)
            {
                RecordFallback(recordType, signature, dataLength, "DATA-FloatArray");
                return SubrecordSchema.FloatArray;
            }

            // Larger or irregular DATA blocks default to no swap
            RecordFallback(recordType, signature, dataLength, "DATA-ByteArray-Large");
            return SubrecordSchema.ByteArray;
        }

        // WTHR uses keyed *IAD subrecords (e.g., \x00IAD, @IAD, AIAD) for float pairs
        // These are NOT fallbacks - they're explicitly handled as float arrays
        if (recordType == "WTHR" && signature.Length == 4 && signature[1] == 'I' && signature[2] == 'A' &&
            signature[3] == 'D')
        {
            return SubrecordSchema.FloatArray;
        }

        return null;
    }

    /// <summary>
    ///     Gets schema for IMAD (Image Space Adapter) subrecords.
    ///     IMAD records have mostly float array subrecords.
    /// </summary>
    private static SubrecordSchema? GetImadSchema(string signature)
    {
        // EDID is a string - handled by IsStringSubrecord
        if (signature == "EDID")
        {
            return SubrecordSchema.String;
        }

        // Known float array subrecords in IMAD
        if (signature is "DNAM" or "BNAM" or "VNAM" or "TNAM" or "NAM3" or "RNAM" or "SNAM"
            or "UNAM" or "NAM1" or "NAM2" or "WNAM" or "XNAM" or "YNAM" or "NAM4")
        {
            return SubrecordSchema.FloatArray;
        }

        // Keyed *IAD subrecords (e.g., @IAD, AIAD, BIAD, etc.) - time/value float pairs
        if (signature.Length == 4 && signature[1] == 'I' && signature[2] == 'A' && signature[3] == 'D')
        {
            return SubrecordSchema.FloatArray;
        }

        // Unknown IMAD subrecord - treat as float array if divisible by 4
        return SubrecordSchema.FloatArray;
    }

    /// <summary>
    ///     Checks if a subrecord contains string data (no conversion needed).
    /// </summary>
    public static bool IsStringSubrecord(string signature, string recordType)
    {
        // Check record-specific string signatures first (more specific)
        if (s_stringSubrecords.Contains((signature, recordType)))
        {
            return true;
        }

        // Check global string signatures (universal like EDID, FULL, MODL)
        return s_stringSubrecords.Contains((signature, null));
    }

    /// <summary>
    ///     Build the complete schema registry from declarative definitions.
    /// </summary>
    private static Dictionary<SchemaKey, SubrecordSchema> BuildSchemaRegistry()
    {
        var schemas = new Dictionary<SchemaKey, SubrecordSchema>();

        // ========================================================================
        // SIMPLE 4-BYTE FORMID REFERENCES
        // ========================================================================
        // These subrecords are always a single 4-byte FormID reference
        RegisterSimpleFormId(schemas, "NAME", "FormID reference");
        RegisterSimpleFormId(schemas, "TPLT", "Template FormID");
        RegisterSimpleFormId(schemas, "VTCK", "Voice Type FormID");
        RegisterSimpleFormId(schemas, "LNAM", "Load Screen FormID");
        RegisterSimpleFormId(schemas, "LTMP", "Lighting Template FormID");
        RegisterSimpleFormId(schemas, "INAM", "Idle FormID");
        RegisterSimpleFormId(schemas, "REPL", "Repair List FormID");
        RegisterSimpleFormId(schemas, "ZNAM", "Combat Style FormID");
        RegisterSimpleFormId(schemas, "XOWN", "Owner FormID");
        RegisterSimpleFormId(schemas, "XEZN", "Encounter Zone FormID");
        RegisterSimpleFormId(schemas, "XCAS", "Acoustic Space FormID");
        RegisterSimpleFormId(schemas, "XCIM", "Image Space FormID");
        RegisterSimpleFormId(schemas, "XCMO", "Music Type FormID");
        RegisterSimpleFormId(schemas, "XCWT", "Water FormID");
        RegisterSimpleFormId(schemas, "PKID", "Package FormID");
        RegisterSimpleFormId(schemas, "NAM6", "FormID reference 6");
        RegisterSimpleFormId(schemas, "NAM7", "FormID reference 7");
        RegisterSimpleFormId(schemas, "NAM8", "FormID reference 8");
        RegisterSimpleFormId(schemas, "HCLR", "Hair Color FormID");
        RegisterSimpleFormId(schemas, "ETYP", "Equipment Type FormID");
        RegisterSimpleFormId(schemas, "WMI1", "Weapon Mod 1 FormID");
        RegisterSimpleFormId(schemas, "WMI2", "Weapon Mod 2 FormID");
        RegisterSimpleFormId(schemas, "WMI3", "Weapon Mod 3 FormID");
        RegisterSimpleFormId(schemas, "WMS1", "Weapon Mod Scope FormID");
        RegisterSimpleFormId(schemas, "WMS2", "Weapon Mod Scope 2 FormID");
        RegisterSimpleFormId(schemas, "EFID", "Effect ID FormID");
        RegisterSimpleFormId(schemas, "SCRI", "Script FormID");
        RegisterSimpleFormId(schemas, "CSCR", "Companion Script FormID");
        RegisterSimpleFormId(schemas, "BIPL", "Body Part List FormID");
        RegisterSimpleFormId(schemas, "EITM", "Enchantment Item FormID");
        RegisterSimpleFormId(schemas, "TCLT", "Target Creature List FormID");
        RegisterSimpleFormId(schemas, "QSTI", "Quest Stage Item FormID");
        RegisterSimpleFormId(schemas, "SPLO", "Spell List Override FormID");

        // ========================================================================
        // SIMPLE 4-BYTE NON-FORMID VALUES
        // ========================================================================
        // These are 4-byte values but NOT FormID references (floats, indices, etc.)
        RegisterSimple4Byte(schemas, "XCLW", "Water Height float");
        RegisterSimple4Byte(schemas, "RPLI", "Region Point List Index");

        // Additional FormID subrecords (from fallback analysis)
        RegisterSimpleFormId(schemas, "ANAM", "Acoustic Space FormID"); // ASPC, DOOR (TERM is 1 byte handled separately)
        RegisterSimpleFormId(schemas, "CARD", "Card FormID"); // CDCK - FormID for resolution
        RegisterSimpleFormId(schemas, "CSDI", "Sound FormID"); // CREA
        RegisterSimpleFormId(schemas, "GNAM", "Grass FormID"); // LTEX, MSET, ALOC
        RegisterSimpleFormId(schemas, "JNAM", "Jump Target FormID"); // MSET
        RegisterSimpleFormId(schemas, "KNAM", "Keyword FormID"); // INFO, MSET
        RegisterSimpleFormId(schemas, "LVLG", "Global FormID"); // LVLI
        RegisterSimpleFormId(schemas, "MNAM", "Male/Map FormID"); // FURN, REFR (RACE is 0 bytes handled separately)
        RegisterSimpleFormId(schemas, "NAM3", "FormID reference 3"); // WRLD, ALOC
        RegisterSimpleFormId(schemas, "NAM4", "FormID reference 4"); // NPC_, CREA, WRLD
        RegisterSimpleFormId(schemas, "QNAM", "Quest FormID"); // CONT
        RegisterSimpleFormId(schemas, "RAGA", "Ragdoll FormID"); // BPTD
        RegisterSimpleFormId(schemas, "RCIL", "Recipe Item List FormID"); // AMMO, RCPE
        RegisterSimpleFormId(schemas, "RDSB", "Region Sound FormID"); // REGN
        RegisterSimpleFormId(schemas, "SCRO", "Script Object Ref FormID"); // SCPT, TERM, REFR
        RegisterSimpleFormId(schemas, "TCFU", "Topic Count FormID Upper"); // INFO
        RegisterSimpleFormId(schemas, "TCLF", "Topic Count FormID Lower"); // INFO
        RegisterSimpleFormId(schemas, "WNM1", "Weapon Mod Name 1 FormID"); // WEAP
        RegisterSimpleFormId(schemas, "WNM2", "Weapon Mod Name 2 FormID"); // WEAP
        RegisterSimpleFormId(schemas, "WNM3", "Weapon Mod Name 3 FormID"); // WEAP
        RegisterSimpleFormId(schemas, "WNM4", "Weapon Mod Name 4 FormID"); // WEAP
        RegisterSimpleFormId(schemas, "WNM5", "Weapon Mod Name 5 FormID"); // WEAP
        RegisterSimpleFormId(schemas, "WNM6", "Weapon Mod Name 6 FormID"); // WEAP
        RegisterSimpleFormId(schemas, "WNM7", "Weapon Mod Name 7 FormID"); // WEAP
        RegisterSimpleFormId(schemas, "XAMT", "Ammo Type FormID"); // REFR
        RegisterSimpleFormId(schemas, "XEMI", "Emittance FormID"); // REFR
        RegisterSimpleFormId(schemas, "XLKR", "Linked Reference FormID"); // REFR, ACRE, ACHR
        RegisterSimpleFormId(schemas, "XMRC", "Merchant Container FormID"); // ACHR, ACRE
        RegisterSimpleFormId(schemas, "XSRD", "Sound Reference FormID"); // REFR
        RegisterSimpleFormId(schemas, "XTRG", "Target FormID"); // REFR

        // Additional non-FormID 4-byte values
        RegisterSimple4Byte(schemas, "CSDT", "Sound Type"); // CREA - enum value
        RegisterSimple4Byte(schemas, "FLTV", "Float Value"); // GLOB - float
        RegisterSimple4Byte(schemas, "IDLT", "Idle Time"); // IDLM, PACK - time value
        RegisterSimple4Byte(schemas, "INFC", "Info Count"); // DIAL - count
        RegisterSimple4Byte(schemas, "INFX", "Info Index"); // DIAL - index
        RegisterSimple4Byte(schemas, "INTV", "Interval Value"); // CCRD - numeric
        RegisterSimple4Byte(schemas, "NVER", "NavMesh Version"); // NAVI, NAVM, RGDL - version number
        RegisterSimple4Byte(schemas, "PKE2", "Package Entry 2"); // PACK - numeric
        RegisterSimple4Byte(schemas, "PKFD", "Package Float Data"); // PACK - float
        RegisterSimple4Byte(schemas, "QOBJ", "Quest Objective"); // QUST - objective index
        RegisterSimple4Byte(schemas, "RCLR", "Region Color"); // REGN - color value
        RegisterSimple4Byte(schemas, "RCOD", "Recipe Output Data"); // RCPE - numeric
        RegisterSimple4Byte(schemas, "RCQY", "Recipe Quantity"); // RCPE - count
        RegisterSimple4Byte(schemas, "RDAT", "Region Data"); // ASPC - data
        RegisterSimple4Byte(schemas, "RDSI", "Region Sound Index"); // REGN - index
        RegisterSimple4Byte(schemas, "SCRV", "Script Variable"); // SCPT, TERM, REFR - local var ref
        RegisterSimple4Byte(schemas, "XACT", "Activate Parent Flags"); // REFR - flags
        RegisterSimple4Byte(schemas, "XAMC", "Ammo Count"); // REFR - count
        RegisterSimple4Byte(schemas, "XHLP", "Health Percent"); // REFR - percentage
        RegisterSimple4Byte(schemas, "XLCM", "Level Modifier"); // ACRE, ACHR - modifier
        RegisterSimple4Byte(schemas, "XPRD", "Patrol Data"); // REFR - data
        RegisterSimple4Byte(schemas, "XRAD", "Radiation Level"); // REFR - level
        RegisterSimple4Byte(schemas, "XRNK", "Faction Rank"); // REFR - rank index
        RegisterSimple4Byte(schemas, "XSRF", "Sound Reference Flags"); // REFR - flags
        RegisterSimple4Byte(schemas, "XXXX", "Size Prefix"); // WRLD - size

        // RNAM - depends on record type
        // RNAM in INFO/CHAL/CREA/REGN is string or special, others are FormID
        RegisterSimpleFormId(schemas, "RNAM", "FormID");

        // NAM9 - WRLD has 8 bytes (bounds), others have 4 bytes
        schemas[new SchemaKey("NAM9", null, 4)] = SubrecordSchema.Simple4Byte("FormID");

        // ========================================================================
        // CONDITIONAL 4-BYTE SWAPS (depend on data length)
        // ========================================================================
        schemas[new SchemaKey("HNAM", null, 4)] = SubrecordSchema.Simple4Byte();
        schemas[new SchemaKey("ENAM", null, 4)] = SubrecordSchema.Simple4Byte();
        schemas[new SchemaKey("PNAM", null, 4)] = SubrecordSchema.Simple4Byte();
        schemas[new SchemaKey("NAM0", null, 4)] = SubrecordSchema.Simple4Byte();
        schemas[new SchemaKey("VNAM", null, 4)] = SubrecordSchema.Simple4Byte();
        schemas[new SchemaKey("BNAM", null, 4)] = SubrecordSchema.Simple4Byte();
        schemas[new SchemaKey("EPFD", null, 4)] = SubrecordSchema.Simple4Byte();
        schemas[new SchemaKey("XSCL", null, 4)] = SubrecordSchema.Simple4Byte("Scale");
        schemas[new SchemaKey("XCNT", null, 4)] = SubrecordSchema.Simple4Byte("Count");
        schemas[new SchemaKey("XRDS", null, 4)] = SubrecordSchema.Simple4Byte("Radius");
        schemas[new SchemaKey("XCCM", null, 4)] = SubrecordSchema.Simple4Byte("Climate"); // CELL climate override FormID
        schemas[new SchemaKey("XLTW", null, 4)] = SubrecordSchema.Simple4Byte("Water"); // REFR water reference FormID
        schemas[new SchemaKey("INDX", null, 4)] = SubrecordSchema.Simple4Byte("Index");

        // Record-specific 4-byte swaps
        schemas[new SchemaKey("UNAM")] = SubrecordSchema.Simple4Byte(); // Not IMAD
        schemas[new SchemaKey("WNAM")] = SubrecordSchema.Simple4Byte(); // Not IMAD
        schemas[new SchemaKey("YNAM")] = SubrecordSchema.Simple4Byte(); // Not IMAD
        schemas[new SchemaKey("SNAM", "WEAP", 4)] = SubrecordSchema.Simple4Byte("Sound FormID");
        schemas[new SchemaKey("TNAM", null, 4)] = SubrecordSchema.Simple4Byte(); // Not IMAD
        schemas[new SchemaKey("XNAM", null, 4)] = SubrecordSchema.Simple4Byte(); // Not IMAD

        // ========================================================================
        // SIMPLE 2-BYTE SWAPS
        // ========================================================================
        schemas[new SchemaKey("NAM5", null, 2)] = SubrecordSchema.Simple2Byte();
        schemas[new SchemaKey("EAMT")] = SubrecordSchema.Simple2Byte("Enchantment Amount");
        schemas[new SchemaKey("DNAM", "TXST", 2)] = SubrecordSchema.Simple2Byte("Texture Set Flags");
        schemas[new SchemaKey("SNAM", "RACE", 2)] = SubrecordSchema.Simple2Byte();

        // Additional 2-byte swaps (from fallback analysis)
        schemas[new SchemaKey("ATTR", "RACE", 2)] = SubrecordSchema.Simple2Byte("Attribute"); // RACE attributes
        schemas[new SchemaKey("CNAM", "RACE", 2)] = SubrecordSchema.Simple2Byte("Color Index"); // RACE
        schemas[new SchemaKey("EPF3", "PERK", 2)] = SubrecordSchema.Simple2Byte("Perk Entry"); // PERK
        // INDX in QUST is already little-endian on Xbox 360 - DO NOT SWAP!
        schemas[new SchemaKey("INDX", "QUST", 2)] = new SubrecordSchema(F.UInt16LittleEndian("Quest Index"));
        schemas[new SchemaKey("PKPT", "PACK", 2)] = SubrecordSchema.Simple2Byte("Package PT"); // PACK
        schemas[new SchemaKey("PNAM", "WRLD", 2)] = SubrecordSchema.Simple2Byte("Parent World"); // WRLD
        schemas[new SchemaKey("TNAM", "REFR", 2)] = SubrecordSchema.Simple2Byte("Talk Distance"); // REFR

        // ========================================================================
        // RECORD-SPECIFIC 4-BYTE FORMIDS
        // ========================================================================
        schemas[new SchemaKey("CNAM", "NPC_", 4)] = SubrecordSchema.Simple4Byte("Class FormID");
        schemas[new SchemaKey("CNAM", "CREA", 4)] = SubrecordSchema.Simple4Byte("Combat Style FormID");
        schemas[new SchemaKey("CNAM", "REFR", 4)] = SubrecordSchema.Simple4Byte("Audio Location");
        schemas[new SchemaKey("CNAM", "WRLD", 4)] = SubrecordSchema.Simple4Byte("Climate FormID");
        schemas[new SchemaKey("CNAM", "PACK", 4)] = SubrecordSchema.Simple4Byte("Combat Style FormID");
        schemas[new SchemaKey("CNAM", "MSET", 4)] = SubrecordSchema.Simple4Byte("Music Set FormID");
        schemas[new SchemaKey("DNAM", "TERM", 4)] = SubrecordSchema.Simple4Byte();
        schemas[new SchemaKey("FNAM", "LIGH", 4)] = SubrecordSchema.Simple4Byte("Light Flags");
        schemas[new SchemaKey("FNAM", "MSET", 4)] = SubrecordSchema.Simple4Byte("Music Set Flags");
        schemas[new SchemaKey("FNAM", "ALOC", 4)] = SubrecordSchema.Simple4Byte("Flags");
        schemas[new SchemaKey("NAM2", "WRLD", 4)] = SubrecordSchema.Simple4Byte("NAM2 FormID");
        schemas[new SchemaKey("NAM2", "ALOC", 4)] = SubrecordSchema.Simple4Byte("NAM2 FormID");
        schemas[new SchemaKey("NAM5", "CREA", 4)] = SubrecordSchema.Simple4Byte("NAM5 FormID");
        schemas[new SchemaKey("NAM5", "ALOC", 4)] = SubrecordSchema.Simple4Byte("NAM5 FormID");
        schemas[new SchemaKey("SNAM", "ASPC", 4)] = SubrecordSchema.Simple4Byte("Sound FormID");
        schemas[new SchemaKey("SNAM", "ACTI", 4)] = SubrecordSchema.Simple4Byte("Sound FormID");
        schemas[new SchemaKey("SNAM", "TACT", 4)] = SubrecordSchema.Simple4Byte("Sound FormID");
        schemas[new SchemaKey("SNAM", "TERM", 4)] = SubrecordSchema.Simple4Byte("Sound FormID");
        schemas[new SchemaKey("SNAM", "CONT", 4)] = SubrecordSchema.Simple4Byte("Sound FormID");
        schemas[new SchemaKey("IAD", "WTHR", 4)] =
            SubrecordSchema.Simple4Byte("Image Adapter Float"); // Weather float array

        // Record-specific DNAM (multi-float)
        schemas[new SchemaKey("DNAM", "RACE", 8)] =
            new SubrecordSchema(F.Float("Height Male"), F.Float("Height Female"))
            {
                Description = "RACE Height Data"
            };
        schemas[new SchemaKey("DNAM", "ARMO", 12)] =
            new SubrecordSchema(F.Float("AR"), F.Float("Weight"), F.Float("Health"))
            {
                Description = "ARMO Data"
            };
        schemas[new SchemaKey("DNAM", "PWAT", 8)] = new SubrecordSchema(F.Float("Value1"), F.Float("Value2"))
        {
            Description = "Placeable Water Data"
        };
        schemas[new SchemaKey("DNAM", "WRLD", 8)] = new SubrecordSchema(F.Float("Value1"), F.Float("Value2"))
        {
            Description = "World Default Land Height/Water Height"
        };
        schemas[new SchemaKey("DNAM", "INFO", 4)] = SubrecordSchema.Simple4Byte("Response Type");
        schemas[new SchemaKey("DNAM", "WATR")] = SubrecordSchema.FloatArray; // WATR 196 bytes = floats
        schemas[new SchemaKey("DNAM", "NPC_", 28)] = SubrecordSchema.ByteArray; // NPC skill values - no conversion
        // WEAP DNAM - OBJ_WEAP (204 bytes)
        schemas[new SchemaKey("DNAM", "WEAP", 204)] = new SubrecordSchema(
            F.Int8("WeaponType"),
            F.Padding(3),
            F.Float("Speed"),
            F.Float("Reach"),
            F.UInt8("Flags"),
            F.UInt8("HandGripAnim"),
            F.UInt8("AmmoPerShot"),
            F.UInt8("ReloadAnim"),
            F.Float("MinSpread"),
            F.Float("Spread"),
            F.Float("Drift"),
            F.Float("IronFov"),
            F.UInt8("ConditionLevel"),
            F.Padding(3),
            // Xbox 360 stores Projectile FormID in little-endian (already native format)
            F.FormIdLittleEndian("Projectile"),
            F.UInt8("VatToHitChance"),
            F.UInt8("AttackAnim"),
            F.UInt8("NumProjectiles"),
            F.UInt8("EmbeddedConditionValue"),
            F.Float("MinRange"),
            F.Float("MaxRange"),
            F.UInt32("HitBehavior"),
            F.UInt32("FlagsEx"),
            F.Float("AttackMult"),
            F.Float("ShotsPerSec"),
            F.Float("ActionPoints"),
            F.Float("RumbleLeftMotor"),
            F.Float("RumbleRightMotor"),
            F.Float("RumbleDuration"),
            F.Float("DamageToWeaponMult"),
            F.Float("AnimShotsPerSecond"),
            F.Float("AnimReloadTime"),
            F.Float("AnimJamTime"),
            F.Float("AimArc"),
            F.UInt32("Skill"),
            F.UInt32("RumblePattern"),
            F.Float("RumbleWavelength"),
            F.Float("LimbDamageMult"),
            F.UInt32("Resistance"),
            F.Float("IronSightUseMult"),
            F.Float("SemiAutoDelayMin"),
            F.Float("SemiAutoDelayMax"),
            F.Float("CookTimer"),
            F.UInt32("ModActionOne"),
            F.UInt32("ModActionTwo"),
            F.UInt32("ModActionThree"),
            F.Float("ModActionOneValue"),
            F.Float("ModActionTwoValue"),
            F.Float("ModActionThreeValue"),
            F.UInt8("PowerAttackOverrideAnim"),
            F.Padding(3),
            F.UInt32("StrengthRequirement"),
            F.Int8("ModReloadClipAnimation"),
            F.Int8("ModFireAnimation"),
            F.Padding(2),
            F.Float("AmmoRegenRate"),
            F.Float("KillImpulse"),
            F.Float("ModActionOneValueTwo"),
            F.Float("ModActionTwoValueTwo"),
            F.Float("ModActionThreeValueTwo"),
            F.Float("KillImpulseDistance"),
            F.UInt32("SkillRequirement"))
        {
            Description = "Weapon Data"
        };
        schemas[new SchemaKey("DNAM", "IMGS")] = SubrecordSchema.FloatArray; // IMGS 152 bytes = floats
        schemas[new SchemaKey("DNAM", "ADDN", 4)] = SubrecordSchema.Simple4Byte("Addon Flags");
        schemas[new SchemaKey("DNAM", "VTYP", 1)] = SubrecordSchema.ByteArray; // Voice type - single byte
        schemas[new SchemaKey("DNAM", "IPCT", 4)] = SubrecordSchema.Simple4Byte("Impact Data");
        schemas[new SchemaKey("DNAM", "ARMA", 12)] = SubrecordSchema.FloatArray; // ARMA 12 bytes = 3 floats
        schemas[new SchemaKey("DNAM", "MESG", 4)] = SubrecordSchema.Simple4Byte("Message Flags");
        schemas[new SchemaKey("DNAM", "MSET", 4)] = SubrecordSchema.Simple4Byte("Media Set Data");

        // Record-specific SNAM
        schemas[new SchemaKey("SNAM", "ARMO", 12)] = SubrecordSchema.ByteArray; // ARMO SNAM: 12 bytes, no swap
        schemas[new SchemaKey("SNAM", "DOOR", 4)] = SubrecordSchema.Simple4Byte("Sound FormID");
        schemas[new SchemaKey("SNAM", "LIGH", 4)] = SubrecordSchema.Simple4Byte("Sound FormID");
        schemas[new SchemaKey("SNAM", "MSTT", 4)] = SubrecordSchema.Simple4Byte("Sound FormID");
        schemas[new SchemaKey("SNAM", "TREE", 4)] = SubrecordSchema.Simple4Byte("Sound FormID");
        schemas[new SchemaKey("SNAM", "TREE", 20)] = SubrecordSchema.ByteArray; // TREE 20 bytes - no swap
        schemas[new SchemaKey("SNAM", "CREA", 8)] =
            new SubrecordSchema(F.FormId("Faction"), F.UInt8("Rank"), F.Bytes("Unused", 3))
            {
                Description = "Creature Faction Membership"
            };
        schemas[new SchemaKey("SNAM", "NOTE", 4)] = SubrecordSchema.Simple4Byte("Sound FormID");
        schemas[new SchemaKey("SNAM", "WTHR", 8)] = new SubrecordSchema(F.FormId("Sound"), F.UInt32("Type"))
        {
            Description = "Weather Sound"
        };
        schemas[new SchemaKey("SNAM", "NPC_", 8)] =
            new SubrecordSchema(F.FormId("Faction"), F.UInt8("Rank"), F.Bytes("Unused", 3))
            {
                Description = "NPC Faction Membership"
            };
        schemas[new SchemaKey("SNAM", "INFO", 4)] = SubrecordSchema.Simple4Byte("Speaker FormID");
        schemas[new SchemaKey("SNAM", "WATR", 4)] = SubrecordSchema.Simple4Byte("Sound FormID");
        schemas[new SchemaKey("SNAM", "ADDN", 4)] = SubrecordSchema.Simple4Byte("Sound FormID");
        schemas[new SchemaKey("SNAM", "CPTH", 4)] = SubrecordSchema.Simple4Byte("Camera Path Sound");
        schemas[new SchemaKey("SNAM", "IPCT", 4)] = SubrecordSchema.Simple4Byte("Impact Sound");
        schemas[new SchemaKey("SNAM", "CHAL", 4)] = SubrecordSchema.Simple4Byte("Challenge Sound");

        // CHAL DATA (24 bytes) - Challenge data structure
        // Pattern: 2×4B swap + 2×2B swap + 4B swap + 4×2B swap
        schemas[new SchemaKey("DATA", "CHAL", 24)] = new SubrecordSchema(
            F.UInt32("Type"),
            F.UInt32("Threshold"),
            F.UInt16("Flags"),
            F.UInt16("Interval"),
            F.UInt32("Value1"),
            F.UInt16("Value2"),
            F.UInt16("Value3"),
            F.UInt16("Value4"),
            F.UInt16("Value5")
        );

        // NAM1 in IPCT - secondary effect FormID
        schemas[new SchemaKey("NAM1", "IPCT", 4)] = SubrecordSchema.Simple4Byte("Secondary Effect FormID");

        // MSET (Media Set) - FormID references
        // NAM2-NAM7 are strings (audio paths) handled in BuildStringSubrecords
        schemas[new SchemaKey("NAM1", "MSET", 4)] = SubrecordSchema.Simple4Byte("Music Track FormID");
        schemas[new SchemaKey("NAM8", "MSET", 4)] = SubrecordSchema.Simple4Byte("Music Track FormID");
        schemas[new SchemaKey("NAM9", "MSET", 4)] = SubrecordSchema.Simple4Byte("Music Track FormID");
        schemas[new SchemaKey("NAM0", "MSET", 4)] = SubrecordSchema.Simple4Byte("Music Track FormID");
        schemas[new SchemaKey("ANAM", "MSET", 4)] = SubrecordSchema.Simple4Byte("MSET FormID");
        schemas[new SchemaKey("BNAM", "MSET", 4)] = SubrecordSchema.Simple4Byte("MSET FormID");
        schemas[new SchemaKey("CNAM", "MSET", 4)] = SubrecordSchema.Simple4Byte("MSET FormID");
        schemas[new SchemaKey("JNAM", "MSET", 4)] = SubrecordSchema.Simple4Byte("MSET FormID");
        schemas[new SchemaKey("KNAM", "MSET", 4)] = SubrecordSchema.Simple4Byte("MSET FormID");
        schemas[new SchemaKey("LNAM", "MSET", 4)] = SubrecordSchema.Simple4Byte("MSET FormID");
        schemas[new SchemaKey("MNAM", "MSET", 4)] = SubrecordSchema.Simple4Byte("MSET FormID");
        schemas[new SchemaKey("NNAM", "MSET", 4)] = SubrecordSchema.Simple4Byte("MSET FormID");
        schemas[new SchemaKey("ONAM", "MSET", 4)] = SubrecordSchema.Simple4Byte("MSET FormID");
        schemas[new SchemaKey("DNAM", "MSET", 4)] = SubrecordSchema.Simple4Byte("MSET FormID");
        schemas[new SchemaKey("ENAM", "MSET", 4)] = SubrecordSchema.Simple4Byte("MSET FormID");
        schemas[new SchemaKey("FNAM", "MSET", 4)] = SubrecordSchema.Simple4Byte("MSET FormID");
        schemas[new SchemaKey("GNAM", "MSET", 4)] = SubrecordSchema.Simple4Byte("MSET FormID");
        schemas[new SchemaKey("DATA", "MSET", 0)] = SubrecordSchema.Empty;

        // REFR (Placed Object Reference) - NNAM is a FormID (linked ref keyword)
        schemas[new SchemaKey("NNAM", "REFR", 4)] = SubrecordSchema.Simple4Byte("Linked Ref Keyword FormID");

        // ALOC (Media Location Controller) - mostly FormID references
        schemas[new SchemaKey("NAM1", "ALOC", 4)] = SubrecordSchema.Simple4Byte("Location Controller FormID");
        schemas[new SchemaKey("NAM2", "ALOC", 4)] = SubrecordSchema.Simple4Byte("Location Controller FormID");
        schemas[new SchemaKey("NAM3", "ALOC", 4)] = SubrecordSchema.Simple4Byte("Location Controller FormID");
        schemas[new SchemaKey("NAM4", "ALOC", 4)] = SubrecordSchema.Simple4Byte("Location Controller FormID");
        schemas[new SchemaKey("NAM5", "ALOC", 4)] = SubrecordSchema.Simple4Byte("Location Controller FormID");
        schemas[new SchemaKey("NAM6", "ALOC", 4)] = SubrecordSchema.Simple4Byte("Location Controller FormID");
        schemas[new SchemaKey("NAM7", "ALOC", 4)] = SubrecordSchema.Simple4Byte("Location Controller FormID");
        schemas[new SchemaKey("GNAM", "ALOC", 4)] = SubrecordSchema.Simple4Byte("Location Controller FormID");
        schemas[new SchemaKey("LNAM", "ALOC", 4)] = SubrecordSchema.Simple4Byte("Location Controller FormID");
        schemas[new SchemaKey("HNAM", "ALOC", 4)] = new SubrecordSchema(F.FormId("Location Controller FormID"));
        schemas[new SchemaKey("ZNAM", "ALOC", 4)] = new SubrecordSchema(F.FormId("Location Controller FormID"));
        schemas[new SchemaKey("XNAM", "ALOC", 4)] = new SubrecordSchema(F.FormId("Location Controller FormID"));
        schemas[new SchemaKey("YNAM", "ALOC", 4)] = new SubrecordSchema(F.FormId("Location Controller FormID"));
        schemas[new SchemaKey("RNAM", "ALOC", 4)] = new SubrecordSchema(F.FormId("Location Controller FormID"));
        schemas[new SchemaKey("FNAM", "ALOC", 4)] = SubrecordSchema.Simple4Byte("Location Controller Flags");

        // Record-specific ENIT (enchantment/ingredient info)
        schemas[new SchemaKey("ENIT", "ENCH", 16)] = new SubrecordSchema(
                F.UInt32("Type"),
                F.UInt32("ChargeAmount"),
                F.UInt32("EnchantCost"),
                F.Bytes("Flags", 4)) // 4 bytes of flags (not swapped)
        {
            Description = "Enchantment Data"
        };
        schemas[new SchemaKey("ENIT", "INGR", 8)] = new SubrecordSchema(
            F.UInt32("Value"),
            F.UInt32("Flags"))
        {
            Description = "Ingredient Data"
        };
        // ALCH ENIT is 5 dwords in FNV/FO3-style records:
        //   Value (u32), Flags (u32), Addiction (FormID), AddictionChance (float), UseSound/WithdrawalEffect (FormID)
        // The addiction FormID being left in Xbox endian produces runtime errors like "Failed to find addiction item (00690600)".
        // NOTE: Flags is raw bytes (not swapped) - Xbox and PC have identical byte patterns (only first byte used,
        // remaining 3 bytes are uninitialized 0xCD from debug builds).
        schemas[new SchemaKey("ENIT", "ALCH", 20)] = new SubrecordSchema(
            F.UInt32("Value"),
            F.Bytes("Flags", 4),  // Not swapped - same raw bytes on Xbox and PC
            F.FormId("Addiction"),
            F.Float("AddictionChance"),
            F.FormId("UseSoundOrWithdrawalEffect"))
        {
            Description = "Alchemy/Potion Data"
        };

        // DOBJ (DefaultObjectManager) - DATA is a fixed array of 4-byte FormIDs in FNV.
        // If left in Xbox endian, the game logs errors like:
        //   "Unable to find valid 'Stimpak' default object for ID (00510100)."
        // PC reference starts with: 69 51 01 00 ... but Xbox source starts with: 00 01 51 69 ...
        schemas[new SchemaKey("DATA", "DOBJ", 136)] = SubrecordSchema.FormIdArray;

        // ========================================================================
        // SINGLE BYTE (FLAGS) - NO CONVERSION NEEDED
        // ========================================================================
        schemas[new SchemaKey("FNAM", "REFR", 1)] = SubrecordSchema.ByteArray; // REFR flags
        schemas[new SchemaKey("FNAM", "WATR", 1)] = SubrecordSchema.ByteArray; // Water flags
        schemas[new SchemaKey("ANAM", "TERM", 1)] = SubrecordSchema.ByteArray; // Terminal type
        schemas[new SchemaKey("BRUS", "STAT", 1)] = SubrecordSchema.ByteArray; // Brush flags
        schemas[new SchemaKey("CSDC", "CREA", 1)] = SubrecordSchema.ByteArray; // Sound chance
        schemas[new SchemaKey("EPFT", "PERK", 1)] = SubrecordSchema.ByteArray; // Perk entry type
        schemas[new SchemaKey("FNAM", "GLOB", 1)] = SubrecordSchema.ByteArray; // Global type
        schemas[new SchemaKey("FNAM", "DOOR", 1)] = SubrecordSchema.ByteArray; // Door flags
        schemas[new SchemaKey("IDLC", null, 1)] = SubrecordSchema.ByteArray; // Idle count (IDLM, PACK)
        schemas[new SchemaKey("IDLF", null, 1)] = SubrecordSchema.ByteArray; // Idle flags (IDLM, PACK)
        schemas[new SchemaKey("LVLD", null, 1)] = SubrecordSchema.ByteArray; // Level data (LVLC, LVLN, LVLI)
        schemas[new SchemaKey("LVLF", null, 1)] = SubrecordSchema.ByteArray; // Level flags (LVLC, LVLN, LVLI)
        schemas[new SchemaKey("MODD", null, 1)] = SubrecordSchema.ByteArray; // Model data flags (RACE, ARMO, WEAP)
        schemas[new SchemaKey("MOSD", null, 1)] = SubrecordSchema.ByteArray; // Model SD flags (ARMO, ARMA)
        schemas[new SchemaKey("PNAM", "MSET", 1)] = SubrecordSchema.ByteArray; // Music set priority
        schemas[new SchemaKey("PRKC", "PERK", 1)] = SubrecordSchema.ByteArray; // Perk condition
        schemas[new SchemaKey("QSDT", "QUST", 1)] = SubrecordSchema.ByteArray; // Quest stage data
        schemas[new SchemaKey("SNAM", "LTEX", 1)] = SubrecordSchema.ByteArray; // Specular
        schemas[new SchemaKey("XAPD", null, 1)] = SubrecordSchema.ByteArray; // Activate parent delay (REFR, ACHR, ACRE)
        schemas[new SchemaKey("XSED", "REFR", 1)] = SubrecordSchema.ByteArray; // Speed tree seed

        // ========================================================================
        // ZERO-BYTE MARKERS - NO DATA
        // ========================================================================
        schemas[new SchemaKey("DSTF", null, 0)] =
            SubrecordSchema.ByteArray; // Destruction stage flag (ACTI, CONT, MSTT)
        schemas[new SchemaKey("FNAM", "RACE", 0)] = SubrecordSchema.ByteArray; // Race flag marker
        schemas[new SchemaKey("MMRK", "REFR", 0)] = SubrecordSchema.ByteArray; // Map marker
        schemas[new SchemaKey("NAM0", "RACE", 0)] = SubrecordSchema.ByteArray; // Race name marker
        schemas[new SchemaKey("NAM2", "RACE", 0)] = SubrecordSchema.ByteArray; // Race name 2 marker
        schemas[new SchemaKey("NEXT", "INFO", 0)] = SubrecordSchema.ByteArray; // Next marker
        schemas[new SchemaKey("PKAM", "PACK", 0)] = SubrecordSchema.ByteArray; // Package am marker
        schemas[new SchemaKey("PKED", "PACK", 0)] = SubrecordSchema.ByteArray; // Package ed marker
        schemas[new SchemaKey("POBA", "PACK", 0)] = SubrecordSchema.ByteArray; // Package ob a marker
        schemas[new SchemaKey("POCA", "PACK", 0)] = SubrecordSchema.ByteArray; // Package oc a marker
        schemas[new SchemaKey("POEA", "PACK", 0)] = SubrecordSchema.ByteArray; // Package oe a marker
        schemas[new SchemaKey("PRKF", "PERK", 0)] = SubrecordSchema.ByteArray; // Perk flag marker
        schemas[new SchemaKey("PUID", "PACK", 0)] = SubrecordSchema.ByteArray; // Package uid marker
        schemas[new SchemaKey("XIBS", null, 0)] = SubrecordSchema.ByteArray; // Ignored by sandbox (REFR, ACHR)
        schemas[new SchemaKey("XMRK", "REFR", 0)] = SubrecordSchema.ByteArray; // Map marker data
        schemas[new SchemaKey("XPPA", "REFR", 0)] = SubrecordSchema.ByteArray; // Patrol point

        // ========================================================================
        // BYTE ARRAYS - NO CONVERSION NEEDED
        // ========================================================================
        schemas[new SchemaKey("ATTR", null, 7)] = SubrecordSchema.ByteArray; // CLAS: 7 S.P.E.C.I.A.L attributes
        schemas[new SchemaKey("VNML")] = SubrecordSchema.ByteArray; // LAND vertex normals (33×33×3 = 3267)
        schemas[new SchemaKey("VCLR")] = SubrecordSchema.ByteArray; // LAND vertex colors (33×33×3 = 3267)
        schemas[new SchemaKey("SCDA")] = SubrecordSchema.ByteArray; // Compiled script bytecode
        schemas[new SchemaKey("PRKE", null, 3)] = SubrecordSchema.ByteArray; // PERK effect header (3 uint8s)
        schemas[new SchemaKey("TNAM", "CLMT", 6)] = SubrecordSchema.ByteArray; // Climate timing (all bytes)
        schemas[new SchemaKey("ONAM", "WTHR", 4)] = SubrecordSchema.ByteArray; // Sun glare RGBA color (NOT a FormID)
        // Note: PNAM and NAM0 for WTHR handled specially in processor (ARGB→BGRA color conversion)

        // ========================================================================
        // REPEATING ARRAY STRUCTURES
        // ========================================================================

        // VTXT - Vertex Texture Blend (repeating 8-byte entries for LAND)
        // Each entry: uint16 Position + uint16 Unused + float Opacity
        schemas[new SchemaKey("VTXT", "LAND")] = new SubrecordSchema(
            F.UInt16("Position"),
            F.Bytes("Unused", 2),
            F.Float("Opacity"))
        {
            ExpectedSize = -1, // Repeating array (8 bytes per entry)
            Description = "Land Vertex Texture Blend Array"
        };

        // ========================================================================
        // FIXED-SIZE STRUCTURES
        // ========================================================================

        // OBND - Object Bounds (12 bytes = 6 × int16)
        schemas[new SchemaKey("OBND", null, 12)] = new SubrecordSchema(
            F.Int16("X1"), F.Int16("Y1"), F.Int16("Z1"),
            F.Int16("X2"), F.Int16("Y2"), F.Int16("Z2"))
        {
            Description = "Object Bounds"
        };

        // SLSD - Script Local Variable Data (24 bytes)
        schemas[new SchemaKey("SLSD", null, 24)] = new SubrecordSchema(
            F.UInt32("Index"),
            F.Bytes("Unused", 12),
            F.UInt8("Type"),
            F.Bytes("Unused2", 7))
        {
            Description = "Script Local Variable Data"
        };

        // XNAM - FACT Relation (12 bytes)
        schemas[new SchemaKey("XNAM", "FACT", 12)] = new SubrecordSchema(
            F.FormId("Faction"),
            F.Int32("Modifier"),
            F.UInt32("CombatReaction"))
        {
            Description = "Faction Relation"
        };

        // CNTO - Container Item (8 bytes)
        schemas[new SchemaKey("CNTO", null, 8)] = new SubrecordSchema(
            F.FormId("Item"),
            F.Int32("Count"))
        {
            Description = "Container Item"
        };

        // CTDA - Condition (28 bytes)
        // Pattern: 4B same + 4B swap + 2×2B swap + 4×4B swap
        schemas[new SchemaKey("CTDA", null, 28)] = new SubrecordSchema(
            F.Padding(4), // TypeAndFlags - doesn't need swap
            F.Float("ComparisonValue"),
            F.UInt16("ComparisonType"),
            F.UInt16("FunctionIndex"),
            F.FormId("Parameter1"),
            F.UInt32("Parameter2"),
            F.UInt32("RunOn"),
            F.FormId("Reference"))
        {
            Description = "Condition Data"
        };

        // XCLL - Cell Lighting (40 bytes = 10 × uint32)
        schemas[new SchemaKey("XCLL", null, 40)] = new SubrecordSchema(
            F.UInt32("AmbientColor"), F.UInt32("DirectionalColor"),
            F.UInt32("FogColor"), F.Float("FogNear"), F.Float("FogFar"),
            F.Int32("DirectionalRotationXY"), F.Int32("DirectionalRotationZ"),
            F.Float("DirectionalFade"), F.Float("FogClipDistance"), F.Float("FogPow"))
        {
            Description = "Cell Lighting"
        };

        // XNDP - Navigation Door Portal (8 bytes)
        schemas[new SchemaKey("XNDP", null, 8)] = new SubrecordSchema(
            F.FormId("Navmesh"),
            F.UInt16("TriangleIndex"),
            F.Padding(2))
        {
            Description = "Navigation Door Portal"
        };

        // XESP - Enable Parent (8 bytes)
        schemas[new SchemaKey("XESP", null, 8)] = new SubrecordSchema(
            F.FormId("ParentRef"),
            F.UInt32("Flags"))
        {
            Description = "Enable Parent"
        };

        // XTEL - Door Teleport (32 bytes = 8 × uint32/float)
        schemas[new SchemaKey("XTEL", null, 32)] = new SubrecordSchema(
            F.FormId("DestinationDoor"),
            F.Float("PosX"), F.Float("PosY"), F.Float("PosZ"),
            F.Float("RotX"), F.Float("RotY"), F.Float("RotZ"),
            F.UInt32("Flags"))
        {
            Description = "Door Teleport Destination"
        };

        // XLKR - Linked Reference (8 bytes)
        schemas[new SchemaKey("XLKR", null, 8)] = new SubrecordSchema(
            F.FormId("Keyword"),
            F.FormId("Reference"))
        {
            Description = "Linked Reference"
        };

        // XAPR - Activation Parent (8 bytes)
        schemas[new SchemaKey("XAPR", null, 8)] = new SubrecordSchema(
            F.FormId("Reference"),
            F.Float("Delay"))
        {
            Description = "Activation Parent"
        };

        // XLOC - Lock Information (20 bytes for FNV)
        schemas[new SchemaKey("XLOC", null, 20)] = new SubrecordSchema(
            F.UInt8("Level"),
            F.Padding(3),
            F.FormId("Key"),
            F.UInt32("Flags"),
            F.Padding(8))
        {
            Description = "Lock Information"
        };

        // XLOD - LOD Data (12 bytes = 3 floats)
        schemas[new SchemaKey("XLOD", null, 12)] = new SubrecordSchema(F.Vec3("LOD"))
        {
            Description = "LOD Data"
        };

        // EFIT - Effect Item (20 bytes = 5 × uint32)
        schemas[new SchemaKey("EFIT", null, 20)] = new SubrecordSchema(
            F.UInt32("Magnitude"), F.UInt32("Area"), F.UInt32("Duration"),
            F.UInt32("Type"), F.UInt32("ActorValue"))
        {
            Description = "Effect Item"
        };

        // PKDT - Package Data (12 bytes)
        // Pattern: 1B same + 2B swap + 3B same + 2×2B swap + 2B same
        schemas[new SchemaKey("PKDT", null, 12)] = new SubrecordSchema(
            F.UInt8("Flags1"),
            F.UInt16("Flags2"),
            F.UInt8("Type"),
            F.UInt8("Unused1"),
            F.UInt8("Unused2"),
            F.UInt16("FalloutBehaviorFlags"),
            F.UInt16("TypeSpecificFlags"),
            F.UInt8("Unknown1"),
            F.UInt8("Unknown2"))
        {
            Description = "Package Data"
        };

        // PSDT - Package Schedule Data (8 bytes)
        schemas[new SchemaKey("PSDT", null, 8)] = new SubrecordSchema(
            F.UInt8("Month"),
            F.UInt8("DayOfWeek"),
            F.UInt8("Date"),
            F.Int8("Time"),
            F.Int32("Duration"))
        {
            Description = "Package Schedule Data"
        };

        // CRDT - Critical Data (16 bytes)
        // OBJ_WEAP_CRITICAL
        // Xbox 360 stores CriticalEffect FormID in little-endian (already native format)
        schemas[new SchemaKey("CRDT", null, 16)] = new SubrecordSchema(
            F.UInt16("CriticalDamage"),
            F.Padding(2),
            F.Float("CriticalChanceMult"),
            F.UInt8("EffectOnDeath"),
            F.Padding(3),
            F.FormIdLittleEndian("CriticalEffect"))
        {
            Description = "Critical Data"
        };

        // SNDD - Sound Data (36 bytes)
        schemas[new SchemaKey("SNDD", "SOUN", 36)] = new SubrecordSchema(
            F.UInt8("MinAttenuationDistance"),
            F.UInt8("MaxAttenuationDistance"),
            F.Int8("FreqAdjustment"),
            F.Padding(1),
            F.UInt32("Flags"),
            F.Int16("StaticAttenuation"),
            F.UInt8("EndTime"),
            F.UInt8("StartTime"),
            F.Int16("Attenuation1"), F.Int16("Attenuation2"), F.Int16("Attenuation3"),
            F.Int16("Attenuation4"), F.Int16("Attenuation5"),
            F.Int16("ReverbAttenuation"),
            F.Int32("Priority"),
            F.Int32("LoopBegin"),
            F.Int32("LoopEnd"))
        {
            Description = "Sound Data"
        };

        // DODT - Decal Data (36 bytes)
        // Color is stored as ARGB on Xbox 360, needs conversion to RGBA for PC
        schemas[new SchemaKey("DODT", null, 36)] = new SubrecordSchema(
            F.Float("MinWidth"), F.Float("MaxWidth"),
            F.Float("MinHeight"), F.Float("MaxHeight"),
            F.Float("Depth"), F.Float("Shininess"), F.Float("ParallaxScale"),
            F.UInt8("Passes"), F.UInt8("Flags"), F.Padding(2),
            F.ColorArgb("Color"))
        {
            Description = "Decal Data"
        };

        // DAT2 - AMMO Secondary Data (20 bytes)
        schemas[new SchemaKey("DAT2", null, 20)] = new SubrecordSchema(
            F.Padding(8),
            F.UInt32("Value1"),
            F.Padding(4),
            F.UInt32("Value2"))
        {
            Description = "AMMO Secondary Data"
        };

        // VATS - VATS Data (20 bytes)
        // OBJ_WEAP_VATS_SPECIAL
        // Xbox 360 stores VatSpecialEffect FormID in little-endian (already native format)
        schemas[new SchemaKey("VATS", "WEAP", 20)] = new SubrecordSchema(
            F.FormIdLittleEndian("VatSpecialEffect"),
            F.Float("VatSpecialAP"),
            F.Float("VatSpecialMultiplier"),
            F.Float("VatSkillRequired"),
            F.UInt8("Silent"),
            F.UInt8("ModRequired"),
            F.UInt8("Flags"),
            F.Padding(1))
        {
            Description = "VATS Data (Weapon)"
        };

        // ATXT/BTXT - Texture Alpha (8 bytes)
        // Note: byte 5 needs platform conversion (see special handler)
        schemas[new SchemaKey("ATXT", null, 8)] = new SubrecordSchema(
            F.FormId("Texture"),
            F.UInt8("Quadrant"),
            F.UInt8("PlatformFlag"),
            F.UInt16("Layer"))
        {
            Description = "Alpha Texture"
        };
        schemas[new SchemaKey("BTXT", null, 8)] = new SubrecordSchema(
            F.FormId("Texture"),
            F.UInt8("Quadrant"),
            F.UInt8("PlatformFlag"),
            F.UInt16("Layer"))
        {
            Description = "Base Texture"
        };

        // VHGT - Height Data (1096 bytes)
        schemas[new SchemaKey("VHGT", null, 1096)] = new SubrecordSchema(
            F.Float("HeightOffset"),
            F.Bytes("HeightData", 1089),
            F.Padding(3))
        {
            Description = "Vertex Height Data"
        };

        // HNAM - LTEX Havok Data (3 bytes = uint8 values)
        schemas[new SchemaKey("HNAM", "LTEX", 3)] = SubrecordSchema.ByteArray;

        // HNAM - RACE Hair (variable, array of FormIDs)
        schemas[new SchemaKey("HNAM", "RACE")] = SubrecordSchema.FormIdArray;

        // ENAM - RACE Eyes (variable, array of FormIDs)
        schemas[new SchemaKey("ENAM", "RACE")] = SubrecordSchema.FormIdArray;

        // ONAM - RACE Older Race (4 bytes = single FormID)
        schemas[new SchemaKey("ONAM", "RACE", 4)] = SubrecordSchema.Simple4Byte("Older Race FormID");

        // ONAM - SCOL Static Object Reference (4 bytes = single FormID)
        schemas[new SchemaKey("ONAM", "SCOL", 4)] = SubrecordSchema.Simple4Byte("Static Object FormID");

        // ONAM - NOTE sound/image reference (4 bytes = single FormID)
        schemas[new SchemaKey("ONAM", "NOTE", 4)] = SubrecordSchema.Simple4Byte("Note Object FormID");

        // ONAM - REFR (0 bytes = marker for open-by-default doors)
        schemas[new SchemaKey("ONAM", "REFR", 0)] = SubrecordSchema.Empty;

        // ONAM - AMMO Short Name (variable length string, NOT a FormID!)
        // Note: This is handled via the string subrecords list in BuildStringSubrecords()

        // YNAM - RACE Younger Race (4 bytes = single FormID)
        // Note: Generic YNAM is already registered, but this is explicit for RACE
        schemas[new SchemaKey("YNAM", "RACE", 4)] = SubrecordSchema.Simple4Byte("Younger Race FormID");

        // VTCK - RACE Voices (8 bytes = two FormIDs: Male + Female voice types)
        schemas[new SchemaKey("VTCK", "RACE", 8)] = new SubrecordSchema(
            F.FormId("Male Voice Type"),
            F.FormId("Female Voice Type"))
        {
            Description = "RACE Voice Types"
        };

        // NAM1/MNAM/FNAM in RACE - Zero-byte marker subrecords (delimiters)
        // These are empty markers that separate sections within RACE records.
        // No conversion needed, just pass through.
        schemas[new SchemaKey("NAM1", "RACE", 0)] = SubrecordSchema.Empty;
        schemas[new SchemaKey("MNAM", "RACE", 0)] = SubrecordSchema.Empty;
        schemas[new SchemaKey("FNAM", "RACE", 0)] = SubrecordSchema.Empty;

        // FGGS/FGTS - Face Geometry (200 bytes = 50 floats)
        schemas[new SchemaKey("FGGS", null, 200)] = SubrecordSchema.FloatArray;
        schemas[new SchemaKey("FGTS", null, 200)] = SubrecordSchema.FloatArray;

        // FGGA - Facegen Asymmetric (120 bytes = 30 floats)
        schemas[new SchemaKey("FGGA", null, 120)] = SubrecordSchema.FloatArray;

        // BMDT - Biped Model Data (8 bytes)
        schemas[new SchemaKey("BMDT", null, 8)] = new SubrecordSchema(
            F.UInt32("BipedFlags"),
            F.Padding(4))
        {
            Description = "Biped Model Data"
        };

        // MODT/MO2T/MO3T/MO4T/DMDT - Model Texture Hashes (24 bytes per texture)
        // NOTE: Xbox 360 and PC data are byte-identical for these blocks; do NOT endian-swap.
        schemas[new SchemaKey("MODT")] = SubrecordSchema.ByteArray;
        schemas[new SchemaKey("MO2T")] = SubrecordSchema.ByteArray;
        schemas[new SchemaKey("MO3T")] = SubrecordSchema.ByteArray;
        schemas[new SchemaKey("MO4T")] = SubrecordSchema.ByteArray;
        schemas[new SchemaKey("DMDT")] = SubrecordSchema.ByteArray;

        // NAM2 - Model Info in PROJ
        schemas[new SchemaKey("NAM2", "PROJ")] = SubrecordSchema.ByteArray;

        // MO*S - Model Shader Data (variable, starts with uint32 count)
        schemas[new SchemaKey("MO2S")] = new SubrecordSchema(F.UInt32("Count"))
        {
            ExpectedSize = 0, // Variable
            Description = "Model Shader Data"
        };
        schemas[new SchemaKey("MO3S")] = new SubrecordSchema(F.UInt32("Count")) { ExpectedSize = 0 };
        schemas[new SchemaKey("MO4S")] = new SubrecordSchema(F.UInt32("Count")) { ExpectedSize = 0 };
        schemas[new SchemaKey("MODS")] = new SubrecordSchema(F.UInt32("Count")) { ExpectedSize = 0 };

        // NIFT - NIF Data
        schemas[new SchemaKey("NIFT")] = new SubrecordSchema(F.UInt32("Count"))
        {
            ExpectedSize = 0,
            Description = "NIF Data"
        };

        // HEDR - File Header (12 bytes)
        schemas[new SchemaKey("HEDR", null, 12)] = new SubrecordSchema(
            F.Float("Version"),
            F.UInt32("NumRecords"),
            F.UInt32("NextObjectId"))
        {
            Description = "File Header"
        };

        // SPIT - Spell Data (16 bytes)
        schemas[new SchemaKey("SPIT", "SPEL", 16)] = new SubrecordSchema(
            F.UInt32("Type"),
            F.UInt32("Cost"),
            F.UInt32("Level"),
            F.UInt8("Flags"),
            F.Padding(3))
        {
            Description = "Spell Data"
        };

        // DEST - Destructible Header (8 bytes)
        schemas[new SchemaKey("DEST", null, 8)] = new SubrecordSchema(
            F.Int32("Health"),
            F.UInt8("Count"),
            F.UInt8("Flags"),
            F.Padding(2))
        {
            Description = "Destructible Header"
        };

        // DSTD - Destruction Stage Data (20 bytes)
        schemas[new SchemaKey("DSTD", null, 20)] = new SubrecordSchema(
            F.UInt8("HealthPercent"),
            F.UInt8("Index"),
            F.UInt8("DamageStage"),
            F.UInt8("Flags"),
            F.Int32("SelfDamagePerSecond"),
            F.FormId("Explosion"),
            F.FormId("Debris"),
            F.Int32("DebrisCount"))
        {
            Description = "Destruction Stage Data"
        };

        // COED - Extra Data (12 bytes)
        schemas[new SchemaKey("COED", null, 12)] = new SubrecordSchema(
            F.FormId("Owner"),
            F.UInt32("GlobalOrRank"),
            F.Float("ItemCondition"))
        {
            Description = "Owner Extra Data"
        };

        // CNAM - Tree Data (32 bytes)
        schemas[new SchemaKey("CNAM", "TREE", 32)] = new SubrecordSchema(
            F.Float("LeafCurvature"), F.Float("MinLeafAngle"), F.Float("MaxLeafAngle"),
            F.Float("BranchDimmingValue"), F.Float("LeafDimmingValue"),
            F.Float("ShadowRadius"), F.Float("RockSpeed"),
            F.Float("RustleSpeed"))
        {
            Description = "Tree Data"
        };

        // BNAM - Tree Billboard (8 bytes)
        schemas[new SchemaKey("BNAM", "TREE", 8)] = new SubrecordSchema(
            F.Float("Width"),
            F.Float("Height"))
        {
            Description = "Billboard Dimensions"
        };

        // LVLO - Leveled List Entry (12 bytes)
        schemas[new SchemaKey("LVLO", null, 12)] = new SubrecordSchema(
            F.UInt16("Level"),
            F.Padding(2),
            F.FormId("Entry"),
            F.UInt16("Count"),
            F.Padding(2))
        {
            Description = "Leveled List Entry"
        };

        // IDLA - Idle Marker Animations (array of FormIDs)
        schemas[new SchemaKey("IDLA")] = SubrecordSchema.FormIdArray;

        // OFST - Worldspace offset table (array of uint32)
        schemas[new SchemaKey("OFST", "WRLD")] = SubrecordSchema.FormIdArray;

        // ONAM - Worldspace persistent cell list (array of FormIDs)
        schemas[new SchemaKey("ONAM", "WRLD")] = SubrecordSchema.FormIdArray;

        // FNAM - Weather Fog Distance (24 bytes = 6 floats)
        schemas[new SchemaKey("FNAM", "WTHR", 24)] = SubrecordSchema.FloatArray;

        // PNAM - Weather Cloud Colors (96 bytes = 24 × 4-byte color values)
        schemas[new SchemaKey("PNAM", "WTHR", 96)] = SubrecordSchema.FormIdArray; // FormIdArray = 4-byte endian swap per entry

        // NAM0 - Weather Colors (240 bytes = 60 × 4-byte color values)
        schemas[new SchemaKey("NAM0", "WTHR", 240)] = SubrecordSchema.FormIdArray; // FormIdArray = 4-byte endian swap per entry

        // INAM - Weather Image Spaces (304 bytes = 76 floats for IMGS modifiers)
        schemas[new SchemaKey("INAM", "WTHR", 304)] = SubrecordSchema.FloatArray;

        // WLST - Weather Types (12 bytes per entry)
        schemas[new SchemaKey("WLST")] = new SubrecordSchema(
            F.FormId("Weather"),
            F.Int32("Chance"),
            F.FormId("Global"))
        {
            ExpectedSize = -1, // Repeating array
            Description = "Weather Types Array"
        };

        // RDAT - Region Data Header (8 bytes)
        schemas[new SchemaKey("RDAT", null, 8)] = new SubrecordSchema(
            F.UInt32("Type"),
            F.UInt8("Override"),
            F.UInt8("Priority"),
            F.Padding(2))
        {
            Description = "Region Data Header"
        };

        // RDSD - Region Sounds (12 bytes per entry)
        schemas[new SchemaKey("RDSD")] = new SubrecordSchema(
            F.FormId("Sound"),
            F.UInt32("Flags"),
            F.UInt32("Chance"))
        {
            ExpectedSize = -1, // Repeating array
            Description = "Region Sounds Array"
        };

        // RDID - Region Imposters (array of FormIDs)
        schemas[new SchemaKey("RDID")] = SubrecordSchema.FormIdArray;

        // RDWT - Region Weather Types (12 bytes per entry)
        schemas[new SchemaKey("RDWT")] = new SubrecordSchema(
            F.FormId("Weather"),
            F.UInt32("Chance"),
            F.FormId("Global"))
        {
            ExpectedSize = -1, // Repeating array
            Description = "Region Weather Types Array"
        };

        // RPLD - Region Point List Data (array of X,Y float pairs)
        schemas[new SchemaKey("RPLD")] = SubrecordSchema.FloatArray;

        // RDOT - Region Objects (52 bytes per entry)
        schemas[new SchemaKey("RDOT")] = new SubrecordSchema(
            F.FormId("Object"),
            F.UInt16("ParentIndex"),
            F.Padding(2),
            F.Float("Density"),
            F.UInt8("Clustering"),
            F.UInt8("MinSlope"),
            F.UInt8("MaxSlope"),
            F.UInt8("Flags"),
            F.UInt16("RadiusWrtParent"),
            F.UInt16("Radius"),
            F.Float("MinHeight"),
            F.Float("MaxHeight"),
            F.Float("Sink"),
            F.Float("SinkVariance"),
            F.Float("SizeVariance"),
            F.UInt16("AngleVarianceX"),
            F.UInt16("AngleVarianceY"),
            F.UInt16("AngleVarianceZ"),
            F.Padding(6))
        {
            ExpectedSize = -1, // Repeating array
            Description = "Region Objects Array"
        };

        // Register more schemas...
        RegisterNavmeshSchemas(schemas);
        RegisterMiscSchemas(schemas);
        RegisterDataSubrecordSchemas(schemas);

        return schemas;
    }

    /// <summary>
    ///     Register navmesh-related schemas.
    /// </summary>
    private static void RegisterNavmeshSchemas(Dictionary<SchemaKey, SubrecordSchema> schemas)
    {
        // NVVX - Navmesh Vertices (array of Vec3, 12 bytes each)
        schemas[new SchemaKey("NVVX")] = SubrecordSchema.FloatArray;

        // NVTR - Navmesh Triangles (16 bytes each)
        schemas[new SchemaKey("NVTR")] = new SubrecordSchema(
            F.UInt16("Vertex0"), F.UInt16("Vertex1"), F.UInt16("Vertex2"),
            F.Int16("Edge01"), F.Int16("Edge12"), F.Int16("Edge20"),
            F.UInt16("Flags"), F.UInt16("CoverFlags"))
        {
            ExpectedSize = -1, // Repeating array
            Description = "Navmesh Triangles Array"
        };

        // NVCA - Cover Triangles (array of uint16)
        schemas[new SchemaKey("NVCA")] = new SubrecordSchema(F.UInt16("Triangle"))
        {
            ExpectedSize = -1, // Repeating array
            Description = "Navmesh Cover Triangles"
        };

        // NVDP - Navmesh Door Links (8 bytes each)
        schemas[new SchemaKey("NVDP")] = new SubrecordSchema(
            F.FormId("DoorRef"),
            F.UInt16("Triangle"),
            F.Padding(2))
        {
            ExpectedSize = -1, // Repeating array
            Description = "Navmesh Door Links Array"
        };

        // NVEX - Navmesh Edge Links (10 bytes each)
        schemas[new SchemaKey("NVEX")] = new SubrecordSchema(
            F.UInt32("Type"),
            F.FormId("Navmesh"),
            F.UInt16("Triangle"))
        {
            ExpectedSize = -1, // Repeating array
            Description = "Navmesh Edge Links Array"
        };
    }

    /// <summary>
    ///     Register miscellaneous schemas.
    /// </summary>
    private static void RegisterMiscSchemas(Dictionary<SchemaKey, SubrecordSchema> schemas)
    {
        // XRGD - Ragdoll Bone Data (28 bytes per bone)
        schemas[new SchemaKey("XRGD")] = new SubrecordSchema(
            F.UInt8("BoneId"),
            F.Padding(3),
            F.Vec3("Position"),
            F.Vec3("Rotation"))
        {
            ExpectedSize = -1, // Repeating array
            Description = "Ragdoll Bone Data Array"
        };

        // XPWR - Water Reflection (8 bytes)
        schemas[new SchemaKey("XPWR")] = new SubrecordSchema(
            F.FormId("Reference"),
            F.UInt32("Type"))
        {
            Description = "Water Reflection/Refraction"
        };

        // XMBO - Bound Half Extents (12 bytes)
        schemas[new SchemaKey("XMBO")] = new SubrecordSchema(F.Vec3("Bounds"))
        {
            Description = "Bound Half Extents"
        };

        // XPRM - Primitive (32 bytes)
        schemas[new SchemaKey("XPRM", null, 32)] = new SubrecordSchema(
            F.Vec3("Bounds"),
            F.Vec3("Colors"),
            F.UInt32("Unknown"),
            F.UInt32("Type"))
        {
            Description = "Primitive Data"
        };

        // XPOD - Portal Data (8 bytes)
        schemas[new SchemaKey("XPOD", null, 8)] = new SubrecordSchema(
            F.FormId("Origin"),
            F.FormId("Destination"))
        {
            Description = "Portal Data"
        };

        // XRGB - Biped Rotation (12 bytes)
        schemas[new SchemaKey("XRGB")] = new SubrecordSchema(F.Vec3("Rotation"))
        {
            Description = "Biped Rotation"
        };

        // XRDO - Radio Data (16 bytes)
        schemas[new SchemaKey("XRDO", null, 16)] = new SubrecordSchema(
            F.Float("Range"),
            F.UInt32("Type"),
            F.Float("StaticPercentage"),
            F.FormId("PositionRef"))
        {
            Description = "Radio Data"
        };

        // XOCP - Occlusion Plane (36 bytes = 9 floats)
        schemas[new SchemaKey("XOCP", null, 36)] = SubrecordSchema.FloatArray;

        // XORD - Linked Occlusion Planes (16 bytes = 4 FormIDs)
        schemas[new SchemaKey("XORD", null, 16)] = new SubrecordSchema(
            F.FormId("Right"),
            F.FormId("Left"),
            F.FormId("Bottom"),
            F.FormId("Top"))
        {
            Description = "Linked Occlusion Planes"
        };

        // MNAM - World Map Data (16 bytes)
        schemas[new SchemaKey("MNAM", "WRLD", 16)] = new SubrecordSchema(
            F.Int32("UsableX"),
            F.Int32("UsableY"),
            F.Int16("NWCellX"),
            F.Int16("NWCellY"),
            F.Int16("SECellX"),
            F.Int16("SECellY"))
        {
            Description = "World Map Data"
        };

        // NAM0/NAM9 - Worldspace Bounds (8 bytes = 2 floats)
        schemas[new SchemaKey("NAM0", "WRLD", 8)] = new SubrecordSchema(F.Float("X"), F.Float("Y"))
        {
            Description = "Worldspace Bounds Min"
        };
        schemas[new SchemaKey("NAM9", "WRLD", 8)] = new SubrecordSchema(F.Float("X"), F.Float("Y"))
        {
            Description = "Worldspace Bounds Max"
        };

        // XCLC - Cell Grid (12 bytes)
        schemas[new SchemaKey("XCLC", null, 12)] = new SubrecordSchema(
            F.Int32("X"),
            F.Int32("Y"),
            F.UInt8("LandFlags"),
            F.Padding(3))
        {
            Description = "Cell Grid"
        };

        // XCLR - Cell Regions (array of FormIDs)
        schemas[new SchemaKey("XCLR")] = SubrecordSchema.FormIdArray;

        // TRDT - INFO Response Data (24 bytes)
        schemas[new SchemaKey("TRDT", null, 24)] = new SubrecordSchema(
            F.UInt32("EmotionType"),
            F.Int32("EmotionValue"),
            F.Padding(4),
            F.UInt8("ResponseNumber"),
            F.Padding(3),
            F.FormId("Sound"),
            F.UInt8("UseEmotionAnim"),
            F.Padding(3))
        {
            Description = "INFO Response Data"
        };

        // QSTA - Quest Target (8 bytes)
        schemas[new SchemaKey("QSTA", null, 8)] = new SubrecordSchema(
            F.FormId("Target"),
            F.UInt8("Flags"),
            F.Padding(3))
        {
            Description = "Quest Target"
        };

        // ANAM - IDLE/CPTH Animations (8 bytes)
        schemas[new SchemaKey("ANAM", "IDLE", 8)] = new SubrecordSchema(
            F.FormId("Parent"),
            F.FormId("Previous"))
        {
            Description = "Idle Animation Parents"
        };
        schemas[new SchemaKey("ANAM", "CPTH", 8)] = new SubrecordSchema(
            F.FormId("Parent"),
            F.FormId("Previous"))
        {
            Description = "Camera Path Parents"
        };

        // PTDT/PTD2 - Package Target (16 bytes)
        // NOTE: On Xbox 360, the leading type field is already stored in PC byte order.
        // Treat it as a single byte + padding to avoid an incorrect 4-byte swap.
        schemas[new SchemaKey("PTDT", null, 16)] = new SubrecordSchema(
            F.UInt8("Type"),
            F.Padding(3),
            F.UInt32("Union"),
            F.Int32("CountDistance"),
            F.Float("Unknown"))
        {
            Description = "Package Target"
        };
        schemas[new SchemaKey("PTD2", null, 16)] = new SubrecordSchema(
            F.UInt8("Type"),
            F.Padding(3),
            F.UInt32("Union"),
            F.Int32("CountDistance"),
            F.Float("Unknown"))
        {
            Description = "Package Target 2"
        };

        // PKDD - Package Dialogue Data (24 bytes)
        schemas[new SchemaKey("PKDD", null, 24)] = new SubrecordSchema(
            F.Float("FOV"),
            F.FormId("Topic"),
            F.UInt32("Flags"),
            F.Padding(4),
            F.UInt32("DialogueType"),
            F.UInt32("Unknown"))
        {
            Description = "Package Dialogue Data"
        };

        // PLDT/PLD2 - Package Location (12 bytes)
        // NOTE: On Xbox 360, the leading type field is already stored in PC byte order.
        // Treat it as a single byte + padding to avoid an incorrect 4-byte swap.
        schemas[new SchemaKey("PLDT", null, 12)] = new SubrecordSchema(
            F.UInt8("Type"),
            F.Padding(3),
            F.UInt32("Union"),
            F.Int32("Radius"))
        {
            Description = "Package Location"
        };
        schemas[new SchemaKey("PLD2", null, 12)] = new SubrecordSchema(
            F.UInt8("Type"),
            F.Padding(3),
            F.UInt32("Union"),
            F.Int32("Radius"))
        {
            Description = "Package Location 2"
        };

        // PKW3 - Package Use Weapon Data (24 bytes)
        schemas[new SchemaKey("PKW3", null, 24)] = new SubrecordSchema(
            F.UInt32("Flags"),
            F.UInt8("FireRate"),
            F.UInt8("FireCount"),
            F.UInt16("NumBursts"),
            F.UInt16("MinShoots"),
            F.UInt16("MaxShoots"),
            F.Float("MinPause"),
            F.Float("MaxPause"),
            F.Padding(4))
        {
            Description = "Package Use Weapon Data"
        };

        // CSTD - Combat Style Standard (92 bytes)
        schemas[new SchemaKey("CSTD", null, 92)] = new SubrecordSchema(
            F.UInt8("DodgeChance"),
            F.UInt8("LRChance"),
            F.Padding(2),
            F.Float("DodgeLRTimerMin"),
            F.Float("DodgeLRTimerMax"),
            F.Float("DodgeFWTimerMin"),
            F.Float("DodgeFWTimerMax"),
            F.Float("DodgeBKTimerMin"),
            F.Float("DodgeBKTimerMax"),
            F.Float("IdleTimerMin"),
            F.Float("IdleTimerMax"),
            F.UInt8("BlockChance"),
            F.UInt8("AttackChance"),
            F.Padding(2),
            F.Float("StaggerBonusToAttack"),
            F.Float("KOBonusToAttack"),
            F.Float("H2HBonusToAttack"),
            F.UInt8("PowerAttackChance"),
            F.Padding(3),
            F.Float("StaggerBonusToPower"),
            F.Float("KOBonusToPower"),
            F.UInt8("AttacksNotBlock"),
            F.UInt8("PowerAttacksNotBlock"),
            F.UInt8("HoldTimerMin"),
            F.UInt8("HoldTimerMax"),
            F.UInt8("Flags"),
            F.Padding(3),
            F.Float("AcrobaticDodge"),
            F.Float("RangeMultOptimal"),
            F.UInt32("RangeMultFlags"),
            F.UInt8("RangedDodgeChance"),
            F.UInt8("RangedRushChance"),
            F.Padding(2),
            F.Float("RangedDamageMult"))
        {
            Description = "Combat Style Standard Data"
        };

        // CSAD - Combat Style Advanced (84 bytes = 21 floats)
        schemas[new SchemaKey("CSAD", null, 84)] = SubrecordSchema.FloatArray;

        // CSSD - Combat Style Simple (64 bytes)
        schemas[new SchemaKey("CSSD", null, 64)] = SubrecordSchema.FloatArray;

        // GNAM - WATR Related Waters (12 bytes)
        schemas[new SchemaKey("GNAM", "WATR", 12)] = new SubrecordSchema(
            F.FormId("Daytime"),
            F.FormId("Nighttime"),
            F.FormId("Underwater"))
        {
            Description = "Water Related Waters"
        };

        // BPND - Body Part Node Data (84 bytes)
        schemas[new SchemaKey("BPND", null, 84)] = new SubrecordSchema(
            F.Float("DamageMult"),
            F.UInt8("Flags"),
            F.UInt8("PartType"),
            F.UInt8("HealthPercent"),
            F.UInt8("ActorValue"),
            F.UInt8("ToHitChance"),
            F.UInt8("ExplodableExplosionChance"),
            F.UInt16("DebrisCount"),
            F.FormId("Debris"),
            F.FormId("Explosion"),
            F.Float("TrackingMaxAngle"),
            F.Float("DebrisScale"),
            F.Int32("SeverableDebrisCount"),
            F.FormId("SeverableDebris"),
            F.FormId("SeverableExplosion"),
            F.Float("SeverableDebrisScale"),
            F.PosRot("GoreTransform"),
            F.FormId("SeverableImpact"),
            F.FormId("ExplodableImpact"),
            F.UInt8("SeverableDecalCount"),
            F.UInt8("ExplodableDecalCount"),
            F.Padding(2),
            F.Float("LimbReplacementScale"))
        {
            Description = "Body Part Node Data"
        };

        // NAM5 - Model Info in BPTD
        schemas[new SchemaKey("NAM5", "BPTD")] = new SubrecordSchema(F.UInt32("TextureCount"))
        {
            ExpectedSize = 0,
            Description = "BPTD Model Info"
        };

        // RAFD - Ragdoll Feedback Data (60 bytes = 15 floats)
        schemas[new SchemaKey("RAFD", null, 60)] = SubrecordSchema.FloatArray;

        // RAPS - Ragdoll Pose Matching (24 bytes)
        schemas[new SchemaKey("RAPS", null, 24)] = new SubrecordSchema(
            F.UInt16("Bone0"),
            F.UInt16("Bone1"),
            F.UInt16("Bone2"),
            F.UInt8("Flags"),
            F.Padding(1),
            F.Float("MotorStrength"),
            F.Float("PoseActivationDelay"),
            F.Float("MatchErrorAllowance"),
            F.Float("DisplacementToDisable"))
        {
            Description = "Ragdoll Pose Matching Data"
        };

        // RAFB - Ragdoll Feedback Dynamic Bones (array of uint16)
        schemas[new SchemaKey("RAFB")] = new SubrecordSchema(F.UInt16("Bone"))
        {
            ExpectedSize = -1, // Repeating array
            Description = "Ragdoll Dynamic Bones"
        };

        // SCHR - Script Header (20 bytes)
        // Pattern: 4B same + 3×4B swap + 4B same
        schemas[new SchemaKey("SCHR", null, 20)] = new SubrecordSchema(
                F.Padding(4),
                F.UInt32("RefCount"),
                F.UInt32("CompiledSize"),
                F.UInt32("VariableCount"),
                F.Padding(4)) // Type + Flags don't need swapping
        {
            Description = "Script Header"
        };

        // ACBS - Actor Base Stats (24 bytes)
        schemas[new SchemaKey("ACBS", null, 24)] = new SubrecordSchema(
            F.UInt32("Flags"),
            F.UInt16("Fatigue"),
            F.UInt16("BarterGold"),
            F.Int16("Level"),
            F.UInt16("CalcMin"),
            F.UInt16("CalcMax"),
            F.UInt16("SpeedMult"),
            F.UInt32("TemplateFlags"),
            F.UInt16("Disposition"),
            F.UInt16("Unknown"))
        {
            Description = "Actor Base Stats"
        };

        // AIDT - AI Data (20 bytes)
        // TESAIForm structure from PDB
        schemas[new SchemaKey("AIDT", null, 20)] = new SubrecordSchema(
            F.UInt8("Aggression"),
            F.UInt8("Confidence"),
            F.UInt8("Energy"),
            F.UInt8("Morality"),
            F.UInt8("Mood"),
            F.Padding(3), // Alignment padding
            F.UInt32("ServiceFlags"),
            F.Int32("TrainSkill"),
            F.Int32("TrainLevel"))
        {
            Description = "AI Data"
        };
    }

    /// <summary>
    ///     Register DATA subrecord schemas (context-dependent).
    /// </summary>
    private static void RegisterDataSubrecordSchemas(Dictionary<SchemaKey, SubrecordSchema> schemas)
    {
        // DATA - NPC_ (11 bytes)
        schemas[new SchemaKey("DATA", "NPC_", 11)] = new SubrecordSchema(
            F.Int32("BaseHealth"),
            F.UInt8("Strength"),
            F.UInt8("Perception"),
            F.UInt8("Endurance"),
            F.UInt8("Charisma"),
            F.UInt8("Intelligence"),
            F.UInt8("Agility"),
            F.UInt8("Luck"))
        {
            Description = "NPC Data"
        };

        // DATA - PROJ (84 bytes) - Projectile Data
        // Structure from TES5Edit wbDefinitionsFNV.pas line 5221
        // NOTE: Flags+Type stored as combined uint32 on Xbox 360 for endian swap purposes
        // NOTE: FormIDs in PROJ DATA are stored in little-endian on Xbox 360 (like WEAP DNAM)
        schemas[new SchemaKey("DATA", "PROJ", 84)] = new SubrecordSchema(
            F.UInt32("FlagsAndType"),               // 0-3: Combined Flags (low 16) + Type (high 16), swapped as uint32
            F.Float("Gravity"),                     // 4
            F.Float("Speed"),                       // 8
            F.Float("Range"),                       // 12
            F.FormIdLittleEndian("Light"),          // 16 - already LE on Xbox
            F.FormIdLittleEndian("MuzzleFlashLight"), // 20 - already LE on Xbox
            F.Float("TracerChance"),                // 24
            F.Float("ExplosionAltTriggerProximity"), // 28
            F.Float("ExplosionAltTriggerTimer"),    // 32
            F.FormIdLittleEndian("Explosion"),      // 36 - already LE on Xbox
            F.FormIdLittleEndian("Sound"),          // 40 - already LE on Xbox
            F.Float("MuzzleFlashDuration"),         // 44
            F.Float("FadeDuration"),                // 48
            F.Float("ImpactForce"),                 // 52
            F.FormIdLittleEndian("SoundCountdown"), // 56 - already LE on Xbox
            F.FormIdLittleEndian("SoundDisable"),   // 60 - already LE on Xbox
            F.FormIdLittleEndian("DefaultWeaponSource"), // 64 - already LE on Xbox
            F.Float("RotationX"),                   // 68
            F.Float("RotationY"),                   // 72
            F.Float("RotationZ"),                   // 76
            F.Float("BouncyMult"))                  // 80
        {
            Description = "Projectile Data"
        };

        // DATA - WEAP (15 bytes)
        schemas[new SchemaKey("DATA", "WEAP", 15)] = new SubrecordSchema(
            F.Int32("Value"),
            F.Int32("Health"),
            F.Float("Weight"),
            F.Int16("Damage"),
            F.UInt8("ClipSize"))
        {
            Description = "Weapon Data"
        };

        // DATA - AMMO (13 bytes)
        schemas[new SchemaKey("DATA", "AMMO", 13)] = new SubrecordSchema(
            F.Float("Speed"),
            F.UInt8("Flags"),
            F.Padding(3),
            F.UInt32("Value"),
            F.UInt8("ClipRounds"))
        {
            Description = "Ammo Data"
        };

        // DATA - QUST (8 bytes)
        schemas[new SchemaKey("DATA", "QUST", 8)] = new SubrecordSchema(
            F.UInt8("Flags"),
            F.UInt8("Priority"),
            F.Padding(2),
            F.Float("QuestDelay"))
        {
            Description = "Quest Data"
        };

        // DATA - REFR/ACHR/ACRE (24 bytes = PosRot)
        schemas[new SchemaKey("DATA", "REFR", 24)] = new SubrecordSchema(F.PosRot("PosRot"));
        schemas[new SchemaKey("DATA", "ACHR", 24)] = new SubrecordSchema(F.PosRot("PosRot"));
        schemas[new SchemaKey("DATA", "ACRE", 24)] = new SubrecordSchema(F.PosRot("PosRot"));

        // DATA - CREA (17 bytes)
        // Bytes 0-3: single bytes (no swap), Bytes 4-7: int32 (swap), Bytes 8-9: int16 (swap), Bytes 10-16: remaining
        schemas[new SchemaKey("DATA", "CREA", 17)] = new SubrecordSchema(
            F.UInt8("CreatureType"),
            F.UInt8("CombatSkill"),
            F.UInt8("MagicSkill"),
            F.UInt8("StealthSkill"),
            F.Int32("AttackDamage"),
            F.Int16("Health"),
            F.Bytes("Remaining", 7))
        {
            Description = "Creature Data"
        };

        // DATA - MGEF (72 bytes)
        schemas[new SchemaKey("DATA", "MGEF", 72)] = new SubrecordSchema(
            F.UInt32("Flags"),
            F.Float("BaseCost"),
            F.FormId("AssocItem"),
            F.Int32("MagicSchool"),
            F.Int32("ResistanceValue"),
            F.UInt16("Unknown"),
            F.Padding(2),
            F.FormId("Light"),
            F.Float("ProjectileSpeed"),
            F.FormId("EffectShader"),
            F.FormId("EnchantEffect"),
            F.FormId("CastingSound"),
            F.FormId("BoltSound"),
            F.FormId("HitSound"),
            F.FormId("AreaSound"),
            F.Float("ConstantEffectEnchantmentFactor"),
            F.Float("ConstantEffectBarterFactor"),
            F.Int32("Archtype"),
            F.Int32("ActorValue"))
        {
            Description = "Magic Effect Data"
        };

        // DATA - RACE (36 bytes)
        schemas[new SchemaKey("DATA", "RACE", 36)] = new SubrecordSchema(
            F.Bytes("SkillBoosts", 14),
            F.Padding(2),
            F.Float("MaleHeight"),
            F.Float("FemaleHeight"),
            F.Float("MaleWeight"),
            F.Float("FemaleWeight"),
            F.UInt32("Flags"))
        {
            Description = "Race Data"
        };

        // DATA - ARMO (12 bytes)
        schemas[new SchemaKey("DATA", "ARMO", 12)] = new SubrecordSchema(
            F.Int32("Value"),
            F.Int32("Health"),
            F.Float("Weight"))
        {
            Description = "Armor Data"
        };

        // DATA - ALCH (4 bytes)
        schemas[new SchemaKey("DATA", "ALCH", 4)] = new SubrecordSchema(F.Float("Weight"));

        // DATA - LAND (4 bytes)
        schemas[new SchemaKey("DATA", "LAND", 4)] = new SubrecordSchema(F.UInt32("Flags"));

        // DATA - INFO (4 bytes)
        schemas[new SchemaKey("DATA", "INFO", 4)] = SubrecordSchema.ByteArray;

        // DATA - CELL (1 byte)
        schemas[new SchemaKey("DATA", "CELL", 1)] = SubrecordSchema.ByteArray;

        // DATA - PERK (variable)
        // Xbox 360 PERK records contain mixed small DATA payloads:
        // - Some 4-byte blocks are big-endian uint32/FormIDs and must be swapped
        // - Some 5-byte blocks are uint32 + trailing byte
        // Anything else falls back to raw bytes.
        schemas[new SchemaKey("DATA", "PERK", 4)] = SubrecordSchema.Simple4Byte();
        // Observed: many PERK:DATA(5) payloads match PC bytes already (likely little-endian + padding);
        // treat as raw bytes to avoid corrupting the bitfield layout.
        schemas[new SchemaKey("DATA", "PERK", 5)] = SubrecordSchema.ByteArray;
        schemas[new SchemaKey("DATA", "PERK")] = SubrecordSchema.ByteArray;

        // DATA - GMST (4 bytes = int or float)
        schemas[new SchemaKey("DATA", "GMST", 4)] = SubrecordSchema.Simple4Byte();
        schemas[new SchemaKey("DATA", "GMST")] = SubrecordSchema.ByteArray; // String GMSTs or variable-length data

        // DATA - CDCK (4 bytes) - Caravan Deck count
        // wbInteger(DATA, 'Count (broken)', itU32)
        schemas[new SchemaKey("DATA", "CDCK", 4)] = new SubrecordSchema(F.UInt32("Count"))
        {
            Description = "Caravan Deck Count"
        };

        // DATA - ECZN (8 bytes) - Encounter Zone
        // wbStruct(DATA, [wbFormIDCk('Owner', [FACT,NPC_]), wbInteger('Rank', itS8),
        //   wbInteger('Minimum Level', itS8), wbInteger('Flags', itU8), wbByteArray('Unused', 1)])
        schemas[new SchemaKey("DATA", "ECZN", 8)] = new SubrecordSchema(
            F.FormId("Owner"),
            F.Int8("Rank"),
            F.Int8("MinimumLevel"),
            F.UInt8("Flags"),
            F.Padding(1))
        {
            Description = "Encounter Zone Data"
        };

        // DATA - RGDL (14 bytes) - Ragdoll
        // PC format: wbInteger('Dynamic Bone Count', itU32), wbUnused(4), 5 bools, wbUnused(1)
        // Xbox uses word-swapped (middle-endian) uint32 for DynamicBoneCount
        // Xbox bytes: 00 15 00 00 -> swap each word -> 15 00 00 00 (value = 21)
        schemas[new SchemaKey("DATA", "RGDL", 14)] = new SubrecordSchema(
            F.UInt32WordSwapped("DynamicBoneCount"),
            F.Padding(4),  // Unused
            F.UInt8("Feedback"),
            F.UInt8("FootIK"),
            F.UInt8("LookIK"),
            F.UInt8("GrabIK"),
            F.UInt8("PoseMatching"),
            F.Padding(1))
        {
            Description = "Ragdoll Data"
        };

        // DATA - NAVM (20 bytes) - Navmesh Data
        // wbStruct(DATA, [wbFormIDCk('Cell', [CELL]), wbInteger('Vertex Count', itU32),
        //   wbInteger('Triangle Count', itU32), wbInteger('Edge Link Count', itU32),
        //   wbInteger('Door Link Count', itU32)])
        schemas[new SchemaKey("DATA", "NAVM", 20)] = new SubrecordSchema(
            F.FormId("Cell"),
            F.UInt32("VertexCount"),
            F.UInt32("TriangleCount"),
            F.UInt32("EdgeLinkCount"),
            F.UInt32("DoorLinkCount"))
        {
            Description = "Navmesh Data"
        };

        // CSTD - Combat Style Standard (92 bytes)
        // Contains combat behavior floats that control dodge timing, attack decisions, etc.
        // CRITICAL: Without proper byte-swap, AI combat behavior is broken!
        schemas[new SchemaKey("CSTD", null, 92)] = new SubrecordSchema(
            F.UInt8("DodgeChance"),
            F.UInt8("LeftRightChance"),
            F.Padding(2),
            F.Float("DodgeLRTimerMin"),
            F.Float("DodgeLRTimerMax"),
            F.Float("DodgeFwdTimerMin"),
            F.Float("DodgeFwdTimerMax"),
            F.Float("DodgeBackTimerMin"),
            F.Float("DodgeBackTimerMax"),
            F.Float("IdleTimerMin"),
            F.Float("IdleTimerMax"),
            F.UInt8("BlockChance"),
            F.UInt8("AttackChance"),
            F.Padding(2),
            F.Float("RecoilStaggerBonus"),
            F.Float("UnconsciousBonus"),
            F.Float("HandToHandBonus"),
            F.UInt8("PowerAttackChance"),
            F.Padding(3),
            F.Float("RecoilPowerBonus"),
            F.Float("UnconsciousPowerBonus"),
            F.UInt8("PowerAttackNormal"),
            F.UInt8("PowerAttackForward"),
            F.UInt8("PowerAttackBack"),
            F.UInt8("PowerAttackLeft"),
            F.UInt8("PowerAttackRight"),
            F.Padding(3),
            F.Float("HoldTimerMin"),
            F.Float("HoldTimerMax"),
            F.UInt16("Flags"),
            F.Padding(2),
            F.UInt8("AcrobaticDodgeChance"),
            F.UInt8("RushingAttackChance"),
            F.Padding(2),
            F.Float("RushingAttackDistMult"))
        {
            Description = "Combat Style Standard Data"
        };

        // CSAD - Combat Style Advanced (84 bytes = 21 floats)
        // All float values for advanced combat modifiers
        schemas[new SchemaKey("CSAD", null, 84)] = SubrecordSchema.FloatArray;

        // CSSD - Combat Style Simple (64 bytes)
        // Floats with one uint32 at offset 40
        // NOTE: WeaponRestrictions at offset 40 is uint32, but we swap all 4-byte values anyway
        // Using FloatArray since all values need 4-byte swap regardless of interpretation
        schemas[new SchemaKey("CSSD", null, 64)] = SubrecordSchema.FloatArray;

        // DATA - GRAS (32 bytes) - Grass
        // wbStruct(DATA, [wbInteger('Density', itU8), wbInteger('Min Slope', itU8),
        //   wbInteger('Max Slope', itU8), wbByteArray('Unused', 1),
        //   wbInteger('Units From Water Amount', itU16), wbByteArray('Unused', 2),
        //   wbInteger('Units From Water Type', itU32), wbFloat('Position Range'),
        //   wbFloat('Height Range'), wbFloat('Color Range'), wbFloat('Wave Period'),
        //   wbInteger('Flags', itU8), wbByteArray('Unused', 3)])
        schemas[new SchemaKey("DATA", "GRAS", 32)] = new SubrecordSchema(
            F.UInt8("Density"),
            F.UInt8("MinSlope"),
            F.UInt8("MaxSlope"),
            F.Padding(1),
            F.UInt16("UnitsFromWaterAmount"),
            F.Padding(2),
            F.UInt32("UnitsFromWaterType"),
            F.Float("PositionRange"),
            F.Float("HeightRange"),
            F.Float("ColorRange"),
            F.Float("WavePeriod"),
            F.UInt8("Flags"),
            F.Padding(3))
        {
            Description = "Grass Data"
        };

        // DATA - EFSH (200 bytes or 224 bytes) - Effect Shader
        // Complex structure with flags, blend modes (uint32s) and many floats
        // wbStruct(DATA, [wbInteger('Flags', itU8), wbUnused(3),
        //   wbInteger('Membrane Shader - Source Blend Mode', itU32), ... many more])
        // Version A: 200 bytes (base), Version B: 224 bytes (additional fields)
        schemas[new SchemaKey("DATA", "EFSH", 200)] = new SubrecordSchema(
            F.UInt8("Flags"),
            F.Padding(3),
            F.UInt32("MembraneSourceBlendMode"),
            F.UInt32("MembraneBlendOperation"),
            F.UInt32("MembraneZTestFunction"),
            F.ColorRgba("FillColorKey1"),
            F.Float("FillAlphaFadeInTime"),
            F.Float("FillAlphaFullTime"),
            F.Float("FillAlphaFadeOutTime"),
            F.Float("FillAlphaPersistentPercent"),
            F.Float("FillAlphaPulseAmplitude"),
            F.Float("FillAlphaPulseFrequency"),
            F.Float("FillTextureAnimSpeedU"),
            F.Float("FillTextureAnimSpeedV"),
            F.Float("EdgeEffectFallOff"),
            F.ColorRgba("EdgeEffectColor"),
            F.Float("EdgeEffectAlphaFadeInTime"),
            F.Float("EdgeEffectAlphaFullTime"),
            F.Float("EdgeEffectAlphaFadeOutTime"),
            F.Float("EdgeEffectAlphaPersistentPercent"),
            F.Float("EdgeEffectAlphaPulseAmplitude"),
            F.Float("EdgeEffectAlphaPulseFrequency"),
            F.Float("FillAlphaFullPercent"),
            F.Float("EdgeEffectAlphaFullPercent"),
            F.UInt32("MembraneDestBlendMode"),
            F.UInt32("PartSourceBlendMode"),
            F.UInt32("PartBlendOperation"),
            F.UInt32("PartZTestFunction"),
            F.UInt32("PartDestBlendMode"),
            F.Float("PartBirthRampUpTime"),
            F.Float("PartBirthFullTime"),
            F.Float("PartBirthRampDownTime"),
            F.Float("PartBirthFullRatio"),
            F.Float("PartBirthPersistentRatio"),
            F.Float("PartLifetimeAverage"),
            F.Float("PartLifetimeRange"),
            F.Float("PartSpeedAcrossHoriz"),
            F.Float("PartAccelAcrossHoriz"),
            F.Float("PartSpeedAcrossVert"),
            F.Float("PartAccelAcrossVert"),
            F.Float("PartSpeedAlongNormal"),
            F.Float("PartAccelAlongNormal"),
            F.Float("PartSpeedAlongRotation"),
            F.Float("PartAccelAlongRotation"),
            F.Float("HolesStartTime"),
            F.Float("HolesEndTime"),
            F.Float("HolesStartValue"),
            F.Float("HolesEndValue"))
        {
            Description = "Effect Shader Data (200 bytes)"
        };

        schemas[new SchemaKey("DATA", "EFSH", 224)] = new SubrecordSchema(
            F.UInt8("Flags"),
            F.Padding(3),
            F.UInt32("MembraneSourceBlendMode"),
            F.UInt32("MembraneBlendOperation"),
            F.UInt32("MembraneZTestFunction"),
            F.ColorRgba("FillColorKey1"),
            F.Float("FillAlphaFadeInTime"),
            F.Float("FillAlphaFullTime"),
            F.Float("FillAlphaFadeOutTime"),
            F.Float("FillAlphaPersistentPercent"),
            F.Float("FillAlphaPulseAmplitude"),
            F.Float("FillAlphaPulseFrequency"),
            F.Float("FillTextureAnimSpeedU"),
            F.Float("FillTextureAnimSpeedV"),
            F.Float("EdgeEffectFallOff"),
            F.ColorRgba("EdgeEffectColor"),
            F.Float("EdgeEffectAlphaFadeInTime"),
            F.Float("EdgeEffectAlphaFullTime"),
            F.Float("EdgeEffectAlphaFadeOutTime"),
            F.Float("EdgeEffectAlphaPersistentPercent"),
            F.Float("EdgeEffectAlphaPulseAmplitude"),
            F.Float("EdgeEffectAlphaPulseFrequency"),
            F.Float("FillAlphaFullPercent"),
            F.Float("EdgeEffectAlphaFullPercent"),
            F.UInt32("MembraneDestBlendMode"),
            F.UInt32("PartSourceBlendMode"),
            F.UInt32("PartBlendOperation"),
            F.UInt32("PartZTestFunction"),
            F.UInt32("PartDestBlendMode"),
            F.Float("PartBirthRampUpTime"),
            F.Float("PartBirthFullTime"),
            F.Float("PartBirthRampDownTime"),
            F.Float("PartBirthFullRatio"),
            F.Float("PartBirthPersistentRatio"),
            F.Float("PartLifetimeAverage"),
            F.Float("PartLifetimeRange"),
            F.Float("PartSpeedAcrossHoriz"),
            F.Float("PartAccelAcrossHoriz"),
            F.Float("PartSpeedAcrossVert"),
            F.Float("PartAccelAcrossVert"),
            F.Float("PartSpeedAlongNormal"),
            F.Float("PartAccelAlongNormal"),
            F.Float("PartSpeedAlongRotation"),
            F.Float("PartAccelAlongRotation"),
            F.Float("HolesStartTime"),
            F.Float("HolesEndTime"),
            F.Float("HolesStartValue"),
            F.Float("HolesEndValue"),
            F.ColorRgba("FillColorKey2"),
            F.ColorRgba("FillColorKey3"),
            F.Float("FillColorKey1Scale"),
            F.Float("FillColorKey2Scale"),
            F.Float("FillColorKey3Scale"),
            F.Float("FillColorKey1Time"),
            F.Float("FillColorKey2Time"),
            F.Float("FillColorKey3Time"))
        {
            Description = "Effect Shader Data (224 bytes)"
        };

        // DATA - EFSH (308 bytes) - Extended Effect Shader (full FO3/FNV structure)
        // Includes all 224-byte fields plus: rotation, addon models FormID, holes, edge, textures
        schemas[new SchemaKey("DATA", "EFSH", 308)] = new SubrecordSchema(
            // Base fields (same as 224-byte version)
            F.UInt8("Flags"),
            F.Padding(3),
            F.UInt32("MembraneSourceBlendMode"),
            F.UInt32("MembraneBlendOperation"),
            F.UInt32("MembraneZTestFunction"),
            F.ColorRgba("FillColorKey1"),
            F.Float("FillAlphaFadeInTime"),
            F.Float("FillAlphaFullTime"),
            F.Float("FillAlphaFadeOutTime"),
            F.Float("FillAlphaPersistentPercent"),
            F.Float("FillAlphaPulseAmplitude"),
            F.Float("FillAlphaPulseFrequency"),
            F.Float("FillTextureAnimSpeedU"),
            F.Float("FillTextureAnimSpeedV"),
            F.Float("EdgeEffectFallOff"),
            F.ColorRgba("EdgeEffectColor"),
            F.Float("EdgeEffectAlphaFadeInTime"),
            F.Float("EdgeEffectAlphaFullTime"),
            F.Float("EdgeEffectAlphaFadeOutTime"),
            F.Float("EdgeEffectAlphaPersistentPercent"),
            F.Float("EdgeEffectAlphaPulseAmplitude"),
            F.Float("EdgeEffectAlphaPulseFrequency"),
            F.Float("FillAlphaFullPercent"),
            F.Float("EdgeEffectAlphaFullPercent"),
            F.UInt32("MembraneDestBlendMode"),
            F.UInt32("PartSourceBlendMode"),
            F.UInt32("PartBlendOperation"),
            F.UInt32("PartZTestFunction"),
            F.UInt32("PartDestBlendMode"),
            F.Float("PartBirthRampUpTime"),
            F.Float("PartBirthFullTime"),
            F.Float("PartBirthRampDownTime"),
            F.Float("PartBirthFullRatio"),
            F.Float("PartBirthPersistentRatio"),
            F.Float("PartLifetimeAverage"),
            F.Float("PartLifetimeRange"),
            F.Float("PartSpeedAcrossHoriz"),
            F.Float("PartAccelAcrossHoriz"),
            F.Float("PartSpeedAcrossVert"),
            F.Float("PartAccelAcrossVert"),
            F.Float("PartSpeedAlongNormal"),
            F.Float("PartAccelAlongNormal"),
            F.Float("PartSpeedAlongRotation"),
            F.Float("PartAccelAlongRotation"),
            F.Float("PartScaleKey1"),
            F.Float("PartScaleKey2"),
            F.Float("PartScaleKey1Time"),
            F.Float("PartScaleKey2Time"),
            F.ColorRgba("FillColorKey2"),
            F.ColorRgba("FillColorKey3"),
            F.Float("FillColorKey1Scale"),
            F.Float("FillColorKey2Scale"),
            F.Float("FillColorKey3Scale"),
            F.Float("FillColorKey1Time"),
            F.Float("FillColorKey2Time"),
            F.Float("FillColorKey3Time"),
            // Extended fields (308-byte version)
            F.Float("PartInitialSpeedNormalVariance"),
            F.Float("PartInitialRotation"),
            F.Float("PartInitialRotationVariance"),
            F.Float("PartRotationSpeed"),
            F.Float("PartRotationSpeedVariance"),
            F.FormId("AddonModels"),
            F.Float("HolesStartTime"),
            F.Float("HolesEndTime"),
            F.Float("HolesStartValue"),
            F.Float("HolesEndValue"),
            F.Float("EdgeWidth"),
            F.ColorRgba("EdgeColor2"),
            F.Float("ExplosionWindSpeed"),
            F.UInt32("TextureCountU"),
            F.UInt32("TextureCountV"),
            F.Float("AddonModelsFadeInTime"),
            F.Float("AddonModelsFadeOutTime"),
            F.Float("AddonModelsScaleStart"),
            F.Float("AddonModelsScaleEnd"),
            F.Float("AddonModelsScaleInTime"),
            F.Float("AddonModelsScaleOutTime"))
        {
            Description = "Effect Shader Data (308 bytes - extended)"
        };

        // DATA - EXPL (52 bytes) - Explosion Data
        // CRITICAL: Contains FormIDs that were being incorrectly swapped as floats!
        // wbStruct(DATA, [wbFloat('Force'), wbFloat('Damage'), wbFloat('Radius'),
        //   wbFormIDCk('Light', [LIGH, NULL]), wbFormIDCk('Sound 1', [SOUN, NULL]),
        //   wbInteger('Flags', itU32), wbFloat('IS Radius'),
        //   wbFormIDCk('Impact DataSet', [IPDS, NULL]), wbFormIDCk('Sound 2', [SOUN, NULL]),
        //   wbFloat('Radiation Level'), wbFloat('Radiation Dissipation Time'),
        //   wbFloat('Radiation Radius'), wbInteger('Sound Level', itU32)])
        schemas[new SchemaKey("DATA", "EXPL", 52)] = new SubrecordSchema(
            F.Float("Force"),
            F.Float("Damage"),
            F.Float("Radius"),
            F.FormId("Light"),
            F.FormId("Sound1"),
            F.UInt32("Flags"),
            F.Float("ISRadius"),
            F.FormId("ImpactDataSet"),
            F.FormId("Sound2"),
            F.Float("RadiationLevel"),
            F.Float("RadiationDissipationTime"),
            F.Float("RadiationRadius"),
            F.UInt32("SoundLevel"))
        {
            Description = "Explosion Data"
        };

        // DATA - CONT (5 bytes) - Container Data
        // wbStruct(DATA, [wbInteger('Flags', itU8), wbFloat('Weight')])
        schemas[new SchemaKey("DATA", "CONT", 5)] = new SubrecordSchema(
            F.UInt8("Flags"),
            F.Float("Weight"))
        {
            Description = "Container Data"
        };

        // DATA - BOOK (10 bytes) - Book Data
        // wbStruct(DATA, [wbInteger('Flags', itU8), wbInteger('Skill', itS8),
        //   wbInteger('Value', itS32), wbFloat('Weight')])
        schemas[new SchemaKey("DATA", "BOOK", 10)] = new SubrecordSchema(
            F.UInt8("Flags"),
            F.Int8("Skill"),
            F.Int32("Value"),
            F.Float("Weight"))
        {
            Description = "Book Data"
        };

        // DATA - LIGH (32 bytes) - Light Data
        // wbStruct(DATA, [wbInteger('Time', itS32), wbInteger('Radius', itU32),
        //   wbByteColors('Color'), wbInteger('Flags', itU32), wbFloat('Falloff Exponent'),
        //   wbFloat('FOV'), wbInteger('Value', itU32), wbFloat('Weight')])
        schemas[new SchemaKey("DATA", "LIGH", 32)] = new SubrecordSchema(
            F.Int32("Time"),
            F.UInt32("Radius"),
            F.ColorRgba("Color"),
            F.UInt32("Flags"),
            F.Float("FalloffExponent"),
            F.Float("FOV"),
            F.UInt32("Value"),
            F.Float("Weight"))
        {
            Description = "Light Data"
        };

        // DATA - MISC (8 bytes) - Misc Item Data
        // wbStruct(DATA, [wbInteger('Value', itS32), wbFloat('Weight')])
        schemas[new SchemaKey("DATA", "MISC", 8)] = new SubrecordSchema(
            F.Int32("Value"),
            F.Float("Weight"))
        {
            Description = "Misc Item Data"
        };

        // DATA - KEYM (8 bytes) - Key Data
        // wbStruct(DATA, [wbInteger('Value', itS32), wbFloat('Weight')])
        schemas[new SchemaKey("DATA", "KEYM", 8)] = new SubrecordSchema(
            F.Int32("Value"),
            F.Float("Weight"))
        {
            Description = "Key Data"
        };

        // DATA - CAMS (40 bytes) - Camera Shot Data
        // wbStruct(DATA, [wbInteger('Action', itU32), wbInteger('Location', itU32),
        //   wbInteger('Target', itU32), wbInteger('Flags', itU32),
        //   wbFloat('Player Time Mult'), wbFloat('Target Time Mult'), wbFloat('Global Time Mult'),
        //   wbFloat('Max Time'), wbFloat('Min Time'), wbFloat('Target % Between Actors')])
        schemas[new SchemaKey("DATA", "CAMS", 40)] = new SubrecordSchema(
            F.UInt32("Action"),
            F.UInt32("Location"),
            F.UInt32("Target"),
            F.UInt32("Flags"),
            F.Float("PlayerTimeMult"),
            F.Float("TargetTimeMult"),
            F.Float("GlobalTimeMult"),
            F.Float("MaxTime"),
            F.Float("MinTime"),
            F.Float("TargetPctBetweenActors"))
        {
            Description = "Camera Shot Data"
        };

        // DATA - NAVM (24 bytes) - Navmesh Data (alternate size)
        // Some navmeshes have 24 bytes instead of 20 - includes extra unknown field
        schemas[new SchemaKey("DATA", "NAVM", 24)] = new SubrecordSchema(
            F.FormId("Cell"),
            F.UInt32("VertexCount"),
            F.UInt32("TriangleCount"),
            F.UInt32("EdgeLinkCount"),
            F.UInt32("DoorLinkCount"),
            F.UInt32("Unknown"))
        {
            Description = "Navmesh Data (24 bytes)"
        };

        // DATA - SCOL - Static Collection Placements (variable size, 28 bytes per placement)
        // wbArrayS(DATA, 'Placements', wbStruct('Placement', [wbVec3Pos, wbVec3Rot, wbFloat('Scale')]))
        // Each placement is: 3 floats (position) + 3 floats (rotation) + 1 float (scale) = 28 bytes
        // ALL values are floats, so use FloatArray for any size
        schemas[new SchemaKey("DATA", "SCOL")] = SubrecordSchema.FloatArray;

        // DATA - WTHR (15 bytes) - Weather Data
        // All UInt8 fields - no swapping needed
        // wbStruct(DATA, [wbInteger('Wind Speed', itU8), wbUnused(2), wbInteger('Trans Delta', itU8),
        //   wbInteger('Sun Glare', itU8), ...])
        schemas[new SchemaKey("DATA", "WTHR", 15)] = SubrecordSchema.ByteArray;

        // DATA - LSCT (88 bytes) - Load Screen Type Data
        // Controls load screen display configuration (type, position, fonts, colors)
        // wbStruct(DATA, [wbInteger('Type', itU32), wbStruct('Data 1', [X, Y, Width, Height (all U32),
        //   wbFloatAngle('Orientation'), wbInteger('Font', itU32), wbFloat('R'), wbFloat('G'), wbFloat('B'),
        //   wbInteger('Font Alignment', itU32)]), wbByteArray('Unknown', 20), wbStruct('Data 2', [...])])
        schemas[new SchemaKey("DATA", "LSCT", 88)] = new SubrecordSchema(
            F.UInt32("Type"),
            // Data 1 block
            F.UInt32("X"),
            F.UInt32("Y"),
            F.UInt32("Width"),
            F.UInt32("Height"),
            F.Float("Orientation"),
            F.UInt32("Font1"),
            F.Float("FontColor1R"),
            F.Float("FontColor1G"),
            F.Float("FontColor1B"),
            F.UInt32("FontAlignment1"),
            // Unknown block (20 bytes = 5 x UInt32)
            F.UInt32("Unknown1"),
            F.UInt32("Unknown2"),
            F.UInt32("Unknown3"),
            F.UInt32("Unknown4"),
            F.UInt32("Unknown5"),
            // Data 2 block
            F.UInt32("Font2"),
            F.Float("FontColor2R"),
            F.Float("FontColor2G"),
            F.Float("FontColor2B"),
            F.UInt32("Unknown6"),
            F.UInt32("Stats"))
        {
            Description = "Load Screen Type Data"
        };

        // DATA - DEBR (variable length) - Debris Model Data
        // Structure: UInt8 Percentage + variable-length string + UInt8 HasCollision
        // No multi-byte values to swap - mark as ByteArray explicitly
        schemas[new SchemaKey("DATA", "DEBR")] = SubrecordSchema.ByteArray;

        // ========================================================================
        // DATA-FloatArray EXPLICIT SCHEMAS
        // These were previously handled by the FloatArray fallback but need explicit
        // schemas to properly identify FormIDs vs numeric values.
        // ========================================================================

        // DATA - FACT (4 bytes) - Faction Data (NOT floats - it's 2 UInt8 + 2 unused)
        schemas[new SchemaKey("DATA", "FACT", 4)] = SubrecordSchema.ByteArray;

        // DATA - ANIO (4 bytes) - Animated Object (single FormID reference to IDLE)
        schemas[new SchemaKey("DATA", "ANIO", 4)] = new SubrecordSchema(F.FormId("Animation"))
        {
            Description = "Animation FormID"
        };

        // DATA - CCRD (4 bytes) - Caravan Card Value
        schemas[new SchemaKey("DATA", "CCRD", 4)] = new SubrecordSchema(F.UInt32("Value"))
        {
            Description = "Caravan Card Value"
        };

        // DATA - PGRE (24 bytes) - Placed Grenade Position/Rotation (6 floats)
        schemas[new SchemaKey("DATA", "PGRE", 24)] = SubrecordSchema.FloatArray;

        // DATA - ARMA (12 bytes) - Armor Addon Data
        schemas[new SchemaKey("DATA", "ARMA", 12)] = new SubrecordSchema(
            F.Int32("Value"),
            F.Int32("MaxCondition"),
            F.Float("Weight"))
        {
            Description = "Armor Addon Data"
        };

        // DATA - IPCT (24 bytes) - Impact Data
        schemas[new SchemaKey("DATA", "IPCT", 24)] = new SubrecordSchema(
            F.Float("EffectDuration"),
            F.UInt32("EffectOrientation"),
            F.Float("AngleThreshold"),
            F.Float("PlacementRadius"),
            F.UInt32("SoundLevel"),
            F.UInt32("NoDecalData"))
        {
            Description = "Impact Data"
        };

        // DATA - RCPE (16 bytes) - Recipe Data (contains FormIDs!)
        schemas[new SchemaKey("DATA", "RCPE", 16)] = new SubrecordSchema(
            F.Int32("Skill"),
            F.UInt32("Level"),
            F.FormId("Category"),
            F.FormId("SubCategory"))
        {
            Description = "Recipe Data"
        };

        // DATA - CLAS (28 bytes) - Class Data
        schemas[new SchemaKey("DATA", "CLAS", 28)] = new SubrecordSchema(
            F.Int32("TagSkill1"),
            F.Int32("TagSkill2"),
            F.Int32("TagSkill3"),
            F.Int32("TagSkill4"),
            F.UInt32("Flags"),
            F.UInt32("BuysServices"),
            F.Int8("Teaches"),
            F.UInt8("MaxTrainingLevel"),
            F.Padding(2))
        {
            Description = "Class Data"
        };

        // DATA - IPDS (48 bytes) - Impact Data Set (12 FormIDs!)
        schemas[new SchemaKey("DATA", "IPDS", 48)] = new SubrecordSchema(
            F.FormId("Stone"),
            F.FormId("Dirt"),
            F.FormId("Grass"),
            F.FormId("Glass"),
            F.FormId("Metal"),
            F.FormId("Wood"),
            F.FormId("Organic"),
            F.FormId("Cloth"),
            F.FormId("Water"),
            F.FormId("HollowMetal"),
            F.FormId("OrganicBug"),
            F.FormId("OrganicGlow"))
        {
            Description = "Impact Data Set - Material Impact References"
        };

        // DATA - IMOD (8 bytes) - Item Mod Data
        schemas[new SchemaKey("DATA", "IMOD", 8)] = new SubrecordSchema(
            F.UInt32("Value"),
            F.Float("Weight"))
        {
            Description = "Item Mod Data"
        };

        // DATA - AMEF (12 bytes) - Ammo Effect Data
        schemas[new SchemaKey("DATA", "AMEF", 12)] = new SubrecordSchema(
            F.UInt32("Type"),
            F.UInt32("Operation"),
            F.Float("Value"))
        {
            Description = "Ammo Effect Data"
        };

        // DATA - ADDN (4 bytes) - Addon Node Index
        schemas[new SchemaKey("DATA", "ADDN", 4)] = new SubrecordSchema(F.Int32("NodeIndex"))
        {
            Description = "Addon Node Index"
        };

        // DATA - LGTM (40 bytes) - Lighting Template Data
        schemas[new SchemaKey("DATA", "LGTM", 40)] = new SubrecordSchema(
            F.ColorRgba("AmbientColor"),
            F.ColorRgba("DirectionalColor"),
            F.ColorRgba("FogColor"),
            F.Float("FogNear"),
            F.Float("FogFar"),
            F.Int32("DirectionalRotationXY"),
            F.Int32("DirectionalRotationZ"),
            F.Float("DirectionalFade"),
            F.Float("FogClipDist"),
            F.Float("FogPower"))
        {
            Description = "Lighting Template Data"
        };

        // DATA - REPU (4 bytes) - Reputation Value (single float)
        schemas[new SchemaKey("DATA", "REPU", 4)] = new SubrecordSchema(F.Float("Value"))
        {
            Description = "Reputation Value"
        };

        // DATA - CMNY (4 bytes) - Caravan Money Value
        schemas[new SchemaKey("DATA", "CMNY", 4)] = new SubrecordSchema(F.UInt32("AbsoluteValue"))
        {
            Description = "Caravan Money Value"
        };

        // DATA - CSNO (56 bytes) - Casino Data (contains FormIDs!)
        schemas[new SchemaKey("DATA", "CSNO", 56)] = new SubrecordSchema(
            F.Float("DecksPercentBeforeShuffle"),
            F.Float("BlackJackPayoutRatio"),
            F.UInt32("SlotReelStop1"),
            F.UInt32("SlotReelStop2"),
            F.UInt32("SlotReelStop3"),
            F.UInt32("SlotReelStop4"),
            F.UInt32("SlotReelStop5"),
            F.UInt32("SlotReelStop6"),
            F.UInt32("SlotReelStopW"),
            F.UInt32("NumberOfDecks"),
            F.UInt32("MaxWinnings"),
            F.FormId("Currency"),
            F.FormId("CasinoWinningsQuest"),
            F.UInt32("Flags"))
        {
            Description = "Casino Data"
        };

        // DATA - DEHY (8 bytes) - Dehydration Stage (contains FormID!)
        schemas[new SchemaKey("DATA", "DEHY", 8)] = new SubrecordSchema(
            F.UInt32("TriggerThreshold"),
            F.FormId("ActorEffect"))
        {
            Description = "Dehydration Stage Data"
        };

        // DATA - HUNG (8 bytes) - Hunger Stage (contains FormID!)
        schemas[new SchemaKey("DATA", "HUNG", 8)] = new SubrecordSchema(
            F.UInt32("TriggerThreshold"),
            F.FormId("ActorEffect"))
        {
            Description = "Hunger Stage Data"
        };

        // DATA - RADS (8 bytes) - Radiation Stage (contains FormID!)
        schemas[new SchemaKey("DATA", "RADS", 8)] = new SubrecordSchema(
            F.UInt32("TriggerThreshold"),
            F.FormId("ActorEffect"))
        {
            Description = "Radiation Stage Data"
        };

        // DATA - SLPD (8 bytes) - Sleep Deprivation Stage (contains FormID!)
        schemas[new SchemaKey("DATA", "SLPD", 8)] = new SubrecordSchema(
            F.UInt32("TriggerThreshold"),
            F.FormId("ActorEffect"))
        {
            Description = "Sleep Deprivation Stage Data"
        };

        // DATA - INGR (4 bytes) - Ingredient Weight (single float)
        schemas[new SchemaKey("DATA", "INGR", 4)] = new SubrecordSchema(F.Float("Weight"))
        {
            Description = "Ingredient Weight"
        };

        // ========================================================================
        // DATA-ByteArray-Small EXPLICIT SCHEMAS
        // These are 1-2 byte DATA subrecords. Most don't need swapping, but WATR
        // has a UInt16 that requires byte-swapping!
        // ========================================================================

        // DATA - DIAL (2 bytes) - Dialog Topic Data (2 UInt8 flags, no swap needed)
        schemas[new SchemaKey("DATA", "DIAL", 2)] = SubrecordSchema.ByteArray;

        // DATA - NOTE (1 byte) - Note Type
        schemas[new SchemaKey("DATA", "NOTE", 1)] = SubrecordSchema.ByteArray;

        // DATA - CPTH (1 byte) - Camera Path Zoom Type
        schemas[new SchemaKey("DATA", "CPTH", 1)] = SubrecordSchema.ByteArray;

        // DATA - MSTT (1 byte) - Moveable Static On Local Map flag
        schemas[new SchemaKey("DATA", "MSTT", 1)] = SubrecordSchema.ByteArray;

        // DATA - WATR (2 bytes) - Water Damage (UInt16 - NEEDS SWAP!)
        schemas[new SchemaKey("DATA", "WATR", 2)] = new SubrecordSchema(F.UInt16("Damage"))
        {
            Description = "Water Damage"
        };

        // DATA - HAIR (1 byte) - Hair Flags
        schemas[new SchemaKey("DATA", "HAIR", 1)] = SubrecordSchema.ByteArray;

        // DATA - HDPT (1 byte) - Head Part Playable flag
        schemas[new SchemaKey("DATA", "HDPT", 1)] = SubrecordSchema.ByteArray;

        // DATA - WRLD (1 byte) - Worldspace Flags
        schemas[new SchemaKey("DATA", "WRLD", 1)] = SubrecordSchema.ByteArray;

        // DATA - EYES (1 byte) - Eyes Flags
        schemas[new SchemaKey("DATA", "EYES", 1)] = SubrecordSchema.ByteArray;

        // DATA - RCCT (1 byte) - Recipe Category Flags
        schemas[new SchemaKey("DATA", "RCCT", 1)] = SubrecordSchema.ByteArray;
    }

    /// <summary>
    ///     Register a simple 4-byte schema.
    /// </summary>
    private static void RegisterSimple4Byte(Dictionary<SchemaKey, SubrecordSchema> schemas, string signature,
        string description)
    {
        schemas[new SchemaKey(signature)] = new SubrecordSchema(F.UInt32(description))
        {
            Description = description
        };
    }

    /// <summary>
    ///     Register a simple 4-byte FormID schema.
    /// </summary>
    private static void RegisterSimpleFormId(Dictionary<SchemaKey, SubrecordSchema> schemas, string signature,
        string description)
    {
        schemas[new SchemaKey(signature)] = new SubrecordSchema(F.FormId(description))
        {
            Description = description
        };
    }

    /// <summary>
    ///     Build the set of string subrecords.
    /// </summary>
    private static HashSet<(string Signature, string? RecordType)> BuildStringSubrecords()
    {
        var strings = new HashSet<(string Signature, string? RecordType)>
        {
            // ============================================================
            // GLOBAL STRING SUBRECORDS (apply to all record types)
            // These subrecords contain null-terminated strings that should
            // NOT be byte-swapped during endian conversion.
            // ============================================================
            ("EDID", AnyRecordType), // Editor ID
            ("FULL", AnyRecordType), // Display name
            ("MODL", AnyRecordType), // Model path
            ("DMDL", AnyRecordType), // Destruction model path
            ("ICON", AnyRecordType), // Large icon path
            ("MICO", AnyRecordType), // Small icon path
            ("ICO2", AnyRecordType), // Large icon path 2
            ("MIC2", AnyRecordType), // Small icon path 2
            ("DESC", AnyRecordType), // Description text
            ("BMCT", AnyRecordType), // Ragdoll constraint template
            // NOTE: NNAM varies by record type - string in WRLD/QUST, FormID in MSET
            // Explicit entries below for string cases, schema registry for FormID cases
            ("KFFZ", AnyRecordType), // Animation file list (null-separated)
            ("TX00", AnyRecordType), ("TX01", AnyRecordType), ("TX02", AnyRecordType), ("TX03", AnyRecordType),
            ("TX04", AnyRecordType), ("TX05", AnyRecordType), ("TX06", AnyRecordType), ("TX07", AnyRecordType),
            ("MWD1", AnyRecordType), ("MWD2", AnyRecordType), ("MWD3", AnyRecordType), ("MWD4", AnyRecordType),
            ("MWD5", AnyRecordType), ("MWD6", AnyRecordType), ("MWD7", AnyRecordType),
            ("VANM", AnyRecordType), // Animation path
            ("MOD2", AnyRecordType), ("MOD3", AnyRecordType), ("MOD4", AnyRecordType), // Model paths
            ("NIFZ", AnyRecordType), // NIF file list
            ("SCVR", AnyRecordType), // Script variable name
            ("XATO", AnyRecordType), // Activation prompt override
            ("ITXT", AnyRecordType), // Item text
            ("SCTX", AnyRecordType), // Script source text
            ("RDMP", AnyRecordType), // Region map name

            // NOTE: ONAM meaning varies by record type:
            // - RACE: FormID (Older Race) - handled by schema
            // - SCOL: FormID (Static Object) - handled by schema
            // - WRLD: FormID array (land textures) - handled by schema
            // - AMMO: String (Short Name) - add here

            // AMMO short name
            ("ONAM", "AMMO"),

            // TES4-specific
            ("CNAM", "TES4"),
            ("SNAM", "TES4"),
            ("MAST", "TES4"),

            // INFO/CHAL RNAM is string
            ("RNAM", "INFO"),
            ("RNAM", "CHAL"),

            // NOTE strings
            ("TNAM", "NOTE"),
            ("XNAM", "NOTE"),

            // CELL water noise texture
            ("XNAM", "CELL"),

            // WRLD strings
            ("XNAM", "WRLD"),
            ("NNAM", "WRLD"),

            // WEAP embedded node name
            ("NNAM", "WEAP"),

            // WATR noise texture path
            ("NNAM", "WATR"),

            // INFO strings
            ("NAM1", "INFO"),
            ("NAM2", "INFO"),
            ("NAM3", "INFO"),

            // PROJ muzzle flash model path
            ("NAM1", "PROJ"),

            // DIAL
            ("TDUM", "DIAL"),

            // QUST
            ("CNAM", "QUST"),
            ("NNAM", "QUST"),

            // PERK
            ("EPF2", "PERK"),

            // BPTD
            ("BPTN", "BPTD"),
            ("BPNN", "BPTD"),
            ("BPNT", "BPTD"),
            ("BPNI", "BPTD"),
            ("NAM1", "BPTD"),
            ("NAM4", "BPTD"),

            // AMMO
            ("QNAM", "AMMO"),

            // WTHR cloud textures
            ("DNAM", "WTHR"),
            ("CNAM", "WTHR"),
            ("ANAM", "WTHR"),
            ("BNAM", "WTHR"),

            // CLMT sun textures
            ("FNAM", "CLMT"),
            ("GNAM", "CLMT"),

            // FACT rank titles
            ("MNAM", "FACT"),
            ("FNAM", "FACT"),
            ("INAM", "FACT"),

            // SOUN filename
            ("FNAM", "SOUN"),

            // AVIF short name
            ("ANAM", "AVIF"),

            // RGDL death pose
            ("ANAM", "RGDL"),

            // MUSC filename
            ("FNAM", "MUSC"),

            // MSET audio paths
            ("NAM2", "MSET"),
            ("NAM3", "MSET"),
            ("NAM4", "MSET"),
            ("NAM5", "MSET"),
            ("NAM6", "MSET"),
            ("NAM7", "MSET")
        };

        return strings;
    }

    /// <summary>
    ///     Schema key for lookup - combines signature, optional record type, and optional data length.
    /// </summary>
    /// <param name="Signature">4-character subrecord signature.</param>
    /// <param name="RecordType">Parent record type (null for any).</param>
    /// <param name="DataLength">Data length constraint (null for any, or expected size).</param>
    public readonly record struct SchemaKey(string Signature, string? RecordType = null, int? DataLength = null);
}