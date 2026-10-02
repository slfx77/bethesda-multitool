using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json.Nodes;
using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     Carries a BSDismemberSkinInstance's per-partition body parts onto the primitive's own triangles (plan section 3,
///     "Skin": face-domain streams over Faces = the triangles). Pure: the partitions are already read and checked.
/// </summary>
/// <remarks>
///     <para>
///         The primitive's triangles come from the NiTriShapeData or NiTriStripsData; the body parts belong to the
///         hardware partitions, which store the same triangles again in partition vertex indices. Each primitive triangle
///         is therefore matched, by its shape vertex indices, to the partition triangles mapped through each partition's
///         vertex map. The match keeps winding: a triangle is keyed by the rotation of its three indices that starts with
///         the smallest, so (a, b, c), (b, c, a) and (c, a, b) match each other and (a, c, b) does not. Matching by
///         ordinal would be wrong, because partitions regroup triangles by bone set.
///     </para>
///     <para>
///         The result is typed only when it is exact: the body-part list has one entry per partition, every partition
///         has a vertex map, and every primitive triangle matches partition triangles whose entries agree. Otherwise
///         nothing is typed, the failure is returned for a diagnostic, and the partitions stay native state. Partition
///         triangles that no primitive triangle uses (for example the partitions' own stitching) are counted only.
///     </para>
///     <para>
///         Output: <see cref="SceneFaceList" /> with one three-corner face per primitive triangle (corners = the
///         primitive's indices), and two non-primary Face-domain UInt16 streams in little-endian order,
///         <see cref="BodyPartAttribute" /> and <see cref="PartFlagAttribute" />, holding each face's Body Part and Part
///         Flag exactly as stored.
///     </para>
/// </remarks>
internal static class NifModelDismemberFaces
{
    /// <summary>The Face-domain stream of BSDismemberBodyPartType values.</summary>
    public const string BodyPartAttribute = "nif.dismember.bodyPart";

    /// <summary>The Face-domain stream of BSPartFlag bits.</summary>
    public const string PartFlagAttribute = "nif.dismember.partFlag";

    /// <summary>The semantic declared for <see cref="BodyPartAttribute" />.</summary>
    public const string BodyPartSemantic = "bmt.nif.dismember-body-part";

    /// <summary>The semantic declared for <see cref="PartFlagAttribute" />.</summary>
    public const string PartFlagSemantic = "bmt.nif.dismember-part-flag";

    /// <summary>The matching rule recorded in native state.</summary>
    public const string MatchRule =
        "each primitive triangle is matched by shape vertex indices, winding kept (rotation starting at the smallest " +
        "index), to the partition triangles mapped through each partition's vertex map; the partition's body-part " +
        "entry supplies the face values";

    /// <summary>Builds the faces and streams, or reports why they cannot be typed exactly.</summary>
    /// <param name="triangles">The primitive's kept triangle indices (three per triangle).</param>
    /// <param name="partitions">The checked partitions.</param>
    /// <param name="bodyParts">The dismember body-part list (entry k for partition k).</param>
    /// <param name="cancellationToken">Observed per partition and per 1024 triangles.</param>
    /// <returns>The faces and streams when exact, else the failure; the facts in both cases.</returns>
    public static (SceneFaceList? Faces, IReadOnlyList<SceneAttributeStream> Streams, string? Failure, JsonObject Facts)
        Build(IReadOnlyList<int> triangles, IReadOnlyList<NifSkinPartitionView> partitions,
            IReadOnlyList<NifSkinBodyPart> bodyParts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(triangles);
        ArgumentNullException.ThrowIfNull(partitions);
        ArgumentNullException.ThrowIfNull(bodyParts);
        var none = Array.Empty<SceneAttributeStream>();
        var facts = new JsonObject
        {
            ["rule"] = MatchRule,
            ["partitions"] = partitions.Count,
            ["bodyPartEntries"] = bodyParts.Count,
            ["bodyParts"] = BodyPartsJson(bodyParts)
        };
        if (bodyParts.Count != partitions.Count)
        {
            return (null, none, string.Create(CultureInfo.InvariantCulture,
                $"the BSDismemberSkinInstance lists {bodyParts.Count} body part(s) for {partitions.Count} " +
                $"partition(s)"), facts);
        }

        // Key -> the partition whose triangle it is; -1 once two partitions with different entries claim it.
        var owners = new Dictionary<(int, int, int), int>();
        var partitionTriangles = 0;
        foreach (var partition in partitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (partition.Triangles is not { } local)
            {
                continue;
            }

            if (partition.VertexMap is not { } map)
            {
                return (null, none, string.Create(CultureInfo.InvariantCulture,
                    $"partition {partition.Ordinal} stores triangles but no vertex map"), facts);
            }

            var indices = local.Indices;
            for (var t = 0; t + 2 < indices.Length; t += 3)
            {
                partitionTriangles++;
                var key = TriangleKey(map[indices[t]], map[indices[t + 1]], map[indices[t + 2]]);
                if (!owners.TryGetValue(key, out var owner))
                {
                    owners.Add(key, partition.Ordinal);
                }
                else if (owner >= 0 && bodyParts[owner] != bodyParts[partition.Ordinal])
                {
                    owners[key] = -1;
                }
            }
        }

        var faceCount = triangles.Count / 3;
        var bodyPartValues = new ushort[faceCount];
        var partFlagValues = new ushort[faceCount];
        var used = new HashSet<(int, int, int)>();
        int unmatched = 0, conflicting = 0, firstUnmatched = -1;
        for (var f = 0; f < faceCount; f++)
        {
            if ((f & 1023) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var key = TriangleKey(triangles[f * 3], triangles[f * 3 + 1], triangles[f * 3 + 2]);
            if (!owners.TryGetValue(key, out var owner))
            {
                unmatched++;
                firstUnmatched = firstUnmatched < 0 ? f : firstUnmatched;
                continue;
            }

            if (owner < 0)
            {
                conflicting++;
                continue;
            }

            used.Add(key);
            bodyPartValues[f] = bodyParts[owner].BodyPart;
            partFlagValues[f] = bodyParts[owner].PartFlag;
        }

        facts["faces"] = faceCount;
        facts["partitionTriangles"] = partitionTriangles;
        facts["unmatchedFaces"] = unmatched;
        facts["conflictingFaces"] = conflicting;
        facts["partitionTrianglesUnusedByFaces"] = owners.Count - used.Count;
        if (unmatched > 0 || conflicting > 0)
        {
            var detail = unmatched > 0
                ? string.Create(CultureInfo.InvariantCulture,
                    $"{unmatched} of {faceCount} triangle(s) match no partition triangle (first: triangle " +
                    $"{firstUnmatched})")
                : string.Create(CultureInfo.InvariantCulture,
                    $"{conflicting} of {faceCount} triangle(s) belong to partitions with different body parts");
            return (null, none, detail, facts);
        }

        var faces = new SceneFaceList(Enumerable.Repeat(3, faceCount), triangles);
        SceneAttributeStream[] streams =
        [
            Stream(BodyPartAttribute, BodyPartSemantic, bodyPartValues),
            Stream(PartFlagAttribute, PartFlagSemantic, partFlagValues)
        ];
        facts["streams"] = new JsonArray(BodyPartAttribute, PartFlagAttribute);
        return (faces, streams, null, facts);
    }

    /// <summary>The winding-preserving key: the rotation of (a, b, c) that starts with the smallest index.</summary>
    internal static (int, int, int) TriangleKey(int a, int b, int c)
    {
        if (a <= b && a <= c)
        {
            return (a, b, c);
        }

        return b <= c ? (b, c, a) : (c, a, b);
    }

    private static SceneAttributeStream Stream(string name, string semantic, ushort[] values)
    {
        var bytes = new byte[values.Length * 2];
        for (var i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2), values[i]);
        }

        return new SceneAttributeStream(name, semantic, SceneAttributeDomain.Face, SceneAttributeComponentType.UInt16,
            1, values.Length, bytes);
    }

    private static JsonNode BodyPartsJson(IReadOnlyList<NifSkinBodyPart> bodyParts)
    {
        if (bodyParts.Count > NifModelNativeValues.MaximumInlineElements)
        {
            return new JsonObject
            {
                ["count"] = bodyParts.Count,
                ["listed"] = "see the BSDismemberSkinInstance block row"
            };
        }

        return new JsonArray(bodyParts.Select(part => (JsonNode?)new JsonObject
        {
            ["partFlag"] = part.PartFlag,
            ["bodyPart"] = part.BodyPart
        }).ToArray());
    }
}
