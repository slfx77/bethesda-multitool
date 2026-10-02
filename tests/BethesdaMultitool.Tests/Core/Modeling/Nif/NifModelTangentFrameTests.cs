using System.Numerics;
using System.Text.Json.Nodes;
using BethesdaMultitool.Core.Modeling;
using BethesdaMultitool.Core.Modeling.Nif;
using BethesdaMultitool.Tests.Helpers;
using Slfx77.Multitool.Core.Models;
using Xunit;
using static BethesdaMultitool.Tests.Core.Modeling.Nif.NifModelTestSupport;

namespace BethesdaMultitool.Tests.Core.Modeling.Nif;

/// <summary>
///     The stored NIF tangent frame lowered to <see cref="SceneTangents" /> as glTF defines it (the Shared contract:
///     TANGENT.xyz along increasing U, w so that cross(N, xyz) * w is glTF's bitangent), per source kind the reader
///     types: PC inline arrays (NiTriShapeData and NiTriStripsData), the same arrays inside a big-endian file, and the six
///     console packed layouts (half and float frames, static and skinned). Each fixture is a flat quad in z = 0 with
///     normal +Z whose derivatives are known without reading any tangent: upright UVs (x, 1 - y) give dP/du = +X and
///     dP/dv = -Y, UVs mirrored in U (1 - x, 1 - y) give dP/du = -X. The stored arrays are written the way the measured
///     retail files store them (TestOutput/nif-tangent-frame-20260928/MEASURE.md): the array nif.xml calls "Bitangents"
///     (the packed Bitangent channel, the lower offset) holds dP/du and "Tangents" (the packed Tangent channel) holds
///     dP/dv.
/// </summary>
/// <remarks>
///     <para>
///         Convention (glTF's): TEXCOORD is top-left origin with V down the image, NIF UVs pass through unflipped and
///         glTF's normal texture is +Y up, so the bitangent must run along -dP/dv and the handedness the UVs ask for is
///         sign(dot(cross(N, dP/du), -dP/dv)): +1 on the upright quad and -1 on the mirrored one. Shared's validator only
///         requires a unit xyz and w of exactly +1 or -1, so it cannot decide this; <see cref="NifUvTangentFrame" /> is the
///         independent judge, cross-checked here against the known derivatives.
///     </para>
///     <para>
///         The pin, for every vertex: xyz is the stored dP/du array bit for bit, it runs along +dP/du, w equals the UV
///         handedness, and glTF's rebuilt bitangent runs up the image. Controls: the reader's previous mapping (xyz = the
///         stored "Tangents", w = sign(dot(cross(N, T), B))) fails the pin on every vertex, although its w is
///         algebraically the same, because its xyz runs along dP/dv; and the U-mirrored quad flips w (and the xyz) on
///         every vertex, so a constant or UV-blind w cannot pass both quads.
///     </para>
/// </remarks>
public class NifModelTangentFrameTests
{
    private static readonly Dictionary<string, string> X360 = new() { [BethesdaModelRegistration.PlatformOption] = "x360" };

    /// <summary>The quad's corners in the z = 0 plane (every value an exact half).</summary>
    private static readonly float[] QuadPositions = [0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f, 1f, 1f, 0f];

    private static readonly ushort[] QuadTriangles = [0, 1, 2, 1, 3, 2];

    /// <summary>The packed vertices of <see cref="NifPackedSkinFixture.TwoPartitions" />, as quad corners.</summary>
    private static readonly int[] SkinnedPoints = [0, 1, 2, 2, 3, 1];

    /// <summary>The known dP/dv of both quads.</summary>
    private static readonly Vector3 KnownDpDv = -Vector3.UnitY;

    public static TheoryData<string> Layouts => new(NifTestPackedLayouts.Ids);

