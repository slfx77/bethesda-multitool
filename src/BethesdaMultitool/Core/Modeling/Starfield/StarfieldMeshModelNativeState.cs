using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Geometry;
using Slfx77.Multitool.Core.Models;
using Slfx77.Multitool.Core.Models.Sources;

namespace BethesdaMultitool.Core.Modeling.Starfield;

/// <summary>
///     The native-state rows of a Starfield <c>.mesh</c> document (cut-2 plan section 3.5), each at payload version
///     <see cref="PayloadVersion" />: <see cref="HeaderKind" /> on the document (the version, the section walk, the
///     counts, the scale with its bits, the tail and the W census); <see cref="QuantizationKind" /> on the main primitive
///     (the scale, the largest stored component and the numeric routes); <see cref="MeshletsKind" /> and
///     <see cref="CullKind" /> on the main primitive when the tail holds records (every record, the measured evidence,
///     the SHA-256 of the section's records and, with <see cref="ModelNativeDetail.Full" />, those raw bytes); and
///     <see cref="SentinelsKind" /> on the main primitive when a Dec4 zero sentinel occurs.
/// </summary>
/// <remarks>
///     Bounds: a meshlet or cull section of more than <see cref="MaximumInlineRecords" /> records keeps only its count
///     and SHA-256 inline (the retail maximum is 1,749 meshlets, under 50 KB of JSON for the meshlets and 200 KB for the
///     cull records), so no row can exceed <see cref="SceneNativeState.MaximumPayloadCharacters" />. Floats are JSON
///     numbers when finite and their IEEE bits in hex otherwise.
/// </remarks>
internal static class StarfieldMeshModelNativeState
{
    /// <summary>The document header row kind.</summary>
    public const string HeaderKind = "bmt.starfield.mesh.header";

    /// <summary>The quantization row kind.</summary>
    public const string QuantizationKind = "bmt.starfield.mesh.quantization";

    /// <summary>The meshlet row kind.</summary>
    public const string MeshletsKind = "bmt.starfield.mesh.meshlets";

    /// <summary>The cull row kind.</summary>
    public const string CullKind = "bmt.starfield.mesh.cull";

    /// <summary>The Dec4 zero-sentinel census row kind.</summary>
    public const string SentinelsKind = "bmt.starfield.mesh.dec4-sentinels";

    /// <summary>The payload schema version of every kind.</summary>
    public const int PayloadVersion = 1;

    /// <summary>The most meshlet or cull records a row lists inline.</summary>
    public const int MaximumInlineRecords = 8192;

    /// <summary>The position route the quantization row states (plan decision D5).</summary>
    public const string PositionRoute = "position = fl32(q x scale / 32767), evaluated in binary64 and rounded once";

    /// <summary>The Dec4 route the quantization row states (plan decision D5).</summary>
    public const string Dec4Route =
        "normal and tangent channel = fl32((2v - 1023) / 1023) per 10-bit code v, unnormalized; tangent w = glTF " +
        "handedness = -1 for code 3, +1 for code 0 (the stored bitangent sign negated: it puts cross(N, T) x w along " +
        "+dP/dv of the DirectX UVs, glTF's bitangent runs along -dP/dv)";

    /// <summary>The UV route the quantization row states.</summary>
    public const string UvRoute = "uv = the stored half value exactly; a non-finite component reads 0 (bits in starfield.uv{n}.raw)";

    /// <summary>The measured meshlet evidence the meshlet row states (plan section 0.2).</summary>
    public const string MeshletEvidence =
        "record = (vertexCount, vertexOffset, triangleCount, triangleOffset); over the 714,487 retail files with the " +
        "tail the triangle counts sum to the main list, vertexOffset is the running sum of vertexCount, and " +
        "triangleOffset is the byte offset of 3-byte local triangles with each meshlet's block padded to 4 bytes (the " +
        "index-unit reading holds on 339,403 only); the meshlet-vertex list those offsets index is not in the file " +
        "(inferred: the engine builds it)";

    /// <summary>The measured cull evidence the cull row states (plan section 0.2).</summary>
    public const string CullEvidence =
        "record = center xyz then extent xyz: read so, each box contains its meshlet's triangles on 713,595 of 714,487 " +
        "retail files; read as min/max, on 4";

