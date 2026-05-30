# v3 Scope: Real-Time 3D Cell/Worldspace Viewer

**Status:** Active. v2 complete; v3 work authorized. Open questions resolved (see "Resolved decisions" below). **Phase 0a complete 2026-05-29 — rendering pipeline migrated to Vortice.Windows; all 15 GPU tests green. Phase 0b complete 2026-05-29 — SwapChainPanel + Vortice swapchain wired through a 2D/3D toggle in the World Map tab, Sprite-mode removed, full build green, accessibility ratchet green, NpcRenderSmokeTests still green, spinning triangle visually confirmed (Path A validated — no Path B fallback needed).** Phase 1 (camera + cell-origin transforms) is next.
**Owner:** slfx77
**Last updated:** 2026-05-29

## Vision

Replace (or augment) the current 2D overhead map in [WorldMapControl](../src/FalloutXbox360Utils/App/Controls/WorldMapControl.xaml.cs) with a real-time 3D viewer that renders:

- Terrain heightmaps with multi-layer ground textures (LTEX/BTXT/ATXT/VTXT blending)
- Placed REFR objects loaded from their NIF models, with correct position/rotation/scale
- Flythrough camera (WASD + mouse look + scroll zoom) and cell/worldspace selection
- Data sourced from any of the formats we already parse — ESM, DMP runtime state, FXS save state

The 3D viewer is intended as a sibling to the 2D viewer, not a hard replacement. The 2D map remains useful for navigation, marker overlays, and quick visual diffing — both will be available.

## Why this is a v3 feature

- Crosses every major subsystem (ESM, NIF, BSA, GUI, GPU) and benefits from a clean version boundary.
- Adds a new live D3D/Vulkan surface inside WinUI 3, which is the first persistent GPU surface in the app (everything today is offscreen render-to-texture). This needs design space, not an opportunistic patch onto v2.
- Avoids scope creep mid-v2 — v2 is finishing ESM/DMP runtime coverage, plugin packing, and dmp→esp conversion work.

## Current state — what already exists and is reusable

| Subsystem | Location | Reusable for v3 |
|---|---|---|
| Placement data | [CellRecord.cs](../src/FalloutXbox360Utils/Core/Formats/Esm/Models/Records/World/CellRecord.cs) `PlacedObjects` | Yes — model path, position, rotation, scale, base FormID already populated at parse time |
| Terrain geometry | [RuntimeTerrainMesh.cs](../src/FalloutXbox360Utils/Core/Formats/Esm/Models/RuntimeTerrainMesh.cs), [LandHeightmap.cs](../src/FalloutXbox360Utils/Core/Formats/Esm/Models/World/LandHeightmap.cs) | Yes — 33×33 vertex grids, normals, VCLR ready; just need GPU upload path |
| Terrain export proof | `TerrainObjExporter` under [Core/Formats/Esm/Records/](../src/FalloutXbox360Utils/Core/Formats/Esm/Records/) | Yes — proves the math; we replace OBJ with VBO/IBO upload |
| Land visual data | [LandVisualData](../src/FalloutXbox360Utils/Core/Formats/Esm/Models/World/) (BTXT/ATXT/VTXT/VTEX) | Partial — FormIDs present, but no LTEX→texture-path resolver |
| GPU device + mesh upload | [GpuDevice](../src/FalloutXbox360Utils/Core/Formats/Nif/Rendering/), [GpuMeshUploader](../src/FalloutXbox360Utils/Core/Formats/Nif/Rendering/), [GpuSpriteRenderer](../src/FalloutXbox360Utils/Core/Formats/Nif/Rendering/) | Yes for upload + pipelines; **No** for swapchain — currently headless |
| Texture/BSA resolver | [NifTextureResolver](../src/FalloutXbox360Utils/Core/Formats/Nif/Rendering/) | Yes — concurrent cache, BSA-backed lookup, path normalization |
| 2D viewer host | [WorldMapControl.xaml.cs](../src/FalloutXbox360Utils/App/Controls/WorldMapControl.xaml.cs) | Data wiring (worldspace → cell → REFRs) carries over; rendering layer is replaced |

## Scope

### In scope for v3

