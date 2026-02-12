using F = FalloutXbox360Utils.Core.Formats.Esm.Conversion.Schema.SubrecordField;

namespace FalloutXbox360Utils.Core.Formats.Esm.Conversion.Schema;

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

        // TRDT - INFO Response Data (24 bytes) — PDB: RESPONSE_DATA
        // Disassembly confirms swap32 at offsets 0, 4, 8, 16
        schemas[new SchemaKey("TRDT", null, 24)] = new SubrecordSchema(
            F.UInt32("EmotionType"),
            F.Int32("EmotionValue"),
            F.FormId("ConversationTopic"),
            F.UInt8("ResponseNumber"),
            F.Padding(3),
            F.FormId("Sound"),
            F.UInt8("UseEmotionAnim"),
            F.Padding(3))
        {
            Description = "INFO Response Data (RESPONSE_DATA)"
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
        // DNAM - TERM (4 bytes) - PDB: TERMINAL_DATA (4 individual bytes: cDifficulty, cFlags, cServerType, cUnused)
        schemas[new SchemaKey("DNAM", "TERM", 4)] = SubrecordSchema.ByteArray;
        schemas[new SchemaKey("SNAM", "TERM", 4)] = SubrecordSchema.Simple4Byte("Sound FormID");

        // ========================================================================
        // MESSAGE SCHEMAS (MESG)
        // ========================================================================

        schemas[new SchemaKey("DNAM", "MESG", 4)] = SubrecordSchema.Simple4Byte("Message Flags");

        // ========================================================================
        // SCRIPT SCHEMAS (SCPT)
        // ========================================================================

        // SCHR - Script Header (20 bytes) - matches PDB SCRIPT_HEADER struct
        schemas[new SchemaKey("SCHR", null, 20)] = new SubrecordSchema(
            F.UInt32("VariableCount"),
            F.UInt32("RefObjectCount"),
            F.UInt32("CompiledSize"),
            F.UInt32("LastVariableId"),
            F.UInt8("IsQuestScript"),
            F.UInt8("IsMagicEffectScript"),
            F.UInt8("IsCompiled"),
            F.Padding(1))
        {
            Description = "Script Header (SCRIPT_HEADER)"
        };

        // SLSD - Script Local Variable Data (24 bytes) — PDB: SCRIPT_LOCAL
        // Disassembly confirms: swap32 at offset 0 (uiID), swap64 at offset 8 (fValue double)
        // Layout: uiID(4) + padding(4) + fValue(double,8) + bIsInteger(bool,1) + padding(7)
        schemas[new SchemaKey("SLSD", null, 24)] = new SubrecordSchema(
            F.UInt32("Index"),
            F.Padding(4),
            F.Double("Value"),
            F.UInt8("IsInteger"),
            F.Padding(7))
        {
            Description = "Script Local Variable Data (SCRIPT_LOCAL)"
        };

        schemas[new SchemaKey("SCDA")] = SubrecordSchema.ByteArray;

        // ========================================================================
        // CONDITION SCHEMAS
        // ========================================================================

        // CTDA - Condition (28 bytes) — PDB: CONDITION_ITEM_DATA
        // Disassembly confirms: swap32 at offsets 4, 20, 24; FUNCTION_DATA::Endian at offset 8
        // FUNCTION_DATA::Endian: swap16 at +0, swap32 at +4, swap32 at +8
        // Bytes 10-11 are padding within FUNCTION_DATA (between iFunction and pParam)
        schemas[new SchemaKey("CTDA", null, 28)] = new SubrecordSchema(
            F.UInt8("Type"),
            F.Padding(3),
            F.Float("ComparisonValue"),
            F.UInt16("FunctionIndex"),
            F.Padding(2),
            F.FormId("Parameter1"),
            F.UInt32("Parameter2"),
            F.UInt32("RunOn"),
            F.FormId("Reference"))
        {
            Description = "Condition Data (CONDITION_ITEM_DATA)"
        };

        // ========================================================================
        // CHALLENGE SCHEMAS (CHAL)
        // ========================================================================

        // DATA - CHAL (24 bytes) — PDB: CHALLENGE_DATA
        schemas[new SchemaKey("DATA", "CHAL", 24)] = new SubrecordSchema(
            F.Int32("ChallengeType"),
            F.Int32("Threshold"),
            F.UInt16("Flags"),
            F.Padding(2),
            F.Int32("Interval"),
            F.UInt16("SpecialDataOne"),
            F.UInt16("SpecialDataTwo"),
            F.UInt16("SpecialDataThree"),
            F.Padding(2));

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

        // PKDD - Package Dialogue Data (24 bytes) - PDB: PACK_DIALOGUE_DATA
        // Offsets 8-10 are individual bools, NOT a uint32. Offset 12 is a float, NOT padding.
        schemas[new SchemaKey("PKDD", null, 24)] = new SubrecordSchema(
            F.Float("FOV"),
            F.FormId("TopicID"),
            F.Padding(4), // 3 bools (NOHeadtracking, DoNotControlTarget, SpeakerMoveTalk) + 1 pad
            F.Float("DistanceStartTalking"),
            F.Padding(4), // 1 bool (SayTo) + 3 pad
            F.UInt32("TriggerType"))
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

        // PKW3 - Package Use Weapon Data (24 bytes) - PDB: PACK_USE_WEAPON_DATA_PKW3
        // Offsets 0-5 are individual bools, NOT a uint32+bytes. Offset 20 is a uint32, NOT padding.
        schemas[new SchemaKey("PKW3", null, 24)] = new SubrecordSchema(
            F.Padding(6), // 6 bools (AlwaysHit, DoNoDamage, Crouch, HoldFire, VolleyFire, RepeatFire)
            F.UInt16("BurstCount"),
            F.UInt16("VolleyShotsMin"),
            F.UInt16("VolleyShotsMax"),
            F.Float("VolleyWaitMin"),
            F.Float("VolleyWaitMax"),
            F.UInt32("Weapon"))
        {
            Description = "Package Use Weapon Data"
        };

        schemas[new SchemaKey("CNAM", "PACK", 4)] = SubrecordSchema.Simple4Byte("Combat Style FormID");
        // PKPT - PDB: PACK_PATROL_DATA (2 individual bools: bRepeatable, bStartingLocationAtLinkedRef)
        schemas[new SchemaKey("PKPT", "PACK", 2)] = SubrecordSchema.ByteArray;
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
