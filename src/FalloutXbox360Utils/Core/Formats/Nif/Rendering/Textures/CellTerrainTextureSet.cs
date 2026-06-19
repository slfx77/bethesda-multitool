using System.Numerics;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Textures;

/// <summary>
///     Per-cell fixed-slot texture binding + per-vertex blend weights derived from a
///     <see cref="CellLayerWeightTable" />. The 3D terrain renderer binds up to
///     <see cref="MaxSlots" /> diffuse textures per cell and reads <see cref="SlotVectors" />
///     Vector4s of weights per vertex; the fragment shader's job is
///     <c>color = Σ slot_i_weight × t_i.Sample(uv)</c> over the active slots.
///
///     <para>
///         Slot cap: 16. The engine-accurate per-vertex weight list is variable-length (the 2D
///         per-pixel blit blends up to 16 unique FormIDs at the four bilinear-surrounding
///         vertices), so 16 matches that ceiling — the 3D path is now non-lossy for every real
///         FNV cell and renders exactly what the shared <see cref="CellLayerWeightTable" />
///         produces. Cells that somehow exceed 16 distinct LTEXs truncate to their
///         top-16-by-total-weight contributors (vanishingly rare; the lost layers are minor).
///     </para>
/// </summary>
public sealed class CellTerrainTextureSet
{
    public const int MaxSlots = 16;

    /// <summary>Number of <see cref="Vector4" />s needed to hold <see cref="MaxSlots" /> per-vertex
    /// weights (4 weights per Vector4). The terrain mesh's slot-1 vertex stream is this many
    /// float4s wide.</summary>
    public const int SlotVectors = MaxSlots / 4;

    /// <summary>Per-cell vertex count for the default 33×33 grid (Morrowind cells are 65×65 = 4225).</summary>
    public const int VertexCount = CellLayerWeightTable.CellVertexCount * CellLayerWeightTable.CellVertexCount;

    /// <summary>
    ///     Per-cell vertex count of <b>this</b> set (the source <see cref="CellLayerWeightTable" />'s
    ///     vertex count — 33×33 for Fallout-family, 65×65 for Morrowind).
    /// </summary>
    public int CellVertexCount { get; }

    /// <summary>
    ///     LTEX FormID bound to slot index 0..<see cref="MaxSlots" />-1. <c>0</c> is the
    ///     engine-default sentinel (see <see cref="CellLayerWeightTable.EngineDefaultSentinelFormId" />);
    ///     the renderer maps it to DirtWasteland01. Slot count = <see cref="ActiveSlotCount" />;
    ///     remaining slots are 0.
    /// </summary>
    public uint[] SlotFormIds { get; } = new uint[MaxSlots];

    /// <summary>Number of populated slots in <see cref="SlotFormIds" /> (1..<see cref="MaxSlots" />).</summary>
    public int ActiveSlotCount { get; set; }

    /// <summary>
    ///     Per-vertex blend weights into the <see cref="MaxSlots" /> slots, packed as
    ///     <see cref="SlotVectors" /> Vector4s per vertex. Layout: index = (vy * gridSize + vx) *
    ///     SlotVectors + k, where k selects the 4-slot group (slots 4k..4k+3). Weights sum to ~1
    ///     at vertices with any contribution; 0 at empty vertices.
    /// </summary>
    public Vector4[] VertexWeights { get; }

    /// <summary>Default-grid (33×33) set. Retained for single-cell callers and tests; the renderer's
    /// <see cref="Project" /> sizes the set to the source table's actual grid (33×33 or 65×65).</summary>
    public CellTerrainTextureSet() : this(VertexCount)
    {
    }

    private CellTerrainTextureSet(int cellVertexCount)
    {
        CellVertexCount = cellVertexCount;
        VertexWeights = new Vector4[cellVertexCount * SlotVectors];
    }

    /// <summary>
    ///     Project a <see cref="CellLayerWeightTable" /> onto the fixed-slot representation.
    ///     Picks the top-<see cref="MaxSlots" /> LTEXs by total cell-wide weight, then for each
    ///     vertex sums its contributions into the slot each FormID was assigned to and
    ///     renormalizes so the per-vertex weight vector sums to 1. Vertices whose entire weight
    ///     set falls outside the top-N contribute nothing and get all-zero weights (the renderer
    ///     binds an engine-default fallback for those).
    /// </summary>
    public static CellTerrainTextureSet? Project(CellLayerWeightTable? table)
    {
        if (table is null) return null;

        // Phase 1: sum total weight per unique FormID across the cell, to pick the top-MaxSlots.
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

        var sorted = new List<KeyValuePair<uint, float>>(totals);
        sorted.Sort(static (a, b) => b.Value.CompareTo(a.Value));

        var set = new CellTerrainTextureSet(table.Vertices.Length);
        var slotCount = Math.Min(MaxSlots, sorted.Count);
        set.ActiveSlotCount = slotCount;
        var formIdToSlot = new Dictionary<uint, int>(slotCount);
        for (var s = 0; s < slotCount; s++)
        {
            set.SlotFormIds[s] = sorted[s].Key;
            formIdToSlot[sorted[s].Key] = s;
        }

        // Phase 2: per-vertex projection into a scratch float[MaxSlots], renormalize, then pack
        // into the SlotVectors Vector4s.
        Span<float> w = stackalloc float[MaxSlots];
        for (var i = 0; i < table.Vertices.Length; i++)
        {
            w.Clear();
            ref readonly var v = ref table.Vertices[i];
            if (v.Count > 0) AddIfSlotted(formIdToSlot, v.E0, w);
            if (v.Count > 1) AddIfSlotted(formIdToSlot, v.E1, w);
            if (v.Count > 2) AddIfSlotted(formIdToSlot, v.E2, w);
            if (v.Count > 3) AddIfSlotted(formIdToSlot, v.E3, w);
            if (v.Overflow is not null)
            {
                var n = v.Count - 4;
                for (var k = 0; k < n; k++) AddIfSlotted(formIdToSlot, v.Overflow[k], w);
            }

            var total = 0f;
            for (var s = 0; s < MaxSlots; s++) total += w[s];
            if (total > 0f && MathF.Abs(total - 1f) > 1e-4f)
            {
                var inv = 1f / total;
                for (var s = 0; s < MaxSlots; s++) w[s] *= inv;
            }

            for (var k = 0; k < SlotVectors; k++)
            {
                set.VertexWeights[i * SlotVectors + k] = new Vector4(
                    w[k * 4 + 0], w[k * 4 + 1], w[k * 4 + 2], w[k * 4 + 3]);
            }
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
        Span<float> weights)
    {
        if (formIdToSlot.TryGetValue(entry.FormId, out var slot))
        {
            weights[slot] += entry.Weight;
        }
    }
}
