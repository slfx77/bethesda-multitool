using System.Buffers.Binary;
using System.Text.Json.Nodes;
using BethesdaMultitool.Core.Modeling;
using BethesdaMultitool.Core.Modeling.Xngine;
using BethesdaMultitool.Tests.Helpers;
using Slfx77.Multitool.Core.Assets;
using Slfx77.Multitool.Core.Models;
using Slfx77.Multitool.Core.Models.Export;
using Slfx77.Multitool.Core.Models.Sources;
using Slfx77.Multitool.Core.Settings;
using Slfx77.Multitool.Media.Blender;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Modeling.Xngine;

/// <summary>
///     Shared plumbing for the XnGine <c>.3D</c> reader tests (cut-1c slice 5): read a built record through the real
///     <see cref="IModelSourceReader" /> contract (an in-memory source, or an entry of a synthetic archive opened through
///     the production session so the container facts answer), probe a bounded candidate, and read the typed streams and
///     native rows back. Records come from <see cref="XnGineTestMeshBuilder" />, which is written from the plan's layout
///     tables, so the reader is never compared with its own writer.
/// </summary>
internal static class XnGineModelTestSupport
{
    /// <summary>The virtual path loose fixtures are read from; its stem is the document name.</summary>
    public const string DefaultPath = "3dart/MODEL.3D";

    /// <summary>The in-memory source id.</summary>
    public const string SourceId = "memory";

    /// <summary>
    ///     Shared's Blender reason code for a face whose corners repeat a vertex (<c>BlendReasonCodes.FacesRepeatVertex</c>,
    ///     internal to Shared): Blender omits the face.
    /// </summary>
    public const string FacesRepeatVertex = "faces-repeat-vertex";

    /// <summary>An option bag naming the game (<c>daggerfall</c>, <c>battlespire</c>, <c>redguard</c>).</summary>
    public static IReadOnlyDictionary<string, string> Game(string game)
    {
        return new Dictionary<string, string>(StringComparer.Ordinal) { [BethesdaModelRegistration.GameOption] = game };
    }

    /// <summary>Reads a loose record through an in-memory source with a fresh registration cache, then validates its structure.</summary>
    public static ModelReadResult Read(byte[] bytes, IReadOnlyDictionary<string, string>? options = null,
        ModelNativeDetail detail = ModelNativeDetail.Metadata, string path = DefaultPath,
        ModelReadPurpose purpose = ModelReadPurpose.Conversion)
    {
        var source = new InMemoryAssetSource(SourceId);
        var item = new ModelSourceItem(source, source.Add(path, bytes));
        using var input = new MemoryStream(bytes, false);
        var context = new ModelReadContext(item, input, BethesdaModelRegistration.CreateCache(), detail, options,
            purpose: purpose);
        var result = new XnGineModelReader().Read(item, context, CancellationToken.None);
        SceneValidation.ValidateStructure(result.Document);
        return result;
    }

    /// <summary>Reads an archive entry through its production browse source (so the container facts answer), then validates.</summary>
    public static async Task<ModelReadResult> ReadEntryAsync(IAssetSource source, string path,
        IReadOnlyDictionary<string, string>? options = null, ModelNativeDetail detail = ModelNativeDetail.Metadata)
    {
        var entry = await ClassicContainerFixture.FindEntryAsync(source, path);
        var item = new ModelSourceItem(source, entry);
        await using var input = await item.OpenReadAsync(TestContext.Current.CancellationToken);
        var context = new ModelReadContext(item, input, BethesdaModelRegistration.CreateCache(), detail, options);
        var result = new XnGineModelReader().Read(item, context, TestContext.Current.CancellationToken);
        SceneValidation.ValidateStructure(result.Document);
        return result;
    }

    /// <summary>
    ///     Probes the first <paramref name="prefixLength" /> bytes (default: up to the 64 KiB budget), complete when the
    ///     prefix is the whole record unless <paramref name="isComplete" /> says otherwise. The path deliberately has no
    ///     mesh extension: recognition must come from content alone.
    /// </summary>
    public static ModelProbeResult Probe(byte[] bytes, int? prefixLength = null, bool? isComplete = null)
    {
        var length = prefixLength ?? Math.Min(bytes.Length, ModelSourceCandidate.MaximumProbeBytes);
        var entry = new AssetEntry(new AssetReference(SourceId, "probe/candidate.bin"), bytes.Length);
        var candidate = new ModelSourceCandidate(entry, bytes.AsSpan(0, length), isComplete ?? length == bytes.Length);
        return new XnGineModelReader().Probe(candidate);
    }

    /// <summary>The rows of one native kind.</summary>
    public static List<SceneNativeState> Rows(ModelDocument document, string kind)
    {
        return document.NativeStates.Where(row => string.Equals(row.Kind, kind, StringComparison.Ordinal)).ToList();
    }

