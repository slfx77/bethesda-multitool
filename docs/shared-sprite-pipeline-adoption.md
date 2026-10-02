# Shared sprite pipeline and submission adoption

**BMT sprite ownership adopted — runtime `228c0d16`, diagnostic correction
`8b38b351`, unchanged Shared pin `9a32aa7`.** The sprite renderer now uses Shared pipeline and submission owners.
Its normalized key covers 488 native states while preserving the existing
unknown-blend fallback, shaders, root layout and rendering behavior. Each pending
readback retains its own resources; completing a later sprite cannot dispose an
earlier unread result. Disposal drains unfinished work before staged release.

NIF file, directory and archive callers keep GPU creation, rendering and disposal
on one thread, including suspended reads and cancellation. CPU batches retain
their parallel path. All **61 focused tests pass**, no skips, including
native pixels, partial failures, out-of-order completion and thread rejection.
Locked portable/Windows Release pass with 710/88 warnings.
The first build's eleven new diagnostics were corrected before Windows validation;
the test-only rebuild also confirms the nested-loop formatting correction.
Portable ILLink analysis still runs out of memory. Receipts: `BmtSpritePipeline*`.

All seven source/test paths are synchronized into canonical BMT with its existing
staging preserved. AWE remains `7b42197` / `7825bd3`. No GUI/world-corpus or
fault-injected COM cleanup claim. Unpublished constructor-cleanup failures and
outer legacy-owner retry remain open; NIF's existing unavailable `--gpu` fallback
behavior was preserved and needs a separate compatibility decision.

**Next:** dynamic world pipeline/root ownership and production descriptor,
submission and residency-cache adoption; then remaining AWE fidelity/lifecycle
work. All six milestones and TB1-TB6 remain active. No new checkout, backup,
dependency, pin change or publication. All checks terminal; component claim released.

## Implementation and compatibility

`GpuSpritePipelineKey` identifies the actual native blend, cull and shader state.
Nonblended modes share pipeline state but keep their different shader inputs.
Unknown source blend bytes still map to SourceAlpha. The capacity bounds pipeline
identities, not concurrent renders or total GPU memory.

`GpuSpriteRenderer12` owns one `ShaderPipelineResources` family. Per-submission
`SubmissionResourceRetirement` retains a staged `RetiredResourceDisposal` bundle
before allocation. Queue execution is classified before the fence association;
uncertain execution or failed signaling retains ownership until teardown proves
queue completion or terminal device removal. Cleanup releases command lists before
their resources/allocators and pending bundles before persistent pipeline resources.
Failed release actions stay owned for retry. No second native playback/render clock
or queue is introduced.

Single-file NIF rendering reads first, then creates and disposes its GPU owner
without another await. Directory/archive GPU batches execute on a synchronous
worker; asynchronous input reads block that worker without moving native ownership.
CPU processing retains its asynchronous parallel route. Output naming, JSON,
render settings, per-item failures and cancellation reporting remain compatible.
The existing NPC submit/complete overlap is unchanged.

## Evidence and remaining limits

Three portable tests exhaustively compare normalized state with the existing
native blend mapper. Three native renderer tests check nonempty red/blue pixels,
blend fallback equality, independent reverse-order readback, abandoned readbacks,
repeat disposal and rejection of foreign-thread calls before state mutation.
Three native caller tests exercise actual file/directory/BSA routes, decoded PNG
and index contents, a suspended read completed by a different thread, real output
failure, injected input failure, cancellation and successful work afterward.
Existing alpha-composition, Starfield sprite routing and classic-skin shader checks
are included. This is synthetic component evidence, not retail NPC/world parity.

Tests use the existing native device, renderer and Shared pin. Builds preserve
isolated compilation, one node, the default 4 GiB admission check and an explicit
8 GiB compiler heap cap. Payload hashes and exact source/pin accompany receipts.
Canonical synchronization checks content and physical index preservation; it does
not make canonical main's old ref equivalent to the entire implementation checkout.

`8b38b351` changes only comments, a narrowly justified analyzer pragma and nested
test-loop braces/indentation. The 61-case native test run uses runtime/test payloads
from `228c0d16`; the corrected test-only build reuses that application runtime.
Windows compiles the corrected application source. C# cannot emit XML documentation
for local functions, so those use descriptive ordinary comments. S3877 is suppressed
only for the throw that prevents unsafe resource release without retirement proof.

Ordinary constructor failures now clean up allocated resources. If cleanup itself
fails before the object is published, there is still no caller-visible retry owner.
The same outer-owner limitation remains in legacy callers when their final Dispose
throws. This increment does not claim fault-injected COM-release recovery there.
The CLI backend selector avoids explicitly disposing a device without retirement
proof after failed renderer construction; that is not a durable quarantine owner.
