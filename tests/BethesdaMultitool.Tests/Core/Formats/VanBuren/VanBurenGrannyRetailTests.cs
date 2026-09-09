using System.Globalization;
using System.Numerics;
using System.Text;
using BethesdaMultitool.Core.Formats.Granny;
using BethesdaMultitool.Core.Formats.VanBuren;
using BethesdaMultitool.Tests.Helpers;
using SharpGLTF.Schema2;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.VanBuren;

/// <summary>
///     Opt-in (<c>RUN_BUCKET_B=1</c>) checks over every Granny 2 payload of the Van Buren
///     prototype. The pins are the population measured 2026-09-08 with an independent Python walk
///     of the archives (headers, section tables, tiling, compression kinds) and with the reader
///     itself for what only expansion can show (root objects, meshes, skeletons, animations).
///     <para>
///         ⚠ The pins are what make the test able to fail: a reader that silently dropped a
///         section, mis-expanded Oodle0 (every one of the 2,735 Oodle0 sections must expand to
///         its declared size AND yield a walkable type tree), or mis-read a vertex layout would
///         change a count here.
///     </para>
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", BucketBTestGuard.Category)]
public sealed class VanBurenGrannyRetailTests
{
    /// <summary>Payloads opening with the Granny magic, by archive (Python survey: 437 + 91 + 18 + 1).</summary>
    private const int TotalPayloads = 547;

    private const int Oodle0Sections = 2_735;

    private const int RawSections = 547;

    private const int ModelFiles = 25;

    /// <summary>
    ///     Files whose root holds a skeleton or model but no mesh. ⚠ Three of them ALSO carry a clip
    ///     (Critters 00117 <c>CR_FloatingEyeBot_4ani</c>, Critters 00208 <c>Male_Collision</c>, Props
    ///     00009 <c>DS_Vault_Main</c>), which is why there are 451 clips over 448 animation-only files.
    /// </summary>
    private const int SkeletonFiles = 74;

    private const int AnimationFiles = 448;

    private const int Clips = 451;

    private static readonly string[] GrannyArchives = ["Critters.grp", "Items.grp", "Props.grp", "Engine.grp"];

