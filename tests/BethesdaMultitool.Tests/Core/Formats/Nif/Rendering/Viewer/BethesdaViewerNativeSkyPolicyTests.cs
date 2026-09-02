using BethesdaMultitool.Core.Formats.Nif;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Viewer;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Viewer;

public sealed class BethesdaViewerNativeSkyPolicyTests
{
    [Fact]
    public void Raw_scene_with_only_drawable_sky_layers_uses_dedicated_framing()
    {
        var scene = CreateScene(BethesdaViewerScenePurpose.RawNif);
        AddPart(scene, SkyObjectType.Sky, drawable: true);
        AddPart(scene, SkyObjectType.Clouds, drawable: true);

        Assert.True(BethesdaViewerNativeSkyPolicy.ShouldUseDedicatedRawNifFraming(scene));
    }

    [Fact]
    public void Mixed_raw_scene_keeps_ordinary_mesh_framing()
    {
        var scene = CreateScene(BethesdaViewerScenePurpose.RawNif);
        AddPart(scene, SkyObjectType.Sky, drawable: true);
        AddPart(scene, type: null, drawable: true);

        Assert.False(BethesdaViewerNativeSkyPolicy.ShouldUseDedicatedRawNifFraming(scene));
    }

    [Fact]
    public void Assembled_scene_never_uses_raw_sky_framing()
    {
        var scene = CreateScene(BethesdaViewerScenePurpose.NpcAppearance);
        AddPart(scene, SkyObjectType.Sky, drawable: true);

        Assert.False(BethesdaViewerNativeSkyPolicy.ShouldUseDedicatedRawNifFraming(scene));
    }

    [Fact]
    public void Empty_raw_scene_does_not_opt_in()
    {
        var scene = CreateScene(BethesdaViewerScenePurpose.RawNif);
        AddPart(scene, type: null, drawable: false);

        Assert.False(BethesdaViewerNativeSkyPolicy.ShouldUseDedicatedRawNifFraming(scene));
    }

    private static BethesdaViewerScene CreateScene(BethesdaViewerScenePurpose purpose) =>
        new("test", purpose);

    private static void AddPart(
        BethesdaViewerScene scene,
        SkyObjectType? type,
        bool drawable)
    {
        scene.MeshParts.Add(new BethesdaViewerMeshPart
        {
            Name = type?.ToString() ?? "ordinary",
            Submesh = new RenderableSubmesh
            {
                Positions = drawable ? [0f, 0f, 0f] : [],
                Triangles = drawable ? [0, 0, 0] : [],
                SkyType = type,
            },
        });
    }
}
