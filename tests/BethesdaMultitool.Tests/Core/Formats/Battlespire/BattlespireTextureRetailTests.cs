using BethesdaMultitool.Core.AssetBrowse;
using BethesdaMultitool.Core.Formats.Battlespire;
using BethesdaMultitool.Core.Formats.Bsa.Index;
using BethesdaMultitool.Core.Formats.Xngine.Mesh;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Battlespire;

/// <summary>
///     Opt-in (<c>RUN_BUCKET_B=1</c>) checks of the mesh-texture mapping on the retail install: every
///     plane key of every mesh source decodes to a BSI stem the archive holds, to a colour, or to
///     one of twelve legal names the shipped archive lacks; two named meshes carry the texture
///     names a wall and a barrel should; L8's material census is pinned; and the GUI seams hand
///     the viewer textured scenes.
///     <para>
///         The expected figures were measured OUTSIDE this code (a Python walk over the same files
///         with its own LZSS and base-40 implementations, 2026-09-08). A key that decodes to a
///         name the archive holds by chance has probability ~2,592 / 40^6 ≈ 6e-7, so the resolution
///         counts could not be reached by a wrong alphabet or radix — that is what would fail here.
///     </para>
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", BucketBTestGuard.Category)]
public sealed class BattlespireTextureRetailTests
{
    /// <summary>The twelve legal names retail meshes carry that BSI.BSA does not hold.</summary>
    private static readonly string[] AbsentNames =
    [
        "hand00", "hand01", "hand02", "hand03", "hand05", "hand07", "hand08", "hand09",
        "keyhol", "presto", "strut0", "wwheel"
    ];

