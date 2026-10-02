using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using BethesdaMultitool.Core.Formats.Nif.Decoding;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Schema;
using BethesdaMultitool.Core.Modeling.Nif;
using Slfx77.Multitool.Core.Models;
using Xunit;
using static BethesdaMultitool.Tests.Core.Modeling.Nif.NifModelTestSupport;
using static BethesdaMultitool.Tests.Core.Modeling.RealAsset.NifModelOracleSupport;

namespace BethesdaMultitool.Tests.Core.Modeling.RealAsset;

/// <summary>
///     The console-against-PC geometry comparison shared by <see cref="NifPackedGeometryOracleTests" /> (the
///     hand-picked layout rows) and <see cref="NifConsoleGeometryOracleTests" /> (every big-endian manifest row): a
///     console document read under its platform is compared shape by shape, paired by ordinal among the geometry blocks
///     that yield a primitive (console files strip most shape names; the pairing is gated by the equal shape count, and
///     the measurement gated it on equal vertex counts and bounding spheres, which held on 95 of 95 manifest files),
///     against the PC document of the same path.
/// </summary>
/// <remarks>
///     <para>
///         A packed shape must reproduce the PC one under the format's own precision: positions, normals, UVs and the
///         tangent frame within one binary16 ulp of the PC float (absolute floor 2^-12; float3 channels exact), and
///         every half component is exactly the PC value rounded to the nearest-even half, except the documented
///         near-zero snapping residue, where both values are below 2^-12 (TestOutput/packed-semantics-20260924,
///         "Half-precision residue": every miss on every measured X360 L1-L4 file is such a pair; 0.02% to 1.3% of the
///         vertices per file, 25% on L6's 114 quads). Triangles are exact (static: the identity; skinned: through the
///         point indices, the PC NiSkinPartition's triangles through its vertex maps as a winding-preserving multiset,
///         because the console file carries only partition triangles and a PC NiTriShapeData may hold triangles no
///         partition does: giantant.nif shape 5 stores 90 there against 88 in both partition sets, so the data block
///         is tallied, never the oracle), influences equal the PC partition's weights and bone indices slot for slot
///         within one half ulp (PS3, the stored lanes) or, on the X360, equal slot for slot and bit for bit the engine
///         rule applied to the PC partition (the nearest-even halves of PC slots 0-2 on their bones plus the derived
///         1f - ((w0 + w1) + w2) on the PC slot-3 bone; see <see cref="CompareEngineLanes" />), the platform deciding which
///         reading the reader must have applied, and colors equal the PC floats within one byte under the platform's
///         order. The oracle
///         is the PC reader over the PC file plus the PC NiSkinPartition read independently by
///         <see cref="NifSkinPartitionView" />.
///     </para>
///     <para>
///         A shape with inline streams inside a packed file (16 of the 459 shapes in the manifest's 78 packed files) is
///         compared bit for bit instead: positions, normals, UVs, tangents, colors, indices and influences, as
///         <see cref="CompareInline" /> does for the console files that have no packed block at all (measured
///         2026-09-24 with the probe's array readers: every inline console array of the manifest equals the PC one bit
///         for bit, 27 shapes, and the one inline skinned shape's NiSkinData and partitions are byte-identical).
///     </para>
///     <para>
///         Controls the callers apply through <see cref="AssertMovedVertexControl" /> and
///         <see cref="CountColorDisagreements" />: a packed position component with |PC value| at least 1/4 (so its
///         half ulp is at least 2^-12, the tolerance floor, and the near-zero branch cannot rescue it) moved by a
///         single half ulp fails the exactness rule and moved by two leaves the one-ulp tolerance; on a file whose
///         decoded colors can tell the two consoles apart, the other platform's byte order fails the color comparison;
///         inside the comparison, one skinned triangle with its winding reversed fails the triangle multiset; and on
///         every X360 skinned vertex whose derived weight is nonzero on a slot-3 bone other than joint 0, the same weight
///         moved to joint 0 fails the engine-lane comparison (counted in the tally).
///     </para>
///     <para>
///         Two color censuses, one per consumer: <see cref="IsOrderSensitive" /> is the reader's byte rule (R, G, B not
///         all equal), reconciled with the reported <c>colorOrderSensitiveVertices</c>; <see cref="IsOrderDiscriminating" />
///         (R, G, B spanning at least two bytes) gates the wrong-order control, because a one-byte spread is
///         order-sensitive to the reader yet invisible to the one-byte tolerance of <see cref="CountColorDisagreements" />.
///     </para>
/// </remarks>
internal static class NifPackedGeometryComparison
{
    /// <summary>2^-12: below it a console half and a PC float may disagree by the near-zero snap (the same floor Tolerance uses).</summary>
    public const double NearZero = 1.0 / 4096;

    /// <summary>The smallest |PC value| whose half ulp reaches the tolerance floor, so a two-ulp move leaves the tolerance.</summary>
    public const float ControlMagnitude = 0.25f;

