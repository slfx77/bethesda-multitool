using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using BethesdaMultitool.Core.Formats.Dds;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12;
using BethesdaMultitool.Core.Formats.Nif.Rendering.NpcAssembly;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Scene;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Viewer;
using BethesdaMultitool.Core.Semantic.LoadOrder;
using BethesdaMultitool.Core.WorldData;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.WorldData;

public sealed class WorldActorRenderingTests
{
    [Theory]
    [InlineData("ACHR", "NPC_", false)]
    [InlineData("ACHR", "NPC_", true)]
    [InlineData("ACRE", "CREA", false)]
    [InlineData("ACRE", "CREA", true)]
    public void SelectedActorUsesPhysicalWinnerAndPlacementTransform(string placementType, string baseType, bool bigEndian)
    {
        using var fixture = new ActorFixture(baseType, bigEndian);
        var catalog = fixture.Build();
        var placement = new PlacedReference
        {
            FormId = 0x02000900, BaseFormId = 0x02000800, RecordType = placementType,
            X = 100, Y = 200, Z = 300, RotZ = MathF.PI / 2, Scale = 2,
            IsInitiallyDisabled = true, ModelPath = @"characters\skeleton.nif"
        };
        var actor = Assert.Single(catalog.Actors);
        Assert.Equal(fixture.Path, actor.Source.FilePath);
        Assert.Equal(fixture.HeaderSize, actor.Source.Offset);
        Assert.Equal(0x01000800u, actor.Source.FileLocalFormId);
        Assert.Equal(0x02000800u, actor.BaseFormId);
        var cache = new WorldRenderCache { ActorCatalog = catalog };
        var cell = new CellRecord { FormId = 0x02001000, PlacedObjects = [placement] };
        var rendered = Assert.Single(cache.GetPlacementList(cell));
        Assert.Equal(actor.CacheKey, rendered.ModelPath);
        Assert.Equal(placement.FormId, rendered.FormId);
        Assert.Equal(placement.BaseFormId, rendered.BaseFormId);
        AssertClose(new Vector3(100, 198, 300), Vector3.Transform(Vector3.UnitX, rendered.WorldMatrix));
        Assert.True(rendered.IsInitiallyDisabled);
        Assert.True(placement.IsInitiallyDisabled);
        Assert.Null(RenderableReference.TryBuild(placement)); // A skeleton MODL alone never admits an actor.
        Assert.Null(catalog.Resolve(placement with { RecordType = "REFR" }));
        Assert.Null(RenderableReference.TryBuild(placement with { X = float.NaN }, actorCatalog: catalog));
        Assert.Null(RenderableReference.TryBuild(placement with { Scale = float.PositiveInfinity }, actorCatalog: catalog));
        var enabled = placement with { IsInitiallyDisabled = false };
        Assert.True(RenderableReference.TryBuild(enabled, xespDisabled: true, actorCatalog: catalog)!.Value.IsInitiallyDisabled);
        Assert.False(enabled.IsInitiallyDisabled);
    }

    [Theory]
    [InlineData("deleted")]
    [InlineData("ambiguous")]
    [InlineData("wrong-offset")]
    public void UnselectedActorCannotBorrowAnOlderAppearance(string scenario)
    {
        using var fixture = new ActorFixture("NPC_", false);
        var catalog = fixture.Build(scenario);
        Assert.Empty(catalog.Actors);
        Assert.Null(RenderableReference.TryBuild(new PlacedReference
        {
            FormId = 0x02000900, BaseFormId = 0x02000800, RecordType = "ACHR", ModelPath = "skeleton.nif"
        }, actorCatalog: catalog));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActorPoseBakesSkinOnceAndOwnsGeneratedPixels(bool creature)
    {
        var scene = new BethesdaViewerScene("selected appearance",
            creature ? BethesdaViewerScenePurpose.CreatureAppearance : BethesdaViewerScenePurpose.NpcAppearance,
            null, textureSourcePaths: ["edition-textures.bsa"]);
        var joint = scene.AddNode("joint", 0, Matrix4x4.Identity, Matrix4x4.CreateTranslation(5, 6, 7),
            BethesdaViewerNodeRole.Skeleton);
        scene.MeshParts.Add(new BethesdaViewerMeshPart
        {
            Name = "body", NodeIndex = joint,
            Submesh = new RenderableSubmesh
            {
                Positions = [0, 0, 0, 1, 0, 0, 0, 1, 0], Triangles = [0, 1, 2],
                DiffuseTexturePath = @"textures\generated\face.dds", SourceNifPath = @"meshes\body.nif"
            },
            Skin = new BethesdaViewerSkinBinding
            {
                JointNodeIndices = [joint], InverseBindMatrices = [Matrix4x4.Identity],
                PerVertexInfluences = [[(0, 1f)], [(0, 1f)], [(0, 1f)]]
            }
        });
        var pixels = new byte[] { 12, 34, 56, 255 };
        scene.AddGeneratedTexture(@"textures\generated\face.dds", new DecodedTexture
        {
            MipLevels = [new DecodedTextureMipLevel { Width = 1, Height = 1, Pixels = pixels }]
        });
        pixels[0] = 99;
        var definition = new WorldActorDefinition(creature ? "ACRE" : "ACHR", 0x800, "actor-key",
            new LoadOrderRecordVersion("FalloutNV.esm", "edition/FalloutNV.esm", 0x800, 0x800,
                creature ? "CREA" : "NPC_", "Actor", 0, 123));
        var source = new WorldActorScene(definition, scene, ["edition-meshes.bsa"]);
        var mesh = WorldActorMeshDecoder12.Decode(source);
        var part = Assert.Single(mesh.Submeshes);
        AssertClose(new Vector3(5, 6, 7), part.Vertices[0].Position);
        Assert.Null(part.Skin); // The world must not apply the skeleton a second time.
        Assert.Equal(12, Assert.Single(mesh.GeneratedTextures!).Value.Pixels[0]);
        Assert.Equal(definition.Source, mesh.ActorProvenance!.Actor.Source);
        Assert.Equal("edition-meshes.bsa", Assert.Single(mesh.ActorProvenance.MeshSources));
        Assert.Equal("edition-textures.bsa", Assert.Single(mesh.ActorProvenance.TextureSources));
        Assert.Equal(@"meshes\body.nif", Assert.Single(mesh.ActorProvenance.MeshPaths));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => WorldActorMeshDecoder12.Decode(source, cancelled.Token));
    }

