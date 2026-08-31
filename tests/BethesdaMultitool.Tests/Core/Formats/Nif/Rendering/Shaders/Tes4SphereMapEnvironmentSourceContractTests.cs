using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Shaders;

/// <summary>
///     Pins the TES3/TES4-era NiTextureEffect sphere-map environment route end to end:
///     texture-state bit 14 (16384) marks the classic env texture as a 2D SPHERE map, the
///     reference fragment shader samples it through the <c>textures[]</c> alias (never the cube
///     alias) with the view-space sphere projection, the cube arm stays byte-identical, the
///     "Window reflections" toggle suppresses window-flagged env draws at constants fill, and the
///     decoded-mesh disk cache persists the marker after the rigid-anim track under DecoderVersion
///     79+.
/// </summary>
public sealed class Tes4SphereMapEnvironmentSourceContractTests
{
    [Fact]
    public void FragmentShader_DeclaresSphereMapBitAndHelper()
    {
        var source = SourceContract.ReadShaderSource("reference.frag.hlsl");

        SourceContract.AssertOrder(
            source,
            "bool HasClassicSphereMapEnvironment(float packedState)",
            "& 16384u");
    }

    [Fact]
    public void FragmentShader_ClassicEnvBlock_BranchesSphereVersusCube()
    {
        var source = SourceContract.ReadShaderSource("reference.frag.hlsl");
        var block = SourceContract.Extract(
            source,
            "// FO3/FNV classic PP-lighting environment pass",
            "// FO4 cubemap environment reflections");

        // Window sign convention is preserved for both arms.
        SourceContract.AssertOrder(
            block,
            "UsesClassicEnvironmentWindowReflection(input.vTextureState.z)",
            "? reflect(V, normal)",
            ": reflect(-V, normal)");

        // Sphere arm: view-space sphere projection sampled via the 2D bindless alias.
        SourceContract.AssertOrder(
            block,
            "if (HasClassicSphereMapEnvironment(input.vTextureState.z))",
            "(rv.z + 1.0) * (rv.z + 1.0)",
            "float2 sphereUv = float2(0.5 + rv.x / m, 0.5 - rv.y / m)",
            "textures[NonUniformResourceIndex(envSlot)].Sample(sPalette, sphereUv)");

        // Cube arm intact, after the sphere arm.
        SourceContract.AssertOrder(
            block,
            "textures[NonUniformResourceIndex(envSlot)].Sample(sPalette, sphereUv)",
            "cubemaps[NonUniformResourceIndex(envSlot)].Sample(sPalette, reflectDir)");

        // The additive combine is shared by both arms (mask × scale × material alpha).
        Assert.Contains(
            "lit += env * vertexRgb * (classicEnvMask * input.vEnvMap.y * input.vAlphaState.z);",
            block);
    }

    [Fact]
    public void CachedSubmesh_PacksSphereBitAndRoutesSphereMapsPastCubePromotion()
    {
        var source = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12",
            "CachedSubmesh12.cs");

        // Bit 14 rides TextureState only when the classic env pass is actually active.
        Assert.Contains(
            "ClassicEnvMap is not null && ClassicEnvMapScale > 0f && ClassicEnvMapIsSphereMap",
            source);
        Assert.Contains("16384f", source);

        // Sphere maps bypass the TextureCube promotion gate but stay barred from the cube alias.
        SourceContract.AssertOrder(
            source,
            "var eligible = ClassicEnvMap is not null && ClassicEnvMapIsSphereMap",
            "env is { IsResident: true, IsCubemap: false }",
            "env is { IsResident: true, IsCubemap: true }");
    }

    [Fact]
    public void Renderer_WindowReflectionsToggle_SuppressesEnvSlotAtBothConstantFillSites()
    {
        var source = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12",
            "ReferenceRenderer12.cs");

        SourceContract.AssertOrder(
            source,
            "private Vector4 ResolveEnvMapState(CachedSubmesh12 submesh)",
            "if (!_windowReflectionsEnabled && submesh.ClassicEnvMapUsesWindowReflection)",
            "state.X = -1f;");

        // Definition + the instanced and blended constant-fill call sites.
        Assert.Equal(3, SourceContract.CountOccurrences(source, "ResolveEnvMapState("));
        Assert.Contains("EnvMap: ResolveEnvMapState(sub),", source);
        Assert.Contains("EnvMap = ResolveEnvMapState(draw.Submesh),", source);
    }

    [Fact]
    public void DiskCache_PersistsSphereMarkerAfterRigidAnimUnderBumpedVersion()
    {
        var source = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Gpu", "D3D12",
            "ReferenceDecodedMeshDiskCache12.cs");

        // v79+: the marker is appended AFTER the rigid-anim track in both directions.
        SourceContract.AssertOrder(
            source,
            "internal const int DecoderVersion =",
            "WriteRigidNodeAnimation(writer, submesh.RigidNodeAnimation);",
            "writer.Write(submesh.ClassicEnvironmentMapIsSphereMap);");
        SourceContract.AssertOrder(
            source,
            "ReadRigidNodeAnimation(reader),",
            "reader.ReadBoolean(),",
            "ReadStarfieldMaterialColor(reader));");

        // Extract returns the start marker inclusively; strip it before parsing the number.
        const string versionMarker = "internal const int DecoderVersion = ";
        var versionText = SourceContract.Extract(source, versionMarker, ";")[versionMarker.Length..];
        Assert.True(
            int.Parse(versionText) >= 79,
            $"DecoderVersion must be >= 79 for the sphere-map payload field (found {versionText}).");
    }
}
