using System.Numerics;
using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Assets;

namespace FalloutXbox360Utils.CLI.Rendering.Npc;

/// <summary>
///     Applies a lightweight eye-look pass for NPC sprite renders so eyeballs face the active camera.
///     Attachment correction gets the eyes into the sockets; this pass only adjusts their local gaze.
/// </summary>
internal static class NpcEyeLookAt
{
    private static readonly Logger Log = Logger.Instance;
    private const float IrisUvCenter = 0.5f;
    private const float ForwardAlignmentThreshold = 0.9995f;
    private const float VectorEpsilon = 0.000001f;
    private const int IrisSampleCount = 16;
    private const double LinearSolveEpsilon = 0.000000001d;

    internal static bool Apply(NifRenderableModel model, float azimuthDeg, float elevationDeg)
    {
        var desiredForward = GetCameraForward(azimuthDeg, elevationDeg);
        var changed = false;

        foreach (var submesh in model.Submeshes)
        {
            if (!IsEyeSubmesh(submesh) ||
                !TryEstimateLookDirection(submesh, out var eyeCenter, out var currentForward))
            {
                continue;
            }

            var dot = Math.Clamp(Vector3.Dot(currentForward, desiredForward), -1f, 1f);
            if (dot >= ForwardAlignmentThreshold)
            {
                continue;
            }

            var axis = Vector3.Cross(currentForward, desiredForward);
            if (axis.LengthSquared() <= VectorEpsilon)
            {
                axis = Vector3.Cross(
                    currentForward,
                    MathF.Abs(currentForward.Y) < 0.95f ? Vector3.UnitY : Vector3.UnitX);
            }

            if (axis.LengthSquared() <= VectorEpsilon)
            {
                continue;
            }

            axis = Vector3.Normalize(axis);
            var angle = MathF.Acos(dot);
            Log.Debug(
                "EyeLookAt[{0}]: center=({1:F3},{2:F3},{3:F3}) current=({4:F4},{5:F4},{6:F4}) desired=({7:F4},{8:F4},{9:F4}) angleDeg={10:F2}",
                submesh.ShapeName ?? submesh.DiffuseTexturePath ?? "(eye)",
                eyeCenter.X,
                eyeCenter.Y,
                eyeCenter.Z,
                currentForward.X,
                currentForward.Y,
                currentForward.Z,
                desiredForward.X,
                desiredForward.Y,
                desiredForward.Z,
                angle * (180f / MathF.PI));
            var transform =
                Matrix4x4.CreateTranslation(-eyeCenter) *
                Matrix4x4.CreateFromAxisAngle(axis, angle) *
                Matrix4x4.CreateTranslation(eyeCenter);
            NpcRenderHelpers.TransformSubmesh(submesh, transform);
            changed = true;
        }

        if (!changed)
        {
            return false;
        }

        model.MinX = float.MaxValue;
        model.MinY = float.MaxValue;
        model.MinZ = float.MaxValue;
        model.MaxX = float.MinValue;
        model.MaxY = float.MinValue;
        model.MaxZ = float.MinValue;

        foreach (var submesh in model.Submeshes)
        {
            model.ExpandBounds(submesh.Positions);
        }

        return true;
    }

    internal static bool TryEstimateLookDirection(
        RenderableSubmesh submesh,
        out Vector3 eyeCenter,
        out Vector3 lookDirection)
    {
        eyeCenter = default;
        lookDirection = default;

        var positions = submesh.Positions;
        var uvs = submesh.UVs;
        if (uvs == null || positions.Length < 9 || uvs.Length < 6)
        {
            return false;
        }

        var vertexCount = Math.Min(positions.Length / 3, uvs.Length / 2);
        if (vertexCount < 3)
        {
            return false;
        }

        if (!TryEstimateEyePivot(submesh, out eyeCenter))
        {
            return false;
        }

        var candidates = new List<(float DistSq, Vector3 Position)>(vertexCount);
        for (var i = 0; i < vertexCount; i++)
        {
            var u = uvs[i * 2];
            var v = uvs[i * 2 + 1];
            var du = u - IrisUvCenter;
            var dv = v - IrisUvCenter;
            candidates.Add((du * du + dv * dv, ReadPosition(positions, i)));
        }

        candidates.Sort(static (left, right) => left.DistSq.CompareTo(right.DistSq));

        var sampleCount = Math.Min(IrisSampleCount, candidates.Count);
        var weightedIrisPoint = Vector3.Zero;
        var totalWeight = 0f;
        for (var i = 0; i < sampleCount; i++)
        {
            var candidate = candidates[i];
            var weight = 1f / MathF.Max(candidate.DistSq, 0.0001f);
            weightedIrisPoint += candidate.Position * weight;
            totalWeight += weight;
        }

        if (totalWeight <= VectorEpsilon)
        {
            return false;
        }

        var irisPoint = weightedIrisPoint / totalWeight;
        var forward = irisPoint - eyeCenter;
        if (forward.LengthSquared() <= VectorEpsilon)
        {
            return false;
        }

        lookDirection = Vector3.Normalize(forward);
        return true;
    }

