# BMT emission adoption validation — September 20, 2026

This receipt covers the bounded emission follow-up to checkpoint
`cb1432e7a5558c4c1ba4e0918004297ed805155b`, with reviewed Shared pin
`88326240d9603a2092886934eed6d63275fdbcf7`. That pin includes the emissive-strength
contract from `fb244162ce8b13fb1b9602e371c6150fd3e418fe` and subsequent native
renderer work. Pin adoption does not establish application renderer acceptance.
Legacy production export dispatch remains active. M2.1, the wider M1–M6 program
and release acceptance remain incomplete.

## Implemented and checked boundary

`NifNeutralSceneAdapter` transports the prepared emissive image, bounded RGB
factor and separate HDR strength without recomputing material policy. An
emission-only image receives a valid sampler with the prepared independent U/V
addressing. Existing material-cache and image-identity rules are retained.

Admission now accepts represented lit emission. It still declines external
emittance, malformed nonzero BGSM emission, BGSM emission on a source unlit
surface, and emission on a prepared unlit surface. The last guard covers authored
sky policy that forces unlit presentation without setting source `IsEmissive`.
Water optics, specialized viewer extensions, unsupported glass composition and
the other source/vegetation guards remain. Inactive external BGSM and inline
unlit-glow suppression retain preparation's existing behavior.

The 30 material cases include exact encoded HDR factor/strength, extension
presence for HDR and absence for default strength, emission-only sampling,
textured HDR and missing-image constant fallback, strength-sensitive material
reuse, malformed/unlit/external-emittance rejection, authored-sky rejection and
source preservation. The
[four-callable source review](../complexity/neutral-emission-adoption-20260920.json)
matches the current adapter's LF-normalized source hash. It is a bounded static
complexity/ownership review, not a runtime or performance receipt.

## Builds and locks

Full application and renderer-profiler locked restores passed. Portable
application and test builds used the coordinator, `-m:1`, ordinary analyzers,
serial compiler analysis and locked restore. The initial application build reported
26 inherited Shared/vendor warnings and no errors. The final test build's application
phase reported zero warnings; its test phase reported 71 inherited warnings and no
errors. Neither log contains AD0001 analyzer
failures or copy-lock warnings. The final test build recompiles the application
with the prepared-unlit guard added after the earlier standalone application
build.

The application full/portable, tests and EsmAnalyzer lock changes add Shared's
ImageSharp project dependency edge; the Windows application graph also adds
Shared WinUI's Win2D edge. Existing resolved package versions and content hashes
are unchanged. The duplicate application Win2D central-version entry was removed.
The profiler lock now selects the Windows application graph: seven new target
entries match the existing application graph's package versions and content
hashes; previously recorded package versions remain unchanged. Evidence is
`lock-review.json`, `profiler-lock-review.json`, `full-restore-locked.log` and
`profiler-restore-locked.log` under the local evidence directory below.

`assembly-correspondence.json` records matching object/application/test copies
for the tested application and Shared Core/Media DLLs and PDBs. The emission
batches below used application SHA256
`8279497ee64d20b4ee4e1b816f67ed04895a2a9c7177b0761a5cbcd2bde82572`
and test DLL SHA256
`b6a8cb8ce5fdbebbe5156dfc576c622017218257b743550f13ef265b90cc28e4`.
Later test-only rebuilds are separate evidence and do not retroactively change
these binary-bound results.

## Executed tests

Counts below were checked against both the logs and actual JUnit testcase nodes;
all have zero failures, errors and skips. Filters, positive minimums and fail-skips
were explicit. The Windows candidate batch used four threads; the other batches
used one. Retail inputs were explicitly enabled.

| Batch | Actual result | Scope and evidence |
| --- | --- | --- |
| Windows candidate | 111 passed | Material preparation (30), neutral adapter/skin, corpus oracle, CLI routing and asset export; `candidate.log` |
| Windows regressions/PSP | 184 passed | Affected sky, emission, water, ORM, alpha, hierarchy, input/host source contracts, PSP synthetic/retail and fixture resolution; `regressions-psp.log` |
| Windows corpus | 11 passed | Broad NIF, three fixed textured NIF fixtures and SpeedTree; `corpus.log` |
| Ubuntu focused | 208 passed | Candidate set plus selected host/input, PSP, alpha, fixture resolution and all three textured NIF fixtures; `linux-focused.log` |

The regression command's minimum was 183; actual discovery/execution was 184.
The Linux batch ran the Windows-built portable managed DLL through Ubuntu WSL
over `/mnt/c` (NTFS), with the configured corpus also on `/mnt/c`. Its receipt
records identical application/test hashes before and after execution. This proves
Linux runtime execution, including textured neon and the corrected PSP locator.
It does not prove a Linux source build, Linux self-contained publish or ext4
case-sensitive corpus behavior.

Physical fixture reads now use the actual lowercase NIF spelling and the
production texture normalizer; authored path assertions remain exact. The
production resolver lowercases Bethesda virtual keys, then performs physical
directory lookup. Existing physically mixed-case files/directories can therefore
remain unresolved on a case-sensitive filesystem. No production repair,
case-insensitive filesystem index or texture-preloading workaround is included.

## Complete textured neon acceptance

