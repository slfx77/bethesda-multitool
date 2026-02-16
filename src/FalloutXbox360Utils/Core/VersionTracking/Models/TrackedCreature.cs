namespace FalloutXbox360Utils.Core.VersionTracking.Models;

/// <summary>
///     Lightweight creature snapshot for version tracking.
/// </summary>
public record TrackedCreature
{
    public uint FormId { get; init; }
    public string? EditorId { get; init; }
    public string? FullName { get; init; }
    public byte CreatureType { get; init; }
    public ushort? Level { get; init; }
    public short AttackDamage { get; init; }
    public byte CombatSkill { get; init; }
    public byte MagicSkill { get; init; }
    public byte StealthSkill { get; init; }
    public uint? ScriptFormId { get; init; }
    public uint? DeathItemFormId { get; init; }
    public List<uint> FactionFormIds { get; init; } = [];
    public List<uint> SpellFormIds { get; init; } = [];
}
