namespace FalloutXbox360Utils.Core.Formats.SaveGame;

/// <summary>
///     Defines a single change flag: a bitmask and its human-readable name.
/// </summary>
public readonly record struct ChangeFlagDef(uint Mask, string Name);

/// <summary>
///     Maps each ChangedForm type to its applicable change flag definitions.
///     Flag values and names sourced from the CHANGE_TYPE enum in the game PDB
///     (Fallout_Debug_Final/types_full.txt, lines 301695-301767).
///     Flags are type-contextual: the same bit position means different things
///     depending on the ChangeType byte of the changed form record.
/// </summary>
public static class ChangeFlagRegistry
{
    // ── Shared flag bits (used across multiple type groups) ──

    private static readonly ChangeFlagDef FormFlags = new(0x00000001, "FORM_FLAGS");

    // ── Reference/Object shared bits (bits 1-6, 26-31) ──

    private static readonly ChangeFlagDef RefrMove = new(0x00000002, "REFR_MOVE");
    private static readonly ChangeFlagDef HavokMove = new(0x00000004, "REFR_HAVOK_MOVE");
    private static readonly ChangeFlagDef CellChanged = new(0x00000008, "REFR_CELL_CHANGED");
    private static readonly ChangeFlagDef Scale = new(0x00000010, "REFR_SCALE");
    private static readonly ChangeFlagDef Inventory = new(0x00000020, "REFR_INVENTORY");
    private static readonly ChangeFlagDef ExtraOwnership = new(0x00000040, "REFR_EXTRA_OWNERSHIP");
    private static readonly ChangeFlagDef ExtraActivatingChildren = new(0x04000000, "REFR_EXTRA_ACTIVATING_CHILDREN");
    private static readonly ChangeFlagDef LeveledInventory = new(0x08000000, "REFR_LEVELED_INVENTORY");
    private static readonly ChangeFlagDef Animation = new(0x10000000, "REFR_ANIMATION");
    private static readonly ChangeFlagDef ExtraEncounterZone = new(0x20000000, "REFR_EXTRA_ENCOUNTER_ZONE");
    private static readonly ChangeFlagDef ExtraCreatedOnly = new(0x40000000, "REFR_EXTRA_CREATED_ONLY");
    private static readonly ChangeFlagDef ExtraGameOnly = new(0x80000000, "REFR_EXTRA_GAME_ONLY");

    // ── Object-specific overlay (bits 10-12, 17, 21-23) ──

    private static readonly ChangeFlagDef ObjectExtraItemData = new(0x00000400, "OBJECT_EXTRA_ITEM_DATA");
    private static readonly ChangeFlagDef ObjectExtraAmmo = new(0x00000800, "OBJECT_EXTRA_AMMO");
    private static readonly ChangeFlagDef ObjectExtraLock = new(0x00001000, "OBJECT_EXTRA_LOCK");
    private static readonly ChangeFlagDef DoorExtraTeleport = new(0x00020000, "DOOR_EXTRA_TELEPORT");
    private static readonly ChangeFlagDef ObjectEmpty = new(0x00200000, "OBJECT_EMPTY");
    private static readonly ChangeFlagDef ObjectOpenDefaultState = new(0x00400000, "OBJECT_OPEN_DEFAULT_STATE");
    private static readonly ChangeFlagDef ObjectOpenState = new(0x00800000, "OBJECT_OPEN_STATE");

    // ── Actor-specific overlay (bits 10-12, 17-23) ──

    private static readonly ChangeFlagDef ActorLifestate = new(0x00000400, "ACTOR_LIFESTATE");
    private static readonly ChangeFlagDef ActorExtraPackageData = new(0x00000800, "ACTOR_EXTRA_PACKAGE_DATA");

    private static readonly ChangeFlagDef ActorExtraMerchantContainer =
        new(0x00001000, "ACTOR_EXTRA_MERCHANT_CONTAINER");

