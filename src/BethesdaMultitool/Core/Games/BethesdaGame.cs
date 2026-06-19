namespace BethesdaMultitool.Core.Games;

/// <summary>
///     The single game/engine identity used across the whole application. Game-specific decisions —
///     plugin framing, the TES3/TES4 parser fork, the armor Damage-Threshold field, and the default
///     landscape texture — resolve from this value via <see cref="GameProfile" /> /
///     <see cref="GameProfiles" />. Detected once (see <see cref="GameDetector" />) and threaded
///     through the pipeline rather than re-derived at each layer.
/// </summary>
public enum BethesdaGame
{
    Unknown = 0,

    /// <summary>Morrowind (TES3): flat records, 16-byte headers, no GRUPs, no FormIDs.</summary>
    Morrowind,

    /// <summary>Oblivion (TES4): 20-byte record + 20-byte GRUP headers (no VCS/version trailer).</summary>
    Oblivion,

    /// <summary>Fallout 3 (TES4): 24-byte headers.</summary>
    Fallout3,

    /// <summary>Fallout: New Vegas (TES4): 24-byte headers.</summary>
    FalloutNewVegas,

    /// <summary>Skyrim / Skyrim Special Edition (TES4): 24-byte headers, localized strings, two moons.</summary>
    Skyrim,

    /// <summary>Fallout 4 (TES4): 24-byte headers, localized strings, BA2 archives.</summary>
    Fallout4,

    /// <summary>Fallout 76 (TES4): 24-byte headers; main master <c>SeventySix.esm</c>.</summary>
    Fallout76,

    /// <summary>Starfield (TES4): 24-byte headers.</summary>
    Starfield
}
