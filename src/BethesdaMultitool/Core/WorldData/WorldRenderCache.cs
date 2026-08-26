using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.CompilerServices;
using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Lighting;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Scene;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Textures;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Vegetation;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Core.Resources;

namespace BethesdaMultitool.Core.WorldData;

/// <summary>
///     Per-loaded-world render cache. Keeps decoded LAND/runtime terrain, derived texture
///     grids, and the per-cell baked placement lists scoped to one
///     <see cref="WorldViewData" /> instance.
/// </summary>
internal sealed class WorldRenderCache : ITrackableResource
{
    private readonly ConcurrentDictionary<CellRecord, IReadOnlyList<PlacedLight>> _placedLights =
        new(ReferenceEqualityComparer.Instance);

    private readonly ConcurrentDictionary<CellRecord, RenderableReference[]> _placements =
        new(ReferenceEqualityComparer.Instance);

    private readonly ConcurrentDictionary<CellRecord, ReferencePlacementSpatialIndex> _placementSpatialIndexes =
        new(ReferenceEqualityComparer.Instance);

    private readonly ConcurrentDictionary<CellRecord, DecodedTerrainCell> _terrain =
        new(ReferenceEqualityComparer.Instance);

    // Bake terrain neighbor lookups + per-quadrant CellLayerWeightTable
    // at LoadData (or lazily on first build). Inputs are worldspace-static (cell LAND +
    // cardinal neighbors' LAND); see TerrainRenderer12.BuildCellTextureSet. Moves
    // ~0.05 ms per cell out of TryBuildAndCache, smoothing the 16-cell-per-frame mesh
    // build burst when entering a new area.
    /// <summary>
    ///     Ceiling for each per-cell derived cache. Sized so a 33-grid worldspace (68 KB a cell)
    ///     never evicts at all - about 7,700 cells, more than any such worldspace holds - while a
    ///     129-grid one settles at a few hundred resident cells instead of tens of thousands.
    /// </summary>
    internal const long MaxDerivedCacheBytes = 512L * 1024L * 1024L;

    private readonly BoundedDerivedCache<CellRecord, CellTerrainTextureSet> _terrainTextureSets =
        new(MaxDerivedCacheBytes);

    private readonly BoundedDerivedCache<CellRecord, TextureWinnerGrid> _textureWinners =
        new(MaxDerivedCacheBytes);

    // Running stats totals, maintained at the insertion sites so GetStats is O(1). Append-only for
    // the session (nothing removes from these maps), so the counters never need a subtract path.
    private long _trackedBytes;
    private long _trackedEntries;

    /// <summary>
    ///     Base-FormID → <see cref="PlacedObjectCategory" /> index (the owning
    ///     <see cref="WorldViewData.CategoryIndex" />). Set once at LoadData, before any placement
    ///     list is baked, so each <see cref="RenderableReference" /> carries its category for the
    ///     renderer's per-category visibility filter (for example, user-hidden activators). Null until
    ///     set → placements bake with <see cref="PlacedObjectCategory.Unknown" />.
    /// </summary>
    internal IReadOnlyDictionary<uint, PlacedObjectCategory>? CategoryIndex { get; set; }

    /// <summary>
    ///     Base-FormID → resolved alternate-texture set (the owning
    ///     <see cref="WorldViewData.AlternateTexturesByFormId" />). Set once at LoadData alongside
    ///     <see cref="CategoryIndex" />, before any placement list is baked, so each placement of a
    ///     re-skinned base (e.g. a billboard) carries its per-shape MODS override into its own
    ///     mesh-cache variant. Null → no alternate textures applied (the common case).
    /// </summary>
    internal IReadOnlyDictionary<uint, AlternateTextureSet>? AlternateTextureIndex { get; set; }

    /// <summary>
    ///     MSWP FormID → normalized material-swap table (the owning
    ///     <see cref="WorldViewData.MaterialSwapsByFormId" />). Set once at LoadData alongside
    ///     <see cref="AlternateTextureIndex" />. FO4/FO76 placements carrying an XMSP resolve through
    ///     this at bake time: the swaps merge into the placement's <see cref="AlternateTextureSet" />
    ///     so its mesh decodes as a distinct variant with the replacement <c>.bgsm</c> render state.
    ///     Null → no material swaps applied (every game before FO4).
    /// </summary>
    internal IReadOnlyDictionary<uint, IReadOnlyDictionary<string, string>>? MaterialSwapIndex { get; set; }

    /// <summary>
    ///     Base-FormID → default MSWP FormID (the owning
    ///     <see cref="WorldViewData.BaseMaterialSwapsByFormId" /> — FO4-family base-record
    ///     <c>MODS</c>). The bake-time fallback for placements with no REFR <c>XMSP</c>; a
    ///     placement's own XMSP always wins. Null → no base-record swaps (every game before FO4).
    /// </summary>
    internal IReadOnlyDictionary<uint, uint>? BaseMaterialSwapIndex { get; set; }

    /// <summary>
    ///     Base-FormID → <c>MODC</c> color-remap index (the owning
    ///     <see cref="WorldViewData.BaseColorRemapsByFormId" />, FO4-family). Folded into the
    ///     placement's <see cref="AlternateTextureSet" /> at bake time so the decode overrides the
    ///     material's gradient-palette row and the mesh cache keys the colorway as its own variant.
    ///     Null → no color remaps (every game before FO4).
    /// </summary>
    internal IReadOnlyDictionary<uint, float>? BaseColorRemapIndex { get; set; }

    /// <summary>
    ///     Refs whose XESP enable-parent chain resolves to disabled in the initial world state
    ///     (WorldViewData.XespDisabledRefs). OR'd into each placement's initially-disabled flag at
    ///     bake time so parent-gated effect meshes don't always draw.
    /// </summary>
    internal IReadOnlyCollection<uint>? XespDisabledRefs { get; set; }

    /// <summary>
    ///     Base-FormID → LIGH record. Set from <see cref="WorldViewData.LightsByFormId" /> before
    ///     placement baking so a REFR can produce a local-light emitter independently of whether
    ///     its base record also carries a renderable model.
    /// </summary>
    internal IReadOnlyDictionary<uint, LightRecord>? LightIndex { get; set; }

    /// <summary>REGN/LIGH color lookup for placed-reference XEMI links.</summary>
    internal IReadOnlyDictionary<uint, Vector3>? ExternalEmittanceIndex { get; set; }

