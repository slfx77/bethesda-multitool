# BMT Travels fidelity validation — September 20, 2026

This increment follows `91d58a57`, retaining exact Shared
`88326240d9603a2092886934eed6d63275fdbcf7`. It changes PSP raster decoding and
Shadowkey neutral preparation. Production export dispatch, native viewer routing,
Granny paths and classic-media timers remain unchanged. M1–M6/TB1–TB6 and separate
integration/release acceptance remain active.

## Changed behavior

- PSP rasters retain every authored mip admitted by the existing exact-length
  inference. Each level decodes its own padded rows, and `ToDecodedTexture` retains
  those levels. Four-bit odd-width rows now round up before pitch alignment.
- Shadowkey defaults to opaque source colors. Explicit magenta keying emits matching
  masked alpha. Exact caller occurrence identity and frame/skin/key choices survive
  normalized and encoded metadata. Animation with unproven timing still declines.

The [PSP decoder review](../psp-authored-mips.md) and
[Shadowkey fidelity review](../shadowkey-neutral-fidelity.md) record source bounds,
independent fixture hashes, source immutability and encoded comparisons.

## Build and runtime results

Both builds used the pinned two-slot coordinator, default 4 GiB admission,
isolated compilation, one MSBuild node, locked restore and ordinary analyzers.
Serial compiler analysis used the recorded response file and 12 GiB heap cap.
The production compile passed without warnings. The initial test compile reported
72 warnings, including one new nested-loop brace warning; the final test-only
rebuild removed that warning and reported the 71 inherited warnings, zero errors,
no AD0001 failures and no copy-lock warnings.

| Focused class | Windows | Ubuntu WSL |
| --- | ---: | ---: |
| Shadowkey neutral fidelity, including three retail cases | 19 | 19 |
| Existing PSP raster decoder | 19 | 19 |
| New synthetic authored mips | 8 | 8 |
| Bounded original PSP raster | 1 | 1 |
| Corpus path resolution | 7 | 7 |
| Total passed | 54 | 54 |

Both runs used explicit class filters, minimum 54, one thread, retail opt-in and
fail-skips. Actual JUnit testcase nodes confirm zero failures, errors or skips.
The first Windows run was 51 passed / 3 failed / 0 skipped. All three failures
required an explicit sampler object even though the pinned writer omits all-default
samplers. The repaired oracle follows the actual base-color texture binding and
checks effective wrapping; explicit clamp/mirror remains a failure. The original
failed log, JUnit and binary receipt remain preserved.

The final application DLL SHA256 is
`6f36328c690980ffc683d1fa4baf6ba3e447cba4c7683bd6056ce522dcf57c25`;
the final test DLL SHA256 is
`ee3aa7a3ac5344f9f000c6152290fe472d49389f7dcc497a9d12d08af1169d56`.
Eight DLL/PDB groups match their object, application and test copies. Five frozen
production/test source hashes match the final build inputs. The test-only repair
did not change the production or Shared binaries.

Ubuntu executes these Windows-built portable binaries through WSL over NTFS.
The receipt verifies unchanged hashes before and after execution. This does not
establish a Linux source build, publish or ext4 case-sensitive fixture behavior.

## Corpus boundary and remaining acceptance

The original Shadowkey pack has 237 slots: 11 empty, 193 carried and 33 declined
for unknown animation timing. Fixed fern and arrow records preserve independent
positions, UVs, image pixels and all oriented triangle occurrences, including the
arrow's six repeated-position triangles. Both opaque and explicitly keyed alpha
are checked. This is encoded-data acceptance; it does not establish game appearance.

The PSP check reads only the original 1,989,324-byte `Hub_1` entry, then verifies
the 11,484-byte `ob_Rock2` raster and all eight independent RGBA hashes from
128×128 through 1×1. It does not claim a full raster corpus scan or normalized PSP
material/export/viewer adoption.

Current-pin Windows GUI/profiler/publish, native rendering, actual GUI replacement,
cancellation/shutdown/localization and Linux source-build acceptance remain open.
The proposed Shadowkey pack browser/native integration is a separate increment.
NIF M2.1 and historical SpeedTree results are unchanged by these tests.

Local evidence lives under `TestOutput/emission-adoption-20260920`:
`tests-travels-fidelity{,-retest}-build.log`, `travels-fidelity{,-retest}.log`,
`linux-travels-fidelity.log`, `assembly-correspondence-travels-fidelity{,-first}.json`
and the source-freeze/WSL receipts. JUnit files are under `TestOutput` with labels
`emission-adoption-20260920-travels-fidelity`, its `-retest`, and
`emission-adoption-20260920-linux-travels-fidelity`. No private payload or generated
render/export artifact is committed.
