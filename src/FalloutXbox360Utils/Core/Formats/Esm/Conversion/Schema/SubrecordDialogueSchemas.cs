using F = FalloutXbox360Utils.Core.Formats.Esm.Conversion.Schema.SubrecordField;

namespace FalloutXbox360Utils.Core.Formats.Esm.Conversion.Schema;

/// <summary>
///     Dialogue-related schemas (DIAL, INFO, QUST, NOTE, BOOK, TERM, MESG).
/// </summary>
internal static class SubrecordDialogueSchemas
{
    /// <summary>
    ///     Register dialogue-related schemas (DIAL, INFO, QUST, NOTE, BOOK, TERM, MESG).
    /// </summary>
    internal static void Register(Dictionary<SubrecordSchemaRegistry.SchemaKey, SubrecordSchema> schemas)
    {
        // ========================================================================
        // QUEST SCHEMAS (QUST)
        // ========================================================================

        // DATA - QUST (8 bytes)
        schemas[new SubrecordSchemaRegistry.SchemaKey("DATA", "QUST", 8)] = new SubrecordSchema(
            F.UInt8("Flags"),
            F.UInt8("Priority"),
            F.Padding(2),
            F.Float("QuestDelay"))
        {
            Description = "Quest Data"
        };

        // QSTA - Quest Target (8 bytes)
        schemas[new SubrecordSchemaRegistry.SchemaKey("QSTA", null, 8)] = new SubrecordSchema(
            F.FormId("Target"),
            F.UInt8("Flags"),
            F.Padding(3))
        {
            Description = "Quest Target"
        };

        // INDX in QUST is already little-endian on Xbox 360 - DO NOT SWAP!
        schemas[new SubrecordSchemaRegistry.SchemaKey("INDX", "QUST", 2)] =
            new SubrecordSchema(F.UInt16LittleEndian("Quest Index"));
        schemas[new SubrecordSchemaRegistry.SchemaKey("QSDT", "QUST", 1)] =
            new SubrecordSchema(F.UInt8("Flags"))
            {
                Description = "Quest stage flags"
            };

        // ========================================================================
        // INFO SCHEMAS (Dialog Response)
        // ========================================================================

        // TRDT - INFO Response Data (24 bytes) — PDB: RESPONSE_DATA
        // Disassembly confirms swap32 at offsets 0, 4, 8, 16
        schemas[new SubrecordSchemaRegistry.SchemaKey("TRDT", null, 24)] = new SubrecordSchema(
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

        // DATA - INFO (4 bytes): dialogue type + next speaker + flags + extended flags.
        // These bytes are already modeled by DialogueRecord.InfoFlags/InfoFlagsExt and
        // emitted by InfoEncoder; keep the schema named so coverage does not treat the
        // behavior flags as an opaque blob.
        schemas[new SubrecordSchemaRegistry.SchemaKey("DATA", "INFO", 4)] = new SubrecordSchema(
            F.UInt8("DialogueType"),
            F.UInt8("NextSpeaker"),
            F.UInt8("Flags"),
            F.UInt8("Flags2"))
        {
            Description = "INFO dialogue flags"
        };

        schemas[new SubrecordSchemaRegistry.SchemaKey("DNAM", "INFO", 4)] =
            SubrecordSchema.Simple4Byte("Response Type");
        schemas[new SubrecordSchemaRegistry.SchemaKey("SNAM", "INFO", 4)] =
            SubrecordSchema.Simple4Byte("Speaker FormID");
        schemas[new SubrecordSchemaRegistry.SchemaKey("TPIC", "INFO", 4)] =
            SubrecordSchema.Simple4Byte("Topic FormID");
        schemas[new SubrecordSchemaRegistry.SchemaKey("NEXT", "INFO", 0)] = SubrecordSchema.Empty;

        // ========================================================================
        // DIALOGUE TOPIC SCHEMAS (DIAL)
        // ========================================================================

        // DATA - DIAL (2 bytes) - Dialog Topic Data (2 UInt8 flags, no swap needed)
        schemas[new SubrecordSchemaRegistry.SchemaKey("DATA", "DIAL", 2)] = new SubrecordSchema(
            F.UInt8("TopicType"),
            F.UInt8("Flags"))
        {
            Description = "Dialog topic data"
        };

        // PNAM - DIAL (4 bytes) - Topic Priority (float)
        schemas[new SubrecordSchemaRegistry.SchemaKey("PNAM", "DIAL", 4)] = new SubrecordSchema(F.Float("Priority"))
        {
            Description = "Topic Priority"
        };

        // ========================================================================
        // NOTE SCHEMAS
        // ========================================================================

        // DATA - NOTE (1 byte) - Note Type
        schemas[new SubrecordSchemaRegistry.SchemaKey("DATA", "NOTE", 1)] =
            new SubrecordSchema(F.UInt8("NoteType"))
            {
                Description = "Note type"
            };
        schemas[new SubrecordSchemaRegistry.SchemaKey("SNAM", "NOTE", 4)] = SubrecordSchema.Simple4Byte("Sound FormID");
        schemas[new SubrecordSchemaRegistry.SchemaKey("ONAM", "NOTE", 4)] =
            SubrecordSchema.Simple4Byte("Note Object FormID");

        // ========================================================================
        // TERMINAL SCHEMAS (TERM)
        // ========================================================================

        schemas[new SubrecordSchemaRegistry.SchemaKey("ANAM", "TERM", 1)] =
            new SubrecordSchema(F.UInt8("MenuItemType"))
            {
                Description = "Terminal menu item type"
            };
        // DNAM - TERM (4 bytes) - PDB: TERMINAL_DATA (4 individual bytes: cDifficulty, cFlags, cServerType, cUnused)
        schemas[new SubrecordSchemaRegistry.SchemaKey("DNAM", "TERM", 4)] = new SubrecordSchema(
            F.UInt8("Difficulty"),
            F.UInt8("Flags"),
            F.UInt8("ServerType"),
            F.Padding(1))
        {
            Description = "Terminal data"
        };
        schemas[new SubrecordSchemaRegistry.SchemaKey("SNAM", "TERM", 4)] = SubrecordSchema.Simple4Byte("Sound FormID");
        schemas[new SubrecordSchemaRegistry.SchemaKey("NEXT", "TERM", 0)] = SubrecordSchema.Empty;

        // ========================================================================
        // MESSAGE SCHEMAS (MESG)
        // ========================================================================

        schemas[new SubrecordSchemaRegistry.SchemaKey("DNAM", "MESG", 4)] =
            SubrecordSchema.Simple4Byte("Message Flags");

        // ========================================================================
        // SCRIPT SCHEMAS (SCPT)
        // ========================================================================

        // SCHR - Script Header (20 bytes) - canonical ESM/ESP serialization per fopdoc
        // (https://github.com/TES5Edit/fopdoc/blob/master/FalloutNV/Records/Subrecords/SCHR.md).
        //
        // The runtime SCRIPT_HEADER struct (PDB-documented in-memory layout) places
        // VariableCount at offset 0 and uiLastID at offset 12; the ESM serialized layout
        // is DIFFERENT: offset 0 is Unused, offset 12 is VariableCount, and offsets 16/18
        // are uint16 Type/Flags rather than three bool bytes + padding.
        //
        // Treating the runtime struct as if it were the ESM layout (which the codebase
        // did prior to 2026-05-28) made every emitted SCHR carry the runtime's uiLastID at
        // offset 12 — the PC engine reads that field as VariableCount, then refuses to
        // resolve any SCDA bytecode variable reference whose index ≥ uiLastID. Result:
        // thousands of `SCRIPTS: Variable ID NNNNNNNN not found. Try to recompile script 'UNKNOWN'`
        // errors in v53-xex44, and equivalent 'Variable ID 0x0E not found in '' scripts'
        // for every INFO result script. See memory/schr_runtime_vs_esm_layout.md.
        //
        // Type uint16 values: 0=Object, 1=Quest, 0x100=Effect.
        // Flags uint16 values: 0x0001 = Enabled.
        schemas[new SubrecordSchemaRegistry.SchemaKey("SCHR", null, 20)] = new SubrecordSchema(
            F.Padding(4),
            F.UInt32("RefCount"),
            F.UInt32("CompiledSize"),
            F.UInt32("VariableCount"),
            F.UInt16("Type"),
            F.UInt16("Flags"))
        {
            Description = "Script Header (canonical ESM SCHR per fopdoc)"
        };

        // SLSD - Script Local Variable Data (24 bytes) — PDB: SCRIPT_LOCAL
        // Disassembly confirms: swap32 at offset 0 (uiID), swap64 at offset 8 (fValue double)
        // Layout: uiID(4) + padding(4) + fValue(double,8) + bIsInteger(bool,1) + padding(7)
        schemas[new SubrecordSchemaRegistry.SchemaKey("SLSD", null, 24)] = new SubrecordSchema(
            F.UInt32("Index"),
            F.Padding(4),
            F.Double("Value"),
            F.UInt8("IsInteger"),
            F.Padding(7))
        {
            Description = "Script Local Variable Data (SCRIPT_LOCAL)"
        };

        schemas[new SubrecordSchemaRegistry.SchemaKey("SCDA")] = SubrecordSchema.ByteArray;

        // ========================================================================
        // CONDITION SCHEMAS
        // ========================================================================

        // CTDA - Condition (28 bytes) — PDB: CONDITION_ITEM_DATA
        // Disassembly confirms: swap32 at offsets 4, 20, 24; FUNCTION_DATA::Endian at offset 8
        // FUNCTION_DATA::Endian: swap16 at +0, swap32 at +4, swap32 at +8
        // Bytes 10-11 are padding within FUNCTION_DATA (between iFunction and pParam)
        schemas[new SubrecordSchemaRegistry.SchemaKey("CTDA", null, 28)] = new SubrecordSchema(
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
        schemas[new SubrecordSchemaRegistry.SchemaKey("DATA", "CHAL", 24)] = new SubrecordSchema(
            F.Int32("ChallengeType"),
            F.Int32("Threshold"),
            F.UInt16("Flags"),
            F.Padding(2),
            F.Int32("Interval"),
            F.UInt16("SpecialDataOne"),
            F.UInt16("SpecialDataTwo"),
            F.UInt16("SpecialDataThree"),
            F.Padding(2));

        schemas[new SubrecordSchemaRegistry.SchemaKey("SNAM", "CHAL", 4)] =
            SubrecordSchema.Simple4Byte("Challenge Sound");

        // ========================================================================
        // CAMERA SCHEMAS (CAMS, CPTH)
        // ========================================================================

        // DATA - CAMS (40 bytes) - Camera Shot Data
        schemas[new SubrecordSchemaRegistry.SchemaKey("DATA", "CAMS", 40)] = new SubrecordSchema(
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
        schemas[new SubrecordSchemaRegistry.SchemaKey("ANAM", "CPTH", 8)] = new SubrecordSchema(
            F.FormId("Parent"),
            F.FormId("Previous"))
        {
            Description = "Camera Path Parents"
        };

        // DATA - CPTH (1 byte)
        schemas[new SubrecordSchemaRegistry.SchemaKey("DATA", "CPTH", 1)] =
            new SubrecordSchema(F.UInt8("Flags"))
            {
                Description = "Camera path flags"
            };
        schemas[new SubrecordSchemaRegistry.SchemaKey("SNAM", "CPTH", 4)] =
            SubrecordSchema.Simple4Byte("Camera Path Sound");

        // ========================================================================
        // PACKAGE SCHEMAS (PACK)
        // ========================================================================

        // PKDT - Package Data (12 bytes, from PDB PACKAGE_DATA)
        schemas[new SubrecordSchemaRegistry.SchemaKey("PKDT", null, 12)] = new SubrecordSchema(
            F.UInt32("iPackFlags"),
            F.UInt8("cPackType"),
            F.UInt8("Unused"),
            F.UInt16("iFOBehaviorFlags"),
            F.UInt16("iPackageSpecificFlags"),
            F.UInt8("Unknown1"),
            F.UInt8("Unknown2"))
        {
            Description = "Package Data"
        };

        // PSDT - Package Schedule Data (8 bytes)
        schemas[new SubrecordSchemaRegistry.SchemaKey("PSDT", null, 8)] = new SubrecordSchema(
            F.UInt8("Month"),
            F.UInt8("DayOfWeek"),
            F.UInt8("Date"),
            F.Int8("Time"),
            F.Int32("Duration"))
        {
            Description = "Package Schedule Data"
        };

        // PTDT/PTD2 - Package Target (16 bytes)
        schemas[new SubrecordSchemaRegistry.SchemaKey("PTDT", null, 16)] = new SubrecordSchema(
            F.UInt8("Type"),
            F.Padding(3),
            F.UInt32("Union"),
            F.Int32("CountDistance"),
            F.Float("Unknown"))
        {
            Description = "Package Target"
        };
        schemas[new SubrecordSchemaRegistry.SchemaKey("PTD2", null, 16)] = new SubrecordSchema(
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
        schemas[new SubrecordSchemaRegistry.SchemaKey("PKDD", null, 24)] = new SubrecordSchema(
            F.Float("FOV"),
            F.FormId("TopicID"),
            F.UInt8("NoHeadtracking"),
            F.UInt8("DoNotControlTarget"),
            F.UInt8("SpeakerMoveTalk"),
            F.Padding(1),
            F.Float("DistanceStartTalking"),
            F.UInt8("SayTo"),
            F.Padding(3),
            F.UInt32("TriggerType"))
        {
            Description = "Package Dialogue Data"
        };

        // PLDT/PLD2 - Package Location (12 bytes)
        schemas[new SubrecordSchemaRegistry.SchemaKey("PLDT", null, 12)] = new SubrecordSchema(
            F.UInt8("Type"),
            F.Padding(3),
            F.UInt32("Union"),
            F.Int32("Radius"))
        {
            Description = "Package Location"
        };
        schemas[new SubrecordSchemaRegistry.SchemaKey("PLD2", null, 12)] = new SubrecordSchema(
            F.UInt8("Type"),
            F.Padding(3),
            F.UInt32("Union"),
            F.Int32("Radius"))
        {
            Description = "Package Location 2"
        };

        // PKW3 - Package Use Weapon Data (24 bytes) - PDB: PACK_USE_WEAPON_DATA_PKW3
        // Offsets 0-5 are individual bools, NOT a uint32+bytes. Offset 20 is a uint32, NOT padding.
        schemas[new SubrecordSchemaRegistry.SchemaKey("PKW3", null, 24)] = new SubrecordSchema(
            F.UInt8("AlwaysHit"),
            F.UInt8("DoNoDamage"),
            F.UInt8("Crouch"),
            F.UInt8("HoldFire"),
            F.UInt8("VolleyFire"),
            F.UInt8("RepeatFire"),
            F.UInt16("BurstCount"),
            F.UInt16("VolleyShotsMin"),
            F.UInt16("VolleyShotsMax"),
            F.Float("VolleyWaitMin"),
            F.Float("VolleyWaitMax"),
            F.UInt32("Weapon"))
        {
            Description = "Package Use Weapon Data"
        };

        schemas[new SubrecordSchemaRegistry.SchemaKey("CNAM", "PACK", 4)] =
            SubrecordSchema.Simple4Byte("Combat Style FormID");
        // PKPT - PDB: PACK_PATROL_DATA (2 individual bools: bRepeatable, bStartingLocationAtLinkedRef)
        schemas[new SubrecordSchemaRegistry.SchemaKey("PKPT", "PACK", 2)] = new SubrecordSchema(
            F.UInt8("Repeatable"),
            F.UInt8("StartingLocationAtLinkedRef"))
        {
            Description = "Package Patrol Flags"
        };
        schemas[new SubrecordSchemaRegistry.SchemaKey("PKAM", "PACK", 0)] = SubrecordSchema.Empty;
        schemas[new SubrecordSchemaRegistry.SchemaKey("PKED", "PACK", 0)] = SubrecordSchema.Empty;
        schemas[new SubrecordSchemaRegistry.SchemaKey("POBA", "PACK", 0)] = SubrecordSchema.Empty;
        schemas[new SubrecordSchemaRegistry.SchemaKey("POCA", "PACK", 0)] = SubrecordSchema.Empty;
        schemas[new SubrecordSchemaRegistry.SchemaKey("POEA", "PACK", 0)] = SubrecordSchema.Empty;
        schemas[new SubrecordSchemaRegistry.SchemaKey("PUID", "PACK", 0)] = SubrecordSchema.Empty;

        // ========================================================================
        // IDLE ANIMATION SCHEMAS (IDLE, IDLM)
        // ========================================================================

        // ANAM - IDLE (8 bytes)
        schemas[new SubrecordSchemaRegistry.SchemaKey("ANAM", "IDLE", 8)] = new SubrecordSchema(
            F.FormId("Parent"),
            F.FormId("Previous"))
        {
            Description = "Idle Animation Parents"
        };

        // DATA - IDLE differs between Xbox and PC.
        // Xbox: AnimData(1), LoopMin(1), LoopMax(1), pad(1), ReplayDelay(2 BE), FlagsEx(1), pad(1)
        // PC:   AnimData(1), LoopMin(1), LoopMax(1), pad(1), ReplayDelay(2 LE)
        schemas[new SubrecordSchemaRegistry.SchemaKey("DATA", "IDLE", 8)] = new SubrecordSchema(
            F.UInt8("AnimData"),
            F.UInt8("LoopMin"),
            F.UInt8("LoopMax"),
            F.Padding(1),
            F.UInt16("ReplayDelay"),
            F.UInt8("FlagsEx"),
            F.Padding(1))
        {
            Description = "Idle Animation Data (Xbox 360)"
        };

        schemas[new SubrecordSchemaRegistry.SchemaKey("DATA", "IDLE", 6)] = new SubrecordSchema(
            F.UInt8("AnimData"),
            F.UInt8("LoopMin"),
            F.UInt8("LoopMax"),
            F.Padding(1),
            F.UInt16("ReplayDelay"))
        {
            Description = "Idle Animation Data (PC)"
        };

        // IDLA - Idle Marker Animations (array of FormIDs)
        schemas[new SubrecordSchemaRegistry.SchemaKey("IDLA")] = SubrecordSchema.FormIdArray;

        schemas[new SubrecordSchemaRegistry.SchemaKey("IDLC", null, 1)] =
            new SubrecordSchema(F.UInt8("Count")) { Description = "Idle animation count" };
        schemas[new SubrecordSchemaRegistry.SchemaKey("IDLF", null, 1)] =
            new SubrecordSchema(F.UInt8("Flags")) { Description = "Idle animation flags" };
    }
}
