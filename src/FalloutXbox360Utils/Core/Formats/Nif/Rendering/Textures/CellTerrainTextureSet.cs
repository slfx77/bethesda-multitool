using System.Numerics;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Textures;

/// <summary>
///     Per-cell fixed-slot texture binding + per-vertex blend weights derived from a
///     <see cref="CellLayerWeightTable" />. The 3D terrain renderer binds up to
///     <see cref="MaxSlots" /> diffuse textures per cell and reads a Vector4 of weights per
///     vertex; the fragment shader's job collapses to <c>color = Σ slot_i_weight × t_i.Sample(uv)</c>.
///
///     Why a fixed 4-slot cap: the engine-accurate per-vertex weight list is variable-length
///     (up to ~16 entries at the cell-center vertex in pathological cases), but a stable
///     shader signature needs a fixed number of texture slots. 4 matches the upper bound for
///     "typical" FNV cells (a handful share four BTXTs across quadrants, ATXTs are usually
///     spatial-not-additive). Cells that exceed 4 truncate to their top-4-by-total-weight
///     contributors; the lost layers are minor visual contributions, comparable to the prior
///     3D shader's hardcoded "Up to 4 layers per quadrant — extras silently dropped" policy.
/// </summary>
public sealed class CellTerrainTextureSet
{
    public const int MaxSlots = 4;
    public const int VertexCount = CellLayerWeightTable.CellVertexCount * CellLayerWeightTable.CellVertexCount;

    /// <summary>
    ///     LTEX FormID bound to slot index 0..3. <c>0</c> is the engine-default sentinel
    ///     (see <see cref="CellLayerWeightTable.EngineDefaultSentinelFormId" />); the
    ///     renderer maps it to DirtWasteland01. Slot count = <see cref="ActiveSlotCount" />;
    ///     remaining slots are 0.
    /// </summary>
    public readonly uint[] SlotFormIds = new uint[MaxSlots];

    /// <summary>Number of populated slots in <see cref="SlotFormIds" /> (1..<see cref="MaxSlots" />).</summary>
    public int ActiveSlotCount;

    /// <summary>
    ///     Per-vertex blend weights into the 4 slots. Row-major, index = vy * 33 + vx, matching
    ///     <see cref="CellLayerWeightTable.At" />. Weights sum to ~1 at vertices with any
    ///     contribution; sum to 0 at empty vertices (caller should bind an engine-default
    ///     fallback for those).
    /// </summary>
    public readonly Vector4[] VertexWeights = new Vector4[VertexCount];

    /// <summary>
    ///     Project a <see cref="CellLayerWeightTable" /> onto the fixed-slot representation.
    ///     Picks the top-<see cref="MaxSlots" /> LTEXs by total cell-wide weight, then for each
    ///     vertex sums its contributions into the slot each FormID was assigned to. Vertices
    ///     whose entire weight set falls outside the top-4 contribute nothing and get all-zero
    ///     weights (renderer must handle the "no contribution" case — typically falls back to
    ///     the engine-default sentinel via slot 0).
    /// </summary>
    public static CellTerrainTextureSet? Project(CellLayerWeightTable? table)
    {
        if (table is null) return null;

        // Phase 1: sum total weight per unique FormID across the cell, so we can pick the
        // top-MaxSlots most significant contributors.
        var totals = new Dictionary<uint, float>();
        for (var i = 0; i < table.Vertices.Length; i++)
        {
            ref readonly var v = ref table.Vertices[i];
            if (v.Count > 0) AddOrIncrement(totals, v.E0.FormId, v.E0.Weight);
            if (v.Count > 1) AddOrIncrement(totals, v.E1.FormId, v.E1.Weight);
            if (v.Count > 2) AddOrIncrement(totals, v.E2.FormId, v.E2.Weight);
            if (v.Count > 3) AddOrIncrement(totals, v.E3.FormId, v.E3.Weight);
            if (v.Overflow is not null)
            {
                var n = v.Count - 4;
                for (var k = 0; k < n; k++) AddOrIncrement(totals, v.Overflow[k].FormId, v.Overflow[k].Weight);
            }
        }
        if (totals.Count == 0) return null;

        // Stable top-N: sort by weight descending; ties keep dictionary order (deterministic
        // enough for a given input — the unstable case is two LTEXs with identical totals, in
        // which case which one wins is cosmetic).
        var sorted = new List<KeyValuePair<uint, float>>(totals);
        sorted.Sort(static (a, b) => b.Value.CompareTo(a.Value));

        var set = new CellTerrainTextureSet();
        var slotCount = Math.Min(MaxSlots, sorted.Count);
        set.ActiveSlotCount = slotCount;
        var formIdToSlot = new Dictionary<uint, int>(slotCount);
        for (var s = 0; s < slotCount; s++)
        {
            set.SlotFormIds[s] = sorted[s].Key;
            formIdToSlot[sorted[s].Key] = s;
        }

        // Phase 2: per-vertex projection. For each cell vertex, take each (FormID, weight)
        // entry and add it to the corresponding slot's weight if that FormID survived the
        // top-N cut. Then renormalize so the per-vertex weight vector sums to 1 — necessary
        // because the truncation removes some weight that the variable-length table had
        // already renormalized to 1 against.
        for (var i = 0; i < table.Vertices.Length; i++)
        {
            ref readonly var v = ref table.Vertices[i];
            var w = Vector4.Zero;
            if (v.Count > 0) AddIfSlotted(formIdToSlot, v.E0, ref w);
            if (v.Count > 1) AddIfSlotted(formIdToSlot, v.E1, ref w);
            if (v.Count > 2) AddIfSlotted(formIdToSlot, v.E2, ref w);
            if (v.Count > 3) AddIfSlotted(formIdToSlot, v.E3, ref w);
            if (v.Overflow is not null)
            {
                var n = v.Count - 4;
                for (var k = 0; k < n; k++) AddIfSlotted(formIdToSlot, v.Overflow[k], ref w);
            }

            var total = w.X + w.Y + w.Z + w.W;
            if (total > 0f && MathF.Abs(total - 1f) > 1e-4f) w *= 1f / total;
            set.VertexWeights[i] = w;
        }

        return set;
    }

    private static void AddOrIncrement(Dictionary<uint, float> totals, uint formId, float weight)
    {
        if (totals.TryGetValue(formId, out var existing))
        {
            totals[formId] = existing + weight;
        }
        else
        {
            totals[formId] = weight;
        }
    }

    private static void AddIfSlotted(
        Dictionary<uint, int> formIdToSlot,
        LayerWeight entry,
        ref Vector4 weights)
    {
        if (!formIdToSlot.TryGetValue(entry.FormId, out var slot)) return;
        switch (slot)
        {
            case 0: weights.X += entry.Weight; break;
            case 1: weights.Y += entry.Weight; break;
            case 2: weights.Z += entry.Weight; break;
            case 3: weights.W += entry.Weight; break;
        }
    }
}
