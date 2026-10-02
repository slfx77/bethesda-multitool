using System.Numerics;
using System.Security.Cryptography;
using BethesdaMultitool.Core.Formats.Nif.Decoding;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Schema;
using BethesdaMultitool.Core.Modeling.Nif;
using BethesdaMultitool.Tests.Helpers;
using Slfx77.Multitool.Core.Models;
using Slfx77.Multitool.Core.Models.Sources;
using Xunit;
using static BethesdaMultitool.Tests.Core.Modeling.Nif.NifModelAnimationReaderTestSupport;

namespace BethesdaMultitool.Tests.Core.Modeling.Nif;

/// <summary>
///     Fixtures for the cut-1b slice 14 tests (<see cref="NifModelPropertyTracks" />, <see cref="NifModelAnimationReader" />):
///     20.2.0.7 block bodies for the property controllers (NiAlphaController, NiMaterialColorController,
///     BSMaterialEmittanceMultController, NiTextureTransformController, NiVisController, NiUVController) and their
///     interpolators and data (NiPoint3Interpolator, NiPosData, NiBoolInterpolator, NiBoolData, NiUVData,
///     NiBSplineCompPoint3Interpolator), written field by field from nif.xml at BS 34 so the reader is never compared
///     with its own decoder; the cut-1a stages a property track needs (textures, materials, geometry) run the way
///     <see cref="NifModelReader" /> runs them; and the document the brief assembles (nodes with their meshes, the
///     materials, the clips), evaluated through Shared's public <see cref="ScenePoseEvaluator" />, the only oracle for
///     sampled property values.
/// </summary>
internal static class NifModelAnimationPropertyTestSupport
{
    /// <summary>nif.xml's #INV_FLT#, the pose Value of an interpolator with none (each #INV_VEC3# component).</summary>
    public const uint InvalidFloatBits = 0xFF7FFFFF;

    /// <summary>The Controller Type strings of the five sequence-capable property controllers.</summary>
    public const string AlphaController = "NiAlphaController";

    /// <summary>NiMaterialColorController.</summary>
    public const string MaterialColorController = "NiMaterialColorController";

    /// <summary>BSMaterialEmittanceMultController.</summary>
    public const string EmittanceMultController = "BSMaterialEmittanceMultController";

    /// <summary>NiTextureTransformController.</summary>
    public const string TextureTransformController = "NiTextureTransformController";

    /// <summary>NiVisController.</summary>
    public const string VisController = "NiVisController";

    /// <summary>NiUVController.</summary>
    public const string UvController = "NiUVController";

    /// <summary>The quad the fixtures draw: four vertices with texture coordinates, two triangles.</summary>
    public static readonly ushort[] QuadTriangles = [0, 1, 2, 1, 3, 2];

    /// <summary>A controller of one of the property types with no fields beyond the interpolator: NiTimeController then Interpolator.</summary>
    public static Action<NifTestBlockWriter> SingleInterpController(int target, int interpolator, ushort flags,
        float frequency = 1f, float phase = 0f, float start = 0f, float stop = 1f, int next = -1)
    {
        return w =>
        {
            TimeController(w, next, flags, frequency, phase, start, stop, target);
            w.Ref(interpolator);
        };
    }

    /// <summary>An NiMaterialColorController: the single-interpolator header, then Target Color (ushort).</summary>
    public static Action<NifTestBlockWriter> MaterialColorControllerBlock(int target, int interpolator, ushort flags,
        ushort targetColor, float start = 0f, float stop = 1f, int next = -1)
    {
        return w =>
        {
            TimeController(w, next, flags, 1f, 0f, start, stop, target);
            w.Ref(interpolator).U16(targetColor);
        };
    }

    /// <summary>An NiTextureTransformController: the single-interpolator header, then Shader Map (bool), Texture Slot, Operation.</summary>
    public static Action<NifTestBlockWriter> TextureTransformControllerBlock(int target, int interpolator, ushort flags,
        uint textureSlot, uint operation, byte shaderMap = 0, float start = 0f, float stop = 1f, int next = -1)
    {
        return w =>
        {
            TimeController(w, next, flags, 1f, 0f, start, stop, target);
            w.Ref(interpolator).U8(shaderMap).U32(textureSlot).U32(operation);
        };
    }

    /// <summary>An NiUVController: NiTimeController, then Texture Set (ushort) and Data.</summary>
    public static Action<NifTestBlockWriter> UvControllerBlock(int target, int data, ushort flags,
        ushort textureSet = 0, float start = 0f, float stop = 1f, int next = -1)
    {
        return w =>
        {
            TimeController(w, next, flags, 1f, 0f, start, stop, target);
            w.U16(textureSet).Ref(data);
        };
    }

