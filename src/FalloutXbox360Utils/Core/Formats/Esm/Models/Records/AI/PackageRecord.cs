using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;

namespace FalloutXbox360Utils.Core.Formats.Esm.Models.Records.AI;

/// <summary>
///     AI Package (PACK) record controlling NPC behavior — where they sleep, eat, patrol,
///     wander, and what they do throughout the day.
/// </summary>
public record PackageRecord
{
    /// <summary>FormID of the PACK record.</summary>
    public uint FormId { get; init; }

    /// <summary>Editor ID.</summary>
    public string? EditorId { get; init; }

    /// <summary>Package data from PKDT subrecord (type, flags).</summary>
    public PackageData? Data { get; init; }

    /// <summary>Package schedule from PSDT subrecord.</summary>
    public PackageSchedule? Schedule { get; init; }

    /// <summary>Use Weapon package data from PKW3, when present.</summary>
    public PackageUseWeaponData? UseWeaponData { get; init; }

    /// <summary>Dialogue package data from PKDD, when present.</summary>
    public PackageDialogueData? DialogueData { get; init; }

    /// <summary>Idle marker package data from IDLF/IDLC/IDLT/IDLA, when present.</summary>
    public PackageIdleCollection? IdleCollection { get; init; }

    /// <summary>Primary package location (PLDT subrecord).</summary>
    public PackageLocation? Location { get; init; }

    /// <summary>Secondary package location (PLD2 subrecord).</summary>
    public PackageLocation? Location2 { get; init; }

    /// <summary>Primary package target (PTDT subrecord).</summary>
    public PackageTarget? Target { get; init; }

    /// <summary>Secondary package target (PTD2 subrecord).</summary>
    public PackageTarget? Target2 { get; init; }

    /// <summary>Package activation conditions (CTDA* with optional CIS1/CIS2 string parameters).</summary>
    public List<DialogueCondition> Conditions { get; init; } = [];

    /// <summary>Whether this patrol package is repeatable (from PKPT byte[0]).</summary>
    public bool IsRepeatable { get; init; }

    /// <summary>Whether patrol starting location uses linked ref (from PKPT byte[1]).</summary>
    public bool IsStartingLocationLinkedRef { get; init; }

    /// <summary>Whether this package carries the PKED Eat marker.</summary>
    public bool HasEatMarker { get; init; }

    /// <summary>Whether this package carries the PUID Use Item marker.</summary>
    public bool HasUseItemMarker { get; init; }

    /// <summary>Whether this package carries the PKAM Ambush marker.</summary>
    public bool HasAmbushMarker { get; init; }

    /// <summary>Package OnBegin event action block.</summary>
    public PackageEventAction? OnBegin { get; init; }

    /// <summary>Package OnEnd event action block.</summary>
    public PackageEventAction? OnEnd { get; init; }

    /// <summary>Package OnChange event action block.</summary>
    public PackageEventAction? OnChange { get; init; }

    /// <summary>
    ///     Combat style override (CNAM subrecord, TESCombatStyle FormID). On the runtime side
    ///     this comes from <c>TESPackage.pCombatStyle</c> at PDB offset +88.
    /// </summary>
    public uint? CombatStyleFormId { get; init; }

    /// <summary>Human-readable package type name (from PKDT data).</summary>
    public string TypeName => Data?.TypeName ?? "AI Package";

    /// <summary>Offset in the dump where this record was found.</summary>
    public long Offset { get; init; }

    /// <summary>Whether the record was detected as big-endian (Xbox 360).</summary>
    public bool IsBigEndian { get; init; }
}
