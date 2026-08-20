using BethesdaMultitool.Core.Formats.Esm.Land.Btd;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Core.Vfs;

namespace BethesdaMultitool.Core.Formats.Esm.Land;

/// <summary>
///     Result of <see cref="BtdTerrainInjector.Inject" />. Owns the kept-open lazy
///     <see cref="BtdHeightSource" />s that back the injected cells'
///     <c>LandHeightmap.ExactHeightsProvider</c> delegates, so it must be disposed only when the
///     parsed cells' heightmaps are done being read — the load result
///     (<c>UnifiedAnalysisResult</c>, or the GUI session that adopts it) carries it.
/// </summary>
public sealed class BtdTerrainInjection : IDisposable
{
    private readonly List<IDisposable> _heightSources = [];

    /// <summary>Number of cells that received BTD terrain (heightmap and/or texture layers).</summary>
    public int PopulatedCells { get; private set; }

    /// <summary>True when at least one lazy height source is being kept open.</summary>
    public bool HasOpenSources => _heightSources.Count > 0;

    public void Dispose()
    {
        foreach (var source in _heightSources)
        {
            source.Dispose();
        }

        _heightSources.Clear();
    }

    internal void Add(int populated, IDisposable? source)
    {
        PopulatedCells += populated;
        if (source is not null)
        {
            _heightSources.Add(source);
        }
    }
}

/// <summary>
///     Attaches external-BTD terrain heights to parsed exterior cells for the games that have no
///     in-record VHGT — Fallout 76 and Starfield (see <see cref="GameProfile.HasExternalBtdTerrain" />).
///     Their worldspaces have CELL records, but the heightmap lives in a Bethesda Terrain Data
///     (<c>.btd</c>) file named after the worldspace editor ID. This injector gives each exterior cell
///     a <see cref="LandHeightmap" /> whose <see cref="LandHeightmap.ExactHeights" /> resolve through
///     the same float-grid path the runtime-mesh and Morrowind importers use — eagerly decoded for
///     small worldspaces, lazily via a kept-open <see cref="BtdHeightSource" /> for large ones — so
///     both the 2D world map and the 3D terrain renderer light up with zero changes to either
///     renderer: they read heights through the shared <c>DecodedTerrainCell.Decode</c> abstraction
///     (the 2D map downsamples; the 3D <c>TerrainMeshBuilder</c> renders the native grid directly).
///     <para>
///         Two lookup routes, in precedence order: a loose <c>Data\Terrain\&lt;editorId&gt;.btd</c>
///         (Fallout 76's <c>Appalachia.btd</c>) and an archived <c>terrain\&lt;editorId&gt;.btd</c>
///         (Starfield ships no loose assets — all 753 of its worldspace BTDs are inside
///         <c>Starfield - Terrain01..04.ba2</c> / <c>TerrainPatch.ba2</c>). The loose route keeps the
///         memory-mapped <see cref="BtdFile" /> path so Fallout 76's 1.48 GB file is never copied.
///     </para>
///     <para>
///         Cells are decoded at full native fidelity (LOD0 = 128×128 samples) into a 129×129 grid:
///         the extra row/column is the <b>shared edge</b> pulled from the east/north neighbor's
///         sample 0. These BTDs pack 128 <i>disjoint</i> samples per cell (a BTD tile is exactly
///         8×128 = 1024 wide, with no shared border), so without that +1 the cell mesh's east/north
///         edge would take its own sample 127 instead of the neighbor's sample 0 at the same world
///         position — a height mismatch that renders as a crack between cells.
///     </para>
/// </summary>
public static class BtdTerrainInjector
{
    // Native BTD resolution: LOD0 = 128 samples per cell edge. Full fidelity (the 3D viewer renders
    // the native grid via the variable-resolution TerrainMeshBuilder; the 2D map downsamples 129->33
    // by an exact step of 4). Heights are NOT materialized here: a 40k-cell worldspace (APPALACHIA)
    // would cost ~2.7 GB resident — instead each cell gets a lazy provider over a kept-open
    // BtdHeightSource, so only the cells a consumer actually reads are ever decoded (bounded LRU).
    private const int SourceLod = 0;
    private const int CellSamples = 128 >> SourceLod; // 128
    internal const int HeightGridSize = CellSamples + 1; // 129: own 128 samples + the neighbor's shared edge