    /// <summary>LTEX and GRAS lookup tables used by the lazy per-cell grass scatter bake.</summary>
    internal IReadOnlyDictionary<uint, LandscapeTextureRecord>? LandTextureIndex { get; set; }

    internal IReadOnlyDictionary<uint, GrassRecord>? GrassIndex { get; set; }

    /// <summary>Game profile and worldspace water plane used by the engine-family grass rules.</summary>
    internal BethesdaGame Game { get; set; }

    internal float? DefaultWaterHeight { get; set; }

    /// <summary>
    ///     Mirrors <see cref="WorldspaceRecord.WaterFromParentWorldspace" /> for
    ///     the active worldspace: a WNAM-inherited default only waters cells flagging Has-Water.
    /// </summary>
    internal bool DefaultWaterRequiresCellHasWater { get; set; }

    public string ResourceName => nameof(WorldRenderCache);

    public ResourceCategory Category => ResourceCategory.CpuCache;

    /// <summary>
    ///     Tracking-only conformance (session-scoped; dropped wholesale with its
    ///     <see cref="WorldViewData" />, never trimmed — shedding derived grids mid-render would
    ///     just thrash rebuilds). Entries = decoded products across the six per-cell maps;
    ///     <see cref="Core.Diagnostics.ResourceStats.EstimatedBytes" /> sums their managed footprint.
    ///     The terrain texture sets dominate (~68 KB each at a 33-grid, ~1 MB at Fallout 76's
    ///     129-grid) and grow on demand as cells are visited, so this number is the single largest
    ///     managed consumer once a big worldspace is open.
    ///     <para>
    ///         O(1): running totals maintained at the insertion sites. The previous implementation
    ///         walked all six per-cell dictionaries per call — on a 40k-cell worldspace that is
    ///         O(cells × buckets) work, and the diagnostics tab calls GetStats every second, which
    ///         is exactly what the "cheap and lock-free" contract on
    ///         <see cref="Core.Diagnostics.ITrackableResource.GetStats" /> forbids.
    ///     </para>
    /// </summary>
    public ResourceStats GetStats()
    {
        return new ResourceStats
        {
            EntryCount = Volatile.Read(ref _trackedEntries),
            EstimatedBytes = Volatile.Read(ref _trackedBytes)
        };
    }

    /// <summary>
    ///     Counts one installed product. Every insertion site gates the call on
    ///     <c>ConcurrentDictionary.TryAdd</c>, which returns true for exactly one thread per key —
    ///     under a build race two threads construct, one installs, and only the installer counts.
    ///     Counting the loser would overstate the total by a whole product with nothing ever
    ///     subtracting it (these maps are append-only for the session).
    /// </summary>
    private void TrackInstalled(long bytes)
    {
        Interlocked.Add(ref _trackedBytes, bytes);
        Interlocked.Increment(ref _trackedEntries);
    }

    /// <summary>
    ///     Counterpart to <see cref="TrackInstalled" /> for the two DERIVED caches, which are the
    ///     only ones that evict. Without it the registry would keep reporting bytes that had already
    ///     been dropped, and a budget steered from that number would be steering from fiction.
    /// </summary>
    private void TrackReleased(long bytes)
    {
        if (bytes <= 0)
        {
            return;
        }

        Interlocked.Add(ref _trackedBytes, -bytes);
        Interlocked.Decrement(ref _trackedEntries);
    }

    internal DecodedTerrainCell GetTerrain(CellRecord cell)
    {
        if (_terrain.TryGetValue(cell, out var existing))
        {
            return existing;
        }

        // Same race semantics as GetOrAdd(factory): a concurrent caller may also decode, and one
        // result installs. The split form exists so the accounting can observe WHICH one did.
        var decoded = DecodedTerrainCell.Decode(cell);
        if (_terrain.TryAdd(cell, decoded))
        {
            TrackInstalled(decoded.EstimatedBytes);
            return decoded;
        }

        return _terrain[cell];
    }

    internal TextureWinnerGrid? GetTextureWinners(CellRecord cell)
    {
        if (_textureWinners.TryGet(cell, out var existing))
        {
            return existing;
        }

        var layers = cell.LandVisualData?.TextureLayers;
        TextureWinnerGrid? winners;
        if (layers is { Count: > 0 })
        {
            winners = TextureWinnerGrid.Build(layers);
        }
        else if (cell.LandVisualData?.VtexTextureFormIds is { Length: > 0 } vtex)
        {
            // Morrowind: build the region grid from the flat 16×16 VTEX grid (no BTXT/ATXT quadrants).
            winners = TextureWinnerGrid.BuildFromVtex(vtex);
        }
        else
        {
            winners = null;
        }

        var winnerBytes = winners?.EstimatedBytes ?? 0L;
        if (_textureWinners.TryAdd(cell, winners, winnerBytes, out var evictedBytes))
        {
            TrackInstalled(winnerBytes);
            TrackReleased(evictedBytes);
            return winners;
        }

        return _textureWinners.TryGet(cell, out var raced) ? raced : winners;
    }

    /// <summary>
    ///     Returns the cached <see cref="CellTerrainTextureSet" /> for this
    ///     cell, building it on first miss via <paramref name="builder" /> (which must call
    ///     <c>TerrainRenderer12.BuildCellTextureSet</c> or equivalent — the cache itself
    ///     doesn't know how to walk neighbor LAND records). Subsequent calls return the
    ///     cached set. Both the cell's own LAND data and the cardinal neighbors'
    ///     LAND data are worldspace-static, so a single bake per cell is sufficient.
    /// </summary>
    internal CellTerrainTextureSet? GetOrBuildTerrainTextureSet(
        CellRecord cell,
        Func<CellTerrainTextureSet?> builder)
    {
        if (_terrainTextureSets.TryGet(cell, out var existing))
        {
            return existing;
        }

        var built = builder();
        var builtBytes = built is not null
            ? built.VertexWeights.Length * 16L /* sizeof(Vector4) */ +
              (long)built.SlotFormIds.Length * sizeof(uint)
            : 0L;
        if (_terrainTextureSets.TryAdd(cell, built, builtBytes, out var evictedBytes))
        {
            TrackInstalled(builtBytes);
            TrackReleased(evictedBytes);
            return built;
        }

        return _terrainTextureSets.TryGet(cell, out var raced) ? raced : built;
    }

