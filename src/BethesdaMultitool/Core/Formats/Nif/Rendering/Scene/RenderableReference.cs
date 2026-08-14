using System.IO;
using System.Numerics;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Inspection;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Textures;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Scene;

/// <summary>
///     v3 Phase 3 — a single placed-object draw item, baked once per <c>LoadData</c> from a
///     <see cref="PlacedReference" />. The render loop reuses these every frame instead of
///     recomposing the world matrix and re-resolving the filter conditions per cell visit.
///     <para>
///         <see cref="WorldMatrix" /> is the composed model-to-world matrix. Per the
///         Gamebryo / NifSkope Euler convention the world rotation is column-vector
///         <c>Rx * Ry * Rz</c> (radians, right-handed CCW; Z up). System.Numerics is
///         row-vector, so the equivalent is <c>world = S * Rz * Ry * Rx * T</c> in
///         row-vector algebra. The vertex shader does
///         <c>mul(viewProj, mul(world, position))</c>; CPU-side this matches
///         <c>position * world * viewProj</c>.
///     </para>
///     <para>
///         <see cref="BoundsCenter" /> + <see cref="BoundsRadius" /> are a conservative
///         bounding sphere in world space derived from the base record's OBND. Used for
///         per-REFR cylinder culling on top of the cell-level cull.
///     </para>
/// </summary>
internal readonly record struct RenderableReference(
    uint FormId,
    Matrix4x4 WorldMatrix,
    string ModelPath,
    Vector3 BoundsCenter,
    float BoundsRadius,
    uint MeshId,
    bool IsInitiallyDisabled,
    bool IsMarker,
    bool IsImposter,
    PlacedObjectCategory Category,
    AlternateTextureSet? AlternateTextures = null,
    bool IsGrass = false,
    // Raw positive finite GRAS DATA WavePeriod field. The recovered shader consumes this value
    // as a direct phase multiplier; the field name deliberately avoids claiming seconds/period
    // or a reciprocal conversion that the retail evidence does not support.
    float GrassWaveMultiplier = 0f)
{
    private static readonly char[] PathSeparators = ['/', '\\'];

    /// <summary>
    ///     Cull-sphere radius (world units) used for a reference whose base record has NO OBND, until its
    ///     mesh first resolves and supplies true bounds. Sized to one exterior cell (4096) so it comfortably
    ///     contains single-cell architecture: an Oblivion cathedral measures a local bounds radius of ~3049,
    ///     and OBND is absent on EVERY Oblivion record, so a smaller value (the previous 1024) culled such
    ///     large meshes before they could decode and self-correct — a chicken-and-egg, since decode only runs
    ///     for cull survivors (ReferenceRenderer12). Structures larger than one cell still need OBLIV-1
    ///     (model-derived bounds). See memory: oblivion-nonrender-nifs-decode-ok.
    /// </summary>
    internal const float NoBoundsFallbackRadius = 4096f;

    /// <summary>
    ///     Click-target radius (world units) used for SELECTION ONLY when a reference has no OBND and its
    ///     mesh has not resolved real bounds — distinct from the generous <see cref="NoBoundsFallbackRadius" />
    ///     used for culling. The cull sphere must be large (one cell) so a big OBND-less mesh survives the
    ///     cull long enough to decode and self-correct; but reusing that 4096 sphere for the pick broadphase
    ///     turns every OBND-less ref into a cell-wide click zone, so in partly-supported games (Oblivion
    ///     trees never decode) overlapping fallback spheres swallow every click. A small "prop-sized" target
    ///     keeps these refs selectable without blanketing the cell. Refs WHOSE mesh resolves still use the
    ///     tight mesh-local bounds; this only governs the never-resolved ones. See memory:
    ///     oblivion-nonrender-nifs-decode-ok.
    /// </summary>
    internal const float SelectionFallbackRadius = 256f;

    /// <summary>
    ///     4-pre Item B — computes the stable per-process MeshId from a ModelPath. Used to
    ///     dedupe the per-REFR mesh-cache lookup in the cull loop: instead of doing a
    ///     case-insensitive string hash + dict lookup per REFR (~80 ns × 5000 REFRs), the
    ///     cull loop reads from a per-frame <c>Dictionary&lt;uint, CachedNifMesh12?&gt;</c>
    ///     by the int MeshId (~25 ns hash). Same-process guarantee is sufficient: the
    ///     registry / per-frame resolve map both live and die with the process.
    /// </summary>
    public static uint ComputeMeshId(string modelPath)
        => (uint)string.GetHashCode(modelPath, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    ///     True when <paramref name="modelPath" /> is an engine "marker" object the game hides in
    ///     play — XMarker/XMarkerHeading, map/travel/teleport/door markers, and the data-defined
    ///     encounter/idle markers under a <c>markers\</c> folder. Engine-derived: the game hardcodes
    ///     a <c>marker*.nif</c> set and assembles markers under <c>EditorMarker</c> nodes that
    ///     <c>RemoveEditorMarkers</c> strips in-game (the embedded-shape case is already filtered by
    ///     <see cref="NifBlockParsers.IsEditorHelperShape" />; this covers the standalone statics
    ///     whose shapes are named e.g. <c>MarkerX:0</c>). Matches the FILENAME prefix (so
    ///     "market"/"supermarket" are NOT misclassified) or a whole <c>markers</c> path segment
    ///     (so "markers2" is not). See memory: ghidra_marker_hide_mechanism.
    /// </summary>
    public static bool IsMarkerModelPath(string? modelPath)
    {
        if (string.IsNullOrEmpty(modelPath)) return false;
        var segments = modelPath.Split(PathSeparators, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return false;
        if (segments[^1].StartsWith("marker", StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var segment in segments)
        {
            if (segment.Equals("markers", StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>
    ///     True when <paramref name="modelPath" /> is an FNV "imposter" — a low-detail distant
    ///     stand-in for geometry that lives elsewhere in the SAME worldspace (the real building/SCOL
    ///     is placed nearby, just at a different origin — e.g. SSHQ's imposter sits ~1195 units from
    ///     its <c>SCOL\SSHQExterior03</c>). The engine hides the imposter once the player's region
    ///     loads the real geometry (region-based; decompiled
    ///     <c>TESRegionDataImposter::SetImpostersVisible</c> / <c>GetIsImposter</c>). In a static
    ///     full-detail render the whole worldspace is loaded, so every imposter is redundant — they
    ///     are all culled (the real STAT/SCOL geometry remains). Identified by the engine's path
    ///     convention: an <c>imposter</c> folder segment OR a <c>_imposter.nif</c> filename suffix.
    ///     Also true for FO4-style distant LOD statics (a <c>LOD</c> folder segment, e.g.
    ///     <c>LOD\Neighborhoods\Fens\Fens10_Bld01LOD.nif</c>, or a <c>*LOD.nif</c> filename) —
    ///     visible-when-distant stand-ins the engine swaps out for full models, so rendering both
    ///     z-fights. <c>*explod.nif</c> is excluded: FNV DLC explosion FX meshes end in "explod".
    ///     See memory: viewer_imposter_doubling.
    ///
    ///     Skyrim is deliberately excluded from the filename-suffix heuristic: its master places
    ///     some <c>*LOD.nif</c> meshes as the only parent-world representation of architecture
    ///     (for example Whiterun's WRCastleMainBuilding01LOD), so treating the suffix alone as an
    ///     imposter makes real geometry disappear. Explicit <c>imposter</c>/<c>LOD</c> path segments
    ///     retain their existing meaning. <see cref="BethesdaGame.Unknown" /> preserves the legacy
    ///     conservative classification for callers that do not yet have a game identity.
    /// </summary>
    public static bool IsImposterModelPath(
        string? modelPath,
        BethesdaGame game = BethesdaGame.Unknown)
    {
        if (string.IsNullOrEmpty(modelPath)) return false;
        var segments = modelPath.Split(PathSeparators, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return false;
        if (segments[^1].EndsWith("_imposter.nif", StringComparison.OrdinalIgnoreCase)) return true;
        if (game != BethesdaGame.Skyrim &&
            segments[^1].EndsWith("lod.nif", StringComparison.OrdinalIgnoreCase) &&
            !segments[^1].EndsWith("explod.nif", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (var segment in segments)
        {
            if (segment.Equals("imposter", StringComparison.OrdinalIgnoreCase)) return true;
            if (segment.Equals("lod", StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>
    ///     True when the placement's BASE record is an FO4-era LOD-duplicate static: a STAT whose
    ///     EditorID ends in <c>_LOD</c> (e.g. <c>MetalIntCeilingA1x1Mid01_LOD</c>). Its MODL is the
    ///     SAME full-detail mesh as its normal sibling STAT — only its MNAM carries the real
    ///     <c>LOD\*.nif</c> — and Bethesda places it as an extra visible-when-distant ref purely to
    ///     seed distant-LOD generation, coincident with the real ref (Fallout4.esm: 29 such STATs,
    ///     225 refs). Drawing it double-renders the full mesh on top of itself (z-fighting). The
    ///     model PATH is the normal mesh, so <see cref="IsImposterModelPath" /> cannot catch these;
    ///     the EditorID suffix is the stable marker (no <c>*_LOD</c> STATs exist in the FNV or
    ///     Skyrim SE masters).
    /// </summary>
    public static bool IsLodDuplicateBaseEditorId(string? baseEditorId)
        => !string.IsNullOrEmpty(baseEditorId) &&
           baseEditorId.EndsWith("_LOD", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    ///     Builds a <see cref="RenderableReference" /> from a <see cref="PlacedReference" />.
    ///     Returns <c>null</c> for ACHR/ACRE (skinned actors — deferred to v4), refs without a
    ///     resolved model path, or refs the renderer cannot place (e.g. NaN coordinates).
    ///     <paramref name="category" /> is the base object's <see cref="PlacedObjectCategory" />
    ///     (resolved by the caller from the category index) so the renderer can apply per-category
    ///     visibility filtering — for example, activators hidden by the user.
    /// </summary>
    public static RenderableReference? TryBuild(
        PlacedReference placement,
        PlacedObjectCategory category = PlacedObjectCategory.Unknown,
        AlternateTextureSet? alternateTextures = null,
        bool xespDisabled = false,
        BethesdaGame game = BethesdaGame.Unknown)
    {
        // Skip skinned actors — v3 renders static meshes only.
        if (placement.RecordType is "ACHR" or "ACRE") return null;
        if (string.IsNullOrEmpty(placement.ModelPath)) return null;

        // Pathological NaN/Inf coords sometimes appear in DMP-only loads where parser fell back
        // to garbage memory. Defensive skip — better to drop a single REFR than NaN-poison the
        // GPU draw.
        if (!float.IsFinite(placement.X) || !float.IsFinite(placement.Y) || !float.IsFinite(placement.Z))
            return null;

        var world = ComposeWorldMatrix(placement);
        var (center, radius) = ComposeWorldBounds(placement, world);

        if (DumpFilter is { Length: > 0 } && placement.ModelPath!.Contains(DumpFilter, StringComparison.OrdinalIgnoreCase))
            DumpRefr(placement, world);

        // Fold the alternate-texture variant key into the MeshId so re-skinned placements of a shared
        // NIF get distinct per-frame resolve slots + mesh-cache entries (see ReferenceMeshCache12).
        var meshId = alternateTextures is null
            ? ComputeMeshId(placement.ModelPath!)
            : ComputeMeshId(placement.ModelPath! + "#" + alternateTextures.VariantKey);

        return new RenderableReference(
            FormId: placement.FormId,
            WorldMatrix: world,
            ModelPath: placement.ModelPath!,
            BoundsCenter: center,
            BoundsRadius: radius,
            MeshId: meshId,
            // XESP enable-parent chains slave a ref's enable state to its parent; the resolved
            // initial-world state rides the same flag the cull's ShowInitiallyDisabled toggle reads.
            // XSRF Imposter refs are deliberately NOT folded in: the 2026-08-10 census of all 301
            // FNV XSRF refs showed 300 are the Vegas-skyline cross-worldspace imposter set (Strip/
            // McCarran walls, freeway, casino window-glow lights) that RETAIL RENDERS — hiding them
            // stripped the skyline (A/B: TestOutput/fo3-parity-2026-08/xsrf-ab). The one ending-FX
            // ref (vLegateCampFortFireFX) is already silenced by the dormant-triggered-FX particle
            // resolve (its NIF's only sequence is 'Forward'), verified at the reported pose.
            IsInitiallyDisabled: placement.IsInitiallyDisabled || xespDisabled,
            IsMarker: IsMarkerModelPath(placement.ModelPath),
            IsImposter: IsImposterModelPath(placement.ModelPath, game) ||
                        IsLodDuplicateBaseEditorId(placement.BaseEditorId),
            Category: category,
            AlternateTextures: alternateTextures);
    }

    /// <summary>
    ///     The composed model-to-world matrix. The engine's REFR orientation is decompiled from the
    ///     Xbox 360 MemDebug XEX (tools/GhidraProject/refr_rotation_decompiled.txt):
    ///     <list type="bullet">
    ///       <item>
    ///         <c>TESObjectREFR::GetOrientation</c> (VA 0x823A3738) calls
    ///         <c>NiMatrix3::FromEulerAnglesXYZ(rotX, rotY, rotZ)</c> (VA 0x82E20B38).
    ///       </item>
    ///       <item>
    ///         <c>FromEulerAnglesXYZ</c> builds <c>M = Rx · (Ry · Rz)</c> — column-vector
    ///         standard right-handed rotations (each <c>Make*Rotation</c> is the textbook RH
    ///         matrix, e.g. <c>MakeZRotation = [c,-s,0; s,c,0; 0,0,1]</c>).
    ///       </item>
    ///     </list>
    ///     On-screen the rotation is <c>W = Rx(−RotX)·Ry(−RotY)·Rz(−RotZ)</c> — the engine matrix
    ///     <c>M = Rx·Ry·Rz</c> with ALL THREE Euler angles negated (the renderer's world frame is a
    ///     chirality flip of the engine's, so a rotation reads as its negation). See
    ///     <see cref="ComposeWorldMatrix" /> for the derivation, proven against ground-truth quarry
    ///     conveyor placement geometry. Pinned by <c>EngineRotationConventionTests</c>.
    /// </summary>
    // Live ground-truth diagnostic. Set FALLOUT_VIEWER_DUMP_REFR=<substring> (matched against the
    // ModelPath, e.g. "road" or "dome") to append, for every matching placed object the live viewer
    // loads: the parsed rotation, the world-space bearing its local +X/+Y axes end up pointing, AND
    // the full affine world matrix (the exact transform handed to the GPU). The matrix lets us apply a
    // mesh's real connector-vertex local coords offline and measure whether consecutive pieces' joints
    // actually coincide in the live transform (e.g. monorail curve "slight rotation offset"). Output:
    // %TEMP%\fallout_refr_dump.txt. Capped to avoid runaway writes.
    private static readonly string? DumpFilter =
        EnvironmentVariables.Get(EnvironmentVariables.Viewer.DumpReference);
    private static readonly object DumpLock = new();
    private static int _dumpCount;

    private static void DumpRefr(PlacedReference p, Matrix4x4 world)
    {
        lock (DumpLock)
        {
            if (_dumpCount >= 800) return;
            _dumpCount++;
            var origin = Vector3.Transform(Vector3.Zero, world);
            var lx = Vector3.Transform(Vector3.UnitX, world) - origin;
            var ly = Vector3.Transform(Vector3.UnitY, world) - origin;
            var lz = Vector3.Transform(Vector3.UnitZ, world) - origin;
            static float Bearing(Vector3 v) => MathF.Atan2(v.Y, v.X) * 180f / MathF.PI;
            var line =
                $"0x{p.FormId:X8} '{p.ModelPath}' pos=({p.X:F0},{p.Y:F0},{p.Z:F0}) " +
                $"rotZdeg={p.RotZ * 180f / MathF.PI:F1} rotX={p.RotX:F3} rotY={p.RotY:F3} scale={p.Scale:F2} " +
                $"|+X->bearing {Bearing(lx):F1} (z {lx.Z:F2}) |+Y->bearing {Bearing(ly):F1} (z {ly.Z:F2}) " +
                $"|+Z->({lz.X:F2},{lz.Y:F2},{lz.Z:F2}) " +
                // Full affine world matrix (row-vector: worldPt = localPt * W). 3x3 = scale·rotation,
                // T = translation. Apply mesh connector-local coords to test joint coincidence.
                $"|W3x3=[{world.M11:F5},{world.M12:F5},{world.M13:F5};" +
                $"{world.M21:F5},{world.M22:F5},{world.M23:F5};" +
                $"{world.M31:F5},{world.M32:F5},{world.M33:F5}] " +
                $"T=({world.M41:F2},{world.M42:F2},{world.M43:F2})";
            try
            {
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "fallout_refr_dump.txt"), line + Environment.NewLine);
            }
            catch
            {
                // Diagnostic only — never let a logging failure break rendering.
            }
        }
    }

    // The REFR placement matrix. The convention (negate all three DATA Euler angles — the engine
    // builds M=Rx·Ry·Rz and renders M·v; this renderer's world frame is a chirality flip, so it
    // applies M(−θ)) lives in PlacedReferenceTransform so the 3D viewer and the 2D top-down map share
    // ONE source of truth — see that type for the full engine derivation, the conveyor-geometry proof,
    // and the dead-ends. The mesh scene-root node's OWN transform is NOT applied here: it is discarded
    // at bake time (NifSceneGraphWalker.ComputeWorldTransforms treatRootsAsIdentity), because placing
    // a REFR replaces the scene root's transform with this placement.
    private static Matrix4x4 ComposeWorldMatrix(PlacedReference p)
        => PlacedReferenceTransform.ComposeWorldMatrix(p.X, p.Y, p.Z, p.RotX, p.RotY, p.RotZ, p.Scale);

    /// <summary>
    ///     World-space bounding sphere from the base record's OBND, conservatively wrapped
    ///     around the rotated AABB. Falls back to a fixed-radius sphere when OBND is absent
    ///     (some MSTT / runtime-only refs). Computed once at LoadData so the per-frame cull
    ///     just does a <c>(centerWorld - cameraXY).LengthSq &lt; (radius + cylinderRadius)^2</c>.
    /// </summary>
    private static (Vector3 Center, float Radius) ComposeWorldBounds(PlacedReference p, Matrix4x4 world)
    {
        var bounds = p.Bounds;
        if (bounds is null || bounds.IsDegenerate)
        {
            // No OBND (every Oblivion record, plus some MSTT / runtime-only refs) — or an authored
            // all-zero OBND, which is "no data" in disguise: a zero-extent sphere would cull the mesh
            // before it could decode AND give the pick a zero-size target (part9 "zero-(0,0,0)-OBND
            // objects not clickable"). Use a generous fallback sphere centered at the REFR position.
            // This radius is TRANSIENT: it only gates the cull until the mesh resolves, after which
            // _meshLocalRadius supplies the true local bounds. It MUST cover single-cell architecture
            // (a cathedral's local radius is ~3049): decode only runs for cull survivors, so an
            // under-sized fallback culls a large mesh before it can decode + self-correct, leaving it
            // permanently invisible. See NoBoundsFallbackRadius.
            return (new Vector3(p.X, p.Y, p.Z), NoBoundsFallbackRadius);
        }

        // OBND is in mesh-local space. The conservative sphere = (centerLocal · world) for the
        // center, and (maxExtent · scale) for the radius — over-approximates but is cheap and never
        // under-culls the AUTHORED OBND. It cannot guarantee containment of actual mesh geometry when
        // shipped OBND data is displaced or undersized; the resident-mesh cull path corrects that later.
        var localCenter = new Vector3(
            (bounds.X1 + bounds.X2) * 0.5f,
            (bounds.Y1 + bounds.Y2) * 0.5f,
            (bounds.Z1 + bounds.Z2) * 0.5f);
        var localExtents = new Vector3(
            (bounds.X2 - bounds.X1) * 0.5f,
            (bounds.Y2 - bounds.Y1) * 0.5f,
            (bounds.Z2 - bounds.Z1) * 0.5f);

        var worldCenter = Vector3.Transform(localCenter, world);
        var scale = p.Scale > 0f ? p.Scale : 1f;
        // Diagonal of the AABB is the tightest sphere that contains the rotated box.
        var radius = localExtents.Length() * scale;
        // Safety floor — vanishingly small OBNDs (sometimes 0/0/0/0/0/0 in DMP captures) would
        // get culled before they ever appear. 64 ≈ a small prop.
        if (radius < 64f) radius = 64f;
        return (worldCenter, radius);
    }
}

