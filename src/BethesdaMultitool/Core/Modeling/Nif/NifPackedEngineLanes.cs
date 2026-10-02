namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     The four skin lanes the Xbox 360 engine plays for one packed skinned vertex (layouts L3 and L4), rather than the
///     four stored halves: slots 0-2 are the stored weights on the owning partition's Bones[index byte], and the fourth
///     weight is derived as <c>1f - ((w0 + w1) + w2)</c> in Float32 on the stored slot-3 bone. The stored fourth half is
///     never read.
/// </summary>
/// <remarks>
///     <para>
///         Evidence (gate 1b one-off (c), TestOutput/gate1b-gap-designs-20260927/one-offs): all 93 skinning vertex shaders
///         in the retail X360 <c>Data/Shaders/shaderpackage.sdp</c> (Xenos microcode) fetch BLENDWEIGHT with three written
///         components (<c>xyz_</c> on 85, <c>_xyz</c> on 8), so the fourth stored half is never loaded, and fetch
///         BLENDINDICES with all four (<c>xyzw</c> 31, <c>wxyz</c> 62); 90 of the 93 form <c>1 - (w0 + w1 + w2)</c> as an
///         ADD of the negated sum with a literal (the other 3 are not classified by the heuristic reading). SLS2003 sums
///         (w0 + w1) + w2 before subtracting, which is the order used here. The PC packages confirm the rule directly:
///         all 1,671 BLENDWEIGHT vertex shaders execute <c>dp3</c> of the weights with (1, 1, 1), subtract the result from
///         1, and multiply that lane by the matrix the fourth BLENDINDICES component selects; none reads the fourth weight.
///         The X360 fetch census is pinned by the Bucket-B test <c>X360SkinningShaderFetchTests</c> over the retail
///         package, with BLENDINDICES (four components, the fourth selected), TEXCOORD (two) and the static shaders (no
///         BLENDWEIGHT) as the parser's controls.
///     </para>
///     <para>
///         The pairing of the derived weight with the stored slot-3 index byte is established by elimination, not by
///         decoding the X360 ALU. (1) Slots 0-2 pair the three fetched weights with the index lanes the reader types as
///         slots 0-2: the typed console lanes equal the PC partitions' weights and bone indices slot for slot (measured
///         2026-09-24), and the PC bytecode pairs weight k with index k, so any other X360 pairing would deform the
///         console meshes differently from their PC twins. (2) The index fetch writes all four components while the
///         weight fetch writes three, and the shader compiler masks a fetch component nothing reads (it does so for the
///         fourth weight), so the fourth index is read. (3) The derived weight is the only weight left to pair with it.
///         On PC the pairing is read directly off the bytecode: the derived lane multiplies the fourth index's matrix.
///     </para>
///     <para>
///         The rule is applied to X360 only: the PS3 packages share the layouts, but their skinning shaders have not been
///         examined, so a PS3 read keeps the stored lanes (<see cref="NifModelSkinInfluences.PackedRule" />). The platform
///         is the read's resolved packed platform (<see cref="NifPackedPlatformSelection" />, the shell's option or the
///         assumed X360), never a detection; under the assumed platform the lanes' provenance is Assumed.
///     </para>
///     <para>
///         Typing: a zero stored weight in slots 0-2 is padded with joint 0 (as for the stored reading, so welded copies
///         agree), and so is slot 3 whenever it carries no weight. When r's joint equals the joint of a positive stored
///         lane, r is added to that lane in Float32 (the engine's two contributions on one matrix sum; exact to within the
///         one Float32 rounding of the addition) and slot 3 becomes a zero lane, so no joint carries two positive lanes.
///         A merge that cancels the stored lane exactly leaves a zero lane, padded with joint 0 like every other; one
///         that leaves it negative makes it the vertex's signed lane. A negative r on a joint the vertex does not
///         otherwise use stays a negative lane: <c>SceneSkinInfluences</c> documents signed weights as valid source data,
///         and the writers lower it explicitly with a measured row. The lanes then sum to one within Float32 rounding.
///         (No retail vertex reaches either end of a merge: over the 793 X360 files with packed skinned geometry,
///         2,306,057 vertices, a merged lane is never at or below zero.)
///     </para>
///     <para>
///         On nv_ncr_flag every vertex whose stored slot-3 half is zero names partition bone 0 (joint 0) in its slot-3
///         index byte, so its derived lane lands on joint 0; the vertices whose stored slot 3 is nonzero name joints 0-9
///         (vertex 1: joint 3). Over the X360 corpus, 79,644 vertices with a zero stored slot 3 and r != 0 name a joint other
///         than 0 there, so the joint-0 padding of the stored reading is not the engine's bone in general.
///     </para>
/// </remarks>
internal static class NifPackedEngineLanes
{
    /// <summary>The rule recorded for influences built from the X360 engine lanes.</summary>
    public const string Rule =
        "stride 4 in packed (partition) order; X360 engine lanes: slots 0-2 = (partition Bones[byte 3 - k of the " +
        "bone-index word], half k of the weight channel), a zero weight padded with joint 0; slot 3 = weight " +
        "1f - ((w0 + w1) + w2) in Float32 on partition Bones[byte 0] (the stored slot-3 bone), merged into a positive " +
        "slot 0-2 on the same joint (slot 3 then a zero lane padded with joint 0), padded with joint 0 when zero, and a " +
        "signed lane when negative on an otherwise unused joint; the stored fourth half (sentinel or residual) is never " +
        "read; no sorting, pruning or renormalization";