    /// <summary>
    ///     PC inline arrays, little- and big-endian, list and strip triangles: the pin holds on both quads, the U mirror
    ///     flips w on every vertex, and the previous mapping fails the pin on every vertex. The stored arrays stay in the
    ///     primitive row under their nif.xml names, with the mapping named.
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void InlineFrame_TangentRunsAlongDpDu_WithGltfHandedness(bool bigEndian, bool strips)
    {
        var label = $"inline {(bigEndian ? "BE" : "LE")} {(strips ? "NiTriStripsData" : "NiTriShapeData")}";
        var upright = Read(InlineQuad(bigEndian, strips, mirrorU: false)).Document;
        var mirrored = Read(InlineQuad(bigEndian, strips, mirrorU: true)).Document;

        foreach (var (document, mirrorU) in new[] { (upright, false), (mirrored, true) })
        {
            var primitive = PrimitiveOf(document, "Shape");
            var frame = AssertGltfFrame(primitive, mirrorU, label);
            var values = primitive.Tangents!.Values;
            var stored = StoredU(mirrorU, 4);
            Assert.Equal(Bits(stored), values.SelectMany(t => Bits([t.X, t.Y, t.Z])).ToArray());

            var old = PreviousMapping(Flat(primitive.Vertices.Select(v => v.Normal)), StoredV(4), stored);
            Assert.Equal(values.Count, PinFailures(frame, old));
            Assert.Equal(values.Select(t => t.W), old.Select(t => t.W));

            var payload = PrimitivePayload(document, 0);
            var tangents = payload["tangents"]!;
            Assert.True((bool)tangents["typed"]!);
            Assert.Equal("Bitangents", (string)tangents["xyz"]!);
            Assert.Equal("Tangents", (string)tangents["handednessFrom"]!);
            Assert.Equal(NifModelGeometryData.TangentMappingRule, (string)tangents["mapping"]!);
            Assert.Equal(NifModelGeometryData.HandednessRule, (string)tangents["rule"]!);
            Assert.Equal(mirrorU ? 0 : 4, (int)tangents["handedness"]!["positive"]!);
            Assert.Equal(mirrorU ? 4 : 0, (int)tangents["handedness"]!["negative"]!);
            var frameRow = payload["storedTangentFrame"]!;
            Assert.Equal(Bits(StoredV(4)), Bits(Components(frameRow["Tangents"]!)));
            Assert.Equal(Bits(stored), Bits(Components(frameRow["Bitangents"]!)));
            Assert.Null(payload["bitangents"]);
            SceneValidation.ValidateStructure(document);
        }

        AssertMirrorFlips(PrimitiveOf(upright, "Shape"), PrimitiveOf(mirrored, "Shape"), label);
    }

    /// <summary>
    ///     The six console packed layouts: the Bitangent channel (the lower-offset frame stream) is the tangent xyz,
    ///     widened exactly, and the pin holds on both quads; the U mirror flips w on every vertex, and the previous
    ///     mapping (the Tangent channel as xyz) fails the pin on every vertex. The primitive row names both channels and
    ///     their offsets.
    /// </summary>
    [Theory]
    [MemberData(nameof(Layouts))]
    public void PackedFrame_TheBitangentChannel_RunsAlongDpDu_WithGltfHandedness(string layout)
    {
        var skinned = NifTestPackedLayouts.IsSkinned(layout);
        var shape = skinned ? "Body" : "Shape";
        var upright = Read(PackedQuad(layout, mirrorU: false), X360).Document;
        var mirrored = Read(PackedQuad(layout, mirrorU: true), X360).Document;

        foreach (var (document, mirrorU) in new[] { (upright, false), (mirrored, true) })
        {
            var primitive = PrimitiveOf(document, shape);
            var count = skinned ? SkinnedPoints.Length : 4;
            Assert.Equal(count, primitive.Vertices.Count);
            var frame = AssertGltfFrame(primitive, mirrorU, layout);
            var values = primitive.Tangents!.Values;
            var stored = StoredU(mirrorU, count);
            Assert.Equal(Bits(stored), values.SelectMany(t => Bits([t.X, t.Y, t.Z])).ToArray());

            var old = PreviousMapping(Flat(primitive.Vertices.Select(v => v.Normal)), StoredV(count), stored);
            Assert.Equal(values.Count, PinFailures(frame, old));

            var payload = PrimitivePayload(document, 0);
            Assert.Equal("Bitangent channel", (string)payload["tangents"]!["xyz"]!);
            Assert.Equal("Tangent channel", (string)payload["tangents"]!["handednessFrom"]!);
            var offsets = NifTestPackedLayouts.StreamTable(layout).ToDictionary(s => s.Semantic, s => (int)s.Offset);
            Assert.Equal(offsets["bitangent"], (int)payload["storedTangentFrame"]!["bitangentOffset"]!);
            Assert.Equal(offsets["tangent"], (int)payload["storedTangentFrame"]!["tangentOffset"]!);
            Assert.True(offsets["bitangent"] < offsets["tangent"]);
            SceneValidation.ValidateStructure(document);
        }

        AssertMirrorFlips(PrimitiveOf(upright, shape), PrimitiveOf(mirrored, shape), layout);
    }

