# Physical geometry backing admission

The production BMT geometry arena now uses Shared `ResourceResidencyCache` for
complete committed blocks. The existing mesh residency cache continues to count
attributed mesh ranges. These are separate limits: a hole in a live block can be
reused, but it does not release the block's physical memory.

`GpuGeometryArena12` queries the device's allocation size, including its alignment,
before creating backing. An initially empty `GpuGeometryBlock12` transfers into
the reserved entry before native creation or mapping. Failed initialization and
failed release retain ownership and their charge until cleanup succeeds.

The default physical ceiling is four GiB. The world cache inherits its configured
logical geometry ceiling unless an explicit physical ceiling is supplied. Staging
ring and transient copy resources remain separately owned and accounted; this is
not a ceiling on all renderer or process memory.

An empty block can release only after all its mesh ranges and pending copies have
retired through the existing recorder and deletion queue. No new fence or playback
clock is introduced. Reusing a slot preserves allocation identities. Diagnostic
readers receive published counters without entering the render-thread ledger.

World pressure requests one eligible least-recent resident at a time, protects the
current demand, and waits for that exact victim's release before requesting another.
This avoids treating either logical holes or freshly rejected candidates as freed
physical memory. A block larger than the complete ceiling does not evict unrelated
meshes. Existing source retry debt and standalone preview failure handling remain.

Runtime `47501b68` and the structured-exception documentation correction `2eda7959`
retain Shared pin `a8617ef`. All 87 focused cases pass without skips, including
actual WARP allocation alignment, packed ranges, fragmentation, rejected provisional
ownership, copied-block retirement, thread access and the pressure policy.

Locked Windows Release passes with 88 existing warnings, no changed-file diagnostics
and no analyzer exceptions. It used isolated compilation, serial compiler execution
and an explicit eight GiB compiler allowance; the four GiB build admission default
is unchanged. Portable Release compilation also passes, but ILLink analyzer
out-of-memory warnings remain an explicit quality gap after six/eight GiB attempts.
The RCS1194 exception is narrowly documented: generic exception constructors cannot
carry the required physical-admission byte counts used by recovery.

Receipts `BmtPhysicalBackingPortable01/02`, `Tests01/02` and `Gui01` under canonical
Shared `TestOutput/archive-waveform-20260914` retain source, pin and payload evidence.
Component files are mirrored to canonical BMT with its index and unrelated cleanup
preserved; the entire dirty canonical checkout is not the tested payload. No GUI
was launched for this increment. Retail-world performance/fidelity, source replacement,
injected native release failure and full program acceptance remain separate work.
