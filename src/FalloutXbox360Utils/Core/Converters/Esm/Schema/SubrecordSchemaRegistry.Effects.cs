using F = FalloutXbox360Utils.Core.Converters.Esm.Schema.SubrecordField;

namespace FalloutXbox360Utils.Core.Converters.Esm.Schema;

public static partial class SubrecordSchemaRegistry
{
    /// <summary>
    ///     Register effect-related schemas (MGEF, ENCH, SPEL, INGR, PERK).
    /// </summary>
    private static void RegisterEffectSchemas(Dictionary<SchemaKey, SubrecordSchema> schemas)
    {
        // ========================================================================
        // MAGIC EFFECT SCHEMAS (MGEF)
        // ========================================================================

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

        // ========================================================================
        // ENCHANTMENT SCHEMAS (ENCH)
        // ========================================================================

        // ENIT - ENCH (16 bytes)
        schemas[new SchemaKey("ENIT", "ENCH", 16)] = new SubrecordSchema(
            F.UInt32("Type"),
            F.UInt32("ChargeAmount"),
            F.UInt32("EnchantCost"),
            F.Bytes("Flags", 4))
        {
            Description = "Enchantment Data"
        };

        // EFIT - Effect Item (20 bytes = 5 x uint32)
        schemas[new SchemaKey("EFIT", null, 20)] = new SubrecordSchema(
            F.UInt32("Magnitude"), F.UInt32("Area"), F.UInt32("Duration"),
            F.UInt32("Type"), F.UInt32("ActorValue"))
        {
            Description = "Effect Item"
        };

        // ========================================================================
        // SPELL SCHEMAS (SPEL)
        // ========================================================================

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

        // ========================================================================
        // INGREDIENT SCHEMAS (INGR)
        // ========================================================================

        // ENIT - INGR (8 bytes)
        schemas[new SchemaKey("ENIT", "INGR", 8)] = new SubrecordSchema(
            F.UInt32("Value"),
            F.UInt32("Flags"))
        {
            Description = "Ingredient Data"
        };

        // DATA - INGR (4 bytes)
        schemas[new SchemaKey("DATA", "INGR", 4)] = new SubrecordSchema(F.Float("Weight"))
        {
            Description = "Ingredient Weight"
        };

        // ========================================================================
        // PERK SCHEMAS
        // ========================================================================

        // DATA - PERK (variable)
        schemas[new SchemaKey("DATA", "PERK", 4)] = SubrecordSchema.Simple4Byte();
        schemas[new SchemaKey("DATA", "PERK", 5)] = SubrecordSchema.ByteArray;
        schemas[new SchemaKey("DATA", "PERK")] = SubrecordSchema.ByteArray;

        schemas[new SchemaKey("PRKE", null, 3)] = SubrecordSchema.ByteArray;
        schemas[new SchemaKey("EPF3", "PERK", 2)] = SubrecordSchema.Simple2Byte("Perk Entry");
        schemas[new SchemaKey("EPFT", "PERK", 1)] = SubrecordSchema.ByteArray;
        schemas[new SchemaKey("PRKC", "PERK", 1)] = SubrecordSchema.ByteArray;
        schemas[new SchemaKey("PRKF", "PERK", 0)] = SubrecordSchema.ByteArray;
        schemas[new SchemaKey("EPFD", null, 4)] = SubrecordSchema.Simple4Byte();

        // ========================================================================
        // EFFECT SHADER SCHEMAS (EFSH)
        // ========================================================================

        // DATA - EFSH (200 bytes)
        schemas[new SchemaKey("DATA", "EFSH", 200)] = new SubrecordSchema(
            F.UInt8("Flags"),
            F.Padding(3),
            F.UInt32("MembraneSourceBlendMode"),
            F.UInt32("MembraneBlendOperation"),
            F.UInt32("MembraneZTestFunction"),
            F.UInt32("FillColorKey1"),
            F.Float("FillAlphaFadeInTime"),
            F.Float("FillAlphaFullTime"),
            F.Float("FillAlphaFadeOutTime"),
            F.Float("FillAlphaPersistentPercent"),
            F.Float("FillAlphaPulseAmplitude"),
            F.Float("FillAlphaPulseFrequency"),
            F.Float("FillTextureAnimSpeedU"),
            F.Float("FillTextureAnimSpeedV"),
            F.Float("EdgeEffectFallOff"),
            F.UInt32("EdgeEffectColor"),
            F.Float("EdgeEffectAlphaFadeInTime"),
            F.Float("EdgeEffectAlphaFullTime"),
            F.Float("EdgeEffectAlphaFadeOutTime"),
            F.Float("EdgeEffectAlphaPersistentPercent"),
            F.Float("EdgeEffectAlphaPulseAmplitude"),
            F.Float("EdgeEffectAlphaPulseFrequency"),
            F.Float("FillAlphaFullPercent"),
            F.Float("EdgeEffectAlphaFullPercent"),
            F.UInt32("MembraneDestBlendMode"),
            F.UInt32("PartSourceBlendMode"),
            F.UInt32("PartBlendOperation"),
            F.UInt32("PartZTestFunction"),
            F.UInt32("PartDestBlendMode"),
            F.Float("PartBirthRampUpTime"),
            F.Float("PartBirthFullTime"),
            F.Float("PartBirthRampDownTime"),
            F.Float("PartBirthFullRatio"),
            F.Float("PartBirthPersistentRatio"),
            F.Float("PartLifetimeAverage"),
            F.Float("PartLifetimeRange"),
            F.Float("PartSpeedAcrossHoriz"),
            F.Float("PartAccelAcrossHoriz"),
            F.Float("PartSpeedAcrossVert"),
            F.Float("PartAccelAcrossVert"),
            F.Float("PartSpeedAlongNormal"),
            F.Float("PartAccelAlongNormal"),
            F.Float("PartSpeedAlongRotation"),
            F.Float("PartAccelAlongRotation"),
            F.Float("HolesStartTime"),
            F.Float("HolesEndTime"),
            F.Float("HolesStartValue"),
            F.Float("HolesEndValue"))
        {
            Description = "Effect Shader Data (200 bytes)"
        };

        // DATA - EFSH (224 bytes)
        schemas[new SchemaKey("DATA", "EFSH", 224)] = new SubrecordSchema(
            F.UInt8("Flags"),
            F.Padding(3),
            F.UInt32("MembraneSourceBlendMode"),
            F.UInt32("MembraneBlendOperation"),
            F.UInt32("MembraneZTestFunction"),
            F.UInt32("FillColorKey1"),
            F.Float("FillAlphaFadeInTime"),
            F.Float("FillAlphaFullTime"),
            F.Float("FillAlphaFadeOutTime"),
            F.Float("FillAlphaPersistentPercent"),
            F.Float("FillAlphaPulseAmplitude"),
            F.Float("FillAlphaPulseFrequency"),
            F.Float("FillTextureAnimSpeedU"),
            F.Float("FillTextureAnimSpeedV"),
            F.Float("EdgeEffectFallOff"),
            F.UInt32("EdgeEffectColor"),
            F.Float("EdgeEffectAlphaFadeInTime"),
            F.Float("EdgeEffectAlphaFullTime"),
            F.Float("EdgeEffectAlphaFadeOutTime"),
            F.Float("EdgeEffectAlphaPersistentPercent"),
            F.Float("EdgeEffectAlphaPulseAmplitude"),
            F.Float("EdgeEffectAlphaPulseFrequency"),
            F.Float("FillAlphaFullPercent"),
            F.Float("EdgeEffectAlphaFullPercent"),
            F.UInt32("MembraneDestBlendMode"),
            F.UInt32("PartSourceBlendMode"),
            F.UInt32("PartBlendOperation"),
            F.UInt32("PartZTestFunction"),
            F.UInt32("PartDestBlendMode"),
            F.Float("PartBirthRampUpTime"),
            F.Float("PartBirthFullTime"),
            F.Float("PartBirthRampDownTime"),
            F.Float("PartBirthFullRatio"),
            F.Float("PartBirthPersistentRatio"),
            F.Float("PartLifetimeAverage"),
            F.Float("PartLifetimeRange"),
            F.Float("PartSpeedAcrossHoriz"),
            F.Float("PartAccelAcrossHoriz"),
            F.Float("PartSpeedAcrossVert"),
            F.Float("PartAccelAcrossVert"),
            F.Float("PartSpeedAlongNormal"),
            F.Float("PartAccelAlongNormal"),
            F.Float("PartSpeedAlongRotation"),
            F.Float("PartAccelAlongRotation"),
            F.Float("HolesStartTime"),
            F.Float("HolesEndTime"),
            F.Float("HolesStartValue"),
            F.Float("HolesEndValue"),
            F.UInt32("FillColorKey2"),
            F.UInt32("FillColorKey3"),
            F.Float("FillColorKey1Scale"),
            F.Float("FillColorKey2Scale"),
            F.Float("FillColorKey3Scale"),
            F.Float("FillColorKey1Time"),
            F.Float("FillColorKey2Time"),
            F.Float("FillColorKey3Time"))
        {
            Description = "Effect Shader Data (224 bytes)"
        };

        // DATA - EFSH (308 bytes - extended)
        schemas[new SchemaKey("DATA", "EFSH", 308)] = new SubrecordSchema(
            F.UInt8("Flags"),
            F.Padding(3),
            F.UInt32("MembraneSourceBlendMode"),
            F.UInt32("MembraneBlendOperation"),
            F.UInt32("MembraneZTestFunction"),
            F.UInt32("FillColorKey1"),
            F.Float("FillAlphaFadeInTime"),
            F.Float("FillAlphaFullTime"),
            F.Float("FillAlphaFadeOutTime"),
            F.Float("FillAlphaPersistentPercent"),
            F.Float("FillAlphaPulseAmplitude"),
            F.Float("FillAlphaPulseFrequency"),
            F.Float("FillTextureAnimSpeedU"),
            F.Float("FillTextureAnimSpeedV"),
            F.Float("EdgeEffectFallOff"),
            F.UInt32("EdgeEffectColor"),
            F.Float("EdgeEffectAlphaFadeInTime"),
            F.Float("EdgeEffectAlphaFullTime"),
            F.Float("EdgeEffectAlphaFadeOutTime"),
            F.Float("EdgeEffectAlphaPersistentPercent"),
            F.Float("EdgeEffectAlphaPulseAmplitude"),
            F.Float("EdgeEffectAlphaPulseFrequency"),
            F.Float("FillAlphaFullPercent"),
            F.Float("EdgeEffectAlphaFullPercent"),
            F.UInt32("MembraneDestBlendMode"),
            F.UInt32("PartSourceBlendMode"),
            F.UInt32("PartBlendOperation"),
            F.UInt32("PartZTestFunction"),
            F.UInt32("PartDestBlendMode"),
            F.Float("PartBirthRampUpTime"),
            F.Float("PartBirthFullTime"),
            F.Float("PartBirthRampDownTime"),
            F.Float("PartBirthFullRatio"),
            F.Float("PartBirthPersistentRatio"),
            F.Float("PartLifetimeAverage"),
            F.Float("PartLifetimeRange"),
            F.Float("PartSpeedAcrossHoriz"),
            F.Float("PartAccelAcrossHoriz"),
            F.Float("PartSpeedAcrossVert"),
            F.Float("PartAccelAcrossVert"),
            F.Float("PartSpeedAlongNormal"),
            F.Float("PartAccelAlongNormal"),
            F.Float("PartSpeedAlongRotation"),
            F.Float("PartAccelAlongRotation"),
            F.Float("PartScaleKey1"),
            F.Float("PartScaleKey2"),
            F.Float("PartScaleKey1Time"),
            F.Float("PartScaleKey2Time"),
            F.UInt32("FillColorKey2"),
            F.UInt32("FillColorKey3"),
            F.Float("FillColorKey1Scale"),
            F.Float("FillColorKey2Scale"),
            F.Float("FillColorKey3Scale"),
            F.Float("FillColorKey1Time"),
            F.Float("FillColorKey2Time"),
            F.Float("FillColorKey3Time"),
            F.Float("PartInitialSpeedNormalVariance"),
            F.Float("PartInitialRotation"),
            F.Float("PartInitialRotationVariance"),
            F.Float("PartRotationSpeed"),
            F.Float("PartRotationSpeedVariance"),
            F.FormId("AddonModels"),
            F.Float("HolesStartTime2"),
            F.Float("HolesEndTime2"),
            F.Float("HolesStartValue2"),
            F.Float("HolesEndValue2"),
            F.Float("EdgeWidth"),
            F.UInt32("EdgeColor2"),
            F.Float("ExplosionWindSpeed"),
            F.UInt32("TextureCountU"),
            F.UInt32("TextureCountV"),
            F.Float("AddonModelsFadeInTime"),
            F.Float("AddonModelsFadeOutTime"),
            F.Float("AddonModelsScaleStart"),
            F.Float("AddonModelsScaleEnd"),
            F.Float("AddonModelsScaleInTime"),
            F.Float("AddonModelsScaleOutTime"))
        {
            Description = "Effect Shader Data (308 bytes - extended)"
        };

        // ========================================================================
        // COMBAT STYLE SCHEMAS (CSTY)
        // ========================================================================

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
            F.UInt32("RangeMultFlags"),
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

        // ========================================================================
        // IMPACT DATA SCHEMAS (IPCT, IPDS)
        // ========================================================================

        // DATA - IPCT (24 bytes) - Impact Data
        schemas[new SchemaKey("DATA", "IPCT", 24)] = new SubrecordSchema(
            F.Float("EffectDuration"),
            F.UInt32("EffectOrientation"),
            F.Float("AngleThreshold"),
            F.Float("PlacementRadius"),
            F.UInt32("SoundLevel"),
            F.UInt32("NoDecalData"))
        {
            Description = "Impact Data"
        };

        // DATA - IPDS (48 bytes) - Impact Data Set (12 FormIDs!)
        schemas[new SchemaKey("DATA", "IPDS", 48)] = new SubrecordSchema(
            F.FormId("Stone"),
            F.FormId("Dirt"),
            F.FormId("Grass"),
            F.FormId("Glass"),
            F.FormId("Metal"),
            F.FormId("Wood"),
            F.FormId("Organic"),
            F.FormId("Cloth"),
            F.FormId("Water"),
            F.FormId("HollowMetal"),
            F.FormId("OrganicBug"),
            F.FormId("OrganicGlow"))
        {
            Description = "Impact Data Set - Material Impact References"
        };

        schemas[new SchemaKey("DNAM", "IPCT", 4)] = SubrecordSchema.Simple4Byte("Impact Data");
        schemas[new SchemaKey("NAM1", "IPCT", 4)] = SubrecordSchema.Simple4Byte("Secondary Effect FormID");
        schemas[new SchemaKey("SNAM", "IPCT", 4)] = SubrecordSchema.Simple4Byte("Impact Sound");

        // ========================================================================
        // ADDON NODE SCHEMAS (ADDN)
        // ========================================================================

        // DATA - ADDN (4 bytes)
        schemas[new SchemaKey("DATA", "ADDN", 4)] = new SubrecordSchema(F.Int32("NodeIndex"))
        {
            Description = "Addon Node Index"
        };

        schemas[new SchemaKey("DNAM", "ADDN", 4)] = SubrecordSchema.Simple4Byte("Addon Flags");
        schemas[new SchemaKey("SNAM", "ADDN", 4)] = SubrecordSchema.Simple4Byte("Sound FormID");
    }
}
