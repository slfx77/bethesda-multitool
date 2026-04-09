namespace PdbAnalyzer;

/// <summary>
///     Shared utility methods for PDB structure analysis commands.
/// </summary>
internal static class PdbAnalyzerHelpers
{
    /// <summary>
    ///     Maps ESM 4-letter record codes to their C++ class names in the PDB.
    ///     Built from PDB class hierarchy inspection and naming conventions.
    /// </summary>
    internal static string? GetClassNameForRecord(string recordCode)
    {
        return recordCode switch
        {
            // System / structural
            "NO_FORM" => null,
            "TES4" => "TESFile",
            "GRUP" => null, // Not a TESForm subclass

            // Core types
            "GMST" => "SettingT<GameSettingCollection>",
            "TXST" => "BGSTextureSet",
            "MICN" => "BGSMenuIcon",
            "GLOB" => "TESGlobal",
            "CLAS" => "TESClass",
            "FACT" => "TESFaction",
            "HDPT" => "BGSHeadPart",
            "HAIR" => "TESHair",
            "EYES" => "TESEyes",
            "RACE" => "TESRace",
            "SOUN" => "TESSound",
            "ASPC" => "BGSAcousticSpace",
            "SKIL" => "TESSkill",
            "MGEF" => "EffectSetting",
            "SCPT" => "Script",
            "LTEX" => "TESLandTexture",
            "ENCH" => "EnchantmentItem",
            "SPEL" => "SpellItem",
            "ACTI" => "TESObjectACTI",

            // Objects
            "TACT" => "BGSTalkingActivator",
            "TERM" => "BGSTerminal",
            "ARMO" => "TESObjectARMO",
            "BOOK" => "TESObjectBOOK",
            "CLOT" => "TESObjectCLOT",
            "CONT" => "TESObjectCONT",
            "DOOR" => "TESObjectDOOR",
            "INGR" => "IngredientItem",
            "LIGH" => "TESObjectLIGH",
            "MISC" => "TESObjectMISC",
            "STAT" => "TESObjectSTAT",
            "SCOL" => "BGSStaticCollection",
            "MSTT" => "BGSMovableStatic",
            "PWAT" => "BGSPlaceableWater",
            "GRAS" => "TESGrass",
            "TREE" => "TESObjectTREE",
            "FLOR" => "TESFlora",
            "FURN" => "TESFurniture",
            "WEAP" => "TESObjectWEAP",
            "AMMO" => "TESAmmo",
            "NPC_" => "TESNPC",
            "CREA" => "TESCreature",
            "LVLC" => "TESLevCharacter",
            "LVLN" => "TESLevCreature",
            "KEYM" => "TESKey",
            "ALCH" => "AlchemyItem",
            "IDLM" => "BGSIdleMarker",
            "NOTE" => "BGSNote",

            // Constructible / leveled
            "COBJ" => "BGSConstructibleObject",
            "PROJ" => "BGSProjectile",
            "LVLI" => "TESLevItem",

            // World / weather
            "WTHR" => "TESWeather",
            "CLMT" => "TESClimate",
            "REGN" => "TESRegion",
            "NAVI" => "NavMeshInfoMap",
            "CELL" => "TESObjectCELL",
            "REFR" => "TESObjectREFR",
            "ACHR" => "Character",
            "ACRE" => "Creature",
            "PMIS" => "MissileProjectile",
            "PGRE" => "GrenadeProjectile",
            "PBEA" => "BeamProjectile",
            "PFLA" => "FlameProjectile",
            "WRLD" => "TESWorldSpace",
            "LAND" => "TESLand",
            "NAVM" => "NavMesh",
            "TLOD" => "TESObjectLAND",

            // Dialog / quest
            "DIAL" => "TESTopic",
            "INFO" => "TESTopicInfo",
            "QUST" => "TESQuest",
            "IDLE" => "TESIdleForm",
            "PACK" => "TESPackage",

            // Combat / style
            "CSTY" => "TESCombatStyle",
            "LSCR" => "TESLoadScreen",
            "LVSP" => "TESLevSpell",
            "ANIO" => "TESObjectANIO",
            "WATR" => "TESWaterForm",
            "EFSH" => "TESEffectShader",
            "TOFT" => null, // TOFT streaming cache, not a standard class
            "EXPL" => "BGSExplosion",
            "DEBR" => "BGSDebris",

            // Image / effects
            "IMGS" => "TESImageSpace",
            "IMAD" => "TESImageSpaceModifier",
            "FLST" => "BGSListForm",
            "PERK" => "BGSPerk",
            "BPTD" => "BGSBodyPartData",
            "ADDN" => "BGSAddonNode",
            "AVIF" => "ActorValueInfo",
            "RADS" => "BGSRadiationStage",
            "CAMS" => "BGSCameraShot",
            "CPTH" => "BGSCameraPath",
            "VTYP" => "BGSVoiceType",
            "IPCT" => "BGSImpactData",
            "IPDS" => "BGSImpactDataSet",
            "ARMA" => "TESObjectARMA",
            "ECZN" => "BGSEncounterZone",
            "MESG" => "BGSMessage",
            "RGDL" => "BGSRagdoll",
            "DOBJ" => "BGSDefaultObjectManager",
            "LGTM" => "BGSLightingTemplate",
            "MUSC" => "BGSMusicType",

            // FNV-specific
            "IMOD" => "TESObjectIMOD",
            "REPU" => "TESReputation",
            "PCBE" => null, // Placed projectile beam — reference type, no dedicated class
            "RCPE" => "TESRecipe",
            "RCCT" => "TESRecipeCategory",
            "CHIP" => "TESCasinoChips",
            "CSNO" => "TESCasino",
            "LSCT" => "TESLoadScreenType",
            "MSET" => "MediaSet",
            "ALOC" => "MediaLocationController",
            "CHAL" => "TESChallenge",
            "AMEF" => "TESAmmoEffect",
            "CCRD" => "TESCaravanCard",
            "CMNY" => "TESCaravanMoney",
            "CDCK" => "TESCaravanDeck",
            "DEHY" => "BGSDehydrationStage",
            "HUNG" => "BGSHungerStage",
            "SLPD" => "BGSSleepDeprevationStage", // Note: typo in original C++ source

            _ => null
        };
    }