    /// <summary>
    ///     Compares every shape of a console document (already read under its platform) with the PC document's shape of
    ///     the same ordinal, asserting on the first disagreement, and returns the tally.
    /// </summary>
    /// <param name="console">The console document, read under <paramref name="platform" />.</param>
    /// <param name="pc">The PC twin's document.</param>
    /// <param name="pcBytes">The PC twin's bytes, for its partitions.</param>
    /// <param name="relativePath">The file, for messages.</param>
    /// <param name="platform">
    ///     The platform the console document was read under (<c>x360</c> or <c>ps3</c>): it decides the lanes the reader
    ///     must have typed (the X360 engine lanes, the PS3 stored lanes), so a reader that regressed to the other reading
    ///     fails instead of switching comparisons.
    /// </param>
    public static NifPackedGeometryComparisonResult Compare(ModelDocument console, ModelDocument pc, byte[] pcBytes,
        string relativePath, string platform)
    {
        Assert.True(platform is NifPackedPlatformOption.X360Value or NifPackedPlatformOption.Ps3Value,
            $"{relativePath}: unknown platform '{platform}'.");
        var engineLanes = platform == NifPackedPlatformOption.X360Value;
        SceneValidation.ValidateStructure(console);
        Assert.DoesNotContain(console.Diagnostics, d => d.Code == NifPackedGeometryReader.PlatformAssumedDiagnostic);
        Assert.DoesNotContain(console.Diagnostics, d => d.Code == NifPackedGeometryReader.NotTypedDiagnostic);
        var consoleShapes = Shapes(console);
        var pcShapes = Shapes(pc);
        Assert.Equal(pcShapes.Count, consoleShapes.Count);
        Assert.True(consoleShapes.Count > 0, $"{relativePath}: no primitive was read.");
        var pcPartitions = PcPartitions(pcBytes);
        var layouts = new HashSet<string>(StringComparer.Ordinal);
        var stats = new NifPackedGeometryTally();
        (float Console, float Pc)? control = null;
        var packedShapes = 0;
        var inlineShapes = 0;
        var orderSensitiveDecoded = 0;
        var orderDiscriminatingDecoded = 0;
        var orderSensitiveReported = 0;

        for (var ordinal = 0; ordinal < consoleShapes.Count; ordinal++)
        {
            var (primitive, payload) = consoleShapes[ordinal];
            var (reference, pcPayload) = pcShapes[ordinal];
            var pcBlock = (int)pcPayload["geometryBlock"]!;
            var where = $"{relativePath} shape {ordinal} (console block {payload["geometryBlock"]}, PC block {pcBlock})";
            if (payload["packed"] is not JsonObject packed)
            {
                layouts.Add("inline");
                stats.InlineComponents += CompareInline(primitive, reference, where);
                inlineShapes++;
                continue;
            }

            packedShapes++;
            var layout = (string)packed["layout"]!;
            layouts.Add(layout);
            var halfFrame = layout is "L1" or "L2" or "L3" or "L4";
            var points = primitive.PointIndices?.Values;
            Assert.Equal(reference.Vertices.Count, points is null ? primitive.Vertices.Count : primitive.PointIndices!.PointCount);

            for (var i = 0; i < primitive.Vertices.Count; i++)
            {
                var pcVertex = reference.Vertices[points is null ? i : points[i]];
                var vertex = primitive.Vertices[i];
                Compare(stats.Positions, vertex.Position, pcVertex.Position, true, where, "position", i);
                Compare(stats.Normals, vertex.Normal, pcVertex.Normal, halfFrame, where, "normal", i);
                Compare(stats.Uvs, vertex.TexCoord.X, pcVertex.TexCoord.X, true, where, "u", i);
                Compare(stats.Uvs, vertex.TexCoord.Y, pcVertex.TexCoord.Y, true, where, "v", i);
                control ??= ControlCandidate(vertex.Position, pcVertex.Position);
            }

            CompareTangents(stats, primitive, reference, points, halfFrame, where);
            CompareTriangles(stats, primitive, reference, points, points is null ? null : pcPartitions.GetValueOrDefault(pcBlock),
                where);
            if (primitive.PrimaryColorAttributeIndex is not null)
            {
                Assert.NotNull(reference.PrimaryColorAttributeIndex);
                for (var i = 0; i < primitive.Vertices.Count; i++)
                {
                    var color = primitive.Vertices[i].Color;
                    var expected = reference.Vertices[points is null ? i : points[i]].Color;
                    Assert.True(ColorsAgree(color, expected), $"{where}: color {i} console {color} pc {expected}");
                    stats.Colors++;
                    if (IsOrderSensitive(color))
                    {
                        orderSensitiveDecoded++;
                    }

                    if (IsOrderDiscriminating(color))
                    {
                        orderDiscriminatingDecoded++;
                    }
                }

                orderSensitiveReported += AssertColorByteOrderFacts(payload, layout, console, where);
            }

            if (primitive.SkinInfluences is { } influences)
            {
                var pcPartition = pcPartitions[pcBlock];
                var lanes = (string?)payload["skin"]?["influences"]?["lanes"];
                Assert.True(lanes == (engineLanes ? "engine" : "stored"),
                    $"{where}: a {platform} read typed the '{lanes}' lanes, not the {(engineLanes ? "engine" : "stored")} lanes.");
                if (engineLanes)
                {
                    CompareEngineLanes(stats, influences, primitive, pcPartition, where);
                }
                else
                {
                    CompareInfluences(stats, influences, primitive, pcPartition, where);
                }
            }
        }

        Assert.True(packedShapes > 0, $"{relativePath}: no packed shape was compared.");
        AssertCensusNotVacuous(stats.Positions, "positions", relativePath);
        AssertCensusNotVacuous(stats.Normals, "normals", relativePath);
        AssertCensusNotVacuous(stats.Uvs, "uvs", relativePath);
        AssertCensusNotVacuous(stats.Tangents, "tangents", relativePath);
        Assert.Equal(layouts.Contains("L6"),
            console.Diagnostics.Any(d => d.Code == NifPackedGeometryReader.ColorOrderInferredDiagnostic));
        Assert.Equal(orderSensitiveDecoded, orderSensitiveReported);
        Assert.True(orderDiscriminatingDecoded <= orderSensitiveDecoded,
            $"{relativePath}: {orderDiscriminatingDecoded} colors span two bytes but only {orderSensitiveDecoded} have " +
            $"unequal R, G, B; the discriminating rule must imply the byte rule.");
        return new NifPackedGeometryComparisonResult(consoleShapes.Count, packedShapes, inlineShapes, layouts, stats,
            orderSensitiveDecoded, orderDiscriminatingDecoded, control);
    }