    /// <summary>
    ///     Returns this cell's static-mesh placements with world transforms and
    ///     bounding spheres pre-computed. Filters out ACHR/ACRE (skinned actors, deferred to v4)
    ///     and refs without a resolved ModelPath. Result is cached per cell across frames;
    ///     <c>ReferenceRenderer12</c> iterates this directly in its per-frame loop.
    /// </summary>
    internal RenderableReference[] GetPlacementList(CellRecord cell)
    {
        if (_placements.TryGetValue(cell, out var existing))
        {
            return existing;
        }

        var built = BuildPlacementList(cell);
        if (_placements.TryAdd(cell, built))
        {
            TrackInstalled((long)built.Length * Unsafe.SizeOf<RenderableReference>());
            // The bake also installed the cell's placed-light list; count it with the winning bake.
            // Under a build race the loser's identical-content list may replace the instance later,
            // but the byte count is the same either way (the bake is deterministic).
            if (_placedLights.TryGetValue(cell, out var lights))
            {
                TrackInstalled((long)lights.Count * Unsafe.SizeOf<PlacedLight>());
            }

            return built;
        }

        return _placements[cell];
    }

    /// <summary>
    ///     Returns the cell's placed LIGH emitters. The normal placement bake populates this list
    ///     before it returns; forcing <see cref="GetPlacementList" /> here keeps mesh and emitter
    ///     resolution a single pass over the cell's REFRs (including meshless emitter-only lights).
    /// </summary>
    internal IReadOnlyList<PlacedLight> GetPlacedLights(CellRecord cell)
    {
        _ = GetPlacementList(cell);
        return _placedLights.TryGetValue(cell, out var lights)
            ? lights
            : Array.Empty<PlacedLight>();
    }

    /// <summary>
    ///     Returns this cell's static-mesh references whose cached aggregate bucket bounds may
    ///     intersect the current reference visibility volume. The renderer still runs exact
    ///     per-reference sphere/frustum tests afterward; this only avoids visiting whole
    ///     sub-cell buckets that are definitely outside.
    ///     <para>
    ///         <paramref name="ringRadius" /> keeps buckets inside the sun-shadow caster ring even when
    ///         the frustum rejects them — see the <c>Query</c> overload it forwards to.
    ///     </para>
    /// </summary>
    internal int QueryPlacementCandidates(
        CellRecord cell,
        float centerX,
        float centerY,
        float radius,
        Frustum? frustum,
        float frustumMargin,
        List<RenderableReference> destination,
        float ringRadius = 0f)
    {
        if (!_placementSpatialIndexes.TryGetValue(cell, out var index))
        {
            var mine = ReferencePlacementSpatialIndex.Build(GetPlacementList(cell));
            if (_placementSpatialIndexes.TryAdd(cell, mine))
            {
                TrackInstalled(mine.EstimatedBytes);
                index = mine;
            }
            else
            {
                index = _placementSpatialIndexes[cell];
            }
        }

        index.Query(centerX, centerY, radius, frustum, frustumMargin, destination, ringRadius);
        return index.Count;
    }

    private RenderableReference[] BuildPlacementList(CellRecord cell)
    {
        var placements = cell.PlacedObjects;
        var categoryIndex = CategoryIndex;
        var alternateTextureIndex = AlternateTextureIndex;
        var materialSwapIndex = MaterialSwapIndex;
        var baseMaterialSwapIndex = BaseMaterialSwapIndex;
        var baseColorRemapIndex = BaseColorRemapIndex;
        var lightIndex = LightIndex;
        // Interns the merged base-MODS + MSWP + MODC set per (base, swap) pair for this cell's bake,
        // so the hundreds of placements sharing one colorway don't each rebuild an identical set
        // (and the mesh cache sees one shared VariantKey instance per combination). The MODC remap
        // is a function of the base FormID, so the (base, swap) key stays unique per combination.
        Dictionary<(uint BaseFormId, uint SwapFormId, uint EmittanceFormId), AlternateTextureSet?>?
            mergedSwapSets = null;
        var built = new List<RenderableReference>(placements.Count);
        List<PlacedLight>? placedLights = null;
        foreach (var p in placements)
        {
            var xespDisabled = XespDisabledRefs?.Contains(p.FormId) == true;
            if (lightIndex is not null && lightIndex.TryGetValue(p.BaseFormId, out var light) &&
                PlacedLight.TryBuild(p, light, xespDisabled, Game) is { } placedLight)
            {
                (placedLights ??= []).Add(placedLight);
            }

            var category = categoryIndex is not null && categoryIndex.TryGetValue(p.BaseFormId, out var c)
                ? c
                : PlacedObjectCategory.Unknown;
            var alternateTextures = alternateTextureIndex is not null &&
                                    alternateTextureIndex.TryGetValue(p.BaseFormId, out var alt)
                ? alt
                : null;

            // FO4/FO76 material swap: the placement's own REFR XMSP wins; a base record's default
            // swap (FO4-family MODS = bare MSWP FormID) covers every placement without one. The
            // winning swap table folds into the base's alternate-texture set so the decode applies
            // the replacement .bgsm materials and the mesh cache keys this placement as its own
            // variant. The base's MODC color remap (gradient-palette row override — shipping-crate
            // colorways) rides the same set, and alone is enough to create one.
            var effectiveSwapFormId = p.MaterialSwapFormId;
            if (effectiveSwapFormId is null && baseMaterialSwapIndex is not null &&
                baseMaterialSwapIndex.TryGetValue(p.BaseFormId, out var baseSwapFormId))
            {
                effectiveSwapFormId = baseSwapFormId;
            }

            IReadOnlyDictionary<string, string>? swaps = null;
            if (materialSwapIndex is not null && effectiveSwapFormId is { } swapFormId)
            {
                materialSwapIndex.TryGetValue(swapFormId, out swaps);
            }

            float? colorRemap = baseColorRemapIndex is not null &&
                                baseColorRemapIndex.TryGetValue(p.BaseFormId, out var remap)
                ? remap
                : null;

            Vector3? externalEmittance = p.EmittanceFormId is { } emittanceFormId &&
                                         ExternalEmittanceIndex is { } emittanceIndex &&
                                         emittanceIndex.TryGetValue(emittanceFormId, out var emittanceColor)
                ? emittanceColor
                : null;

            if (swaps is not null || colorRemap is not null || externalEmittance is not null)
            {
                mergedSwapSets ??= [];
                var key = (p.BaseFormId, effectiveSwapFormId ?? 0u, p.EmittanceFormId ?? 0u);
                if (!mergedSwapSets.TryGetValue(key, out var merged))
                {
                    merged = alternateTextures is null
                        ? AlternateTextureSet.Create([], swaps, colorRemap, externalEmittance)
                        : AlternateTextureSet.Create(
                            alternateTextures.Overrides,
                            swaps ?? alternateTextures.MaterialSwaps,
                            colorRemap ?? alternateTextures.GradientMapVOverride,
                            externalEmittance ?? alternateTextures.ExternalEmittanceColor);
                    mergedSwapSets[key] = merged;
                }

                alternateTextures = merged ?? alternateTextures;
            }

            var renderable = RenderableReference.TryBuild(
                p, category, alternateTextures, xespDisabled, Game);
            if (renderable.HasValue) built.Add(renderable.Value);
        }

        _placedLights[cell] = placedLights is { Count: > 0 }
            ? placedLights
            : Array.Empty<PlacedLight>();

        // LAND/GRAS is not represented by REFR records. Scatter it into the same instanced-reference
        // funnel so every shared grass NIF still resolves once and draws as one model batch.
        if (LandTextureIndex is { Count: > 0 } landTextures && GrassIndex is { Count: > 0 } grasses)
        {
            var terrain = GetTerrain(cell);
            if (terrain.HasTerrain)
            {
                var grassPlacements = GrassPlacementBuilder.Build(
                    cell,
                    terrain.Heights,
                    landTextures,
                    grasses,
                    Game,
                    ResolveEffectiveWaterHeight(cell, DefaultWaterHeight, DefaultWaterRequiresCellHasWater));
                if (grassPlacements.Count > 0) built.AddRange(grassPlacements);
            }
        }

        // Sorted by (spatial bucket, ModelPath). The ModelPath half is the original reason: adjacent
        // REFRs of the same model (road segments, fence posts, lamp poles) collapse into a single
        // texture bind in ReferenceRenderer.BindSrvIfChanged, and identical paths still land together
        // WITHIN a bucket. The bucket half is what lets ReferencePlacementSpatialIndex describe each
        // bucket as a RANGE over this very array instead of copying every placement into a per-bucket
        // array - the struct payload of the whole worldspace was resident twice.
        //
        // Sorting HERE rather than inside the index is deliberate: the array is published to other
        // threads through a ConcurrentDictionary the moment it is cached, and two threads racing to
        // build the index would otherwise sort the same array concurrently.
        built.Sort(static (a, b) =>
        {
            var ak = ReferencePlacementSpatialIndex.BucketKeyOf(a);
            var bk = ReferencePlacementSpatialIndex.BucketKeyOf(b);
            var byX = ak.bx.CompareTo(bk.bx);
            if (byX != 0) return byX;
            var byY = ak.by.CompareTo(bk.by);
            return byY != 0 ? byY : string.CompareOrdinal(a.ModelPath, b.ModelPath);
        });
        return built.Count == 0
            ? Array.Empty<RenderableReference>()
            : built.ToArray();
    }