    /// <summary>An NiUVData: four float KeyGroups (U offset, V offset, U scale, V scale), each as <see cref="NifModelAnimationMorphTestSupport.FloatData" /> lays one out.</summary>
    public static Action<NifTestBlockWriter> UvData(float[][] uOffset, float[][] vOffset, float[][] uScale,
        float[][] vScale)
    {
        return w =>
        {
            NifModelAnimationMorphTestSupport.FloatData(NifModelAnimationMorphTestSupport.Linear, uOffset)(w);
            NifModelAnimationMorphTestSupport.FloatData(NifModelAnimationMorphTestSupport.Linear, vOffset)(w);
            NifModelAnimationMorphTestSupport.FloatData(NifModelAnimationMorphTestSupport.Linear, uScale)(w);
            NifModelAnimationMorphTestSupport.FloatData(NifModelAnimationMorphTestSupport.Linear, vScale)(w);
        };
    }

    /// <summary>An NiPoint3Interpolator (nif.xml: Value Vector3, Data): the three pose words, then the Data ref.</summary>
    public static Action<NifTestBlockWriter> Point3Interpolator(int data, uint xBits = InvalidFloatBits,
        uint yBits = InvalidFloatBits, uint zBits = InvalidFloatBits)
    {
        return w => w.U32(xBits).U32(yBits).U32(zBits).Ref(data);
    }

    /// <summary>
    ///     An NiPosData (nif.xml: one KeyGroup&lt;Vector3&gt;): Num Keys, Interpolation when there are keys, then each key's
    ///     floats exactly as given (time, x, y, z; QUADRATIC adds Forward and Backward triples; TBC adds its three floats).
    /// </summary>
    public static Action<NifTestBlockWriter> PosData(uint keyType, params float[][] keys)
    {
        return NifModelAnimationMorphTestSupport.FloatData(keyType, keys);
    }

    /// <summary>An NiBoolInterpolator (nif.xml: Value bool, Data): the pose byte, then the Data ref.</summary>
    public static Action<NifTestBlockWriter> BoolInterpolator(int data, byte value = 2)
    {
        return w => w.U8(value).Ref(data);
    }

    /// <summary>An NiBoolData (nif.xml: one KeyGroup&lt;byte&gt;): Num Keys, Interpolation when there are keys, then time and byte per key.</summary>
    public static Action<NifTestBlockWriter> BoolData(uint keyType, params (float Time, byte Value)[] keys)
    {
        return w =>
        {
            w.U32((uint)keys.Length);
            if (keys.Length != 0)
            {
                w.U32(keyType);
            }

            foreach (var (time, value) in keys)
            {
                w.F32(time).U8(value);
            }
        };
    }

    /// <summary>
    ///     An NiBSplineCompPoint3Interpolator (nif.xml NiBSplineInterpolator, NiBSplinePoint3Interpolator,
    ///     NiBSplineCompPoint3Interpolator): Start Time, Stop Time, Spline Data, Basis Data, the three Value words,
    ///     Handle, Position Offset, Position Half Range.
    /// </summary>
    public static Action<NifTestBlockWriter> CompPoint3Bspline(float start, float stop, int splineData, int basisData,
        uint handle, float offset, float halfRange)
    {
        return w => w.F32(start).F32(stop).Ref(splineData).Ref(basisData)
            .U32(InvalidFloatBits).U32(InvalidFloatBits).U32(InvalidFloatBits).U32(handle).F32(offset).F32(halfRange);
    }

    /// <summary>An NiMaterialProperty at BS 34 with distinctive rest values: specular (0.4, 0.5, 0.6), emissive (0.2, 0.4, 0.6), glossiness 12.5, the given alpha, Emit Mult 2.5.</summary>
    public static Action<NifTestBlockWriter> MaterialProperty(float alpha = 0.75f, int controller = -1)
    {
        return w =>
        {
            NifTestBlockLayouts.ObjectNet(w, -1, controller: controller);
            w.F32s(0.4f, 0.5f, 0.6f).F32s(0.2f, 0.4f, 0.6f).F32(12.5f).F32(alpha).F32(2.5f);
        };
    }

    /// <summary>
    ///     An NiTexturingProperty with one Base map (source block, texture-coordinate set 0, filter 2, clamp 3) carrying the
    ///     given texture transform, or none, and an optional controller.
    /// </summary>
    public static Action<NifTestBlockWriter> TexturingProperty(int source,
        (float Tu, float Tv, float Su, float Sv, float Rotation, uint Method, float Cu, float Cv)? transform,
        int controller = -1)
    {
        return w =>
        {
            NifTestBlockLayouts.ObjectNet(w, -1, controller: controller);
            w.U16((ushort)(2 << 1));
            w.U32(7);
            w.Bool(true);
            NifTestBlockLayouts.TexDesc(w, source, transform: transform);
            w.Bool(false); // Dark
            w.Bool(false); // Detail
            w.Bool(false); // Gloss
            w.Bool(false); // Glow
            w.Bool(false); // Has Bump Map Texture (Texture Count > 5)
            w.Bool(false); // Has Normal Texture (Texture Count > 6)
            w.U32(0); // Num Shader Textures
        };
    }

