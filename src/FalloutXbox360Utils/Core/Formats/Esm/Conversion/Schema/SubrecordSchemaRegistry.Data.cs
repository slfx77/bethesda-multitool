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
    ///     Register world-related schemas (WRLD, CELL, REFR, LAND, REGN, WTHR, etc.).
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

        // XTEL - Door Teleport (32 bytes)
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

        // XLOC - Lock Information (20 bytes)
        schemas[new SchemaKey("XLOC", null, 20)] = new SubrecordSchema(
            F.UInt8("Level"),
            F.Padding(3),
            F.FormId("Key"),
            F.UInt32("Flags"),
            F.Padding(8))
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

        // ========================================================================
        // CELL SCHEMAS
        // ========================================================================

        // XCLL - Cell Lighting (40 bytes)
        schemas[new SchemaKey("XCLL", null, 40)] = new SubrecordSchema(
            F.UInt32("AmbientColor"), F.UInt32("DirectionalColor"),
            F.UInt32("FogColor"), F.Float("FogNear"), F.Float("FogFar"),
            F.Int32("DirectionalRotationXY"), F.Int32("DirectionalRotationZ"),
            F.Float("DirectionalFade"), F.Float("FogClipDistance"), F.Float("FogPow"))
        {
            Description = "Cell Lighting"
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

        schemas[new SchemaKey("DATA", "CELL", 1)] = SubrecordSchema.ByteArray;

        // ========================================================================
        // LAND SCHEMAS
        // ========================================================================

        // VTXT - Vertex Texture Blend (repeating 8-byte entries)
        schemas[new SchemaKey("VTXT", "LAND")] = new SubrecordSchema(
            F.UInt16("Position"),
            F.Bytes("Unused", 2),
            F.Float("Opacity"))
        {
            ExpectedSize = -1,
            Description = "Land Vertex Texture Blend Array"
        };

        // ATXT/BTXT - Texture Alpha (8 bytes)
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

        schemas[new SchemaKey("DATA", "LAND", 4)] = new SubrecordSchema(F.UInt32("Flags"));
        schemas[new SchemaKey("HNAM", "LTEX", 3)] = SubrecordSchema.ByteArray;
        schemas[new SchemaKey("SNAM", "LTEX", 1)] = SubrecordSchema.ByteArray;

        // ========================================================================
        // WORLDSPACE SCHEMAS (WRLD)
        // ========================================================================

        schemas[new SchemaKey("CNAM", "WRLD", 4)] = SubrecordSchema.Simple4Byte("Climate FormID");
        schemas[new SchemaKey("NAM2", "WRLD", 4)] = SubrecordSchema.Simple4Byte("NAM2 FormID");
        schemas[new SchemaKey("DATA", "WRLD", 1)] = SubrecordSchema.ByteArray;

        // DNAM - WRLD (8 bytes)
        schemas[new SchemaKey("DNAM", "WRLD", 8)] = new SubrecordSchema(F.Float("Value1"), F.Float("Value2"))
        {
            Description = "World Default Land Height/Water Height"
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

        // NAM0/NAM9 - Worldspace Bounds (8 bytes)
        schemas[new SchemaKey("NAM0", "WRLD", 8)] = new SubrecordSchema(F.Float("X"), F.Float("Y"))
        {
            Description = "Worldspace Bounds Min"
        };
        schemas[new SchemaKey("NAM9", "WRLD", 8)] = new SubrecordSchema(F.Float("X"), F.Float("Y"))
        {
            Description = "Worldspace Bounds Max"
        };

        // ONAM - Worldspace persistent cell list (array of FormIDs)
        schemas[new SchemaKey("ONAM", "WRLD")] = SubrecordSchema.FormIdArray;

        // OFST - Worldspace offset table (array of uint32)
        schemas[new SchemaKey("OFST", "WRLD")] = SubrecordSchema.FormIdArray;

        // ========================================================================
        // REGION SCHEMAS (REGN)
        // ========================================================================

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
            ExpectedSize = -1,
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
            ExpectedSize = -1,
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
            ExpectedSize = -1,
            Description = "Region Objects Array"
        };

        // ========================================================================
        // WEATHER SCHEMAS (WTHR)
        // ========================================================================

        // FNAM - Weather Fog Distance (24 bytes = 6 floats)
        schemas[new SchemaKey("FNAM", "WTHR", 24)] = SubrecordSchema.FloatArray;

        // PNAM - Weather Cloud Colors (96 bytes)
        schemas[new SchemaKey("PNAM", "WTHR", 96)] = SubrecordSchema.FormIdArray;

        // NAM0 - Weather Colors (240 bytes)
        schemas[new SchemaKey("NAM0", "WTHR", 240)] = SubrecordSchema.FormIdArray;

        // INAM - Weather Image Spaces (304 bytes = 76 floats)
        schemas[new SchemaKey("INAM", "WTHR", 304)] = SubrecordSchema.FloatArray;

        // WLST - Weather Types (12 bytes per entry)
        schemas[new SchemaKey("WLST")] = new SubrecordSchema(
            F.FormId("Weather"),
            F.Int32("Chance"),
            F.FormId("Global"))
        {
            ExpectedSize = -1,
            Description = "Weather Types Array"
        };

        // SNAM - WTHR (8 bytes)
        schemas[new SchemaKey("SNAM", "WTHR", 8)] = new SubrecordSchema(F.FormId("Sound"), F.UInt32("Type"))
        {
            Description = "Weather Sound"
        };

        // IAD - WTHR (4 bytes)
        schemas[new SchemaKey("IAD", "WTHR", 4)] = SubrecordSchema.Simple4Byte("Image Adapter Float");

        schemas[new SchemaKey("DATA", "WTHR", 15)] = SubrecordSchema.ByteArray;

        // ========================================================================
        // WATER SCHEMAS (WATR)
        // ========================================================================

        // DNAM - WATR (196 bytes)
        schemas[new SchemaKey("DNAM", "WATR", 196)] = new SubrecordSchema(
            F.Float("Unknown1"), F.Float("Unknown2"), F.Float("Unknown3"), F.Float("Unknown4"),
            F.Float("SunPower"), F.Float("ReflectivityAmount"), F.Float("FresnelAmount"),
            F.Padding(4),
            F.Float("FogNear"), F.Float("FogFar"),
            F.UInt32("ShallowColor"), F.UInt32("DeepColor"), F.UInt32("ReflectionColor"),
            F.Padding(4),
            F.Float("RainForce"), F.Float("RainVelocity"), F.Float("RainFalloff"), F.Float("RainDampner"),
            F.Float("DisplacementStartingSize"), F.Float("DisplacementForce"), F.Float("DisplacementVelocity"),
            F.Float("DisplacementFalloff"), F.Float("DisplacementDampner"), F.Float("RainStartingSize"),
            F.Float("NoiseScale"), F.Float("NoiseLayer1WindDir"), F.Float("NoiseLayer2WindDir"),
            F.Float("NoiseLayer3WindDir"), F.Float("NoiseLayer1WindSpeed"), F.Float("NoiseLayer2WindSpeed"),
            F.Float("NoiseLayer3WindSpeed"), F.Float("DepthFalloffStart"), F.Float("DepthFalloffEnd"),
            F.Float("AboveWaterFogAmount"), F.Float("NormalsUVScale"), F.Float("UnderWaterFogAmount"),
            F.Float("UnderWaterFogNear"), F.Float("UnderWaterFogFar"), F.Float("DistortionAmount"),
            F.Float("Shininess"), F.Float("ReflectionHDRMult"), F.Float("LightRadius"), F.Float("LightBrightness"),
            F.Float("NoiseLayer1UVScale"), F.Float("NoiseLayer2UVScale"), F.Float("NoiseLayer3UVScale"),
            F.Float("NoiseLayer1AmpScale"), F.Float("NoiseLayer2AmpScale"), F.Float("NoiseLayer3AmpScale"))
        {
            Description = "Water Visual Data"
        };

        // GNAM - WATR Related Waters (12 bytes)
        schemas[new SchemaKey("GNAM", "WATR", 12)] = new SubrecordSchema(
            F.FormId("Daytime"),
            F.FormId("Nighttime"),
            F.FormId("Underwater"))
        {
            Description = "Water Related Waters"
        };

        schemas[new SchemaKey("SNAM", "WATR", 4)] = SubrecordSchema.Simple4Byte("Sound FormID");
        schemas[new SchemaKey("DATA", "WATR", 2)] = new SubrecordSchema(F.UInt16("Damage"))
        {
            Description = "Water Damage"
        };

        // ========================================================================
        // DOBJ DATA (DefaultObjectManager)
        // ========================================================================

        schemas[new SchemaKey("DATA", "DOBJ", 136)] = SubrecordSchema.FormIdArray;

        // ========================================================================
        // LIGHTING TEMPLATE (LGTM)
        // ========================================================================

        schemas[new SchemaKey("DATA", "LGTM", 40)] = new SubrecordSchema(
            F.UInt32("AmbientColor"),
            F.UInt32("DirectionalColor"),
            F.UInt32("FogColor"),
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

        // ========================================================================
        // ENCOUNTER ZONE (ECZN)
        // ========================================================================

        schemas[new SchemaKey("DATA", "ECZN", 8)] = new SubrecordSchema(
            F.FormId("Owner"),
            F.Int8("Rank"),
            F.Int8("MinimumLevel"),
            F.UInt8("Flags"),
            F.Padding(1))
        {
            Description = "Encounter Zone Data"
        };

        // ========================================================================
        // GRASS SCHEMAS (GRAS)
        // ========================================================================

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

        // ========================================================================
        // TREE SCHEMAS
        // ========================================================================

        schemas[new SchemaKey("CNAM", "TREE", 32)] = new SubrecordSchema(
            F.Float("LeafCurvature"), F.Float("MinLeafAngle"), F.Float("MaxLeafAngle"),
            F.Float("BranchDimmingValue"), F.Float("LeafDimmingValue"),
            F.Float("ShadowRadius"), F.Float("RockSpeed"),
            F.Float("RustleSpeed"))
        {
            Description = "Tree Data"
        };

        schemas[new SchemaKey("BNAM", "TREE", 8)] = new SubrecordSchema(
            F.Float("Width"),
            F.Float("Height"))
        {
            Description = "Billboard Dimensions"
        };

        schemas[new SchemaKey("SNAM", "TREE", 4)] = SubrecordSchema.Simple4Byte("Sound FormID");
        schemas[new SchemaKey("SNAM", "TREE", 20)] = SubrecordSchema.ByteArray;

        // ========================================================================
        // STATIC COLLECTION (SCOL)
        // ========================================================================

        schemas[new SchemaKey("ONAM", "SCOL", 4)] = SubrecordSchema.Simple4Byte("Static Object FormID");
        schemas[new SchemaKey("DATA", "SCOL")] = SubrecordSchema.FloatArray;

        // ========================================================================
        // LOAD SCREEN TYPE (LSCT)
        // ========================================================================

        schemas[new SchemaKey("DATA", "LSCT", 88)] = new SubrecordSchema(
            F.UInt32("Type"),
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
            F.UInt32("Unknown1"),
            F.UInt32("Unknown2"),
            F.UInt32("Unknown3"),
            F.UInt32("Unknown4"),
            F.UInt32("Unknown5"),
            F.UInt32("Font2"),
            F.Float("FontColor2R"),
            F.Float("FontColor2G"),
            F.Float("FontColor2B"),
            F.UInt32("Unknown6"),
            F.UInt32("Stats"))
        {
            Description = "Load Screen Type Data"
        };

        // ========================================================================
        // MISC RECORD-SPECIFIC SCHEMAS
        // ========================================================================

        schemas[new SchemaKey("OBND", null, 12)] = new SubrecordSchema(
            F.Int16("X1"), F.Int16("Y1"), F.Int16("Z1"),
            F.Int16("X2"), F.Int16("Y2"), F.Int16("Z2"))
        {
            Description = "Object Bounds"
        };

        // DODT - Decal Data (36 bytes)
        schemas[new SchemaKey("DODT", null, 36)] = new SubrecordSchema(
            F.Float("MinWidth"), F.Float("MaxWidth"),
            F.Float("MinHeight"), F.Float("MaxHeight"),
            F.Float("Depth"), F.Float("Shininess"), F.Float("ParallaxScale"),
            F.UInt8("Passes"), F.UInt8("Flags"), F.Padding(2),
            F.ColorArgb("Color"))
        {
            Description = "Decal Data"
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

        // DNAM - IMGS (floats)
        schemas[new SchemaKey("DNAM", "IMGS")] = SubrecordSchema.FloatArray;

        // DNAM - PWAT (8 bytes)
        schemas[new SchemaKey("DNAM", "PWAT", 8)] = new SubrecordSchema(F.Float("Value1"), F.Float("Value2"))
        {
            Description = "Placeable Water Data"
        };

        schemas[new SchemaKey("DNAM", "VTYP", 1)] = SubrecordSchema.ByteArray;

        // DATA - ANIO (4 bytes)
        schemas[new SchemaKey("DATA", "ANIO", 4)] = new SubrecordSchema(F.FormId("Animation"))
        {
            Description = "Animation FormID"
        };

        // DATA - GMST (4 bytes)
        schemas[new SchemaKey("DATA", "GMST", 4)] = SubrecordSchema.Simple4Byte();
        schemas[new SchemaKey("DATA", "GMST")] = SubrecordSchema.ByteArray;

        // DATA - CDCK (4 bytes)
        schemas[new SchemaKey("DATA", "CDCK", 4)] = new SubrecordSchema(F.UInt32("Count"))
        {
            Description = "Caravan Deck Count"
        };

        // DATA - CCRD (4 bytes)
        schemas[new SchemaKey("DATA", "CCRD", 4)] = new SubrecordSchema(F.UInt32("Value"))
        {
            Description = "Caravan Card Value"
        };

        // DATA - REPU (4 bytes)
        schemas[new SchemaKey("DATA", "REPU", 4)] = new SubrecordSchema(F.Float("Value"))
        {
            Description = "Reputation Value"
        };

        // DATA - CMNY (4 bytes)
        schemas[new SchemaKey("DATA", "CMNY", 4)] = new SubrecordSchema(F.UInt32("AbsoluteValue"))
        {
            Description = "Caravan Money Value"
        };

        // DATA - CSNO (56 bytes)
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

        // Survival mode stage data (DEHY, HUNG, RADS, SLPD)
        schemas[new SchemaKey("DATA", "DEHY", 8)] = new SubrecordSchema(
            F.UInt32("TriggerThreshold"),
            F.FormId("ActorEffect"))
        {
            Description = "Dehydration Stage Data"
        };

        schemas[new SchemaKey("DATA", "HUNG", 8)] = new SubrecordSchema(
            F.UInt32("TriggerThreshold"),
            F.FormId("ActorEffect"))
        {
            Description = "Hunger Stage Data"
        };

        schemas[new SchemaKey("DATA", "RADS", 8)] = new SubrecordSchema(
            F.UInt32("TriggerThreshold"),
            F.FormId("ActorEffect"))
        {
            Description = "Radiation Stage Data"
        };

        schemas[new SchemaKey("DATA", "SLPD", 8)] = new SubrecordSchema(
            F.UInt32("TriggerThreshold"),
            F.FormId("ActorEffect"))
        {
            Description = "Sleep Deprivation Stage Data"
        };

        // DATA - HAIR/HDPT/EYES (1 byte)
        schemas[new SchemaKey("DATA", "HAIR", 1)] = SubrecordSchema.ByteArray;
        schemas[new SchemaKey("DATA", "HDPT", 1)] = SubrecordSchema.ByteArray;
        schemas[new SchemaKey("DATA", "EYES", 1)] = SubrecordSchema.ByteArray;
        schemas[new SchemaKey("DATA", "MSTT", 1)] = SubrecordSchema.ByteArray;

        // MSET (Media Set) schemas
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

        // ALOC (Media Location Controller) schemas
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

        // Other misc schemas
        schemas[new SchemaKey("SNAM", "ACTI", 4)] = SubrecordSchema.Simple4Byte("Sound FormID");
        schemas[new SchemaKey("SNAM", "ASPC", 4)] = SubrecordSchema.Simple4Byte("Sound FormID");
        schemas[new SchemaKey("SNAM", "TACT", 4)] = SubrecordSchema.Simple4Byte("Sound FormID");
        schemas[new SchemaKey("SNAM", "DOOR", 4)] = SubrecordSchema.Simple4Byte("Sound FormID");
        schemas[new SchemaKey("SNAM", "MSTT", 4)] = SubrecordSchema.Simple4Byte("Sound FormID");
    }
}