    internal static float? ResolveEffectiveWaterHeight(
        CellRecord cell, float? defaultWaterHeight, bool defaultRequiresCellHasWater = false)
    {
        // Sentinel XCLW (float.MaxValue) on a cell means "no explicit override — use the
        // worldspace default," NOT "this cell has no water." Vanilla WastelandNV's Colorado
        // river cells follow exactly this pattern: HasWater flag is set, XCLW is the
        // sentinel, and the engine renders water at the worldspace DNAM level (-2300 in
        // WastelandNV). The range guard below naturally rejects the sentinel (~3.4e38) and
        // falls through to the worldspace fallback.
        if (cell.WaterHeight is { } cellWater && cellWater is > -1e6f and < 1e6f)
        {
            return cellWater;
        }

        // A default inherited from a WNAM parent worldspace (TES4 child worldspaces — BravilWorld
        // takes Tamriel's sea level) applies only to cells that flag Has-Water: the child's dry city
        // cells above the waterline must not gain a plane just because the parent has an ocean.
        // Authored defaults keep the legacy every-cell fallback (FNV Colorado-river pattern above).
        if (defaultRequiresCellHasWater && !cell.HasWater)
        {
            return null;
        }

        // Worldspace-level sentinel still means "no default water plane exists" — leave it
        // as the genuine no-water signal, since the worldspace author intent is the only
        // place that distinction can come from once the cell has no explicit override.
        return WorldHeightNormalizer.IsNoWaterSentinel(defaultWaterHeight)
            ? null
            : defaultWaterHeight;
    }

}

internal sealed class ReferencePlacementSpatialIndex
{
    private const float BucketSize = WorldGridConstants.CellSize / 4f;
    private static readonly ReferencePlacementSpatialIndex Empty = new([], [], 0);

    private readonly ReferencePlacementBucket[] _buckets;

    private readonly RenderableReference[] _placements;

    private ReferencePlacementSpatialIndex(
        RenderableReference[] placements, ReferencePlacementBucket[] buckets, int count)
    {
        _placements = placements;
        _buckets = buckets;
        Count = count;
    }

    internal int Count { get; }

    /// <summary>
    ///     Approximate managed footprint: two bounds vectors and a range per bucket, and nothing
    ///     more. The placements are deliberately NOT counted, because this index does not own them
    ///     - it describes ranges over the cell's own placement array, which
    ///     <c>GetPlacementList</c> has already charged for. It used to copy every placement into a
    ///     per-bucket array, so the struct payload of an entire worldspace was resident twice.
    /// </summary>
    internal long EstimatedBytes => (long)_buckets.Length * (2 * 12 + 2 * sizeof(int));

    /// <summary>The bucket a placement belongs to. Shared with the sort that groups them.</summary>
    internal static (int bx, int by) BucketKeyOf(RenderableReference placement) =>
        BucketKey(placement.BoundsCenter.X, placement.BoundsCenter.Y);

