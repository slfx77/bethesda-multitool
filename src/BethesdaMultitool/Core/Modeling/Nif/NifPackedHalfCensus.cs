using System.Globalization;
using System.Text.Json.Nodes;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     What the fourth half of one half4 channel held: the measurement found it to be exactly 1.0 on every retail
///     vertex of every position, normal, bitangent and tangent channel, so it carries nothing; a decoder records the
///     census instead of interpreting it (it is not the bitangent sign).
/// </summary>
/// <param name="Kind">The channel.</param>
/// <param name="Ones">Vertices whose fourth half is exactly 1.0 (bits 0x3C00).</param>
/// <param name="Others">Vertices whose fourth half is anything else.</param>
/// <param name="FirstOtherBits">The bits of the first fourth half that is not 1.0, or null when all are.</param>
internal sealed record NifPackedHalfCensus(NifPackedStreamKind Kind, int Ones, int Others, ushort? FirstOtherBits)
{
    /// <summary>The census as native state.</summary>
    public JsonObject ToJson()
    {
        var node = new JsonObject
        {
            ["stream"] = Kind.ToString(),
            ["fourthHalfExactlyOne"] = Ones,
            ["fourthHalfOther"] = Others,
            ["rule"] = "the fourth half is a constant 1.0 (measured on 100% of retail vertices) and is not " +
                       "interpreted; it is not the bitangent sign"
        };
        if (FirstOtherBits is { } bits)
        {
            node["firstOtherBits"] = string.Create(CultureInfo.InvariantCulture, $"0x{bits:X4}");
            node["firstOtherValue"] = NifModelNativeValues.Float(NifPackedHalf.ToSingle(bits));
        }

        return node;
    }
}
