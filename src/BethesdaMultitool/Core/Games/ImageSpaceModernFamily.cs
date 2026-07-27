namespace BethesdaMultitool.Core.Games;

/// <summary>
///     The two incompatible semantic layouts which share the 36-byte modern IMGS HNAM signature.
///     Lives beside <see cref="GameProfile" /> (not in the ESM record models) because it is a
///     per-game capability value — <see cref="GameProfile.ImageSpaceFamily" /> selects it, the
///     IMGS parser and the tonemap pipeline consume it.
/// </summary>
public enum ImageSpaceModernFamily
{
    Skyrim,
    Fallout4,
}