    /// <summary>An NiSourceTexture naming the given external file.</summary>
    public static Action<NifTestBlockWriter> SourceTexture(int fileName)
    {
        return w => NifTestBlockLayouts.SourceTexture(w, fileName);
    }

    /// <summary>Adds a textured quad shape at <paramref name="expectedShape" /> (its data at the next index) with the given properties and controller.</summary>
    public static void AddQuad(NifTestFileBuilder builder, int expectedShape, int nameIndex, int[] properties,
        int controller = -1)
    {
        Assert.Equal(expectedShape,
            NifModelTestSupport.AddTriShape(builder, nameIndex, expectedShape + 1, controller, properties: properties));
        Assert.Equal(expectedShape + 1, NifModelTestSupport.AddTriShapeData(builder, NifMaterialFixtures.Quad(),
            QuadTriangles));
    }

    /// <summary>
    ///     Reads a built fixture the way <see cref="NifModelReader" /> reads it before the animation stage: the read state,
    ///     the node graph, then the texture, material, geometry and skin stages, and the property targets slice 14 binds
    ///     to. The document holds the graph's nodes placed over their meshes, the meshes, materials, images and samplers.
    /// </summary>
    public static NifModelPropertyFixture ReadFixture(byte[] bytes)
    {
        var info = NifParser.Parse(bytes);
        Assert.NotNull(info);
        var schema = NifSchema.LoadEmbedded();
        var decoder = new NifBlockDecoder(schema, info, bytes);
        var footer = decoder.ValidateLayout();
        var blocks = new NifDecodedBlock[info.Blocks.Count];
        for (var i = 0; i < blocks.Length; i++)
        {
            var type = info.Blocks[i].TypeName;
            var mode = schema.Inherits(type, "NiNode") || NifModelGeometryReader.IsStrictType(type)
                ? NifDecodeMode.Strict
                : NifDecodeMode.Tolerant;
            blocks[i] = decoder.Decode(i, mode);
        }

        var (item, input) = NifModelTestSupport.Open(bytes);
        var cancellationToken = TestContext.Current.CancellationToken;
        using (input)
        {
            var cache = new NifModelReadCache();
            var context = new ModelReadContext(item, input, cache);
            var state = new NifModelReadState(item, ModelNativeDetail.Metadata, bytes,
                Convert.ToHexStringLower(SHA256.HashData(bytes)), info, schema, decoder, footer, blocks);
            var reachable = NifModelCoverage.ReferenceReachability(blocks, footer.Roots, cancellationToken);
            var palette = NifModelPaletteNames.Read(state, reachable, cancellationToken);
            var graph = NifModelNodeReader.Read(state, palette, cancellationToken);
            var skins = new NifModelSkinReader(state, graph, cancellationToken);
            var textures = new NifModelTextureSource(state, context, cache, NifTextureCodec.Instance, cancellationToken);
            var materials = new NifModelMaterialReader(state, graph, textures, cancellationToken);
            var platform = NifPackedPlatformOption.Resolve(new Dictionary<string, string>(StringComparer.Ordinal));
            var geometry = NifModelGeometryReader.Read(state, graph, materials.Resolve, skins, platform,
                cancellationToken);
            var materialResult = materials.Complete();
            var textureResult = textures.Complete();
            var skinResult = skins.Complete();
            var placed = NifModelNodeReader.WithPlacements(graph, new NifModelNodePlacements(geometry.MeshByNode,
                skinResult.SkinByNode, skinResult.JointNodes, new Dictionary<int, SceneBillboard>()));
            var targets = NifModelPropertyTargets.FromResults(materialResult, geometry);
            return new NifModelPropertyFixture(state, placed, materialResult, textureResult, geometry, targets, platform);
        }
    }