    /// <summary>
    ///     A console shape with inline streams against the PC shape of the same ordinal, bit for bit: positions, normals,
    ///     UV set 0, tangents, colors, the identity index buffer and, when both carry skin influences, the joint indices
    ///     and weight bits. Returns the components compared.
    /// </summary>
    public static int CompareInline(ScenePrimitive primitive, ScenePrimitive reference, string where)
    {
        Assert.Null(primitive.PointIndices);
        Assert.Null(FirstMismatch(reference.Vertices.Select(v => v.Position).ToList(),
            primitive.Vertices.Select(v => v.Position).ToList(), where + " positions"));
        Assert.Null(FirstMismatch(reference.Vertices.Select(v => v.Normal).ToList(),
            primitive.Vertices.Select(v => v.Normal).ToList(), where + " normals"));
        var compared = primitive.Vertices.Count * 6;
        for (var i = 0; i < primitive.Vertices.Count; i++)
        {
            var uv = primitive.Vertices[i].TexCoord;
            var pcUv = reference.Vertices[i].TexCoord;
            Assert.True(SameBits(pcUv.X, uv.X) && SameBits(pcUv.Y, uv.Y), $"{where}: UV {i} console {uv} pc {pcUv}");
            compared += 2;
        }

        Assert.Equal(reference.Tangents is not null, primitive.Tangents is not null);
        if (primitive.Tangents is not null)
        {
            Assert.Null(FirstMismatch(reference.Tangents!.Values, primitive.Tangents.Values, where + " tangents"));
            compared += primitive.Vertices.Count * 4;
        }

        Assert.Equal(reference.PrimaryColorAttributeIndex is not null, primitive.PrimaryColorAttributeIndex is not null);
        if (primitive.PrimaryColorAttributeIndex is not null)
        {
            Assert.Null(FirstMismatch(reference.Vertices.Select(v => v.Color).ToList(),
                primitive.Vertices.Select(v => v.Color).ToList(), where + " colors"));
            compared += primitive.Vertices.Count * 4;
        }

        Assert.Equal(reference.Indices, primitive.Indices);
        Assert.Equal(reference.SkinInfluences is not null, primitive.SkinInfluences is not null);
        if (primitive.SkinInfluences is { } influences)
        {
            var expected = reference.SkinInfluences!;
            Assert.Equal(expected.InfluencesPerVertex, influences.InfluencesPerVertex);
            Assert.Equal(expected.JointIndices.Count, influences.JointIndices.Count);
            // The packed path pads a zero-weight slot with joint 0 (2026-09-26); the PC partition path keeps the
            // partition's own bone index there, so only weighted slots compare, and the console padding must be 0.
            for (var i = 0; i < influences.JointIndices.Count; i++)
            {
                if (influences.Weights[i] != 0f)
                {
                    Assert.True(expected.JointIndices[i] == influences.JointIndices[i],
                        $"{where}: joint slot {i} console {influences.JointIndices[i]} pc {expected.JointIndices[i]}");
                }
                else
                {
                    Assert.True(influences.JointIndices[i] == 0,
                        $"{where}: zero-weight joint slot {i} console {influences.JointIndices[i]}, expected the joint-0 padding");
                }
            }

            Assert.Equal(expected.Weights.Count, influences.Weights.Count);
            for (var i = 0; i < influences.Weights.Count; i++)
            {
                Assert.True(SameBits(expected.Weights[i], influences.Weights[i]),
                    $"{where}: weight {i} console {influences.Weights[i]:R} pc {expected.Weights[i]:R}");
            }

            compared += influences.Weights.Count * 2;
        }

        return compared;
    }

    /// <summary>
    ///     The moved-vertex control: the recorded packed position component moved by a single half ulp fails the
    ///     exactness rule (its magnitude keeps the near-zero branch from rescuing it), and moved by two it leaves the
    ///     one-ulp tolerance, so the per-component gate sees both.
    /// </summary>
    public static void AssertMovedVertexControl(NifPackedGeometryComparisonResult result, string relativePath)
    {
        Assert.True(result.MovedVertexControl is not null,
            $"{relativePath}: no exactly matching packed position component with |PC value| >= {ControlMagnitude} to move for the control.");
        var (console, pc) = result.MovedVertexControl!.Value;
        var movedOnce = NextHalfAway(console);
        Assert.False(HalfAgrees(movedOnce, pc), "a component moved by one half ulp still passed the exactness gate");
        var movedTwice = NextHalfAway(movedOnce);
        Assert.True(Math.Abs((double)movedTwice - pc) > Tolerance(pc),
            "a component moved by two half ulps stayed inside the one-ulp tolerance");
    }

