using BethesdaMultitool.Core.Formats.Nif.Rendering.Water;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool;

public sealed partial class WorldView3DControl
{
    /// <summary>
    ///     Diagnostic input for the recovered WATER007 normal consumer. This binds a known texture
    ///     to discriminate composition and descriptor plumbing; it does not simulate retail rain,
    ///     actor wading, or WATERDISPLACE000..007. The default route remains without a source.
    /// </summary>
    private void BindOblivionDisplacementDiagnostic()
    {
        if (_water is null)
        {
            return;
        }

        // SetGame clears this state. Rebind for both exterior and interior scene selection, and
        // explicitly clear invalid/disabled probes so a former selection cannot leak its source.
        _water.SetOblivionDisplacementTexture(null, 0f, 0f);
        if (_data?.Game != BethesdaGame.Oblivion || _textureResolver12 is null)
        {
            return;
        }

        var mode = OblivionWaterDisplacementComposition.ParseProbeMode(
            Environment.GetEnvironmentVariable(
                Core.EnvironmentVariables.Viewer.OblivionWaterDisplacementProbe));
        if (mode == OblivionWaterDisplacementComposition.ProbeMode.Disabled)
        {
            return;
        }

        var key = "diagnostic:oblivion-water-displacement:rgba8-nomips:" +
                  OblivionWaterDisplacementComposition.GetProbeKey(mode);
        var pixels = OblivionWaterDisplacementComposition.GenerateProbeTexture(mode);
        var index = _textureResolver12.GetOrCreateSyntheticBindlessIndex(
            key,
            OblivionWaterDisplacementComposition.ProbeTextureSize,
            OblivionWaterDisplacementComposition.ProbeTextureSize,
            pixels,
            generateMips: false);
        var amount = OblivionWaterDisplacementComposition.GetProbeBlendAmount(mode);
        _water.SetOblivionDisplacementTexture(
            index, OblivionWaterDisplacementComposition.ProbeBlendRadius, amount, key);
        Log.Info(
            "[Water] Diagnostic WATER007 displacement input={0}; grid=256x256, " +
            "output=R8G8B8A8_UNorm, levels=1, blend={1}; WATERDISPLACE simulation unavailable.",
            key, amount);
    }
}
