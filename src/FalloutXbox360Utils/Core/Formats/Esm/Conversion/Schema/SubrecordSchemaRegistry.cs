using System.Collections.Concurrent;
using F = FalloutXbox360Utils.Core.Formats.Esm.Conversion.Schema.SubrecordField;

namespace FalloutXbox360Utils.Core.Formats.Esm.Conversion.Schema;

/// <summary>
///     Registry for subrecord schemas used in ESM/ESP endian conversion.
///     Provides schema lookup for determining how to byte-swap subrecord data.
/// </summary>
public static class SubrecordSchemaRegistry
{
    private const string? AnyRecordType = null;

    private static readonly Dictionary<SchemaKey, SubrecordSchema> _schemas = BuildSchemaRegistry();

    private static readonly HashSet<(string Signature, string? RecordType)>
        _stringSubrecords = BuildStringSubrecords();

    private static readonly ConcurrentDictionary<(string RecordType, string Subrecord, int DataLength, string
            FallbackType), int>
        _fallbackUsage = new();

    /// <summary>
    ///     Whether fallback logging is enabled.
    /// </summary>
    public static bool EnableFallbackLogging { get; set; }

    /// <summary>
    ///     Gets whether any fallbacks were recorded.
    /// </summary>
    public static bool HasFallbackUsage => !_fallbackUsage.IsEmpty;

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
        if (_schemas.TryGetValue(new SchemaKey(signature, recordType, dataLength), out var schema))
        {
            return schema;
        }

        // Try signature + recordType (any length)
        if (_schemas.TryGetValue(new SchemaKey(signature, recordType), out schema))
        {
            return schema;
        }

        // Try signature + dataLength (any record type)
        if (_schemas.TryGetValue(new SchemaKey(signature, null, dataLength), out schema))
        {
            return schema;
        }

        // Try signature only
        if (_schemas.TryGetValue(new SchemaKey(signature), out schema))
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
    ///     Records a fallback usage for diagnostics.
    /// </summary>
    public static void RecordFallback(string recordType, string subrecord, int dataLength, string fallbackType)
    {
        if (!EnableFallbackLogging)
            return;

        var key = (recordType, subrecord, dataLength, fallbackType);
        _fallbackUsage.AddOrUpdate(key, 1, (_, count) => count + 1);
    }

    /// <summary>
    ///     Clears all recorded fallback usage.
    /// </summary>
    public static void ClearFallbackLog()
    {
        _fallbackUsage.Clear();
    }

    /// <summary>
    ///     Gets the recorded fallback usage, grouped by type.
    /// </summary>
    public static IEnumerable<(string FallbackType, string RecordType, string Subrecord, int DataLength, int Count)>
        GetFallbackUsage()
    {
        return _fallbackUsage
            .Select(kvp => (
                kvp.Key.FallbackType,
                kvp.Key.RecordType,
                kvp.Key.Subrecord,
                kvp.Key.DataLength,
                Count: kvp.Value))
            .OrderBy(x => x.FallbackType)
            .ThenByDescending(x => x.Count)
            .ThenBy(x => x.RecordType)
            .ThenBy(x => x.Subrecord);
    }

