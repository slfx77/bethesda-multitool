using System.Numerics;
using BethesdaMultitool.Core.Formats.Dds;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Inspection;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Rasterization;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Export;

/// <summary>Bakes a submesh's tint color (hair/skin) into its diffuse texture for export, since glTF has no separate tint channel.</summary>
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

        // Mirror the rasterizer's rule (NifScanlineRasterizer.cs:307-331): when a submesh
        // carries a tint color, the tinted texture is the sole color source and vertex
        // colors are ignored — engine shader paths bound to tint-receiving geometry
        // (NPC hair / brow / lash, skin / armour HCLR overrides) substitute tint × texture
        // for vertex-color modulation. Without this we'd multiply the auburn baked
        // diffuse (R≈0.18, G≈0.09, B≈0.02) by hair-card AO masks (R≈0.08, G/B=1.0),
        // killing the red channel and rendering hair green. The previous extra
        // `!submesh.UseVertexColors` guard was a misread: BSShaderFlags2 bit 5 is forced
        // to true upstream (NifExportExtractor.cs:353) because it's unreliable as a
        // gating signal on shipped meshes, so reading the flag here told us nothing.
        if (HasTintColor(submesh))
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

