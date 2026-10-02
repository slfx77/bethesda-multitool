using System.Globalization;
using System.Text.Json.Nodes;
using BethesdaMultitool.Core.Formats.Nif.Decoding;
using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     Builds a skinned primitive's <see cref="SceneSkinInfluences" /> exactly as authored (plan section 3, "Skin";
///     Shared: no pruning, no renormalization, no duplicate merging). Pure: the views are already read and checked.
/// </summary>
/// <remarks>
///     <para>
///         From NiSkinData (Has Vertex Weights not 0): the stride is the largest number of weight entries any vertex has
///         across all bones; each vertex lists its entries in bone order (then stored order within a bone), with the
///         weight exactly as stored, and is padded to the stride with (joint 0, weight 0.0). Nothing is sorted by weight,
///         truncated to four, dropped for being small, or normalized, so a vertex whose weights do not sum to one keeps
///         that sum and a vertex with no entry is all padding.
///     </para>
///     <para>
///         From NiSkinPartition (Has Vertex Weights 0 on PC): every partition must carry a vertex map, weights and bone
///         indices. The stride is the largest Num Weights Per Vertex; a shape vertex takes its row from the first
///         partition that maps it (joint = the partition's Bones[bone index], weight as stored, zero-weight slots
///         included), padded to the stride. A vertex that two partitions map to different rows is a conflict the reader
///         does not resolve, so the influences are not typed; a vertex no partition maps is all padding and is counted.
///     </para>
///     <para>
///         From the packed streams (slice 10, console layouts L3 and L4): the primitive's vertices are in partition order,
///         stride 4. On the Xbox 360 (the read's resolved packed platform) vertex v of partition p takes the engine's
///         lanes (<see cref="NifPackedEngineLanes" />): slots 0-2 as stored on the partition's Bones[decoded bone index],
///         and the fourth weight derived as 1f - ((w0 + w1) + w2) on the stored slot-3 bone, merged into a positive slot
///         on the same joint, a signed lane when negative on an otherwise unused joint; the stored fourth half is never
///         read (the engine never loads it), so the sentinel and the residual halves are census facts only, and the
///         lanes sum to one within Float32 rounding. The facts' <c>storedSlot3NotTheEngineWeight</c> counts the vertices
///         whose decoded fourth half, taken before the sentinel rule (a stored 1.0 counts as 1.0), is not the derived
///         weight. The resolved platform is the shell's <c>--platform</c> option when given and the assumed X360
///         otherwise, so a PS3 file keeps its stored lanes only when the option names it; under the assumption the
///         engine lanes carry provenance Assumed. On the PS3, whose skinning shaders have not been examined, the
///         four decoded weights are typed stored slot for slot (the slot-3 sentinel read as 0 by the decoder; a vertex
///         may use slot 3 with slots 1 and 2 empty); they are binary16, so they sum to one only within the half's
///         precision, and the facts record the largest deviation (<c>weightSumMaxDeviation</c>) with
///         <see cref="PackedWeightSumNote" />. A decoded bone index at or beyond the partition's Num Bones is corrupt
///         input, in every slot.
///     </para>
///     <para>
///         A non-finite weight cannot be carried (Shared rejects it), so the influences are not typed and the caller
///         keeps the skin as native state with <see cref="NifModelCoverage.SkinNonFiniteReason" />. A weight's vertex
///         index at or beyond the geometry's Num Vertices is corrupt input.
///     </para>
/// </remarks>
internal static class NifModelSkinInfluences
{
    /// <summary>The slots per vertex of the packed weight and bone-index channels.</summary>
    public const int PackedStride = 4;