    /// <summary>
    ///     Describes <paramref name="placements" /> as bucket ranges.
    ///     <para>
    ///         <b>Requires the array to be grouped by bucket already.</b> <c>BuildPlacementList</c>
    ///         sorts it that way. This detects runs rather than grouping, so an ungrouped array does
    ///         not fail - it silently yields many small buckets with repeated keys, which is slower
    ///         but still correct. That is why the ordering lives with the sort comparer, next to a
    ///         comment saying so, rather than being assumed here.
    ///     </para>
    /// </summary>
    internal static ReferencePlacementSpatialIndex Build(RenderableReference[] placements)
    {
        if (placements.Length == 0)
        {
            return Empty;
        }

        var buckets = new List<ReferencePlacementBucket>();
        var start = 0;
        var currentKey = BucketKeyOf(placements[0]);
        var min = new Vector3(float.PositiveInfinity);
        var max = new Vector3(float.NegativeInfinity);

        for (var i = 0; i < placements.Length; i++)
        {
            var key = BucketKeyOf(placements[i]);
            if (key != currentKey)
            {
                buckets.Add(new ReferencePlacementBucket(start, i - start, min, max));
                start = i;
                currentKey = key;
                min = new Vector3(float.PositiveInfinity);
                max = new Vector3(float.NegativeInfinity);
            }

            var radius = placements[i].BoundsRadius;
            min = Vector3.Min(min, placements[i].BoundsCenter - new Vector3(radius));
            max = Vector3.Max(max, placements[i].BoundsCenter + new Vector3(radius));
        }

        buckets.Add(new ReferencePlacementBucket(start, placements.Length - start, min, max));
        return new ReferencePlacementSpatialIndex(placements, [.. buckets], placements.Length);
    }

    /// <summary>
    ///     Collects this index's placements whose bucket bounds may intersect the visibility volume.
    ///     <para>
    ///         <paramref name="ringRadius" /> admits buckets the frustum rejects but which lie inside a
    ///         camera-centred Chebyshev square of that half-extent — the sun-shadow caster ring, whose
    ///         off-screen occupants still cast into view. Callers previously had to pass a NULL frustum
    ///         whenever the ring was active, because a frustum-rejected bucket could hold a ring caster;
    ///         that disabled the broadphase entirely and pushed the full ~540k candidate set through the
    ///         per-reference loop. The ring cannot be expressed as a frustum margin (it is a 360° region),
    ///         hence a disjunction rather than a wider <paramref name="frustumMargin" />.
    ///     </para>
    /// </summary>
    internal void Query(
        float centerX,
        float centerY,
        float radius,
        Frustum? frustum,
        float frustumMargin,
        List<RenderableReference> destination,
        float ringRadius = 0f)
    {
        var radiusSq = radius * radius;
        var margin = frustumMargin > 0f ? new Vector3(frustumMargin) : Vector3.Zero;
        foreach (var bucket in _buckets)
        {
            if (!IntersectsCylinder(bucket.Min, bucket.Max, centerX, centerY, radiusSq))
            {
                continue;
            }

            if (frustum.HasValue)
            {
                var min = bucket.Min - margin;
                var max = bucket.Max + margin;
                if (!frustum.Value.IntersectsAabb(min, max) &&
                    !OverlapsChebyshevSquare(min, max, centerX, centerY, ringRadius))
                {
                    continue;
                }
            }

            var end = bucket.Start + bucket.Count;
            for (var i = bucket.Start; i < end; i++)
            {
                destination.Add(_placements[i]);
            }
        }
    }

    /// <summary>
    ///     XY overlap between an AABB and a centred square of half-extent <paramref name="half" />.
    ///     A non-positive half-extent means "no ring", which never admits.
    /// </summary>
    private static bool OverlapsChebyshevSquare(
        Vector3 min, Vector3 max, float centerX, float centerY, float half)
    {
        return half > 0f &&
               min.X <= centerX + half && max.X >= centerX - half &&
               min.Y <= centerY + half && max.Y >= centerY - half;
    }

    private static bool IntersectsCylinder(Vector3 min, Vector3 max, float centerX, float centerY, float radiusSq)
    {
        var closestX = Math.Clamp(centerX, min.X, max.X);
        var closestY = Math.Clamp(centerY, min.Y, max.Y);
        var dx = centerX - closestX;
        var dy = centerY - closestY;
        return dx * dx + dy * dy <= radiusSq;
    }

    private static (int bx, int by) BucketKey(float x, float y)
    {
        return ((int)MathF.Floor(x / BucketSize), (int)MathF.Floor(y / BucketSize));
    }

    /// <summary>A half-open range over the cell's placement array, plus that range's bounds.</summary>
    private readonly record struct ReferencePlacementBucket(
        int Start,
        int Count,
        Vector3 Min,
        Vector3 Max);
}

internal sealed class DecodedTerrainCell
{
    internal const int GridSize = 33;
    internal const int HeightCount = GridSize * GridSize;

    private readonly Dictionary<int, byte[]> _waterMaskByHeightBits = new();

    private DecodedTerrainCell(
        float[] heights,
        bool fromEsmHeightmap,
        bool fromRuntimeTerrain,
        bool missingTerrain)
    {
        Heights = heights;
        FromEsmHeightmap = fromEsmHeightmap;
        FromRuntimeTerrain = fromRuntimeTerrain;
        MissingTerrain = missingTerrain;
        // Lowest vertex on the SAME 33×33 grid the water masks sample (Morrowind's native 65×65 is
        // already downsampled by then), so the dry-cell early-outs below are exact rather than
        // conservative. Non-finite heights appear in runtime-DMP terrain; treat any as "unknown" by
        // falling to NegativeInfinity, which disables the early-out instead of silently dropping water.
        var minHeight = float.PositiveInfinity;
        foreach (var h in heights)
        {
            if (!float.IsFinite(h))
            {
                minHeight = float.NegativeInfinity;
                break;
            }

            if (h < minHeight) minHeight = h;
        }

        MinHeight = heights.Length == 0 ? float.NegativeInfinity : minHeight;
    }

    internal float[] Heights { get; }

    /// <summary>
    ///     Lowest height on the 33×33 grid, or <see cref="float.NegativeInfinity" /> when there is no
    ///     terrain or any vertex is non-finite. Lets the water-mask builders reject a cell that lies
    ///     entirely above the waterline without touching a single output pixel — the dominant cost of
    ///     the 2D map's terrain-textures layer, which samples up to 1,056² per cell.
    /// </summary>
    internal float MinHeight { get; }

    internal byte[]? LowResWaterMask { get; private set; }
    internal bool FromEsmHeightmap { get; }
    internal bool FromRuntimeTerrain { get; }
    internal bool MissingTerrain { get; }
    internal bool HasTerrain => Heights.Length == HeightCount;

    /// <summary>
    ///     Approximate managed footprint: the 33×33 height grid plus the most recent low-res
    ///     water mask. The per-height-bits water-mask dictionary is deliberately not walked (it
    ///     would need its lock on the render thread's hot path and is tiny — 1089 B per cached
    ///     height — next to the 68 KB terrain texture sets that dominate this cache).
    /// </summary>
    internal long EstimatedBytes => (long)Heights.Length * sizeof(float) + (LowResWaterMask?.Length ?? 0);

