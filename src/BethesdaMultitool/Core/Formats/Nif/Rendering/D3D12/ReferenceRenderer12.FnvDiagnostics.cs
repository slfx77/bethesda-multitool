#if WINDOWS_GUI
using System.Numerics;
using System.Runtime.CompilerServices;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Lighting;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Profiling;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Scene;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Textures;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12;

internal sealed partial class ReferenceRenderer12
{
    private static readonly bool FnvAdtAlphaTestEnabled =
        EnvironmentVariables.Get(EnvironmentVariables.Viewer.FnvAdtAlphaTest) != "0";

    // Weak keys preserve normal cache eviction even in long diagnostic sessions. A registration
    // is replaced whenever the selected reference is admitted again, before its log-once gate.
    private readonly ConditionalWeakTable<CachedSubmesh12, FnvAdTargetRegistration> _fnvAdTargets = new();

    private bool FnvAdTargetDiagnosticsEnabled =>
        ReferenceOverrideTraceFormId != 0 &&
        _renderCache?.Game == BethesdaGame.FalloutNewVegas &&
        !OpaqueIndirectRequested && !StaticOpaquePacketRequested &&
        RendererProfilerTrace.IsEnabled;

    private void RegisterFnvAdTarget(
        in RenderableReference reference, string shapeName, ShapeTextureOverride textureOverride,
        string? diffuseKey, string? normalKey, CachedSubmesh12[] matches)
    {
        if (!FnvAdTargetDiagnosticsEnabled)
        {
            return;
        }

        // CachedSubmesh retains a source block, not a shape name. DDS correlation is therefore
        // accepted only when unique; the retail fixture/validator additionally pins that block.
        if (matches.Length != 1 || (diffuseKey is null && normalKey is null))
        {
            foreach (var ambiguous in matches) _fnvAdTargets.Remove(ambiguous);
            return;
        }

        var submesh = matches[0];
        _fnvAdTargets.Remove(submesh);
        if (submesh.SourceBlockIndex < 0) return;
        _fnvAdTargets.Add(submesh, new FnvAdTargetRegistration(
            reference.FormId, reference.BaseFormId, reference.MeshId, reference.ModelPath,
            reference.AlternateTextures?.VariantKey, shapeName, textureOverride.TextureSetFormId,
            textureOverride.Index, diffuseKey, normalKey, reference.WorldMatrix));
    }

    private unsafe void ObserveFnvAdTargetInstancedDraw(
        CachedSubmesh12 submesh, Vector4 textureState, Vector4 alphaState,
        nint drawInstanceCpuBase, uint drawStartInstance, int drawCount)
    {
        if (!FnvAdTargetDiagnosticsEnabled || drawInstanceCpuBase == 0 || drawCount <= 0 ||
            !_fnvAdTargets.TryGetValue(submesh, out var target))
        {
            return;
        }

        var length = (long)drawStartInstance + drawCount;
        if (length > int.MaxValue) return;
        var instances = new ReadOnlySpan<Matrix4x4>((void*)drawInstanceCpuBase, (int)length);
        var relativeWorld = target.WorldMatrix;
        relativeWorld.Translation -= _frameRenderOrigin;
        var matchIndex = SubmittedInstanceMatrixMatcher.FindExactMatch(
            instances, (int)drawStartInstance, drawCount, relativeWorld);
        if (matchIndex < 0) return;

        var submission = new FnvAdDrawSubmission(
            "direct-instanced", "exact-submitted-relative-world", drawStartInstance, drawCount,
            matchIndex, submesh.IndexCount, instances[(int)drawStartInstance + matchIndex]);
        EmitFnvAdTargetDraw(target, submesh, textureState, alphaState, in submission);
    }

    private void ObserveFnvAdTargetIndividualDraw(
        in BlendedReferenceDraw draw, Vector4 textureState, Vector4 alphaState, int indexCount)
    {
        if (!FnvAdTargetDiagnosticsEnabled ||
            !_fnvAdTargets.TryGetValue(draw.Submesh, out var target) ||
            draw.PhysicsLiteSeed != target.ReferenceFormId || draw.SourceWorld != target.WorldMatrix)
        {
            return;
        }

        var submission = new FnvAdDrawSubmission(
            "direct-individual", "exact-absolute-source-world", 0, 1, 0, indexCount, draw.SourceWorld);
        EmitFnvAdTargetDraw(target, draw.Submesh, textureState, alphaState, in submission);
    }