    private static void AssertClose(Vector3 expected, Vector3 actual) =>
        Assert.InRange(Vector3.Distance(expected, actual), 0, 0.0001f);

    [Fact]
    public void CancelledActorRequestDoesNotOpenAssets()
    {
        using var fixture = new ActorFixture("NPC_", false);
        var catalog = fixture.Build();
        using var source = new WorldActorSceneSource(catalog, fixture.Path,
            ["missing-meshes.bsa"], ["missing-textures.bsa"]);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            source.Build(Assert.Single(catalog.Actors).CacheKey, cancelled.Token));
    }

    private sealed class ActorFixture : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bmt-world-actor-" + Guid.NewGuid().ToString("N"));
        private readonly string _baseType;
        private readonly PluginLoadOrder _order;
        internal string Path { get; }
        internal int HeaderSize { get; }

        internal ActorFixture(string baseType, bool bigEndian)
        {
            _baseType = baseType;
            Directory.CreateDirectory(_directory);
            var basePath = System.IO.Path.Combine(_directory, "FalloutNV.esm");
            var otherPath = System.IO.Path.Combine(_directory, "Other.esm");
            Path = System.IO.Path.Combine(_directory, "Dlc.esm");
            var hedr = new byte[12];
            if (bigEndian) BinaryPrimitives.WriteSingleBigEndian(hedr, 1.34f);
            else BinaryPrimitives.WriteSingleLittleEndian(hedr, 1.34f);
            var header = bigEndian ? EsmTestRecordBuilder.BuildMinimalRecordBE("TES4", 0, "HEDR", hedr)
                : EsmTestRecordBuilder.BuildMinimalRecordLE("TES4", 0, "HEDR", hedr);
            var actor = bigEndian
                ? EsmTestRecordBuilder.BuildMinimalRecordBE(baseType, 0x01000800, "EDID", Encoding.ASCII.GetBytes("SelectedActor\0"))
                : EsmTestRecordBuilder.BuildMinimalRecordLE(baseType, 0x01000800, "EDID", Encoding.ASCII.GetBytes("SelectedActor\0"));
            HeaderSize = header.Length;
            File.WriteAllBytes(basePath, header);
            File.WriteAllBytes(otherPath, header);
            File.WriteAllBytes(Path, [..header, ..actor]);
            _order = PluginLoadOrder.Create([basePath, otherPath, Path], false,
                path => System.IO.Path.GetFileName(path) == "Dlc.esm" ? ["FalloutNV.esm"] : []);
        }

        internal WorldActorCatalog Build(string scenario = "selected")
        {
            var version = new LoadOrderRecordVersion("Dlc.esm", Path, 0x01000800, 0x02000800,
                _baseType, "SelectedActor", scenario == "deleted" ? 0x20u : 0,
                scenario == "wrong-offset" ? 500 : HeaderSize);
            var index = LoadOrderRecordIndex.Create(_order, scenario == "ambiguous"
                ? [version, version with { Offset = 500 }] : [version]);
            return new WorldActorCatalog(NpcAppearanceResolver.Build(_order, index, TestContext.Current.CancellationToken));
        }

        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }
}
