using System.Security.Cryptography;
using System.Text.Json;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Export;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Export;

/// <summary>Retains source object identity and exact serialized field/buffer values before neutral adaptation.</summary>
internal sealed class NifCorpusSourceSnapshot
{
    private static readonly JsonSerializerOptions Options = new() { IncludeFields = true };
    private readonly object?[] _references;
    private readonly byte[] _contentHash;

    /// <summary>Captures the source graph, matrices, skin tuples and material/geometry arrays without modifying them.</summary>
    internal NifCorpusSourceSnapshot(GlbScene source)
    {
        _references = References(source).ToArray();
        _contentHash = ContentHash(source);
    }

    /// <summary>Requires the graph's objects, array ownership and contents to remain unchanged before legacy export.</summary>
    internal void AssertUnchanged(GlbScene source)
    {
        var current = References(source).ToArray();
        Assert.Equal(_references.Length, current.Length);
        for (var index = 0; index < current.Length; index++) Assert.Same(_references[index], current[index]);
        Assert.Equal(_contentHash, ContentHash(source));
    }

    /// <summary>Hashes every public property and field, including nested matrix/vector and influence values.</summary>
    private static byte[] ContentHash(GlbScene source) =>
        SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(source, Options));

    /// <summary>Enumerates owned graph, geometry, skin and material-metadata references in source order.</summary>
    private static IEnumerable<object?> References(GlbScene source)
    {
        yield return source;
        yield return source.Nodes;
        yield return source.MeshParts;
        foreach (var node in source.Nodes) yield return node;
        foreach (var part in source.MeshParts)
        {
            yield return part;
            var geometry = part.Submesh;
            yield return geometry;
            yield return geometry.Positions;
            yield return geometry.Triangles;
            yield return geometry.Normals;
            yield return geometry.UVs;
            yield return geometry.VertexColors;
            yield return geometry.Tangents;
            yield return geometry.Bitangents;
            yield return geometry.BindPosePositions;
            yield return geometry.ShaderMetadata;
            yield return geometry.ShaderMetadata?.TextureSlots;
            yield return part.Skin;
            if (part.Skin is not { } skin) continue;
            yield return skin.JointNodeIndices;
            yield return skin.InverseBindMatrices;
            yield return skin.PerVertexInfluences;
            foreach (var influences in skin.PerVertexInfluences) yield return influences;
        }
    }
}
