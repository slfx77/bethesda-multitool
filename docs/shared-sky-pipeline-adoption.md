# Shared sky pipeline adoption

**BMT sky pipeline ownership adopted — source `c716ac2a`, unchanged Shared pin
`20a96ce`.** Sky geometry and celestial billboards now retain independent Shared
pipeline families against the existing world root. BMT's portable
`SkyPipelineFactory12` constructs the actual three geometry and two billboard
states. Game shaders, weather, draw order, vertex layouts, blending, MSAA and
clipping stay unchanged; the superseded private creators are removed.

All **35 focused cases pass**, without skips, including seven native sky
captures through the production factory. These check color and alpha blending,
geometry clipping versus unclipped billboards, and geometry-family recreation
while the billboard family and root remain usable. Locked portable/Windows Release
pass with 697/88 warnings. No portable analyzer crash is reported in the successful retry.

The first portable build failed in Roslyn's analyzer driver with an out-of-memory
exception. The successful retry limits its runtime to two logical processors;
analyzers, isolated compilation, the eight GiB compiler heap cap and four GiB
admission remain enabled. This is an invocation override, not a build-default
change. The first Windows build's two new style diagnostics were fixed before
final checks. Receipts: `BmtSkyPipeline*`; component record:
BMT `docs/shared-sky-pipeline-adoption.md`.

All eight source/test paths and the factory correction are synchronized into
canonical Bethesda with staging preserved. No new Shared runtime, dependency or
pin change; AWE remains `7b42197` / `7825bd3`. Callers still prove GPU retirement.
No new GUI/retail-world or injected native-release-failure evidence is claimed;
cleanup failures during unpublished construction and outer-owner retry gaps remain.

**Next:** water and dynamic reference families, then descriptor/submission/residency
production adoption and remaining AWE fidelity/lifecycle work. All six milestones
and TB1-TB6 remain active. Checks are terminal; the sky claim is released. No new
checkout, backup or publication.

## Implemented boundary

`SkyPipelineFactory12` contains the actual production pipeline creation code and is compiled outside
the `WINDOWS_GUI` gate. Native tests can call it directly, without a second copy of the descriptions.
It compiles the established `sky_geo` and `sky_billboard` vertex/pixel pairs through
`GpuShaderCompiler12`, which already delegates compilation to Shared.

- `CreateGeometryPipelines(gpu, pipelineResources)` creates gradient, stars and clouds in slots 0–2.
- `CreateBillboardPipelines(gpu, pipelineResources)` creates additive and alpha billboards in slots 0–1.
- `SkyGeometryRenderer12` retains a capacity-3 dependent family before compilation or native allocation.
  It publishes borrowed handles only after all pipelines and the fallback dome are available.
- `SkyBillboardRenderer12` retains a separate capacity-2 dependent family before creating its pair.
- Both families retain the exact world root through `GpuRootSignature12.CreatePipelineResources`.
  Renderer fields borrow handles; superseded private builders and individual PSO disposal are removed.

## Preserved rendering declarations

All five pipelines retain the scene sample count, `GpuSceneFormats.SceneColor`, `D32_Float`,
disabled depth testing/writes and stencil, no culling, counterclockwise front faces, full sample mask,
and triangle primitive topology type. Each established draw path retains its own list/strip commands.

| Family | RGB source/destination | Alpha source/destination | Depth clipping |
|---|---|---|---|
| Geometry gradient | Blending disabled | Blending disabled | Enabled |
| Geometry stars | Source alpha / one | One / one | Enabled |
| Geometry clouds | Source alpha / inverse source alpha | One / inverse source alpha | Enabled |
| Billboard additive | Source alpha / one | One / inverse source alpha | Disabled |
| Billboard alpha | Source alpha / inverse source alpha | One / inverse source alpha | Disabled |

Enabled blend operations remain additive with all color channels writable. Geometry preserves its
24-byte vertex layout: float3 position, float2 UV and RGBA8 vertex color. Billboards preserve the empty
input layout and vertex-ID expansion. Weather controls, cloud clocks, shader calculations, moon/sun
parameters and FO3/FNV sun/stars/clouds/glare ordering remain application-owned and unchanged.

## Lifetime and failure behavior

Construction, native rendering and disposal remain on the creating managed thread. Disposal verifies
that thread before setting the stopped marker. It invokes the shared owner even after an earlier
disposal attempt failed, allowing failed native releases to be retried. Borrowed handles are never
disposed separately. Geometry clears its managed layers after the pipeline family releases.

The caller must prove GPU completion or device removal before disposal; neither renderer adds a fence
or establishes retirement itself. Normal world teardown waits for GPU idle before disposing sky and
then the shared root. This increment does not repair unrelated outer teardown paths.

Constructor failures dispose the retained family, including earlier successful pipeline allocations.
If that cleanup itself throws, the unpublished renderer is not available for durable external retry;
this limitation remains explicit. No global failed-constructor holder or background cleanup was added.

## Built payloads

`BmtSkyPipelineGui02/payloads.json` records SHA-256, size and product version for
both application targets, the portable tests, Shared Core and Shared shader
assemblies. All application/test versions identify `c716ac2a`; Shared assemblies
identify the unchanged `20a96ce` pin. No build-output copy was made. The successful
portable run's `invocation-override.json` records the two-processor override.

The production native captures run through WARP with four-sample scene targets.
They establish representative raster and ownership behavior, not interactive
world appearance or full weather/streaming parity.
