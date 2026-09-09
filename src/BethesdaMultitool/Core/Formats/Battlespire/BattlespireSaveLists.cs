// The lists below are implemented from the prose of UESP's "Mod:Battlespire/Save Game Format"
// (https://en.uesp.net/wiki/Mod:Battlespire/Save_Game_Format), which this repo treats as a spec.
// No code was ported. The off-by-one conventions noted on each list were MEASURED on the
// 2026-09-07 SAVE0 fixture, not taken from the page.

namespace BethesdaMultitool.Core.Formats.Battlespire;

/// <summary>
///     The enumerations Battlespire's save records index by number: item types, enemies, skills,
///     spells, races, spell schools and the conversation-variable hash table.
///     <para>
///         ⚑ <b>Measured on SAVE0 (2026-09-07):</b> <see cref="ItemNames" /> relates to the stored
///         item name in three ways over the 323 Item records — 275 equal outright, 31 are the
///         SINGULAR of a plural entry (Greave/Greaves 10, Gauntlet/Gauntlets 8, Pauldron/Pauldrons 7,
///         Boot/Boots 6) and 17 are id 19 (Clothes), whose name is the BSI sub-type (Arm Bands /
///         Shirt / Pants / Cape) — 323/323 once both rules are applied. ⚠ An earlier revision said
///         "306/323 outright, the other 17 all Clothes"; 306 is the equal-or-singular total, and the
///         singular rule holds for 31 real records. <see cref="EnemyNames" /> agrees with the
///         Monster name on 97/97 (0 Scamp, 1 Vermai, 2 Dremora).
///     </para>
///     <para>
///         ⚠ <b>Spells are stored ONE-BASED.</b> A Spell record named "Cure Health" stores 21 at
///         +97 and 20 at +99 (its icon); "Teleport" stores 25 / 24. UESP's list has Cure Health at 20
///         and Teleport at 24, so the stored id is <c>list + 1</c> and the icon is the list value.
///         Use <see cref="StoredSpellName" /> for a stored id and <see cref="SpellName" /> for a list
///         index — mixing them names the wrong spell every time.
///     </para>
///     <para>
///         ⚠ The StaticEnemy block in SAVEVARS stores the enemy the same way, <c>list + 1</c>,
///         because 0 there means "random enemy" in the level BS6 (UESP's own note).
///     </para>
/// </summary>
internal static class BattlespireSaveLists
{
    /// <summary>UESP ItemList — the u16 at Item +97 indexes it directly (0-based).</summary>
    public static readonly string[] ItemNames =
    [
        "Dagger", "Short Sword", "Long Sword", "Broad Sword", "Claymore", "Battle Axe", "War Axe",
        "Mace", "Short Bow", "Long Bow", "Crossbow", "Javelin", "Sling", "Staff", "Spear", "Potion",
        "Sack", "Large Chest", "Small Chest", "Clothes", "Gem", "Scroll", "Arrow", "Lantern",
        "Helmet", "Cuirass", "Pauldrons", "Greaves", "Boots", "Gauntlets", "Voidguide",
        "Sigil Amulet", "Book", "Gold Pieces", "Key", "Gatekey", "Cog", "Rod", "Gatekey",
        "Parchment", "Spear Case", "Gatekey", "Coffer of Restoration"
    ];

    /// <summary>UESP EnemyList — the u32 at Player/Monster +655 indexes it directly (0-based).</summary>
    public static readonly string[] EnemyNames =
    [
        "Scamp", "Vermai", "Dremora", "Spider Daedra", "Skeleton", "Ghost", "Wraith",
        "Morphoid Daedra", "Fire Daedra", "Frost Daedra", "Herne", "Clannfear", "Seducer",
        "Dark Seducer", "Daedra Count", "Daedra Lord"
    ];

    /// <summary>UESP SkillList, in the order the 21 (value, useCount, base) triplets sit in the record.</summary>
    public static readonly string[] SkillNames =
    [
        "Medical", "Jumping", "Lockpicking", "Stealth", "Swimming", "Backstabbing", "Dodging",
        "Running", "Destruction", "Restoration", "Illusion", "Alteration", "Thaumaturgy",
        "Mysticism", "Short Blade", "Long Blade", "Hand to Hand", "Axe", "Blunt Weapon",
        "Missile", "Critical Strike"
    ];

