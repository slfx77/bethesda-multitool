using F = FalloutXbox360Utils.Core.Converters.Esm.Schema.SubrecordField;

namespace FalloutXbox360Utils.Core.Converters.Esm.Schema;

public static partial class SubrecordSchemaRegistry
{
    /// <summary>
    ///     Register dialogue-related schemas (DIAL, INFO, QUST, NOTE, BOOK, TERM, MESG).
    /// </summary>
    private static void RegisterDialogueSchemas(Dictionary<SchemaKey, SubrecordSchema> schemas)
    {
        // ========================================================================
        // QUEST SCHEMAS (QUST)
        // ========================================================================

        // DATA - QUST (8 bytes)
        schemas[new SchemaKey("DATA", "QUST", 8)] = new SubrecordSchema(
            F.UInt8("Flags"),
            F.UInt8("Priority"),
            F.Padding(2),
            F.Float("QuestDelay"))
        {
            Description = "Quest Data"
        };

        // QSTA - Quest Target (8 bytes)
        schemas[new SchemaKey("QSTA", null, 8)] = new SubrecordSchema(
            F.FormId("Target"),
            F.UInt8("Flags"),
            F.Padding(3))
        {
            Description = "Quest Target"
        };

        // INDX in QUST is already little-endian on Xbox 360 - DO NOT SWAP!
        schemas[new SchemaKey("INDX", "QUST", 2)] = new SubrecordSchema(F.UInt16LittleEndian("Quest Index"));
        schemas[new SchemaKey("QSDT", "QUST", 1)] = SubrecordSchema.ByteArray;

        // ========================================================================
        // INFO SCHEMAS (Dialog Response)
        // ========================================================================

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

        // DATA - INFO (4 bytes)
        schemas[new SchemaKey("DATA", "INFO", 4)] = SubrecordSchema.ByteArray;

        schemas[new SchemaKey("DNAM", "INFO", 4)] = SubrecordSchema.Simple4Byte("Response Type");
        schemas[new SchemaKey("SNAM", "INFO", 4)] = SubrecordSchema.Simple4Byte("Speaker FormID");
        schemas[new SchemaKey("NEXT", "INFO", 0)] = SubrecordSchema.ByteArray;

        // ========================================================================
        // DIALOGUE TOPIC SCHEMAS (DIAL)
        // ========================================================================

        // DATA - DIAL (2 bytes) - Dialog Topic Data (2 UInt8 flags, no swap needed)
        schemas[new SchemaKey("DATA", "DIAL", 2)] = SubrecordSchema.ByteArray;

        // ========================================================================
        // NOTE SCHEMAS
        // ========================================================================

        // DATA - NOTE (1 byte) - Note Type
        schemas[new SchemaKey("DATA", "NOTE", 1)] = SubrecordSchema.ByteArray;
        schemas[new SchemaKey("SNAM", "NOTE", 4)] = SubrecordSchema.Simple4Byte("Sound FormID");
        schemas[new SchemaKey("ONAM", "NOTE", 4)] = SubrecordSchema.Simple4Byte("Note Object FormID");

        // ========================================================================
        // TERMINAL SCHEMAS (TERM)
        // ========================================================================

        schemas[new SchemaKey("ANAM", "TERM", 1)] = SubrecordSchema.ByteArray;
        schemas[new SchemaKey("DNAM", "TERM", 4)] = SubrecordSchema.Simple4Byte();
        schemas[new SchemaKey("SNAM", "TERM", 4)] = SubrecordSchema.Simple4Byte("Sound FormID");

        // ========================================================================
        // MESSAGE SCHEMAS (MESG)
        // ========================================================================

        schemas[new SchemaKey("DNAM", "MESG", 4)] = SubrecordSchema.Simple4Byte("Message Flags");

        // ========================================================================
        // SCRIPT SCHEMAS (SCPT)
        // ========================================================================

        // SCHR - Script Header (20 bytes)
        schemas[new SchemaKey("SCHR", null, 20)] = new SubrecordSchema(
                F.Padding(4),
                F.UInt32("RefCount"),
                F.UInt32("CompiledSize"),
                F.UInt32("VariableCount"),
                F.Padding(4))
            {
                Description = "Script Header"
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

        schemas[new SchemaKey("SCDA")] = SubrecordSchema.ByteArray;

        // ========================================================================
        // CONDITION SCHEMAS
        // ========================================================================

        // CTDA - Condition (28 bytes)
        schemas[new SchemaKey("CTDA", null, 28)] = new SubrecordSchema(
            F.Padding(4),
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

        // ========================================================================
        // CHALLENGE SCHEMAS (CHAL)
        // ========================================================================

        // DATA - CHAL (24 bytes)
        schemas[new SchemaKey("DATA", "CHAL", 24)] = new SubrecordSchema(
            F.UInt32("Type"),
            F.UInt32("Threshold"),
            F.UInt16("Flags"),
            F.UInt16("Interval"),
            F.UInt32("Value1"),
            F.UInt16("Value2"),
            F.UInt16("Value3"),
            F.UInt16("Value4"),
            F.UInt16("Value5"));

        schemas[new SchemaKey("SNAM", "CHAL", 4)] = SubrecordSchema.Simple4Byte("Challenge Sound");

        // ========================================================================
        // CAMERA SCHEMAS (CAMS, CPTH)
        // ========================================================================

        // DATA - CAMS (40 bytes) - Camera Shot Data
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

        // ANAM - CPTH (8 bytes)
        schemas[new SchemaKey("ANAM", "CPTH", 8)] = new SubrecordSchema(
            F.FormId("Parent"),
            F.FormId("Previous"))
        {
            Description = "Camera Path Parents"
        };

        // DATA - CPTH (1 byte)
        schemas[new SchemaKey("DATA", "CPTH", 1)] = SubrecordSchema.ByteArray;
        schemas[new SchemaKey("SNAM", "CPTH", 4)] = SubrecordSchema.Simple4Byte("Camera Path Sound");

        // ========================================================================
        // PACKAGE SCHEMAS (PACK)
        // ========================================================================

        // PKDT - Package Data (12 bytes)
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

        // PTDT/PTD2 - Package Target (16 bytes)
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

        schemas[new SchemaKey("CNAM", "PACK", 4)] = SubrecordSchema.Simple4Byte("Combat Style FormID");
        schemas[new SchemaKey("PKPT", "PACK", 2)] = SubrecordSchema.Simple2Byte("Package PT");
        schemas[new SchemaKey("PKAM", "PACK", 0)] = SubrecordSchema.ByteArray;
        schemas[new SchemaKey("PKED", "PACK", 0)] = SubrecordSchema.ByteArray;
        schemas[new SchemaKey("POBA", "PACK", 0)] = SubrecordSchema.ByteArray;
        schemas[new SchemaKey("POCA", "PACK", 0)] = SubrecordSchema.ByteArray;
        schemas[new SchemaKey("POEA", "PACK", 0)] = SubrecordSchema.ByteArray;
        schemas[new SchemaKey("PUID", "PACK", 0)] = SubrecordSchema.ByteArray;

        // ========================================================================
        // IDLE ANIMATION SCHEMAS (IDLE, IDLM)
        // ========================================================================

        // ANAM - IDLE (8 bytes)
        schemas[new SchemaKey("ANAM", "IDLE", 8)] = new SubrecordSchema(
            F.FormId("Parent"),
            F.FormId("Previous"))
        {
            Description = "Idle Animation Parents"
        };

        // IDLA - Idle Marker Animations (array of FormIDs)
        schemas[new SchemaKey("IDLA")] = SubrecordSchema.FormIdArray;

        schemas[new SchemaKey("IDLC", null, 1)] = SubrecordSchema.ByteArray;
        schemas[new SchemaKey("IDLF", null, 1)] = SubrecordSchema.ByteArray;
    }
}
