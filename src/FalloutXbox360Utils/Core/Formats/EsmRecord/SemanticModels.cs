namespace FalloutXbox360Utils.Core.Formats.EsmRecord;

/// <summary>
///     Fully reconstructed NPC from memory dump.
///     Aggregates data from NPC_ main record header, EDID, FULL, ACBS, faction refs, etc.
/// </summary>
public record ReconstructedNpc
{
    /// <summary>FormID of the NPC record.</summary>
    public uint FormId { get; init; }

    /// <summary>Editor ID (e.g., "DocMitchell").</summary>
    public string? EditorId { get; init; }

    /// <summary>Display name (e.g., "Doc Mitchell").</summary>
    public string? FullName { get; init; }

    /// <summary>Actor base stats from ACBS subrecord.</summary>
    public ActorBaseSubrecord? Stats { get; init; }

    /// <summary>Race FormID (RNAM subrecord).</summary>
    public uint? Race { get; init; }

    /// <summary>Script FormID (SCRI subrecord).</summary>
    public uint? Script { get; init; }

    /// <summary>Class FormID (CNAM subrecord).</summary>
    public uint? Class { get; init; }

    /// <summary>Death item FormID (INAM subrecord).</summary>
    public uint? DeathItem { get; init; }

    /// <summary>Voice type FormID (VTCK subrecord).</summary>
    public uint? VoiceType { get; init; }

    /// <summary>Template FormID (TPLT subrecord).</summary>
    public uint? Template { get; init; }

    /// <summary>Faction memberships (SNAM subrecords).</summary>
    public List<FactionMembership> Factions { get; init; } = [];

    /// <summary>Spell/ability FormIDs (SPLO subrecords).</summary>
    public List<uint> Spells { get; init; } = [];

    /// <summary>Item inventory (CNTO subrecords).</summary>
    public List<InventoryItem> Inventory { get; init; } = [];

    /// <summary>AI package FormIDs (PKID subrecords).</summary>
    public List<uint> Packages { get; init; } = [];

    /// <summary>Offset in the dump where this record was found.</summary>
    public long Offset { get; init; }

    /// <summary>Whether the record was detected as big-endian (Xbox 360).</summary>
    public bool IsBigEndian { get; init; }
}

/// <summary>
///     Faction membership information from SNAM subrecord.
/// </summary>
public record FactionMembership(uint FactionFormId, sbyte Rank);

/// <summary>
///     Inventory item information from CNTO subrecord.
/// </summary>
public record InventoryItem(uint ItemFormId, int Count);

/// <summary>
///     Fully reconstructed Quest from memory dump.
///     Aggregates data from QUST main record header, stages, objectives, etc.
/// </summary>
public record ReconstructedQuest
{
    /// <summary>FormID of the quest record.</summary>
    public uint FormId { get; init; }

    /// <summary>Editor ID (e.g., "vDialogueGoodsprings").</summary>
    public string? EditorId { get; init; }

    /// <summary>Display name (e.g., "Ain't That a Kick in the Head").</summary>
    public string? FullName { get; init; }

    /// <summary>Quest flags from DATA subrecord.</summary>
    public byte Flags { get; init; }

    /// <summary>Quest priority from DATA subrecord.</summary>
    public byte Priority { get; init; }

    /// <summary>Quest script FormID (SCRI subrecord).</summary>
    public uint? Script { get; init; }

    /// <summary>Quest stages (INDX + log entries).</summary>
    public List<QuestStage> Stages { get; init; } = [];

    /// <summary>Quest objectives (QOBJ + display text).</summary>
    public List<QuestObjective> Objectives { get; init; } = [];

    /// <summary>Offset in the dump where this record was found.</summary>
    public long Offset { get; init; }

    /// <summary>Whether the record was detected as big-endian (Xbox 360).</summary>
    public bool IsBigEndian { get; init; }
}

/// <summary>
///     Quest stage information from INDX + CNAM/QSDT subrecords.
/// </summary>
public record QuestStage
{
    /// <summary>Stage index value.</summary>
    public int Index { get; init; }

    /// <summary>Log entry text (CNAM subrecord, null-terminated).</summary>
    public string? LogEntry { get; init; }

    /// <summary>Stage flags (from QSDT).</summary>
    public byte Flags { get; init; }
}

/// <summary>
///     Quest objective information from QOBJ + NNAM subrecords.
/// </summary>
public record QuestObjective
{
    /// <summary>Objective index value.</summary>
    public int Index { get; init; }

    /// <summary>Objective display text (NNAM subrecord).</summary>
    public string? DisplayText { get; init; }

    /// <summary>Target stage for completion.</summary>
    public int? TargetStage { get; init; }
}

/// <summary>
///     Fully reconstructed dialogue response from INFO record.
///     Aggregates data from INFO main record header, NAM1 (response text), TRDT (emotion), etc.
/// </summary>
public record ReconstructedDialogue
{
    /// <summary>FormID of the INFO record.</summary>
    public uint FormId { get; init; }

    /// <summary>Editor ID of the INFO record.</summary>
    public string? EditorId { get; init; }

    /// <summary>Parent DIAL topic FormID.</summary>
    public uint? TopicFormId { get; init; }

    /// <summary>Parent quest FormID (QSTI subrecord).</summary>
    public uint? QuestFormId { get; init; }

    /// <summary>Speaker NPC FormID (if specified).</summary>
    public uint? SpeakerFormId { get; init; }

    /// <summary>Response entries (each INFO can have multiple responses).</summary>
    public List<DialogueResponse> Responses { get; init; } = [];

