using System.Numerics;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;

internal readonly record struct NifMorphScalarKey(float Time, float Value, float InTangent = 0f, float OutTangent = 0f);

internal sealed record NifMorphScalarCurve(
    NifKeyInterpolation Interpolation,
    float FallbackWeight,
    NifMorphScalarKey[] Keys);

internal sealed record NifGeometryMorphTarget(string Name, Vector3[] Positions, NifMorphScalarCurve Curve);

/// <summary>Ordinary relative morph data; target zero is absolute, later targets are stored deltas.</summary>
internal sealed record NifGeometryMorphData(
    int SourceBlockIndex,
    float Frequency,
    float Phase,
    float StartTime,
    float StopTime,
    bool Loops,
    NifGeometryMorphTarget[] Targets)
{
    internal int VertexCount => Targets[0].Positions.Length;

    internal NifGeometryMorphData Snapshot()
    {
        return this with
        {
            Targets = Targets.Select(static target => target with
            {
                Positions = (Vector3[])target.Positions.Clone(),
                Curve = target.Curve with { Keys = (NifMorphScalarKey[])target.Curve.Keys.Clone() }
            }).ToArray()
        };
    }
}

/// <summary>
///     Scalar Bezier and relative composition recovered from NiBezFloatKey and
///     NiGeomMorpherController::GenMorphInterp in the matching local FNV debug engine.
/// </summary>
internal static class NifGeometryMorphEvaluator
{
    internal static float Sample(NifMorphScalarCurve curve, float time)
    {
        var keys = curve.Keys;
        if (keys.Length == 0) return curve.FallbackWeight;
        if (time <= keys[0].Time) return keys[0].Value;
        if (time >= keys[^1].Time) return keys[^1].Value;
        var lower = 0;
        var upper = keys.Length - 1;
        while (upper - lower > 1)
        {
            var middle = lower + (upper - lower) / 2;
            if (keys[middle].Time <= time) lower = middle;
            else upper = middle;
        }

        var earlier = keys[lower];
        var later = keys[upper];
        var fraction = (time - earlier.Time) / (later.Time - earlier.Time);
        var delta = later.Value - earlier.Value;
        if (curve.Interpolation == NifKeyInterpolation.Linear)
        {
            return earlier.Value + fraction * delta;
        }
        if (curve.Interpolation != NifKeyInterpolation.Quadratic)
        {
            throw new InvalidDataException("Geometry morph scalar interpolation is unsupported.");
        }

        // Tangents are already segment-scaled: earlier Backward/out, later Forward/in.
        var a = 3f * delta - (later.InTangent + 2f * earlier.OutTangent);
        var b = earlier.OutTangent + later.InTangent - 2f * delta;
        return earlier.Value + fraction * (earlier.OutTangent + fraction * (a + fraction * b));
    }

    internal static void SampleWeights(NifGeometryMorphData morph, float time, Span<float> weights)
    {
        if (weights.Length != morph.Targets.Length)
        {
            throw new ArgumentException("Morph weight count must match authored targets.", nameof(weights));
        }

        weights[0] = 1f;
        for (var target = 1; target < weights.Length; target++)
        {
            // A successful interpolator replaces the current weight. It never multiplies it.
            weights[target] = Sample(morph.Targets[target].Curve, time);
        }
    }

    internal static Vector3 EvaluatePosition(NifGeometryMorphData morph, ReadOnlySpan<float> weights, int vertex)
    {
        var position = morph.Targets[0].Positions[vertex];
        for (var target = 1; target < morph.Targets.Length; target++)
        {
            var weight = weights[target];
            if (weight > -0.001f && weight < 0.001f) continue;
            position += weight * morph.Targets[target].Positions[vertex];
        }

        return position;
    }

    internal static (Vector3 Minimum, Vector3 Maximum) GetConservativeBounds(NifGeometryMorphData morph)
    {
        var intervals = morph.Targets.Select(static target => GetWeightInterval(target.Curve)).ToArray();
        var minimum = new Vector3(float.PositiveInfinity);
        var maximum = new Vector3(float.NegativeInfinity);
        for (var vertex = 0; vertex < morph.VertexCount; vertex++)
        {
            var low = morph.Targets[0].Positions[vertex];
            var high = low;
            for (var target = 1; target < morph.Targets.Length; target++)
            {
                var delta = morph.Targets[target].Positions[vertex];
                var a = delta * intervals[target].Minimum;
                var b = delta * intervals[target].Maximum;
                low += Vector3.Min(a, b);
                high += Vector3.Max(a, b);
            }

            minimum = Vector3.Min(minimum, low);
            maximum = Vector3.Max(maximum, high);
        }

        // Cover float accumulation rounding at the interval extrema.
        var margin = Vector3.Max(Vector3.Abs(minimum), Vector3.Abs(maximum)) * 1e-4f + new Vector3(1e-4f);
        return (minimum - margin, maximum + margin);
    }

    private static (float Minimum, float Maximum) GetWeightInterval(NifMorphScalarCurve curve)
    {
        // Include zero because the engine suppresses the open interval (-0.001,+0.001).
        var low = Math.Min(0d, curve.FallbackWeight);
        var high = Math.Max(0d, curve.FallbackWeight);
        foreach (var key in curve.Keys)
        {
            low = Math.Min(low, key.Value);
            high = Math.Max(high, key.Value);
        }

        if (curve.Interpolation == NifKeyInterpolation.Quadratic)
        {
            for (var index = 1; index < curve.Keys.Length; index++)
            {
                var earlier = curve.Keys[index - 1];
                var later = curve.Keys[index];
                var delta = (double)later.Value - earlier.Value;
                var a = 3d * delta - (later.InTangent + 2d * earlier.OutTangent);
                var b = earlier.OutTangent + (double)later.InTangent - 2d * delta;
                var discriminant = 4d * a * a - 12d * b * earlier.OutTangent;
                if (b.Equals(0d))
                {
                    if (!a.Equals(0d)) Include(-earlier.OutTangent / (2d * a));
                }
                else if (discriminant >= 0d)
                {
                    var root = Math.Sqrt(discriminant);
                    Include((-2d * a - root) / (6d * b));
                    Include((-2d * a + root) / (6d * b));
                }

                void Include(double u)
                {
                    if (u <= 0d || u >= 1d) return;
                    var value = earlier.Value + u * (earlier.OutTangent + u * (a + u * b));
                    low = Math.Min(low, value);
                    high = Math.Max(high, value);
                }
            }
        }

        return ((float)low, (float)high);
    }
}
