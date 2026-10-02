using System.Numerics;
using System.Text.Json;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Export;
using SharpGLTF.Schema2;
using Slfx77.Multitool.Core.Models;
using Slfx77.Multitool.Media.Models;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Export;

/// <summary>Checks production-shaped skin occurrences against independent poses and the unchanged legacy GLB writer.</summary>
public sealed class NifNeutralSkinPlacementTests
{
    /// <summary>A null owning node, as emitted by NifExportSceneBuilder.Build, receives its own placement and converted bind pose.</summary>
    [Fact]
    public void AnonymousSkinPreservesSourceHierarchyAndNativeWorldPlacement()
    {
        var source = new GlbScene();
        var joint = AddNode(source, "joint", Matrix4x4.CreateTranslation(0, 0, 5));
        var binding = Skin([joint], [Matrix4x4.CreateTranslation(0, 0, -2)]);
        var part = Part(null, binding);
        source.MeshParts.Add(part);
        var originalNodes = source.Nodes.ToArray();
        var document = Adapt(source);
        Assert.Equal(originalNodes.Length + 1, document.Nodes.Count);
        var placement = document.Nodes[^1];
        Assert.Equal(Matrix4x4.Identity, placement.LocalTransform);
        Assert.Equal(0, Ordinal(placement));
        Assert.Null(SourceNode(placement));
        Assert.Equal(17, SourceBlock(placement));
        Assert.Contains(document.Nodes.Count - 1, document.Nodes[0].Children);
        Assert.All(document.Nodes.Take(originalNodes.Length), node => Assert.Null(node.MeshIndex));
        for (var index = 0; index < originalNodes.Length; index++)
        {
            Assert.Same(originalNodes[index], source.Nodes[index]);
            Assert.Equal(originalNodes[index].Name, document.Nodes[index].Name);
            Assert.Equal(GltfCoordinateAdapter.ConvertMatrix(originalNodes[index].LocalTransform),
                document.Nodes[index].LocalTransform);
        }
        Assert.Same(part, Assert.Single(source.MeshParts));
        Assert.Null(part.NodeIndex);
        Assert.Equal(new[] { joint }, binding.JointNodeIndices);
        Assert.Equal(Matrix4x4.CreateTranslation(0, 0, -2), binding.InverseBindMatrices[0]);
        AssertNear(new Vector3(0, 3, 0), WorldPosition(document, document.Nodes.Count - 1));
        using var resolver = new NifTextureResolver(_ => null);
        var native = ModelRoot.ParseGLB(GlbWriter.WriteToBytes(source, resolver));
        AssertNear(new Vector3(0, 3, 0), EncodedSingleJointPosition(native));
        AssertNear(new Vector3(0, 3, 0), EncodedSingleJointPosition(Shared(document)));
    }

