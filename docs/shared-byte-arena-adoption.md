# Shared byte-arena adoption

Mesh and terrain GPU arenas now use Shared `ByteArenaAllocator` and its exact-owner
allocation tokens. The application-local allocator is removed. BMT retains native
backing, stream layout, upload/copy recording, physical residency, pending-copy
holds and completed-use proofs. No shader, geometry interpretation or frame policy
changes with this extraction.

The shared implementation preserves global best-fit packing, coalescing, stable
block indices, aligned long ranges and reuse of oversized blocks. It checks owner
identity and overflow before mutation. Liveness is available even when additional
overlap diagnostics are disabled. Two old tests that explicitly permitted repeated
free-list corruption now require rejection and unchanged accounting; this is a
deliberate safety repair, not relaxed verification.

Terrain cells retain one deferred range owner and transfer it only after queue
acceptance. Terrain release handles mark completion after a successful return, so
failed synchronous release remains retryable. The stopped-queue path and idempotent
late disposal remain supported. Terrain's borrowed global deletion queue stays
owned by the host.

Shared metadata now always tracks exact live allocation identities. This increases
managed bookkeeping over the donor's default diagnostics-off path; no performance
improvement is claimed. Logical emptiness alone never authorizes native release.

Runtime `a4fd3803` pins Shared `7cea6714`. Locked portable and Windows Release
builds pass with 723 and 114 emitted warnings respectively, no changed-file
diagnostics and no analyzer exceptions. Serial isolated compilation used an explicit
eight GiB allowance and retained the four GiB admission default. This closes the
earlier portable ILLink memory gap for this source; the historical failures remain
recorded with their original versions and settings.

All 102 focused cases pass without skips, retaining BMT allocator, mesh and upload
retirement workloads and adding an actual WARP terrain release/retry case. Shared's
23 allocator cases also pass. Receipts `BmtByteArenaPortable01`, `Tests01`, `Gui01`
and `ByteArenaCoreTests02` under canonical Shared `TestOutput/archive-waveform-20260914`
retain source, pin and payload evidence.

The component and exact Shared pin are mirrored into canonical BMT with its index,
staging and pre-existing submodule changes preserved. The entire dirty canonical
checkout is not the tested payload. Shared's later owner-authorized ReSharper and
analyzer checkpoints are not adopted by this application pin.

No GUI was launched for this increment. Terrain cache failure retention, partial
native upload ownership, native-release failure injection, actual world/GUI fidelity
and complete platform/publish acceptance remain separate integration gaps.
