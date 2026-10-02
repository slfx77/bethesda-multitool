using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using SharpGLTF.Schema2;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Export;

/// <summary>
///     Layer B of the M2.1 parity gate: compares an encoded native GLB with an encoded normalized GLB through their
///     decoded accessors, independent of vertex welding, node pruning, mesh grouping and table order.
/// </summary>
/// <remarks>
///     <para>
///         Triangles pair by oriented identity: an exact bit-level key first, then tolerance matching inside the
///         same material content signature and the 27 spatial cells around the native centroid, so a value that
///         sits on a decimal rounding or cell boundary cannot flake. Only cyclic rotations pair, never a reversal,
///         so winding stays observable. Any triangle left unpaired on either side fails.
///     </para>
///     <para>
///         Tolerances: local position within max(1e-5, 2^-20 |p|); world position within max(1e-3, 1e-6 |p|);
///         normal and tangent directions within 1e-5 per component; tangent handedness exact; texture coordinates
///         within 1e-6; a color within 1e-6 of the exact interval of inputs its accessor's encoder maps to the
///         decoded value (a float is a point interval; see <see cref="NifGlbColorQuantization" />).
///     </para>
///     <para>
///         Known native behavior is accounted for exactly, never by widening a tolerance: repeated-position
///         triangles (<see cref="NifGlbParityExpectations.RepeatedPositionTriangles" />), merged identical material
///         rows (the normalized GLB may hold more rows per content signature, never fewer), inert fallback tangents
///         (decided per primitive through <see cref="NifGlbParityExpectations.SharedPrimitiveHasAuthoredTangents" />),
///         and the toolkit's extra identity child node per additional mesh on one node (placements are compared per
///         drawn primitive).
///     </para>
/// </remarks>
internal static class NifGlbParityOracle
{
    /// <summary>The absolute floor of the local-position tolerance.</summary>
    internal const float LocalPositionFloor = 1e-5f;

    /// <summary>The relative local-position tolerance, 2^-20 of the position's length.</summary>
    internal const float LocalPositionRelative = 1f / 1048576f;

    /// <summary>The absolute floor of the world-position tolerance.</summary>
    internal const float WorldPositionFloor = 1e-3f;

    /// <summary>The relative world-position tolerance.</summary>
    internal const float WorldPositionRelative = 1e-6f;

    /// <summary>The per-component tolerance of normal and tangent directions.</summary>
    internal const float DirectionTolerance = 1e-5f;

    /// <summary>The per-component tolerance of texture coordinates.</summary>
    internal const float TexCoordTolerance = 1e-6f;

    /// <summary>The slack allowed outside an encoder's exact color interval.</summary>
    internal const float ColorSlack = 1e-6f;

    /// <summary>The smallest spatial matching cell.</summary>
    internal const double MinimumCell = 1e-3;

    /// <summary>Feature class: the compared primitive's material binds a base-color texture.</summary>
    internal const string Textured = "Textured";

    /// <summary>Feature class: the compared primitive's material binds a normal texture.</summary>
    internal const string NormalMapped = "NormalMapped";

    /// <summary>Feature class: the compared primitive's material uses MASK alpha.</summary>
    internal const string AlphaMask = "AlphaMask";

    /// <summary>Feature class: the compared primitive's material uses BLEND alpha.</summary>
    internal const string AlphaBlend = "AlphaBlend";

    /// <summary>Feature class: the compared primitive's material is double-sided.</summary>
    internal const string DoubleSided = "DoubleSided";

    /// <summary>Feature class: the compared primitive carries a non-white vertex color.</summary>
    internal const string VertexColored = "VertexColored";

    /// <summary>Feature class: the compared primitive's material is lit and emissive.</summary>
    internal const string LitEmissive = "LitEmissive";

    /// <summary>Feature class: the compared primitive's node sits below an intermediate node.</summary>
    internal const string MultiNodeHierarchy = "MultiNodeHierarchy";

    /// <summary>Feature class: the primitive lost triangles to the repeated-position accounting.</summary>
    internal const string RepeatedPositionDrop = "RepeatedPositionDrop";

    /// <summary>Feature class: the compared primitive's signature has more normalized than native rows.</summary>
    internal const string MergedMaterialRow = "MergedMaterialRow";

    /// <summary>Every feature class the gate measures, in receipt order.</summary>
    internal static IReadOnlyList<string> FeatureClassNames { get; } =
    [
        Textured, NormalMapped, AlphaMask, AlphaBlend, DoubleSided, VertexColored, LitEmissive, MultiNodeHierarchy,
        RepeatedPositionDrop, MergedMaterialRow
    ];