    /// <summary>Noncommuting hierarchical transforms retain row-vector bind, child and parent order across the basis.</summary>
    [Fact]
    public void HierarchicalSkinPreservesNoncommutingTransformOrder()
    {
        var source = new GlbScene();
        var parentTransform = Matrix4x4.CreateRotationZ(MathF.PI / 2f) * Matrix4x4.CreateTranslation(10, 20, 30);
        var parent = source.AddNode("same", GlbScene.RootNodeIndex, parentTransform, parentTransform,
            GlbNodeKind.Skeleton, "same");
        var childLocal = Matrix4x4.CreateScale(2, 3, 4) * Matrix4x4.CreateRotationX(MathF.PI / 2f) *
            Matrix4x4.CreateTranslation(1, 2, 3);
        var childWorld = childLocal * parentTransform;
        var joint = source.AddNode("same", parent, childLocal, childWorld, GlbNodeKind.Skeleton, "same");
        var part = Part(null, Skin([joint], [Matrix4x4.CreateTranslation(0, -1, 0)]));
        source.MeshParts.Add(part);
        var originalNodes = source.Nodes.ToArray();
        var document = Adapt(source);
        Assert.Equal(originalNodes.Length + 1, document.Nodes.Count);
        for (var index = 0; index < originalNodes.Length; index++)
        {
            Assert.Same(originalNodes[index], source.Nodes[index]);
            Assert.Equal(originalNodes[index].Name, document.Nodes[index].Name);
            Assert.Equal(GltfCoordinateAdapter.ConvertMatrix(originalNodes[index].LocalTransform),
                document.Nodes[index].LocalTransform);
        }
        Assert.Equal(parent, source.Nodes[joint].ParentIndex);
        Assert.Equal(childWorld, source.Nodes[joint].WorldTransform);
        Assert.Equal(new[] { parent, document.Nodes.Count - 1 }, document.Nodes[GlbScene.RootNodeIndex].Children);
        Assert.Equal(new[] { joint }, document.Nodes[parent].Children);
        Assert.Empty(document.Nodes[joint].Children);
        Assert.Equal(Matrix4x4.Identity, document.Nodes[^1].LocalTransform);
        Assert.Same(part, Assert.Single(source.MeshParts));
        Assert.Null(part.NodeIndex);

        // Vertex zero: bind (0,-1,0), scale (0,-3,0), Rx (0,0,-3), child translation (1,2,0),
        // parent Rz (-2,1,0), parent translation (8,21,30), then Z-up to Y-up (8,30,-21).
        var expected = new Vector3(8, 30, -21);
        AssertNear(expected, WorldPosition(document, document.Nodes.Count - 1));
        using var resolver = new NifTextureResolver(_ => null);
        var native = ModelRoot.ParseGLB(GlbWriter.WriteToBytes(source, resolver));
        AssertNear(expected, EncodedSingleJointPosition(native));
        Assert.Equal(2, native.LogicalNodes.Count(node => node.Name == "same"));
        Assert.Equal("same", source.Nodes[parent].Name);
        Assert.Equal("same", source.Nodes[joint].Name);
        AssertNear(expected, EncodedSingleJointPosition(Shared(document)));
    }

    /// <summary>An optional source node is provenance for skins, not a second world transform.</summary>
    [Fact]
    public void ExplicitSkinOwnerTransformIsPreservedWithoutDoubleApplyingIt()
    {
        var source = new GlbScene();
        var ownerTransform = Matrix4x4.CreateScale(3) * Matrix4x4.CreateTranslation(100, 200, 300);
        var owner = AddNode(source, "same", ownerTransform);
        var joint = AddNode(source, "joint", Matrix4x4.CreateTranslation(10, 0, 0));
        source.MeshParts.Add(Part(owner, Skin([joint], [Matrix4x4.CreateTranslation(-4, 0, 0)])));
        var document = Adapt(source);
        Assert.Equal(GltfCoordinateAdapter.ConvertMatrix(ownerTransform), document.Nodes[owner].LocalTransform);
        Assert.Null(document.Nodes[owner].MeshIndex);
        Assert.Null(document.Nodes[owner].SkinIndex);
        Assert.Equal(owner, SourceNode(document.Nodes[^1]));
        AssertNear(new Vector3(6, 0, 0), WorldPosition(document, document.Nodes.Count - 1));
        using var resolver = new NifTextureResolver(_ => null);
        AssertNear(new Vector3(6, 0, 0), EncodedSingleJointPosition(ModelRoot.ParseGLB(GlbWriter.WriteToBytes(source, resolver))));
        AssertNear(new Vector3(6, 0, 0), EncodedSingleJointPosition(Shared(document)));
    }

