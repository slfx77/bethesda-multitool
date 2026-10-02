using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using System.Text;
using System.Text.Json;
using BethesdaMultitool.Core.Modeling;
using BethesdaMultitool.Core.Modeling.Redguard;
using BethesdaMultitool.Core.Modeling.Xngine;
using BethesdaMultitool.Tests.Core.Modeling.Xngine;
using BethesdaMultitool.Tests.Helpers;
using Slfx77.Multitool.Core.Assets;
using Slfx77.Multitool.Core.Models;
using Slfx77.Multitool.Core.Models.Export;
using Slfx77.Multitool.Core.Models.Sources;
using Slfx77.Multitool.Core.Settings;
using Slfx77.Multitool.Media.Blender;
using Slfx77.Multitool.Media.Blender.Package;
using Slfx77.Multitool.Media.Models;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Modeling.Redguard;

/// <summary>
///     Shared plumbing for the Redguard <c>.3DC</c> reader tests (cut-1c slice 6): read a built stack through the real
///     <see cref="IModelSourceReader" /> contract, probe a bounded candidate, rebuild a document with another clip or
///     primitive for a control, sample a morph track independently of Shared, and run the Shared GLB writer. Stacks
///     come from <see cref="Redguard3DcTestStackBuilder" />, written from the plan's layout, so the reader is never
///     compared with its own writer.
/// </summary>
internal static class Redguard3DcModelTestSupport
{
    /// <summary>The virtual path loose stacks are read from; its stem is the document name.</summary>
    public const string DefaultPath = "3dart/ACTOR.3DC";

    /// <summary>Reads a stack through an in-memory source with a fresh registration cache, then validates its structure.</summary>
    public static ModelReadResult Read(byte[] bytes, IReadOnlyDictionary<string, string>? options = null,
        ModelNativeDetail detail = ModelNativeDetail.Metadata, string path = DefaultPath)
    {
        var source = new InMemoryAssetSource(XnGineModelTestSupport.SourceId);
        var item = new ModelSourceItem(source, source.Add(path, bytes));
        using var input = new MemoryStream(bytes, false);
        var context = new ModelReadContext(item, input, BethesdaModelRegistration.CreateCache(), detail, options);
        var result = new Redguard3DcModelReader().Read(item, context, CancellationToken.None);
        SceneValidation.ValidateStructure(result.Document);
        return result;
    }

    /// <summary>
    ///     Probes the first <paramref name="prefixLength" /> bytes (default: up to the 64 KiB budget), complete when the
    ///     prefix is the whole file unless <paramref name="isComplete" /> says otherwise. The path has no mesh extension:
    ///     recognition must come from content alone.
    /// </summary>
    public static ModelProbeResult Probe(byte[] bytes, int? prefixLength = null, bool? isComplete = null)
    {
        return new Redguard3DcModelReader().Probe(Candidate(bytes, prefixLength, isComplete));
    }

    /// <summary>A bounded candidate over a prefix of the bytes (see <see cref="Probe" />).</summary>
    public static ModelSourceCandidate Candidate(byte[] bytes, int? prefixLength = null, bool? isComplete = null)
    {
        var length = prefixLength ?? Math.Min(bytes.Length, ModelSourceCandidate.MaximumProbeBytes);
        var entry = new AssetEntry(new AssetReference(XnGineModelTestSupport.SourceId, "probe/candidate.bin"),
            bytes.Length);
        return new ModelSourceCandidate(entry, bytes.AsSpan(0, length), isComplete ?? length == bytes.Length);
    }

    /// <summary>The same document with its clips replaced (every other root collection shared), for a control.</summary>
    public static ModelDocument WithAnimations(ModelDocument document, IEnumerable<SceneAnimation> animations)
    {
        return Rebuild(document, document.Meshes, animations);
    }

    /// <summary>The same document with mesh 0's primitive <paramref name="index" /> replaced, for a control.</summary>
    public static ModelDocument WithPrimitive(ModelDocument document, int index, ScenePrimitive primitive)
    {
        var mesh = document.Meshes[0];
        var primitives = mesh.Primitives.ToArray();
        primitives[index] = primitive;
        return Rebuild(document, [new SceneMesh(mesh.Name, primitives, mesh.ExtrasJson, mesh.MorphWeights)],
            document.Animations);
    }

