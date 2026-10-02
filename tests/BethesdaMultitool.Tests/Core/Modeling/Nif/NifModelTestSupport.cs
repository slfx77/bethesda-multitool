using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using BethesdaMultitool.Core.Modeling;
using BethesdaMultitool.Core.Modeling.Nif;
using BethesdaMultitool.Tests.Helpers;
using Slfx77.Multitool.Core.Assets;
using Slfx77.Multitool.Core.Localization;
using Slfx77.Multitool.Core.Models;
using Slfx77.Multitool.Core.Models.Sources;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Modeling.Nif;

/// <summary>
///     Shared plumbing for the NIF model-reader tests: read a built fixture through the real
///     <see cref="IModelSourceReader" /> contract (item, context, cache), probe a bounded candidate, and lay out the
///     node and geometry bodies the fixtures use. Block bodies come from <see cref="NifTestBlockLayouts" />, which is
///     transcribed from nif.xml without NifSchema, so the reader is never compared with its own decoder.
/// </summary>
internal static class NifModelTestSupport
{
    /// <summary>The virtual path fixtures are read from; its stem is the document name.</summary>
    public const string DefaultPath = "meshes/test/model.nif";

    /// <summary>The source id of the in-memory source.</summary>
    public const string SourceId = "memory";

    /// <summary>Reads a built fixture with a fresh cache, as the Shared read lifetime would.</summary>
    public static ModelReadResult Read(byte[] bytes, IReadOnlyDictionary<string, string>? options = null,
        ModelNativeDetail detail = ModelNativeDetail.Metadata, string path = DefaultPath)
    {
        var (item, input) = Open(bytes, path);
        using (input)
        {
            var context = new ModelReadContext(item, input, new NifModelReadCache(), detail, options);
            return new NifModelReader().Read(item, context, CancellationToken.None);
        }
    }

    /// <summary>
    ///     Reads a built fixture with an explicit companion resolver, purpose and texture codec (the counting seam). When
    ///     <paramref name="source" /> is given the fixture is added to it at <paramref name="path" />, so Shared's default
    ///     resolver (a null <paramref name="resolver" />) searches that source.
    /// </summary>
    public static ModelReadResult ReadWith(byte[] bytes, ModelCompanionResolver? resolver,
        ModelReadPurpose purpose = ModelReadPurpose.Conversion, INifTextureCodec? codec = null,
        InMemoryAssetSource? source = null, string path = DefaultPath, NifModelReadCache? cache = null,
        IReadOnlyDictionary<string, string>? options = null)
    {
        source ??= new InMemoryAssetSource(SourceId);
        var item = new ModelSourceItem(source, source.Add(path, bytes));
        using var input = new MemoryStream(bytes, false);
        var context = new ModelReadContext(item, input, cache ?? new NifModelReadCache(), ModelNativeDetail.Metadata,
            options, resolver, purpose);
        var reader = codec is null ? new NifModelReader() : new NifModelReader(codec);
        return reader.Read(item, context, CancellationToken.None);
    }

    /// <summary>
    ///     A companion resolver over <see cref="BethesdaMultitool.Core.Modeling.BethesdaTextureCompanions" /> and an
    ///     in-memory game file system holding <paramref name="files" /> (virtual path, bytes).
    /// </summary>
    public static BethesdaTextureCompanions Companions(params (string Path, byte[] Bytes)[] files)
    {
        return new BethesdaTextureCompanions(new MemoryGameFileSystem("memory-data", files), "memory-data");
    }

    /// <summary>Creates a retained item over the bytes and opens its input stream.</summary>
    public static (ModelSourceItem Item, Stream Input) Open(byte[] bytes, string path = DefaultPath)
    {
        var source = new InMemoryAssetSource(SourceId);
        var item = new ModelSourceItem(source, source.Add(path, bytes));
        return (item, new MemoryStream(bytes, false));
    }