    /// <summary>Previous INFO FormID (link chain).</summary>
    public uint? PreviousInfo { get; init; }

    /// <summary>Offset in the dump where this record was found.</summary>
    public long Offset { get; init; }

    /// <summary>Whether the record was detected as big-endian (Xbox 360).</summary>
    public bool IsBigEndian { get; init; }
}

/// <summary>
///     Individual dialogue response from NAM1 + TRDT subrecords.
/// </summary>
public record DialogueResponse
{
    /// <summary>Response text (NAM1 subrecord).</summary>
    public string? Text { get; init; }

    /// <summary>Emotion type (0=Neutral, 1=Anger, 2=Disgust, etc.).</summary>
    public uint EmotionType { get; init; }

    /// <summary>Emotion value (-100 to +100).</summary>
    public int EmotionValue { get; init; }

    /// <summary>Response number within the INFO record.</summary>
    public byte ResponseNumber { get; init; }

    /// <summary>Human-readable emotion name.</summary>
    public string EmotionName => EmotionType switch
    {
        0 => "Neutral",
        1 => "Anger",
        2 => "Disgust",
        3 => "Fear",
        4 => "Sad",
        5 => "Happy",
        6 => "Surprise",
        7 => "Pained",
        8 => "Puzzled",
        _ => $"Unknown ({EmotionType})"
    };
}

/// <summary>
///     Fully reconstructed Note with text content.
///     Aggregates data from NOTE main record header, EDID, FULL, and text content.
/// </summary>
public record ReconstructedNote
{
    /// <summary>FormID of the NOTE record.</summary>
    public uint FormId { get; init; }

    /// <summary>Editor ID.</summary>
    public string? EditorId { get; init; }

    /// <summary>Display name.</summary>
    public string? FullName { get; init; }

    /// <summary>Note type (0=Sound, 1=Text, 2=Image, 3=Voice).</summary>
    public byte NoteType { get; init; }

    /// <summary>Text content (TNAM subrecord, or DESC for books).</summary>
    public string? Text { get; init; }

    /// <summary>Offset in the dump where this record was found.</summary>
    public long Offset { get; init; }

    /// <summary>Whether the record was detected as big-endian (Xbox 360).</summary>
    public bool IsBigEndian { get; init; }

    /// <summary>Human-readable note type.</summary>
    public string NoteTypeName => NoteType switch
    {
        0 => "Sound",
        1 => "Text",
        2 => "Image",
        3 => "Voice",
        _ => $"Unknown ({NoteType})"
    };
}

/// <summary>
///     Fully reconstructed Cell with placed objects.
///     Aggregates data from CELL main record header and associated REFR/ACHR/ACRE records.
/// </summary>
public record ReconstructedCell
{
    /// <summary>FormID of the CELL record.</summary>
    public uint FormId { get; init; }

    /// <summary>Editor ID.</summary>
    public string? EditorId { get; init; }

    /// <summary>Display name.</summary>
    public string? FullName { get; init; }

    /// <summary>Cell X coordinate in the grid (from XCLC, null for interior cells).</summary>
    public int? GridX { get; init; }

    /// <summary>Cell Y coordinate in the grid (from XCLC, null for interior cells).</summary>
    public int? GridY { get; init; }

    /// <summary>Parent worldspace FormID (null for interior cells).</summary>
    public uint? WorldspaceFormId { get; init; }

    /// <summary>Cell flags from DATA subrecord.</summary>
    public byte Flags { get; init; }

    /// <summary>Whether this is an interior cell.</summary>
    public bool IsInterior => (Flags & 0x01) != 0;

    /// <summary>Whether this cell has water.</summary>
    public bool HasWater => (Flags & 0x02) != 0;

    /// <summary>Placed objects in this cell (REFR, ACHR, ACRE records).</summary>
    public List<PlacedReference> PlacedObjects { get; init; } = [];

    /// <summary>Associated LAND record heightmap (if found).</summary>
    public LandHeightmap? Heightmap { get; init; }

    /// <summary>Offset in the dump where this record was found.</summary>
    public long Offset { get; init; }

    /// <summary>Whether the record was detected as big-endian (Xbox 360).</summary>
    public bool IsBigEndian { get; init; }
}

/// <summary>
///     Placed object reference from REFR/ACHR/ACRE records.
/// </summary>
public record PlacedReference
{
    /// <summary>FormID of the placed reference.</summary>
    public uint FormId { get; init; }

    /// <summary>FormID of the base object being placed.</summary>
    public uint BaseFormId { get; init; }

    /// <summary>Editor ID of the base object (if resolved).</summary>
    public string? BaseEditorId { get; init; }

    /// <summary>Record type (REFR, ACHR, or ACRE).</summary>
    public string RecordType { get; init; } = "REFR";

    /// <summary>X position in world coordinates.</summary>
    public float X { get; init; }

    /// <summary>Y position in world coordinates.</summary>
    public float Y { get; init; }

    /// <summary>Z position in world coordinates.</summary>
    public float Z { get; init; }

    /// <summary>X rotation in radians.</summary>
    public float RotX { get; init; }

    /// <summary>Y rotation in radians.</summary>
    public float RotY { get; init; }

    /// <summary>Z rotation in radians.</summary>
    public float RotZ { get; init; }

    /// <summary>Scale factor (1.0 = normal).</summary>
    public float Scale { get; init; } = 1.0f;

    /// <summary>Owner FormID (XOWN subrecord).</summary>
    public uint? OwnerFormId { get; init; }

    /// <summary>Enable parent FormID (XESP subrecord).</summary>
    public uint? EnableParentFormId { get; init; }

