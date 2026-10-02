using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using BethesdaMultitool.Core.Modeling.Starfield;
using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Tests.Core.Modeling.Starfield;

/// <summary>
///     The reader-side half of hops A1 and A3: the same SHA-256 digests the Python expectations state
///     (<c>DIGEST_RULES</c> in <c>tools/scripts/gate2/starfield_mesh_cover.py</c>), computed from a
///     <see cref="ModelDocument" /> alone (its vertices, tangents, UV sets, attribute streams, LOD meshes and the meshlet
///     and cull native rows), never from the decoder the reader used.
/// </summary>
/// <remarks>
///     The meshlet and cull digests follow the Python rule: present whenever the tail is. The reader writes no native
///     row for a tail section holding no records, so such a section, which the header row states with count 0, digests
///     as the SHA-256 of no bytes, exactly as the oracle writes it.
/// </remarks>
internal static class Cut2MeshDigests
{
    /// <summary>
    ///     The digests of a document, keyed as the expectations key them. <paramref name="lodCount" /> is the number of
    ///     LOD lists (node k carries LOD k); <paramref name="present" /> says which optional vertex arrays the file stores
    ///     (<c>normals</c>, <c>tangents</c>, <c>uv0</c>, <c>uv1</c>), so an absent array is not digested as zeros.
    /// </summary>
    public static Dictionary<string, string> Compute(ModelDocument document, int lodCount, ISet<string> present)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(present);
        var primitive = document.Meshes[0].Primitives[0];
        var digests = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["indices"] = Int32s(primitive.Indices),
            ["positions"] = Floats(primitive.Vertices.SelectMany(static v => new[] { v.Position.X, v.Position.Y, v.Position.Z }))
        };
        if (present.Contains("normals"))
        {
            digests["normals"] = Floats(primitive.Vertices.SelectMany(static v => new[] { v.Normal.X, v.Normal.Y, v.Normal.Z }));
        }

        if (present.Contains("tangents") && primitive.Tangents is { } tangents)
        {
            digests["tangents"] = Tangents(tangents, 1f);
        }

        if (present.Contains("uv0"))
        {
            digests["uv0"] = Floats(primitive.Vertices.SelectMany(static v => new[] { v.TexCoord.X, v.TexCoord.Y }));
        }

        if (present.Contains("uv1") && primitive.AdditionalTextureCoordinates.Count > 0)
        {
            digests["uv1"] = Floats(primitive.AdditionalTextureCoordinates[0].Values.SelectMany(static v => new[] { v.X, v.Y }));
        }

        foreach (var stream in primitive.Attributes)
        {
            digests[stream.Name] = Sha256(stream.CopyContent());
        }

        for (var lod = 1; lod <= lodCount; lod++)
        {
            var node = document.Nodes[lod];
            digests[$"lod.{lod}"] = node.MeshIndex is { } mesh
                ? Int32s(document.Meshes[mesh].Primitives[0].Indices)
                : Sha256([]);
        }

        if (Row(document, StarfieldMeshModelNativeState.MeshletsKind) is { } meshlets)
        {
            digests["meshlets"] = MeshletDigest(meshlets);
        }
        else if (IsEmptyTailSection(document, "meshlets"))
        {
            digests["meshlets"] = Sha256([]);
        }

        if (Row(document, StarfieldMeshModelNativeState.CullKind) is { } cull)
        {
            digests["cull"] = CullDigest(cull);
        }
        else if (IsEmptyTailSection(document, "cull"))
        {
            digests["cull"] = Sha256([]);
        }

        return digests;
    }

    /// <summary>
    ///     Whether the header row states the meshlet tail and the named tail section with no records (the reader then
    ///     writes no row for it).
    /// </summary>
    private static bool IsEmptyTailSection(ModelDocument document, string name)
    {
        if (Row(document, StarfieldMeshModelNativeState.HeaderKind) is not { } header)
        {
            return false;
        }

        var payload = JsonNode.Parse(header.PayloadJson)!.AsObject();
        return payload["meshletTail"]?.GetValue<bool>() == true &&
               payload["sections"]!.AsArray().Any(section =>
                   section!["name"]!.GetValue<string>() == name && section["count"]!.GetValue<long>() == 0);
    }

    /// <summary>
    ///     The tangent digest with every w multiplied by <paramref name="sign" />: -1 gives the engine's reading of the
    ///     stored sign (glTF's w negated), the control the expectations call <c>swappedTangentsDigest</c>.
    /// </summary>
    public static string Tangents(SceneTangents tangents, float sign)
    {
        ArgumentNullException.ThrowIfNull(tangents);
        return Floats(tangents.Values.SelectMany(t => new[] { t.X, t.Y, t.Z, t.W * sign }));
    }

    /// <summary>The digest of float32 values in little-endian order.</summary>
    public static string Floats(IEnumerable<float> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var list = values.ToList();
        var bytes = new byte[list.Count * 4];
        for (var i = 0; i < list.Count; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4), list[i]);
        }

        return Sha256(bytes);
    }

    /// <summary>The digest of int32 values in little-endian order.</summary>
    public static string Int32s(IReadOnlyList<int> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var bytes = new byte[values.Count * 4];
        for (var i = 0; i < values.Count; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(i * 4), values[i]);
        }

        return Sha256(bytes);
    }

    /// <summary>The lowercase SHA-256 of bytes.</summary>
    public static string Sha256(ReadOnlySpan<byte> bytes)
    {
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    /// <summary>The one native row of a kind, or null.</summary>
    public static SceneNativeState? Row(ModelDocument document, string kind)
    {
        return document.NativeStates.SingleOrDefault(s => string.Equals(s.Kind, kind, StringComparison.Ordinal));
    }

    /// <summary>The meshlet records re-encoded from the row's four arrays (the row's own digest when it is summarized).</summary>
    private static string MeshletDigest(SceneNativeState row)
    {
        var payload = JsonNode.Parse(row.PayloadJson)!.AsObject();
        if (payload["summarized"]?.GetValue<bool>() == true)
        {
            return payload["recordsSha256"]!.GetValue<string>();
        }

        var count = payload["count"]!.GetValue<int>();
        var fields = new[] { "vertexCount", "vertexOffset", "triangleCount", "triangleOffset" }
            .Select(name => payload[name]!.AsArray()).ToArray();
        var bytes = new byte[count * 16];
        for (var record = 0; record < count; record++)
        {
            for (var field = 0; field < 4; field++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(record * 16 + field * 4),
                    fields[field][record]!.GetValue<uint>());
            }
        }

        return Sha256(bytes);
    }

    /// <summary>The cull records re-encoded from the row's values (numbers, or hex bits when not finite).</summary>
    private static string CullDigest(SceneNativeState row)
    {
        var payload = JsonNode.Parse(row.PayloadJson)!.AsObject();
        if (payload["summarized"]?.GetValue<bool>() == true)
        {
            return payload["recordsSha256"]!.GetValue<string>();
        }

        var values = payload["records"]!.AsArray().SelectMany(static r => r!.AsArray()).Select(static v =>
        {
            var node = v!.AsValue();
            return node.TryGetValue<string>(out var hex)
                ? BitConverter.UInt32BitsToSingle(Convert.ToUInt32(hex[2..], 16))
                : node.GetValue<float>();
        });
        return Floats(values);
    }
}
