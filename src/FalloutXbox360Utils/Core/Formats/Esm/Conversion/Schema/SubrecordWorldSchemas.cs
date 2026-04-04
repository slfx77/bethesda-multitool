using F = FalloutXbox360Utils.Core.Formats.Esm.Conversion.Schema.SubrecordField;

namespace FalloutXbox360Utils.Core.Formats.Esm.Conversion.Schema;

/// <summary>
///     Reference-related schemas (REFR, ACHR, ACRE).
/// </summary>
internal static class SubrecordWorldSchemas
{
    /// <summary>
    ///     Register reference-related schemas (REFR, ACHR, ACRE).
    /// </summary>
    internal static void Register(Dictionary<SubrecordSchemaRegistry.SchemaKey, SubrecordSchema> schemas)
    {
        // ========================================================================
        // REFERENCE SCHEMAS (REFR, ACHR, ACRE)
        // ========================================================================

        // DATA - REFR/ACHR/ACRE (24 bytes = PosRot)
        schemas[new SubrecordSchemaRegistry.SchemaKey("DATA", "REFR", 24)] = new SubrecordSchema(F.PosRot("PosRot"));
        schemas[new SubrecordSchemaRegistry.SchemaKey("DATA", "ACHR", 24)] = new SubrecordSchema(F.PosRot("PosRot"));
        schemas[new SubrecordSchemaRegistry.SchemaKey("DATA", "ACRE", 24)] = new SubrecordSchema(F.PosRot("PosRot"));
        schemas[new SubrecordSchemaRegistry.SchemaKey("DATA", "PGRE", 24)] = SubrecordSchema.FloatArray;

        // XTEL - Door Teleport (32 bytes) — PDB: DoorTeleportData
        // pLinkedDoor(FormID) + position(NiPoint3) + rotation(NiPoint3) + cFlags(uchar) + pad(3)
        // Disasm confirms: Save uses stb for cFlags (single byte), buffer memset pads bytes 29-31 to 0.
        schemas[new SubrecordSchemaRegistry.SchemaKey("XTEL", null, 32)] = new SubrecordSchema(
            F.FormId("DestinationDoor"),
            F.Float("PosX"), F.Float("PosY"), F.Float("PosZ"),
            F.Float("RotX"), F.Float("RotY"), F.Float("RotZ"),
            F.UInt8("Flags"),
            F.Padding(3))
        {
            Description = "Door Teleport Destination (DoorTeleportData)"
        };

        // XLKR - Linked Reference (8 bytes)
        schemas[new SubrecordSchemaRegistry.SchemaKey("XLKR", null, 8)] = new SubrecordSchema(
            F.FormId("Keyword"),
            F.FormId("Reference"))
        {
            Description = "Linked Reference"
        };

        // XAPR - Activation Parent (8 bytes)
        schemas[new SubrecordSchemaRegistry.SchemaKey("XAPR", null, 8)] = new SubrecordSchema(
            F.FormId("Reference"),
            F.Float("Delay"))
        {
            Description = "Activation Parent"
        };

        // XLOC - Lock Information (20 bytes)
        // PDB: REFR_LOCK { cBaseLevel(uint8,+0), pKey(FormID,+4), cFlags(char,+8),
        //   uiNumTries(uint32,+12), uiTimesUnlocked(uint32,+16) }
        // Endian() swaps pKey, uiNumTries, uiTimesUnlocked. Single bytes don't swap.
        schemas[new SubrecordSchemaRegistry.SchemaKey("XLOC", null, 20)] = new SubrecordSchema(
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
        schemas[new SubrecordSchemaRegistry.SchemaKey("XLOD", null, 12)] = new SubrecordSchema(F.Vec3("LOD"))
        {
            Description = "LOD Data"
        };

        // XESP - Enable Parent (8 bytes)
        schemas[new SubrecordSchemaRegistry.SchemaKey("XESP", null, 8)] = new SubrecordSchema(
            F.FormId("ParentRef"),
            F.UInt8("Flags"),
            F.Padding(3))
        {
            Description = "Enable Parent"
        };

        // XCLP - Linked Reference Color (8 bytes)
        schemas[new SubrecordSchemaRegistry.SchemaKey("XCLP", null, 8)] = new SubrecordSchema(
            F.UInt32("LinkStartColor"),
            F.UInt32("LinkEndColor"))
        {
            Description = "Linked Reference Color"
        };

        // XNDP - Navigation Door Portal (8 bytes)
        schemas[new SubrecordSchemaRegistry.SchemaKey("XNDP", null, 8)] = new SubrecordSchema(
            F.FormId("Navmesh"),
            F.UInt16("TriangleIndex"),
            F.Padding(2))
        {
            Description = "Navigation Door Portal"
        };

        schemas[new SubrecordSchemaRegistry.SchemaKey("CNAM", "REFR", 4)] =
            SubrecordSchema.Simple4Byte("Audio Location");
        schemas[new SubrecordSchemaRegistry.SchemaKey("NNAM", "REFR", 4)] =
            SubrecordSchema.Simple4Byte("Linked Ref Keyword FormID");

        // XPWR - Water Reflection (8 bytes)
        schemas[new SubrecordSchemaRegistry.SchemaKey("XPWR")] = new SubrecordSchema(
            F.FormId("Reference"),
            F.UInt32("Type"))
        {
            Description = "Water Reflection/Refraction"
        };

        // XMBO - Bound Half Extents (12 bytes)
        schemas[new SubrecordSchemaRegistry.SchemaKey("XMBO")] = new SubrecordSchema(F.Vec3("Bounds"))
        {
            Description = "Bound Half Extents"
        };

        // XPRM - Primitive (32 bytes)
        schemas[new SubrecordSchemaRegistry.SchemaKey("XPRM", null, 32)] = new SubrecordSchema(
            F.Vec3("Bounds"),
            F.Vec3("Colors"),
            F.UInt32("Unknown"),
            F.UInt32("Type"))
        {
            Description = "Primitive Data"
        };

        // XPOD - Portal Data (8 bytes)
        schemas[new SubrecordSchemaRegistry.SchemaKey("XPOD", null, 8)] = new SubrecordSchema(
            F.FormId("Origin"),
            F.FormId("Destination"))
        {
            Description = "Portal Data"
        };

        // XRGB - Biped Rotation (12 bytes)
        schemas[new SubrecordSchemaRegistry.SchemaKey("XRGB")] = new SubrecordSchema(F.Vec3("Rotation"))
        {
            Description = "Biped Rotation"
        };

        // XRDO - Radio Data (16 bytes)
        schemas[new SubrecordSchemaRegistry.SchemaKey("XRDO", null, 16)] = new SubrecordSchema(
            F.Float("Range"),
            F.UInt32("Type"),
            F.Float("StaticPercentage"),
            F.FormId("PositionRef"))
        {
            Description = "Radio Data"
        };

        // XOCP - Occlusion Plane (36 bytes = 9 floats)
        schemas[new SubrecordSchemaRegistry.SchemaKey("XOCP", null, 36)] = SubrecordSchema.FloatArray;

        // XORD - Linked Occlusion Planes (16 bytes = 4 FormIDs)
        schemas[new SubrecordSchemaRegistry.SchemaKey("XORD", null, 16)] = new SubrecordSchema(
            F.FormId("Right"),
            F.FormId("Left"),
            F.FormId("Bottom"),
            F.FormId("Top"))
        {
            Description = "Linked Occlusion Planes"
        };
    }
}
