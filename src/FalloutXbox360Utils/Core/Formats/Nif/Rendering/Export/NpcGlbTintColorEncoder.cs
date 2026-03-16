using System.Numerics;
using FalloutXbox360Utils.Core.Formats.Dds;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Export;

internal static class NpcGlbTintColorEncoder
{
    internal static bool HasTintColor(RenderableSubmesh submesh)
    {
        ArgumentNullException.ThrowIfNull(submesh);
        return submesh.TintColor.HasValue;
    }

    internal static DecodedTexture? BakeDiffuseTexture(
        RenderableSubmesh submesh,
        DecodedTexture? diffuseTexture)
    {
        ArgumentNullException.ThrowIfNull(submesh);

        if (diffuseTexture == null || !HasTintColor(submesh))
        {
            return diffuseTexture;
        }

        var tint = EncodeTint(submesh.TintColor!.Value);
        var source = diffuseTexture.Pixels;
        var tinted = new byte[source.Length];
        for (var index = 0; index < source.Length; index += 4)
        {
            tinted[index] = MultiplyColor(source[index], tint.X);
            tinted[index + 1] = MultiplyColor(source[index + 1], tint.Y);
            tinted[index + 2] = MultiplyColor(source[index + 2], tint.Z);
            tinted[index + 3] = source[index + 3];
        }

        return DecodedTexture.FromBaseLevel(
            tinted,
            diffuseTexture.Width,
            diffuseTexture.Height,
            false);
    }

    internal static Vector4 BuildBaseColor(
        RenderableSubmesh submesh,
        bool hasDiffuseTexture)
    {
        ArgumentNullException.ThrowIfNull(submesh);

        if (HasTintColor(submesh))
        {
            if (hasDiffuseTexture)
            {
                return new Vector4(1f, 1f, 1f, Math.Clamp(submesh.MaterialAlpha, 0f, 1f));
            }

            var tint = EncodeTint(submesh.TintColor!.Value);
            return new Vector4(tint, Math.Clamp(submesh.MaterialAlpha, 0f, 1f));
        }

        var tintColor = submesh.TintColor ?? (1f, 1f, 1f);
        return new Vector4(
            tintColor.R,
            tintColor.G,
            tintColor.B,
            Math.Clamp(submesh.MaterialAlpha, 0f, 1f));
    }

    internal static Vector4 BuildVertexColor(
        RenderableSubmesh submesh,
        int vertexIndex)
    {
        ArgumentNullException.ThrowIfNull(submesh);

        if (!NifVertexColorPolicy.HasVertexColorData(submesh))
        {
            return Vector4.One;
        }

        var color = NifVertexColorPolicy.Read(submesh, vertexIndex);
        return new Vector4(
            color.R / 255f,
            color.G / 255f,
            color.B / 255f,
            color.A / 255f);
    }

    private static Vector3 EncodeTint((float R, float G, float B) tint)
    {
        return new Vector3(
            Math.Clamp(tint.R * 2f, 0f, 1f),
            Math.Clamp(tint.G * 2f, 0f, 1f),
            Math.Clamp(tint.B * 2f, 0f, 1f));
    }

    private static byte MultiplyColor(byte channel, float tint)
    {
        return (byte)Math.Clamp(MathF.Round(channel * tint), 0f, 255f);
    }
}