    /// <summary>The parsed payload of the one row of a kind.</summary>
    public static JsonObject Payload(ModelDocument document, string kind)
    {
        return JsonNode.Parse(Assert.Single(Rows(document, kind)).PayloadJson)!.AsObject();
    }

    /// <summary>The primitive's <c>xngine.uv16</c> tuples in face-corner order.</summary>
    public static (short U, short V)[] StoredUv(ScenePrimitive primitive)
    {
        var stream = Assert.Single(primitive.Attributes, a => a.Name == XnGineModelGeometry.UvAttributeName);
        Assert.Equal(SceneAttributeDomain.FaceCorner, stream.Domain);
        Assert.Equal(SceneAttributeComponentType.Int16, stream.ComponentType);
        Assert.Equal(2, stream.Components);
        var result = new (short, short)[stream.Count];
        for (var i = 0; i < stream.Count; i++)
        {
            var tuple = stream.GetTupleBytes(i);
            result[i] = (BinaryPrimitives.ReadInt16LittleEndian(tuple), BinaryPrimitives.ReadInt16LittleEndian(tuple[2..]));
        }

        return result;
    }

    /// <summary>The primitive's <c>xngine.plane</c> ordinals, one per face.</summary>
    public static int[] PlaneOrdinals(ScenePrimitive primitive)
    {
        var stream = Assert.Single(primitive.Attributes, a => a.Name == XnGineModelGeometry.PlaneAttributeName);
        Assert.Equal(SceneAttributeDomain.Face, stream.Domain);
        Assert.Equal(SceneAttributeComponentType.Int32, stream.ComponentType);
        var result = new int[stream.Count];
        for (var i = 0; i < stream.Count; i++)
        {
            result[i] = BinaryPrimitives.ReadInt32LittleEndian(stream.GetTupleBytes(i));
        }

        return result;
    }

    /// <summary>
    ///     The Blender writer's planned fidelity rows for a document: admission only, planned without Blender (no
    ///     executable, and a settings store at an unused temporary path that planning never reads or writes), so nothing
    ///     is packaged or launched.
    /// </summary>
    public static IReadOnlyList<ModelFidelityRow> BlenderRows(ModelDocument document)
    {
        var settings = new ExternalToolSettings(Path.Combine(Path.GetTempPath(), "bmt-xngine-blender-rows-unused.json"));
        var writer = new ModelBlendWriter(settings);
        var environment = ModelBlendWriter.PreflightPackage(new Dictionary<string, string>(StringComparer.Ordinal),
            TestContext.Current.CancellationToken);
        return writer.Plan(document, environment, new ModelConvertOptions(writer.Format),
            TestContext.Current.CancellationToken).Fidelity.Rows;
    }

    /// <summary>
    ///     The faces whose point indices repeat a source point, by primitive index, each as its source plane ordinal
    ///     (<c>xngine.plane</c>), computed from <c>Faces</c> and <c>PointIndices</c> alone, independently of the reader's
    ///     own count (the rule Shared's Blender admission applies). Primitives without such a face are absent.
    /// </summary>
    public static Dictionary<int, List<int>> RepeatedPointFaces(ModelDocument document)
    {
        var result = new Dictionary<int, List<int>>();
        var primitives = Assert.Single(document.Meshes).Primitives;
        for (var p = 0; p < primitives.Count; p++)
        {
            var primitive = primitives[p];
            var faces = primitive.Faces!;
            var points = primitive.PointIndices!.Values;
            var ordinals = PlaneOrdinals(primitive);
            var cursor = 0;
            for (var f = 0; f < faces.FaceCount; f++)
            {
                var corners = Enumerable.Range(cursor, faces.FaceSizes[f]).Select(c => points[faces.CornerIndices[c]])
                    .ToList();
                if (corners.Distinct().Count() < corners.Count)
                {
                    if (!result.TryGetValue(p, out var list))
                    {
                        list = [];
                        result[p] = list;
                    }

                    list.Add(ordinals[f]);
                }

                cursor += faces.FaceSizes[f];
            }
        }

        return result;
    }

    /// <summary>
    ///     A record that walks as a static mesh AND satisfies the <c>.3DC</c> shape test: header +16 = 1 frame, the +20
    ///     frame block placed in the trailing bytes, whose first dword puts the frame table 12 bytes (one 3-dword record)
    ///     before the plane list. With <paramref name="frames" /> = 0 the same bytes fail the test (the control).
    /// </summary>
    public static byte[] AnimatedShapeRecord(int frames = 1)
    {
        var plain = XnGineTestMeshBuilder.EightByteRecord(headerPlus20: 1714);
        var planeList = BinaryPrimitives.ReadInt32LittleEndian(plain.AsSpan(60));
        var record = new byte[plain.Length + 24];
        plain.CopyTo(record, 0);
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(16), frames);
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(20), plain.Length);
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(plain.Length), planeList - 12);
        return record;
    }
}
