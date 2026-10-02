using System.Numerics;
using Slfx77.Multitool.Core.Models;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Modeling.RealAsset;

/// <summary>
///     Samples one reader track through Shared's public <see cref="ScenePoseEvaluator" /> so its value can be read back
///     exactly (hop A8-sample and the rotation-path hops). The track is evaluated in a document that holds the read's
///     nodes and a single clip with that track alone and the parent clip's clock: then every other component of the node
///     is its rest value, so the published local matrix (scale times rotation times translation) gives the translation
///     exactly, the scale to within one rounding of the product (divided by the rest rotation's largest entry of the row)
///     and the rotation's matrix exactly up to the rest scale's division.
/// </summary>
/// <remarks>
///     Shared's evaluator takes a nonnegative absolute time and maps it through the clip clock and then the track clock
///     (<see cref="SceneAnimationClock.Map" />). A desired track-local time is therefore inverted to an absolute time
///     (<see cref="Absolute" />; a loop or reverse cycle is shifted by whole periods to stay nonnegative) and the time the
///     evaluator actually samples is recomputed with the same public maps (<see cref="Mapped" />); the oracle evaluates at
///     that mapped time, so a clock never enters the curve comparison.
/// </remarks>
internal static class NifAnimationPoseSampler
{
    /// <summary>A document with the read's nodes and one clip holding one transform track under the parent clip's clock.</summary>
    public static ModelDocument Isolated(Cut1bAnimationRead read, SceneAnimation parent, SceneTransformTrack track)
    {
        var clip = new SceneAnimation("a8", [], null, [track], clock: parent.Clock);
        return Cut1bAnimationStage.Assemble(read, [clip]);
    }

    /// <summary>A document with the read's nodes and one clip holding one Euler track under the parent clip's clock.</summary>
    public static ModelDocument Isolated(Cut1bAnimationRead read, SceneAnimation parent, SceneEulerRotationTrack track)
    {
        var clip = new SceneAnimation("a8", [], null, clock: parent.Clock, eulerRotationTracks: [track]);
        return Cut1bAnimationStage.Assemble(read, [clip]);
    }

    /// <summary>
    ///     The track-local times to sample a key curve at: every key time and the quarter points of every segment (the
    ///     quarter points are where an exchanged Hermite tangent pair shows even when the midpoint cannot).
    /// </summary>
    public static List<float> KeyTimes(IReadOnlyList<float> times)
    {
        var result = new List<float>(times.Count * 4);
        for (var key = 0; key < times.Count; key++)
        {
            result.Add(times[key]);
            if (key + 1 < times.Count)
            {
                var start = (double)times[key];
                var length = times[key + 1] - start;
                result.Add((float)(start + 0.25 * length));
                result.Add((float)(start + 0.5 * length));
                result.Add((float)(start + 0.75 * length));
            }
        }

        return result;
    }

    /// <summary>The track-local times to sample a B-spline at: every knot of its uniform parameterization and the quarter points between.</summary>
    public static List<float> KnotTimes(SceneBSplineCurve spline)
    {
        var spans = spline.ControlPointCount - spline.Degree;
        var times = new List<float>(spans * 4 + 1);
        var start = (double)spline.StartSeconds;
        var length = (double)spline.StopSeconds - start;
        for (var step = 0; step <= spans * 4; step++)
        {
            times.Add((float)(start + length * step / (spans * 4.0)));
        }

        return times;
    }

    /// <summary>An absolute (nonnegative) evaluator time whose mapped track time is, or is near, <paramref name="local" />.</summary>
    public static float Absolute(float local, SceneAnimationClock? clipClock, SceneAnimationClock? trackClock)
    {
        var clipTime = Invert(local, trackClock);
        var absolute = Invert(clipTime, clipClock);
        return !double.IsFinite(absolute) || absolute < 0 ? 0f : (float)absolute;
    }

    /// <summary>The track-local time Shared's evaluator samples for an absolute time (the clip clock, then the track clock).</summary>
    public static float Mapped(float absolute, SceneAnimationClock? clipClock, SceneAnimationClock? trackClock)
    {
        var clipSeconds = clipClock?.Map(absolute) ?? absolute;
        return trackClock?.Map(clipSeconds) ?? clipSeconds;
    }

    /// <summary>Evaluates clip 0 of a document at each absolute time and returns the node's local matrix per time.</summary>
    public static List<Matrix4x4> Sample(ModelDocument document, int node, IReadOnlyList<float> absoluteTimes)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var evaluator = new ScenePoseEvaluator(document, cancellationToken);
        var pose = evaluator.CreateWorkspace(0, cancellationToken);
        var matrices = new List<Matrix4x4>(absoluteTimes.Count);
        foreach (var time in absoluteTimes)
        {
            evaluator.EvaluateClip(pose, 0, time, cancellationToken);
            matrices.Add(pose.LocalMatrices.Span[node]);
        }