    /// <summary>
    ///     The vertices of a console document read under the OTHER platform whose colors disagree with the PC document's
    ///     (through the point indices); positive on a file whose colors can tell the consoles apart, since a grey
    ///     vertex (R, G, B all within one byte of each other) reads the same under A,R,G,B and A,G,B,R.
    /// </summary>
    public static int CountColorDisagreements(ModelDocument misread, ModelDocument pc)
    {
        var shapes = Shapes(misread);
        var pcShapes = Shapes(pc);
        Assert.Equal(pcShapes.Count, shapes.Count);
        var disagreeing = 0;
        for (var ordinal = 0; ordinal < shapes.Count; ordinal++)
        {
            var (primitive, _) = shapes[ordinal];
            if (primitive.PrimaryColorAttributeIndex is null)
            {
                continue;
            }

            var (reference, _) = pcShapes[ordinal];
            var points = primitive.PointIndices?.Values;
            for (var i = 0; i < primitive.Vertices.Count; i++)
            {
                if (!ColorsAgree(primitive.Vertices[i].Color, reference.Vertices[points is null ? i : points[i]].Color))
                {
                    disagreeing++;
                }
            }
        }

        return disagreeing;
    }

    /// <summary>The first primitive of every geometry block that yielded one, in geometry-block order, with its facts.</summary>
    public static List<(ScenePrimitive Primitive, JsonObject Payload)> Shapes(ModelDocument document)
    {
        var byBlock = new SortedDictionary<int, (ScenePrimitive, JsonObject)>();
        foreach (var row in Rows(document, NifModelGeometryReader.PrimitiveKind))
        {
            var payload = JsonNode.Parse(row.PayloadJson)!.AsObject();
            var mesh = row.Target.Index!.Value;
            byBlock.TryAdd((int)payload["geometryBlock"]!, (document.Meshes[mesh].Primitives[0], payload));
        }

        return byBlock.Values.ToList();
    }

    /// <summary>
    ///     The PC file's partitions per skinned geometry block, read independently of the reader: the shape's Skin
    ///     Instance link, its Skin Partition link, then <see cref="NifSkinPartitionView.ReadAll" />.
    /// </summary>
    private static Dictionary<int, IReadOnlyList<NifSkinPartitionView>> PcPartitions(byte[] pcBytes)
    {
        var result = new Dictionary<int, IReadOnlyList<NifSkinPartitionView>>();
        var nif = Assert.IsType<NifInfo>(NifParser.Parse(pcBytes));
        var schema = NifSchema.LoadEmbedded();
        var decoder = new NifBlockDecoder(schema, nif, pcBytes);
        for (var i = 0; i < nif.Blocks.Count; i++)
        {
            if (!NifModelGeometryReader.IsGeometryType(nif.Blocks[i].TypeName))
            {
                continue;
            }

            var shape = decoder.Decode(i, NifDecodeMode.Tolerant).Root;
            if (!shape.TryGet("Skin Instance", out var skin) || skin is not NifRefValue { IsNone: false } skinLink ||
                !shape.TryGet("Data", out var data) || data is not NifRefValue { IsNone: false } dataLink)
            {
                continue;
            }

            var instance = decoder.Decode(skinLink.Index, NifDecodeMode.Tolerant);
            var view = NifSkinInstanceView.Read(instance,
                schema.Inherits(instance.Type, NifModelSkinReader.DismemberType));
            if (view.PartitionLink < 0)
            {
                continue;
            }

            var vertexCount = (int)decoder.Decode(dataLink.Index, NifDecodeMode.Tolerant).Root
                .Get<NifIntegerValue>("Num Vertices").Value;
            result[i] = NifSkinPartitionView.ReadAll(decoder.Decode(view.PartitionLink, NifDecodeMode.Tolerant),
                view.Bones.Count, vertexCount);
        }

        return result;
    }

    /// <summary>
    ///     The color byte-order facts of one shape under a declared platform: an L1 or L4 shape's order was measured
    ///     (ReverseEngineered, no evidence text) while an L6 shape's is carried over (Assumed, the evidence text, and
    ///     zero order-sensitive vertices, because every retail L6 vertex is white). Returns the reported
    ///     order-sensitive count, which the caller reconciles with the decoded colors.
    /// </summary>
    private static int AssertColorByteOrderFacts(JsonObject payload, string layout, ModelDocument console, string where)
    {
        var platform = payload["packed"]!["platform"]!;
        var colors = payload["vertexColors"]!;
        Assert.False((bool)platform["assumed"]!);
        var reported = (int)platform["colorOrderSensitiveVertices"]!;
        Assert.Equal(reported, (int)colors["colorOrderSensitiveVertices"]!);
        if (layout == "L6")
        {
            Assert.Equal(nameof(SceneValueProvenance.Assumed), (string)platform["colorByteOrderProvenance"]!);
            Assert.Equal(nameof(SceneValueProvenance.Assumed), (string)colors["byteOrderProvenance"]!);
            Assert.Equal(NifPackedGeometryLayout.L6ColorInference, (string)platform["colorByteOrderEvidence"]!);
            Assert.Equal(NifPackedGeometryLayout.L6ColorInference, (string)colors["colorByteOrderEvidence"]!);
            Assert.True(reported == 0, $"{where}: an L6 shape reports {reported} order-sensitive vertices; the " +
                                       $"measurement found every retail L6 vertex white");
            Assert.Contains(console.Diagnostics, d => d.Code == NifPackedGeometryReader.ColorOrderInferredDiagnostic);
        }
        else
        {
            Assert.Equal(nameof(SceneValueProvenance.ReverseEngineered), (string)platform["colorByteOrderProvenance"]!);
            Assert.Equal(nameof(SceneValueProvenance.ReverseEngineered), (string)colors["byteOrderProvenance"]!);
            Assert.Null(platform["colorByteOrderEvidence"]);
            Assert.Null(colors["colorByteOrderEvidence"]);
        }

        return reported;
    }