    /// <summary>
    ///     UESP SpellList (0-based). Slots 31-37 are unused; 38 and 39 are the potion/enchantment-only
    ///     effects Water Breathing and Restore Spell Points.
    /// </summary>
    public static readonly string?[] SpellNames =
    [
        "Monster Summoning", "Detect Spell", "Detect Enemy", "Detect Invisibility", "Invisibility",
        "Shadow", "Chameleon", "Slow Fall", "Continuous Damage", "Poison", "Confusion",
        "Vampiric Drain", "Delayed Damage", "Dispel Magic", "Spell Reflection", "Spell Resistance",
        "Spell Absorption", "Cause Damage", "Fire Shield", "Cure Poison", "Cure Health", "Jumping",
        "Running", "Etherealness", "Teleport", "Shield", "Resistance", "Slow", "Haste", "Strength",
        "Dispel Sigil", null, null, null, null, null, null, null, "Water Breathing",
        "Restore Spell Points"
    ];

    /// <summary>UESP RaceList — the u8 at Player +670; 1 (Breton) on the fixture.</summary>
    public static readonly string[] RaceNames =
    [
        "Redguard", "Breton", "Nord", "High Elf", "Dark Elf", "Wood Elf"
    ];

    /// <summary>UESP SpellSchoolList — the u32 at Spell +168.</summary>
    public static readonly string[] SpellSchoolNames =
    [
        "Alteration", "Restoration", "Destruction", "Mysticism", "Thaumaturgy", "Illusion"
    ];

    /// <summary>
    ///     UESP VariableList: the conversation-variable hashes the Global/LocalVariable blocks carry.
    ///     The hash is not reversible, so this table is the only way to a name. All 28 hashes on the
    ///     fixture (26 global + 2 local) resolve here; conversation-branch identifiers that some
    ///     conversations also set are NOT listed (UESP declines to, citing collisions).
    /// </summary>
    public static readonly IReadOnlyDictionary<uint, string> VariableNames = new Dictionary<uint, string>
    {
        [18276u] = "Cat",
        [18389u] = "Bye",
        [18743u] = "Dog",
        [20177u] = "Iya",
        [292469u] = "Case",
        [293507u] = "Cess",
        [313967u] = "Hero",
        [327059u] = "Kids",
        [4616770u] = "Armor",
        [4654613u] = "Blame",
        [4667012u] = "Boast",
        [4692121u] = "Buddy",
        [5533085u] = "Payem",
        [5785954u] = "Spear",
        [5788734u] = "Spoon",
        [5795229u] = "Tayem",
        [75873186u] = "Dagger",
        [78045524u] = "FayMad",
        [78149207u] = "FDBrag",
        [79066711u] = "FrBrag",
        [79307092u] = "GemMad",
        [83759266u] = "Killer",
        [88610309u] = "PCMale",
        [90827426u] = "Reiver",
        [92707077u] = "Tattle",
        [95839941u] = "WarAxe",
        [97431892u] = "XivMad",
        [294957597u] = "CallToView",
        [365125991u] = "ImagoAlly",
        [369456781u] = "Malacath",
        [430335651u] = "SumeerAlly",
        [430386079u] = "SumeerName",
        [430393896u] = "SumeerOpen",
        [511167899u] = "Mischief",
        [610655631u] = "DremoraDone",
        [610694879u] = "DremoraNice",
        [611635545u] = "MorphMad",
        [634955582u] = "ZenaideMad",
        [634957737u] = "ZenaideTwo",
        [709091800u] = "Neonymic",
        [727212442u] = "AngadaMad",
        [727213978u] = "AngadaSad",
        [778744043u] = "DeyaniraName",
        [881226311u] = "NotArmor",
        [949645999u] = "Nocturnal",
        [1032625632u] = "Voidguide",
        [1127534183u] = "DagonNoTalk",
        [1181540349u] = "DremoraSpeak",
        [1184929603u] = "Pauldron",
        [1202329098u] = "PCFemale",
        [1211909557u] = "CogPage",
        [1214710100u] = "DarkMad",
        [1214945907u] = "PCReturn",
        [1231678197u] = "NotReiver",
        [1236651630u] = "Egahirn",
        [1248696901u] = "FayDupe",
        [1251373396u] = "FearMad",
        [1256628564u] = "FireMad",
        [1256629340u] = "FirePal",
        [1268880421u] = "GemDone",
        [1295308882u] = "MorphNoTalk",
        [1300918700u] = "SpiderMad",
        [1330279004u] = "Journal",
        [1363579220u] = "LordMad",
        [1468311221u] = "Scourge",
        [1473909154u] = "JacielMad",
        [1531392340u] = "VornMad",
        [1558864153u] = "XivAlly",
        [1558869517u] = "XivCalm",
        [1569359031u] = "ZenaideName",
        [1651011965u] = "GatanasName",
        [1651013853u] = "GatanasNice",
        [1714054682u] = "WearAmuletD",
        [1714054686u] = "WearAmuletH",
        [1714054691u] = "WearAmuletM",
        [1714054693u] = "WearAmuletO",
        [1714054696u] = "WearAmuletR",
        [1714054699u] = "WearAmuletU",
        [1714054701u] = "WearAmuletW",
        [1714054704u] = "WearAmuletZ",
        [1721931941u] = "ClarenDone",
        [1727670897u] = "DarkEnemy",
        [1728630387u] = "DarkThink",
        [1767404608u] = "ReadBook",
        [1787327552u] = "RestBook",
        [1853655393u] = "RishDeal",
        [1853656720u] = "NoRishDeal",
        [1901998879u] = "VermaiNice",
        [1982148044u] = "ScampNice",
        [1982150367u] = "ScampOath",
        [2002932057u] = "ScampMad",
        [2050621348u] = "SeduceNoTalk",
        [2056806840u] = "ServeDagon",
        [2086714775u] = "WonshalaMad",
        [2090957236u] = "TalchelmMad",
        [2090957238u] = "TanchelmMad",
        [2107685754u] = "JacielFree",
        [2107714298u] = "JacielName",
        [2150925840u] = "SKNoTalk",
        [2179497472u] = "Dismissed2",
        [2225312088u] = "CountMad",
        [2250671626u] = "GatanasMad",
        [2301940191u] = "GhostNoTalk",
        [2335475216u] = "SVNoTalk",
        [2363664058u] = "RathineMad",
        [2490508909u] = "StormName",
        [2510188251u] = "ReadyRite",
        [2591555654u] = "SumeerServe",
        [2693120294u] = "SoulDagger",
        [2704575370u] = "SkelNoTalk",
        [2711253420u] = "SumeerMad",
        [2721351254u] = "GemNoTalk",
        [2722521035u] = "DremoraFoe",
        [2722522602u] = "DremoraMad",
        [2745032328u] = "CountYield",
        [2747749378u] = "ChimereJournal",
        [2799315501u] = "FayEnemy",
        [2800454890u] = "FayVsXiv",
        [2803229102u] = "VermaiMad",
        [2804526801u] = "HerneFite",
        [2828569565u] = "QuestStart",
        [2842078857u] = "FearFite",
        [3027445274u] = "VornName",
        [3074986328u] = "FrostMad",
        [3074987104u] = "FrostPal",
        [3123090845u] = "GemReady",
        [3311500540u] = "RishaalName",
        [3322669644u] = "WonshalaName",
        [3322694322u] = "WonshalaTask",
        [3331880893u] = "MethatsName",
        [3331882781u] = "MethatsNice",
        [3390511660u] = "TalchelmDone",
        [3390511692u] = "TanchelmDone",
        [3396510040u] = "HerneMad",
        [3458890877u] = "RathineName",
        [3458892765u] = "RathineNice",
        [3467258414u] = "XivEnemy",
        [3630343479u] = "DohtAmulet",
        [3642233908u] = "InvVoidguide1",
        [3647351483u] = "FaydraName",
        [3657892940u] = "JacielAwake",
        [3658994868u] = "JacielScram",
        [3764290303u] = "XivilaiName",
        [3780919640u] = "ImagoMad",
        [3894314972u] = "Dismissed",
        [4195854332u] = "SkelServe"
    };