    /// <summary>Offset in the dump where this record was found.</summary>
    public long Offset { get; init; }

    /// <summary>Whether the record was detected as big-endian (Xbox 360).</summary>
    public bool IsBigEndian { get; init; }
}

/// <summary>
///     Fully reconstructed Worldspace from memory dump.
/// </summary>
public record ReconstructedWorldspace
{
    /// <summary>FormID of the WRLD record.</summary>
    public uint FormId { get; init; }

    /// <summary>Editor ID (e.g., "WastelandNV").</summary>
    public string? EditorId { get; init; }

    /// <summary>Display name.</summary>
    public string? FullName { get; init; }

    /// <summary>Parent worldspace FormID (if any).</summary>
    public uint? ParentWorldspaceFormId { get; init; }

    /// <summary>Climate FormID (CNAM subrecord).</summary>
    public uint? ClimateFormId { get; init; }

    /// <summary>Water FormID (NAM2 subrecord).</summary>
    public uint? WaterFormId { get; init; }

    /// <summary>Cells belonging to this worldspace.</summary>
    public List<ReconstructedCell> Cells { get; init; } = [];

    /// <summary>Offset in the dump where this record was found.</summary>
    public long Offset { get; init; }

    /// <summary>Whether the record was detected as big-endian (Xbox 360).</summary>
    public bool IsBigEndian { get; init; }
}

/// <summary>
///     Weapon type classification from WEAP DATA/DNAM subrecords.
/// </summary>
public enum WeaponType : byte
{
    HandToHand = 0,
    Melee = 1,
    Pistol = 2,
    Rifle = 3,
    Automatic = 4,
    BigGun = 5,
    Energy = 6,
    SmallGun = 7
}

/// <summary>
///     Fully reconstructed Weapon from memory dump.
///     Aggregates data from WEAP main record header, DATA (15 bytes), DNAM (204 bytes), CRDT, etc.
/// </summary>
public record ReconstructedWeapon
{
    // Core identification
    /// <summary>FormID of the weapon record.</summary>
    public uint FormId { get; init; }

    /// <summary>Editor ID (e.g., "WeapGunPistol10mm").</summary>
    public string? EditorId { get; init; }

    /// <summary>Display name (e.g., "10mm Pistol").</summary>
    public string? FullName { get; init; }

    // DATA subrecord (15 bytes)
    /// <summary>Base value in caps.</summary>
    public int Value { get; init; }

    /// <summary>Weapon health/condition.</summary>
    public int Health { get; init; }

    /// <summary>Weight in units.</summary>
    public float Weight { get; init; }

    /// <summary>Base damage.</summary>
    public short Damage { get; init; }

    /// <summary>Clip/magazine size.</summary>
    public byte ClipSize { get; init; }

    // DNAM subrecord (204 bytes) - key combat fields
    /// <summary>Weapon type classification.</summary>
    public WeaponType WeaponType { get; init; }

    /// <summary>Animation type (for attack speed).</summary>
    public uint AnimationType { get; init; }

    /// <summary>Attack speed multiplier.</summary>
    public float Speed { get; init; }

    /// <summary>Melee reach distance.</summary>
    public float Reach { get; init; }

    /// <summary>Ammo consumed per shot.</summary>
    public byte AmmoPerShot { get; init; }

    /// <summary>Minimum spread (accuracy).</summary>
    public float MinSpread { get; init; }

    /// <summary>Maximum spread (inaccuracy).</summary>
    public float Spread { get; init; }

    /// <summary>Sight/sway drift.</summary>
    public float Drift { get; init; }

    /// <summary>Ammo type FormID (ENAM subrecord).</summary>
    public uint? AmmoFormId { get; init; }

    /// <summary>Projectile type FormID.</summary>
    public uint? ProjectileFormId { get; init; }

    /// <summary>VATS to-hit chance bonus.</summary>
    public byte VatsToHitChance { get; init; }

    /// <summary>Number of projectiles per shot.</summary>
    public byte NumProjectiles { get; init; }

    /// <summary>Minimum effective range.</summary>
    public float MinRange { get; init; }

    /// <summary>Maximum effective range.</summary>
    public float MaxRange { get; init; }

    /// <summary>Shots per second (fire rate).</summary>
    public float ShotsPerSec { get; init; }

    /// <summary>Action point cost in VATS.</summary>
    public float ActionPoints { get; init; }

    /// <summary>Strength requirement to use effectively.</summary>
    public uint StrengthRequirement { get; init; }

    /// <summary>Skill requirement to use effectively.</summary>
    public uint SkillRequirement { get; init; }

    // CRDT subrecord (critical data)
    /// <summary>Critical hit bonus damage.</summary>
    public short CriticalDamage { get; init; }

    /// <summary>Critical hit chance multiplier.</summary>
    public float CriticalChance { get; init; }

    /// <summary>Critical effect FormID.</summary>
    public uint? CriticalEffectFormId { get; init; }

    // Model reference
    /// <summary>Model file path (MODL subrecord).</summary>
    public string? ModelPath { get; init; }

    // Computed
    /// <summary>Calculated damage per second.</summary>
    public float DamagePerSecond => Damage * ShotsPerSec;

    /// <summary>Offset in the dump where this record was found.</summary>
    public long Offset { get; init; }

    /// <summary>Whether the record was detected as big-endian (Xbox 360).</summary>
    public bool IsBigEndian { get; init; }