    internal static DecodedTerrainCell Decode(CellRecord cell)
    {
        var source = cell.Heightmap;
        var fromEsm = source is not null;
        var fromRuntime = false;
        if (source is null)
        {
            source = cell.RuntimeTerrainMesh?.ToLandHeightmap();
            fromRuntime = source is not null;
        }

        if (source is null)
        {
            return new DecodedTerrainCell([], false, false, true);
        }

        var calculated = source.CalculateHeights();

        // This cache is fixed at 33×33 (it also feeds the 2D map + water mask). Most games are 33×33
        // already; Morrowind's native 65×65 LAND is downsampled here by an integer step so the 2D map
        // keeps its existing look. The 3D viewer renders Morrowind from the native float[,] directly
        // (TerrainMeshBuilder bypasses this cache for grids larger than 33×33).
        var nativeEdge = calculated.GetLength(0);
        var step = nativeEdge > GridSize ? (nativeEdge - 1) / (GridSize - 1) : 1;
        var flat = new float[HeightCount];
        for (var y = 0; y < GridSize; y++)
        {
            for (var x = 0; x < GridSize; x++)
            {
                flat[y * GridSize + x] = calculated[y * step, x * step];
            }
        }

        return new DecodedTerrainCell(flat, fromEsm, fromRuntime, false);
    }

    internal float HeightAt(int x, int y)
    {
        return Heights[y * GridSize + x];
    }

