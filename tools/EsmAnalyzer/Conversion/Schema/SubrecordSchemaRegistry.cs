using F = EsmAnalyzer.Conversion.Schema.SubrecordField;

namespace EsmAnalyzer.Conversion.Schema;

/// <summary>
///     Defines subrecord schemas by key (signature + optional record type + optional data length).
///     This is the single source of truth for subrecord conversion rules.
/// </summary>
public static class SubrecordSchemaRegistry
{
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
                return imadSchema;
        }

        // Try exact match
        if (s_schemas.TryGetValue(new SchemaKey(signature, recordType, dataLength), out var schema))
            return schema;

        // Try signature + recordType (any length)
        if (s_schemas.TryGetValue(new SchemaKey(signature, recordType), out schema))
            return schema;

        // Try signature + dataLength (any record type)
        if (s_schemas.TryGetValue(new SchemaKey(signature, null, dataLength), out schema))
            return schema;

        // Try signature only
        if (s_schemas.TryGetValue(new SchemaKey(signature), out schema))
            return schema;

        // DATA fallback: mirror switch behavior for small fixed-size blocks
        if (signature == "DATA")
        {
            if (dataLength <= 2)
                return SubrecordSchema.ByteArray;

            if (dataLength <= 64 && dataLength % 4 == 0)
                return SubrecordSchema.FloatArray;

            // Larger or irregular DATA blocks default to no swap
            return SubrecordSchema.ByteArray;
        }

        // WTHR uses keyed *IAD subrecords (e.g., \x00IAD, @IAD, AIAD) for float pairs
        if (recordType == "WTHR" && signature.Length == 4 && signature[1] == 'I' && signature[2] == 'A' &&
            signature[3] == 'D')
            return SubrecordSchema.FloatArray;

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
            return SubrecordSchema.String;

        // Known float array subrecords in IMAD
        if (signature is "DNAM" or "BNAM" or "VNAM" or "TNAM" or "NAM3" or "RNAM" or "SNAM"
            or "UNAM" or "NAM1" or "NAM2" or "WNAM" or "XNAM" or "YNAM" or "NAM4")
            return SubrecordSchema.FloatArray;

        // Keyed *IAD subrecords (e.g., @IAD, AIAD, BIAD, etc.) - time/value float pairs
        if (signature.Length == 4 && signature[1] == 'I' && signature[2] == 'A' && signature[3] == 'D')
            return SubrecordSchema.FloatArray;

        // Unknown IMAD subrecord - treat as float array if divisible by 4
        return SubrecordSchema.FloatArray;
    }

    /// <summary>
    ///     Checks if a subrecord contains string data (no conversion needed).
    /// </summary>
    public static bool IsStringSubrecord(string signature, string recordType)
    {
        // Check global string signatures first
        if (s_stringSubrecords.Contains((signature, null)))
            return true;

        // Check record-specific string signatures
        return s_stringSubrecords.Contains((signature, recordType));
    }

    /// <summary>
    ///     Build the complete schema registry from declarative definitions.
    /// </summary>
    private static Dictionary<SchemaKey, SubrecordSchema> BuildSchemaRegistry()
    {
        var schemas = new Dictionary<SchemaKey, SubrecordSchema>();

        // ========================================================================
        // SIMPLE 4-BYTE FORMID/UINT32 SWAPS
        // ========================================================================
        // These subrecords are always a single 4-byte value
        RegisterSimple4Byte(schemas, "NAME", "FormID reference");
        RegisterSimple4Byte(schemas, "TPLT", "Template FormID");
        RegisterSimple4Byte(schemas, "VTCK", "Voice Type FormID");
        RegisterSimple4Byte(schemas, "LNAM", "Load Screen FormID");
        RegisterSimple4Byte(schemas, "LTMP", "Lighting Template FormID");
        RegisterSimple4Byte(schemas, "INAM", "Idle FormID");
        RegisterSimple4Byte(schemas, "REPL", "Repair List FormID");
        RegisterSimple4Byte(schemas, "ZNAM", "Combat Style FormID");
        RegisterSimple4Byte(schemas, "XOWN", "Owner FormID");
        RegisterSimple4Byte(schemas, "XEZN", "Encounter Zone FormID");
        RegisterSimple4Byte(schemas, "XCAS", "Acoustic Space FormID");
        RegisterSimple4Byte(schemas, "XCIM", "Image Space FormID");
        RegisterSimple4Byte(schemas, "XCMO", "Music Type FormID");
        RegisterSimple4Byte(schemas, "XCWT", "Water FormID");
        RegisterSimple4Byte(schemas, "PKID", "Package FormID");
        RegisterSimple4Byte(schemas, "NAM6", "FormID reference 6");
        RegisterSimple4Byte(schemas, "NAM7", "FormID reference 7");
        RegisterSimple4Byte(schemas, "NAM8", "FormID reference 8");
        RegisterSimple4Byte(schemas, "HCLR", "Hair Color");
        RegisterSimple4Byte(schemas, "ETYP", "Equipment Type");
        RegisterSimple4Byte(schemas, "WMI1", "Weapon Mod 1");
        RegisterSimple4Byte(schemas, "WMI2", "Weapon Mod 2");
        RegisterSimple4Byte(schemas, "WMI3", "Weapon Mod 3");
        RegisterSimple4Byte(schemas, "WMS1", "Weapon Mod Scope");
        RegisterSimple4Byte(schemas, "WMS2", "Weapon Mod Scope 2");
        RegisterSimple4Byte(schemas, "EFID", "Effect ID FormID");
        RegisterSimple4Byte(schemas, "SCRI", "Script FormID");
        RegisterSimple4Byte(schemas, "CSCR", "Companion Script FormID");
        RegisterSimple4Byte(schemas, "BIPL", "Body Part List FormID");
        RegisterSimple4Byte(schemas, "EITM", "Enchantment Item FormID");
        RegisterSimple4Byte(schemas, "TCLT", "Target Creature List FormID");
        RegisterSimple4Byte(schemas, "QSTI", "Quest Stage Item FormID");
        RegisterSimple4Byte(schemas, "SPLO", "Spell List Override FormID");
        RegisterSimple4Byte(schemas, "XCLW", "Water Height float");
        RegisterSimple4Byte(schemas, "RPLI", "Region Point List Index");

        // Additional 4-byte FormID/uint32 subrecords (from fallback analysis)
        RegisterSimple4Byte(schemas, "ANAM", "Acoustic Space FormID"); // ASPC, DOOR (TERM is 1 byte handled separately)
        RegisterSimple4Byte(schemas, "CARD", "Card FormID"); // CDCK
        RegisterSimple4Byte(schemas, "CSDI", "Sound FormID"); // CREA
        RegisterSimple4Byte(schemas, "CSDT", "Sound Type"); // CREA
        RegisterSimple4Byte(schemas, "FLTV", "Float Value"); // GLOB
        RegisterSimple4Byte(schemas, "GNAM", "Grass FormID"); // LTEX, MSET, ALOC
        RegisterSimple4Byte(schemas, "IDLT", "Idle Time"); // IDLM, PACK
        RegisterSimple4Byte(schemas, "INFC", "Info Count"); // DIAL
        RegisterSimple4Byte(schemas, "INFX", "Info Index"); // DIAL
        RegisterSimple4Byte(schemas, "INTV", "Interval Value"); // CCRD
        RegisterSimple4Byte(schemas, "JNAM", "Jump Target FormID"); // MSET
        RegisterSimple4Byte(schemas, "KNAM", "Keyword FormID"); // INFO, MSET
        RegisterSimple4Byte(schemas, "LVLG", "Global FormID"); // LVLI
        RegisterSimple4Byte(schemas, "MNAM", "Male/Map FormID"); // FURN, REFR (RACE is 0 bytes handled separately)
        RegisterSimple4Byte(schemas, "NAM3", "FormID reference 3"); // WRLD, ALOC
        RegisterSimple4Byte(schemas, "NAM4", "FormID reference 4"); // NPC_, CREA, WRLD
        RegisterSimple4Byte(schemas, "NVER", "NavMesh Version"); // NAVI, NAVM, RGDL
        RegisterSimple4Byte(schemas, "PKE2", "Package Entry 2"); // PACK
        RegisterSimple4Byte(schemas, "PKFD", "Package Float Data"); // PACK
        RegisterSimple4Byte(schemas, "QNAM", "Quest FormID"); // CONT
        RegisterSimple4Byte(schemas, "QOBJ", "Quest Objective"); // QUST
        RegisterSimple4Byte(schemas, "RAGA", "Ragdoll FormID"); // BPTD
        RegisterSimple4Byte(schemas, "RCIL", "Recipe Item List"); // AMMO, RCPE
        RegisterSimple4Byte(schemas, "RCLR", "Region Color"); // REGN
        RegisterSimple4Byte(schemas, "RCOD", "Recipe Output Data"); // RCPE
        RegisterSimple4Byte(schemas, "RCQY", "Recipe Quantity"); // RCPE
        RegisterSimple4Byte(schemas, "RDAT", "Region Data"); // ASPC
        RegisterSimple4Byte(schemas, "RDSB", "Region Sound FormID"); // REGN
        RegisterSimple4Byte(schemas, "RDSI", "Region Sound Index"); // REGN
        RegisterSimple4Byte(schemas, "SCRO", "Script Object Ref"); // SCPT, TERM, REFR
        RegisterSimple4Byte(schemas, "SCRV", "Script Variable"); // SCPT, TERM, REFR
        RegisterSimple4Byte(schemas, "TCFU", "Topic Count FormID Upper"); // INFO
        RegisterSimple4Byte(schemas, "TCLF", "Topic Count FormID Lower"); // INFO
        RegisterSimple4Byte(schemas, "WNM1", "Weapon Name 1"); // WEAP
        RegisterSimple4Byte(schemas, "WNM2", "Weapon Name 2"); // WEAP
        RegisterSimple4Byte(schemas, "WNM3", "Weapon Name 3"); // WEAP
        RegisterSimple4Byte(schemas, "WNM4", "Weapon Name 4"); // WEAP
        RegisterSimple4Byte(schemas, "WNM5", "Weapon Name 5"); // WEAP
        RegisterSimple4Byte(schemas, "WNM6", "Weapon Name 6"); // WEAP
        RegisterSimple4Byte(schemas, "WNM7", "Weapon Name 7"); // WEAP
        RegisterSimple4Byte(schemas, "XACT", "Activate Parent Flags"); // REFR
        RegisterSimple4Byte(schemas, "XAMC", "Ammo Count"); // REFR
        RegisterSimple4Byte(schemas, "XAMT", "Ammo Type FormID"); // REFR
        RegisterSimple4Byte(schemas, "XEMI", "Emittance FormID"); // REFR
        RegisterSimple4Byte(schemas, "XHLP", "Health Percent"); // REFR
        RegisterSimple4Byte(schemas, "XLCM", "Level Modifier"); // ACRE, ACHR
        RegisterSimple4Byte(schemas, "XLKR", "Linked Reference"); // REFR, ACRE, ACHR
        RegisterSimple4Byte(schemas, "XMRC", "Merchant Container"); // ACHR, ACRE
        RegisterSimple4Byte(schemas, "XPRD", "Patrol Data"); // REFR
        RegisterSimple4Byte(schemas, "XRAD", "Radiation Level"); // REFR
        RegisterSimple4Byte(schemas, "XRNK", "Faction Rank"); // REFR
        RegisterSimple4Byte(schemas, "XSRD", "Sound Reference"); // REFR
        RegisterSimple4Byte(schemas, "XSRF", "Sound Reference Flags"); // REFR
        RegisterSimple4Byte(schemas, "XTRG", "Target FormID"); // REFR
        RegisterSimple4Byte(schemas, "XXXX", "Size Prefix"); // WRLD

        // RNAM - depends on record type
        // RNAM in INFO/CHAL/CREA/REGN is string or special, others are FormID
        schemas[new SchemaKey("RNAM")] = SubrecordSchema.Simple4Byte("FormID");

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
        schemas[new SchemaKey("INDX", "QUST", 2)] = SubrecordSchema.Simple2Byte("Quest Index"); // QUST
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
        schemas[new SchemaKey("DNAM", "WEAP")] = SubrecordSchema.FloatArray; // WEAP 204 bytes = 51 floats/uint32s
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
        schemas[new SchemaKey("ENIT", "ALCH", 20)] = new SubrecordSchema(
            F.UInt32("Value"),
            F.Bytes("Flags", 12), // 12 bytes (middle section not swapped based on switch code)
            F.UInt32("WithdrawalEffect"))
        {
            Description = "Alchemy/Potion Data"
        };

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
        schemas[new SchemaKey("PNAM", "WTHR")] = SubrecordSchema.ByteArray; // Weather cloud colors
        schemas[new SchemaKey("NAM0", "WTHR")] = SubrecordSchema.ByteArray; // Weather colors

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
        schemas[new SchemaKey("CTDA", null, 28)] = new SubrecordSchema(
            F.UInt32("TypeAndFlags"),
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
        schemas[new SchemaKey("PKDT", null, 12)] = new SubrecordSchema(
            F.UInt32("GeneralFlags"),
            F.UInt8("Type"),
            F.UInt8("Unused"),
            F.UInt16("FalloutBehaviorFlags"),
            F.UInt16("TypeSpecificFlags"),
            F.UInt16("Unknown"))
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
        schemas[new SchemaKey("CRDT", null, 16)] = new SubrecordSchema(
            F.UInt16("Damage"),
            F.UInt16("Unknown"),
            F.Float("Multiplier"),
            F.Padding(8))
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
        schemas[new SchemaKey("DODT", null, 36)] = new SubrecordSchema(
            F.Float("MinWidth"), F.Float("MaxWidth"),
            F.Float("MinHeight"), F.Float("MaxHeight"),
            F.Float("Depth"), F.Float("Shininess"), F.Float("ParallaxScale"),
            F.UInt8("Passes"), F.UInt8("Flags"), F.Padding(2),
            F.ColorRgba("Color"))
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

        // VATS - VATS Data (20 bytes = 5 × uint32)
        schemas[new SchemaKey("VATS", null, 20)] = new SubrecordSchema(
            F.Float("PlayerAP"),
            F.Float("AttackChance"),
            F.FormId("SilentChanceMod"),
            F.FormId("BodyPart"),
            F.Float("Damage"))
        {
            Description = "VATS Data"
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
        schemas[new SchemaKey("MODT")] = SubrecordSchema.TextureHashes;
        schemas[new SchemaKey("MO2T")] = SubrecordSchema.TextureHashes;
        schemas[new SchemaKey("MO3T")] = SubrecordSchema.TextureHashes;
        schemas[new SchemaKey("MO4T")] = SubrecordSchema.TextureHashes;
        schemas[new SchemaKey("DMDT")] = SubrecordSchema.TextureHashes;

        // NAM2 - Model Info in PROJ
        schemas[new SchemaKey("NAM2", "PROJ")] = SubrecordSchema.TextureHashes;

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
        schemas[new SchemaKey("PTDT", null, 16)] = new SubrecordSchema(
            F.Int32("Type"),
            F.UInt32("Union"),
            F.Int32("CountDistance"),
            F.Float("Unknown"))
        {
            Description = "Package Target"
        };
        schemas[new SchemaKey("PTD2", null, 16)] = new SubrecordSchema(
            F.Int32("Type"),
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
        schemas[new SchemaKey("PLDT", null, 12)] = new SubrecordSchema(
            F.Int32("Type"),
            F.UInt32("Union"),
            F.Int32("Radius"))
        {
            Description = "Package Location"
        };
        schemas[new SchemaKey("PLD2", null, 12)] = new SubrecordSchema(
            F.Int32("Type"),
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
            F.UInt16("RangeMultFlags"),
            F.Padding(2),
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
        schemas[new SchemaKey("SCHR", null, 20)] = new SubrecordSchema(
            F.Padding(4),
            F.UInt32("RefCount"),
            F.UInt32("CompiledSize"),
            F.UInt32("VariableCount"),
            F.UInt16("Type"),
            F.UInt16("Flags"))
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
        schemas[new SchemaKey("AIDT", null, 20)] = new SubrecordSchema(
            F.UInt8("Aggression"),
            F.UInt8("Confidence"),
            F.UInt8("Energy"),
            F.UInt8("Morality"),
            F.UInt8("Mood"),
            F.Bytes("XboxData", 3), // Zeroed during conversion
            F.Bytes("Remaining", 12))
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

        // DATA - CELL (1 byte)
        schemas[new SchemaKey("DATA", "CELL", 1)] = SubrecordSchema.ByteArray;

        // DATA - PERK (variable, small)
        schemas[new SchemaKey("DATA", "PERK")] = SubrecordSchema.ByteArray;

        // DATA - GMST (4 bytes = int or float)
        schemas[new SchemaKey("DATA", "GMST", 4)] = SubrecordSchema.Simple4Byte();
        schemas[new SchemaKey("DATA", "GMST")] = SubrecordSchema.ByteArray; // String GMSTs or variable-length data
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
    ///     Build the set of string subrecords.
    /// </summary>
    private static HashSet<(string, string?)> BuildStringSubrecords()
    {
        var strings = new HashSet<(string, string?)>
        {
            // Global string signatures (any record type)
            ("EDID", null),
            ("FULL", null),
            ("MODL", null),
            ("DMDL", null),
            ("ICON", null),
            ("MICO", null),
            ("ICO2", null),
            ("MIC2", null),
            ("DESC", null),
            ("BMCT", null),
            ("NNAM", null),
            ("KFFZ", null),
            ("TX00", null), ("TX01", null), ("TX02", null), ("TX03", null),
            ("TX04", null), ("TX05", null), ("TX06", null), ("TX07", null),
            ("MWD1", null), ("MWD2", null), ("MWD3", null), ("MWD4", null),
            ("MWD5", null), ("MWD6", null), ("MWD7", null),
            ("VANM", null),
            ("MOD2", null), ("MOD3", null), ("MOD4", null),
            ("NIFZ", null),
            ("SCVR", null),
            ("XATO", null),
            ("ITXT", null),
            ("ONAM", null),
            ("SCTX", null),
            ("NAM1", null),
            ("RDMP", null),

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

            // INFO strings
            ("NAM1", "INFO"),
            ("NAM2", "INFO"),
            ("NAM3", "INFO"),

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