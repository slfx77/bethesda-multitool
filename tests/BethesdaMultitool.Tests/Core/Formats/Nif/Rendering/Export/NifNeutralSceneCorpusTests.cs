using System.Numerics;
using System.Text.Json;
using BethesdaMultitool.Core.Formats.Nif;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Export;
using BethesdaMultitool.Tests.Helpers;
using SharpGLTF.Schema2;
using Slfx77.Multitool.Core.Models;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Export;

/// <summary>
///     Runs the neutral NIF snapshot against real corpus files and compares it with the native writer's own GLB.
/// </summary>
/// <remarks>
///     <para>
///         Unit cases pin the adapter against its contracts. They cannot tell whether it agrees with the writer it
///         is meant to replace across real authored data, which is what this gate is for. It parses both the
///         neutral and the native GLB and compares the geometry that survived.
///     </para>
///     <para>
///         <c>ModelRoot.ParseGLB</c> appears here deliberately. The no-GLB-round-trip constraint applies to
///         production source, not to a test oracle — AweMultitool's equivalent corpus gate parses GLB for exactly
///         the same reason.
///     </para>
/// </remarks>
[Trait("Category", BucketBTestGuard.Category)]
[Collection(SequentialIntegrationGroup.Name)]
public sealed class NifNeutralSceneCorpusTests
{
    /// <summary>How many corpus files one run inspects. The corpus holds tens of thousands; this keeps the gate quick.</summary>
    private const int SampleSize = 120;

    private static string[] CorpusFiles()
    {
        var root = RealAssetPaths.SampleDirectory("Builds");
        if (root is null) return [];

        // Ordered so the selection is the same on every run: a gate that inspects a different set each
        // time cannot be compared with its own previous result.
        return Directory
            .EnumerateFiles(root, "*.nif", SearchOption.AllDirectories)
            .Where(static path => !path.Contains("Reference_Code", StringComparison.OrdinalIgnoreCase))
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .Take(SampleSize)
            .ToArray();
    }

    /// <summary>Compares retained geometry and exact source-node and skin correspondence against eligible corpus exports.</summary>
    [Fact]
    public void EveryAdaptedFileAgreesWithTheNativeWriterOnGeometry()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var files = CorpusFiles();
        Assert.SkipWhen(files.Length == 0, "No NIF corpus present.");

        using var resolver = new NifTextureResolver(static _ => null);
        var adapted = 0;
        var declined = 0;
        var compared = 0;
        var parseErrors = 0;
        var noParsedModel = 0;
        var noScene = 0;
        var legacyRepeatedPositionDrops = 0;
        var declineReasons = new SortedDictionary<string, int>(StringComparer.Ordinal);

        foreach (var path in files)
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();

            byte[] data;
            NifInfo? nif;
            try
            {
                data = File.ReadAllBytes(path);
                nif = NifParser.Parse(data);
            }
            catch (Exception error)
            {
                // A file this reader cannot parse at all is not this gate's subject.
                parseErrors++;
                TestContext.Current.TestOutputHelper?.WriteLine(
                    $"{path}: pre-adapter read/parse error {error.GetType().Name}: {error.Message}");
                continue;
            }

            if (nif is null)
            {
                noParsedModel++;
                TestContext.Current.TestOutputHelper?.WriteLine($"{path}: parser returned no model");
                continue;
            }

            var scene = NifExportSceneBuilder.Build(data, nif, Path.GetFileName(path));
            if (scene is null)
            {
                noScene++;
                TestContext.Current.TestOutputHelper?.WriteLine($"{path}: scene builder returned no renderable scene");
                continue;
            }

            if (!NifNeutralSceneAdapter.TryAdapt(scene, resolver, Path.GetFileNameWithoutExtension(path),
                    out var neutral, out var reason, TestContext.Current.CancellationToken))
            {
                declined++;
                declineReasons[reason!] = declineReasons.GetValueOrDefault(reason!) + 1;
                continue;
            }

            adapted++;
            var sourceTriangles = scene.MeshParts
                .Where(part => !GlbWriter.ShouldSkipStarfieldNoDrawSubmesh(part.Submesh, resolver))
                .Sum(static part => part.Submesh.TriangleCount);
            var repeatedPositions = NifCorpusLegacyTriangles.RepeatedPositions(scene, resolver);

