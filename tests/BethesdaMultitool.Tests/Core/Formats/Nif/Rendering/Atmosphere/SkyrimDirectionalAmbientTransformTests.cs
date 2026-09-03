using System.Numerics;
using System.Runtime.InteropServices;
using BethesdaMultitool;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Atmosphere;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Atmosphere;

public sealed class SkyrimDirectionalAmbientTransformTests
{
    [Fact]
    public void Create_UsesRetailHalfDifferencesAndSixFaceMean()
    {
        var cube = new AtmosphereState.ResolvedAmbientCube(
            new Vector3(2f, 4f, 6f),
            new Vector3(6f, 10f, 14f),
            new Vector3(1f, 3f, 5f),
            new Vector3(5f, 9f, 13f),
            new Vector3(-2f, 0f, 2f),
            new Vector3(2f, 6f, 10f));

        var packed = SkyrimDirectionalAmbientTransform.Create(cube);

        AssertVector(new Vector4(2f, 2f, 2f, 14f / 6f), packed.Row0);
        AssertVector(new Vector4(3f, 3f, 3f, 32f / 6f), packed.Row1);
        AssertVector(new Vector4(4f, 4f, 4f, 50f / 6f), packed.Row2);
    }

    [Fact]
    public void DragonsreachFixture_UpNormalUsesRetailAffineResultInsteadOfDarkPositiveZFace()
    {
        static Vector3 Rgb(float r, float g, float b) => new(r / 255f, g / 255f, b / 255f);
        static Vector4 Rgba(float r, float g, float b, float a) =>
            new(r / 255f, g / 255f, b / 255f, a / 255f);

        // CELL 0x000165A3 after its LTMP inheritance is applied. Order is the authored X+/X-,
        // Y+/Y-, Z+/Z- order passed by retail Sky::UpdateColors to AE ID 105643.
        var cube = new AtmosphereState.ResolvedAmbientCube(
            Rgb(100f, 80f, 67f),
            Rgb(81f, 80f, 61f),
            Rgb(91f, 83f, 64f),
            Rgb(91f, 76f, 64f),
            Rgb(43f, 38f, 29f),
            Rgb(138f, 121f, 99f));

        var packed = SkyrimDirectionalAmbientTransform.Create(cube);

        AssertVector(Rgba(-9.5f, 0f, 47.5f, 544f / 6f), packed.Row0);
        AssertVector(Rgba(0f, -3.5f, 41.5f, 478f / 6f), packed.Row1);
        AssertVector(Rgba(-3f, 0f, 35f, 64f), packed.Row2);

        AssertVector(Rgb(829f / 6f, 727f / 6f, 99f), packed.EvaluateUnitNormal(Vector3.UnitZ));
        AssertVector(Rgb(259f / 6f, 229f / 6f, 29f), packed.EvaluateUnitNormal(-Vector3.UnitZ));
        Assert.True(
            packed.EvaluateUnitNormal(Vector3.UnitZ).X > cube.PositiveZ.X * 3f,
            "The old raw-face path selected dark Z+ for an up normal; retail's matrix evaluates toward Z-.");
    }

    [Fact]
    public void TryCreate_SelectsOnlySkyrimAndRequiresAnUploadedCube()
    {
        var cube = new AtmosphereState.ResolvedAmbientCube(
            Vector3.One, Vector3.One, Vector3.One, Vector3.One, Vector3.One, Vector3.One);

        Assert.True(SkyrimDirectionalAmbientTransform.TryCreate(BethesdaGame.Skyrim, cube, out _));
        Assert.False(SkyrimDirectionalAmbientTransform.TryCreate(BethesdaGame.Fallout76, cube, out _));
        Assert.False(SkyrimDirectionalAmbientTransform.TryCreate(BethesdaGame.Fallout4, cube, out _));
        Assert.False(SkyrimDirectionalAmbientTransform.TryCreate(BethesdaGame.Skyrim, null, out _));
    }