    /// <summary>The item type name for an Item record's +97 id, or null when out of range.</summary>
    public static string? ItemName(int id)
    {
        return id >= 0 && id < ItemNames.Length ? ItemNames[id] : null;
    }

    /// <summary>The enemy name for a Monster record's +655 type, or null when out of range.</summary>
    public static string? EnemyName(int enemyType)
    {
        return enemyType >= 0 && enemyType < EnemyNames.Length ? EnemyNames[enemyType] : null;
    }

    /// <summary>The skill name for a SkillList index, or null when out of range.</summary>
    public static string? SkillName(int skill)
    {
        return skill >= 0 && skill < SkillNames.Length ? SkillNames[skill] : null;
    }

    /// <summary>The spell name for a 0-based SpellList index (an icon value), or null.</summary>
    public static string? SpellName(int listIndex)
    {
        return listIndex >= 0 && listIndex < SpellNames.Length ? SpellNames[listIndex] : null;
    }

    /// <summary>
    ///     The spell name for a STORED spell id — Spell +97, the hotkey bytes, a potion's +122 — which
    ///     is <c>list + 1</c>; 0 means none.
    /// </summary>
    public static string? StoredSpellName(int storedId)
    {
        return storedId <= 0 ? null : SpellName(storedId - 1);
    }

    /// <summary>The race name for Player +670, or null when out of range.</summary>
    public static string? RaceName(int race)
    {
        return race >= 0 && race < RaceNames.Length ? RaceNames[race] : null;
    }

    /// <summary>The conversation-variable name for a hash, or null when UESP does not list it.</summary>
    public static string? VariableName(uint hash)
    {
        return VariableNames.TryGetValue(hash, out var name) ? name : null;
    }
}
