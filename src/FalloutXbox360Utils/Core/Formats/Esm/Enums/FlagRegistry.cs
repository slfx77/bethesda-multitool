namespace FalloutXbox360Utils.Core.Formats.Esm.Enums;

/// <summary>
///     A single named bit within a flags field.
/// </summary>
public readonly record struct FlagBit(uint Mask, string Name);

/// <summary>
///     Centralized registry of bitfield flag definitions for ESM record types.
///     Used by EsmBrowserTreeBuilder to decode raw flag uint values into
///     human-readable GECK-style flag names.
/// </summary>
public static class FlagRegistry
{
    /// <summary>
    ///     Decode a flags value into a comma-separated list of set flag names.
    ///     Returns "None" if no recognized bits are set.
    /// </summary>
    public static string DecodeFlagNames(uint value, FlagBit[] definitions)
    {
        if (value == 0)
        {
            return "None";
        }

        var names = new List<string>(4);
        foreach (var def in definitions)
        {
            if ((value & def.Mask) != 0)
            {
                names.Add(def.Name);
            }
        }

        return names.Count > 0 ? string.Join(", ", names) : $"0x{value:X8}";
    }

    /// <summary>
    ///     Decode a flags value into a formatted string showing both names and hex value.
    ///     Format: "Name1, Name2 (0x0003)"
    /// </summary>
    public static string DecodeFlagNamesWithHex(uint value, FlagBit[] definitions)
    {
        if (value == 0)
        {
            return "None";
        }

        var names = DecodeFlagNames(value, definitions);
        return $"{names} (0x{value:X4})";
    }

    // ================================================================
    // ACBS - Actor Base Stats (NPC_ and CREA records)
    // ================================================================

    public static readonly FlagBit[] ActorBaseFlags =
    [
        new(0x00000001, "Female"),
        new(0x00000002, "Essential"),
        new(0x00000004, "Is CharGen Face Preset"),
        new(0x00000008, "Respawn"),
        new(0x00000010, "Auto-calc Stats"),
        new(0x00000040, "PC Level Mult"),
        new(0x00000080, "Use Template"),
        new(0x00000100, "No Low Level Processing"),
        new(0x00000200, "No Blood Spray"),
        new(0x00000400, "No Blood Decal"),
        new(0x00000800, "No Head"),
        new(0x00001000, "No Right Arm"),
        new(0x00002000, "No Left Arm"),
        new(0x00004000, "No Combat in Water"),
        new(0x00008000, "No Shadow"),
        new(0x00020000, "No VATS Melee"),
        new(0x00040000, "Can Be All Races"),
        new(0x00080000, "No Open Door"),
        new(0x00100000, "Immobile"),
        new(0x00200000, "Tilt Front/Back"),
        new(0x00400000, "Tilt Left/Right"),
        new(0x01000000, "No Knockdowns"),
        new(0x02000000, "Not Pushable"),
        new(0x08000000, "No Rotating to Head-Track"),
        new(0x40000000, "No Perception Condition"),
    ];

    // ================================================================
    // ACBS - Template Use Flags
    // ================================================================

    public static readonly FlagBit[] TemplateUseFlags =
    [
        new(0x0001, "Use Traits"),
        new(0x0002, "Use Stats"),
        new(0x0004, "Use Factions"),
        new(0x0008, "Use Actor Effect List"),
        new(0x0010, "Use AI Data"),
        new(0x0020, "Use AI Packages"),
        new(0x0040, "Use Model/Animation"),
        new(0x0080, "Use Base Data"),
        new(0x0100, "Use Inventory"),
        new(0x0200, "Use Script"),
    ];

    // ================================================================
    // FACT - Faction Flags
    // ================================================================

    public static readonly FlagBit[] FactionFlags =
    [
        new(0x0001, "Hidden from PC"),
        new(0x0002, "Evil"),
        new(0x0004, "Special Combat"),
        new(0x0040, "Track Crime"),
        new(0x0080, "Allow Sell"),
        new(0x4000, "Can Be Owner"),
    ];

    // ================================================================
    // CLAS - Class Flags
    // ================================================================

    public static readonly FlagBit[] ClassFlags =
    [
        new(0x01, "Playable"),
        new(0x02, "Guard"),
    ];

    // ================================================================
    // CLAS - Barter/Service Flags
    // ================================================================

    public static readonly FlagBit[] BarterFlags =
    [
        new(0x0001, "Weapons"),
        new(0x0002, "Armor"),
        new(0x0004, "Alcohol"),
        new(0x0008, "Food"),
        new(0x0010, "Chems"),
        new(0x0020, "Stimpacks"),
        new(0x0040, "Lights"),
        new(0x0100, "Misc"),
        new(0x0400, "Magic Items"),
        new(0x0800, "Potions"),
        new(0x1000, "Training"),
        new(0x2000, "Recharge"),
        new(0x4000, "Repair"),
    ];

    // ================================================================
    // RACE - Data Flags
    // ================================================================

    public static readonly FlagBit[] RaceDataFlags =
    [
        new(0x01, "Playable"),
        new(0x02, "FaceGen Head"),
        new(0x04, "Child"),
        new(0x08, "Tilt Front/Back"),
        new(0x10, "Tilt Left/Right"),
        new(0x20, "No Shadow"),
        new(0x40, "Swims"),
        new(0x80, "Flies"),
        new(0x100, "Walks"),
        new(0x200, "Immobile"),
        new(0x400, "Not Pushable"),
        new(0x800, "No Combat in Water"),
        new(0x1000, "No Rotating to Head-Track"),
    ];