    /// <summary>Compares two parsed GLBs and records every difference without throwing on the first.</summary>
    /// <param name="native">The native writer's parsed output, the oracle side.</param>
    /// <param name="shared">The normalized writer's parsed output, the side under test.</param>
    /// <param name="expectations">What the source proves about this pair.</param>
    /// <param name="cancellationToken">Cancels a large comparison.</param>
    /// <returns>The measured report; <see cref="NifGlbParityReport.Passed" /> is the verdict.</returns>
    internal static NifGlbParityReport Compare(ModelRoot native, ModelRoot shared,
        NifGlbParityExpectations expectations, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(native);
        ArgumentNullException.ThrowIfNull(shared);
        ArgumentNullException.ThrowIfNull(expectations);
        var report = new NifGlbParityReport();
        var pixels = new Dictionary<string, string>(StringComparer.Ordinal);
        var nativeModel = Decode(native, "native", expectations.NativeColorQuantization, pixels, report,
            cancellationToken);
        var sharedModel = Decode(shared, "normalized", expectations.SharedColorQuantization, pixels, report,
            cancellationToken);
        report.NativeTriangles = nativeModel.Triangles.Count;
        report.SharedTriangles = sharedModel.Triangles.Count;
        foreach (var primitive in nativeModel.Primitives)
        {
            NifGlbParityReport.Increment(report.NativeColorAccessors, primitive.ColorAccessor);
        }

        foreach (var primitive in sharedModel.Primitives)
        {
            NifGlbParityReport.Increment(report.SharedColorAccessors, primitive.ColorAccessor);
        }

        var retained = RetainedSharedTriangles(sharedModel, expectations, report);
        var pairs = Match(nativeModel, sharedModel, retained, report, cancellationToken);
        CompareTangents(nativeModel, sharedModel, pairs, expectations, report);
        var merged = CompareMaterials(nativeModel, sharedModel, retained, report);
        CompareNames(nativeModel, sharedModel, report);
        ComparePlacements(nativeModel, sharedModel, report, cancellationToken);
        CompareExtensions(nativeModel, sharedModel, report);
        RecordFeatures(sharedModel, merged, report);
        return report;
    }

    /// <summary>Compares two parsed GLBs and fails the calling test with the report summary when they differ.</summary>
    /// <param name="native">The native writer's parsed output.</param>
    /// <param name="shared">The normalized writer's parsed output.</param>
    /// <param name="expectations">What the source proves about this pair.</param>
    /// <param name="cancellationToken">Cancels a large comparison.</param>
    /// <returns>The passing report, for callers that also inspect its measurements.</returns>
    internal static NifGlbParityReport AssertEquivalent(ModelRoot native, ModelRoot shared,
        NifGlbParityExpectations expectations, CancellationToken cancellationToken = default)
    {
        var report = Compare(native, shared, expectations, cancellationToken);
        Assert.True(report.Passed, report.Summary());
        return report;
    }

    /// <summary>Reads every drawn primitive occurrence, material row, name and declared extension.</summary>
    private static NifGlbDecodedModel Decode(ModelRoot model, string side, NifGlbColorQuantization quantization,
        Dictionary<string, string> pixels, NifGlbParityReport report, CancellationToken cancellationToken)
    {
        var decoded = new NifGlbDecodedModel();
        foreach (var extension in model.ExtensionsUsed)
        {
            decoded.ExtensionsUsed.Add(extension);
        }

        var signatures = new string[model.LogicalMaterials.Count];
        foreach (var material in model.LogicalMaterials)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var signature = NifGlbMaterialSignature.Compute(material, pixels, cancellationToken, out var transformed);
            if (transformed)
            {
                report.Fail($"The {side} material '{material.Name}' carries a texture transform, which neither " +
                            "writer emits.");
            }

            signatures[material.LogicalIndex] = signature;
            decoded.MaterialRows[signature] = decoded.MaterialRows.GetValueOrDefault(signature) + 1;
            if (!string.IsNullOrEmpty(material.Name))
            {
                decoded.MaterialNames.Add(material.Name);
            }
        }

        foreach (var mesh in model.LogicalMeshes)
        {
            if (!string.IsNullOrEmpty(mesh.Name))
            {
                decoded.MeshNames.Add(mesh.Name);
            }

            foreach (var primitive in mesh.Primitives)
            {
                if (PrimitiveName(primitive) is { } name)
                {
                    decoded.PrimitiveNames.Add(name);
                }
            }
        }

        if (model.DefaultScene is null)
        {
            report.Fail($"The {side} GLB has no default scene.");
            return decoded;
        }

        foreach (var node in DrawNodes(model.DefaultScene.VisualChildren))
        {
            var depth = Depth(node);
            foreach (var primitive in node.Mesh!.Primitives)
            {
                cancellationToken.ThrowIfCancellationRequested();
                decoded.Placements.Add(node.WorldMatrix);
                DecodePrimitive(decoded, node, depth, primitive, side, quantization, signatures, report);
            }
        }