    /// <summary>A copy of a primitive with another normal provenance (every buffer shared), for the Flat agreement control.</summary>
    public static ScenePrimitive WithNormalProvenance(ScenePrimitive primitive, SceneNormalProvenanceKind kind)
    {
        return new ScenePrimitive(primitive.Name, primitive.Vertices, primitive.Indices, primitive.MaterialIndex,
            primitive.ColorEncoding, primitive.MorphTargets, primitive.ExtrasJson, normalMode: primitive.NormalMode)
        {
            Faces = primitive.Faces,
            PointIndices = primitive.PointIndices,
            Attributes = primitive.Attributes,
            NormalProvenance = new SceneNormalProvenance(kind)
        };
    }

    /// <summary>
    ///     Samples a morph track at <paramref name="time" /> seconds, independently of Shared's evaluator: before the first
    ///     key its weights, at or after the last key its weights, else Step holds the key at or before the time and
    ///     Linear interpolates between the two keys around it. Other interpolations are not sampled here.
    /// </summary>
    /// <exception cref="NotSupportedException">The track's interpolation is neither Step nor Linear.</exception>
    public static float[] Sample(SceneMorphTrack track, float time)
    {
        var width = track.TargetCount;
        var times = track.Times;
        float[] Key(int key) => track.Weights.Skip(key * width).Take(width).ToArray();
        if (time <= times[0])
        {
            return Key(0);
        }

        if (time >= times[^1])
        {
            return Key(times.Count - 1);
        }

        var k = 0;
        while (times[k + 1] <= time)
        {
            k++;
        }

        return track.Interpolation switch
        {
            SceneInterpolation.Step => Key(k),
            SceneInterpolation.Linear => Key(k).Zip(Key(k + 1), (a, b) =>
                a + (b - a) * ((time - times[k]) / (times[k + 1] - times[k]))).ToArray(),
            _ => throw new NotSupportedException("Only Step and Linear are sampled here.")
        };
    }

    /// <summary>The one-hot weights that show frame <paramref name="frame" /> of a stack with <paramref name="targets" /> targets (all zero for the keyframe).</summary>
    public static float[] OneHot(int targets, int frame)
    {
        var weights = new float[targets];
        if (frame > 0)
        {
            weights[frame - 1] = 1f;
        }

        return weights;
    }

    /// <summary>Plans a document with the Shared GLB writer and writes it into <paramref name="directory" />.</summary>
    /// <returns>The plan's fidelity rows and the write result (null when the write threw NotSupportedException).</returns>
    public static async Task<(IReadOnlyList<ModelFidelityRow> Rows, ModelWriteResult? Written, string? Refusal)> WriteGlbAsync(
        ModelDocument document, string directory, string name)
    {
        var writer = new ModelGlbWriter();
        var environment = await writer.PreflightAsync(new Dictionary<string, string>(),
            TestContext.Current.CancellationToken);
        var plan = writer.Plan(document, environment, new ModelConvertOptions("glb"),
            TestContext.Current.CancellationToken);
        try
        {
            var written = await writer.WriteAsync(new ModelWriteRequest(plan, Path.Combine(directory, name + ".glb")),
                TestContext.Current.CancellationToken);
            return (plan.Fidelity.Rows, written, null);
        }
        catch (NotSupportedException exception)
        {
            return (plan.Fidelity.Rows, null, exception.Message);
        }
    }

