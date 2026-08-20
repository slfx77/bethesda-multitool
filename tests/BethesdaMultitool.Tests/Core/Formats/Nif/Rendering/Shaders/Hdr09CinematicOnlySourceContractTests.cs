using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Shaders;

/// <summary>
///     Ungated structural guard for the standalone classic cinematic branch. Shader compiler tests
///     remain the syntax/type authority; this protects dispatch order and input semantics on machines
///     where the opt-in compiler gate is unavailable.
/// </summary>
public sealed class Hdr09CinematicOnlySourceContractTests
{
    private static string ReadTonemapShader()
    {
        return File.ReadAllText(Path.Combine(
            SourceContract.RepoRoot,
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Gpu",
            "Shaders", "Post", "tonemap.frag.hlsl"));
    }

    [Fact]
    public void StandaloneCinematicBranch_PrecedesModernAndUsesOnlyClampedSceneGrade()
    {
        var shader = ReadTonemapShader();

        const string standaloneCondition = "if (uParams0.z >= 3.5 && uParams0.z < 4.5)";
        const string standaloneReturn =
            "return float4(saturate(ApplyClassicCinematic(saturate(hdr.rgb))), hdr.a);";
        const string modernCondition = "if (uParams0.z >= 2.5)";
        var standaloneStart = shader.IndexOf(standaloneCondition, StringComparison.Ordinal);
        var standaloneReturnIndex = shader.IndexOf(standaloneReturn, standaloneStart, StringComparison.Ordinal);
        var modernStart = shader.IndexOf(modernCondition, StringComparison.Ordinal);

        Assert.True(standaloneStart >= 0, "CinematicFo3Fnv dispatch is missing.");
        Assert.True(standaloneReturnIndex > standaloneStart,
            "CinematicFo3Fnv must grade saturate(scene) and clamp its result.");
        Assert.True(modernStart > standaloneReturnIndex,
            "Mode 4 must dispatch before the >=2.5 CreationModern range.");
        var standaloneBranch = shader[standaloneStart..modernStart];
        Assert.DoesNotContain("uAvgLum.Sample", standaloneBranch, StringComparison.Ordinal);
        Assert.DoesNotContain("uBloom.Sample", standaloneBranch, StringComparison.Ordinal);
        Assert.DoesNotContain("uParams0.x", standaloneBranch, StringComparison.Ordinal);
    }

    [Fact]
    public void ClassicSdrBloomBranch_PrecedesMode4AndBloomsOverTheClampedSceneWithoutAdaptation()
    {
        var shader = ReadTonemapShader();

        // Mode 5 dispatches on >= 4.5 BEFORE the 3.5..4.5 mode-4 window so both extractions stay
        // clean; the branch samples uBloom but never uAvgLum (no eye-adapt scaling of the base
        // image), and it clamps the scene BEFORE compositing the bloom term.
        const string sdrBloomCondition = "if (uParams0.z >= 4.5)";
        const string standaloneCondition = "if (uParams0.z >= 3.5 && uParams0.z < 4.5)";
        var sdrBloomStart = shader.IndexOf(sdrBloomCondition, StringComparison.Ordinal);
        var standaloneStart = shader.IndexOf(standaloneCondition, StringComparison.Ordinal);

        Assert.True(sdrBloomStart >= 0, "ClassicSdrBloom dispatch is missing.");
        Assert.True(standaloneStart > sdrBloomStart,
            "Mode 5 must dispatch before the 3.5..4.5 mode-4 window.");

        var branch = shader[sdrBloomStart..standaloneStart];
        Assert.Contains("uBloom.Sample", branch, StringComparison.Ordinal);
        Assert.DoesNotContain("uAvgLum.Sample", branch, StringComparison.Ordinal);
        Assert.Contains("saturate(hdr.rgb)", branch, StringComparison.Ordinal);
        Assert.Contains(
            "return float4(saturate(ApplyClassicCinematic(sdr)), hdr.a);",
            branch,
            StringComparison.Ordinal);
    }
}