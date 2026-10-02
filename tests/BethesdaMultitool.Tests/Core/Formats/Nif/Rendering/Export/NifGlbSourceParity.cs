using System.Globalization;
using System.Numerics;
using System.Text.Json;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Export;
using Slfx77.Multitool.Core.Models;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Export;

/// <summary>
///     Layer A of the M2.1 parity gate: exact checks that a neutral <see cref="ModelDocument" /> holds precisely
///     what the native writer's own helpers derive from the source <see cref="GlbScene" />, part by part.
/// </summary>
/// <remarks>
///     <para>
///         Every drawable source part (nonempty and not a Starfield no-draw helper, the native writer's own rule) is
///         keyed by its source ordinal. Its expected values come from an independently cloned and
///         winding-normalized copy of the part, through the same helpers <see cref="GlbWriter" /> calls:
///         positions through <see cref="GltfCoordinateAdapter.ConvertPosition" />, normals through
///         <see cref="GlbWriter.ReadNormal" />, colors through <see cref="GlbWriter.ReadVertexColor" /> with the
///         prepared projection, texture coordinates raw, and on normal-mapped surfaces tangents through
///         <see cref="GlbWriter.ReadTangent" /> over <see cref="NpcGlbTangentBuilder" />. All comparisons are exact.
///     </para>
///     <para>
///         It also checks the vertex count against the source, the index list against the clone's normalized
///         winding, the owning node against the part's node, that one prepared material key maps to exactly one
///         material row carrying the prepared values, the retained node prefix and edges, and the exact
///         repeated-position count. It never modifies the source; the caller snapshots the source around it.
///     </para>
/// </remarks>
internal static class NifGlbSourceParity
{
    /// <summary>Checks the document against the source and returns the proven primitive-to-part correspondence.</summary>
    /// <param name="source">The assembled source scene, before any native write normalizes its winding in place.</param>
    /// <param name="document">The neutral document the normalized route built from that source.</param>
    /// <param name="resolver">The same texture resolver the adapter used.</param>
    /// <param name="cancellationToken">Cancels the per-vertex walk.</param>
    /// <returns>The mapping from normalized (mesh, primitive) to source part ordinal.</returns>
    internal static NifGlbSourceMapping Check(GlbScene source, ModelDocument document, NifTextureResolver resolver,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(resolver);
        NifNeutralSceneCorpusTests.AssertOriginalNodesAndSkinPlacements(source, document, resolver);

        var mapping = new NifGlbSourceMapping();
        var prepared = new Dictionary<NifMaterialCacheKey, NifPreparedMaterial>();
        var rowByKey = new Dictionary<NifMaterialCacheKey, int>();
        var keyByRow = new Dictionary<int, NifMaterialCacheKey>();
        var primitivesByNode = new Dictionary<int, int>();
        var skinPlacements = SkinPlacementNodes(source, document);
        var drawable = 0;
        for (var ordinal = 0; ordinal < source.MeshParts.Count; ordinal++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var part = source.MeshParts[ordinal];
            if (part.Submesh.TriangleCount == 0 || part.Submesh.VertexCount == 0 ||
                GlbWriter.ShouldSkipStarfieldNoDrawSubmesh(part.Submesh, resolver))
            {
                continue;
            }

            drawable++;
            int meshIndex;
            int primitiveIndex;
            if (part.Skin is null)
            {
                Assert.True(part.NodeIndex is not null, $"Rigid part {ordinal} '{part.Name}' has no owning node.");
                var node = document.Nodes[part.NodeIndex.Value];
                Assert.True(node.MeshIndex is not null,
                    $"Rigid part {ordinal} '{part.Name}' owns node {part.NodeIndex}, which carries no mesh.");
                meshIndex = node.MeshIndex.Value;
                primitiveIndex = primitivesByNode.GetValueOrDefault(part.NodeIndex.Value);
                primitivesByNode[part.NodeIndex.Value] = primitiveIndex + 1;
            }
            else
            {
                Assert.True(skinPlacements.TryGetValue(ordinal, out var placementNode),
                    $"Skinned part {ordinal} '{part.Name}' has no placement node.");
                meshIndex = document.Nodes[placementNode].MeshIndex!.Value;
                primitiveIndex = 0;
            }

            var mesh = document.Meshes[meshIndex];
            Assert.True(primitiveIndex < mesh.Primitives.Count,
                $"Part {ordinal} '{part.Name}' expects primitive {primitiveIndex} of mesh {meshIndex}, which has " +
                $"{mesh.Primitives.Count}.");
            var primitive = mesh.Primitives[primitiveIndex];
            var material = NifMaterialPreparation.Prepare(part.Submesh, resolver, prepared,
                StarfieldGlbVertexLerpProjection.Resolve(part.Submesh));
            CheckGeometry(ordinal, part, primitive, material, cancellationToken);
            CheckMaterial(ordinal, part, primitive, material, document, rowByKey, keyByRow);
            mapping.Add(meshIndex, primitiveIndex, ordinal, part.Submesh.Tangents is not null);
        }

        foreach (var (nodeIndex, parts) in primitivesByNode)
        {
            var meshIndex = document.Nodes[nodeIndex].MeshIndex!.Value;
            Assert.True(document.Meshes[meshIndex].Primitives.Count == parts,
                $"Node {nodeIndex} owns {parts} drawable rigid part(s) but its mesh has " +
                $"{document.Meshes[meshIndex].Primitives.Count} primitive(s).");
        }

        Assert.Equal(drawable, document.Meshes.Sum(static mesh => mesh.Primitives.Count));
        Assert.Equal(rowByKey.Count, document.Materials.Count);
        Assert.Equal(NifCorpusLegacyTriangles.RepeatedPositions(source, resolver),
            NifCorpusLegacyTriangles.RepeatedPositions(document));
        return mapping;
    }