    private static readonly ChangeFlagDef ActorExtraDismemberedLimbs = new(0x00020000, "ACTOR_EXTRA_DISMEMBERED_LIMBS");
    private static readonly ChangeFlagDef ActorExtraLeveledActor = new(0x00040000, "ACTOR_EXTRA_LEVELED_ACTOR");
    private static readonly ChangeFlagDef ActorDispositionModifiers = new(0x00080000, "ACTOR_DISPOSITION_MODIFIERS");
    private static readonly ChangeFlagDef ActorTempModifiers = new(0x00100000, "ACTOR_TEMP_MODIFIERS");
    private static readonly ChangeFlagDef ActorDamageModifiers = new(0x00200000, "ACTOR_DAMAGE_MODIFIERS");
    private static readonly ChangeFlagDef ActorOverrideModifiers = new(0x00400000, "ACTOR_OVERRIDE_MODIFIERS");
    private static readonly ChangeFlagDef ActorPermanentModifiers = new(0x00800000, "ACTOR_PERMANENT_MODIFIERS");

    // ── Quest flags ──

    private static readonly ChangeFlagDef QuestFlags = new(0x00000002, "QUEST_FLAGS");
    private static readonly ChangeFlagDef QuestScriptDelay = new(0x00000004, "QUEST_SCRIPT_DELAY");
    private static readonly ChangeFlagDef QuestObjectives = new(0x20000000, "QUEST_OBJECTIVES");
    private static readonly ChangeFlagDef QuestScript = new(0x40000000, "QUEST_SCRIPT");
    private static readonly ChangeFlagDef QuestStages = new(0x80000000, "QUEST_STAGES");

    // ── Cell flags ──

    private static readonly ChangeFlagDef CellFlags = new(0x00000002, "CELL_FLAGS");
    private static readonly ChangeFlagDef CellFullname = new(0x00000004, "CELL_FULLNAME");
    private static readonly ChangeFlagDef CellOwnership = new(0x00000008, "CELL_OWNERSHIP");
    private static readonly ChangeFlagDef CellExteriorShort = new(0x10000000, "CELL_EXTERIOR_SHORT");
    private static readonly ChangeFlagDef CellExteriorChar = new(0x20000000, "CELL_EXTERIOR_CHAR");
    private static readonly ChangeFlagDef CellDetachTime = new(0x40000000, "CELL_DETACHTIME");
    private static readonly ChangeFlagDef CellSeenData = new(0x80000000, "CELL_SEENDATA");

    // ── NPC/Creature base flags ──

    private static readonly ChangeFlagDef ActorBaseData = new(0x00000002, "ACTOR_BASE_DATA");
    private static readonly ChangeFlagDef ActorBaseAttributes = new(0x00000004, "ACTOR_BASE_ATTRIBUTES");
    private static readonly ChangeFlagDef ActorBaseAiData = new(0x00000008, "ACTOR_BASE_AIDATA");
    private static readonly ChangeFlagDef ActorBaseSpellList = new(0x00000010, "ACTOR_BASE_SPELLLIST");
    private static readonly ChangeFlagDef ActorBaseFullname = new(0x00000020, "ACTOR_BASE_FULLNAME");
    private static readonly ChangeFlagDef NpcSkills = new(0x00000200, "NPC_SKILLS");
    private static readonly ChangeFlagDef NpcClass = new(0x00000400, "NPC_CLASS");
    private static readonly ChangeFlagDef NpcFace = new(0x00000800, "NPC_FACE");
    private static readonly ChangeFlagDef NpcGender = new(0x01000000, "NPC_GENDER");
    private static readonly ChangeFlagDef NpcRace = new(0x02000000, "NPC_RACE");
    private static readonly ChangeFlagDef CreatureSkills = new(0x00000200, "CREATURE_SKILLS");

    // ── Faction flags ──

    private static readonly ChangeFlagDef FactionFlags = new(0x00000002, "FACTION_FLAGS");
    private static readonly ChangeFlagDef FactionReactions = new(0x00000004, "FACTION_REACTIONS");
    private static readonly ChangeFlagDef FactionCrimeCounts = new(0x80000000, "FACTION_CRIME_COUNTS");

    // ── Misc single-flag types ──

