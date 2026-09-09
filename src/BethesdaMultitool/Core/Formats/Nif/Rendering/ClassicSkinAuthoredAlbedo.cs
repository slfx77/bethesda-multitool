using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering;

/// <summary>
///     Original stock TES4 head maps retained beside the composed CPU/export texture. The manager's
///     recovered default Map1 is spatially constant RGB64; no authored age/detail override is modeled.
/// </summary>
internal sealed record ClassicSkinAuthoredAlbedo
{
    private ClassicSkinAuthoredAlbedo(string baseTexturePath, string deltaTexturePath)
    {
        BaseTexturePath = baseTexturePath;
        DeltaTexturePath = deltaTexturePath;
    }

    internal string BaseTexturePath { get; }
    internal string DeltaTexturePath { get; }

    internal static ClassicSkinAuthoredAlbedo? Create(
        BethesdaGame game, string? baseTexturePath, string? deltaTexturePath)
    {
        return game == BethesdaGame.Oblivion &&
               !string.IsNullOrWhiteSpace(baseTexturePath) &&
               !string.IsNullOrWhiteSpace(deltaTexturePath)
            ? new ClassicSkinAuthoredAlbedo(baseTexturePath, deltaTexturePath)
            : null;
    }
}