    /// <summary>Reads the fixture's clips through the reader with its material targets.</summary>
    public static NifModelAnimationResult Read(NifModelPropertyFixture fixture)
    {
        return NifModelAnimationReader.ReadNif(fixture.State, fixture.Graph, fixture.Targets, fixture.Platform,
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    ///     The document the brief assembles: the placed nodes, materials, images and samplers plus the clips, over one
    ///     legacy triangle per cut-1a primitive bound to that primitive's material. The cut-1a reader emits source
    ///     geometry, which Shared's evaluator refuses until it is explicitly lowered, and Shared publishes no lowering;
    ///     property tracks drive materials, layers and nodes, never vertices, so the stand-ins keep every target the
    ///     tracks address (mesh, primitive and material indices) and change nothing the evaluator samples here.
    /// </summary>
    public static ModelDocument AssembleDocument(NifModelPropertyFixture fixture, IEnumerable<SceneAnimation> clips)
    {
        var meshes = fixture.Geometry.Meshes.Select(static mesh => new SceneMesh(mesh.Name,
            mesh.Primitives.Select(static primitive => new ScenePrimitive(primitive.Name, StandInTriangle, [0, 1, 2],
                primitive.MaterialIndex)).ToArray())).ToArray();
        return new ModelDocument("nif", "fixture", [new SceneDefinition("scene", fixture.Graph.RootNodeIndices)],
            fixture.Graph.Nodes, meshes, fixture.Materials.Materials, fixture.Textures.Images,
            fixture.Materials.Samplers, clips);
    }

    /// <summary>The rest pose's factors of one material (no clip evaluated).</summary>
    public static SceneMaterialPose RestMaterial(ModelDocument document, int material)
    {
        return Rest(document).GetMaterial(material);
    }

    /// <summary>The rest pose's texture transform of one layer occurrence (no clip evaluated).</summary>
    public static SceneTextureTransform RestLayer(ModelDocument document, int material, int layer)
    {
        return Rest(document).GetTextureLayerTransform(material, layer);
    }

    /// <summary>The rest pose's local visibility of one node (no clip evaluated).</summary>
    public static bool RestVisible(ModelDocument document, int node)
    {
        return Rest(document).IsNodeVisible(node);
    }

    /// <summary>Evaluates one clip through Shared's public evaluator and returns one material's sampled factors.</summary>
    public static SceneMaterialPose SampleMaterial(ModelDocument document, int clip, float seconds, int material)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var evaluator = new ScenePoseEvaluator(document, cancellationToken);
        var pose = evaluator.CreateWorkspace(0, cancellationToken);
        evaluator.EvaluateClip(pose, clip, seconds, cancellationToken);
        return pose.GetMaterial(material);
    }

    /// <summary>Evaluates one clip and returns one layer occurrence's sampled texture transform.</summary>
    public static SceneTextureTransform SampleLayer(ModelDocument document, int clip, float seconds, int material,
        int layer)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var evaluator = new ScenePoseEvaluator(document, cancellationToken);
        var pose = evaluator.CreateWorkspace(0, cancellationToken);
        evaluator.EvaluateClip(pose, clip, seconds, cancellationToken);
        return pose.GetTextureLayerTransform(material, layer);
    }

    /// <summary>Evaluates one clip and returns one node's sampled local visibility.</summary>
    public static bool SampleVisible(ModelDocument document, int clip, float seconds, int node)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var evaluator = new ScenePoseEvaluator(document, cancellationToken);
        var pose = evaluator.CreateWorkspace(0, cancellationToken);
        evaluator.EvaluateClip(pose, clip, seconds, cancellationToken);
        return pose.IsNodeVisible(node);
    }

    /// <summary>The stand-in triangle of <see cref="AssembleDocument" />: three legacy vertices with UV set 0.</summary>
    private static readonly SceneVertex[] StandInTriangle =
    [
        new(Vector3.Zero, Vector3.UnitZ, Vector4.One, Vector2.Zero),
        new(Vector3.UnitX, Vector3.UnitZ, Vector4.One, Vector2.UnitX),
        new(Vector3.UnitY, Vector3.UnitZ, Vector4.One, Vector2.UnitY)
    ];

    /// <summary>Evaluates the rest pose of a document's scene 0.</summary>
    private static ScenePoseWorkspace Rest(ModelDocument document)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var evaluator = new ScenePoseEvaluator(document, cancellationToken);
        var pose = evaluator.CreateWorkspace(0, cancellationToken);
        evaluator.EvaluateRest(pose, cancellationToken);
        return pose;
    }

    /// <summary>The property track entries of a clip's extras.</summary>
    public static System.Text.Json.Nodes.JsonArray PropertyEntries(SceneAnimation clip)
    {
        return Extras(clip)["propertyTracks"]!.AsArray();
    }

    /// <summary>
    ///     One controlled block for <see cref="NifModelAnimationReaderTestSupport.Sequence" />: the interpolator, the Node
    ///     Name, Controller Type, priority, stored controller ref, Property Type and Controller ID, with no Interpolator ID
    ///     (string indices, -1 for none).
    /// </summary>
    public static (int Interpolator, int Controller, byte Priority, int Node, int PropertyType, int ControllerType,
        int ControllerId, int InterpolatorId) Controlled(int interpolator, int node, int controllerType, byte priority,
            int controller, int propertyType, int controllerId)
    {
        return (interpolator, controller, priority, node, propertyType, controllerType, controllerId, -1);
    }
}