            // The native writer is the thing being agreed with. Parsing its output is the only way to
            // compare what actually survived rather than what the builders intended.
            var nativeBytes = GlbWriter.WriteToBytes(scene, resolver);
            var native = ModelRoot.ParseGLB(nativeBytes);

            var neutralTriangles = neutral.Meshes
                .SelectMany(static mesh => mesh.Primitives)
                .Sum(static primitive => primitive.Indices.Count / 3);
            var nativeTriangles = native.LogicalMeshes
                .SelectMany(static mesh => mesh.Primitives)
                .Sum(static primitive => primitive.GetIndices().Count / 3);

            Assert.Equal(sourceTriangles, neutralTriangles);
            Assert.Equal(repeatedPositions, NifCorpusLegacyTriangles.RepeatedPositions(neutral));
            Assert.True(neutralTriangles - repeatedPositions == nativeTriangles,
                $"{path}: neutral kept {neutralTriangles} triangles, native kept {nativeTriangles}; " +
                $"source has {repeatedPositions} exact repeated-position triangles rejected by the legacy writer.");
            legacyRepeatedPositionDrops += repeatedPositions;
            if (repeatedPositions > 0)
                TestContext.Current.TestOutputHelper?.WriteLine($"{path}: legacy repeated-position drops {repeatedPositions}");

            AssertOriginalNodesAndSkinPlacements(scene, neutral, resolver);

            foreach (var mesh in neutral.Meshes)
            {
                foreach (var primitive in mesh.Primitives)
                {
                    Assert.True(primitive.Vertices.Count > 0, $"{path}: an adapted primitive has no vertices.");
                    foreach (var vertex in primitive.Vertices)
                    {
                        Assert.True(vertex.Normal.LengthSquared() > 0.9f,
                            $"{path}: a normal is not unit length, which the shared validator rejects.");
                    }
                }
            }