    /// <summary>Builds the rows.</summary>
    /// <param name="item">The source occurrence (for the source locations).</param>
    /// <param name="bytes">The whole stream.</param>
    /// <param name="sha256">The stream's SHA-256.</param>
    /// <param name="mesh">The parse.</param>
    /// <param name="facts">The reader's facts.</param>
    /// <param name="detail">Whether raw section bytes are retained.</param>
    public static IReadOnlyList<SceneNativeState> Build(ModelSourceItem item, byte[] bytes, string sha256,
        StarfieldMeshFile mesh, StarfieldMeshModelFacts facts, ModelNativeDetail detail)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(facts);
        var full = detail == ModelNativeDetail.Full;
        var primitive = new SceneElementRef(SceneElementKind.Primitive, 0, 0);
        var rows = new List<SceneNativeState>
        {
            new(new SceneElementRef(SceneElementKind.Document), HeaderKind, PayloadVersion,
                Header(bytes, sha256, mesh, facts).ToJsonString(), Location(item, "stream", 0, bytes.Length)),
            new(primitive, QuantizationKind, PayloadVersion, Quantization(mesh, facts).ToJsonString(),
                Location(item, "positions", StarfieldMeshModelFacts.Section(mesh, "positions")))
        };

        if (mesh.HasMeshletTail)
        {
            var meshlets = StarfieldMeshModelFacts.Section(mesh, "meshlets");
            if (meshlets.Count > 0)
            {
                var raw = Records(bytes, meshlets);
                rows.Add(new SceneNativeState(primitive, MeshletsKind, PayloadVersion,
                    Meshlets(mesh, raw).ToJsonString(), Location(item, "meshlets", meshlets),
                    full ? (ReadOnlyMemory<byte>?)raw : null));
            }

            var cull = StarfieldMeshModelFacts.Section(mesh, "cull");
            if (cull.Count > 0)
            {
                var raw = Records(bytes, cull);
                rows.Add(new SceneNativeState(primitive, CullKind, PayloadVersion, Cull(mesh, raw).ToJsonString(),
                    Location(item, "cull", cull), full ? (ReadOnlyMemory<byte>?)raw : null));
            }
        }

        if (facts.Sentinels > 0)
        {
            rows.Add(new SceneNativeState(primitive, SentinelsKind, PayloadVersion, Sentinels(facts).ToJsonString(),
                Location(item, "normals", StarfieldMeshModelFacts.Section(mesh, "normals"))));
        }