The fixed Prospector fixture is
`meshes/architecture/goodsprings/NV_ProspectorSaloon-Neon_Lights.NIF`, SHA256
`2DA30703F96B9C59F9135CA692F88D32B2DB94B4438E356805A4E59071A8BDED`.
The test requires the complete 19-node, 11-part scene: 2,455 source vertices,
1,895 triangles, five materials and five unlit draw parts. Source block 91 is the
one lit textured-emission part, with RGB factor `(1,1,1)` and strength `1`.
No part is removed to obtain admission; there are no repeated-position triangle
drops in this fixture.

All seven authored DDS paths and raw SHA256 values are pinned, with each direct
read bounded to 1–4 MiB. Cached texture objects, pixel-buffer identities and pixel
hashes remain unchanged before legacy export and after both exports. The source
scene's objects, hierarchy, matrices and buffers remain unchanged before the
legacy writer's established winding mutation. The original-node oracle and each
part's owning node, primitive and material association are checked. Encoded
comparison covers every oriented triangle occurrence, local attributes,
world-space placement, material factors including `EmissiveStrength`, samplers
and independently decoded PNG pixels.

The glow image's 512×128 RGBA hash is
`94169817AF052EA4C978804086FFAC74186C8EFFC221D2EC5CE0ECAEBE24031F`.
A direct Python decode of its raw BC1 blocks reproduced the existing endpoint
expansion and rounded-third policy. Pillow differs in 9,086 color components by
at most one unit because of decoder rounding; this increment preserves BMT's
existing decode rather than changing texture policy. The source glow DDS hash
is `C08BC2B90B92E361A2DA477CF063DE7EB869927BC63953F129EA9DAED6B0AEE4`.
The separate bounded original-BSA identity receipt confirms that the NIF and all
seven loose textures match the original decompressed archive entries byte for
byte (`neon-archive-identity.json`, eight matches, zero missing/mismatched).

## Encoded artifacts and third-party rendering

All ten actual GLB hashes match `corpus-artifact-hashes.json`. The eight preexisting
tincan, upper-body and two fully textured SpeedTree exports are byte-identical to
the preceding batch. The two new neon artifacts are:

| Export | Bytes | SHA256 |
| --- | --- | --- |
| Legacy native GLB | 4,532,316 | `a75fbb10039abdf7e9d5d52bd066dfad9ca447ad3e0c8dbf7a36d53d719515ea` |
| Shared GLB | 6,769,316 | `1fb158731e7003293094f15356824d2715cc6a85f246e951a5388527045ba1e5` |

Khronos validator `2.0.0-dev.3.10` reports zero errors across all ten GLBs.
Only the shared upper-body artifact retains two non-root skin-node warnings and
one empty-node information item. Both neon files have zero warnings and five
unused-tangent information items, corresponding to unlit draw parts. Native
SpeedTree files each have one unused-tangent information item.

Blender 5.1.1 CPU Cycles rendered both neon artifacts from four common cameras at
384×384, with 32 samples, seed 137, frame zero and no material overrides.
`render-neon/comparison.json` reports silhouette IoU 1 and zero RGB/alpha error
in every view. Independent decoding of the eight PNGs confirms each native/shared
pair has identical RGBA pixels. These are third-party GLB export comparisons;
they do not establish Bethesda D3D renderer parity or game-engine appearance.

The broad NIF rerun still inspected 120 files, adapted/compared 119 and declined
none; one input had no renderable scene. The null-texture oracle accounts for
59 exact repeated-position triangles dropped by the legacy writer. SpeedTree
still carries ten static scenes and 32,534 triangles; eight trees lack 17 authored
leaf-path occurrences (13 distinct paths). Its historical 0/10 result remains
historical, and missing authored appearance, wind and LOD remain unaccepted.

## Outstanding current-pin gates

- The later [native neon receipt](native-neon-20260920.md) records current-pin
  Windows application/profiler compilation and two actual Bethesda framebuffer
  captures. Its exact source boundary precedes three subsequent UI analyzer
  corrections. Current self-contained publish and interactive viewport input
  remain pending; the earlier publish used Shared `2d498be3`.
- The test-only Shadowkey locator rebuild passed with 71 inherited warnings and
  no errors. Its Windows baseline batch passed 18/18 without skips (11 adapter,
  seven fixture helpers). The retail pack reached 226 records: 193 carried and
  33 declined for unrecorded animation timing. This predates the subsequent
  Shadowkey material-policy work. Ubuntu baseline execution also passed 18/18
  without skips using the same portable binaries in WSL over NTFS; this does
  not validate the subsequent adapter changes or a Linux source build.
  `assembly-correspondence-shadowkey-lookup.json` binds this test-only rebuild;
  the application and Shared binary hashes remain unchanged.
- Linux source-build/publish and physically mixed-case corpus lookup on a
  case-sensitive filesystem remain unvalidated.

Local evidence is under `TestOutput/emission-adoption-20260920`; JUnit files are
`TestOutput/emission-adoption-20260920-{candidate,regressions-psp,corpus,linux-focused}.junit.xml`.
Private game payloads and generated render/export artifacts remain external to
the committed source. The [preceding receipt](material-resume-20260920.md) retains
its original source/pin boundary and historical failures.