    /// <summary>Human-readable weapon type name.</summary>
    public string WeaponTypeName => WeaponType switch
    {
        WeaponType.HandToHand => "Hand-to-Hand",
        WeaponType.Melee => "Melee",
        WeaponType.Pistol => "Pistol",
        WeaponType.Rifle => "Rifle",
        WeaponType.Automatic => "Automatic",
        WeaponType.BigGun => "Big Gun",
        WeaponType.Energy => "Energy",
        WeaponType.SmallGun => "Small Gun",
        _ => $"Unknown ({(byte)WeaponType})"
    };
}

/// <summary>
///     Fully reconstructed Armor from memory dump.
///     Aggregates data from ARMO main record header, DATA (12 bytes), DNAM (12 bytes).
/// </summary>
public record ReconstructedArmor
{
    /// <summary>FormID of the armor record.</summary>
    public uint FormId { get; init; }

    /// <summary>Editor ID.</summary>
    public string? EditorId { get; init; }

    /// <summary>Display name.</summary>
    public string? FullName { get; init; }

    // DATA subrecord (12 bytes): Value, Health, Weight
    /// <summary>Base value in caps.</summary>
    public int Value { get; init; }

    /// <summary>Armor health/condition.</summary>
    public int Health { get; init; }

    /// <summary>Weight in units.</summary>
    public float Weight { get; init; }

    // DNAM subrecord (12 bytes)
    /// <summary>Armor rating (damage resistance).</summary>
    public int ArmorRating { get; init; }

    /// <summary>Model file path (MODL subrecord).</summary>
    public string? ModelPath { get; init; }

    /// <summary>Offset in the dump where this record was found.</summary>
    public long Offset { get; init; }

    /// <summary>Whether the record was detected as big-endian (Xbox 360).</summary>
    public bool IsBigEndian { get; init; }
}

/// <summary>
///     Fully reconstructed Ammo from memory dump.
///     Aggregates data from AMMO main record header, DATA (13 bytes).
/// </summary>
public record ReconstructedAmmo
{
    /// <summary>FormID of the ammo record.</summary>
    public uint FormId { get; init; }

    /// <summary>Editor ID.</summary>
    public string? EditorId { get; init; }

    /// <summary>Display name.</summary>
    public string? FullName { get; init; }

    // DATA subrecord (13 bytes)
    /// <summary>Projectile speed.</summary>
    public float Speed { get; init; }

    /// <summary>Ammo flags.</summary>
    public byte Flags { get; init; }

    /// <summary>Base value in caps.</summary>
    public uint Value { get; init; }

    /// <summary>Rounds per clip (for display).</summary>
    public byte ClipRounds { get; init; }

    /// <summary>Projectile FormID.</summary>
    public uint? ProjectileFormId { get; init; }

    /// <summary>Model file path (MODL subrecord).</summary>
    public string? ModelPath { get; init; }

    /// <summary>Offset in the dump where this record was found.</summary>
    public long Offset { get; init; }

    /// <summary>Whether the record was detected as big-endian (Xbox 360).</summary>
    public bool IsBigEndian { get; init; }
}

/// <summary>
///     Fully reconstructed Consumable (ALCH) from memory dump.
///     Aggregates data from ALCH main record header, DATA, ENIT, EFID subrecords.
/// </summary>
public record ReconstructedConsumable
{
    /// <summary>FormID of the consumable record.</summary>
    public uint FormId { get; init; }

    /// <summary>Editor ID.</summary>
    public string? EditorId { get; init; }

    /// <summary>Display name.</summary>
    public string? FullName { get; init; }

    // DATA subrecord (4 bytes)
    /// <summary>Weight in units.</summary>
    public float Weight { get; init; }

    // ENIT subrecord (20 bytes)
    /// <summary>Base value in caps.</summary>
    public uint Value { get; init; }

    /// <summary>Addiction FormID (if addictive).</summary>
    public uint? AddictionFormId { get; init; }

    /// <summary>Addiction chance (0.0-1.0).</summary>
    public float AddictionChance { get; init; }

    /// <summary>Effect FormIDs (EFID subrecords).</summary>
    public List<uint> EffectFormIds { get; init; } = [];

    /// <summary>Model file path (MODL subrecord).</summary>
    public string? ModelPath { get; init; }

    /// <summary>Offset in the dump where this record was found.</summary>
    public long Offset { get; init; }

    /// <summary>Whether the record was detected as big-endian (Xbox 360).</summary>
    public bool IsBigEndian { get; init; }
}

/// <summary>
///     Fully reconstructed Misc Item from memory dump.
///     Aggregates data from MISC main record header, DATA (8 bytes).
/// </summary>
public record ReconstructedMiscItem
{
    /// <summary>FormID of the misc item record.</summary>
    public uint FormId { get; init; }

    /// <summary>Editor ID.</summary>
    public string? EditorId { get; init; }

    /// <summary>Display name.</summary>
    public string? FullName { get; init; }

    // DATA subrecord (8 bytes)
    /// <summary>Base value in caps.</summary>
    public int Value { get; init; }

    /// <summary>Weight in units.</summary>
    public float Weight { get; init; }

    /// <summary>Model file path (MODL subrecord).</summary>
    public string? ModelPath { get; init; }

    /// <summary>Offset in the dump where this record was found.</summary>
    public long Offset { get; init; }

    /// <summary>Whether the record was detected as big-endian (Xbox 360).</summary>
    public bool IsBigEndian { get; init; }
}

/// <summary>
///     Perk entry data from PRKE/PRKC/EPFT chains.
/// </summary>
public record PerkEntry
{
    /// <summary>Entry type (0=Quest Stage, 1=Ability, 2=Entry Point).</summary>
    public byte Type { get; init; }

    /// <summary>Rank for this entry.</summary>
    public byte Rank { get; init; }

