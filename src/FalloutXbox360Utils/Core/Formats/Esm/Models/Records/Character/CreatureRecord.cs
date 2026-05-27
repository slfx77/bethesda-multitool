using FalloutXbox360Utils.Core.Formats.Esm.Subrecords;

namespace FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Character;

/// <summary>
///     Parsed Creature record.
///     Similar to NPC but for non-human entities.
/// </summary>
public record CreatureRecord
{
    /// <summary>FormID of the creature record.</summary>
    public uint FormId { get; init; }

    /// <summary>Editor ID.</summary>
    public string? EditorId { get; init; }

    /// <summary>Display name.</summary>
    public string? FullName { get; init; }

    /// <summary>Object bounds (OBND subrecord, 12 bytes int16[6]).</summary>
    public ObjectBounds? Bounds { get; init; }

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

    /// <summary>Death item FormID (INAM).</summary>
    public uint? DeathItem { get; init; }

    /// <summary>Equipped item FormID (EITM).</summary>
    public uint? EquippedItem { get; init; }

    /// <summary>Equipped attack animation flag (EAMT, 2 bytes uint16).</summary>
    public ushort? EquippedAttackAnimation { get; init; }

    /// <summary>Template creature FormID (TPLT).</summary>
    public uint? Template { get; init; }

    /// <summary>Voice type FormID (VTCK).</summary>
    public uint? VoiceType { get; init; }

    /// <summary>Combat style FormID (ZNAM).</summary>
    public uint? CombatStyleFormId { get; init; }

    /// <summary>Creature this creature inherits sounds from (CSCR), or null if not inherited.</summary>
    public uint? InheritsSoundsFrom { get; init; }

    /// <summary>Death item leveled list FormID (LNAM). Distinct from <see cref="DeathItem" /> (INAM).</summary>
    public uint? DeathItemLootList { get; init; }

    /// <summary>Impact data set FormID (CNAM on CREA — different semantics from NPC's class CNAM).</summary>
    public uint? ImpactDataSet { get; init; }

    /// <summary>Body data FormID (PNAM — single FormID per CREA).</summary>
    public uint? BodyData { get; init; }

    /// <summary>Sound type byte (RNAM): 0=Mechanical, 1=Aquatic, 2=Bear, etc.</summary>
    public byte? SoundType { get; init; }

    /// <summary>Turning speed in degrees/sec (TNAM, float).</summary>
    public float? TurningSpeed { get; init; }

    /// <summary>Base scale multiplier (BNAM, float). Engine defaults to 1.0.</summary>
    public float? BaseScale { get; init; }

    /// <summary>Foot weight for sound (WNAM, float).</summary>
    public float? FootWeight { get; init; }

    /// <summary>Impact material type (NAM4, uint32).</summary>
    public uint? ImpactMaterialType { get; init; }

    /// <summary>Sound level enum (NAM5, uint32): 0=Loud, 1=Normal, 2=Silent.</summary>
    public uint? SoundLevel { get; init; }

    /// <summary>Model path.</summary>
    public string? ModelPath { get; init; }

    /// <summary>AI behavior data from AIDT subrecord.</summary>
    public NpcAiData? AiData { get; init; }

    /// <summary>Faction memberships.</summary>
    public List<FactionMembership> Factions { get; init; } = [];

    /// <summary>Inventory items (CNTO + optional COED ownership data).</summary>
    public List<InventoryItem> Inventory { get; init; } = [];

    /// <summary>Spell/ability FormIDs.</summary>
    public List<uint> Spells { get; init; } = [];

    /// <summary>AI package FormIDs (PKID subrecords).</summary>
    public List<uint> Packages { get; init; } = [];

    /// <summary>
    ///     Raw NIFZ subrecord bytes — null-separated list of NIF model paths the creature
    ///     can swap between. Captured opaque so encoder can round-trip verbatim without
    ///     needing to understand the internal zstring layout.
    /// </summary>
    public byte[]? ModelFilesRaw { get; init; }

    /// <summary>Raw NIFT subrecord bytes — texture file hash blob (engine-validated).</summary>
    public byte[]? TextureFilesRaw { get; init; }

    /// <summary>
    ///     Raw KFFZ subrecord bytes — null-separated list of .kf animation file paths.
    ///     Captured opaque for verbatim round-trip.
    /// </summary>
    public byte[]? AnimationFilesRaw { get; init; }

    /// <summary>
    ///     Raw KFNM subrecord bytes — null-separated list of animation names (legacy F3
    ///     subrecord, occasionally present in FNV CREA records).
    /// </summary>
    public byte[]? AnimationNamesRaw { get; init; }

    /// <summary>
    ///     Raw concatenated CSDT/CSDI/CSDC sound-definition groups. Captured opaque because
    ///     each CSDT (sound type byte) is followed by an interleaved CSDI (sound FormID) +
    ///     CSDC (chance byte) sequence; the layout has many variants and engine round-trip
    ///     fidelity is sufficient for spawn parity.
    ///     <para>Stored as a list of (signature, bytes) pairs preserving original order.</para>
    /// </summary>
    public List<KeyValuePair<string, byte[]>>? SoundDefinitionsRaw { get; init; }

    /// <summary>
    ///     Raw concatenated DEST/DSTD/DMDL/DMDT/DSTF destructible-model subrecords, preserved
    ///     in original order. Rare (≈3% of vanilla CREA) and the layout is record-type-shared
    ///     so we capture verbatim rather than re-modeling.
    /// </summary>
    public List<KeyValuePair<string, byte[]>>? DestructionDataRaw { get; init; }

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