    [Fact]
    public void AtmosphereConstantBuffer_AppendsRetailRowsAndModeAtPinnedOffsets()
    {
        Assert.Equal(37, AtmosphereConstantBufferLayout.ClipPlaneFloat4Slot);
        Assert.Equal(38, AtmosphereConstantBufferLayout.SkyrimDirectionalAmbientRow0Float4Slot);
        Assert.Equal(39, AtmosphereConstantBufferLayout.SkyrimDirectionalAmbientRow1Float4Slot);
        Assert.Equal(40, AtmosphereConstantBufferLayout.SkyrimDirectionalAmbientRow2Float4Slot);
        Assert.Equal(41, AtmosphereConstantBufferLayout.DirectionalAmbientModeFloat4Slot);
        Assert.Equal(42, AtmosphereConstantBufferLayout.Float4Count);
        Assert.Equal(672u, AtmosphereConstantBufferLayout.ByteSize);

        Assert.Equal(48, Marshal.SizeOf<SkyrimDirectionalAmbientTransform>());
        Assert.Equal(0, Marshal.OffsetOf<SkyrimDirectionalAmbientTransform>(
            nameof(SkyrimDirectionalAmbientTransform.Row0)).ToInt32());
        Assert.Equal(16, Marshal.OffsetOf<SkyrimDirectionalAmbientTransform>(
            nameof(SkyrimDirectionalAmbientTransform.Row1)).ToInt32());
        Assert.Equal(32, Marshal.OffsetOf<SkyrimDirectionalAmbientTransform>(
            nameof(SkyrimDirectionalAmbientTransform.Row2)).ToInt32());

        // The tests-only target deliberately omits the WinUI WorldView3DControl type, so pin its
        // field order and shared byte-size contract from source while the pure core layout above
        // remains executable on every test target.
        var worldView = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "App", "Controls", "WorldView3D",
            "WorldView3DControl.Frame.cs");
        SourceContract.AssertOrder(
            worldView,
            "public Vector4 ClipPlane;",
            "public Vector4 SkyrimDirectionalAmbientRow0;",
            "public Vector4 SkyrimDirectionalAmbientRow1;",
            "public Vector4 SkyrimDirectionalAmbientRow2;",
            "public Vector4 DirectionalAmbientMode;");
        Assert.Contains(
            "public const uint ByteSize = AtmosphereConstantBufferLayout.ByteSize;",
            worldView,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ShaderAndAllManualCpuWritersShareTheAppendedAbi()
    {
        var include = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Gpu", "Shaders",
            "Include", "atmosphere.hlsli");
        SourceContract.AssertOrder(
            include,
            "float4 uClipPlane;",
            "float4 uSkyrimDirectionalAmbientRow0;",
            "float4 uSkyrimDirectionalAmbientRow1;",
            "float4 uSkyrimDirectionalAmbientRow2;",
            "float4 uDirectionalAmbientMode;");

        foreach (var shaderName in new[] { "Reference/reference.frag.hlsl", "Terrain/terrain_textured.frag.hlsl" })
        {
            var parts = shaderName.Split('/');
            var shader = SourceContract.ReadSource(
                "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Gpu", "Shaders",
                parts[0], parts[1]);
            SourceContract.AssertOrder(
                shader,
                "if (uDirectionalAmbientMode.x > 0.5)",
                "dot(uSkyrimDirectionalAmbientRow0, ambientNormal)",
                "else if (uAmbientPositiveX.w > 0.5)",
                "float3 normalSquared = unitNormal * unitNormal;");
        }

        var manualWriters = new[]
        {
            SourceContract.ReadSource(
                "src", "BethesdaMultitool", "App", "Controls", "BethesdaSceneViewer",
                "BethesdaSceneViewerControl.Lifecycle.cs"),
            SourceContract.ReadSource(
                "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12", "Viewer",
                "BethesdaViewerRenderSession12.cs"),
            SourceContract.ReadSource("src", "BethesdaRendererProfiler", "NifHeadlessRenderer.cs")
        };

        Assert.All(manualWriters, source =>
        {
            Assert.Contains("AtmosphereConstantBufferLayout.ClipPlaneFloat4Slot", source, StringComparison.Ordinal);
            Assert.Contains("AtmosphereConstantBufferLayout.ByteSize", source, StringComparison.Ordinal);
        });
    }

    private static void AssertVector(Vector4 expected, Vector4 actual)
    {
        AssertScalar(expected.X, actual.X);
        AssertScalar(expected.Y, actual.Y);
        AssertScalar(expected.Z, actual.Z);
        AssertScalar(expected.W, actual.W);
    }

    private static void AssertVector(Vector3 expected, Vector3 actual)
    {
        AssertScalar(expected.X, actual.X);
        AssertScalar(expected.Y, actual.Y);
        AssertScalar(expected.Z, actual.Z);
    }

    private static void AssertScalar(float expected, float actual) =>
        Assert.InRange(MathF.Abs(expected - actual), 0f, 0.000002f);
}
