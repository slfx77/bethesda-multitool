using BethesdaMultitool.Core.Formats.Bsa.Extraction;
using BethesdaMultitool.Core.Formats.Bsa.Parsing;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Inspection;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Textures;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Textures;

/// <summary>
///     Guards Morrowind lava rendering (<c>i\in_lava_1024.nif</c>, "lava renders untextured"): its
///     renderable shapes sit under <c>NiBSAnimationNode</c> roots (a Bethesda NiNode subclass) with
///     UV-scroll animation (NiUVController) and carry ordinary NiTexturingProperty → NiSourceTexture
///     chains (Tx_lava_molten/crust.tga) — all of which must classify + resolve like any NiNode
///     scene. Reads the real Morrowind.bsa; skips when absent (CI).
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
public class Tes3LavaNifProbe
{
    private const string Bsa = @"E:\SteamLibrary\SteamApps\common\Morrowind\Data Files\Morrowind.bsa";
    private const string MeshPath = @"meshes\i\in_lava_1024.nif";

    [Fact]
    public void LavaShapes_ClassifyAndResolveTextures()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        Assert.SkipUnless(File.Exists(Bsa), "Morrowind.bsa not present (dev-machine-only asset).");

        using var extractor = new BsaExtractor(Bsa);
        var archive = BsaParser.Parse(Bsa);
        var file = archive.AllFiles.First(f => string.Equals(f.FullPath, MeshPath, StringComparison.OrdinalIgnoreCase));
        var data = extractor.ExtractFile(file);

        var nif = NifParser.Parse(data);
        Assert.NotNull(nif);

        // The scene roots are NiBSAnimationNode — they must be walked as nodes or their shapes
        // never inherit transforms / get reached by node-driven consumers.
        Assert.Contains(nif.Blocks, b => b.TypeName == "NiBSAnimationNode");

        var nodeChildren = new Dictionary<int, List<int>>();
        var shapeDataMap = new Dictionary<int, int>();
        var shapePropertyMap = new Dictionary<int, List<int>>();
        NifSceneGraphWalker.ClassifyBlocks(data, nif, nodeChildren, shapeDataMap, shapePropertyMap);

        // Every NiBSAnimationNode must have been classified as a NODE (children parsed).
        for (var i = 0; i < nif.Blocks.Count; i++)
        {
            if (nif.Blocks[i].TypeName == "NiBSAnimationNode")
            {
                Assert.True(nodeChildren.ContainsKey(i),
                    $"NiBSAnimationNode block {i} was not classified as a scene node");
            }
        }

        // The renderable shapes (Tri Magma / Tri Magma01 / Tri In_Lava_1024) must classify with
        // resolvable diffuse textures; the AvoidNode/RootCollisionNode hulls must be excluded.
        var texturedShapes = shapeDataMap.Keys
            .Where(i => shapePropertyMap.TryGetValue(i, out var p)
                        && NifTexturingPropertyReader.ResolveBaseTexturePath(data, nif, p) is { } tex
                        && tex.Contains("lava", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.True(texturedShapes.Count >= 3,
            $"expected >=3 lava-textured shapes, got {texturedShapes.Count} " +
            $"(shapeDataMap={shapeDataMap.Count}, withProps={shapePropertyMap.Count})");
    }
}