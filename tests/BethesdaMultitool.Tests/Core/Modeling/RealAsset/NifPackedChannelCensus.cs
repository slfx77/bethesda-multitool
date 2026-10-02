using System.Globalization;

namespace BethesdaMultitool.Tests.Core.Modeling.RealAsset;

/// <summary>
///     Compared and exactly matching components of one packed channel (positions, normals, UVs or tangents) over a
///     console file: the exact count is a recorded fact of the comparison, the gate being the per-component residue
///     rule in <see cref="NifPackedGeometryComparison" />; a census with no exact component means the residue rule
///     passed vacuously, which the comparison refuses.
/// </summary>
internal sealed class NifPackedChannelCensus
{
    /// <summary>The components compared.</summary>
    public int Compared { get; set; }

    /// <summary>The components equal to the half-rounded PC value (or, on a float3 channel, to the PC bits).</summary>
    public int Exact { get; set; }

    /// <inheritdoc />
    public override string ToString()
    {
        return string.Create(CultureInfo.InvariantCulture,
            $"{Exact}/{Compared} exact ({(Compared == 0 ? 0 : (double)Exact / Compared):P3})");
    }
}