    private static void CompareTangents(NifPackedGeometryTally stats, ScenePrimitive primitive, ScenePrimitive reference,
        IReadOnlyList<int>? points, bool halfFrame, string where)
    {
        Assert.NotNull(primitive.Tangents);
        Assert.NotNull(reference.Tangents);
        for (var i = 0; i < primitive.Vertices.Count; i++)
        {
            var console = primitive.Tangents.Values[i];
            var pcTangent = reference.Tangents.Values[points is null ? i : points[i]];
            Compare(stats.Tangents, new Vector3(console.X, console.Y, console.Z),
                new Vector3(pcTangent.X, pcTangent.Y, pcTangent.Z), halfFrame, where, "tangent", i);
        }
    }

    /// <summary>
    ///     Static: the identity index buffer. Skinned: the console triangles through the point indices must be the PC
    ///     NiSkinPartition's triangles through its vertex maps, as a winding-preserving multiset (both sides' strips
    ///     triangulated and repeated-index triangles dropped by <see cref="NifSkinPartitionView" />). The PC NiTriShapeData
    ///     is not the oracle: the console file stores only partition triangles, and a PC data block may hold triangles no
    ///     partition carries (giantant.nif shape 5: 90 against 88 in both partition sets), so its differences against the
    ///     PC partitions are tallied, never asserted. Control: one console triangle with its winding reversed fails.
    /// </summary>
    private static void CompareTriangles(NifPackedGeometryTally stats, ScenePrimitive primitive, ScenePrimitive reference,
        IReadOnlyList<int>? points, IReadOnlyList<NifSkinPartitionView>? pcPartitions, string where)
    {
        if (points is null)
        {
            Assert.Equal(reference.Indices, primitive.Indices);
            return;
        }

        Assert.True(pcPartitions is not null, $"{where}: the PC file has no NiSkinPartition for this skinned shape.");
        var mapped = TriangleKeys(primitive.Indices, i => points[i]);
        var expected = new List<(int, int, int)>();
        foreach (var partition in pcPartitions!)
        {
            Assert.NotNull(partition.VertexMap);
            if (partition.Triangles is { } triangles)
            {
                var map = partition.VertexMap;
                expected.AddRange(TriangleKeys(triangles.Indices, i => map[i]));
            }
        }

        Assert.True(mapped.Count > 0, $"{where}: the console shape has no triangle.");
        Assert.True(expected.Count == mapped.Count,
            $"{where}: the console partitions carry {mapped.Count} triangles, the PC partitions {expected.Count}.");
        mapped.Sort();
        expected.Sort();
        Assert.True(mapped.SequenceEqual(expected), $"{where}: the partition triangles are not the PC partition triangles.");

        // Control: reversing the winding of one console triangle with three distinct shape vertices changes the multiset.
        var flippable = mapped.FindIndex(k => k.Item1 != k.Item2 && k.Item2 != k.Item3 && k.Item1 != k.Item3);
        Assert.True(flippable >= 0, $"{where}: every triangle repeats a shape vertex, so no winding can be reversed.");
        var flipped = new List<(int, int, int)>(mapped);
        var (a, b, c) = flipped[flippable];
        flipped[flippable] = NifModelDismemberFaces.TriangleKey(a, c, b);
        flipped.Sort();
        Assert.False(flipped.SequenceEqual(expected), $"{where}: a reversed winding was not detected.");

        // The PC data block against the PC partitions: recorded facts, never a gate.
        var dataBlock = TriangleKeys(reference.Indices, i => i);
        dataBlock.Sort();
        stats.DataBlockTrianglesNotInPartitions += CountNotIn(dataBlock, expected);
        stats.PartitionTrianglesNotInDataBlock += CountNotIn(expected, dataBlock);
    }

    /// <summary>Winding-preserving keys of an index buffer's triangles through a vertex mapping.</summary>
    private static List<(int, int, int)> TriangleKeys(IReadOnlyList<int> indices, Func<int, int> map)
    {
        var keys = new List<(int, int, int)>(indices.Count / 3);
        for (var t = 0; t + 2 < indices.Count; t += 3)
        {
            keys.Add(NifModelDismemberFaces.TriangleKey(map(indices[t]), map(indices[t + 1]), map(indices[t + 2])));
        }

        return keys;
    }

    /// <summary>The elements of one sorted multiset that the other sorted multiset does not match one for one.</summary>
    private static int CountNotIn(List<(int, int, int)> keys, List<(int, int, int)> other)
    {
        var unmatched = 0;
        var o = 0;
        foreach (var key in keys)
        {
            while (o < other.Count && other[o].CompareTo(key) < 0)
            {
                o++;
            }

            if (o < other.Count && other[o].Equals(key))
            {
                o++;
            }
            else
            {
                unmatched++;
            }
        }

        return unmatched;
    }