    /// <summary>Priority within rank.</summary>
    public byte Priority { get; init; }

    /// <summary>Associated ability FormID (for type 1).</summary>
    public uint? AbilityFormId { get; init; }

    /// <summary>Human-readable entry type name.</summary>
    public string TypeName => Type switch
    {
        0 => "Quest Stage",
        1 => "Ability",
        2 => "Entry Point",
        _ => $"Unknown ({Type})"
    };
}

/// <summary>
///     Fully reconstructed Perk from memory dump.
///     Aggregates data from PERK main record header, DATA, DESC, PRKE chains.
/// </summary>
public record ReconstructedPerk
{
    /// <summary>FormID of the perk record.</summary>
    public uint FormId { get; init; }

    /// <summary>Editor ID.</summary>
    public string? EditorId { get; init; }

    /// <summary>Display name.</summary>
    public string? FullName { get; init; }

    /// <summary>Perk description (DESC subrecord).</summary>
    public string? Description { get; init; }

    // DATA subrecord
    /// <summary>Is this a trait (1) or regular perk (0).</summary>
    public byte Trait { get; init; }

    /// <summary>Minimum level to take this perk.</summary>
    public byte MinLevel { get; init; }

    /// <summary>Number of ranks available.</summary>
    public byte Ranks { get; init; }

    /// <summary>Is this perk visible to players (1) or hidden (0).</summary>
    public byte Playable { get; init; }

    /// <summary>Icon file path (ICON/MICO subrecord).</summary>
    public string? IconPath { get; init; }

    /// <summary>Perk entries (PRKE/PRKC/EPFT chains).</summary>
    public List<PerkEntry> Entries { get; init; } = [];

    /// <summary>Offset in the dump where this record was found.</summary>
    public long Offset { get; init; }

    /// <summary>Whether the record was detected as big-endian (Xbox 360).</summary>
    public bool IsBigEndian { get; init; }

    /// <summary>Whether this is a trait rather than a perk.</summary>
    public bool IsTrait => Trait != 0;

    /// <summary>Whether this perk is visible in the perk selection UI.</summary>
    public bool IsPlayable => Playable != 0;
}

/// <summary>
///     Spell type classification from SPEL SPIT subrecord.
/// </summary>
public enum SpellType : uint
{
    Spell = 0,
    Disease = 1,
    Power = 2,
    LesserPower = 3,
    Ability = 4,
    Poison = 5,
    Addiction = 10
}

/// <summary>
///     Fully reconstructed Spell from memory dump.
///     Aggregates data from SPEL main record header, SPIT (16 bytes), EFID subrecords.
/// </summary>
public record ReconstructedSpell
{
    /// <summary>FormID of the spell record.</summary>
    public uint FormId { get; init; }

    /// <summary>Editor ID.</summary>
    public string? EditorId { get; init; }

    /// <summary>Display name.</summary>
    public string? FullName { get; init; }

    // SPIT subrecord (16 bytes)
    /// <summary>Spell type classification.</summary>
    public SpellType Type { get; init; }

    /// <summary>Spell cost.</summary>
    public uint Cost { get; init; }

    /// <summary>Spell level.</summary>
    public uint Level { get; init; }

    /// <summary>Spell flags.</summary>
    public byte Flags { get; init; }

    /// <summary>Effect FormIDs (EFID subrecords).</summary>
    public List<uint> EffectFormIds { get; init; } = [];

    /// <summary>Offset in the dump where this record was found.</summary>
    public long Offset { get; init; }

    /// <summary>Whether the record was detected as big-endian (Xbox 360).</summary>
    public bool IsBigEndian { get; init; }

    /// <summary>Human-readable spell type name.</summary>
    public string TypeName => Type switch
    {
        SpellType.Spell => "Spell",
        SpellType.Disease => "Disease",
        SpellType.Power => "Power",
        SpellType.LesserPower => "Lesser Power",
        SpellType.Ability => "Ability",
        SpellType.Poison => "Poison",
        SpellType.Addiction => "Addiction",
        _ => $"Unknown ({(uint)Type})"
    };
}

/// <summary>
///     Fully reconstructed Race from memory dump.
///     Aggregates data from RACE main record header, DATA (36 bytes), and related subrecords.
/// </summary>
public record ReconstructedRace
{
    /// <summary>FormID of the race record.</summary>
    public uint FormId { get; init; }

    /// <summary>Editor ID.</summary>
    public string? EditorId { get; init; }

    /// <summary>Display name.</summary>
    public string? FullName { get; init; }

    /// <summary>Race description (DESC subrecord).</summary>
    public string? Description { get; init; }

    // DATA subrecord (36 bytes) - S.P.E.C.I.A.L. bonuses
    /// <summary>Strength modifier.</summary>
    public sbyte Strength { get; init; }

    /// <summary>Perception modifier.</summary>
    public sbyte Perception { get; init; }

    /// <summary>Endurance modifier.</summary>
    public sbyte Endurance { get; init; }

    /// <summary>Charisma modifier.</summary>
    public sbyte Charisma { get; init; }

    /// <summary>Intelligence modifier.</summary>
    public sbyte Intelligence { get; init; }

    /// <summary>Agility modifier.</summary>
    public sbyte Agility { get; init; }

    /// <summary>Luck modifier.</summary>
    public sbyte Luck { get; init; }

    // Height data
    /// <summary>Male height multiplier.</summary>
    public float MaleHeight { get; init; }

    /// <summary>Female height multiplier.</summary>
    public float FemaleHeight { get; init; }

    // Related races (ONAM, YNAM)
    /// <summary>Older race FormID (for aging).</summary>
    public uint? OlderRaceFormId { get; init; }