- Live 3D rendering inside the WinUI 3 GUI (new tab or split-view in existing Worldspace tab)
- Terrain mesh from VHGT with multi-layer ground texture blending
- Placed-object rendering for static NIFs in a cell (REFR, ACHR for non-skinned variants)
- Vertex colors / VCLR-tinted terrain
- Flythrough camera, frustum culling, basic LOD switching
- LRU mesh + texture cache so panning between cells doesn't stall
- Selection: click to pick a REFR, surface its FormID/EditorID in the existing inspector
- Toggle: same control surface can render data from ESM, DMP, or FXS (the data source is already abstracted; the renderer just consumes `PlacedReference` lists)

### Out of scope for v3 (potential v4)

- Skinned NPC/creature rendering inside the viewer (the NPC composition planner exists, but live skinned animation in the world view is a separate effort)
- Time-of-day lighting, weather, water shaders
- Interior cell rendering (deferred — exteriors and terrain first; interiors have no terrain and a different data shape)
- LOD mesh streaming from BSA (use full-res NIFs initially; revisit if perf demands it)
- Editing/modification of placements from the viewer (read-only first)

## Architecture — phased plan

### Phase 0a — Migrate headless renderer from Veldrid to Vortice.Windows (prerequisite)

Veldrid has been frozen since v4.9.0 (Feb 2023) with a public "no longer updating" notice. PR #416 for WinUI 3 `SwapChainPanel` support has been open and unreviewed for 4+ years. v3 commits us to a live GPU surface; we can't build that on a frozen dependency. Vortice.Windows ships monthly (3.8.3 Feb/Mar 2026), targets .NET 10, has a dedicated `Vortice.WinUI` package with `ISwapChainPanelNative`, and gives us a future D3D12 path without changing libraries.

Scope is bounded — Veldrid is confined to 4 wrapper files in [Core/Formats/Nif/Rendering/Gpu/](../src/FalloutXbox360Utils/Core/Formats/Nif/Rendering/Gpu/) (~975 lines total). CLI and GUI code never import Veldrid types directly. Existing tests (`NpcRenderSmokeTests`, `GpuTextureCacheTests`) exercise the GPU path end-to-end and gate the swap.

- Replace `Veldrid.GraphicsDevice` → `ID3D11Device` (Vortice) in `GpuDevice.cs`
- Replace `DeviceBuffer` / `VertexLayoutDescription` → `ID3D11Buffer` / `InputElementDescription` in `GpuMeshUploader.cs`
- Replace `Texture`, `Pipeline`, `CommandList`, `Framebuffer` → Vortice equivalents in `GpuSpriteRenderer.cs` and `GpuTextureCache.cs`
- Shader path: keep GLSL sources, precompile to SPIR-V bytecode at build time (or port to HLSL + DXC). Drop `Veldrid.SPIRV` runtime dependency.
- Validate via NpcRenderSmoke + GpuTextureCache test suites; expect byte-exact or near-exact sprite output

Budget: 2–3 focused days. If migration runs over 5 days, fall back to NeoVeldrid (Silk.NET-backed Veldrid fork, drop-in API preservation) as the second-choice path.

### Phase 0b — Live surface spike (1–2 days, gate)

Goal: prove a Vortice swapchain can render into a WinUI 3 control before committing to the rest.

- Path A (preferred): `SwapChainPanel` → `Vortice.WinUI.ISwapChainPanelNative` → DXGI swapchain (`CreateSwapChainForComposition`, FLIP_SEQUENTIAL, BGRA8, BufferCount≥2) → Vortice D3D11 device. Spinning triangle in a test tab.
- Path B (fallback): keep headless rendering, blit the offscreen color target each frame into a Win2D `CanvasControl`. One CPU copy per frame, small latency hit, but uses a known-good integration.

**Decision point:** if Path A works, the rest of v3 builds on it. If it doesn't within ~2 days, fall back to Path B and move on; do not let this block scope. Path A's risk is materially lower than the original Veldrid-based plan because `Vortice.WinUI` ships the panel interop as a first-class supported package.

Threading/DPI watchpoints:
- `SetSwapChain` must run on the UI thread
- Subscribe to both `SizeChanged` and `CompositionScaleChanged`; backbuffer dimensions must scale by `CompositionScaleX/Y` to stay crisp at non-100% DPI
- `SwapChainPanel` does not support transparency / acrylic backdrop overlays

### Phase 1 — Scene infrastructure

