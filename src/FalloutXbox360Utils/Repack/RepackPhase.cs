namespace FalloutXbox360Utils.Repack;

/// <summary>
///     Processing phases for the repacker.
/// </summary>
public enum RepackPhase
{
    /// <summary>Validating source folder.</summary>
    Validating,

    /// <summary>Processing Video folder.</summary>
    Video,

    /// <summary>Processing Music folder.</summary>
    Music,

    /// <summary>Processing BSA files.</summary>
    Bsa,

    /// <summary>Processing ESM files.</summary>
    Esm,

    /// <summary>Processing ESP files.</summary>
    Esp,

    /// <summary>Processing INI file.</summary>
    Ini,

    /// <summary>All processing complete.</summary>
    Complete
}
