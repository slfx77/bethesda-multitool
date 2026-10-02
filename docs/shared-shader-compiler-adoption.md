# Shared shader compiler and pack adoption

BMT runtime `a02bde1f`, with test assertion correction `ed27673b`, consumes Shared
`9a32aa76370c6544be275b0df57c872674be564a`. Native compilation, include lifetimes,
pack fingerprinting, binary validation and serialization use Shared implementations.
The 147-line `GpuShaderBytecodePack12` adapter retains the application's filename,
embedded source/permutation inventory and same-directory atomic publication.

`GpuShaderCompiler12.Compile` and renderer helpers retain `ReadOnlyMemory<byte>`
through cache entries, game-stage pairs and native pipeline descriptions. Repeated
lookups retain one backing buffer; hashing/reflection borrow spans. Pack admission
does not allocate a second bytecode payload. Game macros, shaders, counters, PCF
trace fields, fallback behavior, render states and frame/world policies remain.

The flat embedded-source provider preserves case-insensitive basename lookup.
`CompileSource` still returns independently owned arrays. The CLI pack generator
deliberately calls that uncached path so an old sidecar cannot seed a new pack.
Shared's existing array writer is sufficient; no new writer API, dependency,
submodule pin or lock update was needed for pack adoption.

## Evidence

Canonical Shared receipts are under `TestOutput/archive-waveform-20260914`:

| Receipt | Result |
| --- | --- |
| `BmtSharedPackBuildPortable01` | Locked Release at `a02bde1f`: 701 warnings, zero errors, no changed-file findings. ILLink's analyzer runs out of memory; portable analysis remains incomplete. |
| `BmtSharedPackTests01` | 183/184 pass; one source-order assertion still expected the previous nullable-buffer spelling. |
| `BmtSharedPackBuildTestsOnly01` | Test-only rebuild at `ed27673b`: 611 warnings, zero errors; reuses the unchanged application built at `a02bde1f`. |
| `BmtSharedPackTests02` | All 184 pass, zero failures/skips, with actual native shader compilation enabled. |
| `BmtSharedPackGuiBuild01` | Locked Windows Release at `ed27673b`: 88 warnings, zero errors, no analyzer crashes or changed-file findings. |
| `BmtSharedPackRegeneration01` | Windows payload freshly compiles all 124 permutations. The 1,512,361-byte pack is byte-identical to the previous compiler-adoption output. |
| `BmtSharedPackCanonicalSync01` | Checked component patch synchronizes 24 source/test files while preserving canonical staging; the test assertion correction is also synchronized. |

Pack SHA-256: `AE727C4DC7C76BDB1801A4DDBB8B94C55F3DD35F8CC2151DE390F84F56359812`.
The regeneration receipt identifies both actual application and Shared shader
assembly hashes. Tests cover malformed/truncated/corrupt packs, exact inventory,
atomic publication, source fallback, PCF, reflection and stable owned memory after
the input stream is overwritten and closed. A redundant full portable rebuild
(`BuildPortable02`) was stopped when Git version metadata recompiled the unchanged
application after the test-only correction; its receipt is retained.

Compilation uses an explicit 8 GiB compiler heap cap, isolated compilation and the
unchanged 4 GiB admission rule. Only the one test assertion changed after the full
portable build. No renderer source changed between that build and Windows checks.
GUI/world-rendering parity, publish and other-platform acceptance remain open.

## Checkouts and remaining integration

The implementation is committed in the existing
`C:/dev/Multitool-worktrees/bmt-material-preparation-20260912` checkout. The same
component source is visible in `C:/dev/Multitool/BethesdaMultitool`; unrelated
staged/unstaged work is preserved. This does not equate its older `main` ref and
all prior changes with the verified implementation checkout. Both consume the
same unchanged Shared pin. Earlier compiler/reference adoption is recorded at
`f38eb5cd` / `f4ce8fac` and receipts `BmtSharedCompiler*`.

Pipeline, descriptor, submission and world-cache adoption remain separate
production work. Keep game policy, HDR, frame slots and resource-use proofs in
the adapters. AWE remains the active GUI migration; this component completes a
BMT rendering prerequisite, not the six-milestone program or release gates.
