using F = FalloutXbox360Utils.Core.Formats.Esm.Conversion.Schema.SubrecordField;

namespace FalloutXbox360Utils.Core.Formats.Esm.Conversion.Schema;

public static partial class SubrecordSchemaRegistry
{
    /// <summary>
    ///     Register actor-related schemas (NPC_, CREA, RACE, FACT, CLAS).
    /// </summary>
    private static void RegisterActorSchemas(Dictionary<SchemaKey, SubrecordSchema> schemas)
    {
        // ========================================================================
        // ACBS - Actor Base Stats (24 bytes)
        // ========================================================================
        schemas[new SchemaKey("ACBS", null, 24)] = new SubrecordSchema(
            F.UInt32("Flags"),
            F.UInt16("Fatigue"),
            F.UInt16("BarterGold"),
            F.Int16("Level"),
            F.UInt16("CalcMin"),
            F.UInt16("CalcMax"),
            F.UInt16("SpeedMult"),
            F.Float("KarmaAlignment"),
            F.Int16("Disposition"),
            F.UInt16("TemplateFlags"))
        {
            Description = "Actor Base Stats"
        };

        // ========================================================================
        // AIDT - AI Data (20 bytes)
        // ========================================================================
        // PDB: AIDATA struct (type 0x1AA0D) — 20 bytes with Endian() method
        schemas[new SchemaKey("AIDT", null, 20)] = new SubrecordSchema(
            F.UInt8("Aggression"),
            F.UInt8("Confidence"),
            F.UInt8("Energy"),
            F.UInt8("Responsibility"),
            F.UInt8("Mood"),
            F.Padding(3),
            F.UInt32("ServiceFlags"),
            F.UInt8("TrainingSkill"),
            F.UInt8("TrainingLevel"),
            F.UInt8("Assistance"),
            F.UInt8("AggroRadiusBehavior"),
            F.UInt32("AggroRadius"))
        {
            Description = "AI Data"
        };

        // ========================================================================
        // RACE-SPECIFIC SCHEMAS
        // ========================================================================

        // RACE DNAM - Height Data (8 bytes = 2 floats)
        schemas[new SchemaKey("DNAM", "RACE", 8)] =
            new SubrecordSchema(F.Float("Height Male"), F.Float("Height Female"))
            {
                Description = "RACE Height Data"
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

        // HNAM - RACE Hair (variable, array of FormIDs)
        schemas[new SchemaKey("HNAM", "RACE")] = SubrecordSchema.FormIdArray;

        // ENAM - RACE Eyes (variable, array of FormIDs)
        schemas[new SchemaKey("ENAM", "RACE")] = SubrecordSchema.FormIdArray;

        // ONAM - RACE Older Race (4 bytes = single FormID)
        schemas[new SchemaKey("ONAM", "RACE", 4)] = SubrecordSchema.Simple4Byte("Older Race FormID");

        // YNAM - RACE Younger Race (4 bytes = single FormID)
        schemas[new SchemaKey("YNAM", "RACE", 4)] = SubrecordSchema.Simple4Byte("Younger Race FormID");

        // VTCK - RACE Voices (8 bytes = two FormIDs: Male + Female voice types)
        schemas[new SchemaKey("VTCK", "RACE", 8)] = new SubrecordSchema(
            F.FormId("Male Voice Type"),
            F.FormId("Female Voice Type"))
        {
            Description = "RACE Voice Types"
        };

        // NAM1/MNAM/FNAM in RACE - Zero-byte marker subrecords (delimiters)
        schemas[new SchemaKey("NAM1", "RACE", 0)] = SubrecordSchema.Empty;
        schemas[new SchemaKey("MNAM", "RACE", 0)] = SubrecordSchema.Empty;
        schemas[new SchemaKey("FNAM", "RACE", 0)] = SubrecordSchema.Empty;
        schemas[new SchemaKey("FNAM", "RACE", 0)] = SubrecordSchema.ByteArray;

        // ATTR - RACE attributes (2 bytes)
        schemas[new SchemaKey("ATTR", "RACE", 2)] = SubrecordSchema.Simple2Byte("Attribute");
        schemas[new SchemaKey("CNAM", "RACE", 2)] = SubrecordSchema.Simple2Byte("Color Index");
        schemas[new SchemaKey("SNAM", "RACE", 2)] = SubrecordSchema.Simple2Byte();

        // PNAM - RACE FaceGen Main Clamp (4 bytes = float)
        schemas[new SchemaKey("PNAM", "RACE", 4)] =
            new SubrecordSchema(F.Float("FaceGenMainClamp"))
            {
                Description = "RACE FaceGen Main Clamp"
            };

        // UNAM - RACE FaceGen Face Clamp (4 bytes = float)
        schemas[new SchemaKey("UNAM", "RACE", 4)] =
            new SubrecordSchema(F.Float("FaceGenFaceClamp"))
            {
                Description = "RACE FaceGen Face Clamp"
            };

        // ========================================================================
        // NPC_-SPECIFIC SCHEMAS
        // ========================================================================

        // HNAM - NPC_ Hair FormID (4 bytes)
        schemas[new SchemaKey("HNAM", "NPC_", 4)] = SubrecordSchema.Simple4Byte("Hair FormID");

        // LNAM - NPC_ Hair Length (4 bytes = float)
        schemas[new SchemaKey("LNAM", "NPC_", 4)] =
            new SubrecordSchema(F.Float("HairLength"))
            {
                Description = "NPC Hair Length"
            };

        // ENAM - NPC_ Eyes FormID (4 bytes)
        schemas[new SchemaKey("ENAM", "NPC_", 4)] = SubrecordSchema.Simple4Byte("Eyes FormID");

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

        schemas[new SchemaKey("CNAM", "NPC_", 4)] = SubrecordSchema.Simple4Byte("Class FormID");
        schemas[new SchemaKey("NAM4", "NPC_", 4)] = SubrecordSchema.Simple4Byte("NAM4 FormID");
        schemas[new SchemaKey("DNAM", "NPC_", 28)] = SubrecordSchema.ByteArray; // NPC skill values - no conversion

        // SNAM - NPC_ Faction Membership (8 bytes)
        schemas[new SchemaKey("SNAM", "NPC_", 8)] =
            new SubrecordSchema(F.FormId("Faction"), F.UInt8("Rank"), F.Bytes("Unused", 3))
            {
                Description = "NPC Faction Membership"
            };

        // ========================================================================
        // CREA-SPECIFIC SCHEMAS
        // ========================================================================

        // DATA - CREA (17 bytes)
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

        schemas[new SchemaKey("CNAM", "CREA", 4)] = SubrecordSchema.Simple4Byte("Combat Style FormID");
        schemas[new SchemaKey("NAM4", "CREA", 4)] = SubrecordSchema.Simple4Byte("NAM4 FormID");
        schemas[new SchemaKey("NAM5", "CREA", 4)] = SubrecordSchema.Simple4Byte("NAM5 FormID");
        schemas[new SchemaKey("CSDC", "CREA", 1)] = SubrecordSchema.ByteArray;

        // SNAM - CREA Faction Membership (8 bytes)
        schemas[new SchemaKey("SNAM", "CREA", 8)] =
            new SubrecordSchema(F.FormId("Faction"), F.UInt8("Rank"), F.Bytes("Unused", 3))
            {
                Description = "Creature Faction Membership"
            };

        // ========================================================================
        // FACT-SPECIFIC SCHEMAS
        // ========================================================================

        // XNAM - FACT Relation (12 bytes)
        schemas[new SchemaKey("XNAM", "FACT", 12)] = new SubrecordSchema(
            F.FormId("Faction"),
            F.Int32("Modifier"),
            F.UInt32("CombatReaction"))
        {
            Description = "Faction Relation"
        };

        // DATA - FACT (4 bytes) - Faction Flags
        // PDB: TESFaction.flags = uint32 BE at offset 52
        schemas[new SchemaKey("DATA", "FACT", 4)] = SubrecordSchema.Simple4Byte("Faction Flags");

        // RNAM - FACT Rank Number (4 bytes) - override generic FormID version
        schemas[new SchemaKey("RNAM", "FACT", 4)] = new SubrecordSchema(F.Int32("RankNumber"))
        {
            Description = "Faction Rank Number"
        };

        // CRVA - FACT Crime Values (20 bytes)
        schemas[new SchemaKey("CRVA", "FACT", 20)] = new SubrecordSchema(
            F.Float("CrimeGoldMultiplier"),
            F.Bytes("Remaining", 16))
        {
            Description = "Faction Crime Values"
        };

        // ========================================================================
        // CLAS-SPECIFIC SCHEMAS
        // ========================================================================

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

        // ATTR - CLAS (7 bytes S.P.E.C.I.A.L. attributes)
        schemas[new SchemaKey("ATTR", null, 7)] = SubrecordSchema.ByteArray;

        // ========================================================================
        // BODY PART DATA (BPTD)
        // ========================================================================

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

        // ========================================================================
        // RAGDOLL DATA (RGDL)
        // ========================================================================

        // DATA - RGDL (14 bytes)
        schemas[new SchemaKey("DATA", "RGDL", 14)] = new SubrecordSchema(
            F.UInt32WordSwapped("DynamicBoneCount"),
            F.Padding(4),
            F.UInt8("Feedback"),
            F.UInt8("FootIK"),
            F.UInt8("LookIK"),
            F.UInt8("GrabIK"),
            F.UInt8("PoseMatching"),
            F.Padding(1))
        {
            Description = "Ragdoll Data"
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
            ExpectedSize = -1,
            Description = "Ragdoll Dynamic Bones"
        };

        // XRGD - Ragdoll Bone Data (28 bytes per bone)
        schemas[new SchemaKey("XRGD")] = new SubrecordSchema(
            F.UInt8("BoneId"),
            F.Padding(3),
            F.Vec3("Position"),
            F.Vec3("Rotation"))
        {
            ExpectedSize = -1,
            Description = "Ragdoll Bone Data Array"
        };
    }
}
