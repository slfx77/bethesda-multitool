# Shared native buffer ownership

BMT's geometry arena delegates committed UPLOAD/DEFAULT buffer creation, persistent
mapping, exact physical-byte admission and retained release to Shared
`NativeBufferBlocks`. The duplicate `GpuGeometryBlock12` is removed. BMT still owns
range layout, staging, pending-copy holds, native commands, pressure/eviction policy
and its structured geometry-budget exception. No live GPU user can be evicted merely
because a logical range became empty.

Runtime `269e3928` pins Shared `cb597c13`. Project correction `a1b3a7bc` allows the
two Windows Shared references to retain their own declared runtime graphs; portable
reference properties remain unchanged. The pin also adopts the owner's committed
ReSharper cleanup and analyzer/build-tooling changes. Shared's later standard scene
upload adapter at `b7f27857` is not part of this BMT pin.

September 29 validation, under canonical Shared
`TestOutput/archive-waveform-20260914`:

| Receipt | Result |
| --- | --- |
| `BmtNativeBufferPortable01` | Clean `269e3928`, locked portable Release build passes; 1,248 emitted warnings, zero errors. |
| `BmtNativeBufferTests01` | Same source/pin; all 49 affected cases pass without skips, including WARP backing/copy/retirement checks. Existing assertions are unchanged. |
| `BmtNativeBufferGui01` | Clean `a1b3a7bc`, locked Windows Release build passes; 846 emitted warnings, zero errors. No GUI launch. |
| `NativeBufferBlocksBuild01`, `NativeBufferBlocksTests01` | Shared `cb597c13`, Full native-test build passes; six focused cases pass without skips, including actual mapped upload to DEFAULT to readback through a completed WARP fence. |
| `BmtNativeBufferCanonicalSync01`, `BmtCanonicalProjectGraphSync01` | Component/pin and necessary project/lock graph mirrored into canonical BMT while preserving its staged/index state, local Blender tests and analyzer-profile policy. |

BMT builds use the maintained wrapper's Development profile; BMT's existing
project analyzers remain enabled independently. These are not Full-profile release
gates. Both builds use isolated serial compilation, an explicit eight GiB compiler
allowance and the unchanged four GiB free-memory admission. There are no analyzer
exceptions or diagnostics on the changed application arena. The Shared native owner
reports S6640 for the pointer required by the D3D12 Map ABI; this narrowly reviewed
interop use remains visible. Existing unrelated diagnostics are still outstanding.

Payload hashes and source identities stay with the receipts. The entire dirty
canonical application checkout was not the tested payload. Synthetic GPU tests do
not establish retail-world fidelity/performance, injected COM-release failures or
cross-platform/publish readiness. Terrain ownership and actual world integration
remain subsequent work; the full six-milestone program remains incomplete.