    /// <summary>Younger race FormID (for aging).</summary>
    public uint? YoungerRaceFormId { get; init; }

    // Voice types (VTCK)
    /// <summary>Male voice type FormID.</summary>
    public uint? MaleVoiceFormId { get; init; }

    /// <summary>Female voice type FormID.</summary>
    public uint? FemaleVoiceFormId { get; init; }

    /// <summary>Racial abilities (SPLO subrecords).</summary>
    public List<uint> AbilityFormIds { get; init; } = [];

    /// <summary>Offset in the dump where this record was found.</summary>
    public long Offset { get; init; }

    /// <summary>Whether the record was detected as big-endian (Xbox 360).</summary>
    public bool IsBigEndian { get; init; }
}

/// <summary>
///     Fully reconstructed Creature from memory dump.
///     Similar to NPC but for non-human entities.
/// </summary>
public record ReconstructedCreature
{
    /// <summary>FormID of the creature record.</summary>
    public uint FormId { get; init; }

    /// <summary>Editor ID.</summary>
    public string? EditorId { get; init; }

    /// <summary>Display name.</summary>
    public string? FullName { get; init; }

    /// <summary>Actor base stats from ACBS subrecord.</summary>
    public ActorBaseSubrecord? Stats { get; init; }

    /// <summary>Creature type (0=Animal, 1=MutatedAnimal, 2=MutatedInsect, etc.).</summary>
    public byte CreatureType { get; init; }

    /// <summary>Combat skill level.</summary>
    public byte CombatSkill { get; init; }

    /// <summary>Magic skill level.</summary>
    public byte MagicSkill { get; init; }

    /// <summary>Stealth skill level.</summary>
    public byte StealthSkill { get; init; }

    /// <summary>Attack damage.</summary>
    public short AttackDamage { get; init; }

    /// <summary>Script FormID.</summary>
    public uint? Script { get; init; }

    /// <summary>Death item FormID.</summary>
    public uint? DeathItem { get; init; }

    /// <summary>Model path.</summary>
    public string? ModelPath { get; init; }

    /// <summary>Faction memberships.</summary>
    public List<FactionMembership> Factions { get; init; } = [];

    /// <summary>Spell/ability FormIDs.</summary>
    public List<uint> Spells { get; init; } = [];

    /// <summary>Offset in the dump where this record was found.</summary>
    public long Offset { get; init; }

    /// <summary>Whether the record was detected as big-endian (Xbox 360).</summary>
    public bool IsBigEndian { get; init; }

    /// <summary>Human-readable creature type name.</summary>
    public string CreatureTypeName => CreatureType switch
    {
        0 => "Animal",
        1 => "Mutated Animal",
        2 => "Mutated Insect",
        3 => "Abomination",
        4 => "Super Mutant",
        5 => "Feral Ghoul",
        6 => "Robot",
        7 => "Giant",
        _ => $"Unknown ({CreatureType})"
    };
}

/// <summary>
///     Fully reconstructed Faction from memory dump.
/// </summary>
public record ReconstructedFaction
{
    /// <summary>FormID of the faction record.</summary>
    public uint FormId { get; init; }

    /// <summary>Editor ID.</summary>
    public string? EditorId { get; init; }

    /// <summary>Display name.</summary>
    public string? FullName { get; init; }

    /// <summary>Faction flags.</summary>
    public uint Flags { get; init; }

    /// <summary>Whether this faction is hidden from the player.</summary>
    public bool IsHiddenFromPlayer => (Flags & 0x01) != 0;

    /// <summary>Whether this faction allows evil acts.</summary>
    public bool AllowsEvil => (Flags & 0x02) != 0;

    /// <summary>Whether this faction allows special combat.</summary>
    public bool AllowsSpecialCombat => (Flags & 0x04) != 0;

    /// <summary>Faction relations (other faction FormIDs and their disposition).</summary>
    public List<FactionRelation> Relations { get; init; } = [];

    /// <summary>Faction rank names.</summary>
    public List<string> RankNames { get; init; } = [];

    /// <summary>Offset in the dump where this record was found.</summary>
    public long Offset { get; init; }

    /// <summary>Whether the record was detected as big-endian (Xbox 360).</summary>
    public bool IsBigEndian { get; init; }
}

/// <summary>
///     Faction relation data from XNAM subrecord.
/// </summary>
public record FactionRelation(uint FactionFormId, int Modifier, uint CombatFlags);

/// <summary>
///     Fully reconstructed Book from memory dump.
///     Contains text content similar to notes.
/// </summary>
public record ReconstructedBook
{
    /// <summary>FormID of the book record.</summary>
    public uint FormId { get; init; }

    /// <summary>Editor ID.</summary>
    public string? EditorId { get; init; }

    /// <summary>Display name.</summary>
    public string? FullName { get; init; }

    /// <summary>Book text content (DESC subrecord).</summary>
    public string? Text { get; init; }

    /// <summary>Value in caps.</summary>
    public int Value { get; init; }

    /// <summary>Weight.</summary>
    public float Weight { get; init; }

    /// <summary>Book flags (teaches skill, etc.).</summary>
    public byte Flags { get; init; }

    /// <summary>Skill taught by this book (if any).</summary>
    public byte SkillTaught { get; init; }

    /// <summary>Model path.</summary>
    public string? ModelPath { get; init; }

    /// <summary>Offset in the dump where this record was found.</summary>
    public long Offset { get; init; }

    /// <summary>Whether the record was detected as big-endian (Xbox 360).</summary>
    public bool IsBigEndian { get; init; }