    /// <summary>
    ///     Slot for slot against the PC partition: weights within one half ulp (the console's sentinel slot reads 0
    ///     where the PC slot is 0) and bone indices exactly, empty slots included.
    /// </summary>
    private static void CompareInfluences(NifPackedGeometryTally stats, SceneSkinInfluences influences,
        ScenePrimitive primitive, IReadOnlyList<NifSkinPartitionView> pcPartitions, string where)
    {
        Assert.Equal(4, influences.InfluencesPerVertex);
        var vertex = 0;
        foreach (var partition in pcPartitions)
        {
            Assert.NotNull(partition.Weights);
            Assert.NotNull(partition.BoneIndices);
            Assert.Equal(4, partition.WeightsPerVertex);
            for (var local = 0; local < partition.VertexCount; local++, vertex++)
            {
                for (var k = 0; k < 4; k++)
                {
                    var console = influences.Weights[vertex * 4 + k];
                    var pc = partition.Weights[local][k];
                    Assert.True(Math.Abs(console - pc) <= Tolerance(pc),
                        $"{where}: packed vertex {vertex} weight slot {k} console {console:R} pc {pc:R}");
                    if (console != 0f)
                    {
                        Assert.True(influences.JointIndices[vertex * 4 + k] == partition.Bones[partition.BoneIndices[local][k]],
                            $"{where}: packed vertex {vertex} bone slot {k} console joint " +
                            $"{influences.JointIndices[vertex * 4 + k]} pc joint {partition.Bones[partition.BoneIndices[local][k]]}");
                    }
                    else
                    {
                        // A zero-weight packed slot is padded with joint 0 (2026-09-26), not the partition's bone there.
                        Assert.True(influences.JointIndices[vertex * 4 + k] == 0,
                            $"{where}: packed vertex {vertex} zero-weight bone slot {k} console joint " +
                            $"{influences.JointIndices[vertex * 4 + k]}, expected the joint-0 padding");
                    }
                    stats.Influences++;
                }
            }
        }

        Assert.Equal(primitive.Vertices.Count, vertex);
    }

    /// <summary>
    ///     X360 engine lanes against the engine rule applied to the PC partition, slot for slot and bit for bit: the
    ///     expected lanes are <see cref="EngineLanes" /> over the nearest-even binary16 of the PC slot 0-2 weights on the PC
    ///     partition's Bones[index], with the derived weight on Bones[slot-3 index]. Exact, with no allowance, because the
    ///     measurement licenses it (measurement/engine_lane_oracle_census.json, 2026-09-27, every X360 file with packed
    ///     skinned geometry and a PC twin: 776 files, 4,142 shapes, 2,254,311 vertices): the console halves ARE the
    ///     nearest-even halves of the PC weights on every slot 0-2, so the derived weight equals the one derived from the PC
    ///     side on every vertex, and the console slot-3 bone equals the PC slot-3 bone on every one of the 757,265 vertices
    ///     whose derived weight is nonzero. So the derived weight's JOINT is pinned, not only its value: the earlier
    ///     joint-for-joint comparison with a per-weight allowance of at least 3 x 2^-12 could not see a derived weight
    ///     (at most 3 x 2^-13 in magnitude where negative) put on the wrong joint.
    ///     Control, on every vertex where it can apply: the same rule with joint 0 in place of the slot-3 bone (a reader
    ///     that put r on the stored reading's joint-0 padding, merging it into a joint-0 lane when there is one) must be
    ///     reported by the same comparison. It applies where r is nonzero and the slot-3 bone is not joint 0; it is
    ///     unobservable only where both placements round away inside a merge, which is counted apart.
    /// </summary>
    private static void CompareEngineLanes(NifPackedGeometryTally stats, SceneSkinInfluences influences,
        ScenePrimitive primitive, IReadOnlyList<NifSkinPartitionView> pcPartitions, string where)
    {
        Assert.Equal(4, influences.InfluencesPerVertex);
        var vertex = 0;
        var joints = new int[4];
        var weights = new float[4];
        var controlJoints = new int[4];
        var controlWeights = new float[4];
        foreach (var partition in pcPartitions)
        {
            Assert.NotNull(partition.Weights);
            Assert.NotNull(partition.BoneIndices);
            Assert.Equal(4, partition.WeightsPerVertex);
            for (var local = 0; local < partition.VertexCount; local++, vertex++)
            {
                var pc = partition.Weights[local];
                var bones = partition.BoneIndices[local];
                var h0 = NearestHalf(pc[0]);
                var h1 = NearestHalf(pc[1]);
                var h2 = NearestHalf(pc[2]);
                var residual = (float)(1f - (float)((float)(h0 + h1) + h2));
                var slot3 = residual == 0f ? 0 : partition.Bones[bones[3]];
                var carrier = EngineLanes(h0, h1, h2, Joint(partition, bones, 0, h0), Joint(partition, bones, 1, h1),
                    Joint(partition, bones, 2, h2), slot3, joints, weights, out _);
                var mismatch = EngineLaneMismatch(influences.JointIndices, influences.Weights, vertex * 4, joints, weights);
                Assert.True(mismatch is null, $"{where}: packed vertex {vertex} {mismatch}");
                double sum = 0;
                for (var k = 0; k < 4; k++)
                {
                    sum += influences.Weights[vertex * 4 + k];
                }

                Assert.True(Math.Abs(sum - 1) <= 4 * Math.Pow(2, -24),
                    $"{where}: packed vertex {vertex} engine lanes sum to {sum:R}, not one within Float32 rounding");

                if (residual != 0f && slot3 != 0)
                {
                    var controlCarrier = EngineLanes(h0, h1, h2, Joint(partition, bones, 0, h0), Joint(partition, bones, 1, h1),
                        Joint(partition, bones, 2, h2), 0, controlJoints, controlWeights, out _);
                    // Decided from the lanes' structure, not by the comparison under test: the placement is observable
                    // unless both readings merge r into a lane and round it away there.
                    float[] stored = [h0, h1, h2];
                    var hidden = carrier is >= 0 and < 3 && weights[carrier] == stored[carrier] &&
                                 controlCarrier is >= 0 and < 3 && controlWeights[controlCarrier] == stored[controlCarrier];
                    if (hidden)
                    {
                        stats.EngineLaneJointUnobservable++;
                    }
                    else
                    {
                        Assert.True(EngineLaneMismatch(controlJoints, controlWeights, 0, joints, weights) is not null,
                            $"{where}: packed vertex {vertex}: its derived weight moved from joint {slot3} to joint 0 passed " +
                            $"the engine-lane comparison");
                        stats.EngineLaneJointControls++;
                    }
                }

                stats.EngineLanes++;
                stats.Influences += 4;
            }
        }

        Assert.Equal(primitive.Vertices.Count, vertex);

        static int Joint(NifSkinPartitionView partition, byte[] bones, int slot, float weight)
        {
            return weight == 0f ? 0 : partition.Bones[bones[slot]];
        }
    }

