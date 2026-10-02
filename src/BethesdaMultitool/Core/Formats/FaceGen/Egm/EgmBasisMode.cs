using System.Numerics;

namespace BethesdaMultitool.Core.Formats.FaceGen.Egm;

/// <summary>One original dense principal-component basis mode, retaining its packed representation.</summary>
public sealed class EgmBasisMode
{
    /// <summary>Owns the parser's validated packed coordinate array and finite source scale.</summary>
    /// <param name="scale">The finite source multiplier, already checked against every packed component.</param>
    /// <param name="packedDeltas">The parser-owned complete XYZ array, transferred without another copy.</param>
    internal EgmBasisMode(float scale, short[] packedDeltas)
    {
        Scale = scale;
        PackedDeltas = packedDeltas;
    }

    /// <summary>Original finite multiplier for every signed short in this mode.</summary>
    public float Scale { get; }
    /// <summary>Original dense XYZ triples over the complete EGM domain, including statistical suffix vertices.</summary>
    public ReadOnlyMemory<short> PackedDeltas { get; }

    /// <summary>Returns one decoded basis displacement in source vertex order.</summary>
    /// <param name="vertexIndex">The zero-based index in the complete EGM domain, including statistical targets.</param>
    /// <returns>The original packed XYZ triple multiplied by its source scale.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The index is outside the complete domain.</exception>
    public Vector3 GetDelta(int vertexIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(vertexIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(vertexIndex, PackedDeltas.Length / 3);
        var values = PackedDeltas.Span;
        var offset = vertexIndex * 3;
        return new Vector3(values[offset] * Scale, values[offset + 1] * Scale, values[offset + 2] * Scale);
    }
}