        return decoded;
    }

    /// <summary>Decodes one drawn primitive occurrence into corners and world-space triangles.</summary>
    private static void DecodePrimitive(NifGlbDecodedModel decoded, Node node, int depth, MeshPrimitive primitive,
        string side, NifGlbColorQuantization quantization, string[] signatures, NifGlbParityReport report)
    {
        var label = string.Create(CultureInfo.InvariantCulture,
            $"{side} mesh '{primitive.LogicalParent.Name}' primitive {primitive.LogicalIndex}");
        if (primitive.DrawPrimitiveType != PrimitiveType.TRIANGLES)
        {
            report.Fail($"The {label} draws {primitive.DrawPrimitiveType}, not triangles.");
            return;
        }

        var positionAccessor = primitive.GetVertexAccessor("POSITION");
        var normalAccessor = primitive.GetVertexAccessor("NORMAL");
        if (positionAccessor is null || normalAccessor is null)
        {
            report.Fail($"The {label} lacks a POSITION or NORMAL accessor.");
            return;
        }

        var positions = positionAccessor.AsVector3Array();
        var normals = normalAccessor.AsVector3Array();
        var uvs = primitive.GetVertexAccessor("TEXCOORD_0")?.AsVector2Array();
        var colorAccessor = primitive.GetVertexAccessor("COLOR_0");
        var colors = colorAccessor?.AsVector4Array();
        var tangents = primitive.GetVertexAccessor("TANGENT")?.AsVector4Array();
        var count = positions.Count;
        if (normals.Count != count || (uvs is not null && uvs.Count != count) ||
            (colors is not null && colors.Count != count) || (tangents is not null && tangents.Count != count))
        {
            report.Fail($"The {label} has vertex attributes of different lengths.");
            return;
        }

        var corners = new NifGlbDecodedCorner[count];
        var vertexColored = false;
        for (var vertex = 0; vertex < count; vertex++)
        {
            var (minimum, maximum) = colors is null
                ? (Vector4.One, Vector4.One)
                : ColorInterval(colors[vertex], colorAccessor!, quantization);
            vertexColored |= colors is not null && colors[vertex] != Vector4.One;
            corners[vertex] = new NifGlbDecodedCorner(positions[vertex], normals[vertex],
                uvs?[vertex] ?? Vector2.Zero, minimum, maximum, tangents is null ? null : tangents[vertex]);
        }

        var material = primitive.Material;
        var emissive = material?.FindChannel("Emissive");
        var primitiveOccurrence = decoded.Primitives.Count;
        decoded.Primitives.Add(new NifGlbDecodedPrimitive
        {
            MeshIndex = primitive.LogicalParent.LogicalIndex,
            PrimitiveIndex = primitive.LogicalIndex,
            MeshName = primitive.LogicalParent.Name,
            MaterialSignature = material is null
                ? NifGlbMaterialSignature.NoMaterial
                : signatures[material.LogicalIndex],
            HasBaseColorTexture = material?.FindChannel("BaseColor")?.Texture is not null,
            HasNormalTexture = material?.FindChannel("Normal")?.Texture is not null,
            AlphaMode = material?.Alpha.ToString() ?? "OPAQUE",
            DoubleSided = material?.DoubleSided ?? false,
            LitEmissive = material is { Unlit: false } && emissive is { } channel &&
                          (channel.Texture is not null ||
                           new Vector3(channel.Color.X, channel.Color.Y, channel.Color.Z) != Vector3.Zero),
            HasTangents = tangents is not null,
            ColorAccessor = Describe(colorAccessor),
            VertexColored = vertexColored,
            NodeDepth = depth
        });

        var world = WorldPositions(node, primitive, positions, label, report);
        var indices = primitive.GetIndices();
        if (indices.Count % 3 != 0)
        {
            report.Fail($"The {label} has {indices.Count} indices, which is not a whole number of triangles.");
            return;
        }

        for (var offset = 0; offset < indices.Count; offset += 3)
        {
            var a = indices[offset];
            var b = indices[offset + 1];
            var c = indices[offset + 2];
            if (a >= (uint)count || b >= (uint)count || c >= (uint)count)
            {
                report.Fail($"The {label} indexes outside its {count} vertices.");
                return;
            }

            decoded.Triangles.Add(new NifGlbDecodedTriangle(primitiveOccurrence,
                [corners[a], corners[b], corners[c]], [world[a], world[b], world[c]]));
            decoded.Observe(world[a]);
            decoded.Observe(world[b]);
            decoded.Observe(world[c]);
        }
    }

    /// <summary>Reads the shared writer's primitive label from its reserved extras key.</summary>
    private static string? PrimitiveName(MeshPrimitive primitive) =>
        primitive.Extras is JsonObject extras &&
        extras.TryGetPropertyValue("multitoolPrimitiveName", out var node) &&
        node is JsonValue value && value.TryGetValue<string>(out var name)
            ? name
            : null;

    /// <summary>Names an accessor's stored component type and normalization for the receipt.</summary>
    private static string Describe(Accessor? accessor)
    {
        if (accessor is null)
        {
            return "absent";
        }

        return accessor.Encoding == EncodingType.FLOAT
            ? "FLOAT"
            : accessor.Encoding + (accessor.Normalized ? " normalized" : " unnormalized");
    }

    /// <summary>The exact interval of input colors whose encoding decodes to <paramref name="decoded" />.</summary>
    private static (Vector4 Minimum, Vector4 Maximum) ColorInterval(Vector4 decoded, Accessor accessor,
        NifGlbColorQuantization quantization)
    {
        var scale = accessor.Normalized
            ? accessor.Encoding switch
            {
                EncodingType.UNSIGNED_BYTE => 255f,
                EncodingType.UNSIGNED_SHORT => 65535f,
                _ => 0f
            }
            : 0f;
        if (scale == 0f)
        {
            return (decoded, decoded);
        }

        Span<float> values = [decoded.X, decoded.Y, decoded.Z, decoded.W];
        Span<float> minimum = stackalloc float[4];
        Span<float> maximum = stackalloc float[4];
        for (var index = 0; index < 4; index++)
        {
            var step = MathF.Round(values[index] * scale);
            var (low, high) = quantization == NifGlbColorQuantization.Truncate
                ? (step / scale, (step + 1f) / scale)
                : ((step - 0.5f) / scale, (step + 0.5f) / scale);
            minimum[index] = Math.Clamp(low, 0f, 1f);
            maximum[index] = Math.Clamp(high, 0f, 1f);
        }

        return (new Vector4(minimum[0], minimum[1], minimum[2], minimum[3]),
            new Vector4(maximum[0], maximum[1], maximum[2], maximum[3]));
    }

    /// <summary>Walks the default scene, retaining each drawable node occurrence.</summary>
    private static IEnumerable<Node> DrawNodes(IEnumerable<Node> roots)
    {
        foreach (var node in roots)
        {
            if (node.Mesh is not null)
            {
                yield return node;
            }

            foreach (var child in DrawNodes(node.VisualChildren))
            {
                yield return child;
            }
        }
    }

    /// <summary>The number of ancestors between a node and its scene root.</summary>
    private static int Depth(Node node)
    {
        var depth = 0;
        for (var parent = node.VisualParent; parent is not null; parent = parent.VisualParent)
        {
            depth++;
        }

        return depth;
    }

    /// <summary>Evaluates rest-world positions, independently skinning from every encoded joint and weight set.</summary>
    private static Vector3[] WorldPositions(Node node, MeshPrimitive primitive, IReadOnlyList<Vector3> positions,
        string label, NifGlbParityReport report)
    {
        if (node.Skin is null)
        {
            var placement = node.WorldMatrix;
            return positions.Select(position => Vector3.Transform(position, placement)).ToArray();
        }

        var invalid = Enumerable.Repeat(new Vector3(float.NaN), positions.Count).ToArray();
        var jointSets = primitive.VertexAccessors.Keys
            .Where(static key => key.StartsWith("JOINTS_", StringComparison.Ordinal))
            .Select(static key => int.TryParse(key.AsSpan(7), NumberStyles.None, CultureInfo.InvariantCulture,
                out var set) ? set : -1)
            .Order().ToArray();
        var weightSets = primitive.VertexAccessors.Keys.Count(static key =>
            key.StartsWith("WEIGHTS_", StringComparison.Ordinal));
        if (jointSets.Length == 0 || !jointSets.SequenceEqual(Enumerable.Range(0, jointSets.Length)) ||
            weightSets != jointSets.Length)
        {
            report.Fail($"The skinned {label} lacks consecutive paired JOINTS_n and WEIGHTS_n accessors.");
            return invalid;
        }

        var worlds = new Vector3[positions.Count];
        var sums = new float[positions.Count];
        foreach (var set in jointSets)
        {
            var joints = primitive.GetVertexAccessor($"JOINTS_{set}").AsVector4Array();
            var weights = primitive.GetVertexAccessor($"WEIGHTS_{set}").AsVector4Array();
            if (joints.Count != positions.Count || weights.Count != positions.Count)
            {
                report.Fail($"The skinned {label} has a joint or weight set of the wrong length.");
                return invalid;
            }

            for (var vertex = 0; vertex < positions.Count; vertex++)
            {
                for (var slot = 0; slot < 4; slot++)
                {
                    var weight = weights[vertex][slot];
                    if (!float.IsFinite(weight) || weight < 0f)
                    {
                        report.Fail($"The skinned {label} has a negative or non-finite weight.");
                        return invalid;
                    }

                    sums[vertex] += weight;
                    if (weight == 0f)
                    {
                        continue;
                    }

                    var jointIndex = joints[vertex][slot];
                    if (MathF.Truncate(jointIndex) != jointIndex || jointIndex < 0f ||
                        jointIndex > node.Skin.JointsCount - 1)
                    {
                        report.Fail($"The skinned {label} references a joint outside its skin.");
                        return invalid;
                    }

                    var (joint, inverseBind) = node.Skin.GetJoint((int)jointIndex);
                    // glTF skinning already produces world coordinates; applying the mesh node's world transform
                    // again would transform the skin twice. Row vectors use bind * jointWorld.
                    worlds[vertex] += Vector3.Transform(positions[vertex], inverseBind * joint.WorldMatrix) * weight;
                }
            }
        }

        if (sums.Any(static sum => sum < 0.9999f || sum > 1.0001f))
        {
            report.Fail($"The skinned {label} has weights that do not sum to one.");
            return invalid;
        }

        return worlds;
    }

    /// <summary>Applies the repeated-position accounting and returns which normalized triangles stay in play.</summary>
    private static bool[] RetainedSharedTriangles(NifGlbDecodedModel shared, NifGlbParityExpectations expectations,
        NifGlbParityReport report)
    {
        var retained = new bool[shared.Triangles.Count];
        var removed = 0;
        for (var index = 0; index < shared.Triangles.Count; index++)
        {
            var triangle = shared.Triangles[index];
            var remove = expectations.RepeatedPositionTriangles is not null && triangle.IsRepeatedPosition;
            retained[index] = !remove;
            if (!remove)
            {
                continue;
            }

            removed++;
            shared.Primitives[triangle.Primitive].RemovedTriangles++;
        }

        report.RemovedSharedTriangles = removed;
        if (expectations.RepeatedPositionTriangles is { } expected && expected != removed)
        {
            report.Fail($"The normalized GLB holds {removed} triangle(s) with exactly repeated positions; the " +
                        $"source accounts for exactly {expected}.");
        }

        return retained;
    }

    /// <summary>Pairs native and retained normalized triangles: exact keys first, then tolerance neighbors.</summary>
    private static List<(int Native, int Shared, int Rotation)> Match(NifGlbDecodedModel native,
        NifGlbDecodedModel shared, bool[] retained, NifGlbParityReport report, CancellationToken cancellationToken)
    {
        var pairs = new List<(int Native, int Shared, int Rotation)>();
        var nativePaired = new bool[native.Triangles.Count];
        var sharedPaired = new bool[shared.Triangles.Count];
        for (var index = 0; index < retained.Length; index++)
        {
            sharedPaired[index] = !retained[index];
        }

        var exact = new Dictionary<string, Queue<(int Triangle, int Rotation)>>(StringComparer.Ordinal);
        for (var index = 0; index < shared.Triangles.Count; index++)
        {
            if (sharedPaired[index])
            {
                continue;
            }

            var key = ExactKey(shared, index, out var rotation);
            if (!exact.TryGetValue(key, out var queue))
            {
                queue = new Queue<(int Triangle, int Rotation)>();
                exact.Add(key, queue);
            }

            queue.Enqueue((index, rotation));
        }

        for (var index = 0; index < native.Triangles.Count; index++)
        {
            if ((index & 1023) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var key = ExactKey(native, index, out var nativeRotation);
            if (!exact.TryGetValue(key, out var queue) || queue.Count == 0)
            {
                continue;
            }

            var (partner, sharedRotation) = queue.Dequeue();
            Pair(index, partner, Mod3(sharedRotation - nativeRotation));
            report.ExactPairs++;
        }

        var cell = Math.Max(MinimumCell, 2e-6 * Math.Max(native.MaximumAbsoluteCoordinate,
            shared.MaximumAbsoluteCoordinate));
        var buckets = new Dictionary<(string Signature, long X, long Y, long Z), List<int>>();
        for (var index = 0; index < shared.Triangles.Count; index++)
        {
            if (sharedPaired[index] || Cell(shared.Triangles[index].Centroid, cell) is not { } key)
            {
                continue;
            }

            var bucketKey = (shared.Primitives[shared.Triangles[index].Primitive].MaterialSignature, key.X, key.Y,
                key.Z);
            if (!buckets.TryGetValue(bucketKey, out var bucket))
            {
                bucket = [];
                buckets.Add(bucketKey, bucket);
            }

            bucket.Add(index);
        }

        for (var index = 0; index < native.Triangles.Count; index++)
        {
            if ((index & 255) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (nativePaired[index])
            {
                continue;
            }

            var triangle = native.Triangles[index];
            if (Cell(triangle.Centroid, cell) is not { } origin)
            {
                continue;
            }

            var signature = native.Primitives[triangle.Primitive].MaterialSignature;
            var best = -1;
            var bestRotation = 0;
            var bestAgrees = false;
            var bestError = double.PositiveInfinity;
            for (var dx = -1L; dx <= 1; dx++)
            {
                for (var dy = -1L; dy <= 1; dy++)
                {
                    for (var dz = -1L; dz <= 1; dz++)
                    {
                        if (!buckets.TryGetValue((signature, origin.X + dx, origin.Y + dy, origin.Z + dz),
                                out var bucket))
                        {
                            continue;
                        }

                        foreach (var candidate in bucket)
                        {
                            if (sharedPaired[candidate])
                            {
                                continue;
                            }

                            for (var rotation = 0; rotation < 3; rotation++)
                            {
                                if (!Corresponds(triangle, shared.Triangles[candidate], rotation, out var error,
                                        out var agrees))
                                {
                                    continue;
                                }

                                if ((agrees && !bestAgrees) || (agrees == bestAgrees && error < bestError))
                                {
                                    (best, bestRotation, bestAgrees, bestError) = (candidate, rotation, agrees, error);
                                }
                            }
                        }
                    }
                }
            }

            if (best < 0)
            {
                continue;
            }

            Pair(index, best, bestRotation);
            report.TolerancePairs++;
        }

        for (var index = 0; index < native.Triangles.Count; index++)
        {
            if (nativePaired[index])
            {
                continue;
            }

            report.UnmatchedNativeTriangles++;
            report.Fail("Native triangle " + Describe(native, index) + " has no normalized counterpart.");
        }

        for (var index = 0; index < shared.Triangles.Count; index++)
        {
            if (sharedPaired[index])
            {
                continue;
            }

            report.UnmatchedSharedTriangles++;
            report.Fail("Normalized triangle " + Describe(shared, index) + " has no native counterpart.");
        }

        return pairs;

        void Pair(int nativeIndex, int sharedIndex, int rotation)
        {
            nativePaired[nativeIndex] = true;
            sharedPaired[sharedIndex] = true;
            pairs.Add((nativeIndex, sharedIndex, rotation));
            native.Primitives[native.Triangles[nativeIndex].Primitive].MatchedTriangles++;
            shared.Primitives[shared.Triangles[sharedIndex].Primitive].MatchedTriangles++;
        }
    }

    /// <summary>
    ///     Whether a normalized triangle, read from corner <paramref name="rotation" />, is within every tolerance of
    ///     a native triangle. Tangents do not decide correspondence; they only rank equally close candidates.
    /// </summary>
    private static bool Corresponds(NifGlbDecodedTriangle native, NifGlbDecodedTriangle shared, int rotation,
        out double error, out bool tangentsAgree)
    {
        error = 0;
        tangentsAgree = true;
        for (var corner = 0; corner < 3; corner++)
        {
            var expected = native.Corners[corner];
            var actual = shared.Corners[(corner + rotation) % 3];
            if (!Within(expected.Position, actual.Position, LocalPositionFloor, LocalPositionRelative, ref error) ||
                !Within(native.World[corner], shared.World[(corner + rotation) % 3], WorldPositionFloor,
                    WorldPositionRelative, ref error) ||
                !ComponentsWithin(expected.Normal, actual.Normal, DirectionTolerance, ref error) ||
                !ComponentsWithin(expected.TexCoord, actual.TexCoord, TexCoordTolerance, ref error) ||
                !ColorsOverlap(expected, actual))
            {
                return false;
            }

            tangentsAgree &= TangentsAgree(expected.Tangent, actual.Tangent);
        }

        return true;
    }

    /// <summary>Whether two positions are within a floor-or-relative distance, widening the running error.</summary>
    private static bool Within(Vector3 expected, Vector3 actual, float floor, float relative, ref double error)
    {
        var tolerance = Math.Max(floor, relative * Math.Max(expected.Length(), actual.Length()));
        var distance = Vector3.Distance(expected, actual);
        if (!(distance <= tolerance))
        {
            return false;
        }

        error = Math.Max(error, distance / tolerance);
        return true;
    }

    /// <summary>Whether every component of two directions is within a fixed tolerance.</summary>
    private static bool ComponentsWithin(Vector3 expected, Vector3 actual, float tolerance, ref double error)
    {
        var difference = Vector3.Abs(expected - actual);
        var largest = Math.Max(difference.X, Math.Max(difference.Y, difference.Z));
        if (!(largest <= tolerance))
        {
            return false;
        }

        error = Math.Max(error, largest / tolerance);
        return true;
    }

    /// <summary>Whether every component of two texture coordinates is within a fixed tolerance.</summary>
    private static bool ComponentsWithin(Vector2 expected, Vector2 actual, float tolerance, ref double error)
    {
        var difference = Vector2.Abs(expected - actual);
        var largest = Math.Max(difference.X, difference.Y);
        if (!(largest <= tolerance))
        {
            return false;
        }

        error = Math.Max(error, largest / tolerance);
        return true;
    }

    /// <summary>Whether two encoders could both have been given the same input color, within the slack.</summary>
    private static bool ColorsOverlap(NifGlbDecodedCorner expected, NifGlbDecodedCorner actual)
    {
        var low = Vector4.Max(expected.ColorMinimum, actual.ColorMinimum);
        var high = Vector4.Min(expected.ColorMaximum, actual.ColorMaximum);
        return low.X <= high.X + ColorSlack && low.Y <= high.Y + ColorSlack &&
               low.Z <= high.Z + ColorSlack && low.W <= high.W + ColorSlack;
    }

    /// <summary>Whether two optional tangents are both absent or agree in direction and exact handedness.</summary>
    private static bool TangentsAgree(Vector4? expected, Vector4? actual)
    {
        if (expected is null || actual is null)
        {
            return expected is null && actual is null;
        }

        var difference = Vector4.Abs(expected.Value - actual.Value);
        return difference.X <= DirectionTolerance && difference.Y <= DirectionTolerance &&
               difference.Z <= DirectionTolerance && expected.Value.W.Equals(actual.Value.W);
    }

    /// <summary>Compares paired tangents per primitive, ignoring only source-proven inert fallback tangents.</summary>
    private static void CompareTangents(NifGlbDecodedModel native, NifGlbDecodedModel shared,
        List<(int Native, int Shared, int Rotation)> pairs, NifGlbParityExpectations expectations,
        NifGlbParityReport report)
    {
        var ignored = new HashSet<int>();
        var authored = expectations.SharedPrimitiveHasAuthoredTangents;
        foreach (var (nativeIndex, sharedIndex, rotation) in pairs)
        {
            var expected = native.Triangles[nativeIndex];
            var actual = shared.Triangles[sharedIndex];
            var primitive = shared.Primitives[actual.Primitive];
            if (!primitive.HasNormalTexture && authored is not null &&
                !authored(primitive.MeshIndex, primitive.PrimitiveIndex))
            {
                ignored.Add(actual.Primitive);
                continue;
            }

            string tangentClass;
            if (primitive.HasNormalTexture)
            {
                tangentClass = "NormalMapped";
            }
            else
            {
                tangentClass = authored is null ? "UnknownAuthorship" : "AuthoredWithoutNormalMap";
            }

            for (var corner = 0; corner < 3; corner++)
            {
                var nativeTangent = expected.Corners[corner].Tangent;
                var sharedTangent = actual.Corners[(corner + rotation) % 3].Tangent;
                if (TangentsAgree(nativeTangent, sharedTangent))
                {
                    continue;
                }

                string divergence;
                if (sharedTangent is null)
                {
                    divergence = tangentClass + ": normalized TANGENT absent";
                }
                else if (nativeTangent is null)
                {
                    divergence = tangentClass + ": native TANGENT absent";
                }
                else
                {
                    divergence = tangentClass;
                }

                NifGlbParityReport.Increment(report.TangentDivergences, divergence);
                report.Fail(string.Create(CultureInfo.InvariantCulture,
                    $"Tangent divergence ({divergence}) at corner {corner} of native triangle " +
                    $"{Describe(native, nativeIndex)}: native {Format(nativeTangent)}, " +
                    $"normalized {Format(sharedTangent)}."));
                break;
            }
        }

        report.IgnoredFallbackTangentPrimitives = ignored.Count;
    }

    /// <summary>
    ///     Requires equal distinct content signatures, excluding normalized-only signatures referenced solely by
    ///     removed repeated-position triangles, and never fewer normalized than native rows per signature.
    /// </summary>
    /// <returns>The signatures with more normalized than native rows.</returns>
    private static HashSet<string> CompareMaterials(NifGlbDecodedModel native, NifGlbDecodedModel shared,
        bool[] retained, NifGlbParityReport report)
    {
        var kept = new HashSet<string>(StringComparer.Ordinal);
        var removed = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < shared.Triangles.Count; index++)
        {
            var signature = shared.Primitives[shared.Triangles[index].Primitive].MaterialSignature;
            (retained[index] ? kept : removed).Add(signature);
        }

        removed.ExceptWith(kept);
        report.RemovedOnlyMaterialSignatures = removed.Count;
        foreach (var signature in shared.MaterialRows.Keys)
        {
            if (!removed.Contains(signature) && !native.MaterialRows.ContainsKey(signature))
            {
                report.Fail($"Normalized material content {NifGlbMaterialSignature.Short(signature)} has no native " +
                            "row with the same content.");
            }
        }

        var merged = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (signature, nativeRows) in native.MaterialRows)
        {
            var sharedRows = shared.MaterialRows.GetValueOrDefault(signature);
            if (sharedRows == 0)
            {
                report.Fail($"Native material content {NifGlbMaterialSignature.Short(signature)} is absent from the " +
                            "normalized GLB.");
            }
            else if (removed.Contains(signature))
            {
                report.Fail($"Material content {NifGlbMaterialSignature.Short(signature)} is referenced only by " +
                            "removed repeated-position triangles, yet the native writer kept a row for it.");
            }
            else if (sharedRows < nativeRows)
            {
                report.Fail($"Material content {NifGlbMaterialSignature.Short(signature)} has {sharedRows} " +
                            $"normalized row(s), fewer than the native {nativeRows}.");
            }
            else if (sharedRows > nativeRows)
            {
                merged.Add(signature);
            }
        }

        report.MergedMaterialRowSignatures = merged.Count;
        return merged;
    }

    /// <summary>Requires every native mesh name among normalized mesh or primitive names, and material names too.</summary>
    private static void CompareNames(NifGlbDecodedModel native, NifGlbDecodedModel shared, NifGlbParityReport report)
    {
        foreach (var name in native.MeshNames.Order(StringComparer.Ordinal))
        {
            if (!shared.MeshNames.Contains(name) && !shared.PrimitiveNames.Contains(name))
            {
                report.Fail($"Native mesh name '{name}' appears as no normalized mesh or primitive name.");
            }
        }

        foreach (var name in native.MaterialNames.Order(StringComparer.Ordinal))
        {
            if (!shared.MaterialNames.Contains(name))
            {
                report.Fail($"Native material name '{name}' appears as no normalized material name.");
            }
        }
    }

    /// <summary>Requires the native per-primitive placement multiset to be contained in the normalized one.</summary>
    private static void ComparePlacements(NifGlbDecodedModel native, NifGlbDecodedModel shared,
        NifGlbParityReport report, CancellationToken cancellationToken)
    {
        var used = new bool[shared.Placements.Count];
        foreach (var placement in native.Placements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var best = -1;
            var bestError = double.PositiveInfinity;
            for (var index = 0; index < shared.Placements.Count; index++)
            {
                if (!used[index] && MatricesClose(placement, shared.Placements[index], out var error) &&
                    error < bestError)
                {
                    (best, bestError) = (index, error);
                }
            }

            if (best < 0)
            {
                var translation = placement.Translation.ToString(null, CultureInfo.InvariantCulture);
                report.Fail($"A native primitive placed at world translation {translation} has no normalized " +
                            "placement within tolerance.");
                continue;
            }

            used[best] = true;
        }
    }

    /// <summary>Compares two world matrices: linear terms like local positions, translation like world positions.</summary>
    private static bool MatricesClose(Matrix4x4 expected, Matrix4x4 actual, out double error)
    {
        error = 0;
        ReadOnlySpan<float> linearExpected =
        [
            expected.M11, expected.M12, expected.M13, expected.M21, expected.M22, expected.M23,
            expected.M31, expected.M32, expected.M33
        ];
        ReadOnlySpan<float> linearActual =
        [
            actual.M11, actual.M12, actual.M13, actual.M21, actual.M22, actual.M23,
            actual.M31, actual.M32, actual.M33
        ];
        for (var index = 0; index < linearExpected.Length; index++)
        {
            var tolerance = Math.Max(LocalPositionFloor,
                LocalPositionRelative * Math.Max(Math.Abs(linearExpected[index]), Math.Abs(linearActual[index])));
            var difference = Math.Abs(linearExpected[index] - linearActual[index]);
            if (!(difference <= tolerance))
            {
                return false;
            }

            error = Math.Max(error, difference / tolerance);
        }

        if (!(Math.Abs(expected.M14 - actual.M14) <= 1e-6f && Math.Abs(expected.M24 - actual.M24) <= 1e-6f &&
              Math.Abs(expected.M34 - actual.M34) <= 1e-6f && Math.Abs(expected.M44 - actual.M44) <= 1e-6f))
        {
            return false;
        }

        return Within(expected.Translation, actual.Translation, WorldPositionFloor, WorldPositionRelative,
            ref error);
    }

    /// <summary>Requires identical declared extension sets.</summary>
    private static void CompareExtensions(NifGlbDecodedModel native, NifGlbDecodedModel shared,
        NifGlbParityReport report)
    {
        if (!native.ExtensionsUsed.SetEquals(shared.ExtensionsUsed))
        {
            report.Fail($"ExtensionsUsed differ: native [{string.Join(", ", native.ExtensionsUsed)}], normalized " +
                        $"[{string.Join(", ", shared.ExtensionsUsed)}].");
        }
    }

    /// <summary>Counts the compared normalized primitive occurrences in each feature class.</summary>
    private static void RecordFeatures(NifGlbDecodedModel shared, HashSet<string> merged, NifGlbParityReport report)
    {
        foreach (var primitive in shared.Primitives)
        {
            if (primitive.RemovedTriangles > 0)
            {
                NifGlbParityReport.Increment(report.FeatureClasses, RepeatedPositionDrop);
            }

            if (primitive.MatchedTriangles == 0)
            {
                continue;
            }

            var classes = new (bool Applies, string Name)[]
            {
                (primitive.HasBaseColorTexture, Textured),
                (primitive.HasNormalTexture, NormalMapped),
                (primitive.AlphaMode == nameof(SharpGLTF.Schema2.AlphaMode.MASK), AlphaMask),
                (primitive.AlphaMode == nameof(SharpGLTF.Schema2.AlphaMode.BLEND), AlphaBlend),
                (primitive.DoubleSided, DoubleSided),
                (primitive.VertexColored, VertexColored),
                (primitive.LitEmissive, LitEmissive),
                (primitive.NodeDepth >= 2, MultiNodeHierarchy),
                (merged.Contains(primitive.MaterialSignature), MergedMaterialRow)
            };
            foreach (var (applies, name) in classes)
            {
                if (applies)
                {
                    NifGlbParityReport.Increment(report.FeatureClasses, name);
                }
            }
        }
    }

    /// <summary>
    ///     A bit-exact key over the material signature and every corner attribute and world position, starting at
    ///     the corner that makes the key smallest so equal triangles written from different corners still meet.
    /// </summary>
    private static string ExactKey(NifGlbDecodedModel model, int index, out int rotation)
    {
        var triangle = model.Triangles[index];
        var signature = model.Primitives[triangle.Primitive].MaterialSignature;
        string? best = null;
        rotation = 0;
        for (var start = 0; start < 3; start++)
        {
            var key = new StringBuilder(signature, 512);
            for (var offset = 0; offset < 3; offset++)
            {
                var corner = (start + offset) % 3;
                var value = triangle.Corners[corner];
                key.Append('|');
                Bits(key, value.Position);
                Bits(key, value.Normal);
                Bits(key, value.TexCoord.X);
                Bits(key, value.TexCoord.Y);
                Bits(key, value.ColorMinimum);
                Bits(key, value.ColorMaximum);
                if (value.Tangent is { } tangent)
                {
                    Bits(key, tangent);
                }
                else
                {
                    key.Append("no-tangent,");
                }

                Bits(key, triangle.World[corner]);
            }

            var text = key.ToString();
            if (best is null || StringComparer.Ordinal.Compare(text, best) < 0)
            {
                (best, rotation) = (text, start);
            }
        }

        return best!;
    }

    /// <summary>Appends a vector's exact bits.</summary>
    private static void Bits(StringBuilder key, Vector3 value)
    {
        Bits(key, value.X);
        Bits(key, value.Y);
        Bits(key, value.Z);
    }

    /// <summary>Appends a vector's exact bits.</summary>
    private static void Bits(StringBuilder key, Vector4 value)
    {
        Bits(key, value.X);
        Bits(key, value.Y);
        Bits(key, value.Z);
        Bits(key, value.W);
    }

    /// <summary>Appends one value's exact bits, treating signed zero identically.</summary>
    private static void Bits(StringBuilder key, float value)
    {
        key.Append((value == 0f ? 0 : BitConverter.SingleToInt32Bits(value)).ToString("X8",
            CultureInfo.InvariantCulture)).Append(',');
    }

    /// <summary>The spatial cell holding a centroid, or null when the centroid is not finite.</summary>
    private static (long X, long Y, long Z)? Cell(Vector3 centroid, double size)
    {
        if (!float.IsFinite(centroid.X) || !float.IsFinite(centroid.Y) || !float.IsFinite(centroid.Z))
        {
            return null;
        }

        return ((long)Math.Floor(centroid.X / size), (long)Math.Floor(centroid.Y / size),
            (long)Math.Floor(centroid.Z / size));
    }

    /// <summary>The nonnegative remainder of a rotation difference.</summary>
    private static int Mod3(int value) => ((value % 3) + 3) % 3;

    /// <summary>A failure-message description of one triangle occurrence.</summary>
    private static string Describe(NifGlbDecodedModel model, int index)
    {
        var triangle = model.Triangles[index];
        var primitive = model.Primitives[triangle.Primitive];
        return string.Create(CultureInfo.InvariantCulture,
            $"{index} in mesh '{primitive.MeshName}' primitive {primitive.PrimitiveIndex} (material " +
            $"{NifGlbMaterialSignature.Short(primitive.MaterialSignature)}, world {triangle.World[0]} " +
            $"{triangle.World[1]} {triangle.World[2]})");
    }

    /// <summary>Formats an optional tangent for a failure message.</summary>
    private static string Format(Vector4? tangent) =>
        tangent is { } value ? value.ToString(null, CultureInfo.InvariantCulture) : "absent";
}
