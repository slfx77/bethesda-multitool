namespace FalloutXbox360Utils.Core.Formats.SaveGame;

/// <summary>
///     How the payload was extracted from the STFS container.
/// </summary>
internal enum StfsExtractionMethod
{
    /// <summary>Standard STFS file table extraction.</summary>
    Standard,

    /// <summary>Found via scanning blocks for file table entry.</summary>
    RecoveryScan,

    /// <summary>Found via brute-force magic string search.</summary>
    BruteForce,

    /// <summary>All extraction methods failed.</summary>
    Failed
}