    private static readonly ChangeFlagDef TopicSaidOnce = new(0x80000000, "TOPIC_SAIDONCE");
    private static readonly ChangeFlagDef NoteRead = new(0x80000000, "NOTE_READ");
    private static readonly ChangeFlagDef PackageNeverRun = new(0x80000000, "PACKAGE_NEVER_RUN");
    private static readonly ChangeFlagDef PackageWaiting = new(0x40000000, "PACKAGE_WAITING");
    private static readonly ChangeFlagDef BaseObjectValue = new(0x00000002, "BASE_OBJECT_VALUE");
    private static readonly ChangeFlagDef BaseObjectFullname = new(0x00000004, "BASE_OBJECT_FULLNAME");
    private static readonly ChangeFlagDef BookTeachesSkill = new(0x00000020, "BOOK_TEACHES_SKILL");
    private static readonly ChangeFlagDef TalkingActivatorSpeaker = new(0x00800000, "TALKING_ACTIVATOR_SPEAKER");
    private static readonly ChangeFlagDef EncounterZoneFlags = new(0x00000002, "ENCOUNTER_ZONE_FLAGS");
    private static readonly ChangeFlagDef EncounterZoneGameData = new(0x80000000, "ENCOUNTER_ZONE_GAME_DATA");
    private static readonly ChangeFlagDef ClassTagSkills = new(0x00000002, "CLASS_TAG_SKILLS");
    private static readonly ChangeFlagDef ReputationValues = new(0x00000002, "REPUTATION_VALUES");
    private static readonly ChangeFlagDef ChallengeValue = new(0x00000002, "CHALLENGE_VALUE");
    private static readonly ChangeFlagDef FormListAddedForm = new(0x80000000, "FORM_LIST_ADDED_FORM");
    private static readonly ChangeFlagDef LeveledListAddedObject = new(0x80000000, "LEVELED_LIST_ADDED_OBJECT");
    private static readonly ChangeFlagDef WaterRemapped = new(0x80000000, "WATER_REMAPPED");

    // ── Precomputed flag arrays per type group ──

    private static readonly ChangeFlagDef[] RefrObjectFlags =
    [
        FormFlags, RefrMove, HavokMove, CellChanged, Scale, Inventory, ExtraOwnership,
        ObjectExtraItemData, ObjectExtraAmmo, ObjectExtraLock,
        DoorExtraTeleport, ObjectEmpty, ObjectOpenDefaultState, ObjectOpenState,
        ExtraActivatingChildren, LeveledInventory, Animation, ExtraEncounterZone,
        ExtraCreatedOnly, ExtraGameOnly
    ];

    private static readonly ChangeFlagDef[] ActorFlags =
    [
        FormFlags, RefrMove, HavokMove, CellChanged, Scale, Inventory, ExtraOwnership,
        ActorLifestate, ActorExtraPackageData, ActorExtraMerchantContainer,
        ActorExtraDismemberedLimbs, ActorExtraLeveledActor,
        ActorDispositionModifiers, ActorTempModifiers, ActorDamageModifiers,
        ActorOverrideModifiers, ActorPermanentModifiers,
        ExtraActivatingChildren, LeveledInventory, Animation, ExtraEncounterZone,
        ExtraCreatedOnly, ExtraGameOnly
    ];

    private static readonly ChangeFlagDef[] ProjectileFlags =
    [
        FormFlags, RefrMove, HavokMove, CellChanged, Scale, ExtraGameOnly
    ];

    private static readonly ChangeFlagDef[] CellFlagsArr =
    [
        FormFlags, CellFlags, CellFullname, CellOwnership,
        CellExteriorShort, CellExteriorChar, CellDetachTime, CellSeenData
    ];

    private static readonly ChangeFlagDef[] QuestFlagsArr =
    [
        FormFlags, QuestFlags, QuestScriptDelay, QuestObjectives, QuestScript, QuestStages
    ];

    private static readonly ChangeFlagDef[] NpcFlags =
    [
        FormFlags, ActorBaseData, ActorBaseAttributes, ActorBaseAiData,
        ActorBaseSpellList, ActorBaseFullname,
        NpcSkills, NpcClass, NpcFace, NpcGender, NpcRace
    ];

