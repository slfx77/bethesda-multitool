using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Geometry;
using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.Modeling.Starfield;

/// <summary>
///     Builds the geometry of a Starfield <c>.mesh</c> document (cut-2 plan section 3.2): the main primitive with its
///     vertices, triangles, tangents, second UV set and typed source streams, and one primitive per non-empty LOD list
///     sharing every buffer of the main one (<see cref="ScenePrimitive.WithTriangleIndices" />).
/// </summary>
/// <remarks>
///     <list type="bullet">
///         <item>
///             Position: <see cref="StarfieldMeshFile.PositionMeters" /> of each stored component (one rounding, plan
///             decision D5). Normal and tangent: <see cref="StarfieldMeshFile.Dec4Channel" /> per channel, unnormalized,
///             the zero sentinel kept (D6); tangent w is glTF's handedness, -1 for code 3 and +1 for code 0
///             (<see cref="TangentSign" />; <see cref="StarfieldMeshModelFacts" /> refuses 1 and 2). Without normals the
///             primitive is flat-shaded (no retail file lacks them).
///         </item>
///         <item>
///             UV0 is <see cref="SceneVertex.TexCoord" /> and UV1 the first additional set, each the half value exactly; a
///             non-finite component reads 0 and the whole set's half bits are kept in <c>starfield.uv{n}.raw</c> (the NIF
///             reader's rule, in half bits).
///         </item>
///         <item>
///             Colors are the non-primary stream <see cref="ColorAttribute" /> (UInt8 x4 normalized, RGBA, color space
///             Unknown; D4); <see cref="SceneVertex.Color" /> stays white. Weights are the per-slot streams
///             <see cref="BoneAttributePrefix" /><c>k</c> and <see cref="WeightAttributePrefix" /><c>k</c> (UInt16 x1, the
///             weight normalized; D3), values as stored.
///         </item>
///     </list>
///     Stream order on the primitive: <c>starfield.uv0.raw</c>, <c>starfield.uv1.raw</c> (each only when needed), the
///     color stream, then bone 0, weight 0, bone 1, weight 1 and so on.
/// </remarks>
internal static class StarfieldMeshModelGeometry
{
    /// <summary>The color stream's name.</summary>
    public const string ColorAttribute = "starfield.color";

    /// <summary>The color stream's semantic.</summary>
    public const string ColorSemantic = "bmt.starfield.vertex-color";

    /// <summary>The evidence the color stream's Unknown color space carries.</summary>
    public const string ColorEvidence =
        "the referencing NIF's material decides whether vertex color tints (Starfield material database); 79% of " +
        "retail color streams are saturated masks (271,275 of 343,029 files have every RGB channel 0 or 255)";

    /// <summary>The bone-index stream name prefix; the slot number follows (<c>starfield.bone.0</c>).</summary>
    public const string BoneAttributePrefix = "starfield.bone.";

    /// <summary>The weight stream name prefix; the slot number follows (<c>starfield.weight.0</c>).</summary>
    public const string WeightAttributePrefix = "starfield.weight.";

    /// <summary>The bone-index streams' semantic.</summary>
    public const string BoneSemantic = "bmt.starfield.bone-index";

    /// <summary>The weight streams' semantic.</summary>
    public const string WeightSemantic = "bmt.starfield.bone-weight";

    /// <summary>The raw UV stream name prefix; the set number and <c>.raw</c> follow (<c>starfield.uv0.raw</c>).</summary>
    public const string RawUvAttributePrefix = "starfield.uv";

    /// <summary>The raw UV streams' semantic.</summary>
    public const string RawUvSemantic = "bmt.starfield.texture-coordinate-half-bits";

    /// <summary>The raw UV stream name of a set (<c>starfield.uv0.raw</c>).</summary>
    public static string RawUvAttribute(int set)
    {
        return string.Create(CultureInfo.InvariantCulture, $"{RawUvAttributePrefix}{set}.raw");
    }

    /// <summary>A packed Dec4 word's three channels by the correctly rounded route.</summary>
    public static Vector3 Dec4Vector(uint code)
    {
        return new Vector3(StarfieldMeshFile.Dec4Channel(code), StarfieldMeshFile.Dec4Channel(code >> 10),
            StarfieldMeshFile.Dec4Channel(code >> 20));
    }