    /// <summary>Whether this book teaches a skill.</summary>
    public bool TeachesSkill => (Flags & 0x01) != 0;
}

/// <summary>
///     Fully reconstructed Key from memory dump.
/// </summary>
public record ReconstructedKey
{
    /// <summary>FormID of the key record.</summary>
    public uint FormId { get; init; }

    /// <summary>Editor ID.</summary>
    public string? EditorId { get; init; }

    /// <summary>Display name.</summary>
    public string? FullName { get; init; }

    /// <summary>Value in caps.</summary>
    public int Value { get; init; }

    /// <summary>Weight.</summary>
    public float Weight { get; init; }

    /// <summary>Model path.</summary>
    public string? ModelPath { get; init; }

    /// <summary>Offset in the dump where this record was found.</summary>
    public long Offset { get; init; }

    /// <summary>Whether the record was detected as big-endian (Xbox 360).</summary>
    public bool IsBigEndian { get; init; }
}

/// <summary>
///     Fully reconstructed Container from memory dump.
/// </summary>
public record ReconstructedContainer
{
    /// <summary>FormID of the container record.</summary>
    public uint FormId { get; init; }

    /// <summary>Editor ID.</summary>
    public string? EditorId { get; init; }

    /// <summary>Display name.</summary>
    public string? FullName { get; init; }

    /// <summary>Container flags.</summary>
    public byte Flags { get; init; }

    /// <summary>Whether this container respawns.</summary>
    public bool Respawns => (Flags & 0x02) != 0;

    /// <summary>Contents of the container.</summary>
    public List<InventoryItem> Contents { get; init; } = [];

    /// <summary>Model path.</summary>
    public string? ModelPath { get; init; }

    /// <summary>Script FormID.</summary>
    public uint? Script { get; init; }

    /// <summary>Offset in the dump where this record was found.</summary>
    public long Offset { get; init; }

    /// <summary>Whether the record was detected as big-endian (Xbox 360).</summary>
    public bool IsBigEndian { get; init; }
}

/// <summary>
///     Fully reconstructed Terminal from memory dump.
/// </summary>
public record ReconstructedTerminal
{
    /// <summary>FormID of the terminal record.</summary>
    public uint FormId { get; init; }

    /// <summary>Editor ID.</summary>
    public string? EditorId { get; init; }

    /// <summary>Display name.</summary>
    public string? FullName { get; init; }

    /// <summary>Terminal header text (DESC subrecord).</summary>
    public string? HeaderText { get; init; }

    /// <summary>Terminal difficulty (0-4).</summary>
    public byte Difficulty { get; init; }

    /// <summary>Terminal flags.</summary>
    public byte Flags { get; init; }

    /// <summary>Terminal menu items.</summary>
    public List<TerminalMenuItem> MenuItems { get; init; } = [];

    /// <summary>Password (if set).</summary>
    public string? Password { get; init; }

    /// <summary>Offset in the dump where this record was found.</summary>
    public long Offset { get; init; }

    /// <summary>Whether the record was detected as big-endian (Xbox 360).</summary>
    public bool IsBigEndian { get; init; }

    /// <summary>Human-readable difficulty name.</summary>
    public string DifficultyName => Difficulty switch
    {
        0 => "Very Easy",
        1 => "Easy",
        2 => "Average",
        3 => "Hard",
        4 => "Very Hard",
        _ => $"Unknown ({Difficulty})"
    };
}

/// <summary>
///     Terminal menu item from ITXT/RNAM subrecords.
/// </summary>
public record TerminalMenuItem
{
    /// <summary>Menu item text.</summary>
    public string? Text { get; init; }

    /// <summary>Result script FormID.</summary>
    public uint? ResultScript { get; init; }

    /// <summary>Sub-terminal FormID (if this links to another terminal).</summary>
    public uint? SubTerminal { get; init; }
}

/// <summary>
///     Fully reconstructed Dialog Topic from memory dump.
/// </summary>
public record ReconstructedDialogTopic
{
    /// <summary>FormID of the dialog topic record.</summary>
    public uint FormId { get; init; }

    /// <summary>Editor ID.</summary>
    public string? EditorId { get; init; }

    /// <summary>Display name.</summary>
    public string? FullName { get; init; }

    /// <summary>Parent quest FormID.</summary>
    public uint? QuestFormId { get; init; }

    /// <summary>Topic type (0=Topic, 1=Conversation, 2=Combat, etc.).</summary>
    public byte TopicType { get; init; }

    /// <summary>Topic flags.</summary>
    public byte Flags { get; init; }

    /// <summary>Number of INFO responses under this topic.</summary>
    public int ResponseCount { get; init; }

    /// <summary>Offset in the dump where this record was found.</summary>
    public long Offset { get; init; }

    /// <summary>Whether the record was detected as big-endian (Xbox 360).</summary>
    public bool IsBigEndian { get; init; }

    /// <summary>Human-readable topic type name.</summary>
    public string TopicTypeName => TopicType switch
    {
        0 => "Topic",
        1 => "Conversation",
        2 => "Combat",
        3 => "Persuasion",
        4 => "Detection",
        5 => "Service",
        6 => "Miscellaneous",
        7 => "Radio",
        _ => $"Unknown ({TopicType})"
    };
}

/// <summary>
///     Game setting value type classification.
/// </summary>
public enum GameSettingType
{
    Float,
    Integer,
    String,
    Boolean
}