    private static readonly ChangeFlagDef[] CreatureFlags =
    [
        FormFlags, ActorBaseData, ActorBaseAttributes, ActorBaseAiData,
        ActorBaseSpellList, ActorBaseFullname, CreatureSkills
    ];

    private static readonly ChangeFlagDef[] FactionFlagsArr =
    [
        FormFlags, FactionFlags, FactionReactions, FactionCrimeCounts
    ];

    private static readonly ChangeFlagDef[] PackageFlags =
    [
        PackageWaiting, PackageNeverRun
    ];

    private static readonly ChangeFlagDef[] EncounterZoneArr =
    [
        EncounterZoneFlags, EncounterZoneGameData
    ];

    private static readonly ChangeFlagDef[] BaseObjectArr =
    [
        FormFlags, BaseObjectValue, BaseObjectFullname
    ];

    /// <summary>
    ///     Returns the ordered list of applicable change flag definitions for the given change type.
    ///     The order matches the serialization order in the game's SaveGame/LoadGame functions.
    /// </summary>
    public static ReadOnlySpan<ChangeFlagDef> GetFlags(byte changeType)
    {
        return changeType switch
        {
            0 => RefrObjectFlags, // REFR
            1 or 2 => ActorFlags, // ACHR, ACRE
            >= 3 and <= 6 => ProjectileFlags, // PMIS, PGRE, PBEA, PFLA
            7 => CellFlagsArr, // CELL
            8 => [TopicSaidOnce], // INFO
            9 => QuestFlagsArr, // QUST
            10 => NpcFlags, // NPC_
            11 => CreatureFlags, // CREA
            12 => BaseObjectWithSpeaker(), // ACTI
            13 => BaseObjectWithSpeaker(), // TACT (talking activator)
            14 or 15 or 17 or 18 or 19 or 20 or 21 or 22 or 23 or 24 or 25 or 27 or 28 or 29
                => BaseObjectArr, // TERM, ARMO, CLOT, CONT, DOOR, INGR, LIGH, MISC, STAT, MSTT, FURN, AMMO, KEYM, ALCH
            16 => BaseObjectWithBook(), // BOOK
            26 => BaseObjectArr, // WEAP
            30 => [], // IDLM - no known flags
            31 => [FormFlags, NoteRead], // NOTE
            32 => EncounterZoneArr, // ECZN
            33 => [ClassTagSkills], // CLAS
            34 => FactionFlagsArr, // FACT
            35 => PackageFlags, // PACK
            36 => [], // NAVM - no documented flags
            37 => [FormListAddedForm], // FLST
            38 or 39 or 40 => [LeveledListAddedObject], // LVLC, LVLN, LVLI
            41 => [WaterRemapped], // WATR
            42 => BaseObjectArr, // IMOD
            43 => [ReputationValues], // REPU
            44 => ProjectileFlags, // PCBE (player combat beam)
            45 or 46 => BaseObjectArr, // RCPE, RCCT
            47 or 48 or 49 => BaseObjectArr, // CHIP, CSNO, LSCT
            50 => [ChallengeValue], // CHAL
            51 => BaseObjectArr, // AMEF
            52 or 53 or 54 => BaseObjectArr, // CCRD, CMNY, CDCK
            _ => []
        };
    }

    private static ChangeFlagDef[] BaseObjectWithSpeaker()
    {
        return [FormFlags, BaseObjectValue, BaseObjectFullname, TalkingActivatorSpeaker];
    }

    private static ChangeFlagDef[] BaseObjectWithBook()
    {
        return [FormFlags, BaseObjectValue, BaseObjectFullname, BookTeachesSkill];
    }

    /// <summary>
    ///     Decodes a change flags uint32 into a list of active flag names for display.
    /// </summary>
    public static List<string> DescribeFlags(byte changeType, uint changeFlags)
    {
        var result = new List<string>();
        var defs = GetFlags(changeType);
        var remaining = changeFlags;

        foreach (var def in defs)
        {
            if ((changeFlags & def.Mask) != 0)
            {
                result.Add(def.Name);
                remaining &= ~def.Mask;
            }
        }

        // Report any unknown bits
        if (remaining != 0)
        {
            result.Add($"UNKNOWN(0x{remaining:X8})");
        }

        return result;
    }
}
