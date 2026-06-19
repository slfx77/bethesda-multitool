using FalloutXbox360Utils.Core.Formats.Bsa;
using FalloutXbox360Utils.Core.Formats.Nif;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Textures;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Nif.Rendering;

/// <summary>
///     Guards the RootCollisionNode-exclusion fix: a Morrowind NIF's collision hull (the NiTriShape
///     under a <c>RootCollisionNode</c>) must NOT be classified as a renderable shape, while the real
///     textured shapes must be. Without this, the untextured collision hull renders as white panels /
///     a dark blob over the mesh. Reads the real Morrowind.bsa; skips when it isn't present (CI).
/// </summary>
public class Tes3NifStructureProbe
{
    private const string Bsa = @"E:\SteamLibrary\SteamApps\common\Morrowind\Data Files\Morrowind.bsa";
    private const string MeshPath = @"meshes\f\flora_treestump_wg_01.nif";

    [Fact]
    public void RootCollisionNode_GeometryIsExcludedFromRendering()
    {
        Assert.SkipUnless(File.Exists(Bsa), "Morrowind.bsa not present (dev-machine-only asset).");

        using var extractor = new BsaExtractor(Bsa);
        var archive = BsaParser.Parse(Bsa);
        var file = archive.AllFiles.First(f => string.Equals(f.FullPath, MeshPath, StringComparison.OrdinalIgnoreCase));
        var data = extractor.ExtractFile(file);

        var nif = NifParser.Parse(data);
        Assert.NotNull(nif);
        Assert.True(nif.IsMorrowind);

        var nodeChildren = new Dictionary<int, List<int>>();
        var shapeDataMap = new Dictionary<int, int>();
        var shapePropertyMap = new Dictionary<int, List<int>>();
        NifSceneGraphWalker.ClassifyBlocks(data, nif, nodeChildren, shapeDataMap, shapePropertyMap);

        var collisionShapeIndices = CollisionShapeIndices(data, nif);
        Assert.NotEmpty(collisionShapeIndices); // the test mesh does have a collision hull

        // Collision shapes must be excluded from the render maps.
        foreach (var idx in collisionShapeIndices)
        {
            Assert.DoesNotContain(idx, shapeDataMap.Keys);
            Assert.DoesNotContain(idx, shapePropertyMap.Keys);
        }

        // The two real shapes must remain, each with a resolvable diffuse texture.
        var texturedShapes = shapeDataMap.Keys
            .Where(i => shapePropertyMap.TryGetValue(i, out var p)
                        && NifTexturingPropertyReader.ResolveBaseTexturePath(data, nif, p) is not null)
            .ToList();
        Assert.True(texturedShapes.Count >= 2, $"expected >=2 textured shapes, got {texturedShapes.Count}");
    }

    private static List<int> CollisionShapeIndices(byte[] data, NifInfo nif)
    {
        var result = new List<int>();
        for (var i = 0; i < nif.Blocks.Count; i++)
        {
            if (nif.Blocks[i].TypeName != "RootCollisionNode")
            {
                continue;
            }

            var kids = NifBlockParsers.ParseNodeChildren(data, nif.Blocks[i], nif.BsVersion, nif.IsBigEndian,
                nif.HasInlineStrings);
            if (kids is null)
            {
                continue;
            }

            result.AddRange(kids.Where(k =>
                k >= 0 && k < nif.Blocks.Count && NifSceneGraphWalker.ShapeTypes.Contains(nif.Blocks[k].TypeName)));
        }

        return result;
    }
}