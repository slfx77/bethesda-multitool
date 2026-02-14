namespace FalloutXbox360Utils.Core.Formats.Esm;

/// <summary>
///     Computes field offset shift from Proto Debug PDB values to actual dump values.
///     All known dumps (Debug, Release Beta, Release MemDebug, Release) use TESForm = 40 bytes,
///     matching the Release/Final Debug PDB. The Proto Debug PDB (Jul 2010, TESForm = 24 bytes)
///     doesn't match any available crash dumps — the Debug dumps use a Final Debug build.
///     Shift = +16 from Proto Debug PDB offsets to actual dump offsets for all builds.
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
        0x45 => "DIAL",
        0x47 => "QUST",
        _ => null
    };
}