- Camera controller (orbit + flythrough) with WASD/mouse, configurable invert-Y, scroll zoom
- Cell-origin transform stack: world transform = cell origin + REFR local transform
- Frustum culling at cell granularity
- Frame loop driven by `CompositionTarget.Rendering` (or equivalent) with fixed-timestep camera updates

### Phase 2 — Terrain rendering

- Convert `RuntimeTerrainMesh` → `GpuMeshUploader` vertex layout (pos/normal/uv/color)
- Build LTEX FormID → texture path resolver (audit: does `RecordCollection` already expose this? If not, add an index at ESM load)
- Write multi-layer terrain shader:
  - Base layer (BTXT) sampled at world-space UV
  - Up to N additional layers (ATXT) blended by per-vertex opacity (VTXT 17×17 grid per quadrant)
  - Modulate by VCLR
- Stitch cell boundaries (heights at shared edges already match — verify via existing edge consistency tests)

### Phase 3 — Placed-object rendering

- Refactor `NifGeometryExtractor` so the GPU mesh path can be invoked independently of the sprite pipeline
- For each `PlacedReference` in the visible cell set:
  - Resolve model path → cached `GpuMesh`
  - Resolve textures → cached `GpuTexture` (via `NifTextureResolver`)
  - Compose world transform from position/rotation/scale
  - Submit draw call
- Skinned meshes: render in T-pose for v3 (no animation); skip ACHR with skin partition for v4 if too noisy

### Phase 4 — Caching + perf

- `MeshCache<FormId, GpuMesh>` with LRU eviction, size-bounded
- Texture cache already concurrent; verify it survives WinUI thread model
- Pre-upload adjacent cells (8-neighbor) during idle frames
- Optional: GPU instancing for repeated REFRs (lots of rocks, trees) — measure first

### Phase 5 — UX integration

- New tab in main window: "3D Viewer" (or split-pane inside Worldspace tab)
- Sync selection with existing record inspector (click REFR → highlight in tree)
- Picking via depth-buffer readback or per-object ID buffer
- Keyboard shortcuts → register with `KeyboardShortcutsDialog.All`
- Accessibility: 3D viewer is non-interactive for screen readers but mode-toggle and selection list must have `AutomationProperties.Name` (see [CLAUDE.md](../CLAUDE.md) accessibility section)

## Top risks + mitigations

1. **Veldrid → Vortice migration overrun (medium).**
   - Risk: 4 wrapper files (~975 lines) need rewrite. Veldrid abstracts D3D11 immediate-mode contexts, but its `CommandList`/`Pipeline`/`ResourceSet` model and Vortice's raw `ID3D11DeviceContext` differ in shape, especially around state management and shader/pipeline construction.
   - Mitigation: Confine work to [Core/Formats/Nif/Rendering/Gpu/](../src/FalloutXbox360Utils/Core/Formats/Nif/Rendering/Gpu/); existing wrappers already absorb Veldrid behind a `GpuDevice`-shaped surface. `NpcRenderSmokeTests` + `GpuTextureCacheTests` validate parity. 5-day budget; if it runs over, swap to NeoVeldrid (drop-in Veldrid API preserved with Silk.NET-backed bindings).

2. **WinUI 3 ↔ Vortice swapchain binding (low–medium).**
   - Risk: `SwapChainPanel` interop is COM-marshaled and demands UI-thread `SetSwapChain`, plus correct `CompositionScaleChanged` handling for non-100% DPI. Smaller risk than the original Veldrid plan because `Vortice.WinUI` ships the panel binding as a first-class supported package, but no Veldrid+WinUI prior art existed; we're on the well-trodden path now.
   - Mitigation: Phase 0b spike with 2-day cap. Path B (offscreen + Win2D blit) remains the fallback.

3. **LTEX/BTXT/ATXT texture path resolution (low).**
   - Risk: Resolver chain `LTEX FormID → TXST FormID → texture path` does not yet exist.
   - Mitigation: Action queued in "Resolved decisions" — add FormID lookup dictionaries to `RecordCollection` at ESM load. Small, well-understood data; not a v3 schedule risk.

4. **Per-cell mesh upload stalls during pan (medium).**
   - Risk: Naive upload-on-demand for hundreds of REFRs per cell will hitch when panning.
   - Mitigation: Mesh cache + neighbor pre-upload from Phase 4. Measure before optimizing further; instancing only if measurements justify it.

