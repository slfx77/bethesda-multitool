using System.Numerics;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;

/// <summary>
///     Evaluates the degree-three open-uniform basis used by <c>NiBSplineBasisData</c>. The knot
///     construction matches NifTools' public <c>compute_intervals</c> source and the retail FNV
///     engine's <c>NiBSplineBasis&lt;float,3&gt;</c> type recovered from its debug PDB.
/// </summary>
internal static class NifOpenUniformCubicBspline
{
    internal const int Degree = 3;
    internal const int MinimumControlPointCount = Degree + 1;

    internal static Vector3 Sample(
        Vector3[] controlPoints,
        float startTime,
        float stopTime,
        float time)
    {
        var basis = ComputeBasis(controlPoints.Length, startTime, stopTime, time);
        var value = Vector3.Zero;
        for (var index = 0; index < basis.Count; index++)
        {
            value += controlPoints[basis.FirstControlPoint + index] * basis[index];
        }

        return value;
    }

    internal static Quaternion Sample(
        Quaternion[] controlPoints,
        float startTime,
        float stopTime,
        float time)
    {
        var basis = ComputeBasis(controlPoints.Length, startTime, stopTime, time);
        var x = 0d;
        var y = 0d;
        var z = 0d;
        var w = 0d;
        for (var index = 0; index < basis.Count; index++)
        {
            var controlPoint = controlPoints[basis.FirstControlPoint + index];
            var weight = (double)basis[index];
            x += controlPoint.X * weight;
            y += controlPoint.Y * weight;
            z += controlPoint.Z * weight;
            w += controlPoint.W * weight;
        }

        var value = new Quaternion((float)x, (float)y, (float)z, (float)w);
        var lengthSquared = value.LengthSquared();
        if (!float.IsFinite(lengthSquared) || lengthSquared <= 1e-12f)
        {
            throw new InvalidDataException("B-spline rotation evaluated to a non-finite or zero quaternion.");
        }

        return Quaternion.Normalize(value);
    }

    internal static float Sample(
        float[] controlPoints,
        float startTime,
        float stopTime,
        float time)
    {
        var basis = ComputeBasis(controlPoints.Length, startTime, stopTime, time);
        var value = 0d;
        for (var index = 0; index < basis.Count; index++)
        {
            value += controlPoints[basis.FirstControlPoint + index] * (double)basis[index];
        }

        var result = (float)value;
        if (!float.IsFinite(result))
        {
            throw new InvalidDataException("B-spline scale evaluated to a non-finite value.");
        }

        return result;
    }

    /// <summary>
    ///     Computes the four non-zero Cox-de Boor basis values for an open-uniform cubic spline.
    ///     The right endpoint is exactly the final control point, matching NifTools' source.
    /// </summary>
    internal static Basis ComputeBasis(
        int controlPointCount,
        float startTime,
        float stopTime,
        float time)
    {
        if (controlPointCount < MinimumControlPointCount ||
            !float.IsFinite(startTime) ||
            !float.IsFinite(stopTime) ||
            stopTime <= startTime ||
            !float.IsFinite(time))
        {
            throw new InvalidDataException("B-spline basis inputs are malformed.");
        }

        if (time <= startTime)
        {
            return new Basis(0, 1f, 0f, 0f, 0f, 1);
        }

        if (time >= stopTime)
        {
            return new Basis(controlPointCount - 1, 1f, 0f, 0f, 0f, 1);
        }

        var parameterEnd = controlPointCount - Degree;
        var normalized = ((double)time - startTime) / ((double)stopTime - startTime);
        var parameter = normalized * parameterEnd;
        var span = Math.Min(
            controlPointCount - 1,
            Degree + (int)Math.Floor(parameter));

        Span<double> values = stackalloc double[Degree + 1];
        Span<double> left = stackalloc double[Degree + 1];
        Span<double> right = stackalloc double[Degree + 1];
        values[0] = 1d;
        for (var order = 1; order <= Degree; order++)
        {
            left[order] = parameter - Knot(span + 1 - order, controlPointCount);
            right[order] = Knot(span + order, controlPointCount) - parameter;
            var saved = 0d;
            for (var slot = 0; slot < order; slot++)
            {
                var denominator = right[slot + 1] + left[order - slot];
                if (!double.IsFinite(denominator) || denominator <= 0d)
                {
                    throw new InvalidDataException("B-spline knot interval is malformed.");
                }

                var term = values[slot] / denominator;
                values[slot] = saved + right[slot + 1] * term;
                saved = left[order - slot] * term;
            }

            values[order] = saved;
        }

        return new Basis(
            span - Degree,
            (float)values[0],
            (float)values[1],
            (float)values[2],
            (float)values[3],
            Degree + 1);
    }

    private static double Knot(int index, int controlPointCount)
    {
        if (index <= Degree)
        {
            return 0d;
        }

        var end = controlPointCount - Degree;
        return index >= controlPointCount ? end : index - Degree;
    }

    internal readonly record struct Basis(
        int FirstControlPoint,
        float Weight0,
        float Weight1,
        float Weight2,
        float Weight3,
        int Count)
    {
        internal float this[int index] => index switch
        {
            0 => Weight0,
            1 => Weight1,
            2 => Weight2,
            3 => Weight3,
            _ => throw new ArgumentOutOfRangeException(nameof(index))
        };
    }
}

