using System.Globalization;
using System.Text.Json.Nodes;
using BethesdaMultitool.Core.Formats.Nif.Decoding;
using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     Builds the <see cref="NifModelGeometryData" /> of a data block whose streams live in a BSPackedAdditionalGeometryData
///     (plan section 3, "Packed-stream layout table" and "Skinned Xbox geometry"; section 6, slice 10), for the X360 and
///     PS3 FNV files. The packed block is decoded by <see cref="NifPackedGeometryDecoder" />; this reader settles the
///     vertex order and the triangles, which depend on whether the layout is skinned.
/// </summary>
/// <remarks>
///     <para>
///         Static layouts (L1, L2, L5, L6): packed vertex i is shape vertex i (measured: positions match the PC file
///         under the identity on 100% and the console index buffers are byte-identical to the PC ones), so the packed
///         count must equal Num Vertices and the triangles are the data block's own list or strips, read exactly as
///         for inline streams.
///     </para>
///     <para>
///         Skinned layouts (L3, L4): the packed vertices are the concatenated NiSkinPartition vertex maps (measured:
///         the packed count equals the sum of the partition counts on every retail shape, and the console partitions
///         equal the PC ones in count, maps, bone lists and faces). The primitive's vertex domain is that partition
///         order, exactly and never welded: <see cref="ScenePointIndices" /> over Num Vertices names each vertex's shape
///         vertex, and the triangles are each partition's triangles or strips offset by the partition's first primitive
///         vertex. The partitions are read and checked once here and handed to the skin reader through
///         <see cref="NifModelGeometryData.PackedPartitions" />.
///     </para>
///     <para>
///         Not typed, with the reason on the geometry, its data and the packed block and a
///         <see cref="NotTypedDiagnostic" />: an unknown layout (<see cref="NifModelCoverage.PackedLayoutUnknownReason" />);
///         an empty or absent payload beside a non-zero vertex count, a packed count that is not Num Vertices (static)
///         or the sum of the partition counts (skinned), a skinned layout on a shape without a partition that maps
///         every vertex, a partition that did not decode exactly, or a shape vertex repeated across partitions with
///         differing packed positions (<see cref="NifModelCoverage.PackedPayloadReason" />); and a shape that has a
///         skin instance but a layout without weights, whose vertex order was not measured
///         (<see cref="NifModelCoverage.PackedSkinnedStaticLayoutReason" />; no retail shape does this: measured
///         2026-09-24 per shape over the console Meshes BSAs,
///         TestOutput/packed-semantics-20260924/per_shape_skin_layout.py: X360 4,244 of 4,250 shapes with a skin
///         instance carry a packed block and every one is L3 (3,777) or L4 (467), the other 6 carry inline streams;
///         PS3 4,122 of 4,128, L3 3,657 / L4 465; 0 skinned shapes use L1, L2, L5 or L6 on either console; the 134 L1
///         and 6 L2 blocks the per-file census counts in skin-carrying files all belong to static shapes). A packed
///         block that decoded partially is corrupt input like any strictly decoded block. A geometry with no kept
///         triangle, or a packed block declaring zero vertices, is empty
///         (<see cref="NifModelCoverage.EmptyGeometryReason" />), as for inline streams.
///     </para>
///     <para>
///         When the platform was assumed and a typed primitive carries the layout's color channel,
///         <see cref="PlatformAssumedDiagnostic" /> says so once per packed block, because the color byte order is the
///         one thing the two consoles disagree on; a block kept as native state raises no platform diagnostic, because
///         no color was decoded into typed state. A typed primitive on a layout whose color byte order is carried over
///         rather than measured (L6, <see cref="NifPackedGeometryLayout.ColorByteOrderInferred" />) raises
///         <see cref="ColorOrderInferredDiagnostic" /> once per packed block whether or not the platform was declared,
///         with the count of vertices on which the two orders would differ.
///     </para>
/// </remarks>
internal static class NifPackedGeometryReader
{
    /// <summary>Diagnostic code for packed geometry kept as native state only.</summary>
    public const string NotTypedDiagnostic = "bmt.nif.packed-not-typed";

