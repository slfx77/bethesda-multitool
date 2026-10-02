# Shared HDR display pipeline adoption

**BMT HDR pipeline ownership adopted — runtime `9be44d0f`, tested source
`73bf2b6f`, unchanged Shared pin `9a32aa7`.** The actual display pass used by live
surfaces and headless targets delegates its root and ten fixed pipelines to
Shared. Game shaders, declarations, render states, HDR/bloom calculations and
caller-owned transitions remain. Individual HDR cleanup retains failed releases
for retry before releasing the shared pipeline family.

All **153 focused cases pass**, no skips. The same native fixture before and
after adoption produces **192 byte-identical display frames** across eight
presets, odd/even dimensions and four-sample targets. Each run also compares 192
abandoned frames with a control sequence, wraps descriptor/history rings and
checks explicit history resets. Locked portable/Windows Release pass with
701/88 warnings. Portable analysis remains incomplete because an analyzer runs out of memory.
Receipts `BmtHdrPipeline*` identify actual old/new runtime and test payloads.
The baseline deliberately compiles only the new tests against the retained
`a02bde1f` runtime; it does not claim the new runtime was already tested.

The three changed files are synchronized into canonical BMT with its unrelated
staging preserved. AWE remains `7b42197` / `7825bd3`. No GUI launch or world-corpus
parity is claimed. Failed-constructor cleanup and outer legacy-owner retry gaps
remain; this increment does not prove fault-injected COM release recovery through
every caller. All checks are terminal; no file claim remains.

**Next:** finish sprite caller thread ownership, dynamic world pipeline caches,
descriptor/submission/cache adoption, then remaining AWE fidelity and lifecycle
work. All six milestones and TB1-TB6 remain active. No new checkout, backup,
dependency, pin change or publication was made.

## Ownership and compatibility

`GpuTonemapPass12` retains `ShaderPipelineResources(device, 10)` before native
initialization. Its root and pipeline fields are borrowed handles; only Shared
releases them. Constructor rollback tracks the complete family before the other HDR resources.
Runtime disposal stops further recording, individually retains HDR resource
release actions, then disposes the pipeline family in the next stage. Successful
releases are not repeated when another release fails.

The creating thread must also dispose the shared owner. Live surfaces use their
UI/render thread; synchronous headless capture uses its capture thread. Callers
still prove GPU retirement. This class adds no queue, fence, timer or device.
Sprite async callers and the world's shared roots/dynamic caches need their own
adaptation; the fixed HDR owner does not complete those components.

## Evidence limits

`GpuTonemapPipelineIntegrationTests` covers clamp, ACES, FO3/FNV, Skyrim, TES4,
SDR bloom, cinematic and the existing modern stand-in. It uses uniform synthetic
HDR input and tests current behavior, not newly established engine fidelity.
Per run: 384 submitted frames, 192 discarded recordings, 192 compared readbacks.
The before/after hashes are machine-specific evidence, not portable golden files.

`BmtHdrPipelineBaselineBuild01` initially reported one xUnit2012 diagnostic;
`73bf2b6f` changes only that assertion spelling. Isolated compilation retains
the default 4 GiB admission check and uses an explicit 8 GiB compiler heap cap.
Component synchronization checks exact content and preserves canonical staging;
canonical main's older ref is not equivalent to the full verified checkout.

## Next sprite boundary

Move single-file GPU creation after its asynchronous read. Keep each GPU
directory/archive batch's create, serial rendering and final disposal inside one
synchronous worker action; serial async callbacks do not guarantee one thread.
The existing NPC submit/complete pipeline already stays synchronous. Preserve
the CPU batch route and cancellation/output behavior. Canonicalize pipeline keys
to the actual native blend state before choosing a fixed ownership capacity;
unknown source blend bytes currently resolve to SourceAlpha and must retain that
behavior. Constructor rollback and failure-safe caller teardown are part of that
adoption, with existing sprite pixel tests and suspended-read/batch failure cases.