    /// <summary>
    ///     The rule recorded for influences built from the packed streams' stored lanes: the PS3 reading, kept until the
    ///     PS3 skinning shaders are examined (the X360 reading is <see cref="NifPackedEngineLanes.Rule" />).
    /// </summary>
    public const string PackedRule =
        "stride 4 in packed (partition) order; stored lanes (PS3: skinning shaders not examined, the X360 engine rule " +
        "not applied); slot k = (partition Bones[byte 3 - k of the bone-index word], " +
        "half k of the weight channel); a slot-3 weight of exactly 1.0 beside three weights summing to 1 within 2e-3 " +
        "is a sentinel read as 0; slot-3 halves of 2^-24 / 2^-23 are the exporter's residual on <= 3-slot vertices " +
        "(X360 1,745 L3 + 125 L4) and are counted separately, not zeroed; slots stored slot for slot, except that a " +
        "slot whose weight is exactly 0 after the sentinel rule is padded with joint 0 (the partition's own bone " +
        "index there is padding the deformation never reads, and it differs between two partitions sharing a point, " +
        "so a welding consumer would refuse the pair; measured 2026-09-26: 92 of 563 welded corpus pairs differed " +
        "only there, 0 in a weighted slot); no sorting, pruning or normalization";

    /// <summary>
    ///     The note recorded with a stored-lane (PS3) packed skin's weight sums: the weights are binary16, so a vertex's
    ///     four slots sum to one only within the half's precision (measured: within 2e-3 on every retail vertex after the
    ///     sentinel rule), which is wider than the 1e-4 Shared's legacy interpretation demands; the deviation is the
    ///     format's, not a decode error, and nothing renormalizes it. The X360 engine lanes sum to one within Float32
    ///     rounding instead (<see cref="NifPackedEngineLanes.WeightSumNote" />).
    /// </summary>
    public const string PackedWeightSumNote =
        "weights are binary16: the four slots sum to 1 within the half's precision (measured within 2e-3 on every " +
        "retail vertex), wider than the 1e-4 of Shared's legacy interpretation; not renormalized";
    /// <summary>
    ///     The most influences per vertex the reader types. One partition addresses at most 256 bones (byte bone
    ///     indices), and Num Vertices is a u16, so the influence table stays below 16.8 million slots; a file claiming
    ///     more (for example thousands of weight entries naming one vertex) is kept as native state instead of
    ///     allocating gigabytes.
    /// </summary>
    public const int MaximumStride = 256;

    /// <summary>The rule recorded for influences built from NiSkinData.</summary>
    public const string SkinDataRule =
        "stride = the most weight entries of any vertex; per vertex, entries in bone order then stored order, " +
        "weights exactly as stored; padded with (joint 0, weight 0.0); no sorting, pruning or normalization";

    /// <summary>The rule recorded for influences built from NiSkinPartition.</summary>
    public const string PartitionRule =
        "stride = the largest Num Weights Per Vertex; each shape vertex takes the row of the first partition that " +
        "maps it (joint = partition Bones[bone index], weight as stored, zero-weight slots kept); padded with " +
        "(joint 0, weight 0.0); no sorting, pruning or normalization";

