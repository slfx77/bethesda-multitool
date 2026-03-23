using System.Numerics;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Export;

internal static class GltfCoordinateAdapter
{
    // Bethesda NIF data is authored in a Z-up, Y-forward basis.
    // glTF uses Y-up, so rotate -90 degrees around X during export.
    private static readonly Matrix4x4 BasisTransform = Matrix4x4.CreateRotationX(-MathF.PI / 2f);
    private static readonly Matrix4x4 BasisTransformInverse = Matrix4x4.CreateRotationX(MathF.PI / 2f);

    internal static Vector3 ConvertPosition(Vector3 position)
    {
        return Vector3.Transform(position, BasisTransform);
    }

    internal static Vector3 ConvertDirection(Vector3 direction)
    {
        if (direction.LengthSquared() <= 0.0001f)
        {
            return Vector3.Zero;
        }

        var converted = Vector3.TransformNormal(direction, BasisTransform);
        return converted.LengthSquared() > 0.0001f
            ? Vector3.Normalize(converted)
            : Vector3.Zero;
    }

    internal static Matrix4x4 ConvertMatrix(Matrix4x4 transform)
    {
        var m = BasisTransformInverse * transform * BasisTransform;

        // Sanitize column 4 to guarantee affine validity for SharpGLTF.
        // Matrix inversion + multiplication can drift these from exact [0,0,0,1].
        m.M14 = 0;
        m.M24 = 0;
        m.M34 = 0;
        m.M44 = 1;

        return m;
    }
}
