using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Assets;

/// <summary>
///     Resolves the morph file paired with a FaceGen hair NIF.
/// </summary>
internal static class FaceGenHairEgmPathResolver
{
    internal static string Build(BethesdaGame game, string hairNifPath, bool useHatGeometry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hairNifPath);

        // TES4 stores one sibling EGM per HAIR model. Hat/NoHat names select geometry inside the
        // NIF; they are not suffixes on the EGM asset (for example Style03.NIF -> Style03.egm).
        if (game == BethesdaGame.Oblivion)
        {
            return Path.ChangeExtension(hairNifPath, ".egm");
        }

        var hairBaseName = Path.GetFileNameWithoutExtension(hairNifPath);
        var hairDirectory = Path.GetDirectoryName(hairNifPath) ?? string.Empty;
        var egmSuffix = useHatGeometry ? "hat.egm" : "nohat.egm";
        return Path.Combine(hairDirectory, hairBaseName + egmSuffix);
    }
}
