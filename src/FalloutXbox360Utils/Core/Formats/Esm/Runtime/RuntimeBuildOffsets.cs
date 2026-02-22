namespace FalloutXbox360Utils.Core.Formats.Esm;

/// <summary>
///     Computes field offset shifts between early-era and final-era Xbox 360 builds.
///     Final-era builds (from ~March 30, 2010 onwards) use REFR = 120 bytes.
///     Early-era builds (before ~March 30, 2010) use REFR = 116 bytes.
///     The difference is a 4-byte field in TESChildCell (vtable-only in early builds,
///     vtable + 4B data in final builds). This shifts all OBJ_REFR and later fields by -4.
///     The TESForm base class is 40 bytes in both eras.
///     Note: The Proto Debug PDB (July 2010, TESForm=24) does NOT match these early DMPs.
/// </summary>
internal static class RuntimeBuildOffsets
{
    /// <summary>
    ///     Returns the field offset shift from Proto Debug PDB values to actual dump values.
    ///     Currently +16 for all known builds. The mechanism is retained for future extensibility
    ///     in case a Proto Debug era dump is ever encountered (would need +4 shift).
    /// </summary>
    public static int GetPdbShift(string? buildType) => 16;

    /// <summary>
    ///     Returns the REFR field offset delta for early-era builds.
    ///     Early builds: OBJ_REFR and BSExtraList fields are 4 bytes earlier
    ///     because TESChildCell is vtable-only (4B) vs vtable+data (8B) in final.
    ///     Only applies to REFR/ACHR/ACRE forms that inherit from TESChildCell.
    /// </summary>
    public static int GetRefrFieldShift(bool isEarlyBuild) => isEarlyBuild ? -4 : 0;

    /// <summary>
    ///     Returns the field offset delta for WRLD and CELL forms in early-era builds.
    ///     These forms do NOT inherit from TESChildCell, so the shift mechanism differs.
    ///     Empirically, early-era WRLD cell maps still resolve with some shift — TBD via Ghidra.
    ///     For now, returns 0 (use final offsets) since the exact early WRLD/CELL layout is unknown.
    /// </summary>
    public static int GetWorldCellFieldShift(bool isEarlyBuild) => 0;

    /// <summary>
    ///     Returns the approximate C++ struct size for a given FormType byte.
    ///     Sizes are PDB base sizes + build shift. Used for memory map region sizing.
    /// </summary>
    public static int GetStructSize(byte formType, int shift = 16) => formType switch
    {
        0x08 => 76 + shift,   // TESFaction (FACT)
        0x11 => 84 + shift,   // Script (SCPT)
        0x17 => 168 + shift,  // BGSTerminal (TERM)
        0x18 => 400 + shift,  // TESObjectARMO (ARMO)
        0x1B => 156 + shift,  // TESObjectCONT (CONT)
        0x1F => 172 + shift,  // TESObjectMISC (MISC)
        0x28 => 908 + shift,  // TESObjectWEAP (WEAP)
        0x29 => 220 + shift,  // TESObjectAMMO (AMMO)
        0x2A => 492 + shift,  // TESNPC (NPC_)
        0x2B => 352 + shift,  // TESCreature (CREA)
        0x2E => 172 + shift,  // TESKey (KEYM)
        0x2F => 216 + shift,  // TESObjectALCH (ALCH)
        0x31 => 128 + shift,  // BGSNote (NOTE)
        0x33 => 208 + shift,  // BGSProjectile (PROJ)
        0x39 => 192 + shift,  // TESObjectCELL (CELL)
        0x3A => 120,          // TESObjectREFR (REFR) — Final PDB, no shift
        0x3B => 120,          // Character (ACHR) — base size, subclass may be larger
        0x3C => 120,          // Creature (ACRE) — base size, subclass may be larger
        0x41 => 244 + shift,  // TESWorldSpace (WRLD)
        0x47 => 108 + shift,  // TESQuest (QUST)
        _ => 40               // Base TESForm size (all builds)
    };

    /// <summary>
    ///     Maps a FormType byte to its 4-letter record type code.
    ///     Returns null for unrecognized types.
    /// </summary>
    public static string? GetRecordTypeCode(byte formType) => formType switch
    {
        0x08 => "FACT",
        0x11 => "SCPT",
        0x17 => "TERM",
        0x18 => "ARMO",
        0x19 => "ACTI",
        0x1B => "CONT",
        0x1F => "MISC",
        0x28 => "WEAP",
        0x29 => "AMMO",
        0x2A => "NPC_",
        0x2B => "CREA",
        0x2E => "KEYM",
        0x2F => "ALCH",
        0x31 => "NOTE",
        0x33 => "PROJ",
        0x39 => "CELL",
        0x3A => "REFR",
        0x3B => "ACHR",
        0x3C => "ACRE",
        0x41 => "WRLD",
        0x45 => "DIAL",
        0x47 => "QUST",
        _ => null
    };
}
