using System.Numerics;
using System.Runtime.InteropServices;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Core.Games;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

public sealed class PlacedLightTests
{
    [Fact]
    public void TryBuild_PreservesAuthoredDataAndAppliesPlacementState()
    {
        var placement = new PlacedReference
        {
            FormId = 0x100,
            BaseFormId = 0x200,
            RecordType = "REFR",
            X = 10f,
            Y = 20f,
            Z = 30f,
            Scale = 1.5f
        };
        var light = new LightRecord
        {
            FormId = placement.BaseFormId,
            Radius = 120,
            Color = 0x0033_2211,
            Flags = PlacedLight.NegativeFlag | PlacedLight.OffByDefaultFlag | PlacedLight.SpotLightFlag,
            FalloffExponent = 2.5f,
            Fov = 60f,
            Fade = 0.4f
        };

        var result = PlacedLight.TryBuild(placement, light);

        Assert.NotNull(result);
        var built = result.Value;
        Assert.Equal(placement.FormId, built.FormId);
        Assert.Equal(light.FormId, built.BaseFormId);
        Assert.Equal(new Vector3(10f, 20f, 30f), built.Position);
        Assert.Equal(180f, built.Radius);
        Assert.Equal(0x11 / 255f, built.Color.X, 5);
        Assert.Equal(0x22 / 255f, built.Color.Y, 5);
        Assert.Equal(0x33 / 255f, built.Color.Z, 5);
        Assert.Equal(2.5f, built.FalloffExponent);
        Assert.Equal(60f, built.FieldOfView);
        Assert.Equal(-0.4f, built.Intensity);
        Assert.True(built.IsInitiallyDisabled);
        Assert.Equal(light.Flags, built.Flags);

        // FNV's recovered dynamic-light path always creates an NiPointLight. Keep the parsed
        // spotlight fields as metadata, but do not invent a cone that the executable never uses.
        Assert.True(built.HasSpotFlag);
        var gpu = new GpuPointLight(built, Vector3.Zero);
        Assert.Equal(2.5f, gpu.AuthoredMetadata.X);
        Assert.Equal(60f, gpu.AuthoredMetadata.Y);
        Assert.Equal((float)light.Flags, gpu.AuthoredMetadata.Z);
        Assert.Equal(Vector4.Zero, gpu.Reserved);
    }

    [Fact]
    public void TryBuild_XespDisabledMarksOtherwiseEnabledLightDisabled()
    {
        var placement = Placement(0x101, 0x201, modelPath: null);
        var light = new LightRecord { FormId = placement.BaseFormId, Radius = 64, Fade = 0.75f };

        var result = PlacedLight.TryBuild(placement, light, xespDisabled: true);

        Assert.NotNull(result);
        Assert.True(result.Value.IsInitiallyDisabled);
        Assert.Equal(0.75f, result.Value.Intensity);
    }

    [Fact]
    public void TryBuild_FnvUsesBasePlusExtraRadiusAndIgnoresPlacementScale()
    {
        var placement = Placement(0x111, 0x211, modelPath: null) with
        {
            Scale = 0.84f,
            Radius = -500f
        };
        var light = new LightRecord
        {
            FormId = placement.BaseFormId,
            Radius = 1500
        };

        var result = PlacedLight.TryBuild(
            placement,
            light,
            game: BethesdaGame.FalloutNewVegas);

        Assert.NotNull(result);
        Assert.Equal(1000f, result.Value.Radius);
    }

    [Fact]
    public void TryBuild_FnvExtraRadiusCanSupplyAnOtherwiseZeroBaseRadius()
    {
        var placement = Placement(0x112, 0x212, modelPath: null) with
        {
            Scale = 4f,
            Radius = 30f
        };
        var light = new LightRecord
        {
            FormId = placement.BaseFormId,
            Radius = 0
        };

        var result = PlacedLight.TryBuild(
            placement,
            light,
            game: BethesdaGame.FalloutNewVegas);

        Assert.NotNull(result);
        Assert.Equal(30f, result.Value.Radius);
    }

    [Fact]
    public void TryBuild_ZeroRadiusReturnsNull()
    {
        var placement = Placement(0x102, 0x202, modelPath: null);
        var light = new LightRecord { FormId = placement.BaseFormId, Radius = 0 };

        Assert.Null(PlacedLight.TryBuild(placement, light));
    }