    internal byte[]? GetLowResWaterMask(float? effectiveWaterHeight)
    {
        if (!HasTerrain || effectiveWaterHeight is not (> -1e6f and < 1e6f))
        {
            return null;
        }

        // Dry-cell early-out, same argument as GetHiResWaterMask but against this path's STRICT `<`
        // test: no vertex can satisfy HeightAt < waterH, so the mask is all-zero and the box blur of an
        // all-zero mask is all-zero — equivalent to null for every consumer. Skips the allocation and
        // the cache entry; the only observable difference is that LowResWaterMask (and therefore
        // EstimatedBytes) stays null for dry cells, which is the more accurate figure anyway.
        if (MinHeight >= effectiveWaterHeight.Value) return null;

        var key = BitConverter.SingleToInt32Bits(effectiveWaterHeight.Value);
        lock (_waterMaskByHeightBits)
        {
            if (_waterMaskByHeightBits.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var mask = new byte[HeightCount];
            for (var py = 0; py < GridSize; py++)
            {
                var srcY = GridSize - 1 - py;
                for (var px = 0; px < GridSize; px++)
                {
                    if (HeightAt(px, srcY) < effectiveWaterHeight.Value)
                    {
                        mask[py * GridSize + px] = 180;
                    }
                }
            }

            BlurWaterMask(mask, GridSize, GridSize);
            _waterMaskByHeightBits[key] = mask;
            LowResWaterMask = mask;
            return mask;
        }
    }

    /// <summary>
    ///     Build a 33×33 water mask whose blur spans this cell AND its four cardinal
    ///     neighbors' edge vertices, so the soft fade matches what the heightmap layer
    ///     produces from its single bitmap-wide mask. Without neighbor extension, the
    ///     per-cell 3×3 box blur runs against clamped borders and the water boundary
    ///     ends in a hard rectangular cutoff at cell edges (visible regression in the
    ///     texture-mode 2D map). Not cached because the neighbor configuration varies
    ///     by viewport.
    /// </summary>
    /// <summary>
    ///     Build a water mask at <paramref name="pixelsPerCell" /> resolution by
    ///     bilinear-interpolating the 33×33 heightmap at every output pixel and producing a
    ///     linear shoreline fade based on the height-vs-waterH delta. This is the texture-
    ///     path replacement for "build at 33×33 then nearest-neighbor upscale" — the latter
    ///     produced visible blocky shorelines at high zoom (TexturePixelsPerCell ≥ 132).
    ///     <para>
    ///         Cross-cell continuity is automatic because adjacent cells share their edge
    ///         vertices: this cell's east column = east neighbor's west column. No neighbor
    ///         parameters needed (unlike the 33×33 path, which had to extend the grid because
    ///         the post-blur kernel clamped at cell borders).
    ///     </para>
    ///     <para>
    ///         Shoreline softness: <paramref name="shorelineSoftnessUnits" /> defines the
    ///         half-width (in game vertical units) of the fade band around the waterline.
    ///         <c>h ≤ waterH - softness</c> ⇒ fully water (180); <c>h ≥ waterH + softness</c>
    ///         ⇒ fully land (0); linear in between. Visual band width on screen = softness /
    ///         local terrain slope — steep banks render crisp, gentle shores fade soft.
    ///     </para>
    ///     <para>
    ///         Not cached: size varies with zoom and the texture stream rebuilds per pass
    ///         anyway. Allocates one <c>byte[pxs*pxs]</c> per cell per call — same shape as
    ///         the legacy <see cref="DecodedTerrainCell.GetLowResWaterMask" /> + upscale chain.
    ///     </para>
    /// </summary>
    internal byte[]? GetHiResWaterMask(float? effectiveWaterHeight, int pixelsPerCell,
        float shorelineSoftnessUnits = 8f)
    {
        if (!HasTerrain || effectiveWaterHeight is not (> -1e6f and < 1e6f)) return null;
        if (pixelsPerCell < 2) return null;

        var waterH = effectiveWaterHeight.Value;
        // Dry-cell early-out. Every output pixel's height is a bilinear blend of four grid vertices, so
        // h >= MinHeight everywhere; if even the lowest vertex sits at or above the top of the fade band
        // then t <= 0 for every pixel and the mask is all-zero. An all-zero mask is ALREADY equivalent to
        // null downstream (WriteWaterTilePixels returns false -> BuildCellWaterTile returns null;
        // ApplyCellWaterOverlay no-ops on null), so this is output-identical — it just skips
        // pixelsPerCell² bilinear samples and a pixelsPerCell² allocation per cell per call. At the
        // texture layer's 1,056 px/cell that is ~1.1M iterations and ~1 MB of LOH churn saved for every
        // dry cell, which is all of them across a desert worldspace.
        if (MinHeight >= waterH + shorelineSoftnessUnits) return null;
        const byte InteriorIntensity = 180;
        var twoSoft = 2f * shorelineSoftnessUnits;
        var span = (float)(GridSize - 1); // 32 quad-spaces across the cell
        var vertsPerPixel = span / (pixelsPerCell - 1);

        var mask = new byte[pixelsPerCell * pixelsPerCell];
        for (var py = 0; py < pixelsPerCell; py++)
        {
            // py=0 → north edge of the cell = vertex y=GridSize-1 (matches the flip used by
            // GetLowResWaterMask and the downstream image space).
            var vy = span - py * vertsPerPixel;
            var vy0 = (int)vy;
            if (vy0 >= GridSize - 1) vy0 = GridSize - 2;
            if (vy0 < 0) vy0 = 0;
            var fy = vy - vy0;

            for (var px = 0; px < pixelsPerCell; px++)
            {
                var vx = px * vertsPerPixel;
                var vx0 = (int)vx;
                if (vx0 >= GridSize - 1) vx0 = GridSize - 2;
                if (vx0 < 0) vx0 = 0;
                var fx = vx - vx0;

                var h00 = HeightAt(vx0, vy0);
                var h10 = HeightAt(vx0 + 1, vy0);
                var h01 = HeightAt(vx0, vy0 + 1);
                var h11 = HeightAt(vx0 + 1, vy0 + 1);
                var top = h00 * (1f - fx) + h10 * fx;
                var bot = h01 * (1f - fx) + h11 * fx;
                var h = top * (1f - fy) + bot * fy;

                var t = (waterH - h + shorelineSoftnessUnits) / twoSoft;
                byte v;
                if (t <= 0f) v = 0;
                else if (t >= 1f) v = InteriorIntensity;
                else v = (byte)(t * InteriorIntensity);
                mask[py * pixelsPerCell + px] = v;
            }
        }

        return mask;
    }

    internal static byte[]? BuildLowResWaterMaskWithNeighbors(
        DecodedTerrainCell self,
        DecodedTerrainCell? north,
        DecodedTerrainCell? south,
        DecodedTerrainCell? east,
        DecodedTerrainCell? west,
        float? effectiveWaterHeight)
    {
        if (!self.HasTerrain || effectiveWaterHeight is not (> -1e6f and < 1e6f))
        {
            return null;
        }

        var waterH = effectiveWaterHeight.Value;

        // Dry-cell early-out. Unlike the single-cell paths this must also clear the NEIGHBORS, because
        // the 3×3 blur pulls their border rows in: a dry cell beside a submerged one still needs its
        // shoreline fade. Only terrain-BEARING neighbors can contribute (the else branches copy this
        // cell's own edge row when a neighbor is null or terrain-less), and MinHeight is
        // NegativeInfinity for those, so `HasTerrain &&` keeps a terrain-less neighbor from forcing the
        // slow path — while a genuinely lower neighbor still does.
        static bool NeighborCanWet(DecodedTerrainCell? n, float waterHeight)
        {
            return n is { HasTerrain: true } && n.MinHeight < waterHeight;
        }

        if (self.MinHeight >= waterH &&
            !NeighborCanWet(north, waterH) && !NeighborCanWet(south, waterH) &&
            !NeighborCanWet(east, waterH) && !NeighborCanWet(west, waterH))
        {
            return null;
        }

        const int N = GridSize; // 33
        const int E = N + 2; // 35 — 1 vertex of context per side

        var extMask = new byte[E * E];

        // Inner 33×33: this cell's own vertices.
        for (var py = 0; py < N; py++)
        {
            var srcY = N - 1 - py;
            for (var px = 0; px < N; px++)
            {
                if (self.HeightAt(px, srcY) < waterH)
                {
                    extMask[(py + 1) * E + px + 1] = 180;
                }
            }
        }

        // North border (extMask row 0): one vertex NORTH of our north edge — the north
        // neighbor's second-from-south row (its srcY=1; its srcY=0 IS our north edge).
        if (north != null && north.HasTerrain)
        {
            for (var px = 0; px < N; px++)
            {
                if (north.HeightAt(px, 1) < waterH) extMask[px + 1] = 180;
            }
        }
        else
        {
            for (var px = 0; px < N; px++) extMask[px + 1] = extMask[E + px + 1];
        }

        // South border (extMask row N+1): south neighbor's second-from-north row (srcY=N-2).
        if (south != null && south.HasTerrain)
        {
            for (var px = 0; px < N; px++)
            {
                if (south.HeightAt(px, N - 2) < waterH) extMask[(N + 1) * E + px + 1] = 180;
            }
        }
        else
        {
            for (var px = 0; px < N; px++) extMask[(N + 1) * E + px + 1] = extMask[N * E + px + 1];
        }

        // West border (extMask col 0): west neighbor's second-from-east column (x=N-2).
        if (west != null && west.HasTerrain)
        {
            for (var py = 0; py < N; py++)
            {
                var srcY = N - 1 - py;
                if (west.HeightAt(N - 2, srcY) < waterH) extMask[(py + 1) * E] = 180;
            }
        }
        else
        {
            for (var py = 0; py < N; py++) extMask[(py + 1) * E] = extMask[(py + 1) * E + 1];
        }

        // East border (extMask col N+1): east neighbor's second-from-west column (x=1).
        if (east != null && east.HasTerrain)
        {
            for (var py = 0; py < N; py++)
            {
                var srcY = N - 1 - py;
                if (east.HeightAt(1, srcY) < waterH) extMask[(py + 1) * E + N + 1] = 180;
            }
        }
        else
        {
            for (var py = 0; py < N; py++) extMask[(py + 1) * E + N + 1] = extMask[(py + 1) * E + N];
        }

        // Corners: cheap approximation — duplicate the adjacent cardinal edge. Diagonal
        // neighbors would be ideal but the cardinal extension already kills the visible
        // cutoff; a corner approximation error is one pixel out of ~135 and dominated
        // by the blur kernel.
        extMask[0] = extMask[1]; // NW
        extMask[E - 1] = extMask[E - 2]; // NE
        extMask[(E - 1) * E] = extMask[(E - 1) * E + 1]; // SW
        extMask[(E - 1) * E + (E - 1)] = extMask[(E - 1) * E + (E - 2)]; // SE

        // Blur the extended 35×35; crop the center 33×33.
        BlurWaterMask(extMask, E, E);
        var mask = new byte[N * N];
        for (var py = 0; py < N; py++)
        {
            for (var px = 0; px < N; px++)
            {
                mask[py * N + px] = extMask[(py + 1) * E + px + 1];
            }
        }

        return mask;
    }

    private static void BlurWaterMask(byte[] mask, int width, int height)
    {
        var blurred = new byte[mask.Length];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var sum = 0;
                var count = 0;
                var y0 = Math.Max(0, y - 1);
                var y1 = Math.Min(height - 1, y + 1);
                var x0 = Math.Max(0, x - 1);
                var x1 = Math.Min(width - 1, x + 1);

                for (var ny = y0; ny <= y1; ny++)
                {
                    for (var nx = x0; nx <= x1; nx++)
                    {
                        sum += mask[ny * width + nx];
                        count++;
                    }
                }

                blurred[y * width + x] = (byte)(sum / count);
            }
        }