    /// <summary>
    ///     Gets all unique 4-character signatures registered in the schema.
    ///     Used for generic subrecord detection in memory dumps.
    /// </summary>
    public static IReadOnlySet<string> GetAllSignatures()
    {
        var signatures = new HashSet<string>();

        // Get signatures from schema registry
        foreach (var key in _schemas.Keys)
        {
            signatures.Add(key.Signature);
        }

        // Get signatures from string subrecords
        foreach (var (signature, _) in _stringSubrecords)
        {
            signatures.Add(signature);
        }

        return signatures;
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
    ///     Gets the byte-reversed signature for big-endian detection.
    ///     E.g., "EDID" -> "DIDE"
    /// </summary>
    public static string GetReversedSignature(string signature)
    {
        var chars = signature.ToCharArray();
        Array.Reverse(chars);
        return new string(chars);
    }

    /// <summary>
    ///     Checks if a subrecord contains string data (no conversion needed).
    /// </summary>
    public static bool IsStringSubrecord(string signature, string recordType)
    {
        // Check record-specific string signatures first (more specific)
        if (_stringSubrecords.Contains((signature, recordType)))
        {
            return true;
        }

        // Check global string signatures (universal like EDID, FULL, MODL)
        return _stringSubrecords.Contains((signature, null));
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
    ///     Schema key for lookup - combines signature, optional record type, and optional data length.
    /// </summary>
    /// <param name="Signature">4-character subrecord signature.</param>
    /// <param name="RecordType">Parent record type (null for any).</param>
    /// <param name="DataLength">Data length constraint (null for any, or expected size).</param>
    public readonly record struct SchemaKey(string Signature, string? RecordType = null, int? DataLength = null);

    #region Schema Registration

    private static Dictionary<SchemaKey, SubrecordSchema> BuildSchemaRegistry()
    {
        var schemas = new Dictionary<SchemaKey, SubrecordSchema>();

        // Simple 4-byte FormID references
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
        RegisterSimple4Byte(schemas, "HCLR", "Hair Color");
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

        // Simple 4-byte non-FormID values
        RegisterSimple4Byte(schemas, "XCLW", "Water Height float");
        RegisterSimple4Byte(schemas, "RPLI", "Region Point List Index");

        // Additional FormID subrecords
        RegisterSimpleFormId(schemas, "ANAM", "Acoustic Space FormID");
        RegisterSimpleFormId(schemas, "CARD", "Card FormID");
        RegisterSimpleFormId(schemas, "CSDI", "Sound FormID");
        RegisterSimpleFormId(schemas, "GNAM", "Grass FormID");
        RegisterSimpleFormId(schemas, "JNAM", "Jump Target FormID");
        RegisterSimpleFormId(schemas, "KNAM", "Keyword FormID");
        RegisterSimpleFormId(schemas, "LVLG", "Global FormID");
        RegisterSimpleFormId(schemas, "MNAM", "Male/Map FormID");
        RegisterSimpleFormId(schemas, "NAM3", "FormID reference 3");
        RegisterSimpleFormId(schemas, "NAM4", "FormID reference 4");
        RegisterSimpleFormId(schemas, "QNAM", "Quest FormID");
        RegisterSimpleFormId(schemas, "RAGA", "Ragdoll FormID");
        RegisterSimpleFormId(schemas, "RCIL", "Recipe Item List FormID");
        RegisterSimpleFormId(schemas, "RDSB", "Region Sound FormID");
        RegisterSimpleFormId(schemas, "SCRO", "Script Object Ref FormID");
        RegisterSimpleFormId(schemas, "TCFU", "Topic Count FormID Upper");
        RegisterSimpleFormId(schemas, "TCLF", "Topic Count FormID Lower");
        RegisterSimpleFormId(schemas, "WNM1", "Weapon Mod Name 1 FormID");
        RegisterSimpleFormId(schemas, "WNM2", "Weapon Mod Name 2 FormID");
        RegisterSimpleFormId(schemas, "WNM3", "Weapon Mod Name 3 FormID");
        RegisterSimpleFormId(schemas, "WNM4", "Weapon Mod Name 4 FormID");
        RegisterSimpleFormId(schemas, "WNM5", "Weapon Mod Name 5 FormID");
        RegisterSimpleFormId(schemas, "WNM6", "Weapon Mod Name 6 FormID");
        RegisterSimpleFormId(schemas, "WNM7", "Weapon Mod Name 7 FormID");
        RegisterSimpleFormId(schemas, "XAMT", "Ammo Type FormID");
        RegisterSimpleFormId(schemas, "XEMI", "Emittance FormID");
        RegisterSimpleFormId(schemas, "XLKR", "Linked Reference FormID");
        RegisterSimpleFormId(schemas, "XMRC", "Merchant Container FormID");
        RegisterSimpleFormId(schemas, "XSRD", "Sound Reference FormID");
        RegisterSimpleFormId(schemas, "XTRG", "Target FormID");

        // Additional non-FormID 4-byte values
        RegisterSimple4Byte(schemas, "CSDT", "Sound Type");
        RegisterSimple4Byte(schemas, "FLTV", "Float Value");
        RegisterSimple4Byte(schemas, "IDLT", "Idle Time");
        RegisterSimple4Byte(schemas, "INFC", "Info Count");
        RegisterSimple4Byte(schemas, "INFX", "Info Index");
        RegisterSimple4Byte(schemas, "INTV", "Interval Value");
        RegisterSimple4Byte(schemas, "NVER", "NavMesh Version");
        RegisterSimple4Byte(schemas, "PKE2", "Package Entry 2");
        RegisterSimple4Byte(schemas, "PKFD", "Package Float Data");
        RegisterSimple4Byte(schemas, "QOBJ", "Quest Objective");
        RegisterSimple4Byte(schemas, "RCLR", "Region Color");
        RegisterSimple4Byte(schemas, "RCOD", "Recipe Output Data");
        RegisterSimple4Byte(schemas, "RCQY", "Recipe Quantity");
        RegisterSimple4Byte(schemas, "RDAT", "Region Data");
        RegisterSimple4Byte(schemas, "RDSI", "Region Sound Index");
        RegisterSimple4Byte(schemas, "SCRV", "Script Variable");
        RegisterSimple4Byte(schemas, "XACT", "Activate Parent Flags");
        RegisterSimple4Byte(schemas, "XAMC", "Ammo Count");
        RegisterSimple4Byte(schemas, "XHLP", "Health Percent");
        RegisterSimple4Byte(schemas, "XLCM", "Level Modifier");
        RegisterSimple4Byte(schemas, "XPRD", "Patrol Data");
        RegisterSimple4Byte(schemas, "XRAD", "Radiation Level");
        RegisterSimple4Byte(schemas, "XRNK", "Faction Rank");
        RegisterSimple4Byte(schemas, "XSRF", "Sound Reference Flags");
        RegisterSimple4Byte(schemas, "XXXX", "Size Prefix");
        RegisterSimpleFormId(schemas, "RNAM", "FormID");
        schemas[new SchemaKey("NAM9", null, 4)] = SubrecordSchema.Simple4Byte("FormID");

        // Conditional 4-byte swaps (depend on data length)
        schemas[new SchemaKey("HNAM", null, 4)] = SubrecordSchema.Simple4Byte();
        schemas[new SchemaKey("ENAM", null, 4)] = SubrecordSchema.Simple4Byte();
        schemas[new SchemaKey("PNAM", null, 4)] = SubrecordSchema.Simple4Byte();
        schemas[new SchemaKey("NAM0", null, 4)] = SubrecordSchema.Simple4Byte();
        schemas[new SchemaKey("VNAM", null, 4)] = SubrecordSchema.Simple4Byte();
        schemas[new SchemaKey("BNAM", null, 4)] = SubrecordSchema.Simple4Byte();
        schemas[new SchemaKey("XSCL", null, 4)] = SubrecordSchema.Simple4Byte("Scale");
        schemas[new SchemaKey("XCNT", null, 4)] = SubrecordSchema.Simple4Byte("Count");
        schemas[new SchemaKey("XRDS", null, 4)] = SubrecordSchema.Simple4Byte("Radius");
        schemas[new SchemaKey("XCCM", null, 4)] = SubrecordSchema.Simple4Byte("Climate");
        schemas[new SchemaKey("XLTW", null, 4)] = SubrecordSchema.Simple4Byte("Water");
        schemas[new SchemaKey("INDX", null, 4)] = SubrecordSchema.Simple4Byte("Index");
        schemas[new SchemaKey("UNAM")] = SubrecordSchema.Simple4Byte();
        schemas[new SchemaKey("WNAM")] = SubrecordSchema.Simple4Byte();
        schemas[new SchemaKey("YNAM")] = SubrecordSchema.Simple4Byte();
        schemas[new SchemaKey("TNAM", null, 4)] = SubrecordSchema.Simple4Byte();
        schemas[new SchemaKey("XNAM", null, 4)] = SubrecordSchema.Simple4Byte();

        // Simple 2-byte swaps
        schemas[new SchemaKey("NAM5", null, 2)] = SubrecordSchema.Simple2Byte();
        schemas[new SchemaKey("EAMT")] = SubrecordSchema.Simple2Byte("Enchantment Amount");
        schemas[new SchemaKey("DNAM", "TXST", 2)] = SubrecordSchema.Simple2Byte("Texture Set Flags");
        schemas[new SchemaKey("PNAM", "WRLD", 2)] = SubrecordSchema.Simple2Byte("Parent World");
        schemas[new SchemaKey("TNAM", "REFR", 2)] = SubrecordSchema.Simple2Byte("Talk Distance");

        // Single byte (flags) - no conversion needed
        schemas[new SchemaKey("FNAM", "REFR", 1)] = SubrecordSchema.ByteArray;
        schemas[new SchemaKey("FNAM", "WATR", 1)] = SubrecordSchema.ByteArray;
        schemas[new SchemaKey("BRUS", "STAT", 1)] = SubrecordSchema.ByteArray;
        schemas[new SchemaKey("FNAM", "GLOB", 1)] = SubrecordSchema.ByteArray;
        schemas[new SchemaKey("FNAM", "DOOR", 1)] = SubrecordSchema.ByteArray;
        schemas[new SchemaKey("MODD", null, 1)] = SubrecordSchema.ByteArray;
        schemas[new SchemaKey("MOSD", null, 1)] = SubrecordSchema.ByteArray;
        schemas[new SchemaKey("PNAM", "MSET", 1)] = SubrecordSchema.ByteArray;
        schemas[new SchemaKey("XAPD", null, 1)] = SubrecordSchema.ByteArray;
        schemas[new SchemaKey("XSED", "REFR", 1)] = SubrecordSchema.ByteArray;

        // Zero-byte markers
        schemas[new SchemaKey("MMRK", "REFR", 0)] = SubrecordSchema.ByteArray;
        schemas[new SchemaKey("NAM0", "RACE", 0)] = SubrecordSchema.ByteArray;
        schemas[new SchemaKey("NAM2", "RACE", 0)] = SubrecordSchema.ByteArray;
        schemas[new SchemaKey("XIBS", null, 0)] = SubrecordSchema.ByteArray;
        schemas[new SchemaKey("XMRK", "REFR", 0)] = SubrecordSchema.ByteArray;
        schemas[new SchemaKey("XPPA", "REFR", 0)] = SubrecordSchema.ByteArray;
        schemas[new SchemaKey("ONAM", "REFR", 0)] = SubrecordSchema.Empty;

        // Byte arrays - no conversion needed
        schemas[new SchemaKey("VNML")] = SubrecordSchema.ByteArray;
        schemas[new SchemaKey("VCLR")] = SubrecordSchema.ByteArray;
        schemas[new SchemaKey("TNAM", "CLMT", 6)] = SubrecordSchema.ByteArray;
        schemas[new SchemaKey("ONAM", "WTHR", 4)] = SubrecordSchema.ByteArray;

        // Model texture hashes - no endian swap
        schemas[new SchemaKey("MODT")] = SubrecordSchema.ByteArray;
        schemas[new SchemaKey("MO2T")] = SubrecordSchema.ByteArray;
        schemas[new SchemaKey("MO3T")] = SubrecordSchema.ByteArray;
        schemas[new SchemaKey("MO4T")] = SubrecordSchema.ByteArray;
        schemas[new SchemaKey("DMDT")] = SubrecordSchema.ByteArray;
        schemas[new SchemaKey("MO2S")] = new SubrecordSchema(F.UInt32("Count")) { ExpectedSize = 0 };
        schemas[new SchemaKey("MO3S")] = new SubrecordSchema(F.UInt32("Count")) { ExpectedSize = 0 };
        schemas[new SchemaKey("MO4S")] = new SubrecordSchema(F.UInt32("Count")) { ExpectedSize = 0 };
        schemas[new SchemaKey("MODS")] = new SubrecordSchema(F.UInt32("Count")) { ExpectedSize = 0 };
        schemas[new SchemaKey("NIFT")] = new SubrecordSchema(F.UInt32("Count")) { ExpectedSize = 0 };

        // FaceGen data
        schemas[new SchemaKey("FGGS", null, 200)] = SubrecordSchema.FloatArray;
        schemas[new SchemaKey("FGTS", null, 200)] = SubrecordSchema.FloatArray;
        schemas[new SchemaKey("FGGA", null, 120)] = SubrecordSchema.FloatArray;

        // File header
        schemas[new SchemaKey("HEDR", null, 12)] = new SubrecordSchema(
            F.Float("Version"),
            F.UInt32("NumRecords"),
            F.UInt32("NextObjectId"))
        {
            Description = "File Header"
        };

        // Register category-specific schemas
        SubrecordActorSchemas.Register(schemas);
        SubrecordItemSchemas.Register(schemas);
        SubrecordDialogueSchemas.Register(schemas);
        SubrecordEffectSchemas.Register(schemas);
        SubrecordWorldSchemas.Register(schemas);
        SubrecordNavmeshSchemas.Register(schemas);
        SubrecordCellAndMiscSchemas.Register(schemas);

        return schemas;
    }

    private static HashSet<(string Signature, string? RecordType)> BuildStringSubrecords()
    {
        return
        [
            // Global string subrecords
            ("EDID", AnyRecordType),
            ("FULL", AnyRecordType),
            ("MODL", AnyRecordType),
            ("DMDL", AnyRecordType),
            ("ICON", AnyRecordType),
            ("MICO", AnyRecordType),
            ("ICO2", AnyRecordType),
            ("MIC2", AnyRecordType),
            ("DESC", AnyRecordType),
            ("BMCT", AnyRecordType),
            ("KFFZ", AnyRecordType),
            ("TX00", AnyRecordType), ("TX01", AnyRecordType), ("TX02", AnyRecordType), ("TX03", AnyRecordType),
            ("TX04", AnyRecordType), ("TX05", AnyRecordType), ("TX06", AnyRecordType), ("TX07", AnyRecordType),
            ("MWD1", AnyRecordType), ("MWD2", AnyRecordType), ("MWD3", AnyRecordType), ("MWD4", AnyRecordType),
            ("MWD5", AnyRecordType), ("MWD6", AnyRecordType), ("MWD7", AnyRecordType),
            ("VANM", AnyRecordType),
            ("MOD2", AnyRecordType), ("MOD3", AnyRecordType), ("MOD4", AnyRecordType),
            ("NIFZ", AnyRecordType),
            ("SCVR", AnyRecordType),
            ("XATO", AnyRecordType),
            ("ITXT", AnyRecordType),
            ("SCTX", AnyRecordType),
            ("RDMP", AnyRecordType),

            // Record-specific strings
            ("ONAM", "AMMO"),
            ("CNAM", "TES4"),
            ("SNAM", "TES4"),
            ("MAST", "TES4"),
            ("RNAM", "INFO"),
            ("RNAM", "CHAL"),
            ("TNAM", "NOTE"),
            ("XNAM", "NOTE"),
            ("XNAM", "CELL"),
            ("XNAM", "WRLD"),
            ("NNAM", "WRLD"),
            ("NNAM", "WEAP"),
            ("NNAM", "WATR"),
            ("NAM1", "INFO"),
            ("NAM2", "INFO"),
            ("NAM3", "INFO"),
            ("NAM1", "PROJ"),
            ("TDUM", "DIAL"),
            ("CNAM", "QUST"),
            ("NNAM", "QUST"),
            ("EPF2", "PERK"),
            ("BPTN", "BPTD"),
            ("BPNN", "BPTD"),
            ("BPNT", "BPTD"),
            ("BPNI", "BPTD"),
            ("NAM1", "BPTD"),
            ("NAM4", "BPTD"),
            ("QNAM", "AMMO"),
            ("DNAM", "WTHR"),
            ("CNAM", "WTHR"),
            ("ANAM", "WTHR"),
            ("BNAM", "WTHR"),
            ("FNAM", "CLMT"),
            ("GNAM", "CLMT"),
            ("MNAM", "FACT"),
            ("FNAM", "FACT"),
            ("INAM", "FACT"),
            ("FNAM", "SOUN"),
            ("ANAM", "AVIF"),
            ("ANAM", "RGDL"),
            ("FNAM", "MUSC"),
            ("NAM2", "MSET"),
            ("NAM3", "MSET"),
            ("NAM4", "MSET"),
            ("NAM5", "MSET"),
            ("NAM6", "MSET"),
            ("NAM7", "MSET")
        ];
    }

    #endregion
}
