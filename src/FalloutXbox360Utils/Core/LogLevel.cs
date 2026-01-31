namespace FalloutXbox360Utils.Core;

/// <summary>
///     Verbosity levels for logging.
/// </summary>
public enum LogLevel
{
    /// <summary>No output.</summary>
    None = 0,

    /// <summary>Errors only.</summary>
    Error = 1,

    /// <summary>Warnings and above.</summary>
    Warn = 2,

    /// <summary>Informational messages and above.</summary>
    Info = 3,

    /// <summary>Debug/verbose output and above.</summary>
    Debug = 4,

    /// <summary>Trace-level output (most verbose).</summary>
    Trace = 5
}