    /// <summary>
    ///     A NaN in either stored array leaves the basis untyped with a diagnostic naming that array (the handedness
    ///     reads both, so neither may be non-finite), and the primitive row keeps both arrays. Control: the same quad with
    ///     finite arrays is typed.
    /// </summary>
    [Theory]
    [InlineData("Tangents")]
    [InlineData("Bitangents")]
    public void NonFiniteStoredFrame_LeavesTheBasisUntyped_NamingTheArray(string field)
    {
        var result = Read(InlineQuad(false, false, mirrorU: false, nanIn: field));
        var primitive = PrimitiveOf(result.Document, "Shape");

        Assert.Null(primitive.Tangents);
        var diagnostic = Assert.Single(result.Document.Diagnostics,
            d => d.Code == NifModelGeometryData.NonFiniteTangentDiagnostic);
        Assert.Contains($"a stored {field} value is NaN or infinite (element 2", DiagnosticText(diagnostic));
        var payload = PrimitivePayload(result.Document, 0);
        Assert.False((bool)payload["tangents"]!["typed"]!);
        Assert.NotNull(payload["storedTangentFrame"]!["Tangents"]);
        Assert.NotNull(payload["storedTangentFrame"]!["Bitangents"]);
        SceneValidation.ValidateStructure(result.Document);

        var finite = Read(InlineQuad(false, false, mirrorU: false));
        Assert.NotNull(PrimitiveOf(finite.Document, "Shape").Tangents);
        Assert.DoesNotContain(finite.Document.Diagnostics, d => d.Code == NifModelGeometryData.NonFiniteTangentDiagnostic);
    }

    /// <summary>The known dP/du: +X upright, -X mirrored in U.</summary>
    private static Vector3 KnownDpDu(bool mirrorU)
    {
        return mirrorU ? -Vector3.UnitX : Vector3.UnitX;
    }

    /// <summary>glTF's handedness from the known derivatives: sign(dot(cross(N, dP/du), -dP/dv)).</summary>
    private static float KnownGltfW(bool mirrorU)
    {
        return MathF.Sign(Vector3.Dot(Vector3.Cross(Vector3.UnitZ, KnownDpDu(mirrorU)), -KnownDpDv));
    }

    /// <summary>
    ///     The pin on every vertex (see the type remarks), with the judge cross-checked against the known derivatives;
    ///     returns the judge for the controls.
    /// </summary>
    private static NifUvTangentFrame AssertGltfFrame(ScenePrimitive primitive, bool mirrorU, string label)
    {
        var tangents = Assert.IsType<SceneTangents>(primitive.Tangents).Values;
        var frame = NifUvTangentFrame.Compute(primitive.Vertices, primitive.Indices);
        var expectedW = KnownGltfW(mirrorU);
        Assert.Equal(mirrorU ? -1f : 1f, expectedW);
        for (var i = 0; i < tangents.Count; i++)
        {
            var where = $"{label} ({(mirrorU ? "mirrored" : "upright")}) vertex {i}";
            Assert.True(frame.IsValid(i), $"{where}: the judge classes it {frame.Classes[i]}.");
            Assert.True(Vector3.Dot(frame.DirectionOfU(i), KnownDpDu(mirrorU)) > 0.9999f, $"{where}: judge dP/du.");
            Assert.True(Vector3.Dot(frame.DirectionOfV(i), KnownDpDv) > 0.9999f, $"{where}: judge dP/dv.");
            Assert.Equal((int)expectedW, frame.GltfW(i));

            var t = tangents[i];
            var xyz = new Vector3(t.X, t.Y, t.Z);
            Assert.True(Vector3.Dot(Vector3.Normalize(xyz), KnownDpDu(mirrorU)) > 0.9999f,
                $"{where}: TANGENT.xyz {xyz} does not run along dP/du {KnownDpDu(mirrorU)}.");
            Assert.True(frame.CosineToU(i, xyz) > NifUvTangentFrame.Align, $"{where}: judge cosine.");
            Assert.Equal(expectedW, t.W);
            Assert.True(Vector3.Dot(frame.GltfBitangent(i, t), -KnownDpDv) > 0.9999f,
                $"{where}: glTF's bitangent cross(N, T) * w does not run up the image.");
        }

        Assert.Equal(0, PinFailures(frame, tangents));
        return frame;
    }