    /// <summary>Builds the influences from NiSkinData's per-bone vertex weights.</summary>
    /// <param name="block">The NiSkinData block (for messages).</param>
    /// <param name="data">Its view; <see cref="NifSkinDataView.StoresWeights" /> must be true.</param>
    /// <param name="vertexCount">The geometry's Num Vertices.</param>
    /// <returns>The influences and facts, or a NativeOnly reason and detail when they cannot be typed.</returns>
    /// <exception cref="InvalidDataException">A weight's vertex index is not below <paramref name="vertexCount" />.</exception>
    public static (SceneSkinInfluences? Influences, string? Reason, string? Detail, JsonObject Facts) FromSkinData(
        NifDecodedBlock block, NifSkinDataView data, int vertexCount)
    {
        ArgumentNullException.ThrowIfNull(block);
        ArgumentNullException.ThrowIfNull(data);
        var facts = new JsonObject { ["source"] = "NiSkinData", ["rule"] = SkinDataRule };
        var counts = new int[vertexCount];
        var entries = 0;
        for (var b = 0; b < data.Bones.Count; b++)
        {
            var weights = data.Bones[b].Weights;
            for (var w = 0; w < weights.Count; w++)
            {
                var (vertex, weight) = weights[w];
                if ((uint)vertex >= (uint)vertexCount)
                {
                    throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture,
                        $"NIF block {block.Index} ({block.Type}) Bone List[{b}] weight {w} names vertex {vertex}, " +
                        $"which is not below the geometry's Num Vertices ({vertexCount})."));
                }

                if (!float.IsFinite(weight))
                {
                    return (null, NifModelCoverage.SkinNonFiniteReason, string.Create(CultureInfo.InvariantCulture,
                        $"Bone List[{b}] weight {w} (vertex {vertex}) is not finite " +
                        $"(0x{BitConverter.SingleToUInt32Bits(weight):X8})."), facts);
                }

                counts[vertex]++;
                entries++;
            }
        }

        var stride = counts.Length == 0 ? 0 : counts.Max();
        if (stride == 0)
        {
            return (null, NifModelCoverage.SkinNoInfluencesReason,
                "NiSkinData declares vertex weights but no bone stores any.", facts);
        }

        if (stride > MaximumStride)
        {
            return (null, NifModelCoverage.SkinStrideReason, string.Create(CultureInfo.InvariantCulture,
                $"one vertex has {stride} weight entries; the reader types at most {MaximumStride}."), facts);
        }

        var slots = checked(vertexCount * stride);
        var joints = new int[slots];
        var values = new float[slots];
        var fill = new int[vertexCount];
        for (var b = 0; b < data.Bones.Count; b++)
        {
            foreach (var (vertex, weight) in data.Bones[b].Weights)
            {
                var position = vertex * stride + fill[vertex]++;
                joints[position] = b;
                values[position] = weight;
            }
        }

        AddCensus(facts, joints, values, counts, stride, entries);
        return (new SceneSkinInfluences(stride, joints, values), null, null, facts);
    }

    /// <summary>Builds the influences from the skin partitions (used only when NiSkinData stores no weights).</summary>
    /// <param name="partitions">The checked partitions.</param>
    /// <param name="vertexCount">The geometry's Num Vertices.</param>
    /// <returns>The influences and facts, or a NativeOnly reason and detail when they cannot be typed.</returns>
    public static (SceneSkinInfluences? Influences, string? Reason, string? Detail, JsonObject Facts) FromPartitions(
        IReadOnlyList<NifSkinPartitionView> partitions, int vertexCount)
    {
        ArgumentNullException.ThrowIfNull(partitions);
        var facts = new JsonObject { ["source"] = "NiSkinPartition", ["rule"] = PartitionRule };
        if (partitions.Count == 0)
        {
            return (null, NifModelCoverage.SkinNoInfluencesReason,
                "NiSkinData stores no vertex weights and the NiSkinPartition has no partition.", facts);
        }

        var stride = 0;
        foreach (var partition in partitions)
        {
            if (partition.VertexMap is null || partition.Weights is null || partition.BoneIndices is null)
            {
                return (null, NifModelCoverage.SkinNoInfluencesReason, string.Create(CultureInfo.InvariantCulture,
                    $"NiSkinData stores no vertex weights and partition {partition.Ordinal} lacks its vertex map, " +
                    $"weights or bone indices."), facts);
            }

            foreach (var row in partition.Weights)
            {
                foreach (var weight in row)
                {
                    if (!float.IsFinite(weight))
                    {
                        return (null, NifModelCoverage.SkinNonFiniteReason, string.Create(
                            CultureInfo.InvariantCulture,
                            $"partition {partition.Ordinal} stores a non-finite weight " +
                            $"(0x{BitConverter.SingleToUInt32Bits(weight):X8})."), facts);
                    }
                }
            }

            stride = Math.Max(stride, partition.WeightsPerVertex);
        }

        if (stride == 0)
        {
            return (null, NifModelCoverage.SkinNoInfluencesReason,
                "NiSkinData stores no vertex weights and every partition stores zero weights per vertex.", facts);
        }

        if (stride > MaximumStride)
        {
            return (null, NifModelCoverage.SkinStrideReason, string.Create(CultureInfo.InvariantCulture,
                $"a partition stores {stride} weights per vertex; the reader types at most {MaximumStride}."), facts);
        }

        var slots = checked(vertexCount * stride);
        var joints = new int[slots];
        var values = new float[slots];
        var counts = new int[vertexCount];
        var owner = new int[vertexCount];
        Array.Fill(owner, -1);
        foreach (var partition in partitions)
        {
            for (var local = 0; local < partition.VertexCount; local++)
            {
                var vertex = partition.VertexMap![local];
                var weights = partition.Weights![local];
                var bones = partition.BoneIndices![local];
                if (owner[vertex] >= 0)
                {
                    if (!SameRow(joints, values, vertex * stride, partition, weights, bones, stride))
                    {
                        return (null, NifModelCoverage.SkinNoInfluencesReason, string.Create(
                            CultureInfo.InvariantCulture,
                            $"NiSkinData stores no vertex weights and partitions {owner[vertex]} and " +
                            $"{partition.Ordinal} give vertex {vertex} different influences."), facts);
                    }

                    continue;
                }

                owner[vertex] = partition.Ordinal;
                counts[vertex] = weights.Length;
                for (var k = 0; k < weights.Length; k++)
                {
                    joints[vertex * stride + k] = partition.Bones[bones[k]];
                    values[vertex * stride + k] = weights[k];
                }
            }
        }

        var entries = counts.Sum();
        AddCensus(facts, joints, values, counts, stride, entries);
        facts["verticesWithoutPartition"] = owner.Count(o => o < 0);
        return (new SceneSkinInfluences(stride, joints, values), null, null, facts);
    }

    /// <summary>Builds the influences of packed skinned geometry from its decoded weight and bone-index channels.</summary>
    /// <param name="packed">The decoded channels; <see cref="NifPackedGeometryStreams.Weights" /> must be present.</param>
    /// <param name="partitions">The checked partitions that gave the packed order.</param>
    /// <param name="partitionStarts">The first primitive vertex of each partition.</param>
    /// <returns>The influences and facts, or a NativeOnly reason and detail when they cannot be typed.</returns>
    /// <exception cref="InvalidDataException">A decoded bone index is not below its partition's Num Bones.</exception>
    public static (SceneSkinInfluences? Influences, string? Reason, string? Detail, JsonObject Facts) FromPacked(
        NifPackedGeometryStreams packed, IReadOnlyList<NifSkinPartitionView> partitions,
        IReadOnlyList<int> partitionStarts)
    {
        ArgumentNullException.ThrowIfNull(packed);
        ArgumentNullException.ThrowIfNull(partitions);
        ArgumentNullException.ThrowIfNull(partitionStarts);
        // The X360 engine derives the fourth weight and never reads the stored one; the rule is data-driven by the read's
        // resolved packed platform, and PS3 keeps the stored lanes until its skinning shaders are examined.
        var engine = packed.Platform.Platform == NifPackedPlatform.X360;
        var facts = new JsonObject
        {
            ["source"] = "BSPackedAdditionalGeometryData",
            ["rule"] = engine ? NifPackedEngineLanes.Rule : PackedRule,
            ["lanes"] = engine ? "engine" : "stored",
            ["platform"] = packed.Platform.OptionValue,
            ["platformAssumed"] = packed.Platform.IsAssumed,
            ["packedBlock"] = packed.BlockIndex,
            ["layout"] = packed.Layout.Id,
            ["sentinelSlot3ReadAsZero"] = packed.SentinelWeights,
            ["slot3NonZero"] = packed.Slot3NonZero,
            ["slot3Residual"] = packed.Slot3Residual,
            ["weightSumNote"] = engine ? NifPackedEngineLanes.WeightSumNote : PackedWeightSumNote
        };
        if (packed.Weights is not { } weights || packed.BoneIndices is not { } boneIndices)
        {
            return (null, NifModelCoverage.SkinNoInfluencesReason, string.Create(CultureInfo.InvariantCulture,
                $"packed layout {packed.Layout.Id} carries no bone weights."), facts);
        }

        if (packed.WeightNonFinite > 0)
        {
            return (null, NifModelCoverage.SkinNonFiniteReason, string.Create(CultureInfo.InvariantCulture,
                $"{packed.WeightNonFinite} packed weight half(s) of block {packed.BlockIndex} are NaN or infinite."),
                facts);
        }

        var vertexCount = packed.VertexCount;
        var joints = new int[vertexCount * PackedStride];
        var values = new float[vertexCount * PackedStride];
        var counts = new int[vertexCount];
        var paddedSlots = 0;
        var kinds = new int[Enum.GetValues<NifPackedEngineLaneKind>().Length];
        double maximumResidual = 0, maximumNegativeResidual = 0;
        var storedSlot3Unread = 0;
        foreach (var partition in partitions)
        {
            var start = partitionStarts[partition.Ordinal];
            for (var local = 0; local < partition.VertexCount; local++)
            {
                var vertex = start + local;
                var offset = vertex * PackedStride;
                counts[vertex] = PackedStride;
                for (var k = 0; k < PackedStride; k++)
                {
                    var bone = boneIndices[offset + k];
                    if (bone >= partition.Bones.Count)
                    {
                        throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture,
                            $"NIF block {packed.BlockIndex} (BSPackedAdditionalGeometryData) packed vertex {vertex} " +
                            $"slot {k} names bone {bone}, which is not below partition {partition.Ordinal}'s " +
                            $"{partition.Bones.Count} bones."));
                    }
                }

                if (engine)
                {
                    var kind = NifPackedEngineLanes.Derive(weights.AsSpan(offset, PackedStride),
                        boneIndices.AsSpan(offset, PackedStride), partition.Bones, joints.AsSpan(offset, PackedStride),
                        values.AsSpan(offset, PackedStride), out var residual);
                    kinds[(int)kind]++;
                    maximumResidual = Math.Max(maximumResidual, Math.Abs(residual));
                    if (residual < 0f)
                    {
                        maximumNegativeResidual = Math.Max(maximumNegativeResidual, -residual);
                    }

                    // The decoded fourth half before the sentinel rule: a stored 1.0 sentinel is exactly a value the
                    // engine never reads, so it counts wherever it is not the derived weight.
                    var storedFourth = packed.StoredSlot3Weights is { } fourth ? fourth[vertex] : weights[offset + 3];
                    if (BitConverter.SingleToUInt32Bits(storedFourth) != BitConverter.SingleToUInt32Bits(residual))
                    {
                        storedSlot3Unread++;
                    }

                    for (var k = 0; k < PackedStride; k++)
                    {
                        if (values[offset + k] == 0f)
                        {
                            paddedSlots++;
                        }
                    }

                    continue;
                }

                for (var k = 0; k < PackedStride; k++)
                {
                    var weight = weights[offset + k];
                    var unused = weight == 0f;
                    if (unused)
                    {
                        paddedSlots++;
                    }

                    joints[offset + k] = unused ? 0 : partition.Bones[boneIndices[offset + k]];
                    values[offset + k] = weight;
                }
            }
        }

        if (engine)
        {
            facts["engineLanes"] = new JsonObject
            {
                ["derivedZero"] = kinds[(int)NifPackedEngineLaneKind.Zero],
                ["newLane"] = kinds[(int)NifPackedEngineLaneKind.NewLane],
                ["mergedPositive"] = kinds[(int)NifPackedEngineLaneKind.MergedPositive],
                ["mergedNegative"] = kinds[(int)NifPackedEngineLaneKind.MergedNegative],
                ["signedLane"] = kinds[(int)NifPackedEngineLaneKind.Signed],
                ["maximumAbsoluteDerivedWeight"] = maximumResidual,
                ["maximumNegativeDerivedWeight"] = maximumNegativeResidual,
                ["storedSlot3NotTheEngineWeight"] = storedSlot3Unread,
                ["provenance"] = (packed.Platform.IsAssumed ? SceneValueProvenance.Assumed : SceneValueProvenance.ReverseEngineered)
                    .ToString(),
                ["platformSource"] = packed.Platform.Source,
                ["evidence"] = NifPackedEngineLanes.Evidence
            };
        }

        facts["zeroWeightSlotsPaddedWithJointZero"] = paddedSlots;
        AddCensus(facts, joints, values, counts, PackedStride, vertexCount * PackedStride);
        return (new SceneSkinInfluences(PackedStride, joints, values), null, null, facts);
    }

    /// <summary>
    ///     True when a later partition gives the vertex the same influences as the row already written: the same
    ///     multiset of (joint, weight bits) over the non-zero weights. Zero-weight slots are ignored, because an unused
    ///     slot names each partition's own Bones[bone index] (usually its Bones[0]) and partitions can store different
    ///     Num Weights Per Vertex; the first partition's row, zero slots included, is the one kept.
    /// </summary>
    private static bool SameRow(int[] joints, float[] values, int start, NifSkinPartitionView partition,
        float[] weights, byte[] bones, int stride)
    {
        var written = new List<(int Joint, uint Weight)>(stride);
        for (var k = 0; k < stride; k++)
        {
            if (values[start + k] != 0f)
            {
                written.Add((joints[start + k], BitConverter.SingleToUInt32Bits(values[start + k])));
            }
        }

        var later = new List<(int Joint, uint Weight)>(weights.Length);
        for (var k = 0; k < weights.Length; k++)
        {
            if (weights[k] != 0f)
            {
                later.Add((partition.Bones[bones[k]], BitConverter.SingleToUInt32Bits(weights[k])));
            }
        }

        written.Sort();
        later.Sort();
        return written.SequenceEqual(later);
    }

    /// <summary>Records the stride, padding and the authored properties Shared keeps but legacy consumers reject.</summary>
    private static void AddCensus(JsonObject facts, int[] joints, float[] values, int[] counts, int stride,
        int entries)
    {
        var vertexCount = counts.Length;
        int withoutEntries = 0, repeatedJoint = 0, negative = 0, notUnitSum = 0;
        var maxDeviation = 0.0;
        var seen = new HashSet<int>();
        for (var v = 0; v < vertexCount; v++)
        {
            if (counts[v] == 0)
            {
                withoutEntries++;
            }

            seen.Clear();
            var sum = 0.0;
            var repeated = false;
            for (var k = 0; k < counts[v]; k++)
            {
                var weight = values[v * stride + k];
                sum += weight;
                if (weight < 0f)
                {
                    negative++;
                }

                repeated |= !seen.Add(joints[v * stride + k]);
            }

            if (repeated)
            {
                repeatedJoint++;
            }

            var deviation = Math.Abs(sum - 1.0);
            if (deviation > 1e-4)
            {
                notUnitSum++;
            }

            if (counts[v] > 0 && deviation > maxDeviation)
            {
                maxDeviation = deviation;
            }
        }

        facts["influencesPerVertex"] = stride;
        facts["authoredEntries"] = entries;
        facts["paddedSlots"] = (long)vertexCount * stride - entries;
        facts["verticesWithoutEntries"] = withoutEntries;
        facts["verticesRepeatingAJoint"] = repeatedJoint;
        facts["negativeWeights"] = negative;
        facts["verticesWhoseSumIsNotOneWithin1e-4"] = notUnitSum;
        facts["weightSumMaxDeviation"] = maxDeviation;
        facts["normalized"] = false;
    }
}
