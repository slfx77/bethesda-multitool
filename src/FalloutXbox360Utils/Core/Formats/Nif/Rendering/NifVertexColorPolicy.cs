namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering;

internal static class NifVertexColorPolicy
{
    internal static bool HasVertexColorData(RenderableSubmesh submesh)
    {
        ArgumentNullException.ThrowIfNull(submesh);
        return submesh.VertexColors != null && (submesh.UseVertexColors || submesh.IsEmissive);
    }

    internal static (byte R, byte G, byte B, byte A) Read(RenderableSubmesh submesh, int vertexIndex)
    {
        ArgumentNullException.ThrowIfNull(submesh);

        if (!HasVertexColorData(submesh))
        {
            return (255, 255, 255, 255);
        }

        var offset = vertexIndex * 4;
        var colors = submesh.VertexColors!;
        var alpha = colors[offset + 3];

        // BSShaderNoLighting effect meshes commonly store per-vertex alpha fades without
        // enabling Vertex_Colors RGB modulation. Preserve the alpha ramp, but keep RGB neutral.
        if (!submesh.UseVertexColors)
        {
            return (255, 255, 255, alpha);
        }

        return (colors[offset], colors[offset + 1], colors[offset + 2], alpha);
    }
}