    /// <summary>The nearest-even binary16 of a PC Float32 weight, widened back exactly.</summary>
    internal static float NearestHalf(float value)
    {
        return (float)(Half)value;
    }

    /// <summary>
    ///     The X360 engine rule, stated here independently of the reader (<c>NifPackedEngineLanes.Derive</c>): slots 0-2
    ///     carry the three stored weights on their joints (joint 0 for a zero weight); the derived weight
    ///     r = 1f - ((w0 + w1) + w2) goes to the slot-3 joint, added in Float32 to the first positive slot 0-2 lane on that
    ///     joint (slot 3 then a zero lane; a sum of exactly zero is itself a zero lane padded with joint 0), else into slot 3.
    /// </summary>
    /// <param name="w0">Slot 0's stored weight.</param>
    /// <param name="w1">Slot 1's stored weight.</param>
    /// <param name="w2">Slot 2's stored weight.</param>
    /// <param name="j0">Slot 0's joint (0 when its weight is 0).</param>
    /// <param name="j1">Slot 1's joint (0 when its weight is 0).</param>
    /// <param name="j2">Slot 2's joint (0 when its weight is 0).</param>
    /// <param name="slot3Joint">The joint the derived weight goes to.</param>
    /// <param name="joints">Receives the four joints.</param>
    /// <param name="weights">Receives the four weights.</param>
    /// <param name="residual">Receives r.</param>
    /// <returns>The slot that carries r (3, or the merged slot 0-2), or -1 when r is zero or cancels its lane.</returns>
    internal static int EngineLanes(float w0, float w1, float w2, int j0, int j1, int j2, int slot3Joint, int[] joints,
        float[] weights, out float residual)
    {
        weights[0] = w0;
        weights[1] = w1;
        weights[2] = w2;
        weights[3] = 0f;
        joints[0] = j0;
        joints[1] = j1;
        joints[2] = j2;
        joints[3] = 0;
        residual = (float)(1f - (float)((float)(w0 + w1) + w2));
        if (residual == 0f)
        {
            return -1;
        }

        for (var k = 0; k < 3; k++)
        {
            if (weights[k] > 0f && joints[k] == slot3Joint)
            {
                weights[k] = (float)(weights[k] + residual);
                if (weights[k] != 0f)
                {
                    return k;
                }

                weights[k] = 0f;
                joints[k] = 0;
                return -1;
            }
        }

        weights[3] = residual;
        joints[3] = slot3Joint;
        return 3;
    }

    /// <summary>
    ///     The first disagreement between four console lanes (at <paramref name="offset" />) and four expected lanes, slot
    ///     for slot: the joint exactly and the weight bit for bit, or null when all four agree.
    /// </summary>
    internal static string? EngineLaneMismatch(IReadOnlyList<int> consoleJoints, IReadOnlyList<float> consoleWeights,
        int offset, int[] joints, float[] weights)
    {
        for (var k = 0; k < 4; k++)
        {
            var joint = consoleJoints[offset + k];
            var weight = consoleWeights[offset + k];
            if (joint != joints[k] ||
                BitConverter.SingleToUInt32Bits(weight) != BitConverter.SingleToUInt32Bits(weights[k]))
            {
                return string.Create(CultureInfo.InvariantCulture,
                    $"engine lane {k} is (joint {joint}, {weight:R}), the engine rule over the PC twin gives (joint " +
                    $"{joints[k]}, {weights[k]:R})");
            }
        }

        return null;
    }

    private static void Compare(NifPackedChannelCensus census, Vector3 console, Vector3 pc, bool half, string where,
        string what, int vertex)
    {
        Compare(census, console.X, pc.X, half, where, what + ".x", vertex);
        Compare(census, console.Y, pc.Y, half, where, what + ".y", vertex);
        Compare(census, console.Z, pc.Z, half, where, what + ".z", vertex);
    }

