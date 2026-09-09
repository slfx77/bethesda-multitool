using System.Numerics;
using System.Security.Cryptography;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Viewer;

/// <summary>Bounded native comparison for the two source-reviewed stock human eye meshes.</summary>
internal static class BethesdaViewerOblivionEyePolicy
{
    internal const string EnvironmentVariable = "FALLOUT_VIEWER_NATIVE_OBLIVION_EYES";

    internal static bool IsRequested(string? value)
    {
        return string.Equals(value, "1", StringComparison.Ordinal);
    }

    // These exact assets have one opaque, unskinned eye with no controllers or vertex colors.
    // TES4 FaceGen preparation changes MODULATE to HILIGHT, strips the controller list, and
    // selects ordinary Lighting pass 0x187. A filename alone does not prove those properties.
    internal static bool IsReviewedSource(BethesdaGame game, ReadOnlySpan<byte> data)
    {
        if (game != BethesdaGame.Oblivion || data.Length is not (7559 or 7593)) return false;
        var digest = Convert.ToHexString(SHA256.HashData(data));
        return digest is "81C384F37E2CE861220329A3D9DE19C67951E62A441F5C67A6B9B022066025B9"
            or "E61B90AA2A8BA0C87EA6D489BBEB141728B623DBF3412687E8C7DFAFB314F02F";
    }

    internal static bool IsEnabledFor(bool requested, BethesdaGame game, BethesdaViewerScenePurpose purpose,
        bool reviewedSource, bool opaque, bool billboard)
    {
        return requested && game == BethesdaGame.Oblivion && purpose == BethesdaViewerScenePurpose.NpcAppearance &&
               reviewedSource && opaque && !billboard;
    }

    internal static float DistanceFade(Vector3 camera, Vector3 center, float radius)
    {
        var distance = Vector3.Distance(camera, center) - radius;
        if (!float.IsFinite(distance) || !float.IsFinite(radius) || radius <= 0f) return 0f;
        // Installed bUseEyeEnvMapping=1, fEyeEnvMapLOD1=130, fEyeEnvMapLOD2=190.
        return Math.Clamp((190f - distance) / 60f, 0f, 1f);
    }
}
