// FaceGen control projection methods

namespace FalloutXbox360Utils.Core.Formats.Esm.FaceGen;

/// <summary>
///     FaceGen linear control definitions parsed from si.ctl.
///     Each control defines a named direction in face space.
///     The GECK's Face Advanced slider values are projections of
///     NPC face coordinates onto these control directions.
/// </summary>
public static class FaceGenControls
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
        var results = new (string Name, float Value)[FaceGenGeometrySymmetricData.GeometrySymmetricNames.Length];
        for (var j = 0; j < FaceGenGeometrySymmetricData.GeometrySymmetricNames.Length; j++)
        {
            float dot = 0;
            for (var i = 0; i < 50; i++)
            {
                var merged = fggs[i] + (raceBase is { Length: 50 } ? raceBase[i] : 0f);
                dot += FaceGenGeometrySymmetricData.GeometrySymmetricCoeffs[j][i] * merged;
            }

            results[j] = (FaceGenGeometrySymmetricData.GeometrySymmetricNames[j], dot);
        }

        return results;
    }

    public static (string Name, float Value)[] ComputeGeometryAsymmetric(float[] fgga, float[]? raceBase = null)
    {
        if (fgga.Length != 30) return [];
        var results = new (string Name, float Value)[FaceGenGeometryAsymmetricData.GeometryAsymmetricNames.Length];
        for (var j = 0; j < FaceGenGeometryAsymmetricData.GeometryAsymmetricNames.Length; j++)
        {
            float dot = 0;
            for (var i = 0; i < 30; i++)
            {
                var merged = fgga[i] + (raceBase is { Length: 30 } ? raceBase[i] : 0f);
                dot += FaceGenGeometryAsymmetricData.GeometryAsymmetricCoeffs[j][i] * merged;
            }

            results[j] = (FaceGenGeometryAsymmetricData.GeometryAsymmetricNames[j], dot);
        }

        return results;
    }

    public static (string Name, float Value)[] ComputeTextureSymmetric(float[] fgts, float[]? raceBase = null)
    {
        if (fgts.Length != 50) return [];
        var results = new (string Name, float Value)[FaceGenTextureSymmetricData.TextureSymmetricNames.Length];
        for (var j = 0; j < FaceGenTextureSymmetricData.TextureSymmetricNames.Length; j++)
        {
            float dot = 0;
            for (var i = 0; i < 50; i++)
            {
                var merged = fgts[i] + (raceBase is { Length: 50 } ? raceBase[i] : 0f);
                dot += FaceGenTextureSymmetricData.TextureSymmetricCoeffs[j][i] * merged;
            }

            results[j] = (FaceGenTextureSymmetricData.TextureSymmetricNames[j], dot);
        }

        return results;
    }
}