    /// <summary>Equal-sized palettes on a shared source node cannot alias different actual joints.</summary>
    [Fact]
    public void IncompatiblePalettesOnOneNodeKeepIndependentInstances()
    {
        var source = new GlbScene();
        var owner = AddNode(source, "same", Matrix4x4.Identity);
        var firstJoint = AddNode(source, "same", Matrix4x4.CreateTranslation(10, 0, 0));
        var secondJoint = AddNode(source, "same", Matrix4x4.CreateTranslation(20, 0, 0));
        source.MeshParts.Add(Part(owner, Skin([firstJoint], [Matrix4x4.Identity])));
        source.MeshParts.Add(Part(owner, Skin([secondJoint], [Matrix4x4.Identity])));
        var document = Adapt(source);
        Assert.Equal(2, document.Skins.Count);
        Assert.Equal(2, document.Meshes.Count);
        Assert.Equal(new[] { firstJoint }, document.Skins[0].JointNodeIndices);
        Assert.Equal(new[] { secondJoint }, document.Skins[1].JointNodeIndices);
        Assert.Equal("same", document.Nodes[^2].Name);
        Assert.Equal("same", document.Nodes[^1].Name);
        Assert.Equal(0, Ordinal(document.Nodes[^2]));
        Assert.Equal(1, Ordinal(document.Nodes[^1]));
        Assert.NotEqual(document.Nodes[^2].SkinIndex, document.Nodes[^1].SkinIndex);
        AssertNear(new Vector3(10, 0, 0), WorldPosition(document, document.Nodes.Count - 2));
        AssertNear(new Vector3(20, 0, 0), WorldPosition(document, document.Nodes.Count - 1));
        var encoded = Shared(document);
        Assert.Equal(2, encoded.LogicalSkins.Count);
        Assert.Equal(firstJoint, encoded.LogicalSkins[0].GetJoint(0).Joint.LogicalIndex);
        Assert.Equal(secondJoint, encoded.LogicalSkins[1].GetJoint(0).Joint.LogicalIndex);
    }

    /// <summary>Identical joint identities with different inverse binds remain different skin domains.</summary>
    [Fact]
    public void DifferentInverseBindsOnOneNodeAreNotMerged()
    {
        var source = new GlbScene();
        var owner = AddNode(source, "same", Matrix4x4.Identity);
        var joint = AddNode(source, "joint", Matrix4x4.CreateTranslation(10, 0, 0));
        source.MeshParts.Add(Part(owner, Skin([joint], [Matrix4x4.Identity])));
        source.MeshParts.Add(Part(owner, Skin([joint], [Matrix4x4.CreateTranslation(-4, 0, 0)])));
        var document = Adapt(source);
        Assert.Equal(2, document.Skins.Count);
        AssertNear(new Vector3(10, 0, 0), WorldPosition(document, document.Nodes.Count - 2));
        AssertNear(new Vector3(6, 0, 0), WorldPosition(document, document.Nodes.Count - 1));
        var encoded = Shared(document);
        Assert.Equal(Matrix4x4.Identity, encoded.LogicalSkins[0].GetJoint(0).InverseBindMatrix);
        Assert.Equal(Matrix4x4.CreateTranslation(-4, 0, 0), encoded.LogicalSkins[1].GetJoint(0).InverseBindMatrix);
    }

    /// <summary>A rigid primitive keeps its source transform while a skin on that node retains joint-driven placement.</summary>
    [Fact]
    public void MixedRigidAndSkinnedPartsDoNotShareAMeshOrSkin()
    {
        var source = new GlbScene();
        var owner = AddNode(source, "same", Matrix4x4.CreateTranslation(100, 0, 0));
        var joint = AddNode(source, "joint", Matrix4x4.CreateTranslation(10, 0, 0));
        source.MeshParts.Add(Part(owner));
        source.MeshParts.Add(Part(owner, Skin([joint], [Matrix4x4.Identity])));
        var document = Adapt(source);
        Assert.Equal(2, document.Meshes.Count);
        Assert.NotNull(document.Nodes[owner].MeshIndex);
        Assert.Null(document.Nodes[owner].SkinIndex);
        Assert.Null(document.Meshes[document.Nodes[owner].MeshIndex!.Value].Primitives[0].SkinInfluences);
        Assert.NotEqual(document.Nodes[owner].MeshIndex, document.Nodes[^1].MeshIndex);
        Assert.Equal(1, Ordinal(document.Nodes[^1]));
        AssertNear(new Vector3(100, 0, 0), WorldPosition(document, owner));
        AssertNear(new Vector3(10, 0, 0), WorldPosition(document, document.Nodes.Count - 1));
        Assert.Equal(2, Shared(document).LogicalMeshes.Count);
    }