    internal static string CategorizeStructure(string name)
    {
        if (name.StartsWith("OBJ_WEAP")) return "Weapon Data";
        if (name.StartsWith("OBJ_")) return "Object Data (OBJ_*)";
        if (name.StartsWith("NPC_") || name.Contains("NPC")) return "NPC Data";
        if (name.StartsWith("CELL_") || name.Contains("Cell")) return "Cell Data";
        if (name.StartsWith("LAND_") || name.Contains("Land")) return "Land Data";
        if (name.StartsWith("NAVM") || name.Contains("NavM")) return "NavMesh Data";
        if (name is "FORM" or "CHUNK" or "FILE_HEADER") return "Core ESM";
        if (name.StartsWith("TESObject")) return "TESObject Classes";
        if (name.StartsWith("TESFile")) return "File Handling";
        if (name.StartsWith("TES") || name.StartsWith("BGS")) return "Bethesda Classes";
        return "Other";
    }

    internal static string SanitizeName(string name)
    {
        // Remove invalid characters, convert to PascalCase
        return name.Replace("<", "_").Replace(">", "_").Replace(",", "_").Replace(" ", "_")
            .Replace("*", "Ptr").Replace("::", "_");
    }

    internal static string SanitizeFieldName(string name)
    {
        // Ensure valid C# identifier
        if (char.IsDigit(name[0]))
            return "_" + name;
        if (name == "base" || name == "class" || name == "struct" || name == "event")
            return "@" + name;
        return name;
    }

    internal static string ConvertType(string pdbType)
    {
        return pdbType switch
        {
            "T_REAL32(0040)" => "float",
            "T_REAL64(0041)" => "double",
            "T_INT4(0074)" => "int",
            "T_INT2(0072)" => "short",
            "T_INT1(0068)" => "sbyte",
            "T_UINT4(0075)" => "uint",
            "T_UINT2(0073)" => "ushort",
            "T_UINT1(0069)" => "byte",
            "T_UCHAR(0020)" => "byte",
            "T_RCHAR(0070)" => "sbyte",
            "T_CHAR(0010)" => "byte",
            "T_USHORT(0021)" => "ushort",
            "T_SHORT(0011)" => "short",
            "T_ULONG(0022)" => "uint",
            "T_LONG(0012)" => "int",
            "T_BOOL08(0030)" => "byte", // bool as byte for marshaling
            "T_32PVOID(0403)" => "uint", // pointer as uint
            "T_32PRCHAR(0470)" => "uint", // char* as uint
            "T_NOTYPE(0000)" => "uint", // void as uint
            _ when pdbType.StartsWith("0x") => "uint", // Complex type reference - treat as uint for now
            _ => $"/* {pdbType} */ uint" // Unknown - mark and use uint
        };
    }
}