    /// <summary>Diagnostic code for a packed color channel typed under the assumed (default) platform.</summary>
    public const string PlatformAssumedDiagnostic = "bmt.nif.packed-platform-assumed";

    /// <summary>
    ///     Diagnostic code for a packed color channel typed on a layout whose byte order is carried over from another
    ///     layout rather than measured (L6).
    /// </summary>
    public const string ColorOrderInferredDiagnostic = "bmt.nif.packed-color-order-inferred";

    /// <summary>The message stem of <see cref="PlatformAssumedDiagnostic" />.</summary>
    public const string PlatformAssumedMessage =
        "color byte order assumed X360 ARGB; pass --platform ps3 for a PS3 file";

    /// <summary>Reads the packed form of one data block for one geometry block.</summary>
    /// <param name="state">The read state.</param>
    /// <param name="shape">The placed geometry block (its Skin Instance decides the skinned vertex order).</param>
    /// <param name="dataBlock">The NiTriShapeData or NiTriStripsData block, decoded strictly.</param>
    /// <param name="packedBlock">The BSPackedAdditionalGeometryData block the data names, decoded strictly.</param>
    /// <param name="platform">The platform whose color byte order applies.</param>
    /// <param name="diagnostics">Where the packed diagnostics go.</param>
    /// <param name="cancellationToken">Observed between stages.</param>
    /// <exception cref="InvalidDataException">
    ///     The packed block did not decode exactly, a required channel is not finite, a partition or index is out of
    ///     range, or the shape's skin links name blocks of the wrong type.
    /// </exception>
    public static NifModelGeometryData Read(NifModelReadState state, NifDecodedBlock shape, NifDecodedBlock dataBlock,
        NifDecodedBlock packedBlock, NifPackedPlatformSelection platform, NifModelDiagnosticSink diagnostics,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(shape);
        ArgumentNullException.ThrowIfNull(dataBlock);
        ArgumentNullException.ThrowIfNull(packedBlock);
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentNullException.ThrowIfNull(diagnostics);
        RequireComplete(packedBlock);
        if (!NifPackedGeometryDecoder.TryDecode(packedBlock, platform, out var packed, out var reason, out var detail))
        {
            return NotTyped(dataBlock, packedBlock, shape, reason!, detail!, diagnostics);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var vertexCount = NifModelGeometryData.NumVertices(dataBlock);
        var skinInstance = SkinInstanceLink(state, shape);
        if (!packed!.Layout.IsSkinned)
        {
            if (skinInstance is not null)
            {
                return NotTyped(dataBlock, packedBlock, shape, NifModelCoverage.PackedSkinnedStaticLayoutReason,
                    string.Create(CultureInfo.InvariantCulture,
                        $"block {shape.Index} names skin instance {skinInstance.Value} but layout {packed.Layout.Id} " +
                        $"carries no weights; no retail shape does this (measured per shape on both consoles, " +
                        $"TestOutput/packed-semantics-20260924/per_shape_skin_layout.py) and its vertex order was " +
                        $"not measured."),
                    diagnostics);
            }

            return ReadStatic(dataBlock, packedBlock, shape, packed, vertexCount, diagnostics, cancellationToken);
        }

        if (skinInstance is null)
        {
            return NotTyped(dataBlock, packedBlock, shape, NifModelCoverage.PackedPayloadReason,
                string.Create(CultureInfo.InvariantCulture,
                    $"layout {packed.Layout.Id} is skinned but block {shape.Index} names no skin instance, so no " +
                    $"partition gives the packed vertex order."), diagnostics);
        }

        return ReadSkinned(state, dataBlock, packedBlock, shape, skinInstance.Value, packed, vertexCount, diagnostics,
            cancellationToken);
    }

    /// <summary>The static form: identity order, the data block's own triangles.</summary>
    private static NifModelGeometryData ReadStatic(NifDecodedBlock dataBlock, NifDecodedBlock packedBlock,
        NifDecodedBlock shape, NifPackedGeometryStreams packed, int vertexCount, NifModelDiagnosticSink diagnostics,
        CancellationToken cancellationToken)
    {
        if (packed.VertexCount != vertexCount)
        {
            return NotTyped(dataBlock, packedBlock, shape, NifModelCoverage.PackedPayloadReason,
                string.Create(CultureInfo.InvariantCulture,
                    $"the packed block stores {packed.VertexCount} vertices but the data block declares " +
                    $"{vertexCount}; a static layout requires them equal (packed vertex i is shape vertex i)."),
                diagnostics);
        }

        var triangulation = string.Equals(dataBlock.Type, "NiTriStripsData", StringComparison.Ordinal)
            ? NifModelGeometryData.ReadStrips(dataBlock, vertexCount)
            : NifModelGeometryData.ReadList(dataBlock, vertexCount);
        cancellationToken.ThrowIfCancellationRequested();
        if (triangulation is null || triangulation.StoredTriangles == 0)
        {
            return Empty(dataBlock, packedBlock, shape, "it stores no triangles", diagnostics);
        }

        if (triangulation.KeptTriangles == 0)
        {
            return Empty(dataBlock, packedBlock, shape, string.Create(CultureInfo.InvariantCulture,
                $"all {triangulation.StoredTriangles} stored triangle(s) repeat a vertex index"), diagnostics);
        }

        ReportTangentFrame(dataBlock, packedBlock, packed, diagnostics);
        ReportColorByteOrder(shape, packedBlock, packed, diagnostics);
        var order = Enumerable.Range(0, vertexCount).ToArray();
        return NifModelGeometryData.FromPacked(dataBlock, vertexCount, packed, order, triangulation, null, null, null,
            null);
    }

    /// <summary>The skinned form: partition order with point indices, the partitions' triangles offset.</summary>
    private static NifModelGeometryData ReadSkinned(NifModelReadState state, NifDecodedBlock dataBlock,
        NifDecodedBlock packedBlock, NifDecodedBlock shape, int skinInstance, NifPackedGeometryStreams packed,
        int vertexCount, NifModelDiagnosticSink diagnostics, CancellationToken cancellationToken)
    {
        var instanceBlock = state.Blocks[skinInstance];
        if (!instanceBlock.IsComplete)
        {
            return NotTyped(dataBlock, packedBlock, shape, NifModelCoverage.PackedPayloadReason,
                string.Create(CultureInfo.InvariantCulture,
                    $"skin instance block {instanceBlock.Index} did not decode exactly, so no partition gives the " +
                    $"packed vertex order."), diagnostics);
        }

        var instance = NifSkinInstanceView.Read(instanceBlock,
            state.Schema.Inherits(instanceBlock.Type, NifModelSkinReader.DismemberType));
        if (instance.PartitionLink == -1 || (uint)instance.PartitionLink >= (uint)state.Blocks.Count ||
            !state.Schema.Inherits(state.Blocks[instance.PartitionLink].Type, "NiSkinPartition"))
        {
            return NotTyped(dataBlock, packedBlock, shape, NifModelCoverage.PackedPayloadReason,
                string.Create(CultureInfo.InvariantCulture,
                    $"layout {packed.Layout.Id} is skinned but skin instance {instanceBlock.Index} names no " +
                    $"NiSkinPartition, so nothing gives the packed vertex order."), diagnostics);
        }

        var partitionBlock = state.Blocks[instance.PartitionLink];
        if (!partitionBlock.IsComplete)
        {
            return NotTyped(dataBlock, packedBlock, shape, NifModelCoverage.PackedPayloadReason,
                string.Create(CultureInfo.InvariantCulture,
                    $"NiSkinPartition block {partitionBlock.Index} did not decode exactly, so the packed vertex " +
                    $"order is not established."), diagnostics);
        }

        var partitions = NifSkinPartitionView.ReadAll(partitionBlock, instance.Bones.Count, vertexCount);
        cancellationToken.ThrowIfCancellationRequested();
        var mapped = 0;
        foreach (var partition in partitions)
        {
            if (partition.VertexMap is null)
            {
                return NotTyped(dataBlock, packedBlock, shape, NifModelCoverage.PackedPayloadReason,
                    string.Create(CultureInfo.InvariantCulture,
                        $"partition {partition.Ordinal} of block {partitionBlock.Index} has no vertex map, so the " +
                        $"packed vertex order is not established."), diagnostics);
            }

            mapped += partition.VertexCount;
        }

        if (mapped != packed.VertexCount)
        {
            return NotTyped(dataBlock, packedBlock, shape, NifModelCoverage.PackedPayloadReason,
                string.Create(CultureInfo.InvariantCulture,
                    $"the packed block stores {packed.VertexCount} vertices but the {partitions.Count} partition(s) " +
                    $"map {mapped}; a skinned layout requires them equal (the packed vertices are the concatenated " +
                    $"vertex maps)."), diagnostics);
        }

        var points = new int[mapped];
        var starts = new int[partitions.Count];
        var indices = new List<int>();
        var droppedOrdinals = new List<int>();
        var droppedPerPartition = new int[partitions.Count];
        var perPartition = new JsonArray();
        var next = 0;
        var stored = 0;
        // Shared requires one position per source point: a shape vertex that two partitions both map must decode to
        // the same position from both packed copies (float equality, as Shared compares; -0 equals +0). The map values
        // are already below vertexCount (NifSkinPartitionView.ReadAll).
        var firstPacked = new int[vertexCount];
        Array.Fill(firstPacked, -1);
        foreach (var partition in partitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            starts[partition.Ordinal] = next;
            var map = partition.VertexMap!;
            for (var local = 0; local < partition.VertexCount; local++)
            {
                var s = map[local];
                var p = next + local;
                points[p] = s;
                if (firstPacked[s] == -1)
                {
                    firstPacked[s] = p;
                    continue;
                }

                var first = firstPacked[s];
                if (packed.Positions[p * 3] != packed.Positions[first * 3] ||
                    packed.Positions[p * 3 + 1] != packed.Positions[first * 3 + 1] ||
                    packed.Positions[p * 3 + 2] != packed.Positions[first * 3 + 2])
                {
                    return NotTyped(dataBlock, packedBlock, shape, NifModelCoverage.PackedPayloadReason,
                        string.Create(CultureInfo.InvariantCulture,
                            $"partition {partition.Ordinal} vertex {local} (packed vertex {p}) repeats shape vertex " +
                            $"{s}, first stored as packed vertex {first}, with a different position " +
                            $"({packed.Positions[p * 3]:R}, {packed.Positions[p * 3 + 1]:R}, " +
                            $"{packed.Positions[p * 3 + 2]:R}) vs ({packed.Positions[first * 3]:R}, " +
                            $"{packed.Positions[first * 3 + 1]:R}, {packed.Positions[first * 3 + 2]:R}); Shared " +
                            $"requires one position per source point and the retail measurement (positions match " +
                            $"PC at 100% through the partition map) gives no rule for the disagreement."),
                        diagnostics);
                }
            }

            var kept = 0;
            if (partition.Triangles is { } faces)
            {
                foreach (var index in faces.Indices)
                {
                    indices.Add(next + index);
                }

                foreach (var ordinal in faces.DroppedOrdinals)
                {
                    droppedOrdinals.Add(stored + ordinal);
                }

                droppedPerPartition[partition.Ordinal] = faces.DroppedTriangles;
                stored += faces.StoredTriangles;
                kept = faces.KeptTriangles;
            }

            perPartition.Add(new JsonObject
            {
                ["ordinal"] = partition.Ordinal,
                ["firstVertex"] = next,
                ["numVertices"] = partition.VertexCount,
                ["form"] = partition.StripCount > 0 ? "strips" : "list",
                ["stored"] = partition.Triangles?.StoredTriangles ?? 0,
                ["kept"] = kept,
                ["droppedRepeatedIndex"] = partition.Triangles?.DroppedTriangles ?? 0
            });
            next += partition.VertexCount;
        }

        if (stored == 0)
        {
            return Empty(dataBlock, packedBlock, shape, "its partitions store no triangles", diagnostics);
        }

        var triangulation = new NifTriangulation([.. indices], stored, droppedOrdinals.AsReadOnly(),
            droppedPerPartition);
        if (triangulation.KeptTriangles == 0)
        {
            return Empty(dataBlock, packedBlock, shape, string.Create(CultureInfo.InvariantCulture,
                $"all {stored} partition triangle(s) repeat a vertex index"), diagnostics);
        }

        ReportTangentFrame(dataBlock, packedBlock, packed, diagnostics);
        ReportColorByteOrder(shape, packedBlock, packed, diagnostics);
        var triangleFacts = new JsonObject
        {
            ["form"] = "partitions",
            ["source"] = string.Create(CultureInfo.InvariantCulture,
                $"NiSkinPartition block {partitionBlock.Index}: each partition's triangles or strips offset by its " +
                $"first primitive vertex"),
            ["stored"] = stored,
            ["kept"] = triangulation.KeptTriangles,
            ["droppedRepeatedIndex"] = triangulation.DroppedTriangles,
            ["droppedOrdinals"] = NifModelNativeValues.Integers(triangulation.DroppedOrdinals),
            ["droppedPerPartition"] = NifModelNativeValues.Integers(droppedPerPartition),
            ["partitions"] = perPartition,
            ["rule"] = "strips as for NiTriStripsData (parity rule, repeated index dropped); lists drop repeated " +
                       "indices; ordinals count partition triangles in partition order"
        };
        var order = Enumerable.Range(0, mapped).ToArray();
        return NifModelGeometryData.FromPacked(dataBlock, vertexCount, packed, order, triangulation,
            new ScenePointIndices(vertexCount, points), partitions, starts, triangleFacts);
    }

    /// <summary>The non-finite tangent-frame and texture-coordinate diagnostics of the inline rules, for packed channels.</summary>
    private static void ReportTangentFrame(NifDecodedBlock dataBlock, NifDecodedBlock packedBlock,
        NifPackedGeometryStreams packed, NifModelDiagnosticSink diagnostics)
    {
        if (packed.TangentFrameNonFinite > 0)
        {
            diagnostics.Add(NifModelGeometryData.NonFiniteTangentDiagnostic, string.Create(
                CultureInfo.InvariantCulture,
                $"Block {dataBlock.Index} ({dataBlock.Type}): {packed.TangentFrameNonFinite} packed tangent or " +
                $"bitangent component(s) of block {packedBlock.Index} are NaN or infinite; the primitive carries no " +
                $"typed tangents and the channels stay in native state."));
        }

        if (packed.TexCoordNonFinite > 0)
        {
            diagnostics.Add(NifModelGeometryData.NonFiniteTexCoordDiagnostic, string.Create(
                CultureInfo.InvariantCulture,
                $"Block {dataBlock.Index} ({dataBlock.Type}): {packed.TexCoordNonFinite} packed texture-coordinate " +
                $"component(s) of block {packedBlock.Index} are NaN or infinite. Those components read as 0 in the " +
                $"portable coordinates; the channel's exactly widened bits are kept in the " +
                $"'{NifModelGeometryData.RawTexCoordAttribute}0.raw' attribute and native state."));
        }
    }

    /// <summary>
    ///     The color byte-order diagnostics of a packed block whose color channel reaches typed state: once per packed
    ///     block, <see cref="PlatformAssumedDiagnostic" /> when the platform was assumed, and
    ///     <see cref="ColorOrderInferredDiagnostic" /> when the layout's order is carried over rather than measured
    ///     (L6), with the count of vertices on which the two orders would differ. Called only on the path that reaches
    ///     <see cref="NifModelGeometryData.FromPacked" />, so a block kept as native state raises neither.
    /// </summary>
    private static void ReportColorByteOrder(NifDecodedBlock shape, NifDecodedBlock packedBlock,
        NifPackedGeometryStreams packed, NifModelDiagnosticSink diagnostics)
    {
        if (!packed.Layout.HasVertexColors)
        {
            return;
        }

        if (packed.Platform.IsAssumed)
        {
            diagnostics.Add(PlatformAssumedDiagnostic, string.Create(CultureInfo.InvariantCulture,
                $"Block {packedBlock.Index} ({packedBlock.Type}, layout {packed.Layout.Id}) for block {shape.Index} " +
                $"({shape.Type}): {PlatformAssumedMessage}."));
        }

        if (packed.ColorByteOrderEvidence is { } evidence)
        {
            diagnostics.Add(ColorOrderInferredDiagnostic, string.Create(CultureInfo.InvariantCulture,
                $"Block {packedBlock.Index} ({packedBlock.Type}, layout {packed.Layout.Id}) for block {shape.Index} " +
                $"({shape.Type}): the color byte order ({packed.Platform.ColorByteOrder}) is inferred, not measured " +
                $"on this layout, so the colors carry Assumed provenance; {packed.ColorOrderSensitiveVertices} of " +
                $"{packed.VertexCount} vertices would decode differently under the other order. {evidence}."));
        }
    }

    /// <summary>The shape's non-null Skin Instance link, or null.</summary>
    /// <exception cref="InvalidDataException">The link names a block that is not an NiSkinInstance.</exception>
    private static int? SkinInstanceLink(NifModelReadState state, NifDecodedBlock shape)
    {
        if (!shape.Root.TryGet("Skin Instance", out var value) || value is not NifRefValue link || link.IsNone)
        {
            return null;
        }

        if ((uint)link.Index >= (uint)state.Blocks.Count ||
            !state.Schema.Inherits(state.Blocks[link.Index].Type, NifModelSkinReader.SkinInstanceType))
        {
            throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture,
                $"NIF block {shape.Index} ({shape.Type}) links block {link.Index} as its Skin Instance, which is not " +
                $"an NiSkinInstance."));
        }