    /// <summary>
    ///     A tangent word's handedness as <see cref="SceneTangents" /> defines it (glTF's): -1 for W code 3 and +1 for W
    ///     code 0 (the reader refused 1 and 2).
    /// </summary>
    /// <remarks>
    ///     The stored code is the engine's bitangent sign: read as +1 for code 3 and -1 for code 0, it puts
    ///     <c>cross(N, T) x w</c> along +dP/dv of the stored DirectX UVs on 301,980 of the 314,182 UV-framed cover
    ///     vertices (the cut-2 expectations' frame counts; plan section 0.2: 94.25% of a 3,604-file sample). glTF's bitangent is tangent-space +Y, the top of
    ///     the image, which for its top-left UV origin (the stored UVs reach <c>TEXCOORD_0</c> unflipped) is -dP/dv, so
    ///     the typed w is the stored sign negated. With a Down (-Y) normal map flipped to glTF's +Y green, the engine
    ///     sign would invert the relief twice. The stored codes stay in the header row's <c>tangentW</c> census.
    /// </remarks>
    public static float TangentSign(uint code)
    {
        return code >> 30 == 3 ? -1f : 1f;
    }

    /// <summary>A stored half as a portable coordinate: its value, or 0 when it is not finite.</summary>
    public static float PortableHalf(ushort bits)
    {
        var value = (float)BitConverter.UInt16BitsToHalf(bits);
        return float.IsFinite(value) ? value : 0f;
    }

