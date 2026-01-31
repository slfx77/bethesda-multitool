namespace FalloutXbox360Utils.Core.Formats.Nif.Conversion;

/// <summary>
///     Result from parsing palette and controller sequence blocks.
/// </summary>
internal sealed class NifNameMappings
{
    /// <summary>
    ///     Block index -> name mappings from NiDefaultAVObjectPalette.
    /// </summary>
    public Dictionary<int, string> BlockNames { get; } = [];

    /// <summary>
    ///     The Accum Root Name from NiControllerSequence (root node name).
    ///     This is the BSFadeNode/NiNode root name for the animation system.
    /// </summary>
    public string? AccumRootName { get; set; }
}
