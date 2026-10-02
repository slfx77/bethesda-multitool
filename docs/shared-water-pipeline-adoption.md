# Shared water pipeline adoption

**BMT water pipeline ownership adopted — source `8d47e6d0`, unchanged Shared
pin `20a96ce`.** Eighteen base and six modern pipelines use independent Shared
dependent-root families. The portable application factory retains BMT's game
shaders, macros, textures, descriptors and frame policy. Optional partial
initialization and runtime release retain failed cleanup for creating-thread retry.

**Compatibility repair:** pinned Vortice graphics descriptions are mutable classes.
FO76 dual-source and Skyrim opaque variants previously aliased the ordinary
description, leaking blend changes into later variants and modern templates.
Independent copies now isolate these states; modern creation preserves both input
templates. This is a rendering correction, not byte-identical pre-extraction output.

All **120 focused cases pass**, no skips, including six native raster captures
and three compute readbacks from the real factory. Checks cover ordinary alpha,
reversed-Z ties/occlusion, no water depth writes, FO76 transmission, Skyrim opaque
fallback, FNV normal generation, modern coverage and independent family recreation.
Locked portable/Windows Release pass with 697/88 warnings and no analyzer
crashes. Portable compilation uses the invocation-only two-processor bound;
analyzers, isolated compilation, eight GiB compiler cap and four GiB admission stay
enabled. Receipts: `BmtWaterPipeline*`; BMT `docs/shared-water-pipeline-adoption.md`.

All fourteen source/test files are synchronized into canonical Bethesda with its
index preserved. No Shared runtime, dependency or pin change. GPU retirement
remains caller-owned. Retail-world/GUI and injected native-release-failure checks
remain open, as do unpublished-constructor and outer-owner retry gaps.

**Next:** dynamic reference families, then world descriptor/submission/residency
adoption and remaining AWE migration work. All six milestones and TB1-TB6 remain
active. Checks are terminal; water claim released. AWE stays `7b42197` / `7825bd3`.
No new checkout, backup or publication.

## Implemented boundary

`WaterRenderer12` delegates its eighteen base pipelines to Shared's existing
dependent-root owner. `WaterPipelineFactory12` builds the actual application
permutations in the portable target: fifteen raster states and three noise
compute states. The optional modern path retains a separate family of two raster
and four compute states against the same world root. Game interpretation, shader
macros, render routing, textures, descriptors and frame policy remain BMT-owned.

The optional managed owner is retained before native initialization. Its bounded
cleanup callbacks are registered before allocation; partially initialized outputs,
descriptor slots and pipeline handles remain reachable. Runtime disposal checks
the creating thread before changing stopped state and retains failed actions for
retry. Textures and descriptor returns still use the existing deferred deletion
queue. Callers must prove GPU retirement before disposal; Shared does not infer it.

## Compatibility repair

The pinned Vortice `GraphicsPipelineStateDescription` is a reference type. Existing
assignments such as `var fallout76OpticsPsoDesc = psoDesc` alias the same object.
FO76 dual-source and Skyrim opaque blend mutations therefore contaminated later
ordinary variants, and both saved modern templates referenced that mutable object.
The extraction repairs this independently of ownership: copied descriptions retain
every field and independent mutable arrays/layouts. Ordinary variants keep
SourceAlpha/InverseSourceAlpha RGB and maximum alpha; FO76 alone keeps dual-source
transmission; Skyrim's snapshot variant alone disables blending. Modern creation
copies its inputs without changing the retained templates.

This is an intentional correction to inherited rendering behavior, not a claim of
byte-identical pre-extraction water output. No shader algorithm or approximation
claim changes. Retail game comparisons remain an acceptance gap.

## Focused evidence scope

The native fixture uses the production factory, actual world root, real structured
water packets and WARP with four-sample scene targets. Independent expected output
checks cover ordinary blend/alpha, reversed-Z ties and occlusion, overlapping water
without depth writes, FO76 transmission, Skyrim opaque fallback, FNV normal compute,
modern body/coverage compute and modern-family recreation while the base/root live.
These checks do not substitute for retail-world, UI or injected COM-release-failure
coverage.

Cleanup failure during unpublished base-renderer construction still cannot expose
a retry owner; outer legacy-owner disposal retries remain separate work. Dynamic
reference families and production world descriptor/submission/residency adoption
follow. The six-milestone program and TB1-TB6 remain incomplete.
