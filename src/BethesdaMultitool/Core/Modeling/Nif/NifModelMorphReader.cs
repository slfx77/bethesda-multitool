using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using BethesdaMultitool.Core.Formats.Nif.Decoding;
using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     Reads a geometry block's morph targets (plan section 3, the NiMorphData row): the first NiGeomMorpherController on
///     the shape's controller chain names an NiMorphData, whose morphs 1..n become one <see cref="SceneMorphTarget" />
///     each, named by their Frame Name. Relative Targets 1 gives position deltas, 0 gives absolute positions; morph 0 is
///     the base and is compared bit for bit with the stored positions, with a diagnostic on any difference.
/// </summary>
/// <remarks>
///     Weights, keys and interpolators are animation (cut 1b): the controller stays NativeOnly and the mesh declares no
///     rest weights (implicit zero, Assumed). Packed console geometry (slice 10): the morph vectors stay in the shape
///     vertex domain and are gathered into the primitive's packed order through its point indices (exact); morph 0 is
///     compared with the packed positions after rounding to binary16, the format's own quantization, because the
///     packed positions are halves. When the morph data cannot be typed exactly (a vertex count that differs
///     from its geometry, a Relative Targets value other than 0 or 1, or a non-finite target vector, which Shared would
///     reject), the primitive carries no targets, a diagnostic says why, and the NiMorphData block is NativeOnly with that
///     reason.
/// </remarks>
internal static class NifModelMorphReader
{
    /// <summary>The controller type that drives geometry morphs.</summary>
    public const string MorpherControllerType = "NiGeomMorpherController";

    /// <summary>The morph data type.</summary>
    public const string MorphDataType = "NiMorphData";

    /// <summary>Diagnostic code for a morph 0 whose vectors differ from the stored positions.</summary>
    public const string BaseMismatchDiagnostic = "bmt.nif.morph-base-mismatch";

    /// <summary>Diagnostic code for morph data kept as native state only.</summary>
    public const string NotTypedDiagnostic = "bmt.nif.morph-not-typed";

    /// <summary>Reads the morphs of one drawable geometry.</summary>
    /// <param name="state">The read state.</param>
    /// <param name="shape">The placed geometry block.</param>
    /// <param name="data">Its drawable data.</param>
    /// <param name="diagnostics">Where morph diagnostics go.</param>
    /// <returns>The targets (empty when there are none or they cannot be typed) and the facts.</returns>
    public static NifModelMorphResult Read(NifModelReadState state, NifDecodedBlock shape, NifModelGeometryData data,
        NifModelDiagnosticSink diagnostics)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(shape);
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(diagnostics);

        var morphers = Morphers(state, shape);
        if (morphers.Count == 0)
        {
            return NifModelMorphResult.None;
        }

        var dispositions = new Dictionary<int, NifModelBlockDisposition>();
        for (var i = 1; i < morphers.Count; i++)
        {
            if (Link(state, state.Blocks[morphers[i]], "Data") is { } extra &&
                string.Equals(state.Blocks[extra].Type, MorphDataType, StringComparison.Ordinal))
            {
                dispositions.TryAdd(extra, NifModelBlockDisposition.NativeOnly(NifModelCoverage.ExtraMorpherReason));
            }

            diagnostics.Add(NotTypedDiagnostic, string.Create(CultureInfo.InvariantCulture,
                $"Block {shape.Index} ({shape.Type}) carries more than one {MorpherControllerType}; only block " +
                $"{morphers[0]} is typed, block {morphers[i]} stays native state."));
        }

        var controller = state.Blocks[morphers[0]];
        var facts = new JsonObject { ["controllerBlock"] = controller.Index };
        if (!controller.Root.TryGet("Data", out var dataValue) || dataValue is not NifRefValue dataLink ||
            (!dataLink.IsNone && (uint)dataLink.Index >= (uint)state.Blocks.Count))
        {
            diagnostics.Add(NotTypedDiagnostic, string.Create(CultureInfo.InvariantCulture,
                $"Block {controller.Index} ({controller.Type}) did not decode a usable Data link; the morphs of block " +
                $"{shape.Index} are not typed."));
            facts["typed"] = false;
            return new NifModelMorphResult([], null, dispositions, facts);
        }

        if (dataLink.IsNone ||
            !string.Equals(state.Blocks[dataLink.Index].Type, MorphDataType, StringComparison.Ordinal))
        {
            facts["dataBlock"] = dataLink.Index;
            facts["typed"] = false;
            return new NifModelMorphResult([], null, dispositions, facts);
        }