        return matrices;
    }

    /// <summary>The translation of a local matrix (its fourth row), exact.</summary>
    public static double[] Translation(Matrix4x4 matrix)
    {
        return [matrix.M41, matrix.M42, matrix.M43];
    }

    /// <summary>
    ///     The scale of a local matrix whose rotation is the rest rotation: each row divided by the rest rotation's largest
    ///     entry of that row (in double), within one Float32 rounding of the product.
    /// </summary>
    public static double[] Scale(Matrix4x4 matrix, SceneTrs rest)
    {
        var rotation = Matrix4x4.CreateFromQuaternion(rest.Rotation);
        var result = new double[3];
        for (var row = 0; row < 3; row++)
        {
            var column = 0;
            for (var candidate = 1; candidate < 3; candidate++)
            {
                if (MathF.Abs(Element(rotation, row, candidate)) > MathF.Abs(Element(rotation, row, column)))
                {
                    column = candidate;
                }
            }

            result[row] = Element(matrix, row, column) / (double)Element(rotation, row, column);
        }

        return result;
    }

    /// <summary>The rotation rows of a local matrix whose scale is the rest scale (each row divided by it), or null for a zero scale.</summary>
    public static double[][]? RotationRows(Matrix4x4 matrix, SceneTrs rest)
    {
        var scale = new[] { rest.Scale.X, rest.Scale.Y, rest.Scale.Z };
        if (scale.Any(static s => s == 0f || !float.IsFinite(s)))
        {
            return null;
        }

        var rows = new double[3][];
        for (var row = 0; row < 3; row++)
        {
            rows[row] = new double[3];
            for (var column = 0; column < 3; column++)
            {
                rows[row][column] = Element(matrix, row, column) / (double)scale[row];
            }
        }

        return rows;
    }

    /// <summary>System.Numerics' CreateFromQuaternion upper 3x3 of an X, Y, Z, W quaternion, in double.</summary>
    public static double[][] RowsOf(double x, double y, double z, double w)
    {
        double xx = x * x, yy = y * y, zz = z * z, xy = x * y, wz = w * z, xz = x * z, wy = w * y, yz = y * z,
            wx = w * x;
        return
        [
            [1 - 2 * (yy + zz), 2 * (xy + wz), 2 * (xz - wy)],
            [2 * (xy - wz), 1 - 2 * (zz + xx), 2 * (yz + wx)],
            [2 * (xz + wy), 2 * (yz - wx), 1 - 2 * (yy + xx)]
        ];
    }

    /// <summary>The angle between two rotations given as 3x3 rows, in degrees (atan2 of the skew and trace parts).</summary>
    public static double AngleDegrees(double[][] first, double[][] second)
    {
        var relative = new double[3, 3];
        for (var i = 0; i < 3; i++)
        {
            for (var j = 0; j < 3; j++)
            {
                double sum = 0;
                for (var k = 0; k < 3; k++)
                {
                    sum += first[k][i] * second[k][j];
                }

                relative[i, j] = sum;
            }
        }

        var sx = (relative[2, 1] - relative[1, 2]) * 0.5;
        var sy = (relative[0, 2] - relative[2, 0]) * 0.5;
        var sz = (relative[1, 0] - relative[0, 1]) * 0.5;
        var sine = Math.Sqrt(sx * sx + sy * sy + sz * sz);
        var cosine = (relative[0, 0] + relative[1, 1] + relative[2, 2] - 1) * 0.5;
        return Math.Atan2(sine, cosine) * 180.0 / Math.PI;
    }

    /// <summary>One upper 3x3 element of a matrix.</summary>
    public static float Element(Matrix4x4 matrix, int row, int column)
    {
        return (row, column) switch
        {
            (0, 0) => matrix.M11,
            (0, 1) => matrix.M12,
            (0, 2) => matrix.M13,
            (1, 0) => matrix.M21,
            (1, 1) => matrix.M22,
            (1, 2) => matrix.M23,
            (2, 0) => matrix.M31,
            (2, 1) => matrix.M32,
            (2, 2) => matrix.M33,
            _ => throw new ArgumentOutOfRangeException(nameof(row), $"({row}, {column}) is outside the upper 3x3.")
        };
    }

    /// <summary>One clock inverted: (local - phase) / frequency, shifted by whole periods for a loop or reverse cycle.</summary>
    private static double Invert(double local, SceneAnimationClock? clock)
    {
        if (clock is null)
        {
            return local;
        }

        if (clock.Frequency == 0f || clock.StartSeconds.Equals(clock.StopSeconds))
        {
            return 0;
        }

        var raw = (local - clock.PhaseSeconds) / clock.Frequency;
        if (raw < 0 && clock.Cycle != SceneAnimationCycle.Clamp)
        {
            var length = (double)clock.StopSeconds - clock.StartSeconds;
            var period = (clock.Cycle == SceneAnimationCycle.Loop ? length : 2 * length) / Math.Abs(clock.Frequency);
            raw += Math.Ceiling(-raw / period) * period;
        }

        return raw;
    }
}