            compared++;
        }

        // A gate that silently inspects nothing is worse than no gate. Both halves must be non-trivial.
        Assert.True(adapted + declined > 0, "No corpus file reached the adapter at all.");
        Assert.True(compared > 0, "No corpus file was compared against the native writer.");

        TestContext.Current.TestOutputHelper?.WriteLine(
            $"inspected {files.Length}, adapted {adapted}, compared {compared}, declined {declined}, " +
            $"pre-adapter read/parse errors {parseErrors}, no parsed model {noParsedModel}, no renderable scene {noScene}, " +
            $"legacy repeated-position drops {legacyRepeatedPositionDrops}");
        foreach (var entry in declineReasons)
        {
            TestContext.Current.TestOutputHelper?.WriteLine($"  declined {entry.Value}x: {entry.Key}");
        }
    }

    /// <summary>Checks the exact retained source hierarchy and every additional independent skin occurrence.</summary>
    /// <param name="source">The assembled source scene also supplied to the unchanged native writer.</param>
    /// <param name="neutral">The actual adapted document.</param>
    /// <param name="resolver">The material resolver defining the writer's no-draw rule.</param>
    internal static void AssertOriginalNodesAndSkinPlacements(GlbScene source, ModelDocument neutral,
        NifTextureResolver resolver)
    {
        var skinParts = source.MeshParts.Select((part, ordinal) => (Part: part, Ordinal: ordinal))
            .Where(item => item.Part.Skin is not null && item.Part.Submesh.TriangleCount > 0 &&
                item.Part.Submesh.VertexCount > 0 &&
                !GlbWriter.ShouldSkipStarfieldNoDrawSubmesh(item.Part.Submesh, resolver)).ToArray();
        Assert.Equal(source.Nodes.Count + skinParts.Length, neutral.Nodes.Count);
        Assert.Equal(skinParts.Length, neutral.Skins.Count);
        var expectedChildren = new List<int>[source.Nodes.Count];
        for (var index = 0; index < expectedChildren.Length; index++) expectedChildren[index] = [];
        for (var index = 0; index < source.Nodes.Count; index++)
            if (source.Nodes[index].ParentIndex is { } parent) expectedChildren[parent].Add(index);
        expectedChildren[GlbScene.RootNodeIndex].AddRange(Enumerable.Range(source.Nodes.Count, skinParts.Length));
        for (var index = 0; index < source.Nodes.Count; index++)
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var original = source.Nodes[index];
            var retained = neutral.Nodes[index];
            Assert.Equal(original.Name, retained.Name);
            Assert.Equal(GltfCoordinateAdapter.ConvertMatrix(original.LocalTransform), retained.LocalTransform);
            Assert.Null(retained.SkinIndex);
            Assert.Equal(expectedChildren[index], retained.Children);
        }

        var generatedMeshes = new HashSet<int>();
        var generatedSkins = new HashSet<int>();
        for (var index = 0; index < skinParts.Length; index++)
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var (part, ordinal) = skinParts[index];
            var placement = neutral.Nodes[source.Nodes.Count + index];
            Assert.Equal(Matrix4x4.Identity, placement.LocalTransform);
            Assert.Empty(placement.Children);
            Assert.Equal(part.Name, placement.Name);
            Assert.NotNull(placement.MeshIndex);
            Assert.NotNull(placement.SkinIndex);
            Assert.True(generatedMeshes.Add(placement.MeshIndex.Value));
            Assert.True(generatedSkins.Add(placement.SkinIndex.Value));
            var primitive = Assert.Single(neutral.Meshes[placement.MeshIndex.Value].Primitives);
            Assert.NotNull(primitive.SkinInfluences);
            var retainedInfluences = primitive.SkinInfluences;
            for (var vertex = 0; vertex < part.Skin!.PerVertexInfluences.Length; vertex++)
            {
                var originalInfluences = part.Skin.PerVertexInfluences[vertex];
                Assert.True(retainedInfluences.InfluencesPerVertex >= originalInfluences.Length);
                for (var slot = 0; slot < retainedInfluences.InfluencesPerVertex; slot++)
                {
                    var offset = vertex * retainedInfluences.InfluencesPerVertex + slot;
                    Assert.Equal(slot < originalInfluences.Length ? originalInfluences[slot].BoneIdx : 0,
                        retainedInfluences.JointIndices[offset]);
                    Assert.Equal(slot < originalInfluences.Length ? originalInfluences[slot].Weight : 0f,
                        retainedInfluences.Weights[offset]);
                }
            }
            var skin = neutral.Skins[placement.SkinIndex.Value];
            Assert.Equal(part.Skin!.JointNodeIndices, skin.JointNodeIndices);
            Assert.Equal(part.Skin.InverseBindMatrices.Select(GltfCoordinateAdapter.ConvertMatrix), skin.InverseBindMatrices);
            Assert.Equal(GlbScene.RootNodeIndex, skin.SkeletonRootNodeIndex);
            using var metadata = JsonDocument.Parse(placement.ExtrasJson!);
            var provenance = metadata.RootElement.GetProperty("bethesdaNifSkinPlacement");
            Assert.Equal(ordinal, provenance.GetProperty("meshPartOrdinal").GetInt32());
            var sourceNode = provenance.GetProperty("sourceNodeIndex");
            Assert.Equal(part.NodeIndex, sourceNode.ValueKind == JsonValueKind.Null ? (int?)null : sourceNode.GetInt32());
            var sourceBlock = provenance.GetProperty("sourceBlockIndex");
            Assert.Equal(part.Submesh.SourceBlockIndex >= 0 ? (int?)part.Submesh.SourceBlockIndex : null,
                sourceBlock.ValueKind == JsonValueKind.Null ? (int?)null : sourceBlock.GetInt32());
        }
    }

    [Fact]
    public void DeclinesCarryAReasonAndNeverProduceAPartialDocument()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var files = CorpusFiles();
        Assert.SkipWhen(files.Length == 0, "No NIF corpus present.");

        using var resolver = new NifTextureResolver(static _ => null);
        var seen = 0;

        foreach (var path in files)
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            byte[] data;
            NifInfo? nif;
            try
            {
                data = File.ReadAllBytes(path);
                nif = NifParser.Parse(data);
            }
            catch (Exception)
            {
                continue;
            }

            if (nif is null) continue;

            var scene = NifExportSceneBuilder.Build(data, nif, Path.GetFileName(path));
            if (scene is null) continue;

            var carried = NifNeutralSceneAdapter.TryAdapt(scene, resolver, "fixture", out var neutral,
                out var reason, TestContext.Current.CancellationToken);
            seen++;

            if (carried)
            {
                Assert.NotNull(neutral);
                Assert.Null(reason);
            }
            else
            {
                // The whole point of the decline taxonomy is that nothing is dropped silently.
                Assert.Null(neutral);
                Assert.False(string.IsNullOrWhiteSpace(reason), $"{path}: declined without a reason.");
            }
        }

        Assert.True(seen > 0, "No corpus file reached the adapter at all.");
    }
}
