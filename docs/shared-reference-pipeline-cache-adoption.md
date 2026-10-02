# Shared dynamic reference pipeline adoption

**BMT dynamic reference pipelines adopted — runtime `5f7fb657`, test correction
`197ae5bb`, exact Shared pin `d455164`.** Shared owns caller-keyed native
pipeline caches through independent dependent-root families. BMT's three shader
routes retain separate ordinary/depth-writing caches, raw blend keys, shader
fallback and the existing render recipe. Failed cleanup remains reachable through
route, factory and renderer disposal; packet transfer clears ownership only after
success. Fixed-handle aliases retire once.

All **124 affected BMT cases and 36 Shared native cases pass**, without skips.
The BMT native fixture checks actual four-sample raster output for blending,
maximum alpha, depth writes/tests, winding and decals, plus independent route
replacement and embedded shader pipeline creation. Locked portable/Windows
Release pass with 732/114 warnings. Nine new fixture initializer
warnings are fixed; the corrected test-only build has 611 existing warnings
and reuses the unchanged application payload. No analyzer crash. Portable builds
retain the invocation-only two-processor bound, analyzers, isolated compilation,
eight GiB compiler cap and four GiB admission. Receipts `BmtDynamicPipeline*`
and `SharedDynamicPipeline*` identify source, pin and actual payloads.

All seventeen component source/test paths, the correction and exact pin are
synchronized into canonical BMT with its index unchanged. No GUI/retail-world or
injected COM-release-failure claim. GPU retirement remains caller-owned;
unpublished-constructor and outer legacy-owner retry gaps remain. See BMT
`docs/shared-reference-pipeline-cache-adoption.md`.

**Next:** fixed/optional reference families, then world descriptor/submission/
residency adoption and remaining AWE work. Checks terminal; dynamic-route claim
released. AWE remains `7b42197` / `7825bd3`; all six milestones/TB1-TB6 remain
active. Other pins, peer edits, storage and publication are unchanged.

## Implementation

Shared `11ca85e` adds `ShaderPipelineCache<TKey>` over independent, one-slot
`ShaderPipelineResources` families; `d455164` completes test cancellation wiring.
Keys and shader interpretation remain caller-owned. Cache hits preserve handle
identity and avoid factory invocation; misses retain their family before native
allocation. Failed creation leaves existing hits intact. A failed release stays
owned for explicit retry and blocks further allocation where necessary.

BMT `5f7fb657` adopts exact Shared `d455164` in all three reference blend routes:
ordinary references, direct grass and instanced grass. Each retains separate
ordinary and depth-writing caches. Raw source/destination blend bytes, culling,
decals, depth-test flags, shader fallback and existing growth behavior are
preserved. The extracted portable recipe is also used by fixed pipelines.

Factory and renderer disposal now retain failed actions for retry. Fixed-handle
aliases are released once; an opaque submission packet remains reachable until
queue transfer succeeds. Creating-thread checks precede stopped-state changes.
The application must still prove GPU retirement before replacing or releasing
pipelines. This change does not add a fence policy or alter game shaders.