    /// <summary>Filtered geometry and repeated references do not renumber a generated identity to a display label or filtered index.</summary>
    [Fact]
    public void RepeatedPartObjectsRetainTheirUnfilteredOccurrenceOrdinals()
    {
        var source = new GlbScene();
        var joint = AddNode(source, "same", Matrix4x4.Identity);
        source.MeshParts.Add(new GlbMeshPart
        {
            Name = "same", NodeIndex = 0, Submesh = new RenderableSubmesh { Positions = [], Triangles = [] }
        });
        var part = Part(null, Skin([joint], [Matrix4x4.Identity]));
        source.MeshParts.Add(part);
        source.MeshParts.Add(part);
        var first = Adapt(source);
        var second = Adapt(source);
        Assert.Equal(2, first.Meshes.Count);
        Assert.Equal(2, first.Skins.Count);
        Assert.Equal(1, Ordinal(first.Nodes[^2]));
        Assert.Equal(2, Ordinal(first.Nodes[^1]));
        Assert.Equal(first.Nodes[^2].ExtrasJson, second.Nodes[^2].ExtrasJson);
        Assert.Equal(first.Nodes[^1].ExtrasJson, second.Nodes[^1].ExtrasJson);
        Assert.NotEqual(first.Nodes[^2].ExtrasJson, first.Nodes[^1].ExtrasJson);
        Assert.Same(part, source.MeshParts[1]);
        Assert.Same(part, source.MeshParts[2]);
    }

    /// <summary>Anonymous production-shaped skins retain a fifth joint and its contribution to the evaluated pose.</summary>
    [Fact]
    public void AnonymousSkinRetainsEveryInfluenceBeyondTheLegacyFourSlotLimit()
    {
        var source = new GlbScene();
        var joints = Enumerable.Range(1, 5).Select(index =>
            AddNode(source, "same", Matrix4x4.CreateTranslation(index * 2, 0, 0))).ToArray();
        var binding = Skin(joints, Enumerable.Repeat(Matrix4x4.Identity, 5).ToArray());
        binding.PerVertexInfluences[0] = [(0, 0.2f), (1, 0.2f), (2, 0.2f), (3, 0.2f), (4, 0.2f)];
        source.MeshParts.Add(Part(null, binding));
        var document = Adapt(source);
        Assert.Equal(5, document.Meshes[0].Primitives[0].SkinInfluences!.InfluencesPerVertex);
        AssertNear(new Vector3(6, 0, 0), WorldPosition(document, document.Nodes.Count - 1));
        var primitive = Assert.Single(Assert.Single(Shared(document).LogicalMeshes).Primitives);
        Assert.Equal(4f, primitive.GetVertexAccessor("JOINTS_1").AsVector4Array()[0].X);
        Assert.Equal(0.2f, primitive.GetVertexAccessor("WEIGHTS_1").AsVector4Array()[0].X);
        Assert.Equal(5, binding.PerVertexInfluences[0].Length);
    }

