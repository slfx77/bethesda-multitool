# Shared world-water selection

`WorldWaterCatalog` snapshots the selected load order's water route without changing WRLD records. FO3/FNV PNAM Water inheritance uses `WorldspaceInheritanceResolver`; missing, ambiguous and cyclic routes retain their failure status. TES4 parser-derived water retains its Has-Water gate and is labeled `parser-derived`, with no inferred supplying-record identity.

The same catalog now supplies initial masks, 2D maps and PNG exports, 3D water height/appearance/shader context, spatial indexing and grass placement caches. Cell XCLW overrides remain authoritative when finite; sentinel values fall through to the selected world. Typed CELL XCWT wins over resolved world NAM2. The existing FO3/FNV engine-default appearance remains a separate fallback and does not turn a failed inheritance route into a resolved one. Per-cell 2D palettes use the same typed selection.

Lazy caches resolve each physical cell's world from the immutable catalog; they no longer bake grass according to whichever world was last active. New source/load-order views receive new catalogs. Source selection prefers the parsed collection's known game, falling back to file detection only when unknown. Unlinked cells retain the caller's existing scalar fallback.

3D capture telemetry adds `waterSupplyingWorldspaceFormId`, `waterWorldRouteStatus`, `waterWorldRoutePath`, `waterWorldDefaultHeight` and `waterWorldDeclaredTypeFormId`. Stored world reports remain stored values; `world_inheritance.csv` reports the selected route and physical supplier.

CLI23 built with zero warnings/errors. All 363 focused checks passed, including the 14 `WorldWaterCatalogTests` cases for route/fallback separation, CELL overrides, TES4/FNV gating, nonzero load-order rebasing and selected parent override, immutable snapshots, opposite world visitation, cached grass/masks, typed appearance and terrainless duplicate-grid palette exclusion. Aggregate masks and palettes use the same physical terrain-cell admission/order. The run also covered the existing water, spatial-index, cache and load-order checks. [Build](../artifacts/prototype-feedback/build-twentythird-water-script-fast.receipt.json) · [Focused checks](../artifacts/prototype-feedback/focused-twentythird-water-script-corrected.receipt.json).

Windows21 built with zero warnings/errors in 344.249 seconds. Windows19/20 payloads and prior captures are preserved. Actual 2D/export/3D replay and engine comparison: Pending. [Windows build](../artifacts/prototype-feedback/build-windows-twentyfirst-water-script.receipt.json).
