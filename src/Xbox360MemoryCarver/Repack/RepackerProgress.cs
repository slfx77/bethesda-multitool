namespace Xbox360MemoryCarver.Repack;

/// <summary>
///     Progress information for the repacker.
/// </summary>
public sealed class RepackerProgress
{
    /// <summary>
    ///     Current processing phase.
    /// </summary>
    public required RepackPhase Phase { get; init; }

    /// <summary>
    ///     Current item being processed (file name, etc.).
    /// </summary>
    public string? CurrentItem { get; init; }

    /// <summary>
    ///     Number of items processed in current phase.
    /// </summary>
    public int ItemsProcessed { get; init; }

    /// <summary>
    ///     Total items in current phase.
    /// </summary>
    public int TotalItems { get; init; }

    /// <summary>
    ///     Progress percentage (0-100).
    /// </summary>
    public double Percentage => TotalItems > 0 ? (double)ItemsProcessed / TotalItems * 100 : 0;

    /// <summary>
    ///     Status message.
    /// </summary>
    public string? Message { get; init; }

    /// <summary>
    ///     Whether the operation completed (success or failure).
    /// </summary>
    public bool IsComplete { get; init; }

    /// <summary>
    ///     Whether the operation was successful.
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    ///     Error message if failed.
    /// </summary>
    public string? Error { get; init; }
}

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
