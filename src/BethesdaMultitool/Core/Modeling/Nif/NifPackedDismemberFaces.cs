using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json.Nodes;
using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     The dismember body parts of packed skinned geometry as Face-domain streams (plan section 3, "Skin"): the packed
///     primitive's triangles are each partition's triangles in partition order
///     (<see cref="NifPackedGeometryReader" />), so face f belongs to the partition whose kept-triangle range holds f
///     and takes that partition's BodyPartList entry. No triangle matching is needed, unlike the inline form
///     (<see cref="NifModelDismemberFaces" />), whose triangle keys live in the shape vertex domain. The stream names,
///     semantics and encoding are the inline form's.
/// </summary>
internal static class NifPackedDismemberFaces
{
    /// <summary>The rule recorded in native state.</summary>
    public const string Rule =
        "packed partition order: face f takes the body part of the partition whose kept triangles include f";

    /// <summary>Builds the faces and streams over the packed primitive's triangles.</summary>
    /// <param name="triangles">The primitive's kept triangle indices (three per triangle), in partition order.</param>
    /// <param name="partitions">The checked partitions that gave the order.</param>
    /// <param name="bodyParts">The dismember body-part list (entry k for partition k).</param>
    /// <param name="cancellationToken">Observed per partition.</param>
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
            ["rule"] = Rule,
            ["partitions"] = partitions.Count,
            ["bodyPartEntries"] = bodyParts.Count
        };
        if (bodyParts.Count != partitions.Count)
        {
            return (null, none, string.Create(CultureInfo.InvariantCulture,
                $"the BSDismemberSkinInstance lists {bodyParts.Count} body part(s) for {partitions.Count} " +
                $"partition(s)"), facts);
        }

        var faceCount = triangles.Count / 3;
        var bodyPartValues = new ushort[faceCount];
        var partFlagValues = new ushort[faceCount];
        var face = 0;
        foreach (var partition in partitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var kept = partition.Triangles?.KeptTriangles ?? 0;
            if (face + kept > faceCount)
            {
                return (null, none, string.Create(CultureInfo.InvariantCulture,
                    $"the partitions keep more triangles ({face + kept} so far) than the primitive holds ({faceCount})"),
                    facts);
            }

            for (var f = 0; f < kept; f++, face++)
            {
                bodyPartValues[face] = bodyParts[partition.Ordinal].BodyPart;
                partFlagValues[face] = bodyParts[partition.Ordinal].PartFlag;
            }
        }

        facts["faces"] = faceCount;
        facts["partitionTriangles"] = face;
        if (face != faceCount)
        {
            return (null, none, string.Create(CultureInfo.InvariantCulture,
                $"the partitions keep {face} triangle(s) but the primitive holds {faceCount}"), facts);
        }

        var faces = new SceneFaceList(Enumerable.Repeat(3, faceCount), triangles);
        SceneAttributeStream[] streams =
        [
            Stream(NifModelDismemberFaces.BodyPartAttribute, NifModelDismemberFaces.BodyPartSemantic, bodyPartValues),
            Stream(NifModelDismemberFaces.PartFlagAttribute, NifModelDismemberFaces.PartFlagSemantic, partFlagValues)
        ];
        facts["streams"] = new JsonArray(NifModelDismemberFaces.BodyPartAttribute,
            NifModelDismemberFaces.PartFlagAttribute);
        return (faces, streams, null, facts);
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
}
