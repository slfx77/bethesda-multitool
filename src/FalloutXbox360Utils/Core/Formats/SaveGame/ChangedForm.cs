namespace FalloutXbox360Utils.Core.Formats.SaveGame;

/// <summary>
///     A Changed Form entry from the save file body.
///     Represents a game object that has been modified from its base ESM definition.
/// </summary>
public sealed class ChangedForm
{
    /// <summary>3-byte RefID identifying this form.</summary>
    public SaveRefId RefId { get; init; }

    /// <summary>Bit flags indicating which aspects of the form have changed.</summary>
    public uint ChangeFlags { get; init; }

    /// <summary>Change form type (0-54 for FNV).</summary>
    public byte ChangeType { get; init; }

    /// <summary>Form version.</summary>
    public byte Version { get; init; }

    /// <summary>Raw data bytes for this changed form.</summary>
    public byte[] Data { get; init; } = [];

    /// <summary>Parsed initial data (position, cell) if applicable.</summary>
    public InitialData? Initial { get; init; }

    /// <summary>Human-readable type name from the FNV change type enum.</summary>
    public string TypeName => ChangeType switch
    {
        0 => "REFR",
        1 => "ACHR",
        2 => "ACRE",
        3 => "PMIS",
        4 => "PGRE",
        5 => "PBEA",
        6 => "PFLA",
        7 => "CELL",
        8 => "INFO",
        9 => "QUST",
        10 => "NPC_",
        11 => "CREA",
        12 => "ACTI",
        13 => "TACT",
        14 => "TERM",
        15 => "ARMO",
        16 => "BOOK",
        17 => "CLOT",
        18 => "CONT",
        19 => "DOOR",
        20 => "INGR",
        21 => "LIGH",
        22 => "MISC",
        23 => "STAT",
        24 => "MSTT",
        25 => "FURN",
        26 => "WEAP",
        27 => "AMMO",
        28 => "KEYM",
        29 => "ALCH",
        30 => "IDLM",
        31 => "NOTE",
        32 => "ECZN",
        33 => "CLAS",
        34 => "FACT",
        35 => "PACK",
        36 => "NAVM",
        37 => "FLST",
        38 => "LVLC",
        39 => "LVLN",
        40 => "LVLI",
        41 => "WATR",
        42 => "IMOD",
        43 => "REPU",
        44 => "PCBE",
        45 => "RCPE",
        46 => "RCCT",
        47 => "CHIP",
        48 => "CSNO",
        49 => "LSCT",
        50 => "CHAL",
        51 => "AMEF",
        52 => "CCRD",
        53 => "CMNY",
        54 => "CDCK",
        _ => $"Unknown({ChangeType})"
    };

    /// <summary>Whether this is a reference type (REFR, ACHR, ACRE, projectiles, PCBE).</summary>
    public bool IsReferenceType => ChangeType is 0 or 1 or 2 or 3 or 4 or 5 or 6 or 44;

    /// <summary>Whether this is an actor type (ACHR or ACRE).</summary>
    public bool IsActorType => ChangeType is 1 or 2;
}
