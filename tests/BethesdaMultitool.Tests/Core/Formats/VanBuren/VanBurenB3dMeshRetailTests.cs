using System.Numerics;
using BethesdaMultitool.Core.Formats.VanBuren;
using BethesdaMultitool.Tests.Helpers;
using SharpGLTF.Schema2;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.VanBuren;

/// <summary>
///     The B3D decoder over every payload of the Dec 2003 prototype's 24 <c>.grp</c> archives. The
///     figures are the ones measured 2026-09-08 with an independent Python walk; each is pinned so
///     that a reader that stopped tiling, dropped a form, or mis-sized a field could not stay green.
///     ⚠ The first draft of these pins (1,274,565 vertices / 816,098 triangles / 8,392 groups) came
///     from a walk that used the mesh's FIRST BYTE to choose the form and so refused Props #29; the
///     flag-rule reader decodes it, and the totals below include its 513 vertices, 274 triangles
///     and one group. The handedness, chirality and winding figures were re-derived 2026-09-08 with
///     an independent walk (scratch chirality.py / facing2.py) before being pinned here.
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", BucketBTestGuard.Category)]
public sealed class VanBurenB3dMeshRetailTests
{
    private static List<Decoded>? _cache;

    private static List<Decoded> DecodeAll(string root)
    {
        if (_cache is not null)
        {
            return _cache;
        }

        var results = new List<Decoded>();
        foreach (var grp in Directory.GetFiles(Path.Combine(root, "data"), "*.grp")
                     .OrderBy(p => p, StringComparer.Ordinal))
        {
            var bytes = File.ReadAllBytes(grp);
            var archive = VanBurenGrpArchive.Parse(bytes, Path.GetFileName(grp));
            foreach (var entry in archive.Entries)
            {
                if (entry.Size == 0)
                {
                    continue;
                }

                var payload = VanBurenGrpArchive.Read(bytes, entry);
                if (!VanBurenB3DFile.IsB3d(payload))
                {
                    continue;
                }

                var name = Path.GetFileName(grp) + "/" + entry.Name;
                var ok = VanBurenB3DFile.TryParse(payload, name, out var file, out var error);
                results.Add(new Decoded(Path.GetFileName(grp), entry.Index, payload[8], ok ? file : null, error));
            }
        }

        _cache = results;
        return results;
    }

    [Fact]
    public void EveryPayloadButTheThreeOpcode53OnesTilesExactly()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var root = RealAssetPaths.Classics.VanBuren();
        Assert.SkipWhen(root is null, "Van Buren prototype fixture missing");

        var all = DecodeAll(root);
        Assert.Equal(3915, all.Count);

        var failed = all.Where(d => d.File is null).ToList();
        Assert.Equal(3, failed.Count);
        Assert.All(failed, d => Assert.Equal("Critters.grp", d.Archive));
        Assert.All(failed, d => Assert.Equal(53, d.FirstOpcode));
        Assert.All(failed, d => Assert.Contains("0x35", d.Error, StringComparison.Ordinal));

        var decoded = all.Where(d => d.File is not null).Select(d => d.File!).ToList();
        Assert.Equal(3912, decoded.Count);