    private static string RequireRoot()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var root = RealAssetPaths.Classics.VanBuren();
        Assert.SkipWhen(root is null, RealAssetPaths.SkipMessage("the Van Buren prototype"));
        return root;
    }

    private static IEnumerable<(VanBurenGrannyCatalog Catalog, VanBurenGrpEntry Entry, byte[] Payload)>
        Payloads(string root)
    {
        foreach (var archiveName in GrannyArchives)
        {
            var path = Path.Combine(root, "data", archiveName);
            var bytes = File.ReadAllBytes(path);
            var archive = VanBurenGrpArchive.Parse(bytes, archiveName);
            var catalog = VanBurenGrannyCatalog.Open(path);
            foreach (var entry in catalog.GrannyEntries)
            {
                yield return (catalog, entry, VanBurenGrpArchive.Read(bytes, entry));
            }

            Assert.Equal(
                archive.Entries.Count(e =>
                    e.Size > 0 && VanBurenGrannyFile.IsGranny(bytes.AsSpan((int)e.Offset, Math.Min(e.Size, 16)))),
                catalog.GrannyEntries.Count);
        }
    }

    [Fact]
    public void EveryPayloadParsesExpandsAndWalksToARootObject()
    {
        var root = RequireRoot();
        var payloads = 0;
        var compression = new Dictionary<Gr2Compression, int>();
        var headerProfiles = new HashSet<(int, uint, uint, int)>();
        var rootShapes = new Dictionary<string, int>();
        foreach (var (_, entry, payload) in Payloads(root))
        {
            payloads++;
            var container = Gr2Container.Parse(payload, entry.Name);
            headerProfiles.Add((container.HeaderSize, container.HeaderFormat, container.Version,
                container.Sections.Count));
            foreach (var section in container.Sections)
            {
                compression[section.Compression] = compression.GetValueOrDefault(section.Compression) + 1;
            }

            // Expand enforces expanded == declared for every section, and the tree's constructor
            // walks the root type and root object; either failing throws here.
            var expanded = Gr2ExpandedFile.Expand(container);
            var tree = Gr2TypeTree.Parse(expanded);
            var shape = string.Join(",", tree.RootType.Members.Select(member => member.Name));
            rootShapes[shape] = rootShapes.GetValueOrDefault(shape) + 1;
        }

        Assert.Equal(TotalPayloads, payloads);
        Assert.Equal([(352, 0u, 6u, 6)], headerProfiles);
        Assert.Equal(Oodle0Sections, compression.GetValueOrDefault(Gr2Compression.Oodle0));
        Assert.Equal(RawSections, compression.GetValueOrDefault(Gr2Compression.None));
        Assert.Equal(0, compression.GetValueOrDefault(Gr2Compression.Oodle1));

        // 546 roots carry ExporterInfo; exactly one (a granny_file_info without it) does not.
        const string full =
            "_GrannyFileStringTable,ArtToolInfo,ExporterInfo,FromFileName,Textures,Materials,Skeletons,VertexDatas,TriTopologies,Meshes,Models,TrackGroups,Animations";
        const string noExporter =
            "_GrannyFileStringTable,ArtToolInfo,FromFileName,Textures,Materials,Skeletons,VertexDatas,TriTopologies,Meshes,Models,TrackGroups,Animations";
        Assert.Equal(546, rootShapes.GetValueOrDefault(full));
        Assert.Equal(1, rootShapes.GetValueOrDefault(noExporter));
    }

    [Fact]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "S1244",
        Justification = "Counts exact zero normals and the known 0xCCCCCCCC debug-fill floating-point sentinel.")]
    public void ThePopulationIsAnimationsSkeletonsAndTwentyFiveMeshes()
    {
        var root = RequireRoot();
        var kinds = new Dictionary<VanBurenGrannyKind, int>();
        var layouts = new Dictionary<string, int>();
        var meshNames = new List<string>();
        var unwritten = new List<string>();
        var textures = 0;
        var materials = 0;
        var bones = 0;
        var consistentInverses = 0;
        var indexBits = new Dictionary<bool, int>();
        var normalsOffUnit = 0;
        var normalsZeroOnUnreferenced = 0;
        var normalsChecked = 0;
        var uvOutside = 0;
        var uvStackFill = 0;
        var uvChecked = 0;
        var deathclawUvOutside = -1;
        var weightSumsOff = 0;
        var weightsChecked = 0;
        var degrees = new Dictionary<int, int>();
        var dimensions = new Dictionary<int, int>();
        var clips = 0;
        var tracks = 0;
        var bases = new HashSet<string>();
        Gr2Mesh? deathclaw = null;

        foreach (var (catalog, entry, _) in Payloads(root))
        {
            var decoded = catalog.Decode(entry.Index);
            kinds[decoded.Kind] = kinds.GetValueOrDefault(decoded.Kind) + 1;
            var file = decoded.File;
            textures += file.TextureFileNames.Count;
            materials += file.MaterialNames.Count;
            if (file.ArtToolInfo is { } tool)
            {
                bases.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{tool.ToolName} {tool.MajorRevision}.{tool.MinorRevision} R{tool.Right} U{tool.Up} B{tool.Back} {tool.UnitsPerMeter}/m"));
            }

            foreach (var skeleton in file.Skeletons)
            {
                bones += skeleton.Bones.Count;
                consistentInverses += skeleton.CountConsistentInverses();
            }

            foreach (var mesh in file.Meshes)
            {
                meshNames.Add(mesh.Name);
                layouts[mesh.VertexLayout + $" size={mesh.VertexStride}"] =
                    layouts.GetValueOrDefault(mesh.VertexLayout + $" size={mesh.VertexStride}") + 1;
                indexBits[mesh.SixteenBitIndices] = indexBits.GetValueOrDefault(mesh.SixteenBitIndices) + 1;
                if (mesh.TopologyDefect is not null)
                {
                    unwritten.Add($"{entry.Name}:{mesh.Name}");
                    Assert.Empty(mesh.Indices);
                    Assert.Empty(mesh.Groups);
                    Assert.Equal(52, mesh.Positions.Length);
                }
                else
                {
                    Assert.True(mesh.TriangleCount > 0, $"{mesh.Name} has a written topology with no triangles");
                    Assert.Equal(mesh.TriangleCount, mesh.Groups.Sum(group => group.TriangleCount));
                }

                if (mesh.Name == "deathclaw")
                {
                    deathclaw = mesh;
                }

                var referenced = new bool[mesh.Positions.Length];
                foreach (var index in mesh.Indices)
                {
                    referenced[index] = true;
                }

                if (mesh.Normals is not null)
                {
                    for (var vertex = 0; vertex < mesh.Normals.Length; vertex++)
                    {
                        normalsChecked++;
                        var length = mesh.Normals[vertex].Length();
                        if (MathF.Abs(length - 1f) > 0.01f)
                        {
                            normalsOffUnit++;
                            if (length == 0f && !referenced[vertex])
                            {
                                normalsZeroOnUnreferenced++;
                            }
                        }
                    }
                }

                if (mesh.TextureCoordinates is not null)
                {
                    var outside = 0;
                    foreach (var uv in mesh.TextureCoordinates)
                    {
                        uvChecked++;
                        if (uv.X < 0 || uv.X > 1 || uv.Y < 0 || uv.Y > 1)
                        {
                            outside++;
                            // 0xCCCCCCCC read as a float is -107374176; the exporter's V flip leaves +107374176.
                            if (MathF.Abs(uv.X) == 107374176f && MathF.Abs(uv.Y) == 107374176f)
                            {
                                uvStackFill++;
                            }
                        }
                    }

                    uvOutside += outside;
                    if (mesh.Name == "deathclaw")
                    {
                        deathclawUvOutside = outside;
                    }
                }

                if (mesh.Influences is not null)
                {
                    foreach (var lanes in mesh.Influences)
                    {
                        weightsChecked++;
                        var sum = lanes.Sum(lane => lane.Weight);
                        if (MathF.Abs(sum - 1f) > 2f / 255f)
                        {
                            weightSumsOff++;
                        }
                    }
                }
            }

            foreach (var clip in file.Animations)
            {
                clips++;
                foreach (var track in clip.TrackGroups.SelectMany(group => group.TransformTracks))
                {
                    tracks++;
                    foreach (var curve in new[] { track.Position, track.Orientation, track.ScaleShear }.Where(curve =>
                                 !curve.IsEmpty))
                    {
                        degrees[curve.Degree] = degrees.GetValueOrDefault(curve.Degree) + 1;
                        dimensions[curve.Dimension] = dimensions.GetValueOrDefault(curve.Dimension) + 1;
                        Assert.All(curve.Knots, knot => Assert.True(float.IsFinite(knot)));
                        Assert.All(curve.Controls, control => Assert.True(float.IsFinite(control)));
                    }
                }
            }
        }

        var report = new StringBuilder();
        report.AppendLine(CultureInfo.InvariantCulture,
            $"kinds: {string.Join(", ", kinds.Select(kv => $"{kv.Key}={kv.Value}"))}");
        report.AppendLine(CultureInfo.InvariantCulture,
            $"textures={textures} materials={materials} bones={bones} consistentInverses={consistentInverses}");
        report.AppendLine(CultureInfo.InvariantCulture,
            $"layouts: {string.Join(" | ", layouts.Select(kv => $"{kv.Value}x {kv.Key}"))}");
        report.AppendLine(CultureInfo.InvariantCulture, $"meshes: {string.Join(", ", meshNames)}");
        report.AppendLine(CultureInfo.InvariantCulture, $"unwritten: {string.Join(", ", unwritten)}");
        report.AppendLine(CultureInfo.InvariantCulture,
            $"indexBits: 16={indexBits.GetValueOrDefault(true)} 32={indexBits.GetValueOrDefault(false)}");
        report.AppendLine(CultureInfo.InvariantCulture,
            $"normals off-unit {normalsOffUnit}/{normalsChecked} (zero on unreferenced {normalsZeroOnUnreferenced}); uv outside {uvOutside}/{uvChecked} (stack fill {uvStackFill}, deathclaw {deathclawUvOutside}); weight sums off {weightSumsOff}/{weightsChecked}");
        report.AppendLine(CultureInfo.InvariantCulture,
            $"clips={clips} tracks={tracks} degrees: {string.Join(", ", degrees.Select(kv => $"{kv.Key}={kv.Value}"))} dimensions: {string.Join(", ", dimensions.Select(kv => $"{kv.Key}={kv.Value}"))}");
        report.AppendLine(CultureInfo.InvariantCulture, $"bases: {string.Join(" | ", bases)}");
        report.AppendLine(CultureInfo.InvariantCulture,
            $"deathclaw: {deathclaw?.Positions.Length} verts {deathclaw?.TriangleCount} tris {deathclaw?.BoneBindings.Count} bone bindings");
        if (Environment.GetEnvironmentVariable("VB_GRANNY_REPORT") is { Length: > 0 } reportPath)
        {
            File.WriteAllText(reportPath, report.ToString());
        }

        Assert.Equal(ModelFiles, kinds.GetValueOrDefault(VanBurenGrannyKind.Model));
        Assert.Equal(SkeletonFiles, kinds.GetValueOrDefault(VanBurenGrannyKind.Skeleton));
        Assert.Equal(AnimationFiles, kinds.GetValueOrDefault(VanBurenGrannyKind.Animation));
        Assert.Equal(0, kinds.GetValueOrDefault(VanBurenGrannyKind.Empty));
        Assert.Equal(0, textures);
        Assert.Equal(0, materials);
        Assert.Equal(ModelFiles, meshNames.Count);
        Assert.Equal(Clips, clips);
        Assert.Equal(12_331, tracks);

        // Every curve is degree 0 (SDK 2.2 B-spline form); 3 values per knot on position curves,
        // 4 on orientation, 9 on the 21 scale/shear curves that are not empty.
        Assert.Equal(new[] { 0 }, degrees.Keys.ToArray());
        Assert.Equal(23_654, degrees[0]);
        Assert.Equal(11_866, dimensions.GetValueOrDefault(3));
        Assert.Equal(11_767, dimensions.GetValueOrDefault(4));
        Assert.Equal(21, dimensions.GetValueOrDefault(9));

        // LightWave, left-handed: Right +X, Up +Y, Back -Z (one file says 7.0, the rest 7.5).
        Assert.Equal(
            new[]
            {
                "LightWave 7.0 R<1, 0, 0> U<0, 1, 0> B<0, 0, -1> 1/m",
                "LightWave 7.5 R<1, 0, 0> U<0, 1, 0> B<0, 0, -1> 1/m"
            }, bases.Order(StringComparer.Ordinal).ToArray());

        // Bones: the stored InverseWorldTransform is the inverse of the composed parent chain on
        // 1,166 of 1,176 (a parent-major composition agrees on only 286, so the order is settled).
        // The 10 that disagree: the 8 CR_FloatingEyeBot_4ani bones (every one 1.2% off, the same
        // amount) and Items 00022's one-bone Light and Camera skeletons.
        Assert.Equal(1_176, bones);
        Assert.Equal(1_166, consistentInverses);

        // Geometry: every off-unit normal is a ZERO vector on a vertex no triangle references
        // (the door boxes reference nothing, so their 24 zero normals count too); UVs are only
        // real on deathclaw — the nine CR_*_Coll hulls carry the 0xCCCCCCCC stack fill
        // (±107374176) on every referenced vertex; bone weights sum to 1 on every skinned vertex.
        Assert.Equal(8_694, normalsChecked);
        Assert.Equal(740, normalsOffUnit);
        Assert.Equal(normalsOffUnit, normalsZeroOnUnreferenced);
        Assert.Equal(5_889, uvChecked);
        Assert.Equal(4_122, uvOutside);
        Assert.Equal(uvOutside, uvStackFill);
        Assert.Equal(0, deathclawUvOutside);
        Assert.Equal(8_642, weightsChecked);
        Assert.Equal(0, weightSumsOff);

        // Three vertex layouts: 4-weight with UVs (40 B), 2-weight without UVs (28 B), plain PN (24 B).
        Assert.Equal(10,
            layouts.GetValueOrDefault(
                "Position:Real32[3]@0,BoneWeights:NormalUInt8[4]@12,BoneIndices:UInt8[4]@16,Normal:Real32[3]@20,TextureCoordinates0:Real32[2]@32 size=40"));
        Assert.Equal(14,
            layouts.GetValueOrDefault(
                "Position:Real32[3]@0,BoneWeights:NormalUInt8[2]@12,BoneIndices:UInt8[2]@14,Normal:Real32[3]@16 size=28"));
        Assert.Equal(1, layouts.GetValueOrDefault("Position:Real32[3]@0,Normal:Real32[3]@12 size=24"));

        Assert.NotNull(deathclaw);
        Assert.Equal(1_420, deathclaw.Positions.Length);
        Assert.Equal(1_448, deathclaw.TriangleCount);
        Assert.Equal(30, deathclaw.BoneBindings.Count);
        Assert.Equal(18, meshNames.Count(name => name.Contains("Coll", StringComparison.OrdinalIgnoreCase)));

        // ⚑ Six Props.grp door boxes have a topology the exporter never wrote: every index word and
        // every Groups word is the MSVC debug-heap fill 0xBAADF00D (0xF00D per 16-bit index).
        // Their 52 vertices are real. All five 16-bit-index meshes are among them.
        Assert.Equal(
        [
            "00254.G:DS_Vault_Elevator:ElDoorsBox", "00301.G:DS_City1_Doors:B1Doorbox",
            "00302.G:DS_City1_Doors:B2Doorbox", "00303.G:DS_City1_Doors:B3Doorbox",
            "00304.G:DS_City1_Doors:B4Doorbox", "00315.G:DS_Junktown1_Doors:Layer2"
        ], unwritten);
        Assert.Equal(5, indexBits.GetValueOrDefault(true));
        Assert.Equal(20, indexBits.GetValueOrDefault(false));
    }

    [Fact]
    public void TheDeathclawAndABatHullExportToGlbWithTheirClips()
    {
        var root = RequireRoot();
        var catalog = VanBurenGrannyCatalog.Open(Path.Combine(root, "data", "Critters.grp"));
        var deathclaw = catalog.Find("deathclaw");
        Assert.NotNull(deathclaw);
        Assert.Equal(VanBurenGrannyKind.Model, deathclaw.Kind);
        var clips = catalog.AnimationsFor(deathclaw.File.Skeletons[0].Name);
        var glb = Gr2ModelGlbExporter.WriteToBytes(deathclaw.File, clips);
        Assert.True(glb.Length > 100_000, $"deathclaw GLB is only {glb.Length} bytes");
        Assert.Equal("glTF", Encoding.ASCII.GetString(glb, 0, 4));

        // 12 deathclaw clips bind: 30 animated bones x (translation + rotation) = 60 channels each.
        var model = ModelRoot.ParseGLB(glb);
        Assert.Equal(12, model.LogicalAnimations.Count);
        Assert.All(model.LogicalAnimations, clip => Assert.Equal(60, clip.Channels.Count));
        Assert.Equal(31, Assert.Single(model.LogicalSkins).JointsCount);
        Assert.Equal((1_448, 0), WindingAgreement(model));

        var bat = catalog.Find("CR_Bat_Coll");
        Assert.NotNull(bat);
        var batGlb = Gr2ModelGlbExporter.WriteToBytes(bat.File);
        Assert.True(batGlb.Length > 10_000, $"CR_Bat_Coll GLB is only {batGlb.Length} bytes");
        Assert.Equal((108, 0), WindingAgreement(ModelRoot.ParseGLB(batGlb)));
    }

    /// <summary>
    ///     ⚑ The winding oracle for the basis conversion: after the left-handed LightWave basis
    ///     (Back -Z) is applied and the winding reversed, every exported triangle's
    ///     <c>(B-A)×(C-A)</c> points the way its vertex normals do. Measured 2026-09-08 on the
    ///     deathclaw: 1,448 agree, 0 disagree. A reader that skipped the reversal (or reversed
    ///     unconditionally on a right-handed file) would score 0 agree / 1,448 disagree.
    /// </summary>
    private static (int Agree, int Disagree) WindingAgreement(ModelRoot model)
    {
        var agree = 0;
        var disagree = 0;
        foreach (var primitive in model.LogicalMeshes.SelectMany(mesh => mesh.Primitives))
        {
            var positions = primitive.GetVertexAccessor("POSITION").AsVector3Array();
            var normals = primitive.GetVertexAccessor("NORMAL").AsVector3Array();
            foreach (var (a, b, c) in primitive.GetTriangleIndices())
            {
                var winding = Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]);
                var dot = Vector3.Dot(winding, normals[a] + normals[b] + normals[c]);
                if (dot > 0)
                {
                    agree++;
                }
                else if (dot < 0)
                {
                    disagree++;
                }
            }
        }

        return (agree, disagree);
    }
}