    /// <summary>The vertices whose lane misses the pin: not along +dP/du, or a w other than the UV handedness.</summary>
    private static int PinFailures(NifUvTangentFrame frame, IReadOnlyList<Vector4> lanes)
    {
        var failures = 0;
        for (var i = 0; i < lanes.Count; i++)
        {
            var along = frame.CosineToU(i, new Vector3(lanes[i].X, lanes[i].Y, lanes[i].Z)) > NifUvTangentFrame.Align;
            if (!along || (int)lanes[i].W != frame.GltfW(i))
            {
                failures++;
            }
        }

        return failures;
    }

    /// <summary>Control: every vertex's w and xyz flip between the upright and the U-mirrored quad.</summary>
    private static void AssertMirrorFlips(ScenePrimitive upright, ScenePrimitive mirrored, string label)
    {
        var a = upright.Tangents!.Values;
        var b = mirrored.Tangents!.Values;
        Assert.Equal(a.Count, b.Count);
        for (var i = 0; i < a.Count; i++)
        {
            Assert.True(b[i].W == -a[i].W, $"{label}: the U mirror left w at {b[i].W} on vertex {i}.");
            Assert.True(b[i].X == -a[i].X, $"{label}: the U mirror did not reverse the tangent on vertex {i}.");
        }
    }

    /// <summary>The reader's mapping before 2026-09-28: xyz = the stored dP/dv array, w = sign(dot(cross(N, T), B)).</summary>
    private static List<Vector4> PreviousMapping(float[] normals, float[] storedV, float[] storedU)
    {
        var lanes = new List<Vector4>();
        for (var i = 0; i < normals.Length / 3; i++)
        {
            var n = new Vector3(normals[i * 3], normals[i * 3 + 1], normals[i * 3 + 2]);
            var t = new Vector3(storedV[i * 3], storedV[i * 3 + 1], storedV[i * 3 + 2]);
            var b = new Vector3(storedU[i * 3], storedU[i * 3 + 1], storedU[i * 3 + 2]);
            lanes.Add(new Vector4(t, Vector3.Dot(Vector3.Cross(n, t), b) < 0 ? -1f : 1f));
        }

        return lanes;
    }

    /// <summary>The stored dP/du array ("Bitangents" / the Bitangent channel) of <paramref name="count" /> vertices.</summary>
    private static float[] StoredU(bool mirrorU, int count)
    {
        return Repeat(KnownDpDu(mirrorU), count);
    }

    /// <summary>The stored dP/dv array ("Tangents" / the Tangent channel) of <paramref name="count" /> vertices.</summary>
    private static float[] StoredV(int count)
    {
        return Repeat(KnownDpDv, count);
    }

    /// <summary>UV set 0 of the given quad corners: (x, 1 - y), or (1 - x, 1 - y) mirrored in U.</summary>
    private static float[] Uvs(IEnumerable<int> corners, bool mirrorU)
    {
        return corners.SelectMany(c =>
        {
            var x = QuadPositions[c * 3];
            var y = QuadPositions[c * 3 + 1];
            return new[] { mirrorU ? 1f - x : x, 1f - y };
        }).ToArray();
    }

