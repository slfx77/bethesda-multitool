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
}