    internal static bool TryEstimateEyePivot(RenderableSubmesh submesh, out Vector3 eyeCenter)
    {
        eyeCenter = default;

        var positions = submesh.Positions;
        var vertexCount = positions.Length / 3;
        if (vertexCount < 4)
        {
            return false;
        }

        if (TryFitSphereCenter(positions, vertexCount, out eyeCenter))
        {
            return true;
        }

        var minX = float.MaxValue;
        var minY = float.MaxValue;
        var minZ = float.MaxValue;
        var maxX = float.MinValue;
        var maxY = float.MinValue;
        var maxZ = float.MinValue;
        for (var i = 0; i < vertexCount; i++)
        {
            var position = ReadPosition(positions, i);
            minX = Math.Min(minX, position.X);
            minY = Math.Min(minY, position.Y);
            minZ = Math.Min(minZ, position.Z);
            maxX = Math.Max(maxX, position.X);
            maxY = Math.Max(maxY, position.Y);
            maxZ = Math.Max(maxZ, position.Z);
        }

        eyeCenter = new Vector3(
            (minX + maxX) * 0.5f,
            (minY + maxY) * 0.5f,
            (minZ + maxZ) * 0.5f);
        return true;
    }

    internal static bool HasEyeSubmeshes(NifRenderableModel model)
    {
        return model.Submeshes.Any(IsEyeSubmesh);
    }

    private static Vector3 GetCameraForward(float azimuthDeg, float elevationDeg)
    {
        var alpha = azimuthDeg * MathF.PI / 180f;
        var theta = elevationDeg * MathF.PI / 180f;
        var ct = MathF.Cos(theta);
        return Vector3.Normalize(new Vector3(
            MathF.Cos(alpha) * ct,
            MathF.Sin(alpha) * ct,
            MathF.Sin(theta)));
    }

    private static bool IsEyeSubmesh(RenderableSubmesh submesh)
    {
        return submesh.RenderOrder == 2 ||
               submesh.IsEyeEnvmap ||
               HasExplicitEyeHint(submesh.ShapeName) ||
               HasExplicitEyeHint(submesh.DiffuseTexturePath);
    }

    private static bool HasExplicitEyeHint(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return (value.Contains("eyeleft", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("eyeright", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("\\eyes\\", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("/eyes/", StringComparison.OrdinalIgnoreCase)) &&
               !value.Contains("eyebrow", StringComparison.OrdinalIgnoreCase);
    }

    private static Vector3 ReadPosition(float[] positions, int vertexIndex)
    {
        var offset = vertexIndex * 3;
        return new Vector3(
            positions[offset],
            positions[offset + 1],
            positions[offset + 2]);
    }

    private static bool TryFitSphereCenter(float[] positions, int vertexCount, out Vector3 center)
    {
        center = default;

        Span<double> augmented = stackalloc double[20];
        for (var i = 0; i < vertexCount; i++)
        {
            var position = ReadPosition(positions, i);
            var x = (double)position.X;
            var y = (double)position.Y;
            var z = (double)position.Z;
            var rhs = x * x + y * y + z * z;
            var row0 = 2d * x;
            var row1 = 2d * y;
            var row2 = 2d * z;
            const double row3 = 1d;

            for (var r = 0; r < 4; r++)
            {
                var baseIndex = r * 5;
                var rowValue = r switch
                {
                    0 => row0,
                    1 => row1,
                    2 => row2,
                    _ => row3
                };

                for (var c = 0; c < 4; c++)
                {
                    var columnValue = c switch
                    {
                        0 => row0,
                        1 => row1,
                        2 => row2,
                        _ => row3
                    };
                    augmented[baseIndex + c] += rowValue * columnValue;
                }

                augmented[baseIndex + 4] += rowValue * rhs;
            }
        }

        if (!SolveLinear4x4(augmented))
        {
            return false;
        }

        var cx = augmented[4];
        var cy = augmented[9];
        var cz = augmented[14];
        var k = augmented[19];
        var radiusSquared = k + cx * cx + cy * cy + cz * cz;
        if (radiusSquared <= LinearSolveEpsilon ||
            double.IsNaN(radiusSquared) ||
            double.IsInfinity(radiusSquared))
        {
            return false;
        }

        center = new Vector3((float)cx, (float)cy, (float)cz);
        return float.IsFinite(center.X) &&
               float.IsFinite(center.Y) &&
               float.IsFinite(center.Z);
    }

    private static bool SolveLinear4x4(Span<double> augmented)
    {
        for (var pivot = 0; pivot < 4; pivot++)
        {
            var pivotRow = pivot;
            var pivotMagnitude = Math.Abs(augmented[pivotRow * 5 + pivot]);
            for (var row = pivot + 1; row < 4; row++)
            {
                var magnitude = Math.Abs(augmented[row * 5 + pivot]);
                if (magnitude > pivotMagnitude)
                {
                    pivotMagnitude = magnitude;
                    pivotRow = row;
                }
            }

            if (pivotMagnitude <= LinearSolveEpsilon)
            {
                return false;
            }

            if (pivotRow != pivot)
            {
                SwapRows(augmented, pivotRow, pivot);
            }

            var baseIndex = pivot * 5;
            var pivotValue = augmented[baseIndex + pivot];
            for (var column = pivot; column < 5; column++)
            {
                augmented[baseIndex + column] /= pivotValue;
            }

            for (var row = 0; row < 4; row++)
            {
                if (row == pivot)
                {
                    continue;
                }

                var rowBase = row * 5;
                var factor = augmented[rowBase + pivot];
                if (Math.Abs(factor) <= LinearSolveEpsilon)
                {
                    continue;
                }

                for (var column = pivot; column < 5; column++)
                {
                    augmented[rowBase + column] -= factor * augmented[baseIndex + column];
                }
            }
        }

        return true;
    }

    private static void SwapRows(Span<double> augmented, int leftRow, int rightRow)
    {
        for (var column = 0; column < 5; column++)
        {
            var leftIndex = leftRow * 5 + column;
            var rightIndex = rightRow * 5 + column;
            (augmented[leftIndex], augmented[rightIndex]) = (augmented[rightIndex], augmented[leftIndex]);
        }
    }
}