    private void EmitFnvAdTargetDraw(
        FnvAdTargetRegistration target, CachedSubmesh12 submesh, Vector4 textureState, Vector4 alphaState,
        in FnvAdDrawSubmission submission)
    {
        var flags = (uint)MathF.Round(textureState.Z);
        var eligibility = ResolveFnvActiveAdtBaseEligibility(submesh);
        RendererProfilerTrace.Event("fnv-ad-target-draw", new Dictionary<string, object?>
        {
            ["schemaVersion"] = 1,
            ["referenceFormIdHex"] = $"0x{target.ReferenceFormId:X8}",
            ["baseFormIdHex"] = $"0x{target.BaseFormId:X8}",
            ["meshIdHex"] = $"0x{target.MeshId:X8}",
            ["modelPath"] = target.ModelPath,
            ["meshVariantKey"] = target.MeshVariantKey,
            ["shapeName"] = target.ShapeName,
            ["textureSetFormIdHex"] = $"0x{target.TextureSetFormId:X8}",
            ["modsIndex"] = target.ModsIndex,
            ["sourceBlockIndex"] = submesh.SourceBlockIndex,
            ["materializationSourceIndex"] = submesh.MaterializationSourceIndex,
            ["matchingGpuSubmeshes"] = 1,
            ["shapeIdentityBasis"] = "unique-mods-texture-match-and-source-block",
            ["expectedDiffuse"] = target.ExpectedDiffuse,
            ["expectedNormal"] = target.ExpectedNormal,
            ["diffuseKey"] = submesh.Diffuse.CacheKey,
            ["normalKey"] = submesh.Normal.CacheKey,
            ["diffuseResident"] = submesh.Diffuse.IsResident,
            ["normalResident"] = submesh.Normal.IsResident,
            ["alphaBlend"] = submesh.AlphaBlend,
            ["alphaTest"] = submesh.AlphaTest,
            ["alphaTestThreshold"] = submesh.AlphaTestThreshold,
            ["alphaTestFunction"] = (int)submesh.AlphaTestFunction,
            ["materialAlpha"] = submesh.MaterialAlpha,
            ["hasMaterialAlphaController"] = submesh.MaterialAlphaController is not null,
            ["submittedAlphaState"] = new[] { alphaState.X, alphaState.Y, alphaState.Z, alphaState.W },
            ["runtimeTextureFlags"] = flags,
            ["activeAdt"] = (flags & FnvActiveAdtBasePolicy.RuntimeActiveAdtFlag) != 0,
            ["activeAdtVertexColor"] = (flags & FnvActiveAdtBasePolicy.RuntimeActiveAdtVertexColorFlag) != 0,
            ["allowAlphaTested"] = FnvAdtAlphaTestEnabled,
            ["eligible"] = FnvActiveAdtBasePolicy.IsEligible(eligibility, FnvAdtAlphaTestEnabled),
            ["classifierMode"] = submesh.ClassicBasicShaderMode.ToString(),
            ["lightingEnabled"] = _fnvActiveAdtLightingEnabled,
            ["placedLightCount"] = _placedLightCount,
            ["projectedSunShadowActive"] = _fnvProjectedSunShadowActive,
            ["fogEnabled"] = _fnvActiveAdtFogEnabled,
            ["finiteAdtFogSupported"] = _fnvActiveAdtFogSupported,
            ["activeAdtVertexFog"] = _fnvActiveAdtFogEnabled &&
                (flags & FnvActiveAdtBasePolicy.RuntimeActiveAdtFlag) != 0,
            ["submissionRoute"] = submission.Route,
            ["drawSubmitted"] = true,
            ["targetInstanceMatched"] = true,
            ["instanceMatchIndex"] = submission.MatchIndex,
            ["submittedInstanceIndex"] = (long)submission.StartInstance + submission.MatchIndex,
            ["drawStartInstance"] = submission.StartInstance,
            ["drawInstanceCount"] = submission.InstanceCount,
            ["indexCount"] = submission.IndexCount,
            ["instanceMatchKind"] = submission.MatchKind,
            ["referenceWorldMatrix"] = FnvAdMatrix(target.WorldMatrix),
            ["submittedWorldMatrix"] = FnvAdMatrix(submission.WorldMatrix),
            ["renderOrigin"] = new[] { _frameRenderOrigin.X, _frameRenderOrigin.Y, _frameRenderOrigin.Z },
            ["opaqueIndirectRequested"] = OpaqueIndirectRequested,
            ["staticPacketRequested"] = StaticOpaquePacketRequested,
        });
    }

    private static float[] FnvAdMatrix(Matrix4x4 matrix) =>
    [
        matrix.M11, matrix.M12, matrix.M13, matrix.M14,
        matrix.M21, matrix.M22, matrix.M23, matrix.M24,
        matrix.M31, matrix.M32, matrix.M33, matrix.M34,
        matrix.M41, matrix.M42, matrix.M43, matrix.M44,
    ];

    private sealed record FnvAdTargetRegistration(
        uint ReferenceFormId, uint BaseFormId, uint MeshId, string ModelPath,
        string? MeshVariantKey, string ShapeName, uint TextureSetFormId, int ModsIndex,
        string? ExpectedDiffuse, string? ExpectedNormal, Matrix4x4 WorldMatrix);

    private readonly record struct FnvAdDrawSubmission(
        string Route, string MatchKind, uint StartInstance, int InstanceCount,
        int MatchIndex, int IndexCount, Matrix4x4 WorldMatrix);
}
#endif
