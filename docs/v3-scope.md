# v3 Scope: Real-Time 3D Cell/Worldspace Viewer

**Status:** Planned for v3. Not started — do not begin implementation as part of v2 work.
**Owner:** slfx77
**Last updated:** 2026-05-14

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

### Phase 0 — Live surface spike (1–2 days, gate)

Goal: prove a Veldrid-backed swapchain can render into a WinUI 3 control before committing to the rest.

- Path A (preferred): `SwapChainPanel` → `ISwapChainPanelNative` → DXGI swapchain → Veldrid D3D11 backend. Spinning triangle in a test tab.
- Path B (fallback): keep `GpuDevice` headless, blit the offscreen color target each frame into a Win2D `CanvasControl`. Adds one copy per frame and a small latency hit but eliminates interop risk.

**Decision point:** if Path A works, the rest of v3 builds on it. If it doesn't within ~2 days, fall back to Path B and move on; do not let this block scope.

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

1. **WinUI 3 ↔ Veldrid swapchain binding (high).**
   - Risk: `SwapChainPanel` interop in C# requires native COM marshaling; not a common path for Veldrid.
   - Mitigation: Phase 0 spike with hard 2-day cap. Path B (offscreen + blit into Win2D) is a viable fallback that uses infrastructure we already understand.

2. **LTEX/BTXT/ATXT texture path resolution (medium).**
   - Risk: We have FormIDs but no confirmed resolver path to actual texture files. Coverage gap could derail terrain visuals.
   - Mitigation: Audit `RecordCollection` early in Phase 2. If missing, build a one-shot index at ESM load time — small addition, well-understood data.

3. **Per-cell mesh upload stalls during pan (medium).**
   - Risk: Naive upload-on-demand for hundreds of REFRs per cell will hitch when panning.
   - Mitigation: Mesh cache + neighbor pre-upload from Phase 4. Measure before optimizing further; instancing only if measurements justify it.

4. **Skinned content visual quality (low–medium).**
   - Risk: T-pose NPCs/creatures look unfinished.
   - Mitigation: Hide skinned actors behind a toggle, default off. Real solution is a v4 effort using the existing NPC/creature composition planners ([npc_composition.md](../../.claude/projects/c--Users-mmc99-source-repos-Xbox360MemoryCarver/memory/npc_composition.md)).

## Open questions to resolve before Phase 1

- Does `RecordCollection` already expose LTEX FormID → path? If not, where does the index get built?
- Are exterior cell origins in world units consistent across the four full builds (360 final, July 2010, Aug 2010, PC final)? If shifts exist between builds, the camera needs a build-aware world offset.
- What's the licensing/binding story for Veldrid + `SwapChainPanel` in WinAppSDK 1.x? Check for prior art in WinUI Composition samples before the spike.
- Do we render water? Many cells reference WATR records; for v3 we can render the surface as a flat plane at the cell's water height (already parsed) and defer real water shading.

## File touchpoints (anticipated)

Reference list, not exhaustive — to be refined when work starts.

- **New:** `src/FalloutXbox360Utils/App/Controls/WorldView3DControl.xaml(.cs)` — host control with swapchain or blit target
- **New:** `src/FalloutXbox360Utils/Core/Formats/Nif/Rendering/Live/` — live-render orchestrator, frame loop, camera, scene graph
- **New:** `src/FalloutXbox360Utils/Core/Formats/Nif/Rendering/TerrainRenderer.cs` — terrain VBO/IBO + multi-layer shader
- **New:** `src/FalloutXbox360Utils/Core/Formats/Nif/Rendering/MeshCache.cs` — LRU GPU mesh cache
- **Modified:** [GpuDevice](../src/FalloutXbox360Utils/Core/Formats/Nif/Rendering/) — add swapchain construction path alongside headless
- **Modified:** [NifGeometryExtractor](../src/FalloutXbox360Utils/Core/Formats/Nif/Rendering/) — factor GPU upload out of sprite-specific call sites
- **Modified:** [WorldMapControl.xaml.cs](../src/FalloutXbox360Utils/App/Controls/WorldMapControl.xaml.cs) — add "3D" toggle, share selection state
- **Modified:** `RecordCollection` (or equivalent) — LTEX FormID → texture path index, if not already present

## Reference reading when starting v3

- 2D viewer architecture: [WorldMapControl.xaml.cs](../src/FalloutXbox360Utils/App/Controls/WorldMapControl.xaml.cs)
- Terrain math + edge stitching: [RuntimeTerrainMesh.cs](../src/FalloutXbox360Utils/Core/Formats/Esm/Models/RuntimeTerrainMesh.cs), [LandHeightmap.cs](../src/FalloutXbox360Utils/Core/Formats/Esm/Models/World/LandHeightmap.cs)
- Headless GPU pipeline (model for live version): [Core/Formats/Nif/Rendering/](../src/FalloutXbox360Utils/Core/Formats/Nif/Rendering/)
- NPC composition (informs v4 skinned-actor extension): memory note `npc_composition.md`
- Accessibility expectations for new GUI controls: [CLAUDE.md](../CLAUDE.md) §Accessibility