    /// <summary>
    ///     The quad as inline streams: a NiTriShape + NiTriShapeData triangle list, or a NiTriStrips + NiTriStripsData
    ///     strip [0, 1, 2, 3] (the same two triangles by the parity rule); optionally a NaN at element 2 of one array.
    /// </summary>
    private static byte[] InlineQuad(bool bigEndian, bool strips, bool mirrorU, string? nanIn = null)
    {
        var tangents = StoredV(4);
        var bitangents = StoredU(mirrorU, 4);
        if (nanIn is not null)
        {
            (nanIn == "Tangents" ? tangents : bitangents)[2 * 3 + 1] = float.NaN;
        }

        var streams = new NifTestGeometryStreams
        {
            Vertices = QuadPositions,
            Normals = Repeat(Vector3.UnitZ, 4),
            Uvs = Uvs(Enumerable.Range(0, 4), mirrorU),
            Tangents = tangents,
            Bitangents = bitangents
        };
        if (!strips)
        {
            return SingleTriShape(streams, QuadTriangles, bigEndian);
        }

        var builder = new NifTestFileBuilder(bigEndian, 34);
        AddNode(builder, builder.AddString("Root"), [1]);
        AddTriStrips(builder, builder.AddString("Shape"), 2);
        AddTriStripsData(builder, streams, [[0, 1, 2, 3]]);
        return builder.Build();
    }

    /// <summary>
    ///     The quad in a packed layout: static layouts through <see cref="NifPackedFixture" /> (four vertices, the triangle
    ///     list), skinned ones through <see cref="NifPackedSkinFixture" /> (the two partitions' six packed vertices, with
    ///     <see cref="NifPackedSkinFixture.SixVertices" />' weights and bone indices).
    /// </summary>
    private static byte[] PackedQuad(string layout, bool mirrorU)
    {
        if (!NifTestPackedLayouts.IsSkinned(layout))
        {
            var data = new NifTestPackedVertexData
            {
                Positions = QuadPositions,
                Normals = Repeat(Vector3.UnitZ, 4),
                Uvs = Uvs(Enumerable.Range(0, 4), mirrorU),
                Bitangents = StoredU(mirrorU, 4),
                Tangents = StoredV(4),
                Colors = NifTestPackedLayouts.HasColors(layout) ? NifPackedFixture.QuadColors : null
            };
            return new NifPackedFixture { Layout = layout, Data = data }.Build();
        }

        var six = NifPackedSkinFixture.SixVertices();
        var skinned = new NifTestPackedVertexData
        {
            Positions = SkinnedPoints.SelectMany(p => QuadPositions.Skip(p * 3).Take(3)).ToArray(),
            Normals = Repeat(Vector3.UnitZ, SkinnedPoints.Length),
            Uvs = Uvs(SkinnedPoints, mirrorU),
            Bitangents = StoredU(mirrorU, SkinnedPoints.Length),
            Tangents = StoredV(SkinnedPoints.Length),
            Colors = NifTestPackedLayouts.HasColors(layout) ? Enumerable.Repeat((byte)255, 24).ToArray() : null,
            Weights = six.Weights,
            BoneIndices = six.BoneIndices
        };
        return new NifPackedSkinFixture { Layout = layout, Data = skinned }.Build();
    }

    private static float[] Repeat(Vector3 value, int count)
    {
        return Enumerable.Range(0, count).SelectMany(_ => new[] { value.X, value.Y, value.Z }).ToArray();
    }

    private static float[] Flat(IEnumerable<Vector3> values)
    {
        return values.SelectMany(v => new[] { v.X, v.Y, v.Z }).ToArray();
    }

    /// <summary>The components of a native array value written element-major (an array of arrays of numbers).</summary>
    private static float[] Components(JsonNode node)
    {
        return node.AsArray().SelectMany(element => element!.AsArray().Select(component => (float)component!)).ToArray();
    }

    private static uint[] Bits(IEnumerable<float> values)
    {
        return values.Select(BitConverter.SingleToUInt32Bits).ToArray();
    }
}
