using System.Numerics;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Skinning;

/// <summary>
///     Applies linear blend skinning and dual quaternion skinning to geometry streams.
/// </summary>
internal static class NifSkinningMath
{
    internal static float[] ApplySkinningPositions(
        float[] positions,
        (int BoneIdx, float Weight)[][] perVertexInfluences,
        Matrix4x4[] boneSkinMatrices)
    {
        var numVertices = positions.Length / 3;
        var result = new float[positions.Length];

        for (var vertexIndex = 0; vertexIndex < numVertices; vertexIndex++)
        {
            var influences = perVertexInfluences[vertexIndex];
            if (influences.Length == 0)
            {
                CopyVector3(positions, result, vertexIndex);
                continue;
            }

            var source = ReadVector3(positions, vertexIndex);
            var destination = Vector3.Zero;
            foreach (var (boneIndex, weight) in influences)
            {
                if (boneIndex < boneSkinMatrices.Length)
                {
                    destination += weight * Vector3.Transform(source, boneSkinMatrices[boneIndex]);
                }
            }

            WriteVector3(destination, result, vertexIndex);
        }

        return result;
    }

    internal static float[] ApplySkinningNormals(
        float[] normals,
        (int BoneIdx, float Weight)[][] perVertexInfluences,
        Matrix4x4[] boneSkinMatrices)
    {
        var numVertices = normals.Length / 3;
        var result = new float[normals.Length];

        for (var vertexIndex = 0; vertexIndex < numVertices; vertexIndex++)
        {
            var influences = perVertexInfluences[vertexIndex];
            if (influences.Length == 0)
            {
                CopyVector3(normals, result, vertexIndex);
                continue;
            }

            var source = ReadVector3(normals, vertexIndex);
            var destination = Vector3.Zero;
            foreach (var (boneIndex, weight) in influences)
            {
                if (boneIndex < boneSkinMatrices.Length)
                {
                    destination += weight * Vector3.TransformNormal(
                        source,
                        boneSkinMatrices[boneIndex]);
                }
            }

            var length = destination.Length();
            if (length > 0.001f)
            {
                destination /= length;
            }

            WriteVector3(destination, result, vertexIndex);
        }

        return result;
    }

    internal static float[] ApplySkinningPositionsDqs(
        float[] positions,
        (int BoneIdx, float Weight)[][] perVertexInfluences,
        Matrix4x4[] boneSkinMatrices)
    {
        var numVertices = positions.Length / 3;
        var result = new float[positions.Length];
        var boneDualQuaternions = BuildDualQuaternions(boneSkinMatrices);

        for (var vertexIndex = 0; vertexIndex < numVertices; vertexIndex++)
        {
            var influences = perVertexInfluences[vertexIndex];
            if (influences.Length == 0)
            {
                CopyVector3(positions, result, vertexIndex);
                continue;
            }

            var blended = BlendDualQuaternions(influences, boneDualQuaternions);
            WriteVector3(
                blended.TransformPoint(ReadVector3(positions, vertexIndex)),
                result,
                vertexIndex);
        }

        return result;
    }

    internal static float[] ApplySkinningNormalsDqs(
        float[] normals,
        (int BoneIdx, float Weight)[][] perVertexInfluences,
        Matrix4x4[] boneSkinMatrices)
    {
        var numVertices = normals.Length / 3;
        var result = new float[normals.Length];
        var boneDualQuaternions = BuildDualQuaternions(boneSkinMatrices);

        for (var vertexIndex = 0; vertexIndex < numVertices; vertexIndex++)
        {
            var influences = perVertexInfluences[vertexIndex];
            if (influences.Length == 0)
            {
                CopyVector3(normals, result, vertexIndex);
                continue;
            }

            var blended = BlendDualQuaternions(influences, boneDualQuaternions);
            var destination = blended.RotateVector(ReadVector3(normals, vertexIndex));
            var length = destination.Length();
            if (length > 0.001f)
            {
                destination /= length;
            }

            WriteVector3(destination, result, vertexIndex);
        }

        return result;
    }

    private static DualQuaternion[] BuildDualQuaternions(Matrix4x4[] boneSkinMatrices)
    {
        var result = new DualQuaternion[boneSkinMatrices.Length];
        for (var i = 0; i < boneSkinMatrices.Length; i++)
        {
            result[i] = DualQuaternion.FromMatrix4x4(boneSkinMatrices[i]);
        }

        return result;
    }

    private static DualQuaternion BlendDualQuaternions(
        (int BoneIdx, float Weight)[] influences,
        DualQuaternion[] boneDualQuaternions)
    {
        var realSum = Quaternion.Zero;
        var dualSum = Quaternion.Zero;
        Quaternion referenceRotation = default;
        var hasReference = false;

        foreach (var (boneIndex, weight) in influences)
        {
            if (boneIndex >= boneDualQuaternions.Length)
            {
                continue;
            }

            var dualQuaternion = boneDualQuaternions[boneIndex];
            if (!hasReference)
            {
                referenceRotation = dualQuaternion.Real;
                hasReference = true;
            }
            else if (Quaternion.Dot(referenceRotation, dualQuaternion.Real) < 0)
            {
                dualQuaternion = dualQuaternion.Negated();
            }

            realSum = new Quaternion(
                realSum.X + weight * dualQuaternion.Real.X,
                realSum.Y + weight * dualQuaternion.Real.Y,
                realSum.Z + weight * dualQuaternion.Real.Z,
                realSum.W + weight * dualQuaternion.Real.W);
            dualSum = new Quaternion(
                dualSum.X + weight * dualQuaternion.Dual.X,
                dualSum.Y + weight * dualQuaternion.Dual.Y,
                dualSum.Z + weight * dualQuaternion.Dual.Z,
                dualSum.W + weight * dualQuaternion.Dual.W);
        }

        var length = MathF.Sqrt(
            realSum.X * realSum.X +
            realSum.Y * realSum.Y +
            realSum.Z * realSum.Z +
            realSum.W * realSum.W);
        if (length < 1e-8f)
        {
            return DualQuaternion.Identity;
        }

        var inverseLength = 1f / length;
        return new DualQuaternion(
            new Quaternion(
                realSum.X * inverseLength,
                realSum.Y * inverseLength,
                realSum.Z * inverseLength,
                realSum.W * inverseLength),
            new Quaternion(
                dualSum.X * inverseLength,
                dualSum.Y * inverseLength,
                dualSum.Z * inverseLength,
                dualSum.W * inverseLength));
    }