5. **Skinned content visual quality (low–medium).**
   - Risk: T-pose NPCs/creatures look unfinished.
   - Mitigation: Hide skinned actors behind a toggle, default off. Real solution is a v4 effort using the existing NPC/creature composition planners ([npc_composition.md](../../.claude/projects/c--Users-mmc99-source-repos-Xbox360MemoryCarver/memory/npc_composition.md)).

## Resolved decisions (audit 2026-05-29)

### LTEX FormID → texture path resolver — must be built
- [RecordCollection.cs](../src/FalloutXbox360Utils/Core/Formats/Esm/Models/RecordCollection.cs) holds `LandTextures: List<LandscapeTextureRecord>` and `TextureSets: List<TextureSetRecord>` but has no FormID lookup dictionary
- [LandscapeTextureRecord.cs](../src/FalloutXbox360Utils/Core/Formats/Esm/Models/Records/Misc/LandscapeTextureRecord.cs) exposes `IconPath` (ICON), `SmallIconPath` (MICO), `TextureSetFormId` (TNAM)
- [TextureSetRecord.cs](../src/FalloutXbox360Utils/Core/Formats/Esm/Models/Records/Misc/TextureSetRecord.cs) exposes `DiffuseTexture` / `NormalTexture` / `EnvironmentTexture` / `GlowTexture` / `ParallaxTexture` / `EnvironmentMapTexture` (TX00–TX05)
- No chained resolver exists — [NifTextureResolver](../src/FalloutXbox360Utils/Core/Formats/Nif/Rendering/NifTextureResolver.cs) starts from raw paths and is NIF-only
- **Action (early Phase 2):** add `Dictionary<uint, LandscapeTextureRecord>` + `Dictionary<uint, TextureSetRecord>` to `RecordCollection`, write a chainer `LtexFormId → TextureSetFormId → TXST.DiffuseTexture` with `ICON` fallback when `TextureSetFormId` is null/missing