        return link.Index;
    }

    /// <summary>A packed block about to feed typed state must have decoded exactly (plan section 1, self-check 1).</summary>
    private static void RequireComplete(NifDecodedBlock block)
    {
        if (block.IsComplete)
        {
            return;
        }

        var detail = block.Failure?.Message ??
                     (block.Problems.Count > 0
                         ? block.Problems[0].Message
                         : $"consumed {block.ConsumedBytes} of {block.Size} bytes");
        throw new InvalidDataException(
            $"NIF block {block.Index} ({block.Type}) feeds typed geometry but did not decode exactly: {detail}");
    }

    private static NifModelGeometryData Empty(NifDecodedBlock dataBlock, NifDecodedBlock packedBlock,
        NifDecodedBlock shape, string detail, NifModelDiagnosticSink diagnostics)
    {
        return NotTyped(dataBlock, packedBlock, shape, NifModelCoverage.EmptyGeometryReason, detail, diagnostics);
    }

    private static NifModelGeometryData NotTyped(NifDecodedBlock dataBlock, NifDecodedBlock packedBlock,
        NifDecodedBlock shape, string reason, string detail, NifModelDiagnosticSink diagnostics)
    {
        diagnostics.Add(NotTypedDiagnostic, string.Create(CultureInfo.InvariantCulture,
            $"Block {shape.Index} ({shape.Type}) with data block {dataBlock.Index} and packed block " +
            $"{packedBlock.Index}: kept as native state ({reason}): {detail}"));
        return NifModelGeometryData.Untyped(dataBlock, reason, detail);
    }
}