    // FO76 land-texture grid size for VtexTextureFormIds (16×16, row-major south→north / west→east —
    // the renderer's Morrowind VTEX path resamples it to the live terrain grid).
    private const int VtexGridSize = 16;

    /// <summary>Edge of the BTD's per-cell land-texture alpha map at LOD 0.</summary>
    private const int LtexMapEdge = 128;

    /// <summary>Vertices per quadrant edge, matching the VTXT 17×17 Position convention.</summary>
    private const int QuadVertexEdge = 17;

    /// <summary>
    ///     Native blend-grid edge: one vertex per alpha-map pixel (64 per quadrant) plus the shared far
    ///     edge. Emitting at this edge keeps the BTD's full 128×128 blend fidelity for the 129-grid
    ///     renderer (<c>CellLayerWeightTable</c> resamples per its own grid); the classic 17 sampling
    ///     costs 15/16 of the map. Starfield only for now — its worldspaces top out at a few hundred
    ///     cells, while Appalachia's ~40k cells would multiply an already ~2.7 GB resident set, so FO76
    ///     stays on 17 until that cost is measured.
    /// </summary>
    private const int NativeBlendGridEdge = LtexMapEdge / 2 + 1;

    /// <summary>
    ///     Weighted layers a BTD quadrant can carry on top of its base texture. The A16 map packs one
    ///     3-bit weight per layer into each pixel (5 × 3 = 15 of the 16 bits), which is exactly the
    ///     count of layer slots <c>GetCellTextureSet</c> reports at <c>q*16 + 1..5</c>.
    /// </summary>
    private const int QuadrantLayerSlots = 5;

    /// <summary>Full opacity for a 3-bit packed weight.</summary>
    private const float PackedWeightMax = 7f;

    // LandHeightmap requires HeightDeltas, but ExactHeights takes precedence in CalculateHeights(),
    // so the deltas are never read — share one empty array (matching the Morrowind importer).
    private static readonly sbyte[] UnusedDeltas = [];

    /// <summary>
    ///     Populates exterior-cell heightmaps for every worldspace whose <c>.btd</c> can be found next
    ///     to <paramref name="esmPath" /> — loose first, then this game's terrain archives. No-op for
    ///     games without <see cref="GameProfile.HasExternalBtdTerrain" />. The returned
    ///     <see cref="BtdTerrainInjection" /> owns any kept-open lazy height sources and must live as
    ///     long as the parsed cells' heightmaps are read (the load result owns it).
    /// </summary>
    public static BtdTerrainInjection Inject(RecordCollection records, string esmPath)
    {
        ArgumentNullException.ThrowIfNull(records);
        var injection = new BtdTerrainInjection();
        if (string.IsNullOrEmpty(esmPath))
        {
            return injection;
        }

        var profile = GameDetector.DetectFromFile(esmPath);
        if (!profile.HasExternalBtdTerrain)
        {
            return injection;
        }

        var dir = Path.GetDirectoryName(Path.GetFullPath(esmPath));
        if (dir is null)
        {
            return injection;
        }

        using var resolver = new BtdSourceResolver(dir, profile);
        try
        {
            InjectWorldspaces(records, resolver, profile, injection);
        }
        catch
        {
            // An unfiltered exception must not strand already-opened lazy sources (each holds a
            // memory map): close them before propagating. Cells keep any providers already
            // attached, but those degrade to flat grids post-dispose rather than throwing.
            injection.Dispose();
            throw;
        }

        return injection;
    }