    /// <summary>Sampled joint poses preserve bind-before-child-before-parent order and every authored influence.</summary>
    /// <param name="time">The sample in a one-second translation of the fifth joint.</param>
    /// <param name="allInfluences">Whether all five joints contribute instead of only the moving joint.</param>
    [Theory]
    [InlineData(0f, false)]
    [InlineData(0.5f, false)]
    [InlineData(1f, false)]
    [InlineData(0f, true)]
    [InlineData(0.5f, true)]
    [InlineData(1f, true)]
    public void SampledJointPosesRetainHierarchyAndCompleteInfluences(float time, bool allInfluences)
    {
        var source = new GlbScene();
        var parentTransform = Matrix4x4.CreateRotationZ(MathF.PI / 2f) * Matrix4x4.CreateTranslation(10, 20, 30);
        var parent = AddNode(source, "same", parentTransform);
        var joints = new int[5];
        for (var index = 0; index < joints.Length; index++)
        {
            var translation = 2 * (index + 1) + (index == 4 ? 10 * time : 0);
            var local = Matrix4x4.CreateScale(2, 3, 4) * Matrix4x4.CreateTranslation(translation, 0, 0);
            joints[index] = source.AddNode("same", parent, local, local * parentTransform,
                GlbNodeKind.Skeleton, "same");
        }

        var inverseBind = Matrix4x4.CreateTranslation(0, -1, 0);
        var binding = Skin(joints, Enumerable.Repeat(inverseBind, 5).ToArray());
        binding.PerVertexInfluences[0] = allInfluences
            ? [(0, 0.2f), (1, 0.2f), (2, 0.2f), (3, 0.2f), (4, 0.2f)]
            : [(4, 1f)];
        var part = Part(null, binding);
        part.Submesh.Positions[0] = 1;
        part.Submesh.Positions[1] = 1;
        source.MeshParts.Add(part);
        var originalPositions = part.Submesh.Positions.ToArray();
        var originalNormals = part.Submesh.Normals!.ToArray();
        var originalInfluences = binding.PerVertexInfluences[0].ToArray();
        var originalTransforms = source.Nodes.Select(node => node.LocalTransform).ToArray();
        var document = Adapt(source);

        // Bind moves (1,1,0) to (1,0,0), child scale makes (2,0,0), then child
        // translations average to 6+2t (or 10+10t for joint five alone). Parent
        // rotation maps X to Y before translation; the final basis maps (x,y,z) to (x,z,-y).
        var expected = new Vector3(10, 30, allInfluences ? -28 - 2 * time : -32 - 10 * time);
        AssertNear(expected, WorldPosition(document, document.Nodes.Count - 1));
        AssertNear(expected, EncodedCompleteInfluencePosition(Shared(document)));
        if (!allInfluences)
        {
            using var resolver = new NifTextureResolver(_ => null);
            var native = ModelRoot.ParseGLB(GlbWriter.WriteToBytes(source, resolver));
            AssertNear(expected, EncodedCompleteInfluencePosition(native));
            Assert.Equal(6, native.LogicalNodes.Count(node => node.Name == "same"));
        }

        Assert.Equal(originalPositions, part.Submesh.Positions);
        Assert.Equal(originalNormals, part.Submesh.Normals);
        Assert.Equal(originalInfluences, binding.PerVertexInfluences[0]);
        Assert.Equal(originalTransforms, source.Nodes.Select(node => node.LocalTransform));
        Assert.All(source.Nodes.Skip(1), node => Assert.Equal("same", node.Name));
        Assert.All(binding.InverseBindMatrices, value => Assert.Equal(inverseBind, value));
    }

    /// <summary>Previously supported rigid grouping, node order and normalized winding remain unchanged without mutating source buffers.</summary>
    [Fact]
    public void RigidGroupingAndWindingStayCompatibleWhileSourceArraysRemainUntouched()
    {
        var source = new GlbScene();
        var owner = AddNode(source, "same", Matrix4x4.CreateTranslation(4, 5, 6));
        var first = Part(owner);
        first.Submesh.Triangles[1] = 2;
        first.Submesh.Triangles[2] = 1;
        var positions = first.Submesh.Positions.ToArray();
        var normals = first.Submesh.Normals!.ToArray();
        var uvs = first.Submesh.UVs!.ToArray();
        source.MeshParts.Add(first);
        source.MeshParts.Add(Part(owner));
        var document = Adapt(source);
        Assert.Equal(source.Nodes.Count, document.Nodes.Count);
        Assert.Empty(document.Skins);
        Assert.Equal(2, Assert.Single(document.Meshes).Primitives.Count);
        Assert.Equal(0, document.Nodes[owner].MeshIndex);
        Assert.Equal([0, 1, 2], document.Meshes[0].Primitives[0].Indices);
        Assert.Equal(new ushort[] { 0, 2, 1 }, first.Submesh.Triangles);
        Assert.Equal(positions, first.Submesh.Positions);
        Assert.Equal(normals, first.Submesh.Normals);
        Assert.Equal(uvs, first.Submesh.UVs);
        Assert.Same(first, source.MeshParts[0]);
        AssertNear(new Vector3(4, 6, -5), WorldPosition(document, owner));
    }

    /// <summary>A missing rigid owner remains an explicit decline, while invalid supplied skin provenance is never invented.</summary>
    /// <param name="skinned">Whether the malformed part supplies a skin with an invalid explicit owner.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvalidOwnerReferencesStillDeclineWithoutPartialOutput(bool skinned)
    {
        var source = new GlbScene();
        source.MeshParts.Add(skinned ? Part(99, Skin([0], [Matrix4x4.Identity])) : Part(null));
        using var resolver = new NifTextureResolver(_ => null);
        Assert.False(NifNeutralSceneAdapter.TryAdapt(source, resolver, "fixture", out var document,
            out var reason, TestContext.Current.CancellationToken));
        Assert.Null(document);
        Assert.Equal("A mesh part with no resolved owning node retains the native writer.", reason);
    }

