using System.Globalization;

namespace BethesdaMultitool.Tests.Core.Modeling.RealAsset;

/// <summary>The outcome of one console-against-PC geometry comparison (<see cref="NifPackedGeometryComparison.Compare" />).</summary>
/// <param name="Shapes">Shapes paired by ordinal and compared.</param>
/// <param name="PackedShapes">Shapes whose streams were packed (compared under the format's precision).</param>
/// <param name="InlineShapes">Shapes with inline streams inside a packed file (compared bit for bit).</param>
/// <param name="Layouts">Every packed layout id seen, plus <c>inline</c> for inline shapes.</param>
/// <param name="Tally">The per-channel counts.</param>
/// <param name="OrderSensitiveDecoded">Decoded packed vertex colors whose R, G, B are not all equal (the reader's byte rule; reconciled with the reported colorOrderSensitiveVertices).</param>
/// <param name="OrderDiscriminating">Decoded packed vertex colors whose R, G, B span at least two bytes, the only ones on which the other platform's order fails ColorsAgree.</param>
/// <param name="MovedVertexControl">
///     A packed position component that matched exactly with |PC value| at least 1/4 (console value, PC value), for the
///     moved-vertex control; null when the file has none.
/// </param>
internal sealed record NifPackedGeometryComparisonResult(int Shapes, int PackedShapes, int InlineShapes,
    IReadOnlySet<string> Layouts, NifPackedGeometryTally Tally, int OrderSensitiveDecoded, int OrderDiscriminating,
    (float Console, float Pc)? MovedVertexControl)
{
    /// <summary>One line for the test output.</summary>
    public string Summary(string relativePath, string platform)
    {
        return string.Create(CultureInfo.InvariantCulture,
            $"{relativePath} ({platform}, {string.Join(",", Layouts.Order())}): {Shapes} shape(s), {PackedShapes} " +
            $"packed, {InlineShapes} inline; {Tally} ({OrderSensitiveDecoded} order-sensitive colors, " +
            $"{OrderDiscriminating} spanning two bytes or more). The exact " +
            $"fractions are recorded facts, not gates: the gate is the per-component residue rule.");
    }
}
