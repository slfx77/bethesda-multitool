using F = FalloutXbox360Utils.Core.Converters.Esm.Schema.SubrecordField;

namespace FalloutXbox360Utils.Core.Converters.Esm.Schema;

public static partial class SubrecordSchemaRegistry
{
    /// <summary>
    ///     Register item-related schemas (WEAP, ARMO, AMMO, ALCH, MISC, CONT, KEYM).
    /// </summary>
    private static void RegisterItemSchemas(Dictionary<SchemaKey, SubrecordSchema> schemas)
    {
        // ========================================================================
        // WEAP-SPECIFIC SCHEMAS
        // ========================================================================

        // DNAM - WEAP (204 bytes)
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

        // CRDT - Critical Data (16 bytes)
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

        // VATS - VATS Data (20 bytes)
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

        schemas[new SchemaKey("SNAM", "WEAP", 4)] = SubrecordSchema.Simple4Byte("Sound FormID");

        // ========================================================================
        // ARMO-SPECIFIC SCHEMAS
        // ========================================================================

        // DNAM - ARMO (12 bytes)
        schemas[new SchemaKey("DNAM", "ARMO", 12)] =
            new SubrecordSchema(F.Float("AR"), F.Float("Weight"), F.Float("Health"))
            {
                Description = "ARMO Data"
            };

        // DATA - ARMO (12 bytes)
        schemas[new SchemaKey("DATA", "ARMO", 12)] = new SubrecordSchema(
            F.Int32("Value"),
            F.Int32("Health"),
            F.Float("Weight"))
        {
            Description = "Armor Data"
        };

        schemas[new SchemaKey("SNAM", "ARMO", 12)] = SubrecordSchema.ByteArray;

        // BMDT - Biped Model Data (8 bytes)
        schemas[new SchemaKey("BMDT", null, 8)] = new SubrecordSchema(
            F.UInt32("BipedFlags"),
            F.Padding(4))
        {
            Description = "Biped Model Data"
        };

        // DATA - ARMA (12 bytes) - Armor Addon Data
        schemas[new SchemaKey("DATA", "ARMA", 12)] = new SubrecordSchema(
            F.Int32("Value"),
            F.Int32("MaxCondition"),
            F.Float("Weight"))
        {
            Description = "Armor Addon Data"
        };

        schemas[new SchemaKey("DNAM", "ARMA", 12)] = SubrecordSchema.FloatArray;

        // ========================================================================
        // AMMO-SPECIFIC SCHEMAS
        // ========================================================================

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

        // DAT2 - AMMO Secondary Data (20 bytes)
        schemas[new SchemaKey("DAT2", null, 20)] = new SubrecordSchema(
            F.Padding(8),
            F.UInt32("Value1"),
            F.Padding(4),
            F.UInt32("Value2"))
        {
            Description = "AMMO Secondary Data"
        };

        // DATA - AMEF (12 bytes) - Ammo Effect Data
        schemas[new SchemaKey("DATA", "AMEF", 12)] = new SubrecordSchema(
            F.UInt32("Type"),
            F.UInt32("Operation"),
            F.Float("Value"))
        {
            Description = "Ammo Effect Data"
        };

        // ========================================================================
        // ALCH-SPECIFIC SCHEMAS
        // ========================================================================

        // ENIT - ALCH (20 bytes)
        schemas[new SchemaKey("ENIT", "ALCH", 20)] = new SubrecordSchema(
            F.UInt32("Value"),
            F.Bytes("Flags", 4),
            F.FormId("Addiction"),
            F.Float("AddictionChance"),
            F.FormId("UseSoundOrWithdrawalEffect"))
        {
            Description = "Alchemy/Potion Data"
        };

        // DATA - ALCH (4 bytes)
        schemas[new SchemaKey("DATA", "ALCH", 4)] = new SubrecordSchema(F.Float("Weight"));

        // ========================================================================
        // MISC-SPECIFIC SCHEMAS
        // ========================================================================

        // DATA - MISC (8 bytes) - Misc Item Data
        schemas[new SchemaKey("DATA", "MISC", 8)] = new SubrecordSchema(
            F.Int32("Value"),
            F.Float("Weight"))
        {
            Description = "Misc Item Data"
        };

        // ========================================================================
        // CONT-SPECIFIC SCHEMAS
        // ========================================================================

        // CNTO - Container Item (8 bytes)
        schemas[new SchemaKey("CNTO", null, 8)] = new SubrecordSchema(
            F.FormId("Item"),
            F.Int32("Count"))
        {
            Description = "Container Item"
        };

        // DATA - CONT (5 bytes)
        schemas[new SchemaKey("DATA", "CONT", 5)] = new SubrecordSchema(
            F.UInt8("Flags"),
            F.Float("Weight"))
        {
            Description = "Container Data"
        };

        schemas[new SchemaKey("SNAM", "CONT", 4)] = SubrecordSchema.Simple4Byte("Sound FormID");

        // ========================================================================
        // KEYM-SPECIFIC SCHEMAS
        // ========================================================================

        // DATA - KEYM (8 bytes) - Key Data
        schemas[new SchemaKey("DATA", "KEYM", 8)] = new SubrecordSchema(
            F.Int32("Value"),
            F.Float("Weight"))
        {
            Description = "Key Data"
        };

        // ========================================================================
        // BOOK-SPECIFIC SCHEMAS
        // ========================================================================

        // DATA - BOOK (10 bytes) - Book Data
        schemas[new SchemaKey("DATA", "BOOK", 10)] = new SubrecordSchema(
            F.UInt8("Flags"),
            F.Int8("Skill"),
            F.Int32("Value"),
            F.Float("Weight"))
        {
            Description = "Book Data"
        };

        // ========================================================================
        // IMOD-SPECIFIC SCHEMAS
        // ========================================================================

        // DATA - IMOD (8 bytes) - Item Mod Data
        schemas[new SchemaKey("DATA", "IMOD", 8)] = new SubrecordSchema(
            F.UInt32("Value"),
            F.Float("Weight"))
        {
            Description = "Item Mod Data"
        };

        // ========================================================================
        // PROJECTILE SCHEMAS (PROJ)
        // ========================================================================

        // DATA - PROJ (84 bytes)
        schemas[new SchemaKey("DATA", "PROJ", 84)] = new SubrecordSchema(
            F.UInt32("FlagsAndType"),
            F.Float("Gravity"),
            F.Float("Speed"),
            F.Float("Range"),
            F.FormIdLittleEndian("Light"),
            F.FormIdLittleEndian("MuzzleFlashLight"),
            F.Float("TracerChance"),
            F.Float("ExplosionAltTriggerProximity"),
            F.Float("ExplosionAltTriggerTimer"),
            F.FormIdLittleEndian("Explosion"),
            F.FormIdLittleEndian("Sound"),
            F.Float("MuzzleFlashDuration"),
            F.Float("FadeDuration"),
            F.Float("ImpactForce"),
            F.FormIdLittleEndian("SoundCountdown"),
            F.FormIdLittleEndian("SoundDisable"),
            F.FormIdLittleEndian("DefaultWeaponSource"),
            F.Float("RotationX"),
            F.Float("RotationY"),
            F.Float("RotationZ"),
            F.Float("BouncyMult"))
        {
            Description = "Projectile Data"
        };

        // NAM2 - Model Info in PROJ
        schemas[new SchemaKey("NAM2", "PROJ")] = SubrecordSchema.ByteArray;

        // ========================================================================
        // EXPLOSION SCHEMAS (EXPL)
        // ========================================================================

        // DATA - EXPL (52 bytes)
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

        // ========================================================================
        // LEVELED LIST SCHEMAS
        // ========================================================================

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

        schemas[new SchemaKey("LVLD", null, 1)] = SubrecordSchema.ByteArray;
        schemas[new SchemaKey("LVLF", null, 1)] = SubrecordSchema.ByteArray;

        // ========================================================================
        // DESTRUCTIBLE OBJECT SCHEMAS
        // ========================================================================

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

        schemas[new SchemaKey("DSTF", null, 0)] = SubrecordSchema.ByteArray;

        // COED - Extra Data (12 bytes)
        schemas[new SchemaKey("COED", null, 12)] = new SubrecordSchema(
            F.FormId("Owner"),
            F.UInt32("GlobalOrRank"),
            F.Float("ItemCondition"))
        {
            Description = "Owner Extra Data"
        };

        // DATA - DEBR (variable length) - Debris Model Data
        schemas[new SchemaKey("DATA", "DEBR")] = SubrecordSchema.ByteArray;

        // ========================================================================
        // RECIPE SCHEMAS (RCPE)
        // ========================================================================

        // DATA - RCPE (16 bytes)
        schemas[new SchemaKey("DATA", "RCPE", 16)] = new SubrecordSchema(
            F.Int32("Skill"),
            F.UInt32("Level"),
            F.FormId("Category"),
            F.FormId("SubCategory"))
        {
            Description = "Recipe Data"
        };

        // DATA - RCCT (1 byte) - Recipe Category Flags
        schemas[new SchemaKey("DATA", "RCCT", 1)] = SubrecordSchema.ByteArray;

        // ========================================================================
        // LIGHT SCHEMAS (LIGH)
        // ========================================================================

        // DATA - LIGH (32 bytes)
        schemas[new SchemaKey("DATA", "LIGH", 32)] = new SubrecordSchema(
            F.Int32("Time"),
            F.UInt32("Radius"),
            F.UInt32("Color"),
            F.UInt32("Flags"),
            F.Float("FalloffExponent"),
            F.Float("FOV"),
            F.UInt32("Value"),
            F.Float("Weight"))
        {
            Description = "Light Data"
        };

        schemas[new SchemaKey("FNAM", "LIGH", 4)] = SubrecordSchema.Simple4Byte("Light Flags");
        schemas[new SchemaKey("SNAM", "LIGH", 4)] = SubrecordSchema.Simple4Byte("Sound FormID");
    }
}