    /// <summary>Appends one root child while deliberately allowing repeated labels.</summary>
    /// <param name="source">The fixture hierarchy.</param><param name="name">A presentation label.</param>
    /// <param name="transform">The authored root-relative transform.</param><returns>The original node index.</returns>
    private static int AddNode(GlbScene source, string name, Matrix4x4 transform) =>
        source.AddNode(name, GlbScene.RootNodeIndex, transform, transform, GlbNodeKind.Skeleton, name);

    /// <summary>Creates a triangle whose null owning node matches the successful skinned branch of NifExportSceneBuilder.Build.</summary>
    /// <param name="owner">Optional original source node provenance.</param><param name="skin">Optional independent skin domain.</param>
    /// <returns>A source occurrence with explicit geometry and a source block, without resolving identity through its name.</returns>
    private static GlbMeshPart Part(int? owner, GlbSkinBinding? skin = null) => new()
    {
        Name = "same", NodeIndex = owner, Skin = skin,
        Submesh = new RenderableSubmesh
        {
            ShapeName = "same", SourceBlockIndex = 17,
            Positions = [0, 0, 0, 1, 0, 0, 0, 1, 0], Triangles = [0, 1, 2],
            Normals = [0, 0, 1, 0, 0, 1, 0, 0, 1], UVs = [0, 0, 1, 0, 0, 1]
        }
    };

    /// <summary>Creates one explicit palette whose three vertices initially use its first joint.</summary>
    /// <param name="joints">Original source-node identities.</param><param name="binds">Independent authored inverse binds.</param>
    /// <returns>The mutable source fixture binding retained by the caller for nonmutation assertions.</returns>
    private static GlbSkinBinding Skin(int[] joints, Matrix4x4[] binds) => new()
    {
        JointNodeIndices = joints, InverseBindMatrices = binds,
        PerVertexInfluences = [[(0, 1f)], [(0, 1f)], [(0, 1f)]]
    };

    /// <summary>Runs the actual adapter with an explicit texture-free resolver and the test lifetime token.</summary>
    /// <param name="source">The assembled export-boundary fixture.</param><returns>The complete validated neutral document.</returns>
    private static ModelDocument Adapt(GlbScene source)
    {
        using var resolver = new NifTextureResolver(_ => null);
        Assert.True(NifNeutralSceneAdapter.TryAdapt(source, resolver, "fixture", out var document,
            out var reason, TestContext.Current.CancellationToken), reason);
        return document!;
    }

    /// <summary>Samples the shared rest pose and reads vertex zero of the exact node's first primitive.</summary>
    /// <param name="document">The neutral document.</param><param name="nodeIndex">An exact node identity, never a label.</param>
    /// <returns>The evaluated world position.</returns>
    private static Vector3 WorldPosition(ModelDocument document, int nodeIndex)
    {
        var evaluator = new ScenePoseEvaluator(document, TestContext.Current.CancellationToken);
        var workspace = evaluator.CreateWorkspace(0, TestContext.Current.CancellationToken);
        evaluator.EvaluateRest(workspace, TestContext.Current.CancellationToken);
        var index = Enumerable.Range(0, workspace.Instances.Count).First(index => workspace.Instances[index].NodeIndex == nodeIndex);
        return workspace.GetWorldPositions(index).Span[0];
    }

    /// <summary>Encodes and parses the shared GLB so assertions inspect serialized skin state.</summary>
    /// <param name="document">The actual adapter result.</param><returns>The strictly encoded, parsed GLB.</returns>
    private static ModelRoot Shared(ModelDocument document) => ModelRoot.ParseGLB(GltfExporter.Encode(
        SceneGltfBuilder.Build(document, GltfExportIntent.Interchange, TestContext.Current.CancellationToken),
        TestContext.Current.CancellationToken));

