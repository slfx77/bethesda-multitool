using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Materials;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Export;

/// <summary>
///     Projections of CE2 vertex-driven Lerp into core glTF and the embedded Mesh Viewer.
///     <para>
///         General <c>mix(texture, interpolatedRgb, interpolatedAlpha)</c> is affine and cannot be
///         represented by glTF's multiplicative lane. Three bounded cases are exact: zero weight is
///         a no-op; weight one is vertex RGB with the albedo image omitted; and one uniform RGBA
///         value degenerates to the existing constant-Lerp texture bake. A well-formed varying stream
///         retains neutral COLOR_0 as its portable fallback and carries raw CE2 RGBA in COLOR_1 for the
///         embedded viewer's exact shader hook. Malformed streams remain unsupported and fail closed.
///     </para>
/// </summary>
internal static class StarfieldGlbVertexLerpProjection
{
    internal const string ViewerMaterialExtrasKey = "bethesdaCe2VertexLerpV1";

    internal static StarfieldGlbVertexLerpProjectionResult Resolve(RenderableSubmesh submesh)
    {
        ArgumentNullException.ThrowIfNull(submesh);

        if (!submesh.StarfieldMaterialColor.IsVertexLerp)
        {
            return default;
        }

        var colors = submesh.VertexColors;
        var requiredLength = (long)submesh.VertexCount * 4;
        if (!submesh.UseVertexColors ||
            submesh.VertexCount <= 0 ||
            requiredLength > int.MaxValue ||
            colors is null ||
            colors.LongLength != requiredLength)
        {
            return new StarfieldGlbVertexLerpProjectionResult(
                StarfieldGlbVertexLerpProjectionMode.Unsupported,
                default);
        }

        var allZeroWeight = true;
        var allFullWeight = true;
        var allUniform = true;
        for (var offset = 0; offset < colors.Length; offset += 4)
        {
            allZeroWeight &= colors[offset + 3] == 0;
            allFullWeight &= colors[offset + 3] == byte.MaxValue;
            allUniform &= colors[offset] == colors[0] &&
                          colors[offset + 1] == colors[1] &&
                          colors[offset + 2] == colors[2] &&
                          colors[offset + 3] == colors[3];
        }

        if (allZeroWeight)
        {
            return new StarfieldGlbVertexLerpProjectionResult(
                StarfieldGlbVertexLerpProjectionMode.NoOp,
                default);
        }

        if (allFullWeight)
        {
            return new StarfieldGlbVertexLerpProjectionResult(
                StarfieldGlbVertexLerpProjectionMode.VertexRgb,
                default);
        }

        if (allUniform)
        {
            return new StarfieldGlbVertexLerpProjectionResult(
                StarfieldGlbVertexLerpProjectionMode.UniformTextureBake,
                new StarfieldMaterialColorRenderState(
                    StarfieldMaterialColorRenderMode.ConstantLerp,
                    new Vector4(
                        colors[0] / 255f,
                        colors[1] / 255f,
                        colors[2] / 255f,
                        colors[3] / 255f)));
        }

        return new StarfieldGlbVertexLerpProjectionResult(
            StarfieldGlbVertexLerpProjectionMode.VaryingViewerShader,
            default);
    }

    internal static bool TryBuildVertexColor(
        RenderableSubmesh submesh,
        int vertexIndex,
        StarfieldGlbVertexLerpProjectionResult projection,
        out Vector4 color)
    {
        if (!submesh.StarfieldMaterialColor.IsVertexLerp)
        {
            color = default;
            return false;
        }

        if (projection.Mode == StarfieldGlbVertexLerpProjectionMode.VertexRgb &&
            submesh.VertexColors is { } colors)
        {
            var offset = vertexIndex * 4;
            if (offset >= 0 && offset + 3 < colors.Length)
            {
                color = new Vector4(
                    colors[offset] / 255f,
                    colors[offset + 1] / 255f,
                    colors[offset + 2] / 255f,
                    1f);
                return true;
            }
        }

        // No-op and uniform-bake have already moved all colour work into the material. Exact embedded-
        // viewer Lerp and malformed streams emit neutral COLOR_0 so portable glTF cannot reinterpret
        // CE2 affine data as a multiplicative vertex colour.
        color = Vector4.One;
        return true;
    }

    /// <summary>
    ///     Provides the raw CE2 affine RGB target and weight for COLOR_1. This accessor exists only
    ///     on well-formed varying streams; portable viewers ignore it and retain neutral COLOR_0.
    /// </summary>
    internal static bool TryBuildViewerVertexLerpColor(
        RenderableSubmesh submesh,
        int vertexIndex,
        StarfieldGlbVertexLerpProjectionResult projection,
        out Vector4 color)
    {
        if (!projection.RequiresViewerShader || submesh.VertexColors is not { } colors)
        {
            color = default;
            return false;
        }

        var offset = vertexIndex * 4;
        if (offset < 0 || offset + 3 >= colors.Length)
        {
            color = default;
            return false;
        }

        color = new Vector4(
            colors[offset] / 255f,
            colors[offset + 1] / 255f,
            colors[offset + 2] / 255f,
            colors[offset + 3] / 255f);
        return true;
    }
}

internal enum StarfieldGlbVertexLerpProjectionMode : byte
{
    None = 0,
    NoOp = 1,
    UniformTextureBake = 2,
    VertexRgb = 3,
    VaryingViewerShader = 4,
    Unsupported = 5
}

internal readonly record struct StarfieldGlbVertexLerpProjectionResult(
    StarfieldGlbVertexLerpProjectionMode Mode,
    StarfieldMaterialColorRenderState ConstantLerpState)
{
    internal bool OmitDiffuseTexture => Mode == StarfieldGlbVertexLerpProjectionMode.VertexRgb;

    internal bool IsUniformTextureBake =>
        Mode == StarfieldGlbVertexLerpProjectionMode.UniformTextureBake;

    internal bool RequiresViewerShader =>
        Mode == StarfieldGlbVertexLerpProjectionMode.VaryingViewerShader;

    internal bool IsUnsupported => Mode == StarfieldGlbVertexLerpProjectionMode.Unsupported;
}