    /// <summary>
    ///     Probes the first <paramref name="prefixLength" /> bytes (default: up to the 64 KiB probe budget). The candidate
    ///     is complete when the prefix is the whole file unless <paramref name="isComplete" /> says otherwise. The path
    ///     deliberately has no NIF extension: recognition must come from content alone.
    /// </summary>
    public static ModelProbeResult Probe(byte[] bytes, int? prefixLength = null, bool? isComplete = null)
    {
        var length = prefixLength ?? Math.Min(bytes.Length, ModelSourceCandidate.MaximumProbeBytes);
        var entry = new AssetEntry(new AssetReference(SourceId, "probe/candidate.bin"), bytes.Length);
        var candidate = new ModelSourceCandidate(entry, bytes.AsSpan(0, length), isComplete ?? length == bytes.Length);
        return new NifModelReader().Probe(candidate);
    }

    /// <summary>
    ///     A header prefix with only the identity fields: the header line, the version, the endian byte when the version
    ///     has one, the user version and BS version when given, and Num Blocks, padded with zeros.
    /// </summary>
    public static byte[] IdentityPrefix(string headerLine, uint version, bool? bigEndian, uint? userVersion,
        uint blockCount, uint? bsVersion, int totalLength = 256)
    {
        var w = new NifTestBlockWriter(false);
        w.RawAscii(headerLine).RawU8(0x0A).RawU32Le(version);
        if (bigEndian is { } order)
        {
            w.RawU8(order ? (byte)0 : (byte)1);
        }

        if (userVersion is { } user)
        {
            w.RawU32Le(user);
        }

        w.RawU32Le(blockCount);
        if (bsVersion is { } bs)
        {
            w.RawU32Le(bs);
        }

        var bytes = w.ToArray();
        Array.Resize(ref bytes, Math.Max(bytes.Length, totalLength));
        return bytes;
    }

    /// <summary>Adds a NiNode block with the given name, transform and children, and returns its index.</summary>
    public static int AddNode(NifTestFileBuilder builder, int nameIndex, int[] children,
        (float X, float Y, float Z)? translation = null, float[]? rotation = null, float scale = 1f,
        uint flags = 0x0E, int[]? properties = null, string type = "NiNode")
    {
        var bs = builder.BsVersion;
        return builder.AddBlock(type, w =>
        {
            NifTestBlockLayouts.ObjectNet(w, nameIndex);
            NifTestBlockLayouts.AvObject(w, bs, flags, translation ?? (0f, 0f, 0f),
                rotation ?? NifTestBlockLayouts.Identity, scale, properties);
            NifTestBlockLayouts.NodeTail(w, children);
        });
    }

    /// <summary>
    ///     Adds a NiTriShape for BS 14 to 34: NiObjectNET + NiAVObject, then NiGeometry (nif.xml:9889-9931) at 20.2.0.7
    ///     below BS 100: Data (Ref), Skin Instance (Ref), Material Data (Num Materials uint = 0, Active Material int = -1,
    ///     Material Needs Update bool since 20.2.0.7); Shader Property and Alpha Property are BS &gt; 34 only.
    /// </summary>
    public static int AddTriShape(NifTestFileBuilder builder, int nameIndex, int data = -1, int controller = -1,
        int skinInstance = -1, int[]? properties = null)
    {
        var bs = builder.BsVersion;
        return builder.AddBlock("NiTriShape",
            w => NifTestBlockLayouts.GeometryShape(w, bs, nameIndex, data, skinInstance, controller,
                properties: properties));
    }

    /// <summary>Adds a NiTriStrips (the same NiGeometry layout as <see cref="AddTriShape" />).</summary>
    public static int AddTriStrips(NifTestFileBuilder builder, int nameIndex, int data = -1, int controller = -1)
    {
        var bs = builder.BsVersion;
        return builder.AddBlock("NiTriStrips",
            w => NifTestBlockLayouts.GeometryShape(w, bs, nameIndex, data, controller: controller));
    }