    /// <summary>Evaluates the independent one-joint fixtures from actual encoded vertex, bind and joint-world data.</summary>
    /// <param name="model">The legacy or shared GLB artifact.</param><returns>World-space vertex zero.</returns>
    private static Vector3 EncodedSingleJointPosition(ModelRoot model)
    {
        var node = Assert.Single(model.LogicalNodes, node => node.Mesh is not null && node.Skin is not null);
        Assert.Equal(1, node.Skin!.JointsCount);
        var primitive = Assert.Single(node.Mesh!.Primitives);
        Assert.Equal(0f, primitive.GetVertexAccessor("JOINTS_0").AsVector4Array()[0].X);
        Assert.Equal(1f, primitive.GetVertexAccessor("WEIGHTS_0").AsVector4Array()[0].X);
        var joint = node.Skin.GetJoint(0);
        return Vector3.Transform(primitive.GetVertexAccessor("POSITION").AsVector3Array()[0],
            joint.InverseBindMatrix * joint.Joint.WorldMatrix);
    }

    /// <summary>Evaluates every serialized joint/weight set without inheriting the legacy four-influence limit.</summary>
    /// <param name="model">The actual native or shared GLB with one skin occurrence.</param>
    /// <returns>Vertex zero after complete bind-pose linear skinning.</returns>
    private static Vector3 EncodedCompleteInfluencePosition(ModelRoot model)
    {
        var node = Assert.Single(model.LogicalNodes, node => node.Mesh is not null && node.Skin is not null);
        var primitive = Assert.Single(node.Mesh!.Primitives);
        var position = primitive.GetVertexAccessor("POSITION").AsVector3Array()[0];
        var result = Vector3.Zero;
        for (var set = 0; primitive.GetVertexAccessor($"JOINTS_{set}") is { } jointAccessor; set++)
        {
            var joints = jointAccessor.AsVector4Array()[0];
            var weights = primitive.GetVertexAccessor($"WEIGHTS_{set}").AsVector4Array()[0];
            for (var component = 0; component < 4; component++)
            {
                if (weights[component].Equals(0f)) continue;
                var joint = node.Skin!.GetJoint((int)joints[component]);
                result += Vector3.Transform(position, joint.InverseBindMatrix * joint.Joint.WorldMatrix) * weights[component];
            }
        }
        return result;
    }

    /// <summary>Reads the stable original occurrence ordinal from generated placement metadata.</summary>
    /// <param name="node">A generated skin node.</param><returns>The original unfiltered mesh-part ordinal.</returns>
    private static int Ordinal(SceneNode node)
    {
        using var metadata = JsonDocument.Parse(node.ExtrasJson!);
        return metadata.RootElement.GetProperty("bethesdaNifSkinPlacement").GetProperty("meshPartOrdinal").GetInt32();
    }

    /// <summary>Reads optional source-node provenance without treating a missing owner as the root.</summary>
    /// <param name="node">A generated skin node.</param><returns>The original node index or null.</returns>
    private static int? SourceNode(SceneNode node)
    {
        using var metadata = JsonDocument.Parse(node.ExtrasJson!);
        var value = metadata.RootElement.GetProperty("bethesdaNifSkinPlacement").GetProperty("sourceNodeIndex");
        return value.ValueKind == JsonValueKind.Null ? null : value.GetInt32();
    }

    /// <summary>Reads the optional original geometry block from generated placement provenance.</summary>
    /// <param name="node">A generated skin node.</param><returns>The original block index or null.</returns>
    private static int? SourceBlock(SceneNode node)
    {
        using var metadata = JsonDocument.Parse(node.ExtrasJson!);
        var value = metadata.RootElement.GetProperty("bethesdaNifSkinPlacement").GetProperty("sourceBlockIndex");
        return value.ValueKind == JsonValueKind.Null ? null : value.GetInt32();
    }

    /// <summary>Compares independent expected positions with tolerance for the fixed basis rotation.</summary>
    /// <param name="expected">The manually derived world position.</param><param name="actual">The evaluated or encoded position.</param>
    private static void AssertNear(Vector3 expected, Vector3 actual)
    {
        Assert.Equal(expected.X, actual.X, 4);
        Assert.Equal(expected.Y, actual.Y, 4);
        Assert.Equal(expected.Z, actual.Z, 4);
    }
}