    /// <summary>The note recorded with the engine lanes' weight sums.</summary>
    public const string WeightSumNote =
        "X360 engine lanes: the fourth weight is derived as 1 - ((w0 + w1) + w2), so every vertex's lanes sum to one " +
        "within Float32 rounding; a negative derived weight on an otherwise unused joint is a signed lane";

    /// <summary>The evidence recorded with the engine lanes.</summary>
    public const string Evidence =
        "X360 shaderpackage.sdp: 93/93 skinning vertex shaders fetch BLENDWEIGHT with three written components " +
        "(xyz_ 85, _xyz 8) and BLENDINDICES with four (xyzw 31, wxyz 62), 90/93 form 1 - (w0 + w1 + w2); PC " +
        "shaderpackage*.sdp: 1,671/1,671 BLENDWEIGHT vertex shaders derive the fourth weight as 1 - dp3(w, 1) on the " +
        "fourth index's matrix and never read the fourth weight; the X360 pairing with the slot-3 index is by " +
        "elimination (TestOutput/gate1b-gap-designs-20260927/one-offs)";

    /// <summary>Derives one vertex's four engine lanes (see the type remarks).</summary>
    /// <param name="stored">The four decoded weight halves in slot order; slot 3 is not read.</param>
    /// <param name="indices">The four decoded bone-index bytes in slot order, each already checked below the bone count.</param>
    /// <param name="bones">The owning partition's bone list (skin joint per partition bone index).</param>
    /// <param name="joints">Receives the four joints.</param>
    /// <param name="weights">Receives the four weights.</param>
    /// <param name="residual">Receives the derived weight r.</param>
    /// <returns>What the derived lane does on this vertex.</returns>
    public static NifPackedEngineLaneKind Derive(ReadOnlySpan<float> stored, ReadOnlySpan<byte> indices,
        IReadOnlyList<ushort> bones, Span<int> joints, Span<float> weights, out float residual)
    {
        ArgumentNullException.ThrowIfNull(bones);
        for (var k = 0; k < 3; k++)
        {
            weights[k] = stored[k];
            joints[k] = stored[k] == 0f ? 0 : bones[indices[k]];
        }

        var sum = (float)((float)(stored[0] + stored[1]) + stored[2]);
        residual = (float)(1f - sum);
        var joint = (int)bones[indices[3]];
        weights[3] = 0f;
        joints[3] = 0;
        if (residual == 0f)
        {
            return NifPackedEngineLaneKind.Zero;
        }

        for (var k = 0; k < 3; k++)
        {
            if (stored[k] > 0f && joints[k] == joint)
            {
                var merged = (float)(stored[k] + residual);
                weights[k] = merged;
                if (merged == 0f)
                {
                    // The two contributions cancel on this matrix: the lane carries no weight and keeps the padding.
                    weights[k] = 0f;
                    joints[k] = 0;
                    return NifPackedEngineLaneKind.Zero;
                }

                if (merged < 0f)
                {
                    return NifPackedEngineLaneKind.Signed;
                }

                return residual > 0f ? NifPackedEngineLaneKind.MergedPositive : NifPackedEngineLaneKind.MergedNegative;
            }
        }

        weights[3] = residual;
        joints[3] = joint;
        return residual > 0f ? NifPackedEngineLaneKind.NewLane : NifPackedEngineLaneKind.Signed;
    }
}