    private static string RequireGameData()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var root = RealAssetPaths.Classics.Battlespire();
        Assert.SkipWhen(root is null, RealAssetPaths.SkipMessage("Battlespire (GAMEDATA)"));
        return root;
    }

    private static HashSet<string> BsiStems(string root)
    {
        var path = Path.Combine(root, "BSI.BSA");
        Assert.SkipWhen(!File.Exists(path), RealAssetPaths.SkipMessage("BSI.BSA"));
        using var archive = ArchiveReader.Open(path);
        return archive.ListFiles()
            .Where(e => e.Name.EndsWith(".BSI", StringComparison.OrdinalIgnoreCase))
            .Select(e => Path.GetFileNameWithoutExtension(e.Name).ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);
    }

    private static IEnumerable<XnGineMesh> Meshes(string root, string source)
    {
        if (source == "loose")
        {
            foreach (var path in Directory.EnumerateFiles(root, "*.3D").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                yield return BattlespireMeshArchive.ParseLoose(File.ReadAllBytes(path), Path.GetFileName(path));
            }

            yield break;
        }

        var archivePath = Path.Combine(root, source);
        Assert.SkipWhen(!File.Exists(archivePath), RealAssetPaths.SkipMessage(source));
        using var archive = BattlespireMeshArchive.Open(archivePath);
        for (var i = 0; i < archive.Count; i++)
        {
            if (archive.TryParse(i, out var mesh, out _))
            {
                yield return mesh;
            }
        }
    }

    /// <summary>
    ///     Source, planes, planes naming an image BSI.BSA holds, colour planes, planes naming an
    ///     image it lacks. Each source accounts for every one of its planes.
    /// </summary>
    [Theory]
    [InlineData("loose", 7_945, 7_807, 138, 0)]
    [InlineData("3D.BSA", 134_754, 133_477, 933, 344)]
    [InlineData("3D.BS6", 123_235, 122_323, 568, 344)]
    public void EveryPlaneKey_NamesAnImageOrAColour(string source, int expectedPlanes, int expectedResolved,
        int expectedColour, int expectedAbsent)
    {
        var root = RequireGameData();
        var stems = BsiStems(root);

        var planes = 0;
        var resolved = 0;
        var colour = 0;
        var absent = new SortedSet<string>(StringComparer.Ordinal);
        var absentPlanes = 0;
        var undecodable = 0;
        foreach (var mesh in Meshes(root, source))
        {
            foreach (var plane in mesh.Planes)
            {
                planes++;
                var key = plane.TextureKey;
                Assert.Equal(key, BattlespireTextureResolver.Key(plane.TextureArchive, plane.TextureRecord));

                if (BattlespireTextureName.IsSolidColor(key))
                {
                    colour++;
                    // The colour word is a 15-bit colour shifted left once: bit 0 is never set.
                    Assert.Equal(0u, key & 1);
                    continue;
                }

                var name = BattlespireTextureName.Decode(key);
                if (name is null)
                {
                    undecodable++;
                    continue;
                }

                // Every decoded name re-encodes to its key: the codec is a bijection on this population.
                Assert.Equal(key, BattlespireTextureName.Encode(name));
                if (stems.Contains(name))
                {
                    resolved++;
                }
                else
                {
                    absent.Add(name);
                    absentPlanes++;
                }
            }
        }

        Assert.Equal(expectedPlanes, planes);
        Assert.Equal(expectedResolved, resolved);
        Assert.Equal(expectedColour, colour);
        Assert.Equal(expectedAbsent, absentPlanes);
        Assert.Equal(0, undecodable);
        if (expectedAbsent > 0)
        {
            Assert.Equal(AbsentNames, absent);
        }
        else
        {
            Assert.Empty(absent);
        }
    }

    [Fact]
    public void APinnedWallAndAPinnedBarrel_NameTheArtTheyShouldWear()
    {
        var root = RequireGameData();
        var archivePath = Path.Combine(root, "3D.BSA");
        Assert.SkipWhen(!File.Exists(archivePath), RealAssetPaths.SkipMessage("3D.BSA"));

        using var archive = BattlespireMeshArchive.Open(archivePath);
        using var textures = BattlespireTextureResolver.Open(root);

        // 7ARCH.3D is L8's arch: walls, a floor, a pillar, an alcove and a cell wall.
        var arch = archive.Parse(archive.IndexOf("7ARCH.3D"));
        Assert.Equal(["alcv02", "cell02", "flor01", "pilr06", "wall35", "wall87"], NamesOf(arch));

        // 7VOLC1.3D is one panel of L8's volcanic ring.
        var ring = archive.Parse(archive.IndexOf("7VOLC1.3D"));
        Assert.Equal(["volc00", "volc01"], NamesOf(ring));

        // The loose BAREL.3D is a barrel: barrel art plus wood.
        var barrel = BattlespireMeshArchive.ParseLoose(File.ReadAllBytes(Path.Combine(root, "BAREL.3D")), "BAREL.3D");
        Assert.Equal(["barl01", "wood01"], NamesOf(barrel));
        Assert.Contains(barrel.Planes, p => p.TextureKey == 0x44C50141u);

        // And every one of those names resolves to an image, named after its stem.
        foreach (var mesh in new[] { arch, ring, barrel })
        {
            foreach (var (a, r) in mesh.UniqueTextures)
            {
                var png = textures.Resolve(a, r);
                Assert.NotNull(png);
                Assert.Equal(BattlespireTextureResolver.NameOf(a, r), png.Name);
                Assert.True(png.Width > 0 && png.Height > 0);
            }
        }

        Assert.Empty(textures.MissingNames);
    }

    private static List<string> NamesOf(XnGineMesh mesh)
    {
        return mesh.Planes.Select(p => BattlespireTextureName.Decode(p.TextureKey))
            .OfType<string>().Distinct().Order(StringComparer.Ordinal).ToList();
    }

    [Fact]
    public void L8_CarriesSeventyTwoMaterials_AllOfWhichResolve()
    {
        var root = RequireGameData();
        var levels = Path.Combine(root, "BS6.BSA");
        Assert.SkipWhen(!File.Exists(levels), RealAssetPaths.SkipMessage("BS6.BSA"));

        using var levelArchive = ArchiveReader.Open(levels);
        var entry = levelArchive.ListFiles().Single(e => e.Name.Equals("L8.BS6", StringComparison.OrdinalIgnoreCase));
        var level = Bs6File.Parse(levelArchive.ReadFile(entry.FullPath)!, entry.Name);

        using var meshes = BattlespireMeshLibrary.Open(root);
        using var sprites = BattlespireFlatSpriteSource.Open(root);
        using var textures = BattlespireTextureResolver.Open(root);
        var assembly = Bs6SceneAssembler.Assemble(level, meshes.Resolve);
        var flats = Bs6SceneAssembler.AssembleFlats(level.Flats, sprites.SizeOf, sprites.Register);

        var meshMaterials = assembly.Instances
            .SelectMany(i => i.Mesh.SubMeshes)
            .Select(s => (s.TextureArchive, s.TextureRecord))
            .Distinct()
            .ToList();
        var flatMaterials = flats.Instances
            .SelectMany(i => i.Mesh.SubMeshes)
            .Select(s => (s.TextureArchive, s.TextureRecord))
            .Distinct()
            .ToList();

        // 65 distinct mesh texture keys, none of them a colour plane, plus 7 distinct flat sprites.
        Assert.Equal(65, meshMaterials.Count);
        Assert.Equal(7, flatMaterials.Count);
        Assert.DoesNotContain(meshMaterials, m => BattlespireTextureName.IsSolidColor(BattlespireTextureResolver.Key(m.TextureArchive, m.TextureRecord)));
        Assert.All(flatMaterials, m => Assert.Equal(Bs6FlatBillboard.FlatTextureArchive, m.TextureArchive));

        Assert.All(meshMaterials, m => Assert.NotNull(textures.Resolve(m.TextureArchive, m.TextureRecord)));
        Assert.All(flatMaterials, m => Assert.NotNull(sprites.Resolve(m.TextureArchive, m.TextureRecord)));
        Assert.Empty(textures.MissingNames);
        Assert.Equal(112, flats.Instances.Count);
    }

    [Fact]
    public void TheLevelPane_HandsTheViewerL8Textured()
    {
        var root = RequireGameData();
        var levels = Path.Combine(root, "BS6.BSA");
        Assert.SkipWhen(!File.Exists(levels), RealAssetPaths.SkipMessage("BS6.BSA"));

        using var session = AssetBrowseSession.OpenArchive(levels);
        var node = session.Root.Children.Single(n => n.Name.Equals("L8.BS6", StringComparison.OrdinalIgnoreCase));
        var scene = ClassicLevelPreviewSource.TryLoad(session, node, CancellationToken.None);

        Assert.NotNull(scene);
        // 65 mesh textures + 7 flat sprites, registered as generated textures under their lookup paths.
        Assert.Equal(72, scene.GeneratedTextures.Count);
        Assert.All(scene.MeshParts, part =>
        {
            Assert.False(string.IsNullOrEmpty(part.Submesh.DiffuseTexturePath), part.Name + " is untextured");
            Assert.True(scene.TryGetGeneratedTexture(part.Submesh.DiffuseTexturePath!, out _),
                part.Name + " names a texture the scene does not carry");
        });

        // The flats ride along, so the pane shows what `classic level export` writes.
        Assert.Equal(112, scene.MeshParts.Count(p => p.Name.StartsWith("flat_", StringComparison.Ordinal)));
    }

    [Fact]
    public void TheMeshPane_HandsTheViewerAnArchTextured()
    {
        var root = RequireGameData();
        var archivePath = Path.Combine(root, "3D.BSA");
        Assert.SkipWhen(!File.Exists(archivePath), RealAssetPaths.SkipMessage("3D.BSA"));

        using var session = AssetBrowseSession.OpenArchive(archivePath);
        var node = session.Root.Children.Single(n => n.Name.Equals("7ARCH.3D", StringComparison.OrdinalIgnoreCase));
        var scene = ClassicMeshPreviewSource.TryLoad(session, node, CancellationToken.None);

        Assert.NotNull(scene);
        Assert.Equal(6, scene.GeneratedTextures.Count);
        Assert.Equal(6, scene.MeshParts.Count);
        Assert.Equal(
            ["battlespire/bsi/alcv02", "battlespire/bsi/cell02", "battlespire/bsi/flor01", "battlespire/bsi/pilr06",
                "battlespire/bsi/wall35", "battlespire/bsi/wall87"],
            scene.MeshParts.Select(p => p.Submesh.DiffuseTexturePath!).Order(StringComparer.Ordinal));

        // UVs are normalised by the texture's own size: the largest |u| the viewer sees equals the
        // largest texel |u| the decomposer produced divided by the wall texture's width. Handing
        // texel units through unscaled (the earlier behaviour) is off by that width.
        using var archive = BattlespireMeshArchive.Open(archivePath);
        var decomposed = XnGineMeshDecomposer.Decompose(archive.Parse(archive.IndexOf("7ARCH.3D")));
        var wallKey = BattlespireTextureName.Encode("wall35");
        var wallSubMesh = decomposed.SubMeshes.Single(s => BattlespireTextureResolver.Key(s.TextureArchive, s.TextureRecord) == wallKey);
        var texelMaxU = wallSubMesh.Vertices.Max(v => MathF.Abs(v.TexelUv.X));
        Assert.True(texelMaxU > 1f, "the wall's texel UVs are expected to span whole texels");

        Assert.True(scene.TryGetGeneratedTexture("battlespire/bsi/wall35", out var wallTexture));
        var wall = scene.MeshParts.Single(p => p.Submesh.DiffuseTexturePath == "battlespire/bsi/wall35");
        var viewerMaxU = wall.Submesh.UVs!.Where((_, i) => i % 2 == 0).Max(MathF.Abs);
        Assert.Equal(texelMaxU / wallTexture!.Width, viewerMaxU, 4);
    }
}
