using F = FalloutXbox360Utils.Core.Formats.Esm.Conversion.Schema.SubrecordField;

namespace FalloutXbox360Utils.Core.Formats.Esm.Conversion.Schema;

public static partial class SubrecordSchemaRegistry
{
    /// <summary>
    ///     Build the complete schema registry from declarative definitions.
    /// </summary>
    private static Dictionary<SchemaKey, SubrecordSchema> BuildSchemaRegistry()
    {
        var schemas = new Dictionary<SchemaKey, SubrecordSchema>();

        // ========================================================================
        // SIMPLE 4-BYTE FORMID REFERENCES
        // ========================================================================
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

        // ========================================================================
        // SIMPLE 4-BYTE NON-FORMID VALUES
        // ========================================================================
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

        // ========================================================================
        // CONDITIONAL 4-BYTE SWAPS (depend on data length)
        // ========================================================================
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

        // ========================================================================
        // SIMPLE 2-BYTE SWAPS
        // ========================================================================
        schemas[new SchemaKey("NAM5", null, 2)] = SubrecordSchema.Simple2Byte();
        schemas[new SchemaKey("EAMT")] = SubrecordSchema.Simple2Byte("Enchantment Amount");
        schemas[new SchemaKey("DNAM", "TXST", 2)] = SubrecordSchema.Simple2Byte("Texture Set Flags");
        schemas[new SchemaKey("PNAM", "WRLD", 2)] = SubrecordSchema.Simple2Byte("Parent World");
        schemas[new SchemaKey("TNAM", "REFR", 2)] = SubrecordSchema.Simple2Byte("Talk Distance");

        // ========================================================================
        // SINGLE BYTE (FLAGS) - NO CONVERSION NEEDED
        // ========================================================================
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

        // ========================================================================
        // ZERO-BYTE MARKERS - NO DATA
        // ========================================================================
        schemas[new SchemaKey("MMRK", "REFR", 0)] = SubrecordSchema.ByteArray;
        schemas[new SchemaKey("NAM0", "RACE", 0)] = SubrecordSchema.ByteArray;
        schemas[new SchemaKey("NAM2", "RACE", 0)] = SubrecordSchema.ByteArray;
        schemas[new SchemaKey("XIBS", null, 0)] = SubrecordSchema.ByteArray;
        schemas[new SchemaKey("XMRK", "REFR", 0)] = SubrecordSchema.ByteArray;
        schemas[new SchemaKey("XPPA", "REFR", 0)] = SubrecordSchema.ByteArray;
        schemas[new SchemaKey("ONAM", "REFR", 0)] = SubrecordSchema.Empty;

        // ========================================================================
        // BYTE ARRAYS - NO CONVERSION NEEDED
        // ========================================================================
        schemas[new SchemaKey("VNML")] = SubrecordSchema.ByteArray;
        schemas[new SchemaKey("VCLR")] = SubrecordSchema.ByteArray;
        schemas[new SchemaKey("TNAM", "CLMT", 6)] = SubrecordSchema.ByteArray;
        schemas[new SchemaKey("ONAM", "WTHR", 4)] = SubrecordSchema.ByteArray;

        // ========================================================================
        // MODEL TEXTURE HASHES - NO ENDIAN SWAP
        // ========================================================================
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

        // ========================================================================
        // FACEGEN DATA
        // ========================================================================
        schemas[new SchemaKey("FGGS", null, 200)] = SubrecordSchema.FloatArray;
        schemas[new SchemaKey("FGTS", null, 200)] = SubrecordSchema.FloatArray;
        schemas[new SchemaKey("FGGA", null, 120)] = SubrecordSchema.FloatArray;

        // ========================================================================
        // FILE HEADER
        // ========================================================================
        schemas[new SchemaKey("HEDR", null, 12)] = new SubrecordSchema(
            F.Float("Version"),
            F.UInt32("NumRecords"),
            F.UInt32("NextObjectId"))
        {
            Description = "File Header"
        };

        // Register category-specific schemas
        RegisterActorSchemas(schemas);
        RegisterItemSchemas(schemas);
        RegisterDialogueSchemas(schemas);
        RegisterEffectSchemas(schemas);
        RegisterWorldSchemas(schemas);
        RegisterNavmeshSchemas(schemas);
        RegisterCellAndMiscSchemas(schemas);

        return schemas;
    }

    /// <summary>
    ///     Build the set of string subrecords.
    /// </summary>
    private static HashSet<(string Signature, string? RecordType)> BuildStringSubrecords()
    {
        var strings = new HashSet<(string Signature, string? RecordType)>
        {
            // GLOBAL STRING SUBRECORDS
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

            // RECORD-SPECIFIC STRINGS
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
        };

        return strings;
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
            ExpectedSize = -1,
            Description = "Navmesh Triangles Array"
        };

        // NVCA - Cover Triangles (array of uint16)
        schemas[new SchemaKey("NVCA")] = new SubrecordSchema(F.UInt16("Triangle"))
        {
            ExpectedSize = -1,
            Description = "Navmesh Cover Triangles"
        };