    [Theory]
    [InlineData(0f, 100f, 1f)]
    [InlineData(50f, 100f, 0.75f)]
    [InlineData(100f, 100f, 0f)]
    [InlineData(150f, 100f, 0f)]
    [InlineData(10f, 0f, 0f)]
    public void RadialAttenuation_UsesRecoveredOneMinusDistanceSquaredCurve(
        float distance,
        float radius,
        float expected)
    {
        Assert.Equal(expected, PlacedLight.RadialAttenuation(distance, radius), 5);
    }

    [Fact]
    public void Selector_CapsByDistanceThenFormIdAndFiltersDisabledOrZeroIntensityLights()
    {
        var source = new[]
        {
            Emitter(1, new Vector3(0.1f, 0f, 0f), disabled: true),
            Emitter(2, new Vector3(0.2f, 0f, 0f), intensity: 0f),
            Emitter(30, Vector3.UnitX),
            Emitter(10, -Vector3.UnitX),
            Emitter(20, Vector3.UnitY)
        };
        var destination = new List<PlacedLight>();
        var scratch = new List<PlacedLight>();

        var clipped = PlacedLightSelector.AppendNearest(
            source,
            Vector3.Zero,
            maxPerCell: 2,
            includeInitiallyDisabled: false,
            destination: destination,
            scratch: scratch);

        Assert.Equal(1, clipped);
        Assert.Collection(
            destination,
            first => Assert.Equal(10u, first.FormId),
            second => Assert.Equal(20u, second.FormId));
    }

    [Fact]
    public void GpuPointLight_HasFourContiguousFloat4Fields()
    {
        Assert.Equal(64u, GpuPointLight.ByteSize);
        Assert.Equal(64, Marshal.SizeOf<GpuPointLight>());
        Assert.Equal(0, Marshal.OffsetOf<GpuPointLight>(nameof(GpuPointLight.PositionRadius)).ToInt32());
        Assert.Equal(16, Marshal.OffsetOf<GpuPointLight>(nameof(GpuPointLight.ColorIntensity)).ToInt32());
        Assert.Equal(32, Marshal.OffsetOf<GpuPointLight>(nameof(GpuPointLight.AuthoredMetadata)).ToInt32());
        Assert.Equal(48, Marshal.OffsetOf<GpuPointLight>(nameof(GpuPointLight.Reserved)).ToInt32());
    }

    [Fact]
    public void WorldRenderCache_BakesMeshlessAndModeledLightsAsEmittersButOnlyModeledAsGeometry()
    {
        const uint baseLightId = 0x500;
        var meshless = Placement(0x301, baseLightId, modelPath: null);
        var modeled = Placement(0x302, baseLightId, modelPath: "meshes\\lights\\fixture.nif");
        var cell = new CellRecord
        {
            FormId = 0x300,
            Flags = 0x01,
            PlacedObjects = [meshless, modeled]
        };
        var cache = new WorldRenderCache
        {
            LightIndex = new Dictionary<uint, LightRecord>
            {
                [baseLightId] = new LightRecord { FormId = baseLightId, Radius = 256, Color = 0x00ff_ffff }
            }
        };

        var geometry = cache.GetPlacementList(cell);
        var emitters = cache.GetPlacedLights(cell);

        var rendered = Assert.Single(geometry);
        Assert.Equal(modeled.FormId, rendered.FormId);
        Assert.Collection(
            emitters,
            first => Assert.Equal(meshless.FormId, first.FormId),
            second => Assert.Equal(modeled.FormId, second.FormId));
    }

    private static PlacedReference Placement(uint formId, uint baseFormId, string? modelPath) => new()
    {
        FormId = formId,
        BaseFormId = baseFormId,
        RecordType = "REFR",
        ModelPath = modelPath,
        Scale = 1f
    };

    private static PlacedLight Emitter(
        uint formId,
        Vector3 position,
        bool disabled = false,
        float intensity = 1f) => new(
        FormId: formId,
        BaseFormId: 0x900 + formId,
        Position: position,
        Radius: 100f,
        Color: Vector3.One,
        FalloffExponent: 1f,
        FieldOfView: 0f,
        Intensity: intensity,
        Flags: 0,
        IsInitiallyDisabled: disabled);
}