    private static Vector3 ReadVector3(float[] values, int vertexIndex)
    {
        return new Vector3(
            values[vertexIndex * 3],
            values[vertexIndex * 3 + 1],
            values[vertexIndex * 3 + 2]);
    }

    private static void CopyVector3(float[] source, float[] destination, int vertexIndex)
    {
        destination[vertexIndex * 3] = source[vertexIndex * 3];
        destination[vertexIndex * 3 + 1] = source[vertexIndex * 3 + 1];
        destination[vertexIndex * 3 + 2] = source[vertexIndex * 3 + 2];
    }

    private static void WriteVector3(Vector3 value, float[] destination, int vertexIndex)
    {
        destination[vertexIndex * 3] = value.X;
        destination[vertexIndex * 3 + 1] = value.Y;
        destination[vertexIndex * 3 + 2] = value.Z;
    }

    private readonly struct DualQuaternion(Quaternion real, Quaternion dual)
    {
        internal static readonly DualQuaternion Identity =
            new(Quaternion.Identity, Quaternion.Zero);

        internal Quaternion Real { get; } = real;

        internal Quaternion Dual { get; } = dual;

        internal static DualQuaternion FromMatrix4x4(Matrix4x4 matrix)
            => FromMatrix4x4(matrix, out _);

        internal static DualQuaternion FromMatrix4x4(
            Matrix4x4 matrix,
            out Vector3 scale)
        {
            var scaleX = MathF.Sqrt(
                matrix.M11 * matrix.M11 +
                matrix.M21 * matrix.M21 +
                matrix.M31 * matrix.M31);
            var scaleY = MathF.Sqrt(
                matrix.M12 * matrix.M12 +
                matrix.M22 * matrix.M22 +
                matrix.M32 * matrix.M32);
            var scaleZ = MathF.Sqrt(
                matrix.M13 * matrix.M13 +
                matrix.M23 * matrix.M23 +
                matrix.M33 * matrix.M33);
            scale = new Vector3(scaleX, scaleY, scaleZ);

            var invScaleX = scaleX > 1e-8f ? 1f / scaleX : 0f;
            var invScaleY = scaleY > 1e-8f ? 1f / scaleY : 0f;
            var invScaleZ = scaleZ > 1e-8f ? 1f / scaleZ : 0f;

            var rotationMatrix = new Matrix4x4(
                matrix.M11 * invScaleX,
                matrix.M12 * invScaleY,
                matrix.M13 * invScaleZ,
                0,
                matrix.M21 * invScaleX,
                matrix.M22 * invScaleY,
                matrix.M23 * invScaleZ,
                0,
                matrix.M31 * invScaleX,
                matrix.M32 * invScaleY,
                matrix.M33 * invScaleZ,
                0,
                0,
                0,
                0,
                1);

            var rotation = Quaternion.Normalize(
                Quaternion.CreateFromRotationMatrix(rotationMatrix));
            var dual = new Quaternion(
                0.5f * (matrix.M41 * rotation.W + matrix.M42 * rotation.Z - matrix.M43 * rotation.Y),
                0.5f * (matrix.M42 * rotation.W + matrix.M43 * rotation.X - matrix.M41 * rotation.Z),
                0.5f * (matrix.M43 * rotation.W + matrix.M41 * rotation.Y - matrix.M42 * rotation.X),
                0.5f * (-matrix.M41 * rotation.X - matrix.M42 * rotation.Y - matrix.M43 * rotation.Z));

            return new DualQuaternion(rotation, dual);
        }

        internal DualQuaternion Negated()
        {
            return new DualQuaternion(
                new Quaternion(-Real.X, -Real.Y, -Real.Z, -Real.W),
                new Quaternion(-Dual.X, -Dual.Y, -Dual.Z, -Dual.W));
        }

        internal Vector3 TransformPoint(Vector3 value)
        {
            var rotated = Vector3.Transform(value, Real);
            var conjReal = Quaternion.Conjugate(Real);
            var translationQuat = Multiply(Dual, conjReal);
            return rotated + new Vector3(
                2f * translationQuat.X,
                2f * translationQuat.Y,
                2f * translationQuat.Z);
        }

        internal Vector3 RotateVector(Vector3 value)
        {
            return Vector3.Transform(value, Real);
        }

        private static Quaternion Multiply(Quaternion a, Quaternion b)
        {
            return new Quaternion(
                a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
                a.W * b.Y + a.Y * b.W + a.Z * b.X - a.X * b.Z,
                a.W * b.Z + a.Z * b.W + a.X * b.Y - a.Y * b.X,
                a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z);
        }
    }
}