    /// <summary>
    ///     Plans a document with Shared's Blender writer through its package-only preflight (no Blender is located or
    ///     run), writes the package zip into <paramref name="directory" /> and returns the <c>packageVersion</c> its
    ///     manifest declares.
    /// </summary>
    public static async Task<int> PackageVersionAsync(ModelDocument document, string directory, string name)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var writer = new ModelBlendWriter(new ExternalToolSettings(Path.Combine(directory, "blender-settings-unused.json")));
        var environment = ModelBlendWriter.PreflightPackage(new Dictionary<string, string>(StringComparer.Ordinal),
            cancellationToken);
        var plan = writer.Plan(document, environment, new ModelConvertOptions(writer.Format), cancellationToken);
        var path = Path.Combine(directory, name + ".zip");
        await ModelBlendWriter.WritePackageAsync(plan, path, cancellationToken);
        using var archive = ZipFile.OpenRead(path);
        await using var manifest = archive.GetEntry(BlenderPackageWriter.ManifestEntryName)!.Open();
        using var json = await JsonDocument.ParseAsync(manifest, cancellationToken: cancellationToken);
        return json.RootElement.GetProperty("packageVersion").GetInt32();
    }

    /// <summary>
    ///     A hand-laid one-frame v2.6 stack for the declared-size checks (slice-6 review finding 4), written without the
    ///     stack builder so a header can declare what no builder would: the header (+4 <paramref name="pointCount" />,
    ///     +8 one plane, +12 radius 400, +16 one frame, +20 = 64, +44 = 1, +60 = 100), the preamble at 64 (88, 0,
    ///     <paramref name="unaccounted" />, 0, 0, 0), the one 3-dword frame record at 88 (<paramref name="frame" />), and at
    ///     100 one triangle keyed 0x0182 whose corners store the byte offsets 0, 12 and 24 (points 0, 1, 2), ending at 132.
    ///     The file is <paramref name="length" /> bytes; <paramref name="keyframe" /> is written from 132 when given.
    /// </summary>
    /// <remarks>
    ///     <c>Record(3, (132, 168, 172), 0, 184, three points)</c> is a consistent stack that reads.
    ///     <c>Record(0x15555556, (132, 140, 144), 0, 156)</c> is the review's record: 357,913,942 x 12 wraps to 8 in
    ///     int32, so its blocks tile exactly and, unchecked, the parser allocates a 4 GiB keyframe. A test only hands that
    ///     one to the pure check, never to a parse, so a regressed check cannot make the suite allocate 4 GiB; the probe
    ///     and read tests use the same record with <paramref name="unaccounted" /> 4, which no longer tiles.
    /// </remarks>
    public static byte[] Record(int pointCount, (int Points, int Normals, int PlaneData) frame, int unaccounted,
        int length, params (int X, int Y, int Z)[] keyframe)
    {
        var bytes = new byte[length];
        var span = bytes.AsSpan();
        Encoding.ASCII.GetBytes("v2.6", span[..4]);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], pointCount);
        BinaryPrimitives.WriteInt32LittleEndian(span[8..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(span[12..], 400);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(span[20..], 64);
        BinaryPrimitives.WriteInt32LittleEndian(span[44..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(span[60..], 100);
        int[] preamble = [88, 0, unaccounted, 0, 0, 0];
        for (var i = 0; i < preamble.Length; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(span[(64 + 4 * i)..], preamble[i]);
        }

        BinaryPrimitives.WriteInt32LittleEndian(span[88..], frame.Points);
        BinaryPrimitives.WriteInt32LittleEndian(span[92..], frame.Normals);
        BinaryPrimitives.WriteInt32LittleEndian(span[96..], frame.PlaneData);
        span[100] = 3;
        BinaryPrimitives.WriteUInt16LittleEndian(span[102..], 0x0182);
        for (var corner = 0; corner < 3; corner++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(span[(108 + 8 * corner)..], 12 * corner);
        }

        for (var point = 0; point < keyframe.Length; point++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(span[(132 + 12 * point)..], keyframe[point].X);
            BinaryPrimitives.WriteInt32LittleEndian(span[(136 + 12 * point)..], keyframe[point].Y);
            BinaryPrimitives.WriteInt32LittleEndian(span[(140 + 12 * point)..], keyframe[point].Z);
        }

        return bytes;
    }

    /// <summary>The consistent three-point record of <see cref="Record" />, the control the crafted records are measured against.</summary>
    public static byte[] ConsistentRecord()
    {
        return Record(3, (132, 168, 172), 0, 184, (0, 0, 0), (256, 0, 0), (0, 0, 256));
    }

    /// <summary>The integer point a document position carries: <c>(x, -y, z)</c> undone exactly.</summary>
    public static (int X, int Y, int Z) Integer(Vector3 position)
    {
        return ((int)position.X, (int)-position.Y, (int)position.Z);
    }

    private static ModelDocument Rebuild(ModelDocument document, IEnumerable<SceneMesh> meshes,
        IEnumerable<SceneAnimation> animations)
    {
        return new ModelDocument(document.SourceFormat, document.Name, document.Scenes, document.Nodes, meshes,
            document.Materials, document.Images, document.Samplers, animations, document.DefaultSceneIndex,
            document.SourceIdentity, document.ExtrasJson, document.Skins, document.Diagnostics, document.Units,
            document.SourceBasis, document.NativeStates, document.LayerSets, document.Palettes)
        {
            SourceProvenance = document.SourceProvenance
        };
    }
}