    /// <summary>Adds a NiTriShapeData with the given streams and stored triangle list.</summary>
    public static int AddTriShapeData(NifTestFileBuilder builder, NifTestGeometryStreams streams,
        ushort[] triangleIndices, bool hasTriangles = true, ushort[][]? matchGroups = null)
    {
        return builder.AddBlock("NiTriShapeData", w =>
        {
            NifTestBlockLayouts.GeometryData(w, streams);
            NifTestBlockLayouts.TriShapeDataTail(w, triangleIndices, hasTriangles, matchGroups);
        });
    }

    /// <summary>Adds a NiTriStripsData with the given streams and strips.</summary>
    public static int AddTriStripsData(NifTestFileBuilder builder, NifTestGeometryStreams streams, ushort[][] strips)
    {
        return builder.AddBlock("NiTriStripsData", w =>
        {
            NifTestBlockLayouts.GeometryData(w, streams);
            NifTestBlockLayouts.TriStripsDataTail(w, strips);
        });
    }

    /// <summary>
    ///     Builds the common geometry fixture: 0 NiNode "Root" [1], 1 NiTriShape "Shape" (data 2), 2 NiTriShapeData.
    /// </summary>
    public static byte[] SingleTriShape(NifTestGeometryStreams streams, ushort[] triangleIndices, bool bigEndian = false,
        uint bs = 34)
    {
        var builder = new NifTestFileBuilder(bigEndian, bs);
        AddNode(builder, builder.AddString("Root"), [1]);
        AddTriShape(builder, builder.AddString("Shape"), 2);
        AddTriShapeData(builder, streams, triangleIndices);
        return builder.Build();
    }

    /// <summary>The single primitive of the mesh placed by the node named <paramref name="nodeName" />.</summary>
    public static ScenePrimitive PrimitiveOf(ModelDocument document, string nodeName)
    {
        var node = document.Nodes.Single(n => n.Name == nodeName);
        var mesh = Assert.IsType<int>(node.MeshIndex);
        return Assert.Single(document.Meshes[mesh].Primitives);
    }

    /// <summary>The parsed payload of the <c>bmt.nif.primitive</c> row for one mesh.</summary>
    public static JsonObject PrimitivePayload(ModelDocument document, int meshIndex)
    {
        var row = Rows(document, NifModelGeometryReader.PrimitiveKind)
            .Single(r => r.Target.Kind == SceneElementKind.Primitive && r.Target.Index == meshIndex);
        return JsonNode.Parse(row.PayloadJson)!.AsObject();
    }

    /// <summary>Adds a NiAlphaProperty (nif.xml:5449-5470): NiObjectNET, Flags (ushort), Threshold (byte).</summary>
    public static int AddAlphaProperty(NifTestFileBuilder builder)
    {
        return builder.AddBlock("NiAlphaProperty", w =>
        {
            NifTestBlockLayouts.ObjectNet(w, -1);
            w.U16(0x00ED).U8(128);
        });
    }

    /// <summary>The native-state rows of one kind, in document order.</summary>
    public static List<SceneNativeState> Rows(ModelDocument document, string kind)
    {
        return document.NativeStates.Where(row => string.Equals(row.Kind, kind, StringComparison.Ordinal)).ToList();
    }

    /// <summary>The parsed payload of the <c>bmt.nif.block</c> row for one block.</summary>
    public static JsonObject BlockPayload(ModelDocument document, int blockIndex)
    {
        var row = Rows(document, NifModelNativeState.BlockKind)
            .Single(r => r.SourceLocation?.ElementIdentity == $"block:{blockIndex}");
        return JsonNode.Parse(row.PayloadJson)!.AsObject();
    }

    /// <summary>A diagnostic's text (the reader emits raw text, which resolves unchanged in any catalog).</summary>
    public static string DiagnosticText(SceneDiagnostic diagnostic)
    {
        return diagnostic.Message.Resolve(SharedStrings.Create(CultureInfo.InvariantCulture));
    }

    /// <summary>An ASCII string as raw bytes.</summary>
    public static byte[] Ascii(string text)
    {
        return Encoding.ASCII.GetBytes(text);
    }
}