    /// <summary>Checks one primitive's vertices, indices, tangents and skin presence exactly.</summary>
    private static void CheckGeometry(int ordinal, GlbMeshPart part, ScenePrimitive primitive,
        NifPreparedMaterial material, CancellationToken cancellationToken)
    {
        var label = string.Create(CultureInfo.InvariantCulture, $"part {ordinal} '{part.Name}'");
        var expectedName = AuthoredSkyGlbPreviewProjection.AppliesTo(part.Submesh)
            ? part.Name + AuthoredSkyGlbPreviewProjection.NameSuffix
            : part.Name;
        Assert.Equal(expectedName, primitive.Name);
        var clone = RenderableSubmeshCloner.DeepClone(part.Submesh);
        GlbWriter.NormalizeWinding(clone);
        var vertexCount = part.Submesh.VertexCount;
        Assert.True(vertexCount == primitive.Vertices.Count,
            $"The {label} has {vertexCount} source vertices and {primitive.Vertices.Count} normalized vertices.");
        var projection = material.Key.StarfieldVertexLerpProjection;
        for (var index = 0; index < vertexCount; index++)
        {
            if ((index & 1023) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var vertex = primitive.Vertices[index];
            var position = GltfCoordinateAdapter.ConvertPosition(new Vector3(clone.Positions[index * 3],
                clone.Positions[index * 3 + 1], clone.Positions[index * 3 + 2]));
            Require(vertex.Position.Equals(position), label, index, "position", position, vertex.Position);
            var normal = GlbWriter.ReadNormal(clone, index);
            Require(vertex.Normal.Equals(normal), label, index, "normal", normal, vertex.Normal);
            var color = GlbWriter.ReadVertexColor(clone, index, projection);
            Require(vertex.Color.Equals(color), label, index, "color", color, vertex.Color);
            var texCoord = TexCoord(clone, index);
            Require(vertex.TexCoord.Equals(texCoord), label, index, "texture coordinate", texCoord,
                vertex.TexCoord);
        }

        Assert.True(clone.Triangles.Length == primitive.Indices.Count,
            $"The {label} has {clone.Triangles.Length} source indices and {primitive.Indices.Count} normalized.");
        for (var index = 0; index < clone.Triangles.Length; index++)
        {
            if (clone.Triangles[index] != primitive.Indices[index])
            {
                Assert.Fail($"The {label} index {index} is {primitive.Indices[index]}; the winding-normalized " +
                            $"source index is {clone.Triangles[index]}.");
            }
        }

        if (material.NormalImage is not null)
        {
            Assert.True(primitive.Tangents is not null, $"The normal-mapped {label} lost its tangents.");
            var built = NpcGlbTangentBuilder.BuildTangents(clone);
            for (var index = 0; index < vertexCount; index++)
            {
                var tangent = GlbWriter.ReadTangent(clone, built, index);
                Require(primitive.Tangents.Values[index].Equals(tangent), label, index, "tangent", tangent,
                    primitive.Tangents.Values[index]);
            }
        }
        else if (NifNeutralSceneAdapter.HasAuthoredTangents(clone))
        {
            // Authored tangents take the native builder path too, so they must equal the native writer's.
            Assert.True(primitive.Tangents is not null, $"The {label} authored tangents that were dropped.");
            var built = NpcGlbTangentBuilder.BuildTangents(clone);
            for (var index = 0; index < vertexCount; index++)
            {
                var tangent = GlbWriter.ReadTangent(clone, built, index);
                Require(primitive.Tangents.Values[index].Equals(tangent), label, index, "authored tangent", tangent,
                    primitive.Tangents.Values[index]);
            }
        }
        else
        {
            Assert.True(primitive.Tangents is null,
                $"The {label} is neither normal-mapped nor tangent-authored, yet carries tangents.");
        }

        var skinned = part.Skin is { } skin && skin.PerVertexInfluences.Any(static influences => influences.Length > 0);
        Assert.Equal(skinned, primitive.SkinInfluences is not null);
    }

    /// <summary>Checks that one prepared key owns exactly one material row carrying the prepared values.</summary>
    private static void CheckMaterial(int ordinal, GlbMeshPart part, ScenePrimitive primitive,
        NifPreparedMaterial material, ModelDocument document, Dictionary<NifMaterialCacheKey, int> rowByKey,
        Dictionary<int, NifMaterialCacheKey> keyByRow)
    {
        Assert.True(primitive.MaterialIndex is not null, $"Part {ordinal} '{part.Name}' has no material row.");
        var row = primitive.MaterialIndex.Value;
        if (rowByKey.TryGetValue(material.Key, out var existing))
        {
            Assert.True(existing == row,
                $"Part {ordinal} '{part.Name}' shares a prepared key with row {existing} but binds row {row}.");
            return;
        }

        Assert.False(keyByRow.ContainsKey(row),
            $"Part {ordinal} '{part.Name}' binds row {row}, which already carries a different prepared key.");
        rowByKey.Add(material.Key, row);
        keyByRow.Add(row, material.Key);
        var actual = document.Materials[row];
        Assert.Equal(material.Name, actual.Name);
        Assert.Equal(material.Unlit, actual.Unlit);
        Assert.Equal(material.DoubleSided, actual.DoubleSided);
        Assert.Equal(material.AlphaMode, actual.AlphaMode);
        Assert.Equal(material.AlphaCutoff, actual.AlphaCutoff);
        Assert.Equal(material.BaseColor, actual.BaseColor);
        Assert.Equal(material.BaseColorImage is not null, actual.Texture is not null);
        Assert.Equal(material.NormalImage is not null, actual.NormalTexture is not null);
        Assert.Equal(material.MetallicRoughnessImage is not null, actual.MetallicRoughnessTexture is not null);
        Assert.Equal(material.SpecularImage is not null, actual.SpecularTexture is not null);
        Assert.Equal(material.OcclusionImage is not null, actual.OcclusionTexture is not null);
        Assert.Equal(material.EmissiveImage is not null, actual.EmissiveTexture is not null);
        Assert.Equal(material.NormalScale, actual.NormalScale);
        Assert.Equal(material.MetallicFactor, actual.MetallicFactor);
        Assert.Equal(material.RoughnessFactor, actual.RoughnessFactor);
        Assert.Equal(material.SpecularFactor, actual.SpecularFactor);
        Assert.Equal(material.OcclusionStrength, actual.OcclusionStrength);
        Assert.Equal(material.EmissiveFactor, actual.EmissiveFactor);
        Assert.Equal(material.EmissiveStrength, actual.EmissiveStrength);
    }

    /// <summary>Reads each skin placement node's source part ordinal from its provenance extras.</summary>
    private static Dictionary<int, int> SkinPlacementNodes(GlbScene source, ModelDocument document)
    {
        var placements = new Dictionary<int, int>();
        for (var index = source.Nodes.Count; index < document.Nodes.Count; index++)
        {
            var extras = document.Nodes[index].ExtrasJson;
            if (extras is null)
            {
                continue;
            }

            using var metadata = JsonDocument.Parse(extras);
            if (metadata.RootElement.TryGetProperty("bethesdaNifSkinPlacement", out var provenance) &&
                provenance.TryGetProperty("meshPartOrdinal", out var ordinal))
            {
                placements[ordinal.GetInt32()] = index;
            }
        }

        return placements;
    }

    /// <summary>Reads a UV pair exactly as the adapter does, defaulting to the origin when none was authored.</summary>
    private static Vector2 TexCoord(RenderableSubmesh submesh, int vertexIndex)
    {
        if (submesh.UVs is null)
        {
            return Vector2.Zero;
        }

        var offset = vertexIndex * 2;
        return offset + 1 >= submesh.UVs.Length
            ? Vector2.Zero
            : new Vector2(submesh.UVs[offset], submesh.UVs[offset + 1]);
    }

    /// <summary>Fails with one precise per-vertex message when an exact check does not hold.</summary>
    private static void Require<T>(bool holds, string label, int vertex, string attribute, T expected, T actual)
    {
        if (!holds)
        {
            Assert.Fail(string.Create(CultureInfo.InvariantCulture,
                $"The {label} vertex {vertex} {attribute} is {actual}; the source derives {expected}."));
        }
    }
}