    /// <summary>Builds the main primitive and the LOD primitives.</summary>
    /// <param name="mesh">The parse of the whole stream.</param>
    /// <param name="facts">The reader's checks and facts over it.</param>
    /// <param name="name">The primitive name (the document name).</param>
    /// <param name="cancellationToken">Observed every 1,024 vertices.</param>
    public static StarfieldMeshModelGeometryResult Build(StarfieldMeshFile mesh, StarfieldMeshModelFacts facts,
        string name, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(name);
        var count = facts.VertexCount;
        var q = mesh.QuantizedPositions;
        var normals = mesh.NormalCodes;
        var uv0 = mesh.Uv0Bits;
        var vertices = new SceneVertex[count];
        for (var i = 0; i < count; i++)
        {
            if ((i & 1023) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var position = new Vector3(StarfieldMeshFile.PositionMeters(q[i * 3], mesh.Scale),
                StarfieldMeshFile.PositionMeters(q[i * 3 + 1], mesh.Scale),
                StarfieldMeshFile.PositionMeters(q[i * 3 + 2], mesh.Scale));
            var normal = normals is null ? Vector3.Zero : Dec4Vector(normals[i]);
            var texCoord = uv0 is null ? Vector2.Zero : new Vector2(PortableHalf(uv0[i * 2]), PortableHalf(uv0[i * 2 + 1]));
            vertices[i] = new SceneVertex(position, normal, Vector4.One, texCoord);
        }

        SceneTangents? tangents = null;
        if (mesh.TangentCodes is { } tangentCodes)
        {
            var values = new Vector4[tangentCodes.Length];
            for (var i = 0; i < values.Length; i++)
            {
                var direction = Dec4Vector(tangentCodes[i]);
                values[i] = new Vector4(direction, TangentSign(tangentCodes[i]));
            }

            tangents = new SceneTangents(values);
        }

        List<SceneTextureCoordinates> additional = [];
        if (mesh.Uv1Bits is { } uv1)
        {
            var values = new Vector2[count];
            for (var i = 0; i < count; i++)
            {
                values[i] = new Vector2(PortableHalf(uv1[i * 2]), PortableHalf(uv1[i * 2 + 1]));
            }

            additional.Add(new SceneTextureCoordinates(values, cancellationToken));
        }

        var flat = normals is null;
        var primary = new ScenePrimitive(name, vertices, ToIndices(mesh.Indices), materialIndex: 0,
            tangents: tangents, additionalTextureCoordinates: additional,
            normalMode: flat ? SceneNormalMode.Flat : SceneNormalMode.Vertex)
        {
            Attributes = SourceStreams(mesh, facts, count),
            NormalProvenance = new SceneNormalProvenance(flat
                ? SceneNormalProvenanceKind.Flat
                : SceneNormalProvenanceKind.Authored),
            Purpose = ScenePrimitivePurpose.Render
        };

        var lods = new ScenePrimitive?[mesh.LodIndexLists.Count];
        for (var lod = 0; lod < lods.Length; lod++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var list = mesh.LodIndexLists[lod];
            lods[lod] = list.Length == 0 ? null : primary.WithTriangleIndices(ToIndices(list));
        }

        return new StarfieldMeshModelGeometryResult(primary, lods);
    }

    /// <summary>The typed source streams, in the order the type remarks give.</summary>
    private static List<SceneAttributeStream> SourceStreams(StarfieldMeshFile mesh, StarfieldMeshModelFacts facts,
        int count)
    {
        var streams = new List<SceneAttributeStream>();
        if (facts.Uv0NonFinite > 0 && mesh.Uv0Bits is { } uv0)
        {
            streams.Add(RawUvStream(0, uv0, count));
        }

        if (facts.Uv1NonFinite > 0 && mesh.Uv1Bits is { } uv1)
        {
            streams.Add(RawUvStream(1, uv1, count));
        }

        if (mesh.ColorBytes is { } stored)
        {
            var rgba = new byte[stored.Length];
            for (var i = 0; i < count; i++)
            {
                rgba[i * 4 + 0] = stored[i * 4 + 2];
                rgba[i * 4 + 1] = stored[i * 4 + 1];
                rgba[i * 4 + 2] = stored[i * 4 + 0];
                rgba[i * 4 + 3] = stored[i * 4 + 3];
            }

            streams.Add(new SceneAttributeStream(ColorAttribute, ColorSemantic, SceneAttributeDomain.Vertex,
                SceneAttributeComponentType.UInt8, 4, count, rgba, normalized: true,
                colorSpace: SceneColorSpace.Unknown, colorSpaceProvenance: SceneValueProvenance.Unknown,
                colorSpaceEvidence: ColorEvidence));
        }

        if (mesh.WeightPairs is { } pairs)
        {
            var slots = mesh.WeightsPerVertex;
            for (var slot = 0; slot < slots; slot++)
            {
                var bones = new byte[count * 2];
                var weights = new byte[count * 2];
                for (var i = 0; i < count; i++)
                {
                    var pair = (i * slots + slot) * 2;
                    BinaryPrimitives.WriteUInt16LittleEndian(bones.AsSpan(i * 2), pairs[pair]);
                    BinaryPrimitives.WriteUInt16LittleEndian(weights.AsSpan(i * 2), pairs[pair + 1]);
                }

                streams.Add(new SceneAttributeStream(
                    string.Create(CultureInfo.InvariantCulture, $"{BoneAttributePrefix}{slot}"), BoneSemantic,
                    SceneAttributeDomain.Vertex, SceneAttributeComponentType.UInt16, 1, count, bones));
                streams.Add(new SceneAttributeStream(
                    string.Create(CultureInfo.InvariantCulture, $"{WeightAttributePrefix}{slot}"), WeightSemantic,
                    SceneAttributeDomain.Vertex, SceneAttributeComponentType.UInt16, 1, count, weights,
                    normalized: true));
            }
        }

        return streams;
    }

    private static SceneAttributeStream RawUvStream(int set, ushort[] bits, int count)
    {
        var bytes = new byte[bits.Length * 2];
        for (var i = 0; i < bits.Length; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2), bits[i]);
        }

        return new SceneAttributeStream(RawUvAttribute(set), RawUvSemantic, SceneAttributeDomain.Vertex,
            SceneAttributeComponentType.UInt16, 2, count, bytes);
    }

    private static int[] ToIndices(ushort[] stored)
    {
        var indices = new int[stored.Length];
        for (var i = 0; i < stored.Length; i++)
        {
            indices[i] = stored[i];
        }

        return indices;
    }
}