    /// <summary>
    ///     One component: a float3 channel must be the PC bits; a half channel must lie within one half ulp of the PC
    ///     value AND be either the nearest-even half of it or a near-zero snapping pair (both below 2^-12), the
    ///     README's "Half-precision residue" rule. The exact count is a recorded fact.
    /// </summary>
    private static void Compare(NifPackedChannelCensus census, float console, float pc, bool half, string where,
        string what, int vertex)
    {
        census.Compared++;
        if (!half)
        {
            Assert.True(BitConverter.SingleToUInt32Bits(console) == BitConverter.SingleToUInt32Bits(pc),
                $"{where}: vertex {vertex} {what} console {console:R} pc {pc:R} (float3 channel: exact copy required)");
            census.Exact++;
            return;
        }

        var error = Math.Abs((double)console - pc);
        Assert.True(error <= Tolerance(pc),
            $"{where}: vertex {vertex} {what} console {console:R} pc {pc:R} error {error:E2} exceeds one half ulp");
        var exact = ExactMoved(console, pc);
        Assert.True(exact || HalfAgrees(console, pc),
            $"{where}: vertex {vertex} {what} console {console:R} pc {pc:R} is neither the nearest-even half of the " +
            $"PC value nor a near-zero snapping pair (both below 2^-12)");
        if (exact)
        {
            census.Exact++;
        }
    }

    /// <summary>The first position component that matched exactly with |PC value| at least <see cref="ControlMagnitude" />, or null.</summary>
    private static (float Console, float Pc)? ControlCandidate(Vector3 console, Vector3 pc)
    {
        foreach (var (c, p) in new[] { (console.X, pc.X), (console.Y, pc.Y), (console.Z, pc.Z) })
        {
            if (Math.Abs(p) >= ControlMagnitude && ExactMoved(c, p))
            {
                return (c, p);
            }
        }

        return null;
    }

    /// <summary>True when the console float is exactly the PC float rounded to the nearest-even half.</summary>
    private static bool ExactMoved(float console, float pc)
    {
        return BitConverter.SingleToUInt32Bits(console) ==
               BitConverter.SingleToUInt32Bits((float)BitConverter.UInt16BitsToHalf(BitConverter.HalfToUInt16Bits((Half)pc)));
    }

    /// <summary>The exactness gate: the nearest-even half of the PC value, or a near-zero snapping pair.</summary>
    private static bool HalfAgrees(float console, float pc)
    {
        return ExactMoved(console, pc) || (Math.Abs(console) < NearZero && Math.Abs(pc) < NearZero);
    }

    /// <summary>One binary16 ulp at the PC value, with an absolute floor of 2^-12 for near-zero values.</summary>
    private static double Tolerance(float pc)
    {
        var magnitude = Math.Abs((double)pc);
        if (magnitude == 0)
        {
            return Math.Pow(2, -12);
        }

        var exponent = Math.Max(Math.Floor(Math.Log2(magnitude)), -14);
        return Math.Max(Math.Pow(2, exponent - 10), Math.Pow(2, -12));
    }

    private static bool ColorsAgree(Vector4 console, Vector4 pc)
    {
        const float tolerance = 1f / 255f + 1e-6f;
        return Math.Abs(console.X - pc.X) <= tolerance && Math.Abs(console.Y - pc.Y) <= tolerance &&
               Math.Abs(console.Z - pc.Z) <= tolerance && Math.Abs(console.W - pc.W) <= tolerance;
    }

    /// <summary>
    ///     The reader's rule on the decoded floats: R, G, B are not all equal (NifPackedGeometryDecoder counts the
    ///     vertices whose bytes 1..3 are not all equal). Each float is byte/255, within 255 * 2^-24 of the byte, so
    ///     rounding recovers the byte exactly; and {R, G, B} is {byte1, byte2, byte3} under both platform orders, so
    ///     the rule is platform-independent.
    /// </summary>
    internal static bool IsOrderSensitive(Vector4 color)
    {
        var r = MathF.Round(color.X * 255f);
        var g = MathF.Round(color.Y * 255f);
        var b = MathF.Round(color.Z * 255f);
        return r != g || g != b;
    }

    /// <summary>
    ///     True when a decoded color's R, G, B span at least two bytes: the only vertices on which the other platform's
    ///     order fails <see cref="ColorsAgree" /> (whose tolerance is one byte), so the wrong-order control is gated on
    ///     this count. Implies <see cref="IsOrderSensitive" />; a one-byte spread satisfies only that.
    /// </summary>
    internal static bool IsOrderDiscriminating(Vector4 color)
    {
        const float tolerance = 1f / 255f + 1e-6f;
        return Math.Abs(color.X - color.Y) > tolerance || Math.Abs(color.Y - color.Z) > tolerance ||
               Math.Abs(color.X - color.Z) > tolerance;
    }

    /// <summary>The next half away from zero (one half ulp of larger magnitude), widened.</summary>
    private static float NextHalfAway(float value)
    {
        var bits = BitConverter.HalfToUInt16Bits((Half)value);
        return (float)BitConverter.UInt16BitsToHalf((ushort)(bits + 1));
    }

    /// <summary>A channel was compared and has at least one exact component, so the residue rule did not pass vacuously.</summary>
    private static void AssertCensusNotVacuous(NifPackedChannelCensus census, string what, string relativePath)
    {
        Assert.True(census.Compared > 0, $"{relativePath}: no {what} compared.");
        Assert.True(census.Exact > 0, $"{relativePath}: no {what} component equals the half-rounded PC value.");
    }
}
