using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Atmosphere;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Export;

/// <summary>
///     Projects a typed authored-atmosphere layer into a portable standalone GLB preview. Bethesda's
///     <c>SkyObjectType.Sky</c> vertex RGB stores horizon/lower/upper blend weights, not a display
///     colour, and vertex alpha is the authored coverage over the horizon row. A standalone NIF has
///     no WTHR, climate, game-hour, or weather-transition context, so this projection deliberately
///     uses the same Fallout 76 noon fallback palette as <see cref="AtmosphereState" /> rather than
///     claiming weather parity.
///     <para>
///         The three-row equation is recovered evidence for Fallout 3/New Vegas and is the current
///         World Viewer authored-sky interpretation. Applying that interpretation to Fallout 76 here
///         is an explicitly labeled semantic preview pending a game-specific capture matrix.
///     </para>
///     <para>
///         The source <see cref="RenderableSubmesh.VertexColors" /> array is read only. World Viewer
///         therefore retains the raw weights for its live weather-driven shader while Mesh Viewer and
///         exported GLB receive an opaque, immediately meaningful fallback preview.
///     </para>
/// </summary>
internal static class AuthoredSkyGlbPreviewProjection
{
    internal const string NameSuffix =
        " [authored sky standalone fallback: clear-noon palette; weather/time unavailable]";

    private static readonly AtmosphereState.Resolved PreviewAtmosphere =
        AtmosphereState.Resolve(12f, game: BethesdaGame.Fallout76);

    internal static bool AppliesTo(RenderableSubmesh submesh)
    {
        ArgumentNullException.ThrowIfNull(submesh);

        return submesh.SkyType == SkyObjectType.Sky &&
               submesh.VertexCount > 0 &&
               submesh.VertexColors is { } colors &&
               colors.Length == (long)submesh.VertexCount * 4;
    }

    internal static bool TryBuildVertexColor(
        RenderableSubmesh submesh,
        int vertexIndex,
        out Vector4 projected)
    {
        ArgumentNullException.ThrowIfNull(submesh);
        projected = Vector4.One;

        if (!AppliesTo(submesh) || vertexIndex < 0 || vertexIndex >= submesh.VertexCount)
        {
            return false;
        }

        var offset = vertexIndex * 4;
        var colors = submesh.VertexColors!;
        var weights = new Vector3(
            colors[offset] / 255f,
            colors[offset + 1] / 255f,
            colors[offset + 2] / 255f);
        var coverage = colors[offset + 3] / 255f;

        var weighted = SkyBlendWeights.Evaluate(
            weights,
            PreviewAtmosphere.AuthoredHorizonColor,
            PreviewAtmosphere.SkyLowerColor,
            PreviewAtmosphere.SkyTopColor);
        var composite = SkyBlendWeights.CompositeAtmosphere(
            weighted,
            PreviewAtmosphere.AuthoredHorizonColor,
            coverage);

        projected = new Vector4(Vector3.Clamp(composite, Vector3.Zero, Vector3.One), 1f);
        return true;
    }
}