        // Every payload is Scene Root -> one named node holding exactly one mesh.
        Assert.All(decoded, f => Assert.Equal("Scene Root", f.NodeNames[0]));
        Assert.All(decoded, f => Assert.Equal(2, f.NodeNames.Count));
        Assert.All(decoded, f => Assert.Single(f.Meshes));
        Assert.Equal(3589, decoded.Select(f => f.MeshName).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void TheSceneFlagDecidesTheMeshForm()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var root = RealAssetPaths.Classics.VanBuren();
        Assert.SkipWhen(root is null, "Van Buren prototype fixture missing");

        var decoded = DecodeAll(root).Where(d => d.File is not null).ToList();

        // The 0x1C flag is present on exactly the 3,808 payloads whose first opcode is 28, and it is
        // what selects a version byte; the 104 unflagged ones (first opcode 3) are all raw source form.
        var flagged = decoded.Where(d => d.File!.Versioned).ToList();
        Assert.Equal(3808, flagged.Count);
        Assert.All(flagged, d => Assert.Equal(28, d.FirstOpcode));
        var unflagged = decoded.Where(d => !d.File!.Versioned).ToList();
        Assert.Equal(104, unflagged.Count);
        Assert.All(unflagged, d => Assert.Equal(3, d.FirstOpcode));
        Assert.All(unflagged, d => Assert.Equal(VanBurenB3dMeshForm.Source, d.File!.Meshes[0].Form));

        var byArchiveAndForm = decoded
            .GroupBy(d => (d.Archive, d.File!.Meshes[0].Form))
            .ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(453, byArchiveAndForm[("Critters.grp", VanBurenB3dMeshForm.Converted)]);
        Assert.Equal(129, byArchiveAndForm[("Items.grp", VanBurenB3dMeshForm.Converted)]);
        Assert.Equal(298, byArchiveAndForm[("Props.grp", VanBurenB3dMeshForm.Converted)]);
        Assert.Equal(1, byArchiveAndForm[("Tiles.grp", VanBurenB3dMeshForm.Converted)]);
        Assert.Equal(2927, byArchiveAndForm[("Tiles.grp", VanBurenB3dMeshForm.Source)]);
        Assert.Equal(89, byArchiveAndForm[("Props.grp", VanBurenB3dMeshForm.Source)]);
        Assert.Equal(13, byArchiveAndForm[("Effects.grp", VanBurenB3dMeshForm.Source)]);
        Assert.Equal(2, byArchiveAndForm[("Items.grp", VanBurenB3dMeshForm.Source)]);
        Assert.Equal(881, decoded.Count(d => d.File!.Meshes[0].Form == VanBurenB3dMeshForm.Converted));
    }

    [Fact]
    public void TheMeshsFirstByteWouldMisreadPropsTwentyNine()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var root = RealAssetPaths.Classics.VanBuren();
        Assert.SkipWhen(root is null, "Van Buren prototype fixture missing");