        var morphBlock = state.Blocks[dataLink.Index];
        facts["dataBlock"] = morphBlock.Index;
        var (targets, reason, detail) = ReadTargets(morphBlock, data, facts);
        if (reason is not null)
        {
            dispositions[morphBlock.Index] = NifModelBlockDisposition.NativeOnly(reason);
            if (detail is not null)
            {
                diagnostics.Add(NotTypedDiagnostic, string.Create(CultureInfo.InvariantCulture,
                    $"Block {morphBlock.Index} ({morphBlock.Type}) for block {shape.Index} ({shape.Type}): {detail}"));
            }

            facts["typed"] = false;
            facts["reason"] = reason;
        }
        else
        {
            dispositions[morphBlock.Index] = NifModelBlockDisposition.Typed;
            facts["typed"] = true;
            facts["restWeights"] = "none authored: implicit zero (Assumed); weights and keys are cut 1b animation";
        }

        if (facts["morph0FirstMismatch"] is JsonValue mismatch)
        {
            var vertex = mismatch.GetValue<int>();
            diagnostics.Add(BaseMismatchDiagnostic, string.Create(CultureInfo.InvariantCulture,
                $"Block {morphBlock.Index} ({morphBlock.Type}): morph 0 differs from the positions of block " +
                $"{data.BlockIndex} ({data.BlockType}), first at vertex {vertex} (bit-for-bit comparison); " +
                $"morph 0 is the base and is not emitted as a target."));
        }