    private static void InjectWorldspaces(
        RecordCollection records, BtdSourceResolver resolver, GameProfile profile, BtdTerrainInjection injection)
    {
        foreach (var worldspace in records.Worldspaces)
        {
            if (string.IsNullOrEmpty(worldspace.EditorId))
            {
                continue;
            }

            // Collect the candidate cells BEFORE touching the file system. Starfield ships 753
            // worldspace BTDs but far fewer of them belong to a worldspace that actually has gridded
            // exterior cells in this plugin (AkilaWorld's 131 MB file has no WRLD in the base master
            // at all), so resolving first would extract gigabytes to then throw it away.
            var candidates = CollectCandidateCells(worldspace);
            if (candidates.Count == 0)
            {
                continue;
            }

            BtdFile? btd = null;
            try
            {
                btd = resolver.TryOpen(worldspace.EditorId!);
                if (btd is null)
                {
                    continue;
                }

                // Hold several 8×8-cell tiles resident: a cell's own tile plus the east/north/NE
                // neighbor tiles its shared edge reads from, so both the eager texture-layer sweep
                // and later lazy height reads decompress each tile's LOD pyramid once instead of
                // thrashing.
                btd.SetTileCacheSize(16);
                var (populated, source) = InjectWorldspace(candidates, btd, profile.Game == BethesdaGame.Starfield
                    ? NativeBlendGridEdge
                    : QuadVertexEdge);
                injection.Add(populated, source);
                if (source is not null)
                {
                    btd = null; // ownership moved into the height source (disposed with the injection)
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException)
            {
                // Corrupt / unsupported BTD (e.g. LZ4-frame) — leave the worldspace's cells flat.
                // Reaching here in the lazy route means no providers were attached (they are only
                // attached after every BTD read for the worldspace has succeeded), so no cell can
                // hold a reference to the file being closed below.
            }
            finally
            {
                btd?.Dispose();
            }
        }
    }

    /// <summary>
    ///     Exterior cells that could take BTD terrain: gridded, not interior, not already carrying a
    ///     heightmap. The BTD's own cell bounds are applied later, once it is open.
    /// </summary>
    private static List<CellRecord> CollectCandidateCells(WorldspaceRecord worldspace)
    {
        var candidates = new List<CellRecord>();
        foreach (var cell in worldspace.Cells)
        {
            if (cell.IsInterior || cell.Heightmap is not null)
            {
                continue;
            }

            if (cell.GridX is null || cell.GridY is null)
            {
                continue;
            }

            candidates.Add(cell);
        }

        return candidates;
    }

    private static (int Populated, BtdHeightSource? Source) InjectWorldspace(
        List<CellRecord> candidates, BtdFile btd, int blendEdge)
    {
        var targets = new List<CellRecord>(candidates.Count);
        foreach (var cell in candidates)
        {
            var gx = cell.GridX!.Value;
            var gy = cell.GridY!.Value;
            if (gx < btd.CellMinX || gx > btd.CellMaxX || gy < btd.CellMinY || gy > btd.CellMaxY)
            {
                continue;
            }

            targets.Add(cell);
        }

        if (targets.Count == 0)
        {
            return (0, null);
        }

        // Decode in BTD tile order (8×8-cell tiles). Arbitrary cell order would reload — and
        // re-decompress — the same tile repeatedly as the sweep and its neighbor-edge reads jump
        // around the grid; tile-grouping keeps each tile's decompressed LOD pyramid hot in the cache.
        targets.Sort(CompareByTile);

        // Heights go LAZY when keeping the file open is cheaper than materializing every grid:
        // always for a memory-mapped loose file (pageable, ~free — FO76's Appalachia route, where
        // eager 40k × 129² floats ≈ 2.7 GB), and for an archived byte[] only when the held bytes
        // undercut the decoded grids (Starfield's small worldspaces usually stay eager, exactly the
        // pre-lazy behavior). Texture layers stay eager either way: they are far smaller than
        // heights, and the 3D viewer's LoadData warms ALL cells' texture sets up front, which under
        // a lazy route would funnel the whole worldspace through the decode lock at 3D-open time.
        var decodedGridBytes = (long)targets.Count * HeightGridSize * HeightGridSize * sizeof(float);
        var lazyHeights = btd.IsMemoryMapped || btd.SourceLength < decodedGridBytes;

        foreach (var cell in targets)
        {
            var gx = cell.GridX!.Value;
            var gy = cell.GridY!.Value;
            if (!lazyHeights)
            {
                cell.Heightmap = new LandHeightmap
                {
                    HeightDeltas = UnusedDeltas,
                    ExactHeights = BuildExactHeights(btd, gx, gy)
                };
            }

            // Attach the cell's land textures so it shows its real blended ground instead of the single
            // engine default. FO76/Starfield have no in-CELL BTXT/ATXT — the textures live in the BTD —
            // but the BTD's own model (one base + up to five weighted layers per quadrant) IS the
            // BTXT/ATXT model, so it is rebuilt as layers and every existing consumer (the 16-slot
            // terrain shader, the 2D regions/textures blitters) works unchanged. The flat per-quadrant
            // grid remains only as the fallback for a cell whose layer rebuild yields nothing.
            if (cell.LandVisualData is null)
            {
                if (BuildTextureLayers(btd, gx, gy, blendEdge) is { } layers)
                {
                    cell.LandVisualData = new LandVisualData
                    {
                        TextureLayers = layers,
                        Source = VisualDataSource.MasterEsm
                    };
                }
                else if (BuildVtexFormIdGrid(btd, gx, gy) is { } vtex)
                {
                    cell.LandVisualData = new LandVisualData
                    {
                        VtexTextureFormIds = vtex,
                        Source = VisualDataSource.MasterEsm
                    };
                }
            }
        }

        if (!lazyHeights)
        {
            return (targets.Count, null);
        }

        // Provider attachment runs only after every BTD read above succeeded, and cannot itself
        // throw — so a corrupt BTD can never leave cells holding providers over a file the caller
        // is about to close.
        var source = new BtdHeightSource(btd);
        foreach (var cell in targets)
        {
            var gx = cell.GridX!.Value;
            var gy = cell.GridY!.Value;
            cell.Heightmap = new LandHeightmap
            {
                HeightDeltas = UnusedDeltas,
                ExactHeightsProvider = () => source.GetExactHeights(gx, gy)
            };
        }

        return (targets.Count, source);
    }

    /// <summary>
    ///     Rebuilds a cell's land textures from the BTD as BTXT/ATXT-shaped layers: each quadrant's
    ///     base texture plus up to five weighted layers whose per-vertex opacities are sampled from the
    ///     BTD's 128×128 alpha map.
    ///     <para>
    ///         This replaces reading only <c>texSet[q*16]</c> (one flat texture per quadrant, which
    ///         rendered four hard-edged blocks per cell and discarded the alpha map entirely — it was
    ///         never called anywhere in the codebase). Emitting the engine's own layer model means the
    ///         existing 16-slot blend shader and both 2D blitters consume it with no changes.
    ///     </para>
    ///     Returns null when no quadrant declares any usable texture, so the caller can fall back.
    /// </summary>
    private static List<LandTextureLayer>? BuildTextureLayers(BtdFile btd, int cellX, int cellY, int blendEdge)
    {
        // GetCellTextureSet leaves untouched slots alone, so pre-fill the "none" sentinel.
        var texSet = new byte[64];
        Array.Fill(texSet, (byte)0xFF);
        btd.GetCellTextureSet(texSet, cellX, cellY);

        var alphaMap = new ushort[LtexMapEdge * LtexMapEdge];
        btd.GetCellLandTexture(alphaMap, cellX, cellY);

        var layers = new List<LandTextureLayer>();
        for (var quadrant = 0; quadrant < 4; quadrant++)
        {
            var quadrantBase = quadrant << 4;

            var baseIndex = texSet[quadrantBase];
            if (baseIndex != 0xFF && btd.GetLandTexture(baseIndex) is var baseFormId and not 0)
            {
                layers.Add(new LandTextureLayer
                {
                    Kind = LandTextureLayerKind.Base,
                    TextureFormId = baseFormId,
                    Quadrant = (byte)quadrant
                });
            }

            for (var slot = 0; slot < QuadrantLayerSlots; slot++)
            {
                var index = texSet[quadrantBase + 1 + slot];
                if (index == 0xFF)
                {
                    continue;
                }

                var formId = btd.GetLandTexture(index);
                if (formId == 0)
                {
                    continue;
                }

                var entries = BuildBlendEntries(alphaMap, quadrant, slot, blendEdge);
                if (entries.Count > 0)
                {
                    layers.Add(new LandTextureLayer
                    {
                        Kind = LandTextureLayerKind.Alpha,
                        TextureFormId = formId,
                        Quadrant = (byte)quadrant,
                        Layer = (ushort)slot,
                        BlendEntries = entries,
                        BlendGridEdge = blendEdge
                    });
                }
            }
        }

        return layers.Count > 0 ? layers : null;
    }

    /// <summary>
    ///     Samples one quadrant's <paramref name="blendEdge" />×<paramref name="blendEdge" /> vertex
    ///     opacities for <paramref name="slot" /> out of the cell's alpha map. Quadrant index is
    ///     <c>(north &lt;&lt; 1) | east</c> and quadrant-local (qx, qy) grows east/north from the
    ///     quadrant's SW corner — the same convention <c>CellLayerWeightTable</c> decodes blend
    ///     Positions with, and the same south-to-north row order the BTD height grid uses. Zero-weight
    ///     vertices are omitted, exactly as a sparse VTXT omits them. At the classic 17 edge this
    ///     point-samples every 4th pixel; at <see cref="NativeBlendGridEdge" /> (65) it is 1:1 with the
    ///     alpha map, so no fidelity is lost.
    /// </summary>
    private static List<LandTextureBlendEntry> BuildBlendEntries(
        ushort[] alphaMap, int quadrant, int slot, int blendEdge)
    {
        const int quadPixelEdge = LtexMapEdge / 2;
        var eastOffset = (quadrant & 1) * quadPixelEdge;
        var northOffset = ((quadrant >> 1) & 1) * quadPixelEdge;
        var shift = slot * 3;
        var step = quadPixelEdge / (blendEdge - 1);

        var entries = new List<LandTextureBlendEntry>();
        for (var qy = 0; qy < blendEdge; qy++)
        {
            // blendEdge vertices span quadPixelEdge pixels, so the shared far edge clamps onto the
            // last pixel.
            var py = northOffset + Math.Min(qy * step, quadPixelEdge - 1);
            for (var qx = 0; qx < blendEdge; qx++)
            {
                var px = eastOffset + Math.Min(qx * step, quadPixelEdge - 1);
                var weight = (alphaMap[py * LtexMapEdge + px] >> shift) & 0x7;
                if (weight == 0)
                {
                    continue;
                }

                entries.Add(new LandTextureBlendEntry(
                    (ushort)(qy * blendEdge + qx), 0, 0, weight / PackedWeightMax));
            }
        }

        return entries;
    }

    /// <summary>
    ///     Builds the cell's 16×16 <see cref="LandVisualData.VtexTextureFormIds" /> grid from the BTD's
    ///     per-quadrant default land texture, so FO76 terrain shows its real regional textures rather than
    ///     the single engine default (FO76-1B). Each quadrant's dominant (base) land-texture index is mapped
    ///     through the BTD LTEX table to an LTEX FormID, which <c>TerrainTextureResolver12</c> resolves via
    ///     the LTEX→TXST→diffuse chain. Returns null when no quadrant has a texture (all engine-default), so
    ///     the cell keeps the default fallback. Per-pixel alpha blending (the BTD A16 weight map) is a later
    ///     refinement — the per-quadrant base already removes the "default everywhere" look.
    /// </summary>
    private static uint[]? BuildVtexFormIdGrid(BtdFile btd, int cellX, int cellY)
    {
        // GetCellTextureSet leaves a quadrant's default slot (byte q*16) untouched when the quadrant has no
        // texture, so pre-fill 0xFF ("none") to detect that.
        var texSet = new byte[64];
        Array.Fill(texSet, (byte)0xFF);
        btd.GetCellTextureSet(texSet, cellX, cellY);

        var quadFormIds = new uint[4];
        var anyReal = false;
        for (var q = 0; q < 4; q++)
        {
            var index = texSet[q << 4];
            if (index == 0xFF)
            {
                continue;
            }

            quadFormIds[q] = btd.GetLandTexture(index);
            anyReal |= quadFormIds[q] != 0;
        }

        return anyReal ? AssembleVtexGrid(quadFormIds) : null;
    }

    /// <summary>
    ///     Lays the four BTD quadrant land-texture FormIDs (indexed (north&lt;&lt;1)|east, i.e. SW/SE/NW/NE)
    ///     into the renderer's 16×16 row-major (south→north rows, west→east columns) VtexTextureFormIds grid:
    ///     SW fills the south-west 8×8 block, SE the south-east, NW the north-west, NE the north-east.
    /// </summary>
    internal static uint[] AssembleVtexGrid(uint[] quadrantFormIds)
    {
        var grid = new uint[VtexGridSize * VtexGridSize];
        for (var r = 0; r < VtexGridSize; r++)
        {
            var north = r >= VtexGridSize / 2 ? 1 : 0;
            for (var c = 0; c < VtexGridSize; c++)
            {
                var east = c >= VtexGridSize / 2 ? 1 : 0;
                grid[r * VtexGridSize + c] = quadrantFormIds[(north << 1) | east];
            }
        }

        return grid;
    }

    /// <summary>Orders cells by their 8×8-cell BTD tile, then by cell within the tile.</summary>
    private static int CompareByTile(CellRecord a, CellRecord b)
    {
        int ay = a.GridY!.Value >> 3, by = b.GridY!.Value >> 3;
        if (ay != by) return ay.CompareTo(by);
        int ax = a.GridX!.Value >> 3, bx = b.GridX!.Value >> 3;
        if (ax != bx) return ax.CompareTo(bx);
        if (a.GridY!.Value != b.GridY!.Value) return a.GridY!.Value.CompareTo(b.GridY!.Value);
        return a.GridX!.Value.CompareTo(b.GridX!.Value);
    }

    /// <summary>
    ///     Decodes one BTD cell at full native resolution into a 129×129 grid of world heights (game
    ///     units). Row/column 0 is the south/west edge (VHGT / ExactHeights convention); the last
    ///     row/column (index 128) is the shared edge taken from the north/east neighbor's sample 0,
    ///     clamped to this cell's own outermost sample at the worldspace boundary.
    /// </summary>
    internal static float[,] BuildExactHeights(BtdFile btd, int cellX, int cellY)
    {
        var self = btd.GetCellHeightGrid(cellX, cellY); // float[CellSamples²], south-to-north
        var exact = new float[HeightGridSize, HeightGridSize];

        // Interior: this cell's own samples.
        for (var j = 0; j < CellSamples; j++)
        {
            for (var i = 0; i < CellSamples; i++)
            {
                exact[j, i] = self[j * CellSamples + i];
            }
        }

        FillSharedEdges(btd, exact, self, cellX, cellY);
        return exact;
    }

    /// <summary>
    ///     Fills the 129×129 grid's east column and north row (and the NE corner) from the adjacent
    ///     cells' sample 0, so neighboring cell meshes meet without a crack. At the worldspace edge
    ///     (no neighbor) the cell's own outermost sample is repeated.
    /// </summary>
    private static void FillSharedEdges(BtdFile btd, float[,] exact, float[] self, int cellX, int cellY)
    {
        var hasEast = cellX < btd.CellMaxX;
        var hasNorth = cellY < btd.CellMaxY;
        const int last = CellSamples - 1;

        for (var j = 0; j < CellSamples; j++)
        {
            exact[j, CellSamples] = hasEast
                ? btd.GetCellHeightSample(cellX + 1, cellY, 0, j)
                : self[j * CellSamples + last];
        }

        for (var i = 0; i < CellSamples; i++)
        {
            exact[CellSamples, i] = hasNorth
                ? btd.GetCellHeightSample(cellX, cellY + 1, i, 0)
                : self[last * CellSamples + i];
        }

        // North-east corner: the NE neighbor's (0,0), else extend whichever edge exists inward.
        if (hasEast && hasNorth)
        {
            exact[CellSamples, CellSamples] = btd.GetCellHeightSample(cellX + 1, cellY + 1, 0, 0);
        }
        else if (hasNorth)
        {
            exact[CellSamples, CellSamples] = exact[CellSamples, last];
        }
        else if (hasEast)
        {
            exact[CellSamples, CellSamples] = exact[last, CellSamples];
        }
        else
        {
            exact[CellSamples, CellSamples] = self[last * CellSamples + last];
        }
    }

    /// <summary>
    ///     Finds a worldspace's <c>.btd</c> and opens it, cheapest route first. The mounts are built
    ///     lazily and reused across every worldspace in one <see cref="Inject" /> run.
    ///     <list type="number">
    ///         <item>
    ///             A loose <c>Data\Terrain\&lt;editorId&gt;.btd</c> — opened by path so it stays
    ///             memory-mapped. This is Fallout 76's only route, and it costs no archive parse.
    ///         </item>
    ///         <item>
    ///             The game's terrain archives (<see cref="GameProfile.TerrainArchiveNamePatterns" />).
    ///             For Starfield that is 5 archives / ~2,000 entries.
    ///         </item>
    ///         <item>
    ///             The whole Data folder, when <see cref="GameProfile.TerrainSearchesAllDataArchives" />
    ///             is set — Starfield's DLC and update archives carry terrain too. Built at most once
    ///             per run because it is the expensive one (89 archives, ~1.5 M entries).
    ///         </item>
    ///     </list>
    ///     Probing the terrain archives before the full folder inverts normal load-order precedence, so
    ///     it is only safe because the two name sets are disjoint on retail data — no terrain file
    ///     appears in both. A future archive that overrode one would silently lose, which is why a
    ///     phase-2 hit after a phase-1 miss is the only path that can reach the DLC copies.
    /// </summary>
    private sealed class BtdSourceResolver(string dataDirectory, GameProfile profile) : IDisposable
    {
        private LayeredGameFileSystem? _allArchives;
        private bool _allArchivesBuilt;
        private LayeredGameFileSystem? _terrainArchives;
        private bool _terrainArchivesBuilt;

        public void Dispose()
        {
            _terrainArchives?.Dispose();
            _allArchives?.Dispose();
        }

        public BtdFile? TryOpen(string editorId)
        {
            var loosePath = Path.Combine(dataDirectory, "Terrain", editorId + ".btd");
            if (File.Exists(loosePath))
            {
                return new BtdFile(loosePath);
            }

            var virtualPath = Path.Combine("Terrain", editorId + ".btd");
            var bytes = TerrainArchives()?.TryReadAllBytes(virtualPath)
                        ?? AllArchives()?.TryReadAllBytes(virtualPath);
            return bytes is null ? null : new BtdFile(bytes);
        }

        private LayeredGameFileSystem? TerrainArchives()
        {
            if (!_terrainArchivesBuilt)
            {
                _terrainArchivesBuilt = true;
                if (profile.TerrainArchiveNamePatterns.Count > 0)
                {
                    _terrainArchives = GameFileSystem.OpenArchiveSubset(
                        dataDirectory, profile.TerrainArchiveNamePatterns, false,
                        ArchiveHandleRegistry.Shared);
                }
            }

            return _terrainArchives;
        }

        private LayeredGameFileSystem? AllArchives()
        {
            if (!_allArchivesBuilt)
            {
                _allArchivesBuilt = true;
                if (profile.TerrainSearchesAllDataArchives)
                {
                    _allArchives = GameFileSystem.OpenDataFolder(
                        dataDirectory, false, true,
                        ArchiveHandleRegistry.Shared);
                }
            }

            return _allArchives;
        }
    }
}