/// <summary>
///     Fully reconstructed Game Setting (GMST) from memory dump.
///     The setting type is determined by the first letter of the Editor ID:
///     'f' = float, 'i' = integer, 's' = string, 'b' = boolean.
/// </summary>
public record ReconstructedGameSetting
{
    /// <summary>FormID of the GMST record.</summary>
    public uint FormId { get; init; }

    /// <summary>Editor ID (setting name, e.g., "fActorStrengthEncumbranceMult").</summary>
    public string? EditorId { get; init; }

    /// <summary>The type of value this setting holds.</summary>
    public GameSettingType ValueType { get; init; }

    /// <summary>Float value (if ValueType is Float).</summary>
    public float? FloatValue { get; init; }

    /// <summary>Integer value (if ValueType is Integer or Boolean).</summary>
    public int? IntValue { get; init; }

    /// <summary>String value (if ValueType is String).</summary>
    public string? StringValue { get; init; }

    /// <summary>Offset in the dump where this record was found.</summary>
    public long Offset { get; init; }

    /// <summary>Whether the record was detected as big-endian (Xbox 360).</summary>
    public bool IsBigEndian { get; init; }

    /// <summary>Human-readable value representation.</summary>
    public string DisplayValue => ValueType switch
    {
        GameSettingType.Float => FloatValue?.ToString("F4") ?? "null",
        GameSettingType.Integer => IntValue?.ToString() ?? "null",
        GameSettingType.Boolean => IntValue != 0 ? "true" : "false",
        GameSettingType.String => StringValue ?? "null",
        _ => "unknown"
    };
}

/// <summary>
///     Aggregated semantic reconstruction result from a memory dump.
/// </summary>
public record SemanticReconstructionResult
{
    // Characters
    /// <summary>Reconstructed NPC records.</summary>
    public List<ReconstructedNpc> Npcs { get; init; } = [];

    /// <summary>Reconstructed Creature records.</summary>
    public List<ReconstructedCreature> Creatures { get; init; } = [];

    /// <summary>Reconstructed Race records.</summary>
    public List<ReconstructedRace> Races { get; init; } = [];

    /// <summary>Reconstructed Faction records.</summary>
    public List<ReconstructedFaction> Factions { get; init; } = [];

    // Quests and Dialogue
    /// <summary>Reconstructed Quest records.</summary>
    public List<ReconstructedQuest> Quests { get; init; } = [];

    /// <summary>Reconstructed Dialog Topic records.</summary>
    public List<ReconstructedDialogTopic> DialogTopics { get; init; } = [];

    /// <summary>Reconstructed Dialogue (INFO) records.</summary>
    public List<ReconstructedDialogue> Dialogues { get; init; } = [];

    /// <summary>Reconstructed Note records.</summary>
    public List<ReconstructedNote> Notes { get; init; } = [];

    /// <summary>Reconstructed Book records.</summary>
    public List<ReconstructedBook> Books { get; init; } = [];

    /// <summary>Reconstructed Terminal records.</summary>
    public List<ReconstructedTerminal> Terminals { get; init; } = [];

    // Items
    /// <summary>Reconstructed Weapon records.</summary>
    public List<ReconstructedWeapon> Weapons { get; init; } = [];

    /// <summary>Reconstructed Armor records.</summary>
    public List<ReconstructedArmor> Armor { get; init; } = [];

    /// <summary>Reconstructed Ammo records.</summary>
    public List<ReconstructedAmmo> Ammo { get; init; } = [];

    /// <summary>Reconstructed Consumable (ALCH) records.</summary>
    public List<ReconstructedConsumable> Consumables { get; init; } = [];

    /// <summary>Reconstructed Misc Item records.</summary>
    public List<ReconstructedMiscItem> MiscItems { get; init; } = [];

    /// <summary>Reconstructed Key records.</summary>
    public List<ReconstructedKey> Keys { get; init; } = [];

    /// <summary>Reconstructed Container records.</summary>
    public List<ReconstructedContainer> Containers { get; init; } = [];

    // Abilities
    /// <summary>Reconstructed Perk records.</summary>
    public List<ReconstructedPerk> Perks { get; init; } = [];

    /// <summary>Reconstructed Spell records.</summary>
    public List<ReconstructedSpell> Spells { get; init; } = [];

    // World
    /// <summary>Reconstructed Cell records.</summary>
    public List<ReconstructedCell> Cells { get; init; } = [];

    /// <summary>Reconstructed Worldspace records.</summary>
    public List<ReconstructedWorldspace> Worldspaces { get; init; } = [];

    // Game Data
    /// <summary>Reconstructed Game Setting (GMST) records.</summary>
    public List<ReconstructedGameSetting> GameSettings { get; init; } = [];

    /// <summary>FormID to Editor ID mapping built during reconstruction.</summary>
    public Dictionary<uint, string> FormIdToEditorId { get; init; } = [];

    /// <summary>Total records processed.</summary>
    public int TotalRecordsProcessed { get; init; }

    /// <summary>Number of records successfully reconstructed.</summary>
    public int TotalRecordsReconstructed =>
        Npcs.Count + Creatures.Count + Races.Count + Factions.Count +
        Quests.Count + DialogTopics.Count + Dialogues.Count + Notes.Count + Books.Count + Terminals.Count +
        Weapons.Count + Armor.Count + Ammo.Count + Consumables.Count + MiscItems.Count + Keys.Count + Containers.Count +
        Perks.Count + Spells.Count + Cells.Count + Worldspaces.Count + GameSettings.Count;

    /// <summary>
    ///     Counts of record types that were detected but not fully reconstructed.
    ///     Used for the "Other Records" summary section in split reports.
    /// </summary>
    public Dictionary<string, int> UnreconstructedTypeCounts { get; init; } = [];
}