### Cross-build cell origin consistency — verification test, not a blocker
- [CellRecord.cs:20-24](../src/FalloutXbox360Utils/Core/Formats/Esm/Models/Records/World/CellRecord.cs) carries `GridX`/`GridY` from XCLC
- `CellWorldSize = 4096f` is hardcoded in [WorldMapViewportHelper.cs:47](../src/FalloutXbox360Utils/App/Controls/WorldMapViewportHelper.cs#L47) and ~5 other render paths
- Cross-dump infra exists ([CrossDumpComparisonPipeline.cs](../src/FalloutXbox360Utils/Core/Formats/Esm/Export/CrossDumpComparisonPipeline.cs)) but does not currently assert grid-coord parity
- **Action (pre-Phase 1):** write a one-shot test that loads matched exterior cells (by FormID) from the four builds and asserts `(GridX, GridY)` parity. If clean → camera is build-agnostic. If not → add a per-build origin offset to the camera

### Rendering library — Vortice.Windows (locked)
- See Phase 0a for full reasoning. Veldrid is frozen; Vortice ships monthly, has first-class WinUI 3 swap-chain bindings, gives a D3D12 path later without changing libraries
- Silk.NET evaluated and rejected: no `Silk.NET.WinUI` package, no public WinUI 3 + Silk.NET projects, equivalent for headless but loses on the WinUI 3 surface
- NeoVeldrid (Silk.NET-backed Veldrid fork) reserved as fallback if Vortice migration runs over budget

### Water rendering — flat plane at cell water height, in scope
- `CellRecord.WaterHeight` ([CellRecord.cs:47-48](../src/FalloutXbox360Utils/Core/Formats/Esm/Models/Records/World/CellRecord.cs#L47-L48)) + `WorldspaceRecord.DefaultWaterHeight` fallback
- `WorldHeightNormalizer.IsNoWaterSentinel` already filters "no water" cells (recent fix)
- No per-quadrant complexity, no cross-cell stitching (water height is cell-global, unlike terrain)
- WATR DNAM has `ShallowColor` / `DeepColor` / `ReflectionColor` (ABGR uint) if per-worldspace tinting is wanted
- **Action (Phase 2):** emit one flat quad per visible cell at `Z = WaterHeight` when `!IsNoWaterSentinel`. Constant blue (30, 55, 120) as v3 default; defer per-WATR tint to v4

## File touchpoints (anticipated)

Reference list, not exhaustive — to be refined when work starts.

### Phase 0a (Veldrid → Vortice migration)

- **Modified:** [GpuDevice.cs](../src/FalloutXbox360Utils/Core/Formats/Nif/Rendering/Gpu/GpuDevice.cs) — `Veldrid.GraphicsDevice` → `Vortice.Direct3D11.ID3D11Device`
- **Modified:** [GpuMeshUploader.cs](../src/FalloutXbox360Utils/Core/Formats/Nif/Rendering/Gpu/GpuMeshUploader.cs) — `DeviceBuffer` → `ID3D11Buffer`
- **Modified:** [GpuSpriteRenderer.cs](../src/FalloutXbox360Utils/Core/Formats/Nif/Rendering/Gpu/GpuSpriteRenderer.cs) — render pipeline, command lists, framebuffers
- **Modified:** [GpuTextureCache.cs](../src/FalloutXbox360Utils/Core/Formats/Nif/Rendering/Gpu/GpuTextureCache.cs) — texture creation
- **Modified:** `src/FalloutXbox360Utils/FalloutXbox360Utils.csproj` — replace `Veldrid` + `Veldrid.SPIRV` with `Vortice.Direct3D11`, `Vortice.DXGI`, `Vortice.Direct3D.Compilers`, `Vortice.WinUI`
- **Modified:** [Shaders/](../src/FalloutXbox360Utils/Core/Formats/Nif/Rendering/Gpu/Shaders/) — port GLSL to HLSL, or precompile to SPIR-V bytecode and embed
- **Validated by:** `NpcRenderSmokeTests`, `GpuTextureCacheTests`

### Phase 0b–5 (live viewer build-out)

- **New:** `src/FalloutXbox360Utils/App/Controls/WorldView3DControl.xaml(.cs)` — host control wrapping `SwapChainPanel` via `Vortice.WinUI.ISwapChainPanelNative`
- **New:** `src/FalloutXbox360Utils/Core/Formats/Nif/Rendering/Live/` — live-render orchestrator, frame loop, camera, scene graph
- **New:** `src/FalloutXbox360Utils/Core/Formats/Nif/Rendering/TerrainRenderer.cs` — terrain VBO/IBO + multi-layer shader
- **New:** `src/FalloutXbox360Utils/Core/Formats/Nif/Rendering/WaterRenderer.cs` — flat quad per cell at `WaterHeight`, sentinel-aware
- **New:** `src/FalloutXbox360Utils/Core/Formats/Nif/Rendering/MeshCache.cs` — LRU GPU mesh cache
- **Modified:** [GpuDevice.cs](../src/FalloutXbox360Utils/Core/Formats/Nif/Rendering/Gpu/GpuDevice.cs) — add `SwapChainPanel`-backed construction path alongside headless
- **Modified:** [NifGeometryExtractor](../src/FalloutXbox360Utils/Core/Formats/Nif/Rendering/) — factor GPU upload out of sprite-specific call sites
- **Modified:** [WorldMapControl.xaml.cs](../src/FalloutXbox360Utils/App/Controls/WorldMapControl.xaml.cs) — add "3D" toggle, share selection state
- **Modified:** [RecordCollection.cs](../src/FalloutXbox360Utils/Core/Formats/Esm/Models/RecordCollection.cs) — add `FormId → LandscapeTextureRecord` and `FormId → TextureSetRecord` lookups
- **New:** Cross-build cell-origin parity test (one-shot, gates Phase 1)

## Reference reading when starting v3

- 2D viewer architecture: [WorldMapControl.xaml.cs](../src/FalloutXbox360Utils/App/Controls/WorldMapControl.xaml.cs)
- Terrain math + edge stitching: [RuntimeTerrainMesh.cs](../src/FalloutXbox360Utils/Core/Formats/Esm/Models/RuntimeTerrainMesh.cs), [LandHeightmap.cs](../src/FalloutXbox360Utils/Core/Formats/Esm/Models/World/LandHeightmap.cs)
- Headless GPU pipeline (model for live version): [Core/Formats/Nif/Rendering/](../src/FalloutXbox360Utils/Core/Formats/Nif/Rendering/)
- NPC composition (informs v4 skinned-actor extension): memory note `npc_composition.md`
- Accessibility expectations for new GUI controls: [CLAUDE.md](../CLAUDE.md) §Accessibility