        // The one retail payload that separates the two candidate discriminators: unflagged, so
        // the flag rule reads the source form straight away — but its vertex count is 513 = 0x201,
        // whose first byte 0x01 a first-byte rule takes for "version 1, converted" and then fails to
        // tile. A reader that switched to the first-byte rule would refuse this entry.
        var props29 = DecodeAll(root).Single(d => d.Archive == "Props.grp" && d.Index == 29);
        Assert.NotNull(props29.File);
        var file = props29.File!;
        Assert.False(file.Versioned);
        Assert.Equal(3, props29.FirstOpcode);
        Assert.Equal("PS_Mines1{23}Rocks_Medium2", file.MeshName);
        var mesh = Assert.Single(file.Meshes);
        Assert.Equal(VanBurenB3dMeshForm.Source, mesh.Form);
        Assert.Equal(513, mesh.Vertices.Count);
        Assert.Equal(274, mesh.TriangleCount);
        var group = Assert.Single(mesh.Groups);
        Assert.Equal(["PS_Mines2.tga"], group.Textures);
    }

    [Fact]
    public void GeometryIsWellFormedOnEveryMesh()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var root = RealAssetPaths.Classics.VanBuren();
        Assert.SkipWhen(root is null, "Van Buren prototype fixture missing");

        var meshes = DecodeAll(root).Where(d => d.File is not null).Select(d => d.File!.Meshes[0]).ToList();

        long vertices = 0, triangles = 0, unitNormals = 0, lightmapUvs = 0, lightmapInRange = 0;
        foreach (var mesh in meshes)
        {
            vertices += mesh.Vertices.Count;
            triangles += mesh.TriangleCount;
            foreach (var v in mesh.Vertices)
            {
                if (Math.Abs(v.Normal.Length() - 1f) < 0.01f)
                {
                    unitNormals++;
                }

                if (mesh.Form == VanBurenB3dMeshForm.Converted)
                {
                    lightmapUvs++;
                    if (v.LightmapCoord.X is >= -0.001f and <= 1.001f && v.LightmapCoord.Y is >= -0.001f and <= 1.001f)
                    {
                        lightmapInRange++;
                    }
                }
            }

            // Triangle lists: every group's index count is a multiple of 3 and every index is in range
            // (the reader throws otherwise, so reaching here already proves the latter).
            Assert.All(mesh.Groups, g => Assert.Equal(0, g.Indices.Length % 3));
            Assert.All(mesh.Groups, g => Assert.All(g.Indices, i => Assert.InRange(i, 0, mesh.Vertices.Count - 1)));
        }

        Assert.Equal(1_275_078, vertices);
        Assert.Equal(816_372, triangles);
        Assert.Equal(vertices, unitNormals);
        Assert.Equal(327_402, lightmapUvs);
        Assert.Equal(lightmapUvs, lightmapInRange);

        // A converted mesh's declared bounding box is the box of its vertices — a check that fails
        // the moment the 44-byte vertex is read at the wrong stride or offset.
        var converted = meshes.Where(m => m.Form == VanBurenB3dMeshForm.Converted).ToList();
        Assert.Equal(881, converted.Count);
        Assert.All(converted, m =>
        {
            var (min, max) = m.ComputeBounds();
            Assert.True(Vector3.Distance(min, m.DeclaredBoundsMin!.Value) < 1e-3f, m.NodeName);
            Assert.True(Vector3.Distance(max, m.DeclaredBoundsMax!.Value) < 1e-3f, m.NodeName);
        });

        // Up axis: converted meshes stand on the floor — Y minimum >= 0 on most, X and Z on few.
        Assert.Equal(715, converted.Count(m => m.ComputeBounds().Min.Y >= -1e-3f));
        Assert.Equal(10, converted.Count(m => m.ComputeBounds().Min.X >= -1e-3f));
        Assert.Equal(82, converted.Count(m => m.ComputeBounds().Min.Z >= -1e-3f));
    }

    [Fact]
    public void SkinningIndexesThePayloadsOwnSkeletonAndWeightsSumToOne()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var root = RealAssetPaths.Classics.VanBuren();
        Assert.SkipWhen(root is null, "Van Buren prototype fixture missing");

        var files = DecodeAll(root).Where(d => d.File is not null).Select(d => d.File!).ToList();
        var skinned = files.Where(f => f.Meshes[0].Skinned && f.Meshes[0].Form == VanBurenB3dMeshForm.Converted)
            .ToList();
        Assert.Equal(511, skinned.Count);

        long weighted = 0, summingToOne = 0;
        foreach (var file in skinned)
        {
            var mesh = file.Meshes[0];
            Assert.True(file.Bones.Count > 0, file.Name);
            foreach (var influences in mesh.BoneWeights!)
            {
                Assert.All(influences, w => Assert.InRange(w.Bone, 0, file.Bones.Count - 1));
                if (influences.Length == 0)
                {
                    continue;
                }

                weighted++;
                if (Math.Abs(influences.Sum(w => w.Weight) - 1f) < 0.01f)
                {
                    summingToOne++;
                }
            }
        }

        Assert.Equal(186_811, weighted);
        Assert.Equal(weighted, summingToOne);

        // The source form carries its weights per vertex instead: 6 skinned source meshes, 8,718
        // weighted vertices, every one summing to 1 and indexing its own payload's skeleton.
        var skinnedSource = files.Where(f => f.Meshes[0].Skinned && f.Meshes[0].Form == VanBurenB3dMeshForm.Source)
            .ToList();
        Assert.Equal(6, skinnedSource.Count);
        long sourceWeighted = 0, sourceSummingToOne = 0;
        foreach (var file in skinnedSource)
        {
            foreach (var influences in file.Meshes[0].BoneWeights!)
            {
                Assert.All(influences, w => Assert.InRange(w.Bone, 0, file.Bones.Count - 1));
                if (influences.Length == 0)
                {
                    continue;
                }

                sourceWeighted++;
                if (Math.Abs(influences.Sum(w => w.Weight) - 1f) < 0.01f)
                {
                    sourceSummingToOne++;
                }
            }
        }

        Assert.Equal(8_718, sourceWeighted);
        Assert.Equal(sourceWeighted, sourceSummingToOne);

        // The skeleton reads as one: the bat's 27 bones open Root, Spine_1, Spine_2, Neck, Jaw.
        var bat = files.Single(f => f.Name == "Critters.grp/00000.B3D");
        Assert.Equal("CR_Bat", bat.MeshName);
        Assert.Equal(27, bat.Bones.Count);
        Assert.Equal(["Root", "Spine_1", "Spine_2", "Neck", "Jaw"], bat.Bones.Take(5).Select(b => b.Name));
    }

    [Fact]
    public void GroupsNameTgaTexturesThatNothingInTheBuildResolves()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var root = RealAssetPaths.Classics.VanBuren();
        Assert.SkipWhen(root is null, "Van Buren prototype fixture missing");

        var groups = DecodeAll(root).Where(d => d.File is not null).SelectMany(d => d.File!.Meshes[0].Groups).ToList();
        Assert.Equal(8393, groups.Count);
        var textured = groups.Where(g => g.Textures.Count > 0).ToList();
        Assert.Equal(7368, textured.Count);
        Assert.All(textured,
            g => Assert.All(g.Textures, t => Assert.EndsWith(".tga", t, StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(147, textured.SelectMany(g => g.Textures).Select(t => t.ToLowerInvariant()).Distinct().Count());

        // Material strings are the closed vocabulary the engine switches on
        // ("Unknown blend_state: %s" / "Unknown material: %s" in Gfx_G3D_Mesh::LoadMesh).
        var converted = groups.Where(g => !string.IsNullOrEmpty(g.Shader)).ToList();
        Assert.Contains("BASE_2X", converted.Select(g => g.Shader).Distinct());
        Assert.Subset(new HashSet<string>(["OPAQUE", "ALPHABLEND", "ADD", "ALPHATEST", ""]),
            converted.Select(g => g.BlendState).ToHashSet());
    }

    [Fact]
    public void TheFrameIsLeftHandedByDeclarationChiralityAndWinding()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var root = RealAssetPaths.Classics.VanBuren();
        Assert.SkipWhen(root is null, "Van Buren prototype fixture missing");

        var files = DecodeAll(root).Where(d => d.File is not null).Select(d => d.File!).ToList();

        // (1) Every scene declares it, in the string F3.exe's FUN_00529490 compares against.
        Assert.All(files, f => Assert.Equal("LEFT_HANDED", f.HeaderString));
        Assert.All(files, f => Assert.Equal(1f, f.HeaderFloatA));
        Assert.All(files, f => Assert.Equal(1024f, f.HeaderFloatB));

        // (2) Chirality: vertices weighted to "L ..." bones sit at -X of those on "R ..." bones on
        // every skinned mesh that has both sides, and every body whose head is >= 0.5 units from its
        // pelvis along Z faces +Z. A left at -X on a body facing +Z is left-handed; a right-handed
        // frame would put that left at +X.
        int sided = 0, leftAtMinusX = 0, facingRead = 0, facingPlusZ = 0;
        foreach (var file in files.Where(f =>
                     f.Meshes[0].Skinned && f.Meshes[0].Form == VanBurenB3dMeshForm.Converted))
        {
            var mesh = file.Meshes[0];
            List<float> lx = [], rx = [], headZ = [], rearZ = [];
            for (var v = 0; v < mesh.Vertices.Count; v++)
            {
                var influences = mesh.BoneWeights![v];
                if (influences.Length == 0)
                {
                    continue;
                }

                var bone = file.Bones[influences.MaxBy(w => w.Weight).Bone].Name.ToUpperInvariant();
                var p = mesh.Vertices[v].Position;
                if (bone.StartsWith("L ", StringComparison.Ordinal))
                {
                    lx.Add(p.X);
                }
                else if (bone.StartsWith("R ", StringComparison.Ordinal))
                {
                    rx.Add(p.X);
                }

                if (bone.Contains("HEAD", StringComparison.Ordinal) || bone.Contains("JAW", StringComparison.Ordinal)
                    || bone.Contains("NECK", StringComparison.Ordinal) || bone.Contains("SKULL", StringComparison.Ordinal))
                {
                    headZ.Add(p.Z);
                }

                if (bone.Contains("PELVIS", StringComparison.Ordinal) || bone.Contains("TAIL", StringComparison.Ordinal))
                {
                    rearZ.Add(p.Z);
                }
            }

            if (lx.Count > 0 && rx.Count > 0)
            {
                sided++;
                leftAtMinusX += lx.Average() < rx.Average() ? 1 : 0;
            }

            if (headZ.Count > 0 && rearZ.Count > 0 && Math.Abs(headZ.Average() - rearZ.Average()) >= 0.5f)
            {
                facingRead++;
                facingPlusZ += headZ.Average() > rearZ.Average() ? 1 : 0;
            }
        }

        Assert.Equal(135, sided);
        Assert.Equal(sided, leftAtMinusX);
        Assert.Equal(15, facingRead);
        Assert.Equal(facingRead, facingPlusZ);

        // (3) Winding is one-signed on the raw coordinates: cross(b-a, c-a) . n > 0 — clockwise from
        // outside in the left-handed frame, Direct3D's default front — on all but 1,526 of the
        // 816,372 triangles (96 degenerate), and the majority on every mesh.
        long positive = 0, negative = 0, degenerate = 0, majorityPositive = 0;
        foreach (var mesh in files.Select(f => f.Meshes[0]))
        {
            long meshPositive = 0, meshNegative = 0;
            foreach (var group in mesh.Groups)
            {
                for (var i = 0; i + 2 < group.Indices.Length; i += 3)
                {
                    var a = mesh.Vertices[group.Indices[i]];
                    var b = mesh.Vertices[group.Indices[i + 1]];
                    var c = mesh.Vertices[group.Indices[i + 2]];
                    var d = Vector3.Dot(
                        Vector3.Cross(b.Position - a.Position, c.Position - a.Position),
                        a.Normal + b.Normal + c.Normal);
                    if (d > 0)
                    {
                        meshPositive++;
                    }
                    else if (d < 0)
                    {
                        meshNegative++;
                    }
                    else
                    {
                        degenerate++;
                    }
                }
            }

            positive += meshPositive;
            negative += meshNegative;
            majorityPositive += meshPositive > meshNegative ? 1 : 0;
        }

        Assert.Equal(814_750, positive);
        Assert.Equal(1_526, negative);
        Assert.Equal(96, degenerate);
        Assert.Equal(3912, majorityPositive);
    }

    [Fact]
    public void GlbExportOfTheBatIsCoherentGeometry()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var root = RealAssetPaths.Classics.VanBuren();
        Assert.SkipWhen(root is null, "Van Buren prototype fixture missing");

        var bat = DecodeAll(root).Single(d => d.Archive == "Critters.grp" && d.Index == 0).File!;
        var glb = VanBurenB3DGlbExporter.WriteToBytes(bat);
        using var stream = new MemoryStream(glb);
        var model = ModelRoot.ReadGLB(stream);
        var mesh = Assert.Single(model.LogicalMeshes);
        Assert.Equal("CR_Bat", mesh.Name);
        Assert.Equal(5, mesh.Primitives.Count);
        Assert.Equal(426, mesh.Primitives.Sum(p => p.GetTriangleIndices().Count()));
        Assert.All(mesh.Primitives, p => Assert.Equal("CR_Bat_Default_LG.tga", p.Material.Name));
    }

    [Fact]
    public void GlbExportOfTheCougarMirrorsZAndWindsCounterClockwise()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var root = RealAssetPaths.Classics.VanBuren();
        Assert.SkipWhen(root is null, "Van Buren prototype fixture missing");

        // Raw (left-handed) cougar: 1,091 vertices, 662 triangles, nose at Z +3.0654, tail tip at
        // Z -4.0914, 654 triangles winding positive and 8 negative. The export negates Z and swaps
        // two indices per triangle, so the GLB must show Z in [-3.0654, +4.0914] with 654 positive
        // products — a pass-through export keeps the raw Z range, a mirror without the index swap
        // gives 8 positive / 654 negative, and an index swap alone gives the raw range with 8/654.
        var cougar = DecodeAll(root).Single(d => d.Archive == "Critters.grp" && d.Index == 1).File!;
        Assert.Equal("CR_Cougar", cougar.MeshName);
        Assert.Equal(28, cougar.Bones.Count);
        var raw = cougar.Meshes[0];
        Assert.Equal(1091, raw.Vertices.Count);
        Assert.Equal(662, raw.TriangleCount);
        var (rawMin, rawMax) = raw.ComputeBounds();
        Assert.Equal(-4.0914f, rawMin.Z, 3);
        Assert.Equal(3.0654f, rawMax.Z, 3);

        using var stream = new MemoryStream(VanBurenB3DGlbExporter.WriteToBytes(cougar));
        var model = ModelRoot.ReadGLB(stream);
        var primitive = Assert.Single(Assert.Single(model.LogicalMeshes).Primitives);
        var positions = primitive.GetVertexAccessor("POSITION").AsVector3Array();
        var normals = primitive.GetVertexAccessor("NORMAL").AsVector3Array();
        Assert.Equal(-3.0654f, positions.Min(p => p.Z), 3);
        Assert.Equal(4.0914f, positions.Max(p => p.Z), 3);
        Assert.Equal(0f, positions.Min(p => p.Y), 3);
        Assert.Equal(3.1964f, positions.Max(p => p.Y), 3);

        int positive = 0, negative = 0;
        foreach (var (a, b, c) in primitive.GetTriangleIndices())
        {
            var d = Vector3.Dot(Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]),
                normals[a] + normals[b] + normals[c]);
            positive += d > 0 ? 1 : 0;
            negative += d < 0 ? 1 : 0;
        }

        Assert.Equal(654, positive);
        Assert.Equal(8, negative);
    }

    private sealed record Decoded(string Archive, int Index, byte FirstOpcode, VanBurenB3DFile? File, string Error);
}