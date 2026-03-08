namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Appearance;

internal sealed class NpcAppearanceIndex
{
    public Dictionary<uint, NpcScanEntry> Npcs { get; } =
        new();

    public Dictionary<uint, RaceScanEntry> Races { get; } =
        new();

    public Dictionary<uint, HairScanEntry> Hairs { get; } =
        new();

    public Dictionary<uint, EyesScanEntry> Eyes { get; } =
        new();

    public Dictionary<uint, HdptScanEntry> HeadParts { get; } =
        new();

    public Dictionary<uint, ArmoScanEntry> Armors { get; } =
        new();

    public Dictionary<uint, WeapScanEntry> Weapons { get; } =
        new();

    public Dictionary<uint, List<uint>> LeveledItems { get; } =
        new();

    public Dictionary<uint, List<uint>> LeveledNpcs { get; } =
        new();
}
