# Shared world submission adoption

Implemented in `6c8ae935`, exact Shared pin `d455164`.

Portable Release passes (697 existing warnings, no changed-file diagnostics).
The focused suite covers 114 distinct cases: Tests02 passes 113 with one local
harness skip; Harness01 passes all seven cases of that class, closing the skip
with six repeated checks. Both execute the same committed source. Tests01's
water failures required the existing replay identity and packed shaders; no
source or assertions changed. Existing fixtures are hard-linked, not copied.

Combined acceptance now passes at `aefaade4`, unchanged Shared pin `d455164`:
locked portable/Windows Release (697/88 existing warnings, no changed-file
diagnostics) and **130 affected cases**, with no skips. The combined run includes
the subsequent descriptor/deletion changes and repeats the submission paths.
Receipts `BmtWorldOwnershipPortable02`, `Tests01` and `Gui01` identify the same
committed source; Gui01 includes portable/Windows payload hashes and versions.
Earlier Windows/admission failures remain recorded. Canonical source and staging
preservation are recorded in the component sync receipts. This is build and
focused native evidence, not a new GUI/retail-world interaction claim.

## Implementation

`GpuCommandRecorder12` delegates recorded, fenced, unfenced and failed-release
ownership to the existing Core `SubmissionResourceRetirement`. The exact Shared
pin stays `d455164`. BMT continues to own native commands, allocator rotation,
actual fence waits, profiling and device-terminal policy.

- Possible execution is marked before the native Execute boundary. Notification
  follows the Signal outcome, and successful fence publication still precedes
  water participant callbacks. Uncertain execution preserves the conservative
  legacy submitted callback and recorder poisoning.
- A pre-registered conditional holder transfers the optional capture lifetime
  only if execution becomes uncertain. Successful and definitely unsubmitted
  outcomes leave that resource with its caller. Failed release retains ownership
  for retry; no post-Execute allocation is required to take the optional owner.
- An idle signal covers previous submissions but leaves an open recording's
  resources and participants untouched until its own submit or abort. This
  repairs the former idle path, which could release an unsubmitted list's inputs.
- Shared participant identity handling replaces the application list scan.
  Notifications are isolated, callbacks cannot reenter native recording, and
  creating-thread checks precede native mutation or ownership transfer.
- The bounded owner admits 65,536 pending resource registrations and 4,096
  participants per recording. These are explicit new limits, not historical
  behavior claims; rejected transfers remain caller-owned. Upload bundles count
  as one registration. Existing water/HDR/mesh call sites preserve rejection
  ownership; shadow capture needed a guarded readback transfer.
- Opaque packet replacement now queues its previous owner before clearing the
  field and recording the replacement. Failed registration retains the previous
  owner; failed construction keeps the existing transient draw fallback.
- Ordinary fence values are reserved before possible execution and exclude the
  device-removal sentinel. Shutdown publishes its release plan only after all
  actions are registered and retains failed child releases before native objects.

The participant interface, native outcome and conditional owner each live in a
matching file. No application game interpretation or shader policy moved to Core.

## Evidence and limits

The focused native recorder cases
exercise actual WARP submit/abort/fence behavior, duplicate participants, managed
callback/release failures, optional lifetime ownership, foreign-thread rejection,
real failure before Execute and idle during an open recording. Existing HDR,
water/copy/readback and packet checks cover actual consumers.

Unknown native Execute/Signal failure is not injected into a live queue; existing
Shared state tests and the conditional-owner/legacy-outcome tests cover that
classification without pretending to prove a native driver failure. GUI and
retail-world interaction, partial native constructor cleanup, unpublished raw
readback cleanup failure and outer-owner retry gaps remain explicit. Descriptor,
upload/deletion queue and residency adoption are separate remaining work.
