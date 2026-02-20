namespace FalloutXbox360Utils.Core.Formats.SaveGame;

/// <summary>
///     Gameplay statistics stored in the save file.
///     FNV typically has 27 pipe-delimited DWORD stats.
/// </summary>
public sealed class SaveStatistics
{
    /// <summary>Raw stat values indexed by position.</summary>
    public IReadOnlyList<uint> Values { get; init; } = [];

    /// <summary>Number of stats.</summary>
    public int Count => Values.Count;

    public uint QuestsCompleted => GetValueOrDefault(0);
    public uint LocationsDiscovered => GetValueOrDefault(1);
    public uint PeopleKilled => GetValueOrDefault(2);
    public uint CreaturesKilled => GetValueOrDefault(3);
    public uint LocksPicked => GetValueOrDefault(4);
    public uint ComputersHacked => GetValueOrDefault(5);
    public uint StimpaksTaken => GetValueOrDefault(6);
    public uint RadXTaken => GetValueOrDefault(7);
    public uint RadAwayTaken => GetValueOrDefault(8);
    public uint ChemsTaken => GetValueOrDefault(9);
    public uint TimesAddicted => GetValueOrDefault(10);
    public uint MinesDisarmed => GetValueOrDefault(11);
    public uint SpeechSuccesses => GetValueOrDefault(12);
    public uint PocketsPicked => GetValueOrDefault(13);
    public uint PantsExploded => GetValueOrDefault(14);
    public uint BooksRead => GetValueOrDefault(15);
    public uint BobbleheadsFound => GetValueOrDefault(16);
    public uint WeaponsCreated => GetValueOrDefault(17);
    public uint PeopleMesmerized => GetValueOrDefault(18);
    public uint CaptivesRescued => GetValueOrDefault(19);
    public uint SandmanKills => GetValueOrDefault(20);
    public uint ParalyzingPunches => GetValueOrDefault(21);
    public uint RobotsDisabled => GetValueOrDefault(22);
    public uint ContractsCompleted => GetValueOrDefault(23);
    public uint CorpsesEaten => GetValueOrDefault(24);
    public uint MysteriousStrangerVisits => GetValueOrDefault(25);
    public uint ChallengesCompleted => GetValueOrDefault(26);

    /// <summary>Stat labels by index.</summary>
    public static readonly string[] Labels =
    [
        "Quests Completed", "Locations Discovered", "People Killed", "Creatures Killed",
        "Locks Picked", "Computers Hacked", "Stimpaks Taken", "Rad-X Taken",
        "RadAway Taken", "Chems Taken", "Times Addicted", "Mines Disarmed",
        "Speech Successes", "Pockets Picked", "Pants Exploded", "Books Read",
        "Bobbleheads Found", "Weapons Created", "People Mesmerized", "Captives Rescued",
        "Sandman Kills", "Paralyzing Punches", "Robots Disabled", "Contracts Completed",
        "Corpses Eaten", "Mysterious Stranger Visits", "Challenges Completed"
    ];

    private uint GetValueOrDefault(int index)
    {
        return index < Values.Count ? Values[index] : 0;
    }
}
