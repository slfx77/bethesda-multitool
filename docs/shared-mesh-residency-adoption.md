# Shared mesh residency in the native world renderer

Implementation `618ece87`; exact Shared pin `a8617ef`. This is the production
BMT integration following `docs/shared-mesh-resource-retirement.md`.

Shared owns reservation, prepared/resident/retiring state, exact resource pins
and completed-release accounting. BMT keeps source and material interpretation,
decode scheduling, collision recovery, LRU recency, arena blocks and actual GPU
fence policy. DEFAULT candidates receive exact submission outcomes; an uncertain
submission never publishes a resident. UPLOAD initialization publishes synchronously.

Batch snapshots and animation grace entries retain independent pins. Every
recording retains the unique meshes before particle/skinner updates or draws;
shadow and mirror replays reject nonempty captures from another recording.
Logical eviction stops new draws while old pins retain submitted uses. Native
resources and their attributed charges return only after actual release succeeds.

Mesh admission is bounded at four GiB and 102,400 entries by default, with explicit
constructor limits. Pending, pinned and retiring entries consume that budget.
LRU removal only requests retirement; it never creates immediate byte headroom.
Physical arena blocks, staging and textures have separate accounting and are not
claimed to be governed by this attributed mesh limit.

A producer repair captures arena ranges before fallible backing/copy work. Upload
rollback owns staging and copy holds before commands are recorded, including
exceptions before Upload returns. The staging ring allocates its release handle
before taking a FIFO reservation. Existing material/shader and geometry view
behavior is preserved.

Recorder and delayed-eviction callbacks transfer GPU-retired entries to a
cache-owned release collection. They do not block frame advancement on stale
batch/skinner pins. Frame maintenance retries releases while retaining failures;
shutdown drains transfers and actual releases before the residency owner and arena.
A callback arriving after successful cache shutdown does not repopulate cleanup.

Locked portable and Windows Release pass. Runtime `618ece87`, corrected tests
`77d3473b`, and exact pin `a8617ef` pass all **191 affected cases**, without skips,
including actual WARP geometry/upload, texture ownership and recorder checks.
The Windows build emits 88 existing warnings and no changed-file diagnostics.
Portable02 emits 698 warnings, including one new test path-prefix warning; the
test-only Portable04 build clears it with 611 existing test-project warnings and
retains the byte-identical application DLL. No remaining changed-file diagnostic
was found. Native failure injection and retail-world performance are not established.

Receipts in Shared `TestOutput/archive-waveform-20260914`:

- `BmtMeshResidencyPortable02` / `Portable04`: application build and test-only repair.
- `BmtMeshResidencyTests03`: 191/191 passing, five actual portable payload hashes.
- `BmtMeshResidencyGui02`: Windows build and seven portable/Windows payload hashes.
- `BmtMeshResidencyCanonicalSync01` through `04`: 28 source/test paths and pin,
  with the canonical index and existing staged diff unchanged.

Earlier Gui01/Portable01 at `97d5a2bc` passed compilation; Tests01 was 184/188.
Tests02 was 190/191. Those failures were source assertions that still expected old
locations/signatures. Independent review found the frame-loop retirement failure
and its fix above; the final behavioral regressions include retained pins, native
release retry, rejected transfer and a first callback after cache shutdown.
Checks are terminal. Builds used isolated compilation, one node, eight GiB compiler
cap and four GiB admission; portable compilation used the recorded invocation-only
two-processor bound. Defaults were not changed.

Shared `a8617ef` passes locked Core Release and 25 focused cases without skips
(including eleven Retain cases). Its initial build caught a test callback parameter
shadowing a discard; the compile error was repaired before the passing run.

Main BMT files are mirrored from the committed implementation with the canonical
index/staged changes preserved. No public push, release, backup generation or
new checkout is part of this component. GUI/retail-world behavior, world streaming
pressure/performance, native failure injection, physical block budget admission
and the full six-milestone/TB1-TB6 program remain open.
