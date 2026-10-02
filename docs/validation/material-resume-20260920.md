# BMT continuation validation — September 20, 2026

This receipt covers the first continuation batch through BMT `d5cde669`, based on
`9f08c55d`, with Shared `2d498be3` and DDXConv `805ef26b` unchanged. Validation ran
against the working source before these ordinary checkpoints; committing did not
change that source. It is implementation and bounded integration evidence, not
completion of M2.1, M1–M6, TB1–TB6 or release acceptance.

| Checkpoint | Change |
| --- | --- |
| `dbc7da1e` | Portable lock selection; original full lock and package versions retained |
| `f91d9fd1` | Prepared tint cache identity, duplicate skin labels, textured material/skin corpus oracles and render harness |
| `f72216a8` | Original Oblivion PSP CLUMP graph decoding and bounded corpus acceptance |
| `d57c7ce0` | Actual resolved asset-source protection, cancellation and partial-success checks |
| `d5cde669` | Bare Windows `pex` routing and responsive Model Tools layout candidate |

Builds used the pinned coordinator's two slots, default 4 GiB admission and
isolated BMT compilation. Portable and full locked restores passed. Ordinary
analyzers passed using `/parallel-` and an explicit 12 GiB compiler heap after an
earlier adaptive-heap run produced six analyzer-memory failures. Final portable
application compilation had no warnings; tests had 71 inherited warnings, no
errors, analyzer crashes or copy-lock warnings. Windows GUI Release build and
self-contained publish passed with nine existing warnings and no errors.

All successful test batches below used explicit filters, positive minimum counts,
at most four threads, and fail-skips. Retail batches used one thread.

| Batch | Windows | Ubuntu | Scope |
| --- | --- | --- | --- |
| Material/skin/adapter, corpus oracle, CLI routing, asset export | 99 passed | 99 passed | Includes 57 material/skin/adapter cases, expanded from the original 49 |
| New PSP synthetic plus existing leaf readers | 109 passed | 109 passed | 48 new graph cases plus 61 existing cases |
| Final PSP synthetic/retail and alpha conversion | 60 passed | 50 passed, 10 failed fixture lookup | Linux failures are fail-skips for PSP dated builds; both alpha cases pass |
| Affected material/hierarchy/viewer regressions | 92 passed | Not run | Scoped changed-path regression batch |
| Additional water cases | 8 passed | Not run | Portable extraction policy |
| Retail Starfield water and synthetic alpha | 2 passed | Not run | Earlier bounded batch |
| Accessibility ratchet | 1 passed | Not run | Source contract; no desktop claim |
| NIF/SpeedTree corpus | 10 passed | Not run | Actual counts and missing textures below |

Ubuntu executed the Windows-built portable managed binaries in place under a
coordinator lease. This is not a Linux source build or self-contained publish.
Earlier failures remain in local logs, including the old missing loose Xbox
fixture and the original PSP universal-success expectation. The final Windows
PSP test requires exactly the independently identified NaN-normal decline.

Private corpus results:

- Broad NIF: 120 inspected, 119 adapted and compared, zero adapter declines,
  one no-renderable-scene input. Null textures limit this batch to geometry and
  admission. The oracle accounts for 59 exact repeated-position triangle drops
  by the legacy writer while requiring all distinct-position triangles.
- Textured NIF: fixed-hash `tincan01.nif` and `_male/upperbody.nif` pass decoded
  image/material, triangle correspondence, hierarchy, occurrence, skin and source
  preservation checks. Sampled synthetic animated world positions are not NIF
  animation-track or playback acceptance.
- SpeedTree: current bounded result is 10 carried, zero declined, 32,534 static
  triangles. Eight trees lack authored leaf textures (17 occurrences, 13 distinct
  paths). Two fully textured trees have render evidence. The historical 0/10
  result remains historical; wind, LOD and native rendering are unaccepted.
- PSP: 963 inspected, 962 decoded, one exact invalid-source decline; 14,764
  decoded frames and 992 geometries/atomics. See the
  [CLUMP receipt](../oblivion-psp-clump-decoding.md) for source identity and limits.

Khronos validator 2.0.0-dev.3.10 reports zero errors for all eight NIF/SpeedTree
GLBs. Shared upper-body output has two non-root skin warnings and one retained
empty-node information item. The original hierarchy is preserved.

Blender 5.1.1 CPU Cycles compares four common cameras per fixture, fixed seed,
32 samples and 384-pixel captures. Tincan and both fully textured trees are
pixel-identical across writer outputs. Upper-body silhouette IoU is 1; maximum
foreground linear RGB RMSE is 0.00003584. These are third-party GLB export
comparisons, not Bethesda D3D rendering acceptance.

Actual portable and published Windows CLI runs preserve successful bytes on
partial failure, refuse output over the actual input even with `--force`, and
leave no staged temporary files. Both published NIF exports match the tested
legacy GLBs byte-for-byte. Bare published `pex inspect --help` exits successfully
through the CLI, closing the observed routing defect.

Local evidence lives in `TestOutput/material-resume-20260920` and filtered JUnit
files in `TestOutput`. Final portable assembly correspondence (`v3`) is:

| Artifact | SHA-256 |
| --- | --- |
| Application DLL | `a78a1f270022dd9832095a58f812dc5d7d78be0bea2bd5e08b60d57ad73c5974` |
| Application PDB | `88d2ede4fcab367bb33721b0dee23cd8d5112e2fdfe990cc0dd46f653d9a19b6` |
| Final tests DLL | `6d26239cf55d14913c096b2e0d98559238b8e4599362d3bdc36e7db8c8484b9d` |
| Final tests PDB | `5edac26b04ba08447f6babf35b40aa3613fe339edefbed39e3f681c3671735df` |

Object/bin/test copies agree. Earlier binary-bound receipts remain available;
the final test-only edits corrected the census, fixture lookup and five analyzer
warnings. The production application binary is unchanged.

The native computer-use pipe was unavailable on three checks. Model Tools resize,
splitter behavior, pseudolocalization, GUI source replacement/cancellation and
shutdown remain explicit gaps. Source checks, successful publish and CLI execution
do not replace those checks. Legacy production export dispatch remains active.
The full completion program and all unrelated pending work remain active.
