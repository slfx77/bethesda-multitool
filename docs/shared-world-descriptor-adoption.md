# Shared world descriptor adoption

Implementation: `931dee35`, exact Shared pin `d455164`.
Combined acceptance at `aefaade4`: locked portable/Windows Release pass,
with 697/88 existing warnings and no changed-file diagnostics. All **130 focused
cases pass**, no skips, including the new descriptor and deferred-release cases
and existing actual WARP/hardware submission, HDR and water readbacks. This does
not establish GUI or retail-world parity.

`GpuDescriptorHeapAllocator12` now uses Shared `ShaderDescriptorHeap` and
`ShaderDescriptorRange` for one native heap and its fixed persistent/frame
partitions. Native creation, range identity, handle bounds and heap retirement
are shared. BMT retains LIFO persistent slot reuse, frame bump policy, statistics
and caller-proven fence timing. This is heap/partition ownership adoption, not
complete allocation-policy or residency consolidation.

The persistent prefix still begins at zero; frame regions follow consecutively.
World, native viewer and headless profiler capacities and indices stay intact.
Zero-length frame reservations retain their prior one-past-end marker behavior.
Range owners retire before the native heap, and failed runtime cleanup remains
retained. Diagnostic count reads remain snapshots; native borrowing, mutation
and retirement require the creating thread.

Explicit repairs: reject never-allocated persistent returns, reject unsigned
capacity overflow, and reserve free-list/set capacity before changing ownership.
Thread misuse now fails before mutation instead of logging and continuing.
Stopped owners reject native access. Shared's descriptor capacity limits apply;
all existing application constructor budgets fit.

The unused CPU descriptor heap in WaterRenderer12 and its sole allocator class
were removed. Water descriptors already use the shared shader-visible heap.

Focused checks added cover partition addresses, exhaustion without mutation,
LIFO reuse, invalid returns, sampler increments, device ownership, thread access
and disposal. The existing water fixture now uses the production allocator for
persistent raster inputs and a frame-local compute UAV table, preserving its
expected readback values. The preceding submission suite will run with this
change to cover the affected native callers and its pending Windows integration.

All seven descriptor paths and three subsequent queue/test paths are synchronized
with canonical staging unchanged; receipts `BmtWorldDescriptorCanonicalSync01`
and `BmtWorldDeletionCanonicalSync01`. Initial builds timed out before compilation
at the unchanged four GiB admission gate. After memory recovered, combined
`BmtWorldOwnershipPortable02`, `Tests01` and `Gui01` passed against `aefaade4`.
Gui01 records hashes and source versions for the actual portable/Windows payloads.

The deferred queue now transfers into Shared cleanup before removing its pending
entry. Failed cleanup remains counted and retryable; independent siblings continue.
A monotonic internal frame ordinal preserves delays across the public uint counter
wrap. Creating-thread and reentry checks precede mutation. Late enqueues after
caller-proven shutdown retain their existing synchronous release behavior.
Six new managed cases cover delay, failure/retry, nested enqueue, thread rejection,
clock wrap and invalid admission. Frame counts require the recorder's slot-fence
proof; the queue creates no native completion guarantee.

Remaining evidence includes GUI/world interaction, cube/depth alias parity and
native release-failure injection. Borrowed integer slot identities still require caller discipline;
this change does not add individual generation-bearing slot leases. Partial
unpublished constructor cleanup cannot publish a retry owner. Broader residency,
AWE migration and all six milestones/TB1-TB6 remain open.

A lifecycle follow-up remains: registration teardown retains its original order
before pending cleanup, so a retired diagnostic row can capture the shutdown-entry
queue count. Move unregistration behind successful child cleanup with the next
mesh-retirement integration. The current live queue counts include failed releases.