        Array.Copy(blurred, mask, mask.Length);
    }
}

internal sealed class TextureWinnerGrid
{
    private const int QuadSize = 17;
    private const int QuadVertexCount = QuadSize * QuadSize;
    private const float AtxtOpacityThreshold = 0.5f;

    /// <summary>
    ///     "No winner at this vertex".
    ///     <para>
    ///         <b>Not zero.</b> Zero is <see cref="CellLayerWeightTable.EngineDefaultSentinelFormId" />
    ///         and is a MEANINGFUL value here — Morrowind's VTEX grid uses it for the engine-default
    ///         land texture — so a zero sentinel would silently erase every engine-default vertex.
    ///         <c>uint.MaxValue</c> is safe instead: it encodes plugin index 255, which the engine
    ///         reserves and no LTEX can occupy.
    ///     </para>
    /// </summary>
    private const uint NoWinner = uint.MaxValue;

    private readonly uint[] _winners;

    private TextureWinnerGrid(uint[] winners)
    {
        _winners = winners;
    }

    /// <summary>
    ///     Bytes this grid retains — the accounting the cache charges for it. Tracks the element
    ///     width rather than restating it, so a future format change cannot leave the registry
    ///     reporting the old size.
    /// </summary>
    internal long EstimatedBytes => (long)_winners.Length * sizeof(uint);

    /// <summary>Allocates a grid with every vertex unassigned.</summary>
    private static uint[] NewWinnerArray()
    {
        // uint[] rather than uint?[]: Nullable<uint> is 8 bytes after padding, so the nullable form
        // cost 9,248 bytes per cell against 4,624 — and one of these is retained for every cell in
        // the worldspace (41,219 on Fallout 76's Appalachia).
        var winners = new uint[4 * QuadVertexCount];
        Array.Fill(winners, NoWinner);
        return winners;
    }

    internal static TextureWinnerGrid? Build(List<LandTextureLayer> layers)
    {
        var winners = NewWinnerArray();
        var any = false;

        foreach (var layer in layers)
        {
            if (layer.Kind != LandTextureLayerKind.Base || layer.Quadrant >= 4)
            {
                continue;
            }

            var quadStart = layer.Quadrant * QuadVertexCount;
            for (var i = 0; i < QuadVertexCount; i++)
            {
                winners[quadStart + i] = layer.TextureFormId;
            }

            any = true;
        }

        var alphaLayers = new List<LandTextureLayer>();
        foreach (var layer in layers)
        {
            if (layer.Kind == LandTextureLayerKind.Alpha && layer.Quadrant < 4)
            {
                alphaLayers.Add(layer);
            }
        }

        alphaLayers.Sort(static (a, b) =>
        {
            var q = a.Quadrant.CompareTo(b.Quadrant);
            return q != 0 ? q : a.Layer.CompareTo(b.Layer);
        });

        foreach (var layer in alphaLayers)
        {
            var quadStart = layer.Quadrant * QuadVertexCount;
            // EnumerateVtxt17Entries, not BlendEntries: BTD-injected Starfield layers encode positions
            // on the native 65 grid, which this fixed 17-grid winner map would otherwise drop entirely.
            foreach (var entry in layer.EnumerateVtxt17Entries())
            {
                if (entry.Opacity < AtxtOpacityThreshold || entry.Position >= QuadVertexCount)
                {
                    continue;
                }

                winners[quadStart + entry.Position] = layer.TextureFormId;
                any = true;
            }
        }

        return any ? new TextureWinnerGrid(winners) : null;
    }

    /// <summary>
    ///     Builds a winner grid from Morrowind's flat <paramref name="vtexSize" />×<paramref name="vtexSize" />
    ///     VTEX land-texture grid (resolved to LTEX FormIds; 0 = engine-default). Each 33-grid vertex is
    ///     assigned its VTEX cell's texture using the SAME row/col mapping as
    ///     <c>CellLayerWeightTable.BuildFromVtexGrid</c> so the regions layer agrees with the textured
    ///     layer. Populated via <see cref="Lookup" />'s quadrant addressing so lookups round-trip.
    /// </summary>
    internal static TextureWinnerGrid? BuildFromVtex(uint[] vtex, int vtexSize = 16)
    {
        if (vtex.Length < vtexSize * vtexSize) return null;

        var winners = NewWinnerArray();
        var any = false;
        for (var py = 0; py <= 32; py++)
        {
            var isNorth = py <= 16;
            var qy = isNorth ? 16 - py : 32 - py;
            var row = Math.Min(vtexSize - 1, (32 - py) * vtexSize / 32); // py=0 north → top VTEX row
            for (var px = 0; px <= 32; px++)
            {
                var isEast = px > 16;
                var qx = isEast ? px - 16 : px;
                if (qx < 0 || qx >= QuadSize || qy < 0 || qy >= QuadSize) continue;
                var quad = (isNorth, isEast) switch
                {
                    (true, false) => 2,
                    (true, true) => 3,
                    (false, false) => 0,
                    (false, true) => 1
                };
                var col = Math.Min(vtexSize - 1, px * vtexSize / 32);
                winners[quad * QuadVertexCount + qy * QuadSize + qx] = vtex[row * vtexSize + col];
                any = true;
            }
        }

        return any ? new TextureWinnerGrid(winners) : null;
    }

    internal uint? Lookup(int px, int py)
    {
        var isNorth = py <= 16;
        var isEast = px > 16;
        var quad = (isNorth, isEast) switch
        {
            (true, false) => 2,
            (true, true) => 3,
            (false, false) => 0,
            (false, true) => 1
        };

        var qx = isEast ? px - 16 : px;
        var qy = isNorth ? 16 - py : 32 - py;

        if (qx < 0 || qx >= QuadSize || qy < 0 || qy >= QuadSize)
        {
            return null;
        }

        var winner = _winners[quad * QuadVertexCount + qy * QuadSize + qx];
        return winner == NoWinner ? null : winner;
    }
}
