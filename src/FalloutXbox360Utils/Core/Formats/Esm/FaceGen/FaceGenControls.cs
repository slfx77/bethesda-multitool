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
    ///     Compute control projection values from FGGS/FGGA/FGTS basis coefficients.
    ///     Returns an array of (name, value) pairs for each control.
    /// </summary>
    public static (string Name, float Value)[] ComputeGeometrySymmetric(float[] fggs)
    {
        if (fggs.Length != 50) return Array.Empty<(string, float)>();
        var results = new (string Name, float Value)[GeometrySymmetricNames.Length];
        for (var j = 0; j < GeometrySymmetricNames.Length; j++)
        {
            float dot = 0;
            for (var i = 0; i < 50; i++)
                dot += GeometrySymmetricCoeffs[j][i] * fggs[i];
            results[j] = (GeometrySymmetricNames[j], dot);
        }

        return results;
    }

    public static (string Name, float Value)[] ComputeGeometryAsymmetric(float[] fgga)
    {
        if (fgga.Length != 30) return Array.Empty<(string, float)>();
        var results = new (string Name, float Value)[GeometryAsymmetricNames.Length];
        for (var j = 0; j < GeometryAsymmetricNames.Length; j++)
        {
            float dot = 0;
            for (var i = 0; i < 30; i++)
                dot += GeometryAsymmetricCoeffs[j][i] * fgga[i];
            results[j] = (GeometryAsymmetricNames[j], dot);
        }

        return results;
    }

    public static (string Name, float Value)[] ComputeTextureSymmetric(float[] fgts)
    {
        if (fgts.Length != 50) return Array.Empty<(string, float)>();
        var results = new (string Name, float Value)[TextureSymmetricNames.Length];
        for (var j = 0; j < TextureSymmetricNames.Length; j++)
        {
            float dot = 0;
            for (var i = 0; i < 50; i++)
                dot += TextureSymmetricCoeffs[j][i] * fgts[i];
            results[j] = (TextureSymmetricNames[j], dot);
        }

        return results;
    }
}
