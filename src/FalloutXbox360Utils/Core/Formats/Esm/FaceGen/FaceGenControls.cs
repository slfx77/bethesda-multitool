// FaceGen control projection methods

namespace FalloutXbox360Utils.Core.Formats.Esm;

/// <summary>
///     FaceGen linear control definitions parsed from si.ctl.
///     Each control defines a named direction in face space.
///     The GECK's Face Advanced slider values are projections of
///     NPC face coordinates onto these control directions.
/// </summary>
public static partial class FaceGenControls
{
    /// <summary>
    ///     Compute control projection values from FGGS basis coefficients.
    ///     The NPC's FGGS data is an offset from the race's base face.
    ///     If race base coefficients are provided, they are merged (added) before projection.
    ///     Returns an array of (name, value) pairs for each control.
    /// </summary>
    public static (string Name, float Value)[] ComputeGeometrySymmetric(float[] fggs, float[]? raceBase = null)
    {
        if (fggs.Length != 50) return [];
        var results = new (string Name, float Value)[GeometrySymmetricNames.Length];
        for (var j = 0; j < GeometrySymmetricNames.Length; j++)
        {
            float dot = 0;
            for (var i = 0; i < 50; i++)
            {
                var merged = fggs[i] + (raceBase is { Length: 50 } ? raceBase[i] : 0f);
                dot += GeometrySymmetricCoeffs[j][i] * merged;
            }

            results[j] = (GeometrySymmetricNames[j], dot);
        }

        return results;
    }

    public static (string Name, float Value)[] ComputeGeometryAsymmetric(float[] fgga, float[]? raceBase = null)
    {
        if (fgga.Length != 30) return [];
        var results = new (string Name, float Value)[GeometryAsymmetricNames.Length];
        for (var j = 0; j < GeometryAsymmetricNames.Length; j++)
        {
            float dot = 0;
            for (var i = 0; i < 30; i++)
            {
                var merged = fgga[i] + (raceBase is { Length: 30 } ? raceBase[i] : 0f);
                dot += GeometryAsymmetricCoeffs[j][i] * merged;
            }

            results[j] = (GeometryAsymmetricNames[j], dot);
        }

        return results;
    }

    public static (string Name, float Value)[] ComputeTextureSymmetric(float[] fgts, float[]? raceBase = null)
    {
        if (fgts.Length != 50) return [];
        var results = new (string Name, float Value)[TextureSymmetricNames.Length];
        for (var j = 0; j < TextureSymmetricNames.Length; j++)
        {
            float dot = 0;
            for (var i = 0; i < 50; i++)
            {
                var merged = fgts[i] + (raceBase is { Length: 50 } ? raceBase[i] : 0f);
                dot += TextureSymmetricCoeffs[j][i] * merged;
            }

            results[j] = (TextureSymmetricNames[j], dot);
        }

        return results;
    }
}