        // NVDP - Navmesh Door Links (8 bytes each)
        schemas[new SchemaKey("NVDP")] = new SubrecordSchema(
            F.FormId("DoorRef"),
            F.UInt16("Triangle"),
            F.Padding(2))
        {
            ExpectedSize = -1,
            Description = "Navmesh Door Links Array"
        };

        // NVEX - Navmesh Edge Links (10 bytes each)
        schemas[new SchemaKey("NVEX")] = new SubrecordSchema(
            F.UInt32("Type"),
            F.FormId("Navmesh"),
            F.UInt16("Triangle"))
        {
            ExpectedSize = -1,
            Description = "Navmesh Edge Links Array"
        };

        // DATA - NAVM (20 bytes)
        schemas[new SchemaKey("DATA", "NAVM", 20)] = new SubrecordSchema(
            F.FormId("Cell"),
            F.UInt32("VertexCount"),
            F.UInt32("TriangleCount"),
            F.UInt32("EdgeLinkCount"),
            F.UInt32("DoorLinkCount"))
        {
            Description = "Navmesh Data"
        };

        // DATA - NAVM (24 bytes) - alternate size
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
    }

    /// <summary>
    ///     Register reference-related schemas (REFR, ACHR, ACRE).
    /// </summary>
    private static void RegisterWorldSchemas(Dictionary<SchemaKey, SubrecordSchema> schemas)
    {
        // ========================================================================
        // REFERENCE SCHEMAS (REFR, ACHR, ACRE)
        // ========================================================================

        // DATA - REFR/ACHR/ACRE (24 bytes = PosRot)
        schemas[new SchemaKey("DATA", "REFR", 24)] = new SubrecordSchema(F.PosRot("PosRot"));
        schemas[new SchemaKey("DATA", "ACHR", 24)] = new SubrecordSchema(F.PosRot("PosRot"));
        schemas[new SchemaKey("DATA", "ACRE", 24)] = new SubrecordSchema(F.PosRot("PosRot"));
        schemas[new SchemaKey("DATA", "PGRE", 24)] = SubrecordSchema.FloatArray;

        // XTEL - Door Teleport (32 bytes) — PDB: DoorTeleportData
        // pLinkedDoor(FormID) + position(NiPoint3) + rotation(NiPoint3) + cFlags(uchar) + pad(3)
        // Disasm confirms: Save uses stb for cFlags (single byte), buffer memset pads bytes 29-31 to 0.
        schemas[new SchemaKey("XTEL", null, 32)] = new SubrecordSchema(
            F.FormId("DestinationDoor"),
            F.Float("PosX"), F.Float("PosY"), F.Float("PosZ"),
            F.Float("RotX"), F.Float("RotY"), F.Float("RotZ"),
            F.UInt8("Flags"),
            F.Padding(3))
        {
            Description = "Door Teleport Destination (DoorTeleportData)"
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

        // XLOC - Lock Information (20 bytes)
        // PDB: REFR_LOCK { cBaseLevel(uint8,+0), pKey(FormID,+4), cFlags(char,+8),
        //   uiNumTries(uint32,+12), uiTimesUnlocked(uint32,+16) }
        // Endian() swaps pKey, uiNumTries, uiTimesUnlocked. Single bytes don't swap.
        schemas[new SchemaKey("XLOC", null, 20)] = new SubrecordSchema(
            F.UInt8("Level"),
            F.Padding(3),
            F.FormId("Key"),
            F.UInt8("Flags"),
            F.Padding(3),
            F.UInt32("NumTries"),
            F.UInt32("TimesUnlocked"))
        {
            Description = "Lock Information"
        };

        // XLOD - LOD Data (12 bytes)
        schemas[new SchemaKey("XLOD", null, 12)] = new SubrecordSchema(F.Vec3("LOD"))
        {
            Description = "LOD Data"
        };

        // XESP - Enable Parent (8 bytes)
        schemas[new SchemaKey("XESP", null, 8)] = new SubrecordSchema(
            F.FormId("ParentRef"),
            F.UInt8("Flags"),
            F.Padding(3))
        {
            Description = "Enable Parent"
        };

        // XCLP - Linked Reference Color (8 bytes)
        schemas[new SchemaKey("XCLP", null, 8)] = new SubrecordSchema(
            F.UInt32("LinkStartColor"),
            F.UInt32("LinkEndColor"))
        {
            Description = "Linked Reference Color"
        };

        // XNDP - Navigation Door Portal (8 bytes)
        schemas[new SchemaKey("XNDP", null, 8)] = new SubrecordSchema(
            F.FormId("Navmesh"),
            F.UInt16("TriangleIndex"),
            F.Padding(2))
        {
            Description = "Navigation Door Portal"
        };

        schemas[new SchemaKey("CNAM", "REFR", 4)] = SubrecordSchema.Simple4Byte("Audio Location");
        schemas[new SchemaKey("NNAM", "REFR", 4)] = SubrecordSchema.Simple4Byte("Linked Ref Keyword FormID");

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
    }
}