    // ================================================================
    // MGEF - Base Effect Flags
    // ================================================================

    public static readonly FlagBit[] BaseEffectFlags =
    [
        new(0x00000001, "Hostile"),
        new(0x00000002, "Recover"),
        new(0x00000004, "Detrimental"),
        new(0x00000040, "No Duration"),
        new(0x00000080, "No Magnitude"),
        new(0x00000100, "No Area"),
        new(0x00000200, "FX Persist"),
        new(0x00000800, "Gory Visuals"),
        new(0x00001000, "Display Name Only"),
        new(0x00010000, "Use Skill"),
        new(0x00020000, "Use Attribute"),
        new(0x00080000, "Painless"),
        new(0x00100000, "Spray Projectile Type"),
        new(0x00200000, "Bolt Projectile Type"),
        new(0x00400000, "No Hit Effect"),
        new(0x00800000, "No Death Dispel"),
    ];

    // ================================================================
    // EXPL - Explosion Flags
    // ================================================================

    public static readonly FlagBit[] ExplosionFlags =
    [
        new(0x0001, "Always Uses World Orientation"),
        new(0x0002, "Knock Down - Always"),
        new(0x0004, "Knock Down - By Formula"),
        new(0x0008, "Ignore LOS Check"),
        new(0x0010, "Push Explosion Source Ref Only"),
        new(0x0020, "Ignore Image Space Swap"),
    ];

    // ================================================================
    // PROJ - Projectile Flags
    // ================================================================

    public static readonly FlagBit[] ProjectileFlags =
    [
        new(0x0001, "Hitscan"),
        new(0x0002, "Explosion"),
        new(0x0004, "Alt Trigger"),
        new(0x0008, "Muzzle Flash"),
        new(0x0020, "Can Be Disabled"),
        new(0x0040, "Can Be Picked Up"),
        new(0x0080, "Supersonic"),
        new(0x0100, "Pins Limbs"),
        new(0x0200, "Pass Through Small Transparent"),
        new(0x0400, "Detonates"),
        new(0x0800, "Rotation"),
    ];

    // ================================================================
    // SPEL - Spell Flags
    // ================================================================

    public static readonly FlagBit[] SpellFlags =
    [
        new(0x01, "No Auto-Calc"),
        new(0x04, "Immune to Silence"),
        new(0x10, "Area Effect Ignores LOS"),
        new(0x20, "Script Effect Always Applies"),
        new(0x40, "Disallow Spell Absorb/Reflect"),
        new(0x80, "Touch Explodes Without Target"),
    ];

    // ================================================================
    // ENCH - Enchantment Flags
    // ================================================================

    public static readonly FlagBit[] EnchantmentFlags =
    [
        new(0x01, "No Auto-Calc"),
    ];

    // ================================================================
    // QUST - Quest Flags
    // ================================================================

    public static readonly FlagBit[] QuestFlags =
    [
        new(0x01, "Start Game Enabled"),
        new(0x04, "Allow Repeated Conversation Topics"),
        new(0x08, "Allow Repeated Stages"),
    ];

    // ================================================================
    // MESG - Message Flags
    // ================================================================

    public static readonly FlagBit[] MessageFlags =
    [
        new(0x01, "Message Box"),
        new(0x02, "Auto Display"),
    ];

    // ================================================================
    // CHAL - Challenge Flags
    // ================================================================

    public static readonly FlagBit[] ChallengeFlags =
    [
        new(0x01, "Start Disabled"),
        new(0x02, "Recurring"),
        new(0x04, "Show Zero Progress"),
    ];

    // ================================================================
    // CELL - Cell Flags
    // ================================================================

    public static readonly FlagBit[] CellFlags =
    [
        new(0x01, "Is Interior Cell"),
        new(0x02, "Has Water"),
        new(0x04, "No Travel (Invert Fast Travel)"),
        new(0x08, "No LOD Water"),
        new(0x20, "Public Place"),
        new(0x40, "Hand Changed"),
        new(0x80, "Behave Like Exterior"),
    ];

    // ================================================================
    // CONT - Container Flags
    // ================================================================

    public static readonly FlagBit[] ContainerFlags =
    [
        new(0x01, "Allow Sounds When Animation"),
        new(0x02, "Respawns"),
        new(0x04, "Show Owner"),
    ];

    // ================================================================
    // BOOK - Book Flags
    // ================================================================

    public static readonly FlagBit[] BookFlags =
    [
        new(0x01, "Teaches Skill"),
        new(0x02, "Can't Be Taken"),
    ];

    // ================================================================
    // AMMO - Ammo Flags
    // ================================================================

    public static readonly FlagBit[] AmmoFlags =
    [
        new(0x01, "Ignores Normal Weapon Resistance"),
        new(0x02, "Non-Playable"),
    ];

    // ================================================================
    // LVLI/LVLN/LVLC - Leveled List Flags
    // ================================================================

    public static readonly FlagBit[] LeveledListFlags =
    [
        new(0x01, "Calculate from All Levels"),
        new(0x02, "Calculate for Each Item"),
        new(0x04, "Use All"),
    ];

    // ================================================================
    // TERM - Terminal Flags
    // ================================================================

    public static readonly FlagBit[] TerminalFlags =
    [
        new(0x01, "Leveled"),
        new(0x02, "Unlocked"),
        new(0x04, "Alternate Colors"),
        new(0x08, "Force Redraw"),
    ];
}