        return new NifModelMorphResult(targets, reason is null ? morphBlock.Index : null, dispositions, facts);
    }

    private static (IReadOnlyList<SceneMorphTarget> Targets, string? Reason, string? Detail) ReadTargets(
        NifDecodedBlock morphBlock, NifModelGeometryData data, JsonObject facts)
    {
        var none = Array.Empty<SceneMorphTarget>();
        var root = morphBlock.Root;
        var declaredVertices = Integer(morphBlock, "Num Vertices");
        var relative = Integer(morphBlock, "Relative Targets");
        var morphs = root.Get<NifArrayValue>("Morphs");
        facts["numMorphs"] = morphs.Count;
        facts["numVertices"] = declaredVertices;
        facts["relativeTargets"] = relative;

        if (declaredVertices != data.VertexCount)
        {
            return (none, NifModelCoverage.MorphVertexCountReason, string.Create(CultureInfo.InvariantCulture,
                $"it declares {declaredVertices} vertices, the geometry {data.VertexCount}; its morphs are not typed."));
        }

        var vertexCount = data.VertexCount;
        var vectors = new NifFloatArrayValue[morphs.Count];
        var names = new string[morphs.Count];
        for (var m = 0; m < morphs.Count; m++)
        {
            if (morphs.Items[m] is not NifStructValue morph ||
                !morph.TryGet("Vectors", out var value) || value is not NifFloatArrayValue array ||
                array.ComponentsPerElement != 3 || array.Count != vertexCount)
            {
                throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture,
                    $"NIF block {morphBlock.Index} ({morphBlock.Type}) morph {m} did not decode {vertexCount} vectors."));
            }

            vectors[m] = array;
            names[m] = morph.TryGet("Frame Name", out var name) && name is NifStringValue { Text: { } text }
                ? text
                : "";
        }

        if (morphs.Count > 0)
        {
            var first = data.Packed is null
                ? FirstDifference(vectors[0], data.Positions!)
                : FirstDifferenceThroughHalf(vectors[0], data);
            facts["morph0Comparison"] = data.Packed is null
                ? "bit for bit against the stored positions"
                : "the base rounded to binary16 (nearest even) against the exactly widened packed halves, primitive " +
                  "vertex by primitive vertex through the point indices";
            facts["morph0MatchesBase"] = first < 0;
            if (first >= 0)
            {
                facts["morph0FirstMismatch"] = first;
            }
        }

        facts["frameNames"] = NifModelNativeValues.Texts(names);
        if (morphs.Count <= 1)
        {
            return (none, NifModelCoverage.NoMorphTargetsReason, null);
        }

        if (relative is not (0 or 1))
        {
            return (none, NifModelCoverage.MorphRelativeTargetsReason, string.Create(CultureInfo.InvariantCulture,
                $"Relative Targets is {relative}, whose meaning is not established; its morphs are not typed."));
        }

        for (var m = 1; m < morphs.Count; m++)
        {
            foreach (var bits in vectors[m].Bits)
            {
                if (!float.IsFinite(BitConverter.UInt32BitsToSingle(bits)))
                {
                    return (none, NifModelCoverage.MorphNonFiniteReason, string.Create(CultureInfo.InvariantCulture,
                        $"morph {m} holds a non-finite vector component, which Shared rejects; " +
                        $"its morphs are not typed."));
                }
            }
        }

        // The targets are in the primitive's vertex domain: the shape vertices themselves, or, for packed skinned
        // geometry, each primitive vertex's shape vertex through the point indices (a gather, exact).
        var points = data.PointIndices?.Values;
        var targets = new SceneMorphTarget[morphs.Count - 1];
        for (var m = 1; m < morphs.Count; m++)
        {
            var values = new Vector3[data.Vertices.Count];
            for (var i = 0; i < values.Length; i++)
            {
                var point = points is null ? i : points[i];
                values[i] = new Vector3(vectors[m].Get(point, 0), vectors[m].Get(point, 1), vectors[m].Get(point, 2));
            }

            targets[m - 1] = relative == 1
                ? new SceneMorphTarget(names[m], values)
                : new SceneMorphTarget(names[m], [], absolutePositions: values);
        }

        facts["targetForm"] = relative == 1 ? "positionDeltas" : "absolutePositions";
        facts["targetVertexDomain"] = points is null
            ? "shape vertices"
            : string.Create(CultureInfo.InvariantCulture,
                $"{data.Vertices.Count} packed vertices, each gathering its shape vertex's vector through the point " +
                $"indices");
        return (targets, null, null);
    }

    /// <summary>
    ///     The first primitive vertex whose base vector, rounded to binary16 (nearest even, the format's own
    ///     quantization) and widened, differs from the packed position; -1 when none does.
    /// </summary>
    private static int FirstDifferenceThroughHalf(NifFloatArrayValue morph0, NifModelGeometryData data)
    {
        var positions = data.Positions!;
        var points = data.PointIndices?.Values;
        for (var i = 0; i < data.Vertices.Count; i++)
        {
            var point = points is null ? i : points[i];
            for (var c = 0; c < 3; c++)
            {
                var rounded = NifPackedHalf.ToSingle(BitConverter.HalfToUInt16Bits((Half)morph0.Get(point, c)));
                if (BitConverter.SingleToUInt32Bits(rounded) != positions.GetBits(i, c))
                {
                    return i;
                }
            }
        }

        return -1;
    }

    /// <summary>
    ///     The NiMorphData blocks the shape's morpher controllers link, for a geometry that yields no primitive: they then
    ///     feed nothing and take the geometry's own NativeOnly reason.
    /// </summary>
    public static IReadOnlyList<int> MorphDataBlocks(NifModelReadState state, NifDecodedBlock shape)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(shape);
        var blocks = new List<int>();
        foreach (var morpher in Morphers(state, shape))
        {
            if (Link(state, state.Blocks[morpher], "Data") is { } data &&
                string.Equals(state.Blocks[data].Type, MorphDataType, StringComparison.Ordinal))
            {
                blocks.Add(data);
            }
        }

        return blocks;
    }

    /// <summary>The NiGeomMorpherController blocks on the shape's controller chain, in chain order.</summary>
    private static List<int> Morphers(NifModelReadState state, NifDecodedBlock shape)
    {
        var morphers = new List<int>();
        var visited = new HashSet<int>();
        var next = Link(state, shape, "Controller");
        while (next is { } index && visited.Add(index))
        {
            var controller = state.Blocks[index];
            if (!state.Schema.Inherits(controller.Type, "NiTimeController"))
            {
                break;
            }

            if (string.Equals(controller.Type, MorpherControllerType, StringComparison.Ordinal))
            {
                morphers.Add(index);
            }

            next = Link(state, controller, "Next Controller");
        }

        return morphers;
    }

    /// <summary>A non-null link that names a block of the file (a tolerant decode can keep an out-of-range index).</summary>
    private static int? Link(NifModelReadState state, NifDecodedBlock block, string field)
    {
        return block.Root.TryGet(field, out var value) && value is NifRefValue { IsNone: false } link &&
               (uint)link.Index < (uint)state.Blocks.Count
            ? link.Index
            : null;
    }

    private static long Integer(NifDecodedBlock block, string field)
    {
        return block.Root.TryGet(field, out var value) && value is NifIntegerValue integer
            ? integer.Value
            : throw new InvalidDataException(
                $"NIF block {block.Index} ({block.Type}) did not decode the integer '{field}'.");
    }

    private static int FirstDifference(NifFloatArrayValue a, NifFloatArrayValue b)
    {
        var left = a.Bits;
        var right = b.Bits;
        for (var i = 0; i < Math.Min(left.Length, right.Length); i++)
        {
            if (left[i] != right[i])
            {
                return i / 3;
            }
        }

        return left.Length == right.Length ? -1 : Math.Min(left.Length, right.Length) / 3;
    }
}
