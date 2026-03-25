using F = FalloutXbox360Utils.Core.Formats.Esm.Conversion.Schema.SubrecordField;

namespace FalloutXbox360Utils.Core.Formats.Esm.Conversion.Schema;

/// <summary>
///     Cell, land, worldspace, region, weather, water, and misc record schemas.
/// </summary>
internal static class SubrecordCellAndMiscSchemas
{
    /// <summary>
    ///     Register cell, land, worldspace, region, weather, water, and misc record schemas.
    /// </summary>
    internal static void Register(Dictionary<SubrecordSchemaRegistry.SchemaKey, SubrecordSchema> schemas)
    {
        // ========================================================================
        // CELL SCHEMAS
        // ========================================================================

        // XCLL - Cell Lighting (40 bytes)
        schemas[new SubrecordSchemaRegistry.SchemaKey("XCLL", null, 40)] = new SubrecordSchema(
            F.UInt32("AmbientColor"), F.UInt32("DirectionalColor"),
            F.UInt32("FogColor"), F.Float("FogNear"), F.Float("FogFar"),
            F.Int32("DirectionalRotationXY"), F.Int32("DirectionalRotationZ"),
            F.Float("DirectionalFade"), F.Float("FogClipDistance"), F.Float("FogPow"))
        {
            Description = "Cell Lighting"
        };

        // XCLC - Cell Grid (12 bytes)
        schemas[new SubrecordSchemaRegistry.SchemaKey("XCLC", null, 12)] = new SubrecordSchema(
            F.Int32("X"),
            F.Int32("Y"),
            F.UInt8("LandFlags"),
            F.Padding(3))
        {
            Description = "Cell Grid"
        };

        // XCLR - Cell Regions (array of FormIDs)
        schemas[new SubrecordSchemaRegistry.SchemaKey("XCLR")] = SubrecordSchema.FormIdArray;

        schemas[new SubrecordSchemaRegistry.SchemaKey("DATA", "CELL", 1)] = SubrecordSchema.ByteArray;

        // ========================================================================
        // LAND SCHEMAS
        // ========================================================================

        // VTXT - Vertex Texture Blend (repeating 8-byte entries)
        schemas[new SubrecordSchemaRegistry.SchemaKey("VTXT", "LAND")] = new SubrecordSchema(
            F.UInt16("Position"),
            F.Bytes("Unused", 2),
            F.Float("Opacity"))
        {
            ExpectedSize = -1,
            Description = "Land Vertex Texture Blend Array"
        };

        // ATXT/BTXT - Texture Alpha (8 bytes)
        schemas[new SubrecordSchemaRegistry.SchemaKey("ATXT", null, 8)] = new SubrecordSchema(
            F.FormId("Texture"),
            F.UInt8("Quadrant"),
            F.UInt8("PlatformFlag"),
            F.UInt16("Layer"))
        {
            Description = "Alpha Texture"
        };
        schemas[new SubrecordSchemaRegistry.SchemaKey("BTXT", null, 8)] = new SubrecordSchema(
            F.FormId("Texture"),
            F.UInt8("Quadrant"),
            F.UInt8("PlatformFlag"),
            F.UInt16("Layer"))
        {
            Description = "Base Texture"
        };

        // VHGT - Height Data (1096 bytes)
        schemas[new SubrecordSchemaRegistry.SchemaKey("VHGT", null, 1096)] = new SubrecordSchema(
            F.Float("HeightOffset"),
            F.Bytes("HeightData", 1089),
            F.Padding(3))
        {
            Description = "Vertex Height Data"
        };

        schemas[new SubrecordSchemaRegistry.SchemaKey("DATA", "LAND", 4)] = new SubrecordSchema(F.UInt32("Flags"));
        schemas[new SubrecordSchemaRegistry.SchemaKey("HNAM", "LTEX", 3)] = SubrecordSchema.ByteArray;
        schemas[new SubrecordSchemaRegistry.SchemaKey("SNAM", "LTEX", 1)] = SubrecordSchema.ByteArray;

        // ========================================================================
        // WORLDSPACE SCHEMAS (WRLD)
        // ========================================================================

        schemas[new SubrecordSchemaRegistry.SchemaKey("CNAM", "WRLD", 4)] = SubrecordSchema.Simple4Byte("Climate FormID");
        schemas[new SubrecordSchemaRegistry.SchemaKey("NAM2", "WRLD", 4)] = SubrecordSchema.Simple4Byte("NAM2 FormID");
        schemas[new SubrecordSchemaRegistry.SchemaKey("DATA", "WRLD", 1)] = SubrecordSchema.ByteArray;

        // DNAM - WRLD (8 bytes)
        schemas[new SubrecordSchemaRegistry.SchemaKey("DNAM", "WRLD", 8)] = new SubrecordSchema(F.Float("Value1"), F.Float("Value2"))
        {
            Description = "World Default Land Height/Water Height"
        };

        // MNAM - World Map Data (16 bytes)
        schemas[new SubrecordSchemaRegistry.SchemaKey("MNAM", "WRLD", 16)] = new SubrecordSchema(
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
        schemas[new SubrecordSchemaRegistry.SchemaKey("NAM0", "WRLD", 8)] = new SubrecordSchema(F.Float("X"), F.Float("Y"))
        {
            Description = "Worldspace Bounds Min"
        };
        schemas[new SubrecordSchemaRegistry.SchemaKey("NAM9", "WRLD", 8)] = new SubrecordSchema(F.Float("X"), F.Float("Y"))
        {
            Description = "Worldspace Bounds Max"
        };

        // ONAM - Worldspace persistent cell list (array of FormIDs)
        schemas[new SubrecordSchemaRegistry.SchemaKey("ONAM", "WRLD")] = SubrecordSchema.FormIdArray;

        // OFST - Worldspace offset table (array of uint32)
        schemas[new SubrecordSchemaRegistry.SchemaKey("OFST", "WRLD")] = SubrecordSchema.FormIdArray;

        // ========================================================================
        // REGION SCHEMAS (REGN)
        // ========================================================================

        // RDAT - Region Data Header (8 bytes)
        schemas[new SubrecordSchemaRegistry.SchemaKey("RDAT", null, 8)] = new SubrecordSchema(
            F.UInt32("Type"),
            F.UInt8("Override"),
            F.UInt8("Priority"),
            F.Padding(2))
        {
            Description = "Region Data Header"
        };

        // RDSD - Region Sounds (12 bytes per entry)
        schemas[new SubrecordSchemaRegistry.SchemaKey("RDSD")] = new SubrecordSchema(
            F.FormId("Sound"),
            F.UInt32("Flags"),
            F.UInt32("Chance"))
        {
            ExpectedSize = -1,
            Description = "Region Sounds Array"
        };

        // RDID - Region Imposters (array of FormIDs)
        schemas[new SubrecordSchemaRegistry.SchemaKey("RDID")] = SubrecordSchema.FormIdArray;

        // RDWT - Region Weather Types (12 bytes per entry)
        schemas[new SubrecordSchemaRegistry.SchemaKey("RDWT")] = new SubrecordSchema(
            F.FormId("Weather"),
            F.UInt32("Chance"),
            F.FormId("Global"))
        {
            ExpectedSize = -1,
            Description = "Region Weather Types Array"
        };

        // RPLD - Region Point List Data (array of X,Y float pairs)
        schemas[new SubrecordSchemaRegistry.SchemaKey("RPLD")] = SubrecordSchema.FloatArray;

        // RDOT - Region Objects (52 bytes per entry)
        schemas[new SubrecordSchemaRegistry.SchemaKey("RDOT")] = new SubrecordSchema(
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
        schemas[new SubrecordSchemaRegistry.SchemaKey("FNAM", "WTHR", 24)] = SubrecordSchema.FloatArray;

        // PNAM - Weather Cloud Colors (96 bytes)
        schemas[new SubrecordSchemaRegistry.SchemaKey("PNAM", "WTHR", 96)] = SubrecordSchema.FormIdArray;

        // NAM0 - Weather Colors (240 bytes)
        schemas[new SubrecordSchemaRegistry.SchemaKey("NAM0", "WTHR", 240)] = SubrecordSchema.FormIdArray;

        // INAM - Weather Image Spaces (304 bytes = 76 floats)
        schemas[new SubrecordSchemaRegistry.SchemaKey("INAM", "WTHR", 304)] = SubrecordSchema.FloatArray;

        // WLST - Weather Types (12 bytes per entry)
        schemas[new SubrecordSchemaRegistry.SchemaKey("WLST")] = new SubrecordSchema(
            F.FormId("Weather"),
            F.Int32("Chance"),
            F.FormId("Global"))
        {
            ExpectedSize = -1,
            Description = "Weather Types Array"
        };

        // SNAM - WTHR (8 bytes)
        schemas[new SubrecordSchemaRegistry.SchemaKey("SNAM", "WTHR", 8)] = new SubrecordSchema(F.FormId("Sound"), F.UInt32("Type"))
        {
            Description = "Weather Sound"
        };

        // IAD - WTHR (4 bytes)
        schemas[new SubrecordSchemaRegistry.SchemaKey("IAD", "WTHR", 4)] = SubrecordSchema.Simple4Byte("Image Adapter Float");

        schemas[new SubrecordSchemaRegistry.SchemaKey("DATA", "WTHR", 15)] = SubrecordSchema.ByteArray;

        // ========================================================================
        // WATER SCHEMAS (WATR)
        // ========================================================================

        // DNAM - WATR (196 bytes)
        schemas[new SubrecordSchemaRegistry.SchemaKey("DNAM", "WATR", 196)] = new SubrecordSchema(
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
        schemas[new SubrecordSchemaRegistry.SchemaKey("GNAM", "WATR", 12)] = new SubrecordSchema(
            F.FormId("Daytime"),
            F.FormId("Nighttime"),
            F.FormId("Underwater"))
        {
            Description = "Water Related Waters"
        };

        schemas[new SubrecordSchemaRegistry.SchemaKey("SNAM", "WATR", 4)] = SubrecordSchema.Simple4Byte("Sound FormID");
        schemas[new SubrecordSchemaRegistry.SchemaKey("DATA", "WATR", 2)] = new SubrecordSchema(F.UInt16("Damage"))
        {
            Description = "Water Damage"
        };

        // ========================================================================
        // DOBJ DATA (DefaultObjectManager)
        // ========================================================================

        schemas[new SubrecordSchemaRegistry.SchemaKey("DATA", "DOBJ", 136)] = SubrecordSchema.FormIdArray;

        // ========================================================================
        // LIGHTING TEMPLATE (LGTM)
        // ========================================================================

        schemas[new SubrecordSchemaRegistry.SchemaKey("DATA", "LGTM", 40)] = new SubrecordSchema(
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

        schemas[new SubrecordSchemaRegistry.SchemaKey("DATA", "ECZN", 8)] = new SubrecordSchema(
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

        schemas[new SubrecordSchemaRegistry.SchemaKey("DATA", "GRAS", 32)] = new SubrecordSchema(
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

        schemas[new SubrecordSchemaRegistry.SchemaKey("CNAM", "TREE", 32)] = new SubrecordSchema(
            F.Float("LeafCurvature"), F.Float("MinLeafAngle"), F.Float("MaxLeafAngle"),
            F.Float("BranchDimmingValue"), F.Float("LeafDimmingValue"),
            F.Float("ShadowRadius"), F.Float("RockSpeed"),
            F.Float("RustleSpeed"))
        {
            Description = "Tree Data"
        };

        schemas[new SubrecordSchemaRegistry.SchemaKey("BNAM", "TREE", 8)] = new SubrecordSchema(
            F.Float("Width"),
            F.Float("Height"))
        {
            Description = "Billboard Dimensions"
        };

        schemas[new SubrecordSchemaRegistry.SchemaKey("SNAM", "TREE", 4)] = SubrecordSchema.Simple4Byte("Sound FormID");
        schemas[new SubrecordSchemaRegistry.SchemaKey("SNAM", "TREE", 20)] = SubrecordSchema.ByteArray;

        // ========================================================================
        // STATIC COLLECTION (SCOL)
        // ========================================================================

        schemas[new SubrecordSchemaRegistry.SchemaKey("ONAM", "SCOL", 4)] = SubrecordSchema.Simple4Byte("Static Object FormID");
        schemas[new SubrecordSchemaRegistry.SchemaKey("DATA", "SCOL")] = SubrecordSchema.FloatArray;

        // ========================================================================
        // LOAD SCREEN TYPE (LSCT)
        // ========================================================================

        schemas[new SubrecordSchemaRegistry.SchemaKey("DATA", "LSCT", 88)] = new SubrecordSchema(
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

        schemas[new SubrecordSchemaRegistry.SchemaKey("OBND", null, 12)] = new SubrecordSchema(
            F.Int16("X1"), F.Int16("Y1"), F.Int16("Z1"),
            F.Int16("X2"), F.Int16("Y2"), F.Int16("Z2"))
        {
            Description = "Object Bounds"
        };

        // DODT - Decal Data (36 bytes)
        schemas[new SubrecordSchemaRegistry.SchemaKey("DODT", null, 36)] = new SubrecordSchema(
            F.Float("MinWidth"), F.Float("MaxWidth"),
            F.Float("MinHeight"), F.Float("MaxHeight"),
            F.Float("Depth"), F.Float("Shininess"), F.Float("ParallaxScale"),
            F.UInt8("Passes"), F.UInt8("Flags"), F.Padding(2),
            F.ColorArgb("Color"))
        {
            Description = "Decal Data"
        };

        // SNDD - Sound Data (36 bytes)
        schemas[new SubrecordSchemaRegistry.SchemaKey("SNDD", "SOUN", 36)] = new SubrecordSchema(
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
        schemas[new SubrecordSchemaRegistry.SchemaKey("DNAM", "IMGS")] = SubrecordSchema.FloatArray;

        // DNAM - PWAT (8 bytes)
        schemas[new SubrecordSchemaRegistry.SchemaKey("DNAM", "PWAT", 8)] = new SubrecordSchema(F.Float("Value1"), F.Float("Value2"))
        {
            Description = "Placeable Water Data"
        };

        schemas[new SubrecordSchemaRegistry.SchemaKey("DNAM", "VTYP", 1)] = SubrecordSchema.ByteArray;

        // DATA - ANIO (4 bytes)
        schemas[new SubrecordSchemaRegistry.SchemaKey("DATA", "ANIO", 4)] = new SubrecordSchema(F.FormId("Animation"))
        {
            Description = "Animation FormID"
        };

        // DATA - GMST (4 bytes)
        schemas[new SubrecordSchemaRegistry.SchemaKey("DATA", "GMST", 4)] = SubrecordSchema.Simple4Byte();
        schemas[new SubrecordSchemaRegistry.SchemaKey("DATA", "GMST")] = SubrecordSchema.ByteArray;

        // DATA - CDCK (4 bytes)
        schemas[new SubrecordSchemaRegistry.SchemaKey("DATA", "CDCK", 4)] = new SubrecordSchema(F.UInt32("Count"))
        {
            Description = "Caravan Deck Count"
        };

        // DATA - CCRD (4 bytes)
        schemas[new SubrecordSchemaRegistry.SchemaKey("DATA", "CCRD", 4)] = new SubrecordSchema(F.UInt32("Value"))
        {
            Description = "Caravan Card Value"
        };

        // DATA - REPU (4 bytes)
        schemas[new SubrecordSchemaRegistry.SchemaKey("DATA", "REPU", 4)] = new SubrecordSchema(F.Float("Value"))
        {
            Description = "Reputation Value"
        };

        // DATA - REPU (8 bytes = 2 floats)
        schemas[new SubrecordSchemaRegistry.SchemaKey("DATA", "REPU", 8)] = new SubrecordSchema(
            F.Float("PositiveValue"), F.Float("NegativeValue"))
        {
            Description = "Reputation Data"
        };

        // DATA - CMNY (4 bytes)
        schemas[new SubrecordSchemaRegistry.SchemaKey("DATA", "CMNY", 4)] = new SubrecordSchema(F.UInt32("AbsoluteValue"))
        {
            Description = "Caravan Money Value"
        };

        // DATA - CSNO (56 bytes)
        schemas[new SubrecordSchemaRegistry.SchemaKey("DATA", "CSNO", 56)] = new SubrecordSchema(
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
        schemas[new SubrecordSchemaRegistry.SchemaKey("DATA", "DEHY", 8)] = new SubrecordSchema(
            F.UInt32("TriggerThreshold"),
            F.FormId("ActorEffect"))
        {
            Description = "Dehydration Stage Data"
        };

        schemas[new SubrecordSchemaRegistry.SchemaKey("DATA", "HUNG", 8)] = new SubrecordSchema(
            F.UInt32("TriggerThreshold"),
            F.FormId("ActorEffect"))
        {
            Description = "Hunger Stage Data"
        };

        schemas[new SubrecordSchemaRegistry.SchemaKey("DATA", "RADS", 8)] = new SubrecordSchema(
            F.UInt32("TriggerThreshold"),
            F.FormId("ActorEffect"))
        {
            Description = "Radiation Stage Data"
        };

        schemas[new SubrecordSchemaRegistry.SchemaKey("DATA", "SLPD", 8)] = new SubrecordSchema(
            F.UInt32("TriggerThreshold"),
            F.FormId("ActorEffect"))
        {
            Description = "Sleep Deprivation Stage Data"
        };

        // DATA - HAIR/HDPT/EYES (1 byte)
        schemas[new SubrecordSchemaRegistry.SchemaKey("DATA", "HAIR", 1)] = SubrecordSchema.ByteArray;
        schemas[new SubrecordSchemaRegistry.SchemaKey("DATA", "HDPT", 1)] = SubrecordSchema.ByteArray;
        schemas[new SubrecordSchemaRegistry.SchemaKey("DATA", "EYES", 1)] = SubrecordSchema.ByteArray;
        schemas[new SubrecordSchemaRegistry.SchemaKey("DATA", "MSTT", 1)] = SubrecordSchema.ByteArray;

        // MSET (Media Set) schemas
        schemas[new SubrecordSchemaRegistry.SchemaKey("NAM1", "MSET", 4)] = SubrecordSchema.Simple4Byte("Music Track FormID");
        schemas[new SubrecordSchemaRegistry.SchemaKey("NAM8", "MSET", 4)] = SubrecordSchema.Simple4Byte("Music Track FormID");
        schemas[new SubrecordSchemaRegistry.SchemaKey("NAM9", "MSET", 4)] = SubrecordSchema.Simple4Byte("Music Track FormID");
        schemas[new SubrecordSchemaRegistry.SchemaKey("NAM0", "MSET", 4)] = SubrecordSchema.Simple4Byte("Music Track FormID");
        schemas[new SubrecordSchemaRegistry.SchemaKey("ANAM", "MSET", 4)] = SubrecordSchema.Simple4Byte("MSET FormID");
        schemas[new SubrecordSchemaRegistry.SchemaKey("BNAM", "MSET", 4)] = SubrecordSchema.Simple4Byte("MSET FormID");
        schemas[new SubrecordSchemaRegistry.SchemaKey("CNAM", "MSET", 4)] = SubrecordSchema.Simple4Byte("MSET FormID");
        schemas[new SubrecordSchemaRegistry.SchemaKey("JNAM", "MSET", 4)] = SubrecordSchema.Simple4Byte("MSET FormID");
        schemas[new SubrecordSchemaRegistry.SchemaKey("KNAM", "MSET", 4)] = SubrecordSchema.Simple4Byte("MSET FormID");
        schemas[new SubrecordSchemaRegistry.SchemaKey("LNAM", "MSET", 4)] = SubrecordSchema.Simple4Byte("MSET FormID");
        schemas[new SubrecordSchemaRegistry.SchemaKey("MNAM", "MSET", 4)] = SubrecordSchema.Simple4Byte("MSET FormID");
        schemas[new SubrecordSchemaRegistry.SchemaKey("NNAM", "MSET", 4)] = SubrecordSchema.Simple4Byte("MSET FormID");
        schemas[new SubrecordSchemaRegistry.SchemaKey("ONAM", "MSET", 4)] = SubrecordSchema.Simple4Byte("MSET FormID");
        schemas[new SubrecordSchemaRegistry.SchemaKey("DNAM", "MSET", 4)] = SubrecordSchema.Simple4Byte("MSET FormID");
        schemas[new SubrecordSchemaRegistry.SchemaKey("ENAM", "MSET", 4)] = SubrecordSchema.Simple4Byte("MSET FormID");
        schemas[new SubrecordSchemaRegistry.SchemaKey("FNAM", "MSET", 4)] = SubrecordSchema.Simple4Byte("MSET FormID");
        schemas[new SubrecordSchemaRegistry.SchemaKey("GNAM", "MSET", 4)] = SubrecordSchema.Simple4Byte("MSET FormID");
        schemas[new SubrecordSchemaRegistry.SchemaKey("DATA", "MSET", 0)] = SubrecordSchema.Empty;

        // ALOC (Media Location Controller) schemas
        schemas[new SubrecordSchemaRegistry.SchemaKey("NAM1", "ALOC", 4)] = SubrecordSchema.Simple4Byte("Location Controller FormID");
        schemas[new SubrecordSchemaRegistry.SchemaKey("NAM2", "ALOC", 4)] = SubrecordSchema.Simple4Byte("Location Controller FormID");
        schemas[new SubrecordSchemaRegistry.SchemaKey("NAM3", "ALOC", 4)] = SubrecordSchema.Simple4Byte("Location Controller FormID");
        schemas[new SubrecordSchemaRegistry.SchemaKey("NAM4", "ALOC", 4)] = SubrecordSchema.Simple4Byte("Location Controller FormID");
        schemas[new SubrecordSchemaRegistry.SchemaKey("NAM5", "ALOC", 4)] = SubrecordSchema.Simple4Byte("Location Controller FormID");
        schemas[new SubrecordSchemaRegistry.SchemaKey("NAM6", "ALOC", 4)] = SubrecordSchema.Simple4Byte("Location Controller FormID");
        schemas[new SubrecordSchemaRegistry.SchemaKey("NAM7", "ALOC", 4)] = SubrecordSchema.Simple4Byte("Location Controller FormID");
        schemas[new SubrecordSchemaRegistry.SchemaKey("GNAM", "ALOC", 4)] = SubrecordSchema.Simple4Byte("Location Controller FormID");
        schemas[new SubrecordSchemaRegistry.SchemaKey("LNAM", "ALOC", 4)] = SubrecordSchema.Simple4Byte("Location Controller FormID");
        schemas[new SubrecordSchemaRegistry.SchemaKey("HNAM", "ALOC", 4)] = new SubrecordSchema(F.FormId("Location Controller FormID"));
        schemas[new SubrecordSchemaRegistry.SchemaKey("ZNAM", "ALOC", 4)] = new SubrecordSchema(F.FormId("Location Controller FormID"));
        schemas[new SubrecordSchemaRegistry.SchemaKey("XNAM", "ALOC", 4)] = new SubrecordSchema(F.FormId("Location Controller FormID"));
        schemas[new SubrecordSchemaRegistry.SchemaKey("YNAM", "ALOC", 4)] = new SubrecordSchema(F.FormId("Location Controller FormID"));
        schemas[new SubrecordSchemaRegistry.SchemaKey("RNAM", "ALOC", 4)] = new SubrecordSchema(F.FormId("Location Controller FormID"));
        schemas[new SubrecordSchemaRegistry.SchemaKey("FNAM", "ALOC", 4)] = SubrecordSchema.Simple4Byte("Location Controller Flags");

        // Other misc schemas
        schemas[new SubrecordSchemaRegistry.SchemaKey("SNAM", "ACTI", 4)] = SubrecordSchema.Simple4Byte("Sound FormID");
        schemas[new SubrecordSchemaRegistry.SchemaKey("SNAM", "ASPC", 4)] = SubrecordSchema.Simple4Byte("Sound FormID");
        schemas[new SubrecordSchemaRegistry.SchemaKey("SNAM", "TACT", 4)] = SubrecordSchema.Simple4Byte("Sound FormID");
        schemas[new SubrecordSchemaRegistry.SchemaKey("SNAM", "DOOR", 4)] = SubrecordSchema.Simple4Byte("Sound FormID");
        schemas[new SubrecordSchemaRegistry.SchemaKey("SNAM", "MSTT", 4)] = SubrecordSchema.Simple4Byte("Sound FormID");
    }
}