        return rows.AsReadOnly();
    }

    /// <summary>A section's record bytes (after its count dword).</summary>
    public static byte[] Records(byte[] bytes, StarfieldMeshSection section)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(section);
        return bytes.AsSpan(section.Offset + 4, section.Length - 4).ToArray();
    }

    /// <summary>A float as a JSON number, or its IEEE bits in hex when it is not finite.</summary>
    public static JsonNode Float(float value)
    {
        return float.IsFinite(value)
            ? JsonValue.Create(value)
            : JsonValue.Create(string.Create(CultureInfo.InvariantCulture,
                $"0x{BitConverter.SingleToUInt32Bits(value):X8}"))!;
    }

    private static JsonObject Header(byte[] bytes, string sha256, StarfieldMeshFile mesh, StarfieldMeshModelFacts facts)
    {
        var sections = new JsonArray();
        foreach (var section in mesh.Sections)
        {
            sections.Add(new JsonObject
            {
                ["name"] = section.Name,
                ["offset"] = section.Offset,
                ["length"] = section.Length,
                ["count"] = section.Count
            });
        }

        JsonNode? firstNormalW = facts.FirstNormalW is { } w ? JsonValue.Create(w) : null;
        return new JsonObject
        {
            ["version"] = mesh.Version,
            ["byteLength"] = bytes.Length,
            ["sha256"] = sha256,
            ["sections"] = sections,
            ["indexCount"] = mesh.IndexCount,
            ["vertexCount"] = facts.VertexCount,
            ["scale"] = Float(mesh.Scale),
            ["scaleBits"] = Bits(mesh.Scale),
            ["weightsPerVertex"] = mesh.WeightsPerVertex,
            ["lodIndexCounts"] = new JsonArray(mesh.LodIndexLists
                .Select(static list => (JsonNode?)JsonValue.Create(list.Length)).ToArray()),
            ["meshletTail"] = mesh.HasMeshletTail,
            ["firstNormalW"] = firstNormalW,
            ["normalW"] = Counts(facts.NormalW),
            ["tangentW"] = Counts(facts.TangentW),
            ["uv0NonFinite"] = facts.Uv0NonFinite,
            ["uv1NonFinite"] = facts.Uv1NonFinite,
            ["unusedVertices"] = facts.UnusedVertices
        };
    }

    private static JsonObject Quantization(StarfieldMeshFile mesh, StarfieldMeshModelFacts facts)
    {
        return new JsonObject
        {
            ["scale"] = Float(mesh.Scale),
            ["scaleBits"] = Bits(mesh.Scale),
            ["quantizedMaxAbs"] = facts.QuantizedMaxAbs,
            ["snorm"] = 32767,
            ["positionRoute"] = PositionRoute,
            ["dec4Route"] = Dec4Route,
            ["uvRoute"] = UvRoute
        };
    }

    private static JsonObject Meshlets(StarfieldMeshFile mesh, byte[] raw)
    {
        var count = mesh.Meshlets.Length / 4;
        var payload = new JsonObject
        {
            ["count"] = count,
            ["recordsSha256"] = Convert.ToHexStringLower(SHA256.HashData(raw)),
            ["evidence"] = MeshletEvidence
        };
        if (count > MaximumInlineRecords)
        {
            payload["summarized"] = true;
            return payload;
        }

        var names = new[] { "vertexCount", "vertexOffset", "triangleCount", "triangleOffset" };
        for (var field = 0; field < names.Length; field++)
        {
            var values = new JsonNode?[count];
            for (var record = 0; record < count; record++)
            {
                values[record] = JsonValue.Create(mesh.Meshlets[record * 4 + field]);
            }

            payload[names[field]] = new JsonArray(values);
        }

        return payload;
    }

    private static JsonObject Cull(StarfieldMeshFile mesh, byte[] raw)
    {
        var count = mesh.CullRecords.Length / 6;
        var payload = new JsonObject
        {
            ["count"] = count,
            ["recordsSha256"] = Convert.ToHexStringLower(SHA256.HashData(raw)),
            ["interpretation"] = "center + extent",
            ["evidence"] = CullEvidence
        };
        if (count > MaximumInlineRecords)
        {
            payload["summarized"] = true;
            return payload;
        }

        var records = new JsonNode?[count];
        for (var record = 0; record < count; record++)
        {
            var values = new JsonNode?[6];
            for (var component = 0; component < 6; component++)
            {
                values[component] = Float(mesh.CullRecords[record * 6 + component]);
            }

            records[record] = new JsonArray(values);
        }

        payload["records"] = new JsonArray(records);
        return payload;
    }

    private static JsonObject Sentinels(StarfieldMeshModelFacts facts)
    {
        var channel = StarfieldMeshFile.Dec4Channel(511);
        return new JsonObject
        {
            ["code"] = new JsonArray(JsonValue.Create(511), JsonValue.Create(511), JsonValue.Create(511)),
            ["channel"] = Float(channel),
            ["length"] = Float(MathF.Sqrt(3f * channel * channel)),
            ["normalsByW"] = Counts(facts.NormalSentinelsByW),
            ["tangentsByW"] = Counts(facts.TangentSentinelsByW),
            ["rule"] = "kept as decoded (plan decision D6): the stream's own value, not an exact zero"
        };
    }

    private static JsonArray Counts(IReadOnlyList<int> counts)
    {
        return new JsonArray(counts.Select(static count => (JsonNode?)JsonValue.Create(count)).ToArray());
    }

    private static string Bits(float value)
    {
        return string.Create(CultureInfo.InvariantCulture, $"0x{BitConverter.SingleToUInt32Bits(value):X8}");
    }

    private static SceneSourceLocation Location(ModelSourceItem item, string element, StarfieldMeshSection section)
    {
        return Location(item, element, section.Offset, section.Length);
    }

    private static SceneSourceLocation Location(ModelSourceItem item, string element, long offset, long length)
    {
        var reference = item.Reference;
        return new SceneSourceLocation(reference.SourceId, element, offset, length, reference);
    }
}
