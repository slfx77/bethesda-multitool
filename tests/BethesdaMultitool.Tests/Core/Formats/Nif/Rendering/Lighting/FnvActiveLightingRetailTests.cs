using System.Numerics;
using BethesdaMultitool.Core.Formats.Esm.Plugin.AssetPacking;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Lighting;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Materials;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Scene;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Tests.Core.Formats.Esm;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Lighting;

/// <summary>
///     Installed-master fixture for the active PS2/PS3 material-lighting investigation. It pins a
///     dense ordinary interior with enough local lights to exercise the engine's grouped 2/3-light
///     passes while keeping the placement/camera inputs deterministic for GPU comparisons.
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", BucketBTestGuard.Category)]
public sealed class FnvActiveLightingRetailTests(
    SampleFileFixture samples,
    ITestOutputHelper output)
{
    private const string MeshesBsaRelative =
        @"Sample\Full_Builds\Fallout New Vegas (PC Final)\Data\Fallout - Meshes.bsa";

    private const string PrimmGatedWallModelPath =
        @"meshes\architecture\urban\civicspace\gatedwall\urbangatedwallstrstone01_nv.nif";

    private const uint PrimmParkingLotCellFormId = 0x000E1A03;
    private const uint PrimmGatedWallReferenceFormId = 0x000A6826;
    private const uint PrimmGatedWallBaseFormId = 0x00176233;
    private const uint ProspectorSaloonInteriorFormId = 0x00106185;
    private const uint ProspectorMainEntranceExteriorDoorFormId = 0x0010636F;
    private const uint ProspectorMainEntranceInteriorDoorFormId = 0x0010618E;
    private const uint SouthGateFloodlightReferenceFormId = 0x0011A1F9;

    private const uint SouthGateFloodlightBaseFormId = 0x0004DECE;

    // FlythroughCameraController is WINDOWS_GUI-only; pin its production walk-eye default here so
    // the platform-neutral installed-master test can reproduce the same XTEL arrival pose.
    private const float WalkEyeHeight = 112f;

    [Fact]
    public void PrimmGatedWall_PinsOpaqueActiveAdtBaseFixtureAndCaptureCamera()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        Assert.SkipWhen(samples.PcFinalEsm is null, "PC final FalloutNV.esm not available");
        var meshesBsa = SampleFileFixture.FindSamplePath(MeshesBsaRelative);
        Assert.SkipWhen(meshesBsa is null, "FNV PC-final meshes BSA not available");

        using var archives = MeshArchiveSet.Open(
            meshesBsa!, null, false, false);
        Assert.True(
            archives.TryExtractFile(PrimmGatedWallModelPath, out var data, out _),
            $"Retail NIF missing: {PrimmGatedWallModelPath}");
        var nif = Assert.IsType<NifInfo>(NifParser.Parse(data));
        var model = Assert.IsType<NifRenderableModel>(NifGeometryExtractor.Extract(
            data,
            nif,
            null,
            skipSkinning: true,
            treatRootsAsIdentity: true,
            collectBillboards: true,
            dropBoneAttachedShapes: true));

        var expectedModes = new Dictionary<int, FnvClassicBasicShaderMode>
        {
            [15] = FnvClassicBasicShaderMode.Sls1009,
            [21] = FnvClassicBasicShaderMode.Sls1013VertexColor,
            [25] = FnvClassicBasicShaderMode.Sls1013VertexColor
        };
        var classified = model.Submeshes
            .Select(submesh => new
            {
                Submesh = submesh,
                Mode = FnvClassicBasicShaderPolicy.Resolve(nif, submesh)
            })
            .Where(static candidate => candidate.Mode != FnvClassicBasicShaderMode.None)
            .OrderBy(static candidate => candidate.Submesh.SourceBlockIndex)
            .ToArray();

        Assert.Equal([15, 21, 25], classified.Select(static candidate => candidate.Submesh.SourceBlockIndex));
        Assert.All(classified, candidate =>
        {
            var submesh = candidate.Submesh;
            Assert.Equal(expectedModes[submesh.SourceBlockIndex], candidate.Mode);
            Assert.Equal("BSShaderPPLightingProperty", submesh.ShaderMetadata?.PropertyType);
            Assert.Equal(NifLighting30EmissionPolicy.StandardShaderType, submesh.ShaderMetadata?.ShaderType);
            Assert.Equal(0x82000000u, submesh.ShaderMetadata?.ShaderFlags);
            Assert.Equal(0x00000001u, submesh.ShaderMetadata?.ShaderFlags2);
            Assert.Equal(0u, submesh.ShaderMetadata!.ShaderFlags!.Value & ((1u << 1) | (1u << 5)));
            Assert.False(submesh.HasAlphaBlend);
            Assert.Equal(1f, submesh.MaterialAlpha);
            Assert.Null(submesh.MaterialAlphaController);
        });

        var active = classified
            .Where(candidate => FnvActiveAdtBasePolicy.IsEligible(new FnvActiveAdtBaseEligibility(
                BethesdaGame.FalloutNewVegas,
                true,
                0,
                false,
                false,
                candidate.Submesh.HasAlphaBlend,
                candidate.Submesh.HasAlphaTest,
                candidate.Submesh.MaterialAlpha,
                candidate.Submesh.MaterialAlphaController is not null,
                candidate.Mode)))
            .ToArray();
        var activeFixture = Assert.Single(active);
        Assert.Equal(21, activeFixture.Submesh.SourceBlockIndex);
        Assert.Equal("UrbanGatedWallStr01:0", activeFixture.Submesh.ShapeName);
        Assert.False(activeFixture.Submesh.HasAlphaTest);
        Assert.True(activeFixture.Submesh.UseVertexColors);
        Assert.NotNull(activeFixture.Submesh.VertexColors);
        Assert.True(
            activeFixture.Submesh.VertexColors!.Length >= activeFixture.Submesh.VertexCount * 4);
        Assert.True(classified.Single(candidate => candidate.Submesh.SourceBlockIndex == 15).Submesh.HasAlphaTest);
        Assert.True(classified.Single(candidate => candidate.Submesh.SourceBlockIndex == 25).Submesh.HasAlphaTest);

        var collection = PcFinalEsmPipelineCache.GetOrBuild(samples.PcFinalEsm!).Collection;
        var wasteland = Assert.Single(
            collection.Worldspaces,
            static worldspace => worldspace.EditorId == "WastelandNV");
        var cell = Assert.Single(
            wasteland.Cells,
            static candidate => candidate.FormId == PrimmParkingLotCellFormId);
        Assert.Equal(-14, cell.GridX);
        Assert.Equal(-12, cell.GridY);
        var placement = Assert.Single(
            cell.PlacedObjects,
            static candidate => candidate.FormId == PrimmGatedWallReferenceFormId);
        Assert.Equal(PrimmGatedWallBaseFormId, placement.BaseFormId);
        Assert.Equal("urbangatedwallstrStone01NV", placement.BaseEditorId);
        Assert.Equal(
            PrimmGatedWallModelPath["meshes\\".Length..],
            placement.ModelPath,
            StringComparer.OrdinalIgnoreCase);
        Assert.Equal(-55_889.066f, placement.X);
        Assert.Equal(-47_042.832f, placement.Y);
        Assert.Equal(5_753.3906f, placement.Z);
        Assert.Equal(0f, placement.RotX);
        Assert.Equal(0f, placement.RotY);
        Assert.Equal(3.141593f, placement.RotZ);
        Assert.Equal(1f, placement.Scale);
        Assert.False(placement.IsInitiallyDisabled);

        var localMinimum = new Vector3(
            model.Submeshes.Min(static submesh => MinimumComponent(submesh.Positions, 0)),
            model.Submeshes.Min(static submesh => MinimumComponent(submesh.Positions, 1)),
            model.Submeshes.Min(static submesh => MinimumComponent(submesh.Positions, 2)));
        var localMaximum = new Vector3(
            model.Submeshes.Max(static submesh => MaximumComponent(submesh.Positions, 0)),
            model.Submeshes.Max(static submesh => MaximumComponent(submesh.Positions, 1)),
            model.Submeshes.Max(static submesh => MaximumComponent(submesh.Positions, 2)));
        var localCenter = (localMinimum + localMaximum) * 0.5f;
        VectorAssert.Equal(new Vector3(0.0001f, -26.0110f, 174.81065f), localCenter, 0.001f);

        var placementPosition = new Vector3(placement.X, placement.Y, placement.Z);
        var focus = placementPosition + RotateZ(localCenter * placement.Scale, -placement.RotZ);
        var camera = focus + new Vector3(0f, 650f, 50f);
        var toFocus = focus - camera;
        var yawDegrees = MathF.Atan2(toFocus.X, toFocus.Y) * (180f / MathF.PI);
        var pitchDegrees = MathF.Atan2(
            toFocus.Z,
            MathF.Sqrt(toFocus.X * toFocus.X + toFocus.Y * toFocus.Y)) * (180f / MathF.PI);

        output.WriteLine(
            $"Primm active ADT fixture REFR=0x{placement.FormId:X8}, base=0x{placement.BaseFormId:X8}, " +
            $"cell=0x{cell.FormId:X8}, model={placement.ModelPath}, eligible blocks=[{string.Join(',', active.Select(static candidate => candidate.Submesh.SourceBlockIndex))}].");
        output.WriteLine(
            $"Local bounds min={localMinimum}, max={localMaximum}, center={localCenter}; " +
            $"capture camera={camera}, pitch={pitchDegrees} deg, yaw={yawDegrees} deg.");

        // One opaque block on the pinned REFR is the fixture-level contract. The profiler's 16-cell
        // retail scene deliberately reports larger aggregate draw/instance counts from neighboring
        // eligible geometry and retains alpha-tested fallback submissions for blocks 15 and 25.
        Assert.Single(active);
        VectorAssert.Equal(new Vector3(-55_889.066f, -46_366.82f, 5_978.201f), camera, 0.01f);
        Assert.InRange(pitchDegrees, -4.399f, -4.398f);
        Assert.InRange(MathF.Abs(yawDegrees), 179.999f, 180.001f);
    }

    [Fact]
    public void ProspectorSaloon_PinsPlacedLightAndCameraFixture()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        Assert.SkipWhen(samples.PcFinalEsm is null, "PC final FalloutNV.esm not available");

        var collection = PcFinalEsmPipelineCache.GetOrBuild(samples.PcFinalEsm!).Collection;
        var cell = Assert.Single(
            collection.Cells,
            candidate => candidate.FormId == ProspectorSaloonInteriorFormId);
        Assert.Null(cell.GridX);
        Assert.Null(cell.GridY);

        var finite = cell.PlacedObjects
            .Where(static placement =>
                float.IsFinite(placement.X) &&
                float.IsFinite(placement.Y) &&
                float.IsFinite(placement.Z))
            .ToArray();
        Assert.NotEmpty(finite);

        var minimum = new Vector3(
            finite.Min(static placement => placement.X),
            finite.Min(static placement => placement.Y),
            finite.Min(static placement => placement.Z));
        var maximum = new Vector3(
            finite.Max(static placement => placement.X),
            finite.Max(static placement => placement.Y),
            finite.Max(static placement => placement.Z));
        var center = (minimum + maximum) * 0.5f;
        var extent = MathF.Max(maximum.X - minimum.X, maximum.Y - minimum.Y);
        var frameCamera = new Vector3(
            center.X,
            center.Y - MathF.Max(extent, 4096f) * 0.75f,
            center.Z + extent * 0.5f + 4096f);

        var localDoorReferenceIds = cell.PlacedObjects
            .Where(placement => collection.Doors.Any(door => door.FormId == placement.BaseFormId))
            .Select(static placement => placement.FormId)
            .ToHashSet();
        var allCells = collection.Cells
            .Concat(collection.Worldspaces.SelectMany(static worldspace => worldspace.Cells))
            .GroupBy(static candidate => candidate.FormId)
            .Select(static group => group.Last())
            .ToArray();
        var incomingDoors = allCells
            .SelectMany(sourceCell => sourceCell.PlacedObjects.Select(door => (SourceCell: sourceCell, Door: door)))
            .Where(candidate =>
                candidate.SourceCell.FormId != cell.FormId &&
                candidate.Door.TeleportPosRot is not null &&
                (candidate.Door.DestinationCellFormId == cell.FormId ||
                 (candidate.Door.DestinationDoorFormId is { } destinationDoor &&
                  localDoorReferenceIds.Contains(destinationDoor))))
            .OrderBy(static candidate => candidate.SourceCell.FormId)
            .ThenBy(static candidate => candidate.Door.FormId)
            .ToArray();
        var entrance = Assert.Single(
            incomingDoors,
            candidate => candidate.Door.FormId == ProspectorMainEntranceExteriorDoorFormId);
        Assert.Equal(ProspectorMainEntranceInteriorDoorFormId, entrance.Door.DestinationDoorFormId);
        var entranceArrival = entrance.Door.TeleportPosRot!;
        var camera = new Vector3(
            entranceArrival.X,
            entranceArrival.Y,
            entranceArrival.Z + WalkEyeHeight);
        var cameraYaw = -entranceArrival.RotZ;

        var lightIndex = collection.Lights
            .GroupBy(static light => light.FormId)
            .ToDictionary(static group => group.Key, static group => group.Last());
        var renderCache = new WorldRenderCache
        {
            Game = BethesdaGame.FalloutNewVegas,
            LightIndex = lightIndex
        };
        var lights = renderCache.GetPlacedLights(cell);
        var enabled = lights.Where(static light => !light.IsInitiallyDisabled).ToArray();
        var selected = new List<PlacedLight>();
        var scratch = new List<PlacedLight>();
        var enabledOverrides = new ReferenceEnabledOverrideStore();
        var clipped = PlacedLightSelector.AppendNearest(
            lights,
            camera,
            16,
            enabledOverrides,
            false,
            selected,
            scratch);

        output.WriteLine(
            $"Prospector Saloon placements={cell.PlacedObjects.Count:N0}, finite={finite.Length:N0}, " +
            $"lights={lights.Count:N0}, enabled={enabled.Length:N0}, selected={selected.Count:N0}, " +
            $"clipped={clipped:N0}.");
        output.WriteLine($"Bounds min={minimum}, max={maximum}, center={center}; frame camera={frameCamera}.");
        output.WriteLine(
            $"Main-entrance capture camera={camera}, yaw={cameraYaw} rad " +
            $"({cameraYaw * (180f / MathF.PI)} deg), pitch=0 deg.");
        foreach (var placement in
                 cell.PlacedObjects.Where(placement => localDoorReferenceIds.Contains(placement.FormId)))
        {
            output.WriteLine(
                $"LOCAL DOOR REFR=0x{placement.FormId:X8} base=0x{placement.BaseFormId:X8} " +
                $"editor={placement.BaseEditorId ?? "(none)"} pos=<{placement.X}, {placement.Y}, {placement.Z}> " +
                $"rotZ={placement.RotZ} destination=0x{placement.DestinationDoorFormId.GetValueOrDefault():X8} " +
                $"destinationCell=0x{placement.DestinationCellFormId.GetValueOrDefault():X8}.");
        }

        foreach (var incoming in incomingDoors)
        {
            var arrival = incoming.Door.TeleportPosRot!;
            output.WriteLine(
                $"INCOMING DOOR sourceCell=0x{incoming.SourceCell.FormId:X8}/{incoming.SourceCell.EditorId ?? "(none)"} " +
                $"REFR=0x{incoming.Door.FormId:X8} base=0x{incoming.Door.BaseFormId:X8} " +
                $"destination=0x{incoming.Door.DestinationDoorFormId.GetValueOrDefault():X8} " +
                $"arrival=<{arrival.X}, {arrival.Y}, " +
                $"{arrival.Z + WalkEyeHeight}> yaw={-arrival.RotZ} rad " +
                $"({-arrival.RotZ * (180f / MathF.PI)} deg).");
        }

        foreach (var light in selected)
        {
            output.WriteLine(
                $"LIGH REFR=0x{light.FormId:X8} base=0x{light.BaseFormId:X8} " +
                $"pos={light.Position} radius={light.Radius:0.###} color={light.Color} " +
                $"intensity={light.Intensity:0.###} flags=0x{light.Flags:X8}.");
        }

        Assert.True(lights.Count > 16);
        Assert.NotEmpty(incomingDoors);
        Assert.Equal(132.97281f, camera.X);
        Assert.Equal(-821.69995f, camera.Y);
        Assert.Equal(3568f, camera.Z);
        Assert.InRange(MathF.Abs(cameraYaw), 0f, 0.000001f);
        Assert.Equal(16, selected.Count);
        Assert.True(clipped > 0);
    }

    [Fact]
    public void SouthGateFloodlight_UsesSignedExtraRadiusWithoutPlacementScale()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        Assert.SkipWhen(samples.PcFinalEsm is null, "PC final FalloutNV.esm not available");
        var collection = PcFinalEsmPipelineCache.GetOrBuild(samples.PcFinalEsm!).Collection;
        var cells = collection.Cells
            .Concat(collection.Worldspaces.SelectMany(static worldspace => worldspace.Cells))
            .GroupBy(static cell => cell.FormId)
            .Select(static group => group.Last());
        var placement = Assert.Single(
            cells.SelectMany(static cell => cell.PlacedObjects),
            static candidate => candidate.FormId == SouthGateFloodlightReferenceFormId);
        var light = Assert.Single(
            collection.Lights,
            static candidate => candidate.FormId == SouthGateFloodlightBaseFormId);

        Assert.Equal(SouthGateFloodlightBaseFormId, placement.BaseFormId);
        Assert.Equal("VFSSouthGateFloodlightREF", placement.EditorId);
        Assert.Equal("FXWashElvtrShaftLight01", placement.BaseEditorId);
        Assert.Equal(1500u, light.Radius);
        Assert.Equal(-500f, placement.Radius);
        Assert.Equal(0.84f, placement.Scale);
        Assert.True(placement.IsInitiallyDisabled);

        var built = PlacedLight.TryBuild(
            placement,
            light,
            game: BethesdaGame.FalloutNewVegas);

        Assert.NotNull(built);
        Assert.Equal(1000f, built.Value.Radius);
    }

    private static float MinimumComponent(float[] positions, int component)
    {
        var minimum = float.PositiveInfinity;
        for (var offset = component; offset < positions.Length; offset += 3)
        {
            minimum = Math.Min(minimum, positions[offset]);
        }

        return minimum;
    }

    private static float MaximumComponent(float[] positions, int component)
    {
        var maximum = float.NegativeInfinity;
        for (var offset = component; offset < positions.Length; offset += 3)
        {
            maximum = Math.Max(maximum, positions[offset]);
        }

        return maximum;
    }

    private static Vector3 RotateZ(Vector3 value, float radians)
    {
        var sin = MathF.Sin(radians);
        var cos = MathF.Cos(radians);
        return new Vector3(
            value.X * cos - value.Y * sin,
            value.X * sin + value.Y * cos,
            value.Z);
    }
}
