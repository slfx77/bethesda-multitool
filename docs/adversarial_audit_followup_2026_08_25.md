# Adversarial audit follow-up — fixes + investigations (2026-08-25/26)

Companion to `adversarial_dmp_recovery_audit_2026_08_25.md`. That doc holds the findings; this one
records what was FIXED, what each investigation MEASURED, and the decisions still owed by the user.
Everything below is uncommitted on main.

## Phase A — all ten workstreams landed

| WS | What | Proof |
|---|---|---|
| WS0 | `SyntheticMinidumpBuilder` test infra (byte-valid MDMP: BaseRva parity, range counts, past-EOF regions) | 3 self-tests |
| WS1 | **Gap-scanner VA-parity fix** (`DmpGapRecoveryScanner` anchors stride on `gap.VirtualAddress`) | Regression-proven (test fails on revert). **Debug xex2: 3 → 12,285 candidates** (12,148 dialogue); **xex44 byte-identical** (18,063) |
| WS2 | Minidump hardening: fixed caps → file-capacity bounds + `Logger.Warn`; past-EOF region clamp; 4× ignored `ReadArray` return counts fixed; `EsmStringDetector` chunk overlap | Synthetic tests (12,000-range dump parses; truncated region clamps) |
| WS3 | Region-boundary stitch in `RuntimeObjectScanner` (+`ThrowIfGreaterThan` overlap guard), `RuntimeRefrHeapSweep`, `RuntimeAnimationScanner` — structs straddling VA-adjacent regions now tested; fails closed without a successor | Straddle fixtures with file-order inverted from VA-order (flat over-read would fail) |
| WS4 | Carve windows: per-format `ParseWindowSize` (DDX/PNG 8MB, XDBF 10MB) clamped to the **flat-readable run** (contiguous in file AND VA — the spec'd containing-region clamp was rejected by the Bucket-B gate: it shrank real carves); PNG IHDR fallback instead of silent drop; boundary-fallback carves now flagged in the manifest (both writer paths) | 12/12 Bucket-B DDX pins unchanged; 3 old 0.7-guesses replaced by real boundaries (`pipboyarm01` 367KB→770KB); 9 cross-VA-garbage "boundaries" now correctly fallback-flagged |
| WS5 | FaceGen carver formats EGM/EGT/TRI (exact header-derived sizes; TRI floor+flag); CTL deferred (tool-local parser) | 15 synthetic tests pinned against the real parsers; Debug-dump carve gains 2 .egm + 8 .tri (pre-fix zero) |
| WS6 | `dmp rtti --census --all-regions` (default heap window unchanged) | flag-only superset behavior tested |
| WS7 | CLI: rtti usage-string crash fixed (both sites); `formtype-census` accepts file-or-dir (shared `CliHelpers.DiscoverDumps`); `hexdump` errors loudly + suggests `va:`; SignatureScanner rewritten on the app's `SignatureMatcher` (4 defects retired: first-chunk carry, +overlap offset shift, double-count, duplicate/suffix trie loss) | 46/46 CLI tests; boundary smoke at exact offsets; nonzero .kf hits |
| WS8 | Diagnostics ratchet — 8 new codes: `refr.verdict-unhandled-disposition`, `refr.override-no-master-record`, `catalog.duplicate-dmp-record` (+`differs` metadata), `catalog.alias-shadowed-by-exact`, `land.terrain-mesh-encode-failed`, `land.vclr-extract-failed`, `land.encoder-declined`, `land.no-allocator-base`; decompress-failure pass-through counter; semdiff skip warning; LAND-calibration skip Debug→Warn | 18 new tests; output ESM bytes unchanged (pinned); full suite 8,759/0 |
| WS9 | M1 guard test: every generic-sweep FormType must be yielded or named-exempt (+anti-staleness inverse), exemptions 1:1 with the surfaced set | fails when an exemption is removed |

Full default suite: **8,759 / 0 failed** (two independent full runs). Full both-TFM build: 0 warnings, 0 errors.

Known pre-existing issue surfaced (not fixed): `MaxFilesPerType` cap races under parallel extraction
(`MemoryCarver.cs` — checked before the stats increment), so per-type overshoot varies per run.

## Phase B — investigation verdicts

### I4a — PCM WAV in dumps: REFUTED
Zero hits for canonical PCM (`fmt` 16/tag 1), IEEE-float, and extensible WAV headers across
Fallout_Debug.xex, xex44, xex21, Jacobstown. The XmaParser's non-XMA RIFF rejection discards
nothing that exists in this corpus. Closed, no code change.

### I4b — uncompressed DDS size math: CONFIRMED (1 instance) + FIXED
Debug xex carries exactly one plain uncompressed DDS (`monofonto_verylarge02_dialogs2_0_lod_a.dds`,
512×512, pfFlags 0x41, 16 bpp) — carved at **exactly half** its true size (262,272 vs 524,416) by the
block-compression default. xex44 has zero plain DDS resident. FIXED: `DdsFormat` now sizes
uncompressed formats (no DDPF_FOURCC, empty fourcc, rgbBitCount 1..128) by rgbBitCount, with the
mip chain summed per-pixel; BC paths untouched (existing DXT pins green). Cubemap/volume flags
remain unhandled — LOW, no instance observed.

### I4c — TLOD/LAND FormType: RESOLVED (not an extraction bug) + FIXED
Both era PDBs (Release Beta AND Debug) prove the engine's `ENUM_FORM_ID`:
- **0x42 = LAND_ID → class `TESLand` — never compiled** (no layout in any PDB); no runtime
  instance can carry this byte.
- **0x44 = TLOD_ID → class `TESObjectLAND`** (runtime terrain; 60 B Release / 44 B Debug).
`pdb_layouts.json` was correct all along. FIXED: 0x44 added to `PdbStructLayouts.SpecializedFormTypes`
(RuntimeWorldReader owns it; kills the redundant generic-sweep copy), 0x42's entry re-commented as
vestigial, TLOD guard-test exemption retired. The LAND auto-calibration in `EditorIdLookupTables`
stays as-is (dynamic, era-defensive) — PDB truth says it should detect 0x44.

### I5 — M1 decision table → USER RULES PER-TYPE
Corpus counts from the regenerated census (`artifacts/dmp-audit/census2026-08-25/`), field richness
from pdb_layouts.json + `GetReadableFields` (verified 2026-08-25). "Probeable" = fields usable by
the layout-shift probe; 0 probeable = the per-type shift cannot be verified on that type.

| Type | Records (dumps) | Readable flds (probeable) | Blocked >8B structs | Content | Suggested |
|---|---|---|---|---|---|
| CAMS | 7,328 (32/32) | 15 (2) | CAMERA_SHOT_DATA 40B | camera shots | **wire** (needs raw-bytes widening for DATA) |
| IDLM | 5,610 (32/32) | 11 (5) | OBND only (parsed) | idle markers | **wire** |
| LSCR | 4,869 (32/32) | 8 (3) | — | loading screens (ICON + DESC text both recoverable) | **wire** (cheapest, FLOR-style) |
| IPDS | 1,394 (32/32) | ~0 real (0) | sole field = size-0 unknown array | impact data sets | EDID-stub or skip |
| RGDL | 1,143 (32/32) | 14 (2) | 6 structs (14–80B) | ragdolls | wire-with-widening or defer |
| EFSH | 1,019 (32/32) | 7 (0) | EffectShaderData 308B | effect shaders | defer ⚠ no shift verification possible |
| MSET | 393 (5 late) | 26 (6) | name 12B + 6 layers 16B | radio media sets (proto-interesting) | **wire** |
| CHIP | 81 (17) | 17 (8) | OBND + icon 12B | casino chips | **wire** (cheap) |
| CSNO | 80 (16) | 4 (0) | 2 size-0 arrays + 56B data | casinos | EDID-stub or skip |
| DOBJ | 32 (32) | ~0 real (0) | sole field = size-0 array | default-object manager | skip |
| AMEF | **0** | — | — | nothing captured corpus-wide | **drop from list** |

Enabling work for the "blocked" columns: widen `RuntimeGenericReader.ReadEmbeddedStruct` (>8B) from
descriptor strings to raw bytes + BE→LE per-field swap via the existing `Conversion/Schema`
definitions; then the FLOR five-edit recipe per wired type. Wiring changes converter output
corpus-wide — each type awaits the user's ruling (guard-test exemption removal is forced per type).

### I1 — DDX 0.7 ground truth: 0.7 REFUTED; window fix confirmed; NEW defect found → USER RULES
Data: `artifacts/dmp-audit/i1-ddx/*.csv` (409 carved DDX across 3 dumps, 45.2% fallback; 24,268
ground-truth DDX from the July-21 Textures.bsa).
- **Version drift dominates direct matching**: only 68/409 carves byte-verify against ANY sampled
  BSA build (the dumps predate all shipped BSAs — different compressed streams entirely), so the
  statistical answer comes from the full BSA corpus, which is the exact population 0.7 estimates.
- **The 0.7 constant is wrong**: trueRatio median 0.797, p10–p90 [0.176, 1.289]; per-format medians
  ATI1 1.252 / ATI2 1.186 / DXT1 0.430 / DXT3 0.526 / DXT5 0.561. Flat 0.7 fully captures only
  46.8% of files and loses ≥10% of bytes on 50.7% (≥30% on 35.9%). The 4 byte-verified fallback
  pairs agree (3/4 lost ≥10%; worst −48%).
- **Root cause is the denominator, not the multiplier**: the DDX mip-count decode
  (`(formatDword>>16)&0xF`) yields **1 on all 24,268 corpus files and all 409 carves** —
  `uncompressedSize` never includes the mip chain, which is why mip-heavy ATI1/ATI2 sit >1.0.
  (Also means "mips lost" is unmeasurable from manifest metadata — a defect in itself.)
- **The 8MB window works**: 4 of 6 >512KB carves have real boundaries (impossible pre-fix); the
  fallback rate no longer rises with size. Residual exposure is the constant itself: ~23% of all
  carved DDX expected to lose ≥10% of bytes. Softener: DDS conversion succeeds at ~equal rates for
  fallback (80.5%) vs real-boundary (82.6%) carves — truncation costs tail mips, not mip 0.
- **Options measured for the user to rule on**: flat 1.35 (97.1% full capture, 0.5% lose ≥10%,
  +1,162 MB overshoot — mostly clamped by next-file signatures); per-format p95 constants (95.0%,
  0.7%, +726 MB); or an exact LZX chunk-header walk (no constant at all). Fixing the mip-count
  decode is prerequisite to any denominator-based rule.
### I2 — BSA/zlib-in-dump recovery yield: REFUTED — recovery pass NOT worth building
New `dmp recovery-probe` command (kept as a research tool; 15/15 synthetic tests). Five-dump survey
(`artifacts/dmp-audit/i2-recovery-probe/`):
- **BSA prong**: ZERO `BSA\0` magic hits in any gap of any dump — the engine streams BSAs via file
  handles; tables live in the OS file cache, not captured process pages. Nothing to parse.
- **zlib prong**: 20,442 RFC-1950 candidates, 250 trial-inflated → **1 clean (0.4%)**, 2 partial,
  49.1 KB total inflated corpus-wide (one complete 1.7 KB zlib NIF in Debug xex2; two partials die
  mid-stream on evicted backing). Candidate density matches pure-chance byte-pair expectation — the
  population is noise. Bars (≥20% clean AND ≥5 MB asset-shaped) missed by ~150×.
- `dmp buffers` floor agrees from the other side: gap value is strings (36–69 MB) and pointer
  graphs (26–56 MB) — already exploited by existing tooling — not carvable compressed assets.
The audit's M3 item is CLOSED-REFUTED; M2 (LZX→BSA wiring for XMem-flagged archives) remains a
separate, still-open item unrelated to dumps.

### I3 — attribution residue frequency: ALL THREE THEORETICAL (counters stay as tripwires)
Data: `artifacts/dmp-audit/i3-attribution/` (15 CSVs). New `dmp xclc-audit` command + 11 tests.

| Mechanism | Bar | Measured (xex44 / xex29 / xex21 / Debug xex2) | Verdict |
|---|---|---|---|
| (a) last-cell proximity tail | ≥100 tail refs >100KB past the last cell | **0 tail refs in all four** | theoretical |
| (b) XCLC ±200B grid theft | ≥5 differing-grid contested cells/dump | **0 — zero scannable XCLCs exist in any dump** | theoretical |
| (c) catalog duplicate first-wins | ≥10 `differs=true` discards/dump | xex44 **0**; xex21 **584** discards, all GMST, differs true=0/false=0/**unknown=584** | theoretical ⚠ |

Notes that matter for any future re-run:
- The "DMP proximity found N REFRs" log is **Debug-level and suppressed at default CLI verbosity**
  (`to-esm --verbose` does NOT raise the Logger level) — the measurement instead mirrored
  `ResolveCellRefs`' exact window over the same scan lists. Largest single-cell claim anywhere: 140
  (xex29, mid-list, next-cell-capped, 23KB tail); the uncapped last-cell window lands in empty space
  because REFR runs sit tightly behind their cells.
- (b) is not "the guard works" — it is "there is nothing to guard": the ±200B `FirstOrDefault` at
  `CellRecordHandler.cs:599` never attaches anything in this corpus. If a future dump/build does
  carry standalone XCLCs, re-run `dmp xclc-audit` before trusting grid-keyed terrain attachment.
- ⚠ (c) caveat for the user: zero *provably* differing discards, but all 584 are `unknown` — the
  cheap classifier cannot prove GMST equality. A deep GMST value compare would settle it. On the
  letter of the bar this is below threshold; the counter is now permanent, so drift will show up.

---

# Round 2 — acting on the rulings (2026-08-26)

All four decisions below were ruled on by the user; this section records what was built.

## Ruling 3 — GMST duplicate certainty: DEEP COMPARE LANDED

`RecordCatalog.DescribeDiscardedModelDelta` gained typed arms for `GameSettingRecord` and
`GlobalRecord`. Both are flat scalar C# `record`s, so the compiler-synthesized value equality **is**
an exact deep compare — no hand-written comparer to drift:
`(kept with { Offset = 0 }) == (discarded with { Offset = 0 })`.

Three subtleties worth keeping:
- `Offset` is zeroed (heap address, not content) but **`IsBigEndian` deliberately is not** — two
  captures of one record inside one dump must agree on endianness, so a divergence is a real signal.
- Float members compare through `EqualityComparer<float?>.Default` → `Single.Equals`, which treats
  **NaN as equal to NaN** (unlike `==`). That is the wanted behaviour — a corrupt capture read twice
  is the same capture twice, not a permanent difference. Pinned by a test.
- The `GenericEsmRecord` arm was left as the cheap surface probe **on purpose**: its `Fields` /
  `DecodedTree` are interfaces that do not override `Equals`, so record equality there would degrade
  to reference equality and report `"true"` for every duplicate. Commented in place.

⚠ **A pinned contract flipped deliberately**: `RecordCatalogTests.Duplicate_Dmp_Records_Report_A_
Warning_Diagnostic_With_Differs_Metadata` built two GMSTs differing only in `IntValue` and asserted
`"unknown"`; it now asserts `"true"`. That test is also the regression proof — no other code path can
return `"true"` for two `GameSettingRecord`s. The `GameSetting(...)` fixture helper was also fixed: it
never set `ValueType`, so it produced `Float`-typed records carrying an `IntValue`, which no real
parse can produce. New coverage: identical-but-different-`Offset` → `"false"`; a per-member theory
(`FloatValue`/`IntValue`/`StringValue`/**`ValueType`-only**/`EditorId`) → `"true"`; NaN → `"false"`;
and the same for `GlobalRecord`. The `ValueType`-only case matters because Integer and Boolean both
store their value in `IntValue` — it is the case a naive "compare the non-null value field" misses.

## Ruling 4 — M2 (LZX→BSA): HELD, with the silent failure closed

The user held the feature after the evidence landed: **no archive in the corpus carries `XMemCodec`**
— all 87 `.bsa` under `Sample/` are zlib, including all four Xbox 360 builds (360 texture archives are
flags `0x0143`, `CompressedArchive` clear). With no real archive the two parameters a decoder needs —
the LZX window size, and whether an entry is one XMemCompress stream or several — cannot be validated,
so a decoder would be untestable guesswork.

What did land is the honesty fix, because "reads as file missing" was the actual audit finding:
- `BsaHeader.UsesXMemCodec` added beside `IsXbox360`/`DefaultCompressed`, documenting why it is
  detect-only.
- `BsaExtractor.ExtractFile` now throws a **named** `NotSupportedException` for an XMem entry instead
  of letting it fall into the zlib branch and surface as an opaque deflate error.
- `ArchiveFileSystem.TryReadAllBytes` still returns `null` (layered mounts depend on it) but now
  **logs** the extraction failure — previously an entire unsupported-codec archive was
  indistinguishable from "the file simply isn't here".
- First end-to-end compressed-entry extraction test in the suite (`BsaMalformedTests`, flags `0x203`).

**If a real XMem archive ever appears**, the open unknowns are exactly those two parameters; the
decoder itself (`DDXConv/Compression/LzxDecompressor.cs`) already exists and is `public`, and
`BethesdaMultitool.csproj` already references DDXConv.

## Ruling 2 — DDX exact extent: LANDED. The heuristic is gone; fallbacks went 36-45% → **0%**

The 0.7 constant and the always-1 mip decode are both deleted. `DdxFormat` now walks the payload's own
XMemCompress chunk framing (2-byte BE size; `0xFF` terminator consuming `compressed + 10`; reject
`> 0x980A` and zero-size headers) — **framing arithmetic only, no LZX decode, no window allocation**,
so it is materially *cheaper* than the byte-by-byte token scan it replaces. Implemented entirely in
`BethesdaMultitool`; the `src/DDXConv` submodule was verified untouched.

**Corpus proof (the gate, run before carve behaviour changed):** 26,123 `.ddx` under
`Sample/Unpacked_Builds/360_July_Unpacked` (1 is zero-byte) → **26,122/26,122 walked exactly to file
length, 0 mismatches, 0 framing errors.** Stream counts: 6,281 single, 19,841 double, never more than 2.
`BE32@0x3C` ≥ Σ declared-uncompressed in all cases and *exactly* equal for every two-stream file.
Re-run through the real `DdxFormat.Parse`: **24,268 parsed, 24,268 exact, 0 boundary fallbacks** — and
`mipCount` now spans 1-13 (9 mips is the mode at 12,386 files) instead of 1 for every file.

**Two deviations from the approved design, both forced by measurement — keep these:**
1. **Stream 2 is admitted as a cleanly-framed *prefix*, not all-or-nothing.** The plan's "stream 2 must
   walk cleanly to its own terminator" made `debug_rugsmall01_tail` **shrink 10 → 7 mips**: that dump's
   copy has stream 2 chunk 1 intact (byte-identical to disk) and chunk 2 corrupt, so an all-or-nothing
   rule discarded 14,834 recoverable bytes. Now bounded by an uncompressed budget of `@0x3C − uncomp1`,
   which never under-carves a real file (all 19,841 two-stream files satisfy the equality exactly).
2. **The boundary-token cap applies only to bytes no terminator vouches for.** Capping the whole walk
   truncated three real files, because a 4-byte magic occurring *inside* LZX output is a far weaker
   signal than the framing: `1stpersondisplacergloves_m` would carve 2,547 of 79,494 bytes (−97%),
   `nvmcc_terminal_int_floor02` 140,149 of 516,536, `explosionsparks03` 67,113 of 145,868. The cap now
   scans only `[terminatorProven, walked)`, preserving the "never run into the next file" guarantee
   exactly where the bytes are unproven.
Both follow the user's "prefer over-carving" ruling, and both are pinned by tests.

**Bucket B: 12/12 pass, nothing shrank, zero pin changes.** Contrary to expectation *no mip counts grew*
— the five candidate cases were already at the full chain for their dimensions (256²→9, 512²→10,
512×256→10). The win is that the extent is now **exact** rather than coincidentally sufficient.
`DdxFormatTests` 36/36 (12 new cases incl. paired accept/reject for the `0x980A` cap so it can actually
fail, and a test proving the `(dword>>16)&0xF` bits are now ignored). Full suite at that point: 8,856/0.

**Doc corrections** landed in `docs/Xbox_360_DDX_Format.md`: 0x3C-0x43 is not padding (it is
uncompressed-size @0x3C + first-stream compressed length @0x40), the GPU fetch constant sits at file
offset 0x24 not 0x14, and bits 16-19 of dword_1 are `base_address` (always 0) and must never be read
as a mip count.

### Both of the above defects — FIXED 2026-08-26 (plus a third found alongside)

1. **`CarveWriter` manifest/disk divergence — FIXED.** Two compounding faults: `CarveExtractor.BuildOutputPath`
   used a check-then-use `while (File.Exists(...))` loop (TOCTOU under parallel extraction), and
   `WriteFileWithRetryAsync` renamed the loser to a GUID **after** the caller had already recorded the
   requested name. Now the name is reserved **atomically** via `FileMode.CreateNew` (whoever wins owns it),
   and the writer **returns the path actually written** so both manifest branches record the real file.
   Regression test holds the intended path open to force the retry and asserts the manifest names a file
   that exists on disk.
2. **`ValidateDdxHeader`'s `0x04 != 0xFF` rejection — FIXED, measured.** Byte 0x04 is a small enum
   (corpus values 0-6) whose **0xFF is legitimate on 1,847 of 26,123 files (7.1%)** — and *all 1,847 pass
   every other check*. The all-0xFF garbage it was presumably guarding against is rejected more precisely
   downstream: 0xFF is not in `Xbox360GpuTextureFormats`, and the chunk-framing walk will not walk garbage.
   Replaced with an explicit version upper bound (every corpus file reports version 4; the field was
   previously unbounded, so an all-0xFF header's version 65535 passed).
   **Clean A/B on `Fallout_Debug.xex` (identical binary, only this check toggled): 22 → 23 DDX,
   22 common entries all byte-identical in size, 0 grew, 0 shrank.** The one recovered file is a real
   texture (`load_roulette_wheel.ddx`, 59 KB). No garbage admitted.
   ⚠ Attribution note for anyone re-running this: `TestOutput/ws45_carve` is a **pre-Item-2** baseline.
   Comparing against it shows 6 "shrinks" and a fallback jump that belong to Item 2's exact-extent work,
   not to this change. Use a same-binary A/B.
3. **Residency was being discarded on the conversion path — FIXED** (top-ranked finding of the Q1/Q2/Q3
   audit below). `CarveWriter.TryConvertAsync` built its manifest entry purely from `result.*` and returned
   before the coverage-bearing branch ran, so a partially-resident **DDX/XMA/NIF** was recorded as
   `IsPartial=false`, `ContentType="converted"`, `Notes=null`. Since DDX is converted by default and is the
   corpus's dominant carved format, this mislabelled the common case. Now `IsPartial` and the coverage note
   are carried through conversion. Two adjacent precision bugs fixed with it: coverage rendered `P0`, so
   99.6% printed as "Memory coverage: 100 %" beside `IsPartial=true`; and `BuildNotes` used `else if`, so a
   file that was both partial and repaired silently lost its "Repaired" note.

### Original defect descriptions (kept for context)
- ⚠ **`hairwavy_2.ddx` flakiness is a real carver bug, not test ordering.**
  `CarveWriter.WriteFileWithRetryAsync` appends a **GUID** on `IOException` while
  `CarveExtractor` picks dedupe suffixes with a racy `File.Exists` + `counter++`. Two parallel writers
  collide and emit `hairwavy_3d5c33cc.ddx` while **the manifest still records the intended name** —
  manifest and disk disagree. Reproduced in 2 of 4 runs.
- ⚠ **1,854 real on-disk `.ddx` (7.1%) are rejected outright by `ValidateDdxHeader`** because their
  priority byte at 0x04 is `0xFF` (every `textures/interface/endgame/endscrn_*`, among others).
  Pre-existing carver false-negative, untouched by this item.

## Ruling 1 — wire LSCR/IDLM/CAMS/MSET/CHIP: LANDED

All five types now emit. Each gained an encoder in `Plugin/Writers/Encoders/Misc/` plus the five
wiring rows (DmpRecordSource extractor · EnumerateModelsByType yield · PlannedEncoders · 
RecordEncoderRegistry), and **all five were removed from `GenericSweepEmissionExemptions`** — the M1
guard test passing with those entries gone is the proof they are routed end to end.

Three tiers, as the exploration predicted:
- **LSCR / CHIP / IDLM** needed no reader change at all: `BSStringT<char>` already resolves to real
  strings, `BOUND_DATA` already parses, and pointers already become FormIDs. Subrecords omitted by
  design where recovery would require a list/array walk (LSCR's LNAM, CHIP's DEST, IDLM's IDLA — all
  optional in the FNV schema; IDLM emits IDLC=0 so the count matches the absent array).
- **CAMS** drove the generic widening: `RuntimeGenericReader.ReadEmbeddedStruct` now returns raw
  `byte[]` for >8 B structs instead of a `"[Type, NB]"` descriptor that carried no data.
  `GenericRecordFields.TryBytes` already accepted `byte[]`, so no writer change was needed. BE→LE is
  done by the **existing** oracle — `SubrecordSchemaProcessor.ConvertWithSchema("DATA", raw, "CAMS")`,
  whose 40 B and 36 B CAMS schemas predate this work — rather than a hand-rolled swap.
- **MSET** needed a typed reader, since its six 16 B `MediaSet::MediaLayer` structs hold their name as
  a `BSStringT` *pointer* that raw bytes cannot resolve. `RuntimeMediaSetReader` follows the ASPC
  precedent exactly: it returns a `GenericEsmRecord` keyed by subrecord signature (no new model, no
  writer change), is intercepted in `RuntimeStructReader.ReadGenericRecord`, and declines rather than
  invents when a sound pointer does not resolve to a SOUN. Layer slots are positional —
  NAM2-NAM7 (names) / NAM8·NAM9·NAM0·ANAM·BNAM·CNAM (attenuation) / JNAM-ONAM (percent).

Display fallout of the widening was handled: `CLI/Show/ShowHelpers.cs` gained a `byte[]` hex-preview
arm so those fields no longer render as `System.Byte[]`, and `GenericOnlyEncoderTests`'
`Mstt_RejectsLargeStructDescriptorPlaceholder` contract was flipped from "reject descriptor" to
"accept raw bytes".

Tests: `GenericSweepWiredEncoderTests` 22/22, `RuntimeMediaSetReaderTests` + routing guard +
`GenericOnlyEncoderTests` 53/53.

---

## Round 2 final gate (2026-08-26)

- **Full default suite: 8,856 tests, 0 failed**, 8,624 passed, 232 skipped (the standard opt-in gates).
- **Full both-TFM Release build with analyzers: 0 errors, 0 warnings.**

⚠ **Build/test contention is a real hazard in this repo right now.** Concurrent sessions rebuilding
`src/DDXConv` and `src/BethesdaMultitool` wipe `obj/`+`bin/` mid-run, which surfaces as
`MSB3030 / CS0006 "metadata file could not be found"` or a missing `runtimeconfig.json` with exit 131
— and once as a **phantom single test failure** in an otherwise-green suite. All of these vanish on a
clean rebuild. Retry once before believing any of them; do not chase them as code defects. (A
13-hour agent run was lost to exactly this.)

### Still open after round 2
- The six remaining M1 types (`EFSH`, `CSNO`, `IPDS`, `RGDL`, `DOBJ`, `AMEF`) stay in
  `GenericSweepEmissionExemptions` awaiting per-type rulings. AMEF has 0 records corpus-wide.
  IPDS/CSNO/DOBJ would be EDID-only stubs; EFSH/RGDL decode but cannot be shift-probe verified.
  *(All resolved in round 3 below except AMEF, which stays dropped.)*
- M2 (LZX→BSA), held pending a real XMem-flagged archive.

*(The two DDX-adjacent carver defects that were listed here — the `CarveWriter` GUID/manifest race
and the 1,854 `.ddx` rejected on a `0xFF` priority byte — were both fixed on 2026-08-26 and are
written up ~90 lines above. The line claiming they were open was stale.)*

---

## Decisions owed by the user (nothing further will be changed without a ruling)

1. **M1 per-type wiring** — the I5 table above. Suggested wire: LSCR, IDLM, CAMS, MSET, CHIP.
   Requires widening `RuntimeGenericReader.ReadEmbeddedStruct` (>8B structs → raw bytes + BE→LE via
   the existing Conversion/Schema definitions) for CAMS/MSET/CHIP. AMEF is 0 corpus-wide (drop).
   IPDS/CSNO/DOBJ would be EDID-only stubs; EFSH/RGDL are decodable but unverifiable by shift-probe.
2. **DDX fallback rule (I1)** — flat 1.35 · per-format p95 constants · exact LZX chunk-walk · leave
   0.7. Prerequisite either way: fix the mip-count decode (`(formatDword>>16)&0xF` yields 1 on
   100% of files), which currently also makes "mips lost" unmeasurable.
3. **(c) GMST duplicate certainty** — accept "unknown" as below-bar, or fund a deep value compare.
4. **M2 (LZX→BSA wiring)** — still open, unrelated to dumps: the repo's working LZX decoder is not
   reachable from `BsaExtractor`, so XMem-flagged archives fail as "file missing" through the VFS.

---

# Round 3 — the Q1/Q2/Q3 questions (2026-08-27)

The user asked three follow-up questions: does the pipeline (Q1) follow the dump's own pointers for
non-contiguous data, (Q2) reliably detect and report gapped data, and (Q3) use corroborating
evidence in the dump to repair damaged records. Answers were "mostly, with holes". Those holes, plus
the remaining M1 types, are this round.

**Rulings taken 2026-08-26** (`AskUserQuestion`): Q3 merge → *fill only what's missing*; catalog
duplicates → *field-wise union of both*; gapped-file reporting → *hole positions + per-format check*.

## Q1a — VA correctness on non-contiguous reads: 6 sites fixed

The invariant, worth stating once because it is counter-intuitive: **`IsVaRangeCaptured(va, n)` is a
residency predicate, not a contiguity predicate.** It deliberately walks forward across region
descriptors as long as *virtual addresses* are contiguous (`MinidumpInfo.cs:390-403`); those
regions' *file offsets* need not be adjacent. So `IsVaRangeCaptured` followed by
`ReadBytes(VaToFileOffset(va), n)` **still splices foreign bytes** — the guard passes and the read
is wrong anyway. Only `RuntimeMemoryContext.ReadBytesAtVa` re-derives the file offset per region.

| Site | Was |
|---|---|
| `RecordParserContext.ReadRecordData` | flat `Accessor.ReadArray` for **every** ESM record body across 107 call sites, no VA check, `ReadArray`'s return count discarded |
| `RuntimeCellMapWalker` bucket array | no guard at all, flat read of up to 16 KB of bucket-head pointers |
| `RuntimeCellMapWalker` chain item + cell | guard present, read still flat (the invariant above) |
| `RuntimeCellObjectEnumerator` | flat 192-byte `TESObjectCELL` read from a translated offset |
| `RuntimeMemoryContext.ReadNullTerminatedAsciiString` | a VA-entry-point API that internally flat-read, so a string could run past its region into printable bytes from an unrelated allocation |

`ReadRecordData` now translates to a VA and reassembles by region, taking the **resident prefix**
rather than dropping the record (matching the truncated-compressed salvage philosophy) and counting
affected FormIDs in `NonContiguousRecordFormIds`, reported at the end of a parse. Two new primitives
support the rest: `ReadBytesAtVaInto` (VA-correct copy into a caller-owned buffer, so scan loops keep
their reuse) and `GetCapturedVaRunLength` (for reads that want "as much as is there" instead of
fail-closed). The two remaining `IsVaRangeCaptured` call sites — `DmpGapRecoveryScanner:403` and
`DmpRecoveryProbeCommand:408` — are pure residency predicates with no following read, and are
correct as they stand.

## Q1b — PDB container fields: the gap was `typeDetail`-keyed, not `Kind`-keyed

`pdb_layouts.json` has **no list or array kind**. A `BSSimpleList<T *>` arrives as
`kind:"struct", size:8` and a counted array as a bare `kind:"pointer"`, so both reached
`ReadEmbeddedStruct` and came out as an 8-byte hex dump of the `{itemPtr, nextPtr}` head. Scale: 355
container fields, 241 substantive after excluding the 114 `pSourceFiles` (load-order provenance, no
subrecord corresponds to it).

`RuntimeContainerFieldReader` now walks them, keyed on `TypeDetail`, reusing the existing
`WalkInlineBSSimpleListItemPointers` (cycle guard, 50-node budget, per-node `ReadBytesAtVa`).
Element FormTypes are resolved from the layout database's own `className` index
(`PdbStructLayouts.TryGetFormTypeByClassName`) rather than a parallel hand-kept table. IDLM's
`pIdleArray` gained a declared `{count, T**}` pairing, so **IDLA now emits with IDLC equal to the
walked array's length** — the two can no longer disagree, which is why IDLC used to be forced to 0.

## Q2 — residency reporting: intervals, not a scalar

Coverage was computed correctly and then almost entirely discarded: the `double` died at the
`WriteFileParams` boundary and survived only as a percentage inside free-text `Notes`, and only when
the file was partial. `CarveResidency` now carries the actual hole intervals — the reassembly loop
already had them, since `GetRegionsInRange` returns VA-sorted regions and the complement over
`[0, size)` is the hole set — through to `manifest.json`, which gained `coverage`, `tailTruncated`,
`holes[]` and `criticalRangeHit`.

Tail loss and interior holes are now distinct: a texture missing its last mip and one with a hole
through its mip table were previously the same `IsPartial=true`. `IGapAssessor` (an optional
capability probed with `format is`, following `IFileRepairer`/`IDumpScanner`) lets a format say
whether a hole landed somewhere structural; DDX, NIF, XMA, DDS and PNG implement it. The carve CLI
prints a partial-file row, and `ExtractionSummary` carries the aggregate that `MinidumpExtractor`
used to drop on the floor.

Two dead signals closed alongside: `CarvedFileInfo.IsTruncated` was written by the analysis scanner
and read nowhere, so the GUI showed a half-present file with a green checkmark — it now maps to a new
`ExtractionStatus.Partial`. And `ConversionResult.IsPartial` was **never assigned by any converter**,
making `"converted_partial"` dead as a converter-side signal; rather than fabricate one, its contract
is now documented and the live signal is the carver's own residency.

## Q3 — cross-source repair: 53 add-only merges became repairing merges

`MergeRuntimeRecords` `continue`d *before* the factory ran, so for any FormID the ESM supplied, the
live heap object was **never read**. Across **53 call sites** (not the 9 first reported; the overlay
variant is 14, not 2) the only evidence that could repair a struck ESM record was discarded unread.

`RecordModelUnion` is the shared primitive both rulings needed. It fills only what is genuinely
unset — null, empty string, empty collection — and **never touches a non-nullable scalar**, because
zero cannot be told from unset there. That limit is not a shortcut: it is exactly why the 14
hand-written mergers encode `!= 0` / `> float.Epsilon` per field. Those still run and still win; this
is the general fallback for the ~50 types that never got one. `Offset` and `IsBigEndian` keep the two
conventions every hand-written merger ends with.

At the catalog, `RecordCatalog` no longer discards the losing duplicate. Enumeration order was never
a quality ordering — a capture whose EditorID never resolved could beat one that named the record,
purely by arriving first. The richer capture (scored, with a resolved EditorID counting double) now
leads and the other fills what it left unset; the `catalog.duplicate-dmp-record` diagnostic gained a
`merged` key.

Finally, the one genuine cross-source repair that already existed — the gap scanner's dialogue
parentage, built from an independent topic-to-info map and gated on the two sources agreeing — was
being computed and then dropped: `DmpGapRecoveryPromoter` never read `TopicFormId`, so the repaired
parentage reached the audit CLI and nothing that emits. It now travels on `RuntimeEditorIdEntry` and
into `RuntimeDialogueInfo`, preferred only where the struct's own decode came up empty.

## The remaining M1 types — all five wired; AMEF dropped

**EFSH (1,019), RGDL (1,143), CSNO (80)** needed no new decode: each carries its payload in one
block whose runtime size matches the file schema exactly (308 / 14 / 56), and the BE-to-LE registry
already had schemas at those sizes. All three follow the `CamsEncoder` template.

⚠ **RGDL is the one deliberate divergence.** Its registered DATA schema declares the leading count
as `UInt32WordSwapped` — a quirk of what the Xbox 360 *plugin writer* put on disk, established by
comparing Xbox and PC ESMs. A compiler-laid-out `RagdollSaveStruct` in memory is a plain big-endian
u32, so running runtime bytes through the file schema would swap the halves of a value that was never
swapped. The runtime path writes the block directly (one integer, five booleans, five unused bytes)
with a plausibility ceiling on the bone count; the carve path still uses the schema, where it is
ground truth. `Rgdl_EmitsGeneralDataAndBothRequiredReferences` is the pin.

**IPDS (1,394) and DOBJ (32) were genuinely unreachable** — not deferred, unreachable. Their sole
payload fields exported as `size:0, kind:"unknown"` and were dropped by `GetReadableFields` before
any reader ran. Root cause: `PdbAnalyzer` never parsed `LF_ARRAY` leaves, so every fixed-size array
member in the database resolved to "unknown".

### Regenerating `pdb_layouts.json` — the gate, and the trap

`PdbAnalyzer` gained `LF_ARRAY` parsing (element type + total byte length) and the database was
regenerated. The plan's hard gate — *abort if any existing field's offset, size or kind moves* — was
applied by diffing old against new:

- **0 existing fields changed, 0 removed, 0 added. 54 previously-`unknown` fields resolved.**

⚠ **The first regeneration attempt failed that gate, and the reason is worth recording.** Run
against `Sample/PDB/Proto/Fallout_Release_MemDebug/types_full.txt` — the obvious choice, since the
JSON's `source` field says `Fallout_Release_MemDebug.pdb` — it reported WEAP growing 920 to 924 with
a new trailing `pLastAmmo`. That is a real difference between two builds that share a PDB *name*.
The database was actually generated from **`Sample/PDB/Aug_22_MemDebug/types_full.txt`**, confirmed
by the type indices: the old JSON's unresolved `0x0001A023` / `0x0002227B` exist verbatim in the
Aug-22 dump and not in the Proto one. Regenerating from that source gave the clean diff above.
`StaticLayoutOffsetParityTests` catches the mistake immediately (it pins WEAP at 920 with a comment
naming Aug 22), but the diff gate caught it first — which is the point of having one.

What the 54 unlock beyond IPDS/DOBJ: RACE's head/body model and texture lists and mean face coords
(10 fields), WTHR's cloud textures and colour data (9), ARMO/ARMA/CLOT's biped/world models and icons
(4 each), TXST's texture slots, IMAD's interpolators, BPTD's part array, WATR's weather controls,
CSNO's model and reel-texture arrays. Only the two needed for this round are wired; the rest are now
*readable*, which is the prerequisite.

Both new arrays are **positional slot tables** — DOBJ's 34 default objects, IPDS's 12 materials —
where the index is the meaning and the file subrecord is read by position. An unresolvable slot stays
a NULL FormID (which xEdit's schema explicitly permits) rather than being compacted out, since
dropping one would shift every later entry onto the wrong meaning. A non-zero slot with an impossible
load-order byte rejects the whole table, because that means the read was misaligned.

## Round 3 exemption list

`GenericSweepEmissionExemptions` now holds only genuine non-records: **AMEF** (0 records across all
32 corpus dumps), the three types absent from the FNV file format (SKIL/CLOT/LVSP), the file header,
and the placed-ref/cell-child types that route elsewhere. Every FormType the generic sweep can read
and that has a record block in the FNV schema is now emitted.

## Round 3 final gate (2026-08-27)

- **Full default suite: 8,924 tests, 0 failed** (8,692 passed, 232 skipped).
- **Full both-TFM Release build with analyzers: 0 errors, 0 warnings.**
- **`RUN_BUCKET_B=1` sweep: 8,930 total, 0 failed.** It was **6** at the start of this round: three
  were the `BsaHeader` regression below, and the remaining three are fixed in Round 3b.
- Conversion smoke on `Fallout_Release_Beta.xex44.dmp` against the PC master: exit 0, 13.7 MB
  plugin, 18,411 records parsed. The only new-type diagnostic is one honest RGDL warning
  (`0x001403A8 preview actor did not resolve — omitting XNAM`), which is the encoder declining
  rather than inventing.

### ⚠ A round-2 regression the Bucket-B sweep caught — the XMem flag is version-scoped

Round 2's "fail loudly instead of silently" change (Ruling 4) added
`BsaHeader.UsesXMemCodec => ArchiveFlags.HasFlag(BsaArchiveFlags.XMemCodec)`, reading the 0x200 bit
unconditionally. **Oblivion's v103 archives set that bit with no such meaning** — `Oblivion -
Meshes.bsa` has flags `0x787`, which includes 0x200 — so every compressed Oblivion entry began
throwing "uses the XMem/LZX codec", taking the entire Oblivion mesh corpus offline. Three real-asset
tests failed on it (`NifTextureEffectRetailTests`, `OblivionHavokCollisionIntegrationTests`,
`OblivionNifBrowserTextureIntegrationTests`).

This is **exactly the trap the property three lines above already documents**: `EmbedFileNames` is
gated `Version >= 104` with a comment naming the same archive and the same flags word. `UsesXMemCodec`
is now gated identically, and `ExtractFile_V103ArchiveSettingThe0x200Bit_IsNotTreatedAsXMem` pins it.

Two lessons worth keeping: a new BSA flag read needs a version gate unless proven otherwise, and
**the default suite cannot catch this class** — it is real-asset-only, so a Bucket-B sweep belongs in
any round that touches archive parsing.

### Three pre-existing Bucket-B failures, surfaced but not caused by this round — FIXED in Round 3b

Proven independent: with the short-read handling in `ReadRecordBytes` temporarily reverted to the
old `dataSize` semantics, all three still failed. They are opt-in-only (not in CI, not in the
default suite), so they have had room to drift.

- `ProfileParityTests ... ForFnv("PACK")` — 5 of 4,163 packages diverge.
- `ProfileParityTests ... ForFnv("DIAL")` — 5 of 18,215 topics diverge.
- `OblivionSchemaParseIntegrationTests.Oblivion_Dialogue_Is_Surfaced_For_The_Dialogue_Tab` —
  0 Oblivion INFOs attribute a speaker via CTDA.

The two parity failures are **one defect class**, and the direction matters: in every case the
**typed builder falls back to a default while the schema profile decodes the real value** — PACK
`germHQAlarmFind` types as "AI Package" instead of "Find", `IntroMovieBrotherhoodDefault` as
"AI Package" instead of "Travel"; DIAL `ANY` types as "Topic" instead of "Conversation". The profile
is right and the typed builder is losing a type discriminator on a small set of records. Fixing it
changes typed-model output, so it is left for its own round rather than folded in here.

---

## Round 3b — the three remaining Bucket-B failures (2026-08-27)

All three turned out to be **one defect shape**, and it is worth naming because it is easy to write
and impossible to notice: a `when sub.DataLength >= N` guard written against the **longest** form of
a subrecord. Every shipping record that stops after the required members falls straight through the
switch, keeps its default value, and produces no error anywhere.

xEdit states the real rule explicitly — the trailing argument of `wbStruct` is the
**required-element count**:

| Subrecord | xEdit declaration | Required | Guard was | Retail ships |
|---|---|---|---|---|
| DIAL `DATA` | `wbStruct(DATA, '', [Type, Flags], cpNormal, True, nil, 1)` | Type only | `>= 2` | **1 byte** on the engine-reserved topics |
| PACK `PKDT` | `wbStruct(PKDT, 'General', […], cpNormal, True, nil, 2)` | General Flags + Type | `>= 10` | **8 bytes** |
| PACK `PKPT` | full form adds `Unused(1)` | Repeatable only | `>= 2` | **1 byte** |

Each guard is now the true minimum, with every later member read only when present.

**What each cost.** DIAL: `ANY`, `SPELLHELP`, `ServiceRefusal` and friends typed as "Topic" instead
of "Conversation" — their `DATA` is a single byte. PACK: `germHQAlarmFind` and
`IntroMovieBrotherhoodDefault` typed as the "AI Package" fallback instead of Find/Travel. PKPT:
`mvsRaiderTowerPatrolA/B` reported as non-repeating patrols. In every case the **schema profile was
right and the typed builder was wrong**, which is exactly what `ProfileParityTests` exists to catch.

⚠ The PKPT one is the sharpest: `SubrecordSchemaRegistry` **already carried a PKPT/PACK schema at
length 1**, with a comment saying so. The BE→LE converter knew the record could be one byte while the
typed parser next door did not.

### Oblivion CTDA is 24 bytes, not 20

`OblivionDialogueExtractor` matched `case "CTDA" when sub.Data.Length == 20`, so on the shipping game
the case never fired: no condition was parsed and **not one INFO attributed a speaker**. The 20-byte
figure is the pre-1.1 layout (and the older `CTDT` subrecord); patched Oblivion appends 4 unused
bytes. Measured on retail `Oblivion.esm`: **20,000 of 20,000 sampled CTDA bodies are 24 bytes and not
one is 20.** Everything the parser reads lives in the first 20, so both widths decode identically —
the fix is purely to admit both. `CtdaParser.IsSupportedBodyLength(Oblivion, …)` carried the same
wrong constant and would have reported `game_width_mismatch` for every real Oblivion condition; it is
corrected too.

The commit that introduced the feature (`7201e569`) states "Oblivion CTDA differs from FNV: 20
bytes" and claims 90.8% speaker coverage on the real master — so it evidently worked against
whatever it was measured on. Either way, retail today is 24 and the test now passes at 24.

`SubrecordLengthToleranceTests` pins all four rules synthetically, including both CTDA widths.

---

## Round 3 — measured recovery delta (2026-08-27)

A true A/B: `HEAD` (`6dd0b2cf`, which already contains rounds 1–2) built in a detached worktree,
against the current tree, converting the same dumps with the same master.

| Dump | Before | After | Delta |
|---|---|---|---|
| `Fallout_Release_Beta.xex44.dmp` (late build) | 64 types / 97,012 records | 65 / **97,014** | **+1 type (RGDL), +2 records** |
| `Fallout_Debug.xex2.dmp` (early build) | 60 types / 67,680 records | 61 / **67,681** | **+1 type (RGDL), +1 record**; the one IDLM grew 68 B → 78 B |

⚠ **The DMP→ESM recovery gain is marginal, and the reason matters.** `dmp to-esm` emits an *overlay*
against the PC master. Almost every captured record of the newly-wired types carries a **master
FormID** — xex44 holds 185 IDLM, 152 LSCR, 229 CAMS, 48 IPDS, 37 RGDL, 32 EFSH, 5 CHIP, 1 DOBJ, and
the master has all of them — so the overlay correctly keeps master's copy instead of duplicating a
reconstruction that is necessarily lossier. **Wiring a type only pays off where the dump holds
content the master does not.** On this corpus these types are almost entirely retail content.

What the wiring *did* buy, verified on real output:
- **RGDL emits at all** (2 records on xex44, 1 on the Debug dump) — it produced nothing before.
- **IDLM now emits `IDLA`.** `QJMelissaIdleMarker` (0x01001B80) went `EDID·OBND·IDLF·IDLC·IDLT` →
  `EDID·OBND·IDLF·IDLC·**IDLA**·IDLT`. That is the container walker working end to end: the array
  behind `BGSIdleCollection.pIdleArray` was previously unreachable and IDLC was forced to 0.
- EFSH / CSNO / IPDS / DOBJ / CAMS / CHIP / MSET emitted **zero** on both dumps, for the master-FormID
  reason above — not because the routing fails.

**The VA-contiguity fix affected 0 records on both dumps.** `NonContiguousRecordFormIds` stayed empty:
no ESM record body in either dump straddles a VA-disjoint boundary. The bug was real and the fix is
correct, but on this corpus it is insurance, not recovery. Recording that plainly, as the plan asked.

### Where the round's measurable value actually landed

Not in the dump→plugin path — in correctness of what was already being read:

- **The entire Oblivion mesh corpus, restored.** Round 2's unconditional 0x200 flag read made every
  compressed Oblivion BSA entry throw. From "nothing loads" to "loads".
- **Oblivion dialogue speaker attribution: 0 → thousands.** The 24-vs-20-byte CTDA gate meant no
  Oblivion INFO had ever attributed a speaker on the shipping game.
- **Field-level corrections across ~22k retail FNV records**: PACK type over 4,163 packages, DIAL type
  over 18,215 topics, PKPT repeatable — each silently defaulting before.

The cross-source merge (Q3) and residency intervals (Q2) are not visible in these numbers by
construction: the merge only acts where two captures of one record disagree, and residency is a
reporting change. Neither has been measured against a case that exercises it.

---

## Round 3d — Phase 2 completed: nested struct layouts (2026-08-27)

Four Phase-2 items were left open at the end of round 3 — the `TESTextureList` arm (43 fields), the
`TEX_SWAP` arm (28 fields), LSCR's `LNAM`, and CHIP's `DEST`. They looked like four separate gaps.
They were one:

> **`tools/PdbAnalyzer layouts` exported only the 116 FormType classes.** A reader could see that
> `TESObjectSTAT.TextureSwapList` is a `BSSimpleList<TEX_SWAP *>` and had no way whatsoever to learn
> what a `TEX_SWAP` contains. Every nested payload in the database was therefore unreachable by
> construction, no matter what the reader did.

### The export now emits auxiliary struct layouts

`ExportLayoutsCommand` walks out from the record types' fields, flattens every **non-record** struct
they name, then the structs those name, to a depth of 3. Result: **329 auxiliary structs**, and the
layout file grows 562 KB → 1.14 MB.

⚠ **The diff gate held exactly as the plan demanded**: all 116 types, every field offset, size and
kind **unchanged**. The only movement anywhere was two fields *gaining* a name they previously
lacked (`TESFile.m_pMasterPtrs` and `BGSIdleMarker.pIdleArray` went `typeDetail: null` →
`"TESFile *"` / `"TESIdleForm *"`), because pointer-element resolution is now iterated to a fixed
point and can therefore name a `T**`. Strictly additive.

Two other things were corrected while in there:
- `source` was the hard-coded string `"Fallout_Release_MemDebug.pdb"`, which is ambiguous between
  the Proto and Aug-22 dumps — the exact ambiguity that made a round-3 regeneration silently move
  WEAP from 920 to 924 bytes. It now records the file the export actually read, plus `sourcePath`.
- `RuntimeContainerFieldReader`'s hard-coded `TESTexture` = 12 bytes / name at +4 (previously
  justified by inference across the 28 types carrying it as a base class) is now read from the
  exported layout, which states both outright. The constants remain as the fallback and agree.

### What the four arms turned out to be

Every one is a 1:1 match with its file subrecord — none needed reinterpretation:

| Runtime struct | Layout | Subrecord | Fit |
|---|---|---|---|
| `TEX_SWAP` (136 B) | `pNewTexture`@0 → BGSTextureSet, `iGeomIndex`@4, `pGeomName`@8 (inline `char[128]`) | **MODS** | exactly `AlternateTextureEntry(ShapeName, TextureSetFormId, Index)` |
| `LOAD_FORM_DATA` (12 B) | `iFormID`@0, `iWorldID`@4, `iCellKey`@8 | **LNAM** | three words, same meanings, same order |
| `DestructibleObjectData` (20 B) | `iHealth`@0, `cNumStages`@4, `cFlags`@5, `pStagesArray`@8 | **DEST** (8 B) | first six bytes align 1:1, two pad bytes in both |
| `DestructibleObjectStage` (24 B) | damage stage, health %, flags, self-damage, explosion, debris, debris count, replacement model | **DSTD** (20 B) | every member has a home; DSTD's `Index` is the array position |
| `TESTextureList` (8 B) | `cTextureCount`@0, `pTextureOffsetArray`@4 → `BSFileEntry **` | **MODT** | each `BSFileEntry` opens with the 8-byte `BSHash` MODT stores |

`DestructibleObjectStage` being resolvable is what made CHIP's DEST *safe* to write. DEST's count is
not decorative — the engine sizes its stage array from it and fills the slots from the DSTD blocks
that follow — so a header emitted with a captured count and no stages leaves that array unpopulated.
Header and stages now travel together, and the count is written from the stages actually emitted.
Same rule IDLM's IDLC already follows against its IDLA.

**MODT is read but deliberately not written.** The hashes are of the source build's texture paths,
and the Xbox and PC builds do not share them — different extensions, different archives. The
file-conversion path already byte-preserves MODT for this reason
(`EsmCoverageAnalyzer`: *"byte-preserved because PC hash contents are source-specific"*). The walk
gives a reader the texture count and the hashes; it does not give an encoder bytes worth writing.

### Verified against retail, not just asserted

MSTT `Car02NoKD` (0x01004135), a **new** record, recovered from `xex44`:

```
DEST  health=325 stages=6 flags=0xCE
DSTD  idx=0 health%=93 damageStage=0 selfDmg=0
DSTD  idx=1 health%=89 damageStage=1 selfDmg=2
DSTD  idx=2 health%=83 damageStage=2 selfDmg=10 expl=0x0000446C
DSTD  idx=3 health%=75 damageStage=3 selfDmg=10
DSTD  idx=4 health%=65 damageStage=4 selfDmg=1  expl=0x000B2959  DMDL Vehicles\CarHulk02.NIF
DSTD  idx=5 health%=0  damageStage=5
```

Retail `Sample/ESM/pc_final/FalloutNV.esm` carries `Car02` as **`health=325 stages=6 flags=0xCE`** —
byte-for-byte identical. The odd-looking `0xCE` is what retail actually ships (its DEST flags byte
carries more than the one documented bit; the master's Car01/Car03/Car10 are 0xD9/0xCD/0x7B).

### Reach: reading is much wider than writing

This is the distinction that matters, and it is why the round-3 emission delta looked so thin.

**Read (browse) reach** — `RuntimeGenericReader` populates `GenericEsmRecord.Fields`, which is what
`show` / `list` / the GUI display, whether or not any encoder consumes it. Candidate records in
`xex44` (records of a type that carries the member; the resolved count is at most this, since many
instances hold an empty list):

| Field | Record types | Candidate records in xex44 |
|---|---|---|
| `TESModel.TextureList` → texture hashes | 22 | 3,172 |
| `TESModelTextureSwap.TextureSwapList` → alternate textures | 13 | 728 |
| `BGSDestructibleObjectForm.pData` → destruction block | 10 | 482 |
| `TESLoadScreen.LoadFormList` → locations | 1 | 152 |

Before this round each of those rendered as an 8-byte hex string or a raw Xbox VA.

**Write (emission) reach is narrower and bounded by two things**, both enumerable:
1. Only 6 of the 13 relevant encoders take a `GenericEsmRecord` (ADDN, ANIO, CHIP, FLOR, MSTT,
   TACT); the rest take typed models built by the ESM carve path.
2. The subrecord must exist in the record's xEdit schema. `wbGenericModel` covers MODL/MODB/MODT/
   **MODS**/MODD, so MODS is legal wherever that helper is used. `wbDEST` is used by exactly 21
   records — and **FLOR is not one of them**, so FLOR gets neither DEST nor MODS despite `TESFlora`
   carrying both members at runtime. (FLOR is absent from xEdit's FNV *and* FO3 definitions
   entirely — both only have a commented-out group-order line.)

Wired: MODS on ADDN/ANIO/CHIP/MSTT/TACT, DEST+DSTD+DSTF+DMDL on CHIP/MSTT/TACT, LNAM on LSCR.
Measured on `xex44`: **14 records now carry a full destruction block** (13 MSTT, 1 TACT; 69 stages,
12 with replacement models) where the encoders previously carried a written "deliberately not
emitted" comment. No MODS or LNAM appeared — every captured `TextureSwapList` and `LoadFormList` in
this dump is an empty list, which the walker correctly declines rather than inventing.

⚠ **The remaining reach gap is the specialized readers.** 21 of the 43 MODT owners, 15 of the 28
MODS owners and 16 of the 26 DEST owners have hand-written readers (`RuntimeItemReader`,
`RuntimeWorldObjectReader`, `RuntimeActorReader`, …) that bypass `RuntimeGenericReader` entirely, so
they see none of this. WEAP, ARMO, STAT, MISC, DOOR, FURN, CONT, NPC_, CREA and friends are all in
that set. That is the single largest remaining item in this area and it is a known, enumerable list,
not an unknown.

### `GetFieldProbeCheck` — corrected rather than extended

The plan asked for this switch to be extended in step with the new arms. Extending it naively would
have made the layout-shift probe **worse**: `ScoreSample` adds every declared field to the
denominator and only a passing one to the numerator, so a check that fails at *every* candidate
shift dilutes the margin the caller gates on (`Margin >= 2`). A `TESTextureList`'s first word is a
count and a `TEX_SWAP` node is a plain allocation — neither head is a form pointer, so any available
check on them would be pure noise.

What the switch got instead:
- `BSSimpleList<X *>` where X is a record class → `PointerToFormType(X)`, which is strictly stronger
  than nothing and rejects a pointer that lands on a form of the wrong type.
- ⚠ **A pre-existing defect fixed**: the bare `"pointer" => PointerToForm` arm applied to *every*
  pointer, including ones whose target the layout database knows is not a record class —
  `DestructibleObjectData`, `BaseProcess`, `NiNode`. Those can never resolve to a TESForm, so that
  check was diluting the probe on every type carrying one. Pointers to known record classes now use
  `PointerToFormType`; pointers to known non-record structs get no check at all.

### Phase 7 lead closed: the LSCR 153 → 152 drop

The census puts the drop between **2009-12-15 (`xex4`) and 2010-01-04 (`xex5`)** — a much narrower
window than the "Dec 2009 → Mar 2010" the plan assumed. Diffing the two dumps' LSCR EditorIDs shows
the premise was also wrong: **it is not one load screen being cut.** It is the Big Guns / Small Guns
→ Guns / Explosives skill merge, and the FormIDs say exactly what happened to each:

| FormID | 2009-12-15 | 2010-01-04 | Retail | |
|---|---|---|---|---|
| `0x0002B3BE` | SmallGunsLoadScreen09 | GunsLoadScreen09 | present | renamed in place |
| `0x001133AD` | SmallGunsLoadScreen10 | GunsLoadScreen10 | present | renamed in place |
| `0x001209CE` | — | ExplosivesLoadScreen04 | present | added |
| **`0x0002186E`** | **BigGunsLoadScreen02** | *gone* | **absent** | **cut** |
| **`0x001133AC`** | **BigGunsLoadScreen03** | *gone* | **absent** | **cut** |

Four removed, three added, net −1. Retail's 208 LSCRs contain **no** BigGuns or SmallGuns screen at
all. Both cut screens are recoverable from `xex4` and both used
`interface\loading\loading_magazine03.dds`.

⚠ Noted while pulling them: on the Dec-2009 build `pLoadScreenType` comes back as `0x20736B69` /
`0x6420746F`, which is ASCII `" ski"` / `"d to"`, and `cDescText` does not appear at all.

> **CORRECTION (round 3e).** This was written up as an early-build *layout shift*. It is not one.
> `TESLoadScreen` is identical in the Proto, July_RB and Aug_22 PDBs, and the real cause is a
> weak-validation defect in `ReadPointerField` that affected **248 pointer fields across the whole
> layout database**, not LSCR and not early builds. See round 3e below. The cut-screen finding above
> is unaffected — EditorID and TextureName were always read correctly.

### Gate

Default suite **8,955 / 0 failed** (232 skipped) — up from 8,930, the 25 new tests being the
difference. `RUN_BUCKET_B=1` sweep **8,955 / 0 failed** (52 skipped), 7m58s. New tests:
`RuntimeContainerFieldReaderTests` gains 9 (including a layout pin that fails with one clear message
if a regeneration moves any of the five nested structs) and `NestedPayloadSubrecordTests` adds 7,
with the MODS writer checked against `AlternateTextureParser` as its oracle rather than against
itself.

---

## Round 3e — full closure, the specialized-reader gap, and the LSCR misread (2026-08-27)

Three follow-ups from round 3d, all raised by the user.

### 1. The auxiliary-struct walk is now the full transitive closure

Round 3d capped the reference walk at depth 3 out of a worry that the Ni* / actor-process graph
would drag in the whole engine. It does not: **the walk terminates on its own at depth 9 with 449
structs**, because the `visited` set already makes each struct considered exactly once — that is the
cycle guard, and it is what makes an unbounded walk finite on a graph full of back-references.

Cost of removing the cap: **329 → 449 structs, 1.14 → 1.39 MB**. The depth counter that remains is a
backstop at 64, several times the real closure depth, and exists only so a malformed dump cannot
spin; a run that reaches it is reporting a bad input, not a tuning problem.

⚠ Diff gate held again: all 116 record types byte-identical. The 120 newly reachable structs include
exactly the ones later work will want — `NiSkinData`, `NiSkinPartition::Partition`, `NiTriBasedGeom`,
`InventoryChanges`, `BSShaderPPLightingProperty`, `hkAabb`.

### 2. The specialized-reader gap is closed

Round 3d's honest caveat was that 21 of the 43 MODT owners, 15 of 28 MODS and 16 of 26 DEST are
routed to hand-written readers that never call `RuntimeGenericReader`, so none of them saw any of
this. That was the largest remaining item and it is now done.

**Why a sweep rather than ~20 reader edits.** These three members sit on engine *base* classes —
`TESModel.TextureList`, `TESModelTextureSwap.TextureSwapList`,
`BGSDestructibleObjectForm.pData` — so which record types carry them is decided by C++ inheritance
and cuts straight across the specialized/generic split. Editing twenty readers would put the same
block in twenty places and every reader added later would silently omit it.

The shape:
- `RuntimeGenericReader.ResolveStruct` extracted from `ReadGenericRecord`, so the interior-base /
  VA-preference / layout-shift logic has exactly one implementation. New
  `ReadNestedPayloads(entry)` reuses it and is reachable for **any** FormType.
- `PdbStructLayouts.CarriesNestedPayload(formType)` — derived from the layout database, not a
  hand-listed set — so the sweep costs a set lookup on the FormTypes that carry none.
- `RuntimeNestedPayloadHandler` sweeps the runtime form table once, mirroring
  `AlternateTextureHandler` on the ESM side.
- Results land in `RecordCollection` side-indexes (`AlternateTexturesByFormId` — the existing one —
  plus new `DestructionByFormId` and `TextureHashesByFormId`), so one consumer path serves both
  sources and no typed model grows three properties it rarely wants. Runtime only *fills*: an entry
  the ESM already supplied is left alone, per the cross-source merge ruling.
- Presented once in `RecordDetailPresenter` (which feeds CLI `show` **and** the GUI record browser)
  and once in `ShowHelpers` for the generic renderer.

**Measured on `xex44`:**

```
[Semantic Parse] Recovered nested payloads from 3788 record(s): 177 destruction block(s),
422 alternate-texture set(s), 3189 texture-hash list(s); 3483 from specialized-reader types.
```

**3,483 of 3,788 — 92% — come from FormTypes the generic path can never reach.**

Verified against retail on a specialized type, `STAT 0x000A473A 1stPersonCowboyRepeater`
(`RuntimeWorldObjectReader`):

| | Retail `FalloutNV.esm` | Recovered from the dump |
|---|---|---|
| entries | 7 | **7** |
| texture set | `1stPersonCowboyRepeaterTexture` (0x000A4733) on all | **same TXST on all** |
| node names | `##Trigger:0` `##LeverBolt:0` `##BoltLock:0` `##CRHammer:0` `##ReloadCap:0` `##CRLever:0` `CowboyRepeater:0` | `##BoltLock:0` `##Bolt:0` `##Trigger:0` `##Hammer:0` `##LRLever:0` `##ReloadCap:0` `CowboyRepeater:0` |

⚑ The differing names are a finding in their own right: the proto NIF used `##Bolt` / `##Hammer` /
`##LRLever` where retail uses `##LeverBolt` / `##CRHammer` / `##CRLever`. The 3D indices are
contiguous 0-6 in the proto and sparse (0,1,2,3,6,8,9) in retail, consistent with nodes being added
to the mesh later.

⚠ Still bounded on the **write** side, unchanged from 3d: only 6 encoders take a `GenericEsmRecord`,
and the subrecord must exist in the record's xEdit schema. Reading is now complete; emitting is not,
and that asymmetry is deliberate.

### 3. The LSCR "layout shift" was not a layout shift

Round 3d recorded that early-build LSCR reads looked layout-shifted, on the evidence that
`pLoadScreenType` came back as `0x20736B69` (ASCII `" ski"`). **That diagnosis was wrong**, and the
real cause is worse and much more general.

The layouts rule it out: `TESLoadScreen` is **identical in the Proto, July_RB and Aug_22 PDBs** —
size 80, `LoadFormList`@60, `pLoadScreenType`@68, `cDescText`@72. Nothing moved between builds.
And LSCT has **zero records in every dump in the corpus**, so `pLoadScreenType` is null in practice
everywhere; a correct read returns nothing.

The actual defect is in `RuntimeGenericReader.ReadPointerField`: it called the **untyped**
`FollowPointerToFormId`, which accepts any pointer into captured memory whose byte at +4 is `<= 200`
and whose word at +12 is non-zero. **Every ASCII character satisfies the first and most text
satisfies the second**, so a stale pointer landing in a string returns that string's bytes as a
"FormID" — which is exactly what `0x20736B69` is. The layout had named the target class
(`TESLoadScreenType`) the whole time and nothing was reading it.

Fixed: when the layout names a class the database knows, demand that FormType and return null on
failure — a pointer declared as a record class that does not resolve to one is a misread, not a
value. Untyped pointers keep the raw VA, where it still has diagnostic weight.

⚠ **This is not an LSCR fix.** **248 of the 535 pointer fields** in the layout database name a
record class — WEAP alone has 31, NPC_ 10, TACT 8, CREA 8, WRLD 8 — and every one of them was
accepting this class of false positive.

**Regression-checked, because a stricter check could have cost real references.** Full `xex44`
conversion before and after is **byte-identical**: 97,014 records, 13,743,492 bytes, 78,727 placed
refs, 1,443 script refs, 692 SCPT object/variable refs, 164 package target/location refs, 5,662
actor package refs, semantic check clean. Not one legitimate resolution was lost; only false
positives went away.

Two related repairs while in there:
- `TryCorrectShift` looked for `cModel` or `cFullName` as its validator and gave up when a type had
  neither — so LSCR, GLOB, LSCT and every other type without those two members got **no per-record
  shift correction at all**. It now falls back to any non-`TESForm` `BSStringT` member (LSCR has
  two).
- `cDescText` being absent on the Dec-2009 LSCR is not a defect: `lFileOffset` is 0 and the
  description string simply is not resident in that capture. On `xex44` it is, and it reads fine.

### Gate

Default suite **8,984 / 0 failed** (234 skipped). Full both-TFM Release build with analyzers
**0 errors**. New tests: `RuntimeNestedPayloadSweepTests` (the specialized/generic split, the
typed-pointer decline, and that an untyped pointer still keeps its raw value).

---

## Round 4 — specialized-reader VA reassembly closure (2026-08-27)

T1 from the round-4 handoff is complete. The handoff's literal inventory was correct: **24 flat
fixed-struct reads across 12 specialized reader files** bypassed VA reassembly. The surrounding
audit found that this was not a 24-call problem, though. The same contract leak appeared in
top-level reads hidden behind helpers, fixed windows at known VAs, inline list-head reads, and
bounded arrays or pointer dereferences.

The final scope is **116 audited reader read sites across 27 files**:

- 30 entry/top-level fixed reads;
- 62 fixed windows whose source VA was already known;
- 7 inline member/list-head reads, now taken from the already-stitched parent buffer; and
- 17 bounded arrays or pointer dereferences.

There are now **zero flat `ReadBytes` calls in runtime readers outside `Generic/**` and
`Scanning/**`**. The two raw `ReadArray` calls left in `RuntimeRefrHeapSweep` are intentional: that
scanner reads one capture region at a time and reads a validated successor as a separate segment.
The generic path already uses `ResolveStruct`; its flat fallback is retained only for synthetic
contexts with no region map. Scanners remain region/chunk-oriented and were outside T1.

### Contract repairs

`RuntimeMemoryContext` now owns the rules rather than leaving each reader to approximate them:

- `ReadTesFormBytes` treats a retained `TesFormPointer` as authoritative, otherwise maps the
  retained file offset back to a VA, and reads through `ReadBytesAtVa`. It uses flat file access
  only when there is no region map, preserving the synthetic-test contract. An unreadable retained
  pointer does **not** fall back to a stale file offset.
- `ReadBytesAtVa(uint, count)` sign-extends Xbox module addresses through `VaToLong`; this closes a
  quieter bug in direct `uint` call sites where `0x82xxxxxx` would otherwise be interpreted as a
  positive 64-bit address.
- `ReadBytes` and `ReadBytesAtVa` reject negative counts and overflow-prone ranges before allocating
  or touching the accessor.
- Embedded `BSStringT` headers are read from the stitched struct buffer. File-base overloads map the
  struct base to a VA and add the member offset in VA space. The string payload is then followed by
  VA as before. This closes the second-order version of the defect: stitching the struct but then
  flat-reading its eight-byte string header would simply reintroduce the same boundary bug.

An independent review caught one migration regression before the gates: AVIF originally requested
only the 16-byte TESForm header while its three buffered `BSStringT` headers live at offsets 44, 64,
and 76. It now requests exactly **84 bytes**, and a synthetic two-fragment test recovers all three
strings with the second fragment VA-adjacent but file-disjoint. The review then checked all 17
pointer/array migrations at their original indirection levels and found no remaining defect.

### Real-corpus measurement

The reproducible audit artifact is under `artifacts/dmp-audit/round4-t1/` (ignored by Git, retained
in this workspace). Run it from the repository root:

```powershell
pwsh -NoProfile -File artifacts/dmp-audit/round4-t1/Invoke-T1TopologyAudit.ps1
```

It parsed **50 dumps / 9,137,881,518 bytes / 161,537 regions**. Across the full region topology:

- **152,812** VA-adjacent pairs were also file-adjacent;
- **0** VA-adjacent pairs were file-disjoint;
- **8,675** file-adjacent pairs had a VA gap; and
- there were **0** duplicate VA starts, overlaps, or adjacency ambiguities.

Therefore the handoff's exact byte-mismatch shape is structurally absent from this retained corpus:
no captured VA-contiguous boundary has disjoint backing file offsets. The unsafe implementation was
still capable of a different corruption mode: at any of the 8,675 file-contiguous/VA-gap boundaries,
a flat read could silently continue into unrelated captured memory where a VA-safe read must fail.

Three unique dumps also have retained `runtime_editorids.csv` exports. Their **165,783 rows** yielded
**63,135 representative T1 candidate ranges**: 63,066 stayed inside one region and 69 crossed a
region boundary; all 69 produced identical flat and VA bytes, with zero gaps, unmapped starts, or
mismatches. This is representative rather than an exact invocation trace: the exports cannot
enumerate pointer-only `LoadedLandData` targets or INFO objects discovered through DIAL lists. The
artifact records those limits in `summary.json` rather than turning the zero into a stronger claim.

### Gate

Focused VA-contract and specialized-reader tests **57 / 0 failed**. Default suite **9,013 / 0
failed** (234 skipped). `RUN_BUCKET_B=1` sweep **9,013 / 0 failed** (52 skipped), 7m12.6s. Full
both-TFM Release build with analyzers **0 errors, 2 warnings** — the one pre-existing
`GeometryArenaAllocator.cs:277` warning repeated for both TFMs.

The final `xex44` DMP-to-ESM conversion remained at **97,014 records / 13,743,492 bytes**, with
84,145 semantic input records, 3,788 recovered nested payload owners (including 422 alternate-
texture sets), and a clean semantic check. The output is
`TestOutput/round4_t1_20260827_xex44/out.esp`; its parent directory must exist before invoking the
CLI.

New boundary coverage lives in `RuntimeSpecializedReaderVaRunTests` (AMMO, direct-VA INFO, loaded
LAND data, the AMMO probe, and all three AVIF strings). `RuntimeMemoryContextTests` covers stitched
reads, gaps, pointer authority, no-map fallback, signed module pointers, negative/overflow inputs,
and both file-base and buffered `BSStringT` behavior. `RuntimeDialogueConditionReaderTests` was
updated to exercise the stitched-buffer contract.

---

## Round 5 — closing the round-4 handoff targets (2026-08-28/29)

Round 4 left six ranked targets open (T2–T7). All are now settled, three of them as **real defects**
rather than the "probably fine" the handoff expected.

### T6 — the typed-pointer narrowing was rejecting correct answers (DEFECT, fixed)

Round 4's own assessment was that it "could not find such a case" of a pointer declared as class `X`
legitimately holding a derived class, and honestly called that an absence-of-evidence argument. It
was wrong, and the argument can be replaced with an enumeration.

The layout database already carries the inheritance graph: every flattened field records the `Owner`
that declared it, so a record class's owner set *is* its ancestry. Reading it out gives five record
classes that are an ancestor of another record class:

| Base | Record subclasses |
|---|---|
| `TESObjectREFR` (REFR) | ACHR, ACRE, PMIS, PGRE, PBEA, PFLA |
| `TESObjectACTI` (ACTI) | FLOR, FURN, TACT, TERM |
| `TESObjectMISC` (MISC) | COBJ, KEYM |
| `TESObjectSTAT` (STAT) | MSTT |
| `TESObjectARMO` (ARMO) | ARMA |

**25 of the 248 typed pointer fields name one of those bases** — `MissileProjectile.pShooter`,
`GrenadeProjectile.pDesiredTarget`, `MobileObject.pTalkingActivator`, `BGSCameraShot.pTargetRef`,
`BGSTalkingActivator.pTempRef`, `MediaLocationController.pAudioMarker`, and WEAP's eight
`p1stPerson*Object` fields. C++ pointer assignment is covariant, so `TESObjectREFR* pShooter` holds a
`Character` (ACHR) in practice and a bare REFR essentially never — **demanding the declared class's
own FormType rejected the correct answer.**

Measured by running both checks side by side and logging every read where the strict check failed and
the covariant one succeeded:

| Dump | Reads recovered only under covariance |
|---|---|
| `Fallout_Release_Beta.xex44` | 1 — `BGSCameraShot.pTargetRef` |
| `Fallout_Release_Beta.xex21` | 16 — `MobileObject.pTalkingActivator` |
| `Fallout_Debug.xex2` | 16 — `MobileObject.pTalkingActivator` |

Fixed by demanding the declared class **plus every record class deriving from it**, derived from the
layout data rather than a hand-list (`PdbStructLayouts.TryGetAssignableFormTypes`, plus a
`FollowPointerToFormId` overload taking the accepted set). A leaf class such as `TESLoadScreenType`
still narrows to exactly one FormType, so the round-3e ASCII misread stays rejected.

The generic reader is the only consumer, so the specialized readers' hand-written
`FollowPointerToFormId(..., 0x2A)`-style calls are untouched and still exact by intent.

### T4 — the borrowed array cap was silently dropping MODT (DEFECT, fixed)

The cap was real, not theoretical. Instrumenting the bail showed **three models on `xex44` with
`cTextureCount` of 51, 51 and 53**, each having its entire hash list discarded rather than truncated.

The cap was borrowed from `RuntimeMemoryContext.MaxListItems`, which budgets a *linked-list walk* — a
patience limit, since a corrupt `next` pointer has no natural end. A counted array has no such
problem: `TESTextureList.cTextureCount` is a `u8` (PDB-confirmed, as is
`DestructibleObjectData.cNumStages`), so 255 is the field's own ceiling and the real validator is
that every entry pointer must resolve. The cap is now gone rather than raised to 255, because a bound
that cannot be exceeded is a check that cannot fail.

⚠ Honest result: **raising it recovered nothing on `xex44`.** A success probe confirmed the three
newly-admitted reads still return null — they fail later, at entry-pointer resolution. The fix
removes an arbitrary rejection and a silent whole-list drop; it did not by itself recover data in
this corpus. Asking *why* those reads still failed is what led to the partial-recovery work below.

### T4b — the all-or-nothing bail was discarding partially-captured lists (DEFECT, fixed)

Instrumenting every failing `TESTextureList` read on `xex44` showed the failure mode is almost
entirely **null pointers inside the array** — `badPtr=0` and `unreadable=0` across the board — and
that it splits into two populations:

| Outcome | Lists | Meaning |
|---|---|---|
| every slot null | 3,811 | an allocation that never received its entries — nothing to recover |
| **some slots captured** | **1,632** | **10,761 real hashes, all discarded** |

The engine fills a model's `BSFileEntry*` array as its textures load, so a dump routinely catches one
part-filled. That is the normal state of a capture, not corruption.

The old bail had a real reason: a hash means "the texture in slot *i*", so a **compacted** list
silently re-attributes every hash after a hole. But that argues for keeping the slot, not for
dropping the list. `ReadTextureHashes` now returns a positional `RuntimeTextureHashList` — declared
length preserved, holes marked `null`, and null returned only when *nothing* was captured.

| Dump | Lists before | After | Gain | Partial |
|---|---|---|---|---|
| `xex44` | 3,189 | **4,791** | +1,602 (+50 %) | 1,602 |
| `Fallout_Debug.xex2` | 5,057 | **7,839** | +2,782 (+55 %) | 2,782 |
| `xex21` | 7,375 | **7,838** | +463 (+6 %) | 463 |

⚠ It is a **distinct type, not `IReadOnlyList<string?>`**, because nullable annotations are erased at
runtime: a `List<string?>` still matches an `IReadOnlyList<string>` pattern, and
`ShowHelpers.FormatPdbFieldValue` has exactly such an arm — it would have joined every hole into an
empty string with nothing to warn the reader. Both display paths now render a hole as `--` and say
"N of M captured"; the sweep's log line reports the partial count beside the total.

Emission is unaffected — MODT is deliberately never written — and the `xex44` conversion stays
byte-identical at 97,014 records.

The DEST stage cap of 32 was instrumented the same way and **never fired on xex44, xex21 or xex2**,
so it stays.

### T2 — the sweep's cost, measured

`RuntimeNestedPayloadHandler` now times itself and reports the figure on the line it already emitted,
so this is answered at default verbosity rather than inferred from total parse time.

| Dump | Sweep | Semantic parse | Share of parse |
|---|---|---|---|
| `xex44` (warm host) | **92 ms** | 4.5 s | 2.0 % |
| `xex21` | **211 ms** | — | — |
| `xex44` (quiet) | **324 ms** | 24.9 s | 1.3 % |
| `Fallout_Debug.xex2` | **783 ms** | — | — |
| `xex44` (host under load) | 2,533 ms | 38.7 s | 6.6 % |

Against a 223 s conversion the sweep is well under 1 %. **Not material** — the handoff's proposed
buffer-sharing optimisation is not worth its complexity. Note how far the loaded-host sample sits
from the quiet ones: the handoff was right that wall-clock proxies are worthless here, which is
exactly why the handler times only itself.

### T7 — the small items

- **Unresolved MODS entries now say so.** The browse path deliberately keeps an entry whose TXST
  pointer did not resolve, since the shape name and 3D index are real, but both consumers rendered
  the FormID: the CLI printed `0x00000000` and the GUI built a *navigable link* to it. Both now
  render `(texture set unresolved)`, and the GUI item carries no `LinkedFormId`.
- **DEST's writer clamp is now structural.** The header count clamped at 255 while the stage loop did
  not, so more than 255 stages would have emitted more DSTD blocks than the count claims, with
  duplicate stage indices past the byte boundary. Both now derive from one clamped bound.
- **Layout database cost:** the embedded JSON is 1,385,192 bytes of a 16,366,592-byte assembly
  (**8.5 %**). Cold load is **103 ms** total, lazily and once — 61.5 ms for the 116 record types,
  37.4 ms for the 449 aux structs, 4.4 ms for the new assignable-FormType map. Paid only on a load
  that constructs the runtime reader.
- **Silent on pure-ESM loads: confirmed.** `stats` on retail `FalloutNV.esm` emits no nested-payload
  line (the handler returns early when `RuntimeReader == null`).
- **LSCR `LNAM`** remains exercised only synthetically — no dump in the corpus has a non-empty
  `LoadFormList`. Unchanged from round 4.

### T3 — the rendering change, examined by data and by frame

Runtime MODS reaching `RenderCache.AlternateTextureIndex` had never been looked at. Dumping the index
on `xex44` gives **422 records / 1,463 swap entries**, and its shape answers most of the concern:

| Owner type | Entries | To a real TXST | To `NullTextureSet` |
|---|---|---|---|
| STAT | 1,304 | 1,298 | 6 |
| WEAP | 66 | 66 | — |
| DOOR | 41 | 6 | 35 |
| HDPT | 31 | 31 | — |
| TERM | 13 | 13 | — |
| ACTI | 8 | 8 | — |

The data reads as correct, not as noise. The most-used targets are `1stPersonLaserRifleTexture01`,
`1stPersonVarmintRifleTexture`, `1stPersonHuntingRifleModsTexture` and `1stPerson10mmPistolTexture`,
each on the matching first-person view model. **199 of the 335 STATs are `1stPerson*` view models**
never placed in a cell; the other **136 are landscape scenery** — `RockCanyon11rad1078River`,
`SuburbanRubblePileScumWasteland01`, `TreeEvergreenStumpMudDirt01` — where a region-variant texture
swap is precisely what MODS is for.

⚠ The one alarming-looking value, TXST `0x00000028` on 41 entries, is **vanilla `NullTextureSet`** —
a deliberate engine form, not a misread.

**The failure mode the handoff feared cannot occur.**
`WorldMapOverlayBuilder.BuildAlternateTextureIndex` skips any entry whose TXST does not resolve,
skips any TXST carrying neither diffuse nor normal (which is what `NullTextureSet` is), and omits a
base object left with no surviving overrides. A wrong or unresolved entry therefore degrades to *no
override* — the pre-change behaviour — and can never substitute another object's texture.

**The before/after pixel A/B was then run**, once the other session's `ReferenceRenderer12.cs` edits
compiled again, by suppressing the runtime→MODS merge behind a temporary environment switch (since
removed) and re-capturing identical scenes:

| Scene | Capture | Result |
|---|---|---|
| `GibsonScrapYardInterior` | `--capture-frame` 512x512 | `pixelSha256` **identical** |
| `NovacGiftShop` | `--capture-frame` 512x512 | `pixelSha256` **identical** |
| `RSCharlieNVDESTROYED` | `--capture-frame` 512x512 | `pixelSha256` **identical** |
| `WastelandNV` worldspace (6,579 placements) | `--capture-topdown-batch`, 512 px cap | **identical** |

The three interior hashes also match captures taken *before* the T4 and T6 fixes, so those are
pixel-neutral as well.

⚠⚠ **Trap worth keeping: the first batch capture after a cold decoded-asset disk cache differs from
every later one.** The initial `WastelandNV` run hashed differently from the suppressed run, which
reads exactly like "the change altered the frame". A same-state control settled it — two repeat runs
in the *unsuppressed* state both produced the **suppressed** run's hash, so the odd one out was the
cold-cache first run (its log shows 73 texture-cache misses and 73 stores), not the change. Always
run a same-state control before believing a batch-capture diff.

So the change alters **no pixels** in this dump's renderable scenes. That is consistent with the
breakdown above: the swaps that resolve to a real TXST are overwhelmingly on `1stPerson*` view models
that are never placed, the DOOR swaps target `NullTextureSet` and are filtered out by design, and the
landscape statics that carry the rest are not in this dump's resident cells.

`GibsonScrapYardInterior` also renders correctly with coherent materials on visual inspection. Two of
the other interiors and the exterior default framing put the camera against a wall or over empty
ground, so they are uninformative about appearance — but they are still valid A/B subjects, since
both sides render the same scene.

### Also settled

- **DMDS does not exist in the FO3/FNV schema.** Handoff item 5.5 proposed reading both `DMDT` and
  `DMDS` off `DestructibleObjectStage.pReplacementModel`. Only `DMDT` has a subrecord — `DMDS` appears
  nowhere in the schema registry, the generated FO3 schema, or the merge policy. Since `DMDT` is
  MODT-family and MODT is deliberately never written, what remains of 5.5 is browse-only display of
  stage-model texture hashes.
- **`dmp to-esm` writes an ESM-flagged plugin** (TES4 header flags `0x00000001`). The round-4
  reproduce line named its output `out.esp`, which invites exactly the wrong mental model given the
  engine's ESM-flag cell-children rule. Corrected in the handoff; this round's outputs are `.esm`.
  **The `to-esp` command alias has been removed** (user ruling 2026-08-30) — it produced an
  ESM-flagged file regardless of the name it was invoked under, so keeping it only preserved the
  confusion. `dmp to-esm` is now the only spelling.

### Gate

| Gate | Result |
|---|---|
| Full both-TFM Release build, analyzers on | **0 errors** |
| T3 pixel A/B, 3 interiors + WastelandNV worldspace | **identical** both ways |
| ESM-area tests (the changed surface) | **110 / 0 failed**, 4 skipped |
| Focused nested-payload / container / writer / presenter classes | **51 / 0 failed** |
| `dmp to-esm` `xex44` | 97,014 records — **byte-identical** to the round-4 baseline |
| `dmp to-esm` `xex21` | 75,330 records |
| `dmp to-esm` `Fallout_Debug.xex2` | 67,681 records |

⚠ The full default suite reports **11 failures, none of them from this work**: all sit in
`Core.Formats.Nif.Rendering.*` and `App.WorldViewVisibilityParity*`, source-contract tests over the
renderer tree another session is actively editing — the same edits that break the Windows TFM.
Verified mechanically: not one of the 11 test files references any file this round touched. The suite
total also grew from 9,013 to 9,383 from that session's work, so neither number is comparable to
round 4's.

New tests: covariant pointer acceptance and its rejection of an unrelated type, the record-class
hierarchy pinned against the layout database, leaf-class narrowing, an over-50 texture list, the DEST
255-stage clamp, and the unresolved/resolved MODS presentation split.

---

## Round 6 — recovery-path audit of the DMP **parsing** path (2026-08-30)

Prompted by the MODT partial-recovery finding: if one counted read was discarding data whole, where
else? The user scoped this to the **parsing / record-viewing** path (conversion drops are mostly
deliberate game-stability policy and are treated separately at the end).

### Method

Three sweeps, each measured on `xex44` (63,239 runtime entries, 92 FormTypes):

1. **Every collection reader classified by hand** — does a bad element abandon the collection?
2. **Struct-level reach** — for every runtime entry, does the reader resolve its struct at all?
3. **Field-level yield** — for every FormType, which declared readable fields never produce a value
   on *any* instance? This is the sweep that would have caught MODT, and it needs the declared field
   list as the denominator: `ReadFields` only stores a key when the value is non-null, so a field
   that never reads is simply **absent**, not present-and-null.

### Result 1 — collection readers: one gap, already fixed

| Reader | Behaviour on a bad element | Verdict |
|---|---|---|
| `ReadAlternateTextures` | skips the node | partial-tolerant |
| `ReadLoadScreenLocations` | skips the node | partial-tolerant |
| `ReadInlinePointerArray` | keeps the slot, `0` marks the hole | partial-tolerant |
| `ReadTextureArray` | keeps the slot, `""` marks the hole | partial-tolerant |
| `ReadStringList` | skips empties | partial-tolerant |
| `WalkInlineBSSimpleList…` / `ReadFormIdSimpleList` | stops, keeps what it has | partial-tolerant |
| **`ReadTextureHashes`** | **discarded the whole list** | **fixed this round (T4b)** |
| `ReadDestructionStages` | all-or-nothing | justified, and **never fires** |
| `ReadCountedPointerArray` | all-or-nothing | justified, and **never fires** |

The last two stay strict because DSTD and IDLA are **written**, and a hole would emit a count that
disagrees with the blocks behind it. Instrumented on `xex44` and `Fallout_Debug.xex2`: neither branch
is reached, so the strictness costs nothing today. MODT is the opposite case — never written — which
is exactly why keeping its partial data is free.

The `RuntimeNpcFieldReader` bails (`skills > 100`, `SPECIAL > 15`, sum == 0, <50 % finite floats) are
**validity gates on fixed-size blocks**, not lost data: a skills array holding 137 is not a skills
array. Correctly all-or-nothing.

### Result 2 — struct reach is complete

**63,239 of 63,239 entries resolve their struct. Zero with no retained offset, zero unresolved,
across all 92 FormTypes.** There is no record type the runtime reader cannot reach. Whatever is
missing is missing at field level, not record level.

### Result 3 — a whole field *kind* had no reader (DEFECT, fixed)

210 declared fields never produced a value on any record. Filtering to the generic production path
and excluding engine bookkeeping (`nod_lpPrev/lpNext`, `m_pParentList`, `pSourceFiles`,
`iVersionControl`, `cVCVersion`) left 72 content fields — and the kind histogram gave the cause
away: 127 pointer, 31 struct, and **32 array**.

`ReadFieldValue`'s switch has an arm for every scalar kind, for `pointer` and for `struct` — **and
none for `array`.** `RuntimeContainerFieldReader.Handles` claims an array only when its detail ends
in `T *[]` or is `TESTexture[]`. Everything else fell through to `_ => null` and vanished with no
diagnostic: **43 of the layout's 54 array fields.**

Fixed with an `"array"` arm returning the raw bytes — the same treatment `ReadEmbeddedStruct` already
gives a struct over 8 bytes, rendered through the existing hex preview. An all-zero array still
declines, since that is an allocation the engine never populated.

**31 fields per record type became readable on `xex44`**, among them:

| Type | Recovered |
|---|---|
| RACE | `ppHeadModelFiles`, `ppHeadTextureFiles`, `ppBodyModelFiles`, `ppBodyTextureFiles`, `MeanFaceCoordMale/Female`, `cDefaultHairColor`, `pDefaultVoiceType` |
| NPC_ / CREA / CLAS | `TESAttributes.cAttribute`, `TESNPC.RaceFaceOffsetCoord` (FaceGen) |
| ARMO | `bipedModel`, `worldModel`, `inventoryIcon`, `messageIcon` |
| WTHR | `cloudTexture`, `fFogData`, `fHDRData` |

Only one array field still never reads — `TESWaterForm.pWaterWeatherControl`, which *is* claimed by
the container reader and is genuinely unresolved in this dump.

⚠ Emission is untouched: the `xex44` conversion stays **byte-identical** at 97,014 records. This is a
browse-path recovery, not an encoder change.

### Two corrections to the method, recorded because they nearly produced false findings

1. **`HasSpecializedReader` is not the full list of hand-written readers.** `RuntimeStructReader.ReadGenericRecord`
   dispatches **ASPC (0x0E)** and **MSET (0x6F)** to dedicated readers that are *not* in
   `PdbStructLayouts.SpecializedFormTypes`. Those two topped the first draft of the gap table (MSET
   84 % of fields "unread", ASPC 50 %) purely because the generic reader is not their production
   path. Both were removed from the findings.
2. **A refuted lead.** `TESSound*` pointers never resolving on MSTT, IPCT, TACT and IMOD looked like
   one systematic pointer bug. It is not: **55 of the 89 `TESSound*` fields in the layout do
   resolve** (`ACTI.pSoundActivate`, `ARMO.pPickupSound`, `CONT.pOpenSound` among them). The other 34
   are genuinely null in this capture.

### The conversion path, for contrast

Not this round's target, and the drop table shows why — the large codes are deliberate:

| Code | Count | Verdict |
|---|---|---|
| `cell.itm-override-suppressed` | 29,587 | identical-to-master suppression; correct |
| `refr.sparse-cell-master-preserved` | 3,560 | master's copy kept for master FormIDs; deliberate |
| **`refr.orphan-bucket-dropped`** | **2,670** | **the one large recoverable loss** |
| `info.master-topic-unbound` | 1,760 | deliberate safety — unbound INFOs in a shared master topic hijack vanilla NPCs |

`refr.orphan-bucket-dropped` is **2,670 placed references (2,633 REFR, 33 ACHR, 4 ACRE) across 607
unresolved-parent buckets**. A rescue path exists — persistent refs are re-homed into a worldspace
container — but children that did not move die with the bucket. This is a cell-attribution problem,
not a read problem, and it sits on top of the existing interior-misattribution work. Left open
deliberately: it is the largest remaining item on the conversion side and wants its own pass.

### Gate

| Gate | Result |
|---|---|
| Focused nested-payload / container / writer / presenter classes | **54 / 0 failed** |
| `dmp to-esm` `xex44` | 97,014 records — **byte-identical** to the round-4 baseline |
| MODT lists recovered (partial-recovery change) | `xex44` 3,189 → **4,791**; `xex2` 5,057 → **7,839**; `xex21` 7,375 → **7,838** |

⚠ Another session was live-editing the renderer tree and `StarfieldMaterialAlphaPolicyTests.cs`
throughout; the full-suite failures are all theirs (verified: none of the failing test files
references any file this round touched).

---

## Round 7 — terrain, and a data-**fabrication** defect (2026-08-30)

Follow-up sweep over the five domains the user named: quest scripts, mesh, terrain, dialogue, placed
objects. Terrain produced the finding.

### Runtime LAND enrichment was fabricating 15,745 records per dump (DEFECT, fixed)

Every DMP parse logged this, and it had been logged for a long time:

```
[WRN] EditorIDs: pAllForms: LAND FormType detection low-confidence (best.Value=2, need >=3)
      — skipping LAND population
[INF] LAND terrain: 63239 entries → 15745 with data (0 with mesh, 15745 coords-only), 47494 failed
```

Three separate things are wrong in that pair of lines.

**1. The reader's contract was documented but not enforced.**
`RuntimeWorldReader.ReadRuntimeLandData` opens with *"Caller is responsible for filtering to LAND
entries (FormType varies by build)"* and has no FormType guard.
`RuntimeDataEnricher.EnrichLandRecords` fell back to **the whole `RuntimeEditorIds` table** whenever
LAND FormType detection was low-confidence. So all 63,239 entries — every NPC, weapon, static and
sound — were read as a `TESObjectLAND`, and each one that produced a plausible cell coordinate was
**added to `scanResult.LandRecords` as a new `ExtractedLandRecord`** (`EsmLandEnricher.cs:72-76`).

None of those 15,745 can be genuine: an entry is in `RuntimeEditorIds` only if it *has* an editor ID,
and a LAND record never has one. That is exactly why `RuntimeLandFormEntries` exists as a separate
pAllForms-derived list — the fallback defeated its whole purpose.

Fixed on both sides: the enricher now **fails closed** (no runtime terrain beats invented terrain
attached to real cells), and the reader refuses any FormType the layout database can positively name
as a class other than `TESObjectLAND`, so the contract is code rather than a comment.

⚠ These never reached the plugin — the `xex44` conversion is **byte-identical** at 97,014 records
before and after. They polluted `scanResult.LandRecords`, which feeds heightmap/terrain analysis and
the viewer.

**2. ⚠ The FormType detection must stay empirical — do NOT substitute the PDB.**
An intermediate version of this fix made `pdb_layouts.json`'s `TESObjectLAND` → `0x44` the primary
source. **That was wrong and has been reverted.** `pdb_layouts.json` is generated from the *final*
build's PDB, but the dump corpus spans development builds whose **record enumeration was still
changing** — which is the entire reason this detection is empirical rather than a constant.
Hardcoding the final byte would repeat the old `0x45` default's mistake in a new form.

The reader-side guard was rewritten for the same reason. It no longer consults the PDB; it uses the
one invariant that holds in every build: **a LAND record has no EditorID**, which is why the
pAllForms sweep synthesises one (`__LAND_xxxxxxxx`). An entry carrying a real editor ID is
definitively not LAND, whatever the enumeration.

**3. When detection succeeds, the recovery is excellent — and that refutes the fabricated data.**

| Dump | Detection | Result |
|---|---|---|
| `xex21` | **succeeds** | `36 entries → 36 with data (36 with mesh, 0 coords-only)` |
| `xex44` | low-confidence (2 matches, needs 3) | no runtime terrain |
| `Fallout_Debug.xex2` | no known LAND FormIDs matched | no runtime terrain |

**Genuine LAND entries resolve at 100 %, and 100 % of them carry a mesh.** The fabricated path
managed 25 % "success" and **0 %** with a mesh. That contrast is the proof: real terrain entries have
meshes, invented ones never do.

### ⚠ CORRECTION: terrain mesh is *not* missing from the capture

An earlier draft of this section claimed the terrain vertex buffers live in an uncaptured VA range,
on the evidence that `xex44` showed `vertexPtrBad=15091` with a high-byte histogram of
**`0x3Cxxxxxx` = 14,968**, and that the dump has no regions in `0x3B`–`0x3E`.

**That conclusion was wrong.** Those pointers were not terrain vertex pointers at all — they were
whatever field happened to sit at that offset in the NPCs, weapons and statics the fabricating path
was misreading as `TESObjectLAND`. `xex21` recovers **36 of 36 meshes** from genuine LAND entries, so
the mesh data is present and readable. The `0x3C` histogram was measuring the garbage, not a capture
limitation. No one should spend time on "uncaptured GPU aperture" work on the strength of it.

### The real remaining terrain gap: detection coverage

Runtime terrain is recovered only on dumps with at least 3 carved LAND records to calibrate against.
`xex44` has 2. Improving that is a genuine opportunity, and it must stay build-adaptive. Two signals
are available without touching the PDB:

- **Elimination:** any FormType observed carrying a real EditorID in *this* dump cannot be LAND.
- **Structural confirmation:** candidate entries should parse as `TESObjectLAND` with a valid
  `pLoadedData`, sane cell coordinates **and a resolvable vertex pointer**. The mesh-yield rate is a
  near-perfect discriminator on the evidence above — 100 % for the real type, 0 % for every other.

### The other four domains

Signature census of the `xex44` output plugin, as a baseline for future work:

| | count | note |
|---|---|---|
| `SCDA` (compiled script) | 867 | |
| `SCTX` (script source) | **674** | **193 scripts emitted with bytecode but no source text** |
| `SLSD` / `SCVR` (script variables) | 349 / 349 | matched pair, consistent |
| `SCRO` (script references) | 2,545 | |
| `QUST` / `DIAL` / `INFO` | 297 / 1,204 / 1,464 | |
| `REFR` / `ACHR` | 76,969 / 1,432 | |
| `LAND` / `VHGT` | 41 / 41 | every emitted LAND carries height data |

**Open lead — quest scripts:** the 193-record `SCDA`-without-`SCTX` gap is the clearest remaining
per-domain discrepancy and has not been diagnosed. `Script.m_text` (a `char *`) is declared on
`Script` and is never read by the generic path; SCPT is routed to `RuntimeScriptReader`, so whether
those 193 are a reader gap or genuinely textless in the capture needs the specialized reader checked.
`dmp scripts compare` already exists for exactly this comparison.

**Placed objects** remain as recorded in Round 6: `refr.orphan-bucket-dropped` = 2,670 refs across
607 unresolved-parent buckets, a cell-attribution problem.

---

## Round 8 — LAND FormType identified by evidence, not correlation (2026-08-31)

Round 7 ended with runtime terrain recovered only on dumps carrying at least three carved LAND
records to calibrate the FormID-correlation heuristic. The question was whether better was possible
without reintroducing a build-dependent constant. It is.

### The experiment

Every FormType present in `pAllForms` was tried as if it were LAND, recording how many entries
yielded terrain and how many yielded a **mesh**:

| Dump | pAllForms | FormTypes yielding a mesh | Result |
|---|---|---|---|
| `xex21` | 75,636 | exactly one — **0x42** | 36/36 with mesh |
| `xex44` | 90,794 | exactly one — **0x42** | 25/25 with mesh |
| `Release_Beta.xex` | 67,508 | exactly one — **0x42** | 36/36 with mesh |
| `xex30` | 86,156 | exactly one — **0x42** | 25/25 with mesh |
| `Debug.xex2` | 70,743 | **none** | genuinely no runtime terrain |
| `Debug.xex` | 70,607 | **none** | genuinely no runtime terrain |

**The discriminator is unambiguous.** Exactly one FormType yields any mesh at all, it yields 100 %,
and every other type yields 0 %.

⚠ Note the byte: **0x42**, while the shipped PDB maps `TESObjectLAND` to **0x44**. That is the
enumeration drift in one line, and it is why the PDB cannot arbitrate.

⚠ Coordinates alone are NOT sufficient. `Debug.xex2` has a candidate type with **130 entries that
parse with plausible cell coordinates and zero meshes** — exactly the false-positive class that let
the old whole-table fallback invent 15,745 records. The mesh is the gate.

### The implementation

Two stages, because detection needs the runtime reader and the lookup-table build does not have one:

1. `EditorIdLookupTables` — when correlation cannot name the type, stage the FormTypes it could
   still be into `RuntimeLandCandidateEntries`, narrowed by the build-independent invariant that a
   LAND record has no EditorID (3 candidates on `Debug.xex2`, not the whole table).
2. `RuntimeDataEnricher.ResolveLandFormTypeByMeshYield` — read each candidate group and take the one
   producing the most meshes, requiring at least one. No mesh anywhere means no runtime terrain,
   which is reported and accepted rather than papered over.

The correlation heuristic still runs first and still wins when confident, so dumps that already
worked take the same path they always did.

### Result

| Dump | Before | After |
|---|---|---|
| `xex44` | 0 runtime LAND | **25, all with mesh** (`resolved to 0x42 by mesh yield`) |
| `xex30` | 0 runtime LAND | **25, all with mesh** |
| `xex21` | 36 with mesh | 36 with mesh (unchanged fast path) |
| `Debug.xex2` | 0 (after the Round 7 fix) | 0, silent — correct |

The anti-fabrication filter moved from the single-entry reader to the **batch** entry point, which is
where an unfiltered table becomes thousands of invented records; a deliberate single read is still
honoured as asked. `RuntimeLandFabricationGuardTests` pins both halves with a recording accessor —
a real editor ID is refused *without touching memory*, a sweep entry reaches the parse.

⚠ **Verification gap, stated honestly:** the full suite could not be executed for this change.
Another session is mid-landing a Starfield/FO76 feature and the test project does not compile
(`App/WorldMapOverlayBuilder*`, `StarfieldEnvironmentCaptureTelemetry*`, `StarfieldPlanetData*`,
`Fallout76VolumetricLightingParsingTests`). The behaviour above is verified against six real dumps,
and the guard relocation is verified at source level; the LAND unit tests that failed under the
mis-placed guard call the single-entry API that is now unguarded again.

### Round 8b — the ATXT regression, diagnosed and fixed

Enabling runtime LAND recovery cost 35 alpha layers on the `xex44` conversion (ATXT/VTXT 794 → 759,
18 of 41 records changed, one of them *gaining* layers). Three checks settled what it was:

1. **Not an engine limit.** Max layers per record stayed 24 and 7 records still exceeded 20, so
   nothing was being clamped. The shift was per-quadrant: six-layer quadrants 64 → 27, five-layer
   37 → 76.
2. **Six-layer quadrants are legal.** Retail `FalloutNV.esm` carries them in **2,529 of its 19,133**
   quadrants, with the same 24-layer ceiling. So the reduction was not a correction.
3. **The mechanism.** `CellLandPlanner` calls
   `MergeForEmission(visualData, runtimeVertexColors, masterVisualData)` — the dump is primary, the
   **master ESM** is the fallback. Those 18 records previously had no dump-side layers, so the
   master's authored set was used. Once runtime LAND recovery started producing layers, the primary
   was no longer empty and the master was never consulted.

**Fix.** `ChooseNonEmptyLayers` now prefers any **authored** candidate (`Dmp`, `MasterEsm`) over a
`Runtime` one before falling back to candidate order. A runtime `TESObjectLAND` describes the layers
that were *resident* at the crash; a parsed record describes what the cell *authored*. This is the
standing cross-source ruling — the file wins any field it has, runtime fills only what is absent —
applied to terrain. Runtime layers are still used when nothing authored has any, which is the
DMP-only browse case.

⚠ Deliberately scoped to texture layers. Vertex colours keep their existing runtime-first behaviour
(`MergeForEmission` takes `runtimeVertexColors` explicitly for that purpose); only the layer category
changes.

**Result on `xex44`:**

| | bytes | ATXT/VTXT | LTEX | TXST | REFR | LAND |
|---|---|---|---|---|---|---|
| baseline (no runtime LAND) | 13,743,492 | 794 | 0 | 0 | 76,969 | 41 |
| regressed | 13,728,082 | **759** | 2 | 2 | 76,969 | 41 |
| fixed | 13,742,714 | **793** | **2** | **2** | 76,969 | 41 |

34 of the 35 layers are back and the genuine runtime gain (+1 LTEX, +1 TXST) is kept. `xex21`, which
already had runtime terrain, is **byte-identical** before and after the fix, so it only engages where
runtime would have shadowed authored data.

⚠ **One record still differs and is left for a ruling.** `LAND 0x000DB102` emits 18 layers where the
baseline emitted 19; retail authors 19. Either the dump's own parsed layers are legitimately 18 for
this cell — in which case 18 is correct under the file-wins ruling and the baseline's 19 was the
master leaking through — or a layer is still being lost. One layer on one of 41 records; not chased
further.

⚠ **Not executed:** `LandTextureLayerPrecedenceTests` (4 cases pinning the rule, including that
runtime is still used when nothing authored exists) could not be run. Another session was
mid-refactor moving `WorldMapOverlayBuilder` into `Core/WorldData/` against a `HeightmapRenderer`
that does not exist yet, leaving both the main and test projects uncompilable. The numbers above come
from conversions run before that landed, and from direct parsing of the output plugins.

---

## Round 9 (2026-08-31): adversarial re-verification of rounds 5–8b + the data-linkage audit

The user asked two questions over everything rounds 5–8b produced: is anything wrong or left, and
is recovered data linked together **by evidence or by well-founded heuristics**. A user ruling
taken at planning time frames the second question: **heuristic links are fine as long as there's a
decent rationale for them** — the audit flags only rationale-free or inconsistently-applied
heuristics, never heuristics as such.

Method: three read-only exploration passes (change inventory, linkage map, adversarial pre-seed),
then an 8-group verification workflow (51 agents; every claim verified against source with
CONFIRMED/REFUTED verdicts, every new finding submitted to a 3-refuter majority panel, and a
completeness critic over the whole verdict set), then fixes, then re-measurement against a
byte-identical rebuilt baseline.

### Gate results

- The rounds' own never-executed tests **pass on first run**: `LandTextureLayerPrecedenceTests`
  (4 cases) and `RuntimeLandFabricationGuardTests` all green.
- The predicted stale test **failed exactly as predicted**:
  `MergeCategories_PrefersRuntime_FillsFromMasterEsm` pinned the pre-8b runtime-first layer rule
  (expected `Runtime`, got `MasterEsm`). Rewritten as
  `MergeCategories_AuthoredLayersOutrankRuntime_OtherCategoriesFillPerCandidateOrder`.
- Full fast suite after fixes: **10,117 pass / 35 fail — every failure is the concurrent
  session's** (Nif rendering source-contracts + their nine unregistered Starfield/FO76 typed
  collections). Zero failures in anything rounds 5–9 touched.

### Verdicts (45 claims, 8 groups)

**Refuted — do not re-chase:**

- **T-2a** (REFR/ACHR/ACRE swallowed by the LAND-candidate branch): the premise is empirically
  false — xex44's editor-ID table holds 3,220 named REFR / 1,074 ACHR / 292 ACRE entries, so the
  refr types are in the exclusion set and placed-ref enrichment runs. The protection was
  *incidental* (it relies on at least one named ref per type existing); an explicit exclusion is
  now in place.
- **T-1b** (mesh gate weak at 1%): the 1% floor only selects buffers for
  `RuntimeTerrainGridReconstructionService.Reconstruct`, which demands >=12 valid samples, IQR
  z-filtering, bounded XY spans, and a lattice or 75-80%-coverage grid fit — a garbage candidate
  cannot luck a mesh through it.
- Also refuted: T-2d (`TryProbe` cannot emit the 0xFF sentinel), T-3e, T-4b (bounds guard covered
  by callers), T-4d (covariance derivation complete for the live layouts), T-6e, T-9b
  (`TryAlternateTextures` genuinely implements its all-or-nothing drop), C2
  (`BuildAlternateTextureIndex` survived the Core/ move unchanged).
- The refuter panel also **killed** the "mis-correlated LAND fabricates coords-only records"
  finding 3-0: correlation counts are identity-probed (exact FormID at +12), duplicated form
  copies vote for the *correct* byte, and a mesh veto on the correlation path would **regress**
  builds (xex22/xex43/xex44) whose genuine LANDs hold valid LoadedLandData with unresolvable
  vertex arrays. Do not add a mesh veto there.

**Confirmed and fixed this round** (all verified by the gates and the re-measure):

1. Stale test rewritten (above); missing-stamp, larger-runtime-set and unattached-count cases
   added to `LandTextureLayerPrecedenceTests` (8 cases total).
2. `LandVisualData`: the `?? Source` fallbacks were dead code (per-field sources are
   non-nullable) — replaced with real effective-source properties; pass 1 of
   `ChooseNonEmptyLayers` is now an **authored allowlist** (`Dmp`/`MasterEsm`) instead of a
   Runtime blocklist (closes the None/Merged loopholes); the chooser takes instances and consults
   `HasTextureLayers` before materializing (a lazy BTD candidate no longer decodes just to lose);
   `UnattachedVtxtCount/ByteCount` now survive merges; the `MergeForEmission` docstring no longer
   claims runtime vertex colors outrank the primary — the code was right (file-wins), the
   docstring and the 8b memory note were wrong.
3. `BtdTerrainInjector` + `Tes3RecordParser` now stamp per-field sources.
4. `EditorIdLookupTables`: LAND candidacy explicitly excludes the detected REFR/ACHR/ACRE range;
   both bucket-chain walkers (here and `EsmEditorIdExtractor`) gained cycle guards — a looping
   next-pointer now counts as a chain error instead of appending ~1000x duplicate entries.
5. `RuntimeDataEnricher`: deterministic mesh-yield tie-break (lowest byte wins; a tie logs a WARN
   — the corpus norm is one type at 100% and the rest at 0%, so a tie deserves eyes) and the
   resolution is **persisted** into `ScanResult.RuntimeLandFormEntries` — the gap scanner,
   formtype census and shift probes previously saw zero LAND on every mesh-yield-resolved dump.
6. **W10**: the second `DetectDialFormType` (the one gating all INFO->topic linking) accepted its
   0x45 fallback on mere existence — on a gapped or non-+1-drifted dump that walks the wrong
   record population and fabricates topic/quest links keyed to real FormIDs. Now probe-validated
   (>=3 of 20 `ReadRuntimeDialogTopic` successes) and logged, mirroring its sibling.
7. **W8**: topic quest backfill refuses ties (dictionary-order coin flips no longer assign
   parentage) and counts the refusals.
8. **W4**: `RecoveredTopicFormId` — set by the gap-recovery promoter with a comment claiming the
   repaired parentage reaches emission — was consumed by *nothing*. `MergeDialogueWithRuntimeInfo`
   now fills `TopicFormId` from it when the walk/carve left none, mirroring the quest half that
   already worked. (Runtime-only recovered INFOs still need a creation pass — see punch list.)
9. **W3**: `refr.orphan-bucket-dropped` no longer labels every drop an "unresolved-parent
   bucket"; the message and metadata now carry the bucket kind, grid, EditorId,
   `WorldspaceAssignmentSource`, and the child's `AssignmentSource`.
10. **The V5 pair, shipped together per the critic**: the bounds-inference reuse scan excludes
    `IsVirtual` tiles (the pass's own doc block promised rescued refs would not land in
    planner-deleted cells; the offset-cluster pass running earlier in `Apply` violated that), and
    `CoordBasedCellPairingPass` no longer folds virtual tiles onto master cells — the fold had
    re-keyed doomed buckets under real retail FormIDs (579 of 607 on xex44), misdirecting triage.
11. `RuntimeContainerFieldReader`: the counted-array cap borrowed the BSSimpleList budget again —
    the exact anti-pattern the R5 ruling retired — so an IDLM with more than 50 idles was
    silently dropped whole; now bounded by the count field's own u8 range. The `Handles`
    BSSimpleList branch now requires a non-pointer kind: 8 pointer-to-list fields (ENCH/SPEL
    `m_pkNext`, ARMO/ARMA `pFoleySoundList`, REGN `pPointLists`, CDCK `pDeck`) were being walked
    at the wrong indirection, chasing adjacent struct members as list chains.
12. `ReadPointerField`: a pointer whose declared target is a known **non-record** class no longer
    falls to the weak untyped follow (the " ski" fabrication shape) — it returns the raw VA as a
    diagnostic. ~276 aux-typed/unknown-typed pointer fields exit the misread-prone path.
13. `PdbStructLayouts`: a duplicate class name in a future layout regeneration now logs loudly
    (the TryAdd previously swallowed it; the live DB has zero duplicates, verified).
14. `ShowHelpers`: the texture-hash line escapes captured slot text (the sibling path already
    did).
15. `CsnoEncoder`: doc block updated — the layout regeneration made the CSNO model/texture arrays
    readable, so "no reader change can reach them" is no longer true (emission is open work).

### The ADDN DNAM story — the workflow's one overturned verdict

The workflow **confirmed** (T-4c) that `AddnEncoder` writes captured big-endian DNAM bytes into
the little-endian plugin, and a first fix swapped the leading u16. Empirical measurement then
overturned the verdict's premise:

- Both retail masters store `MPSFlamerFlame01` DNAM as the **same bytes** (`00 00 01 00`) while
  the sibling DATA subrecord is genuinely byte-swapped (`24 00 00 00` vs `00 00 00 24`).
- All 37 runtime `BGSAddonNode`s in xex44 hold `Data@104 = 00 00 01 00` — file byte order —
  while the adjacent `iIndex@96` / `iMasterParticleSystemIndex@108` are big-endian.

DNAM is the INDX-style **already-little-endian** field class: the engine loads it verbatim, so the
encoder's original verbatim emission was correct all along (the swap was reverted; the evidence is
pinned in the encoder comment). The **real** defect was one hop away: the *conversion* schema
`SchemaKey("DNAM","ADDN",4) = Simple4Byte` 4-byte-reverses it, so every `esm convert` emitted
`00 01 00 00` — the Master Particle System Cap transposed into the wrong half. Fixed to two
`F.UInt16LittleEndian` fields and verified end-to-end: a fresh Xbox->PC conversion now produces
DNAM `00 00 01 00`, byte-equal to the PC master. (ADDN never emits from the DMP corpus — 0
records in every output — so `dmp to-esm` outputs were never affected by either side of this.)

### Re-measurement — every delta attributed

A clean worktree at HEAD rebuilt the pre-round-9 baseline: `base_xex44.esm` is **md5-identical**
to the accepted 8b `land_fixed.esm`, proving conversion determinism and making the fix delta
exact. Round 9 vs baseline on xex44:

| | baseline | round 9 |
|---|---|---|
| bytes | 13,742,714 | 13,742,644 |
| records emitted | 97,016 | 97,016 |
| per-type counts | — | all identical (CELL 230, NAVM 20, PGRE 24, ...) |
| ATXT / VTXT | 793 / 793 | **793 / 793** |
| `refr.orphan-bucket-dropped` | 2,670 across 607 buckets | **2,602 across 601** |
| bucket key ranges | 579 of 607 keyed by **master** FormIDs | **all synthetic (0x01)** |

The 68 fewer drops are redistributor-synthesized copies that no longer get created into doomed
tiles; validation and the full semantic check pass on both outputs; the residual byte/hash
difference is a cosmetic synthetic-FormID renumber from the changed allocation order (the same
content classes appear on both sides at shifted IDs). xex21: same shape (75,330 records, -70 B,
clean validation).

**The orphan-bucket population, finally classified** — the new W3 metadata answers what no prior
artifact could: of 2,602 dropped children, **2,593 are `PersistentRedistributedSynthetic`**
placements in virtual tiles and only **9** die in true unresolved-parent buckets. Master-range
Override drops lose the dump's captured *delta* for a ref whose vanilla placement survives via
the master — the "2,670 lost placed refs" headline substantially overstated the unique-content
loss.

### The linkage verdict (the user's second question)

Runtime reads are overwhelmingly **evidence-based**: typed pointer derefs with FormType gates,
hash-table containment for cell membership, a bidirectional backpointer gate on quest<->script
(a brute-forced candidate is accepted only if its `pOwnerQuest` points back), agreement-gated
dialogue parentage recovery, and fail-closed SCTX reads — which honestly explain most of the
867-SCDA / 674-SCTX gap: bytecode has an explicit size, source text needs its NUL terminator
resident. The parse/planner-side heuristics — proximity, grid-map, bounds inference with the
Z-band veto, interior offset clustering with the spatial veto, offset-cluster anchoring, stem
aliasing (refuses ambiguity), single-candidate grid parents — all carry stated rationales and
honest `AssignmentSource`/`WorldspaceAssignmentSource` provenance, and under the ruling are
**documented as sound, not changed**. What the round fixed was never "heuristics": it was the
places where a heuristic was applied inconsistently (W2's IsVirtual asymmetry), misdescribed
(W3's label), unvalidated relative to its own sibling (W10), tie-broken by accident (W8, T-1a),
or where an intended evidence path dead-ended (W4).

### Open items and rulings

**Needs a user ruling:**

- **W2 flip**: should offset-cluster rescue tiles become real cells like the bounds-inference
  pass (rescuing a share of the 2,593 synthetic-copy drops into emission)? The per-class evidence
  is now in the event metadata; note the reduced stakes given the reclassification above.
- `LAND 0x000DB102` 18-vs-19 layers (carried from 8b).
- `EnrichCellsWithMasterEsmLandFallback` is **dead code** (zero callers) — its docstring promises
  parse-time master fill-in for the GUI/viewer that never happens. Wire it (GUI behaviour change)
  or delete it; either way the merges need `VtexTextureFormIds` support first (they currently
  drop that field — latent until Morrowind/BTD instances enter a merge).
- ~~Carried from rounds 2-3: DDX fallback rule, GMST duplicate certainty, M2 LZX->BSA.~~
  **Correction (2026-09-01):** these were already ruled and closed — the Round 2 implementation
  section above records all of it (Ruling 2: DDX exact chunk-walk, 0% fallbacks; Ruling 3: GMST
  deep compare; Ruling 4: M2 HELD pending a real XMem archive, silent failure closed). The
  "Decisions owed" block at the end of Round 1 is a stale snapshot; nothing from rounds 2-3 is
  owed.

**Punch list (no ruling needed):**

- W4 completion: a creation pass for promoted runtime-only INFOs (`MergeRuntimeDialogueData`
  only enriches existing records).
- V7 finding (survived refutation): the enumerator's typed gates for `pLightingTemplate` (0x67),
  `ExtraCellMusicType` (0x6B) and `ExtraCellImageSpace` (0x56) contradict the layout DB (0x65 /
  0x66 / 0x53) with a non-monotonic implied shift — at least some of the six hardcoded gates
  reject the genuine target on any one build, silently nulling those fields. No existing CSV
  carries them; settling needs one instrumented run (hexdump the pointed-to FormType bytes for a
  few known cells, or count non-null fields per build).
- W7: gate `pCellLand` with the now-persisted per-dump LAND FormType; add an `IReadOnlySet`
  overload of `FollowPointerVaToFormId` and gate the cell refs list on the REFR family.
- CSNO model/texture emission (now readable); LSCR LNAM still synthetic-only.
- T-4a (all-zero array decline conflates absent with zero — display-only today; revisit if an
  encoder ever consumes those fields), T-4e (uncaptured-VA typed pointers discarded
  indistinguishably from misreads), T-5a/5b (MODT DeclaredCount plausibility gate + same-span
  entry check), T-2c (a single spurious editor-ID string can exclude a genuine LAND type — wants
  a count threshold + logging), T-3b (rejected candidates' stage counters computed then
  discarded).
- Cosmetic: synthetic-FormID renumbering between versions churns plugin-range IDs (relevant to
  savegame continuity across released versions); terrain captures still owed (≥1920×1080 —
  USER RULING 2026-09-01: 512px is far too small for a capture; it is only a Read-downscale size).
- The both-TFM build gate could not complete this round: the concurrent session's
  `WaterRenderer12.cs` was mid-edit (CS1026) at gate time. The CLI TFM builds 0/0 with all fixes;
  no `App/` file was touched by this round.

**Cross-session facts recorded:** the audited "uncommitted" scope was committed mid-round
(c24c2421..5fb4271d); the `pdb_layouts.json` +31,645-line growth is in 8fcf40c7 and **passes the
diff-gate** (54 unknown->real fields, 2 typeDetails filled, +449 auxStructs, zero pre-existing
fields moved, zero duplicate class names); `RecordModelUnion` correctly skips the now-lazy
`DecodedTree`; the `MiscEnvironmentHandler` BE/LE hunk is the Starfield session's, Starfield-scoped
and fail-safe for Xbox BE records. Unexamined surfaces this round (flagged by the critic): the
round-3 carving/provenance batch (~24 files, covered by R3 but not re-verified here) and
`RecordCollectionFormIdRebaser`'s name-keyed registry completeness (one interaction checked and
neutralized: FormID-0 alternate-texture entries are guarded by the mappers themselves).


### Round 9 rulings applied (2026-09-01)

The user ruled on the three open decisions; all three turned into shipped changes the same day.

**Decision 1 — orphan-bucket refs: RULED (b), re-home by the FormID census.** The user's
instinct — "shouldn't this be done already by our form ID census?" — was exactly right: for an
Override child the master's own GRUP containment (`MasterIndex.RefToCell`, already plumbed into
`PersistentCellReparenting` as `masterRefToCell`) names its authored parent cell with zero
heuristics. `OrphanBucketResolution` (extracted to its own file for the planner line ratchet) now
re-homes each dying Override child into that cell — persistence class follows the MASTER's filing
so the GRUP/flag agreement holds, targets must already be in the plan (sparse-preserved cells stay
untouched), and a global child-FormID index prevents duplicate emission. **Measured on xex44:
2,593 of 2,602 dying children re-home — a complete sweep of the Override class — leaving exactly
the 9 unresolved-parent New records that nothing can place.** Top target (1,826 refs) is a
worldspace persistent container, i.e. where master files those refs. Emission grew 97,016 →
99,891 records (81,594 placed refs), full validation + semantic check green (no duplicate
FormIDs, all refs correctly parented).

**Decision 2 — LAND 0x000DB102's 18-vs-19: NEITHER hypothesis — a null-texture layer drop, now
fixed.** The build-difference reading was tested and refuted: the Aug-22 disk build IS the 360
final (byte-identical ESM), and semdiff shows it authors all 19 layers, layer-for-layer equal to
PC. The truncated-compressed-record recovery was also refuted (kill-switch run emitted the same
18). The actual chain, proven with a temporary planner probe: the record's page is absent from
the dump image, so the cell's terrain is runtime-recovered → the LAND re-encodes instead of
carrying master bytes verbatim → `MergeForEmission` correctly picks the master's 23 layers → and
then BOTH `LandTextureRewritePlanner.RewriteLandVisual` and `LandEncoder.Encode` silently
discarded any layer with `TextureFormId == 0`. Retail authors exactly such a layer (quadrant 0
layer 1 paints the ENGINE-DEFAULT land texture, VTXT and all) — FormID 0 there is a sentinel, not
a dangling reference. Both filters removed (and `LandEncoder`'s DATA quadrant-flag loop aligned).
**Result: the record is now layer-for-layer identical to retail, and the fix restored 9 authored
layers corpus-wide on xex44 (ATXT/VTXT 793 → 802) — the "794-vs-793 residual" was this defect,
and the true parity number was higher than 794 all along** (the old 794 baseline still lost null
layers on OTHER re-encoded records; only verbatim master carry-forward preserved them).

**Decision 3 — `EnrichCellsWithMasterEsmLandFallback`: docstring was WRONG, method stays.** Per
the user, the intended consumer is a VIEWER TOGGLE — swap between the dump's preserved LAND
records and the loaded master ESM's, so placements that survived without terrain can be previewed
as the converted ESM will show them. The docstring no longer claims an unconditional parse-time
fill; the toggle is owed GUI work. Prerequisite fixed now: both merge builders carry
`VtexTextureFormIds` (first non-null wins) and count it in the all-empty check — previously any
merge of a Morrowind/BTD-shaped instance silently blanked the 16×16 texture grid.

Also corrected while assembling the decision brief: the "carried decisions from rounds 2-3" in
the Round 9 punch list were already ruled and closed (see the strike-through note above).

Final round-9 output state (xex44): 99,891 records, ATXT/VTXT 802/802, validation + semantic
check clean; suite 10,110 green with zero failures outside the concurrent session's rendering
scope; planner line ratchet green after the `OrphanBucketResolution` extraction.


### Remaining work executed (2026-09-01, second pass)

**V7 pointer-gate contradiction: measured, then fixed.** The empirical protocol (region-map +
direct dump reads on xex44): 40/40 interior cells' `pLightingTemplate` targets carry **0x65**
(the layout DB's BGSLightingTemplate; the code gated 0x67 = TESObjectIMOD), 19/19 worldspace
`pImageSpace` targets carry **0x53** (DB TESImageSpace; code gated 0x56 = BGSPerk), 4/4
`pMusicType` targets carry **0x66** (DB BGSMusicType; code gated 0x6B = TESRecipeCategory) —
control fields (pClimate 0x36, pWorldSpace 0x41) matched their gates, validating the protocol.
The three wrong literals had **never matched once**; the gates now resolve through
`PdbStructLayouts.TryGetFormTypeByClassName`. Measured emission gain on xex44: **+1 LTMP,
+40 XCMO, +50 XCIM** on cell overrides (+91 authored links the dump always held); XCAS/XEZN
(correct gates all along) byte-stable. xex21's counts were already saturated via its parsed
cells — no emission delta there.

**W7 `pCellLand` gate: attempted, measured, REVERTED — an honest negative.** The per-dump
empirical LAND byte now persists on `RuntimeMemoryContext.ResolvedLandFormType` (kept), but
gating the `pCellLand` follow with it collapsed xex21's WRLD/CELL shift-probe margin (11 → 0)
and shifted downstream cell classification until a real gridless exterior cell hit the
planner's hard guard (`CellContextSynthesizer` throw). Bisect-proven: gate off → clean run.
The link's ungated blast radius is only the runtime cells CSV and probe scoring, so the gate
stays off — documented at the call site — until the probe/classification stop consuming it.

**W4 completed: runtime-only promoted INFOs are now created.** `CreateGapRecoveredDialogues`
(DialogueRuntimeMerger) materializes gap-recovery-promoted INFOs the topic walk missed —
`RecoveredTopicFormId is > 0` is the precise marker (the promoter sets it only under the
agreement gate), topic-less promotions are refused (the `info.master-topic-unbound` hazard),
one record per FormID, ordered before the TES-file script backfill so created records get
segment calibration and result-script recovery. Construction shared with the topic-walk path
via one builder so the two sites cannot drift. Off by default (gap recovery is opt-in). Three
new tests.

**CSNO model/texture arrays: read AND emitted.** New `ReadModelSwapArray` (10 × 32-byte inline
`TESModelTextureSwap`, cModel at +4 from the exported aux layout, fail-closed without it) joins
`ReadTextureArray` (11 × TESTexture); `GenericRecordFields.TryStringSlots` (exact-count
positional accessor); `CsnoEncoder` emits the vanilla-verified order — MODL ×8 (chips + slot
machine), MOD2 duplicating the slot-machine path byte-for-byte, MOD3/MOD4 tables, ICON ×7
reels, ICO2 ×4 deck backs (repeated path preserved) — all-or-nothing per positional group,
MODT deliberately never (schema lists one; no vanilla record carries it). Corpus emission
delta today: 0 (all five casinos are master-preserved) — the win is reader-side paths plus
emission-readiness for proto dumps. Four encoder tests + the `TESModelTextureSwap` aux-layout
ratchet pin.

**Smaller closures:** T-2c — excluding a FormType from LAND candidacy now takes ≥2 named
entries (a single fallible string read can no longer hide a build's terrain; singles are
Debug-logged; a wrongly-admitted type costs only sweep time since the mesh gate yields 0 for
it). T-3b — the quiet sweep path leaves one Debug stage-counter line, so "no candidate yielded
a mesh" is falsifiable from logs. A second stale pre-8b pin
(`Land_Encode_SkipsTextureLayersWithZeroFormId`) was rewritten to the ruled null-texture
behavior (emit, own VTXT, DATA quadrant bit).

**Validation.** Concurrent-session churn made the shared tree unusable mid-pass (their live
edits broke the main project once and the test project three times), so validation ran in an
isolated HEAD worktree carrying only this session's 34 files: main + tests build clean,
**9,699 tests green with zero failures outside the rendering set already failing at HEAD**.
Conversions: xex44 = 99,891 records, semantic check clean, delta vs the rulings baseline fully
attributed (+91 V7 subrecords, +1,000 B); xex21 = 77,085 records, semantic record diff vs
baseline is **zero** (the only byte delta is one worldspace's all-zero OFST table now being
rebuilt, +80 B, engine-benign).

**Still owed:** the viewer master-terrain toggle (GUI; the App tree is the concurrent
session's active battleground), the probe/classification decoupling that would let the
pCellLand gate return, and the standing opens (ATXT-cell captures at ≥1920×1080 per the
2026-09-01 resolution ruling, LSCR LNAM synthetic-only, `pWaterWeatherControl`).


### Viewer master-terrain toggle + 1920px ATXT captures (2026-09-02)

**Two rulings from the user opened this pass:** (1) "512px is usually far too small for a
capture. 1920x1080 is the minimal acceptable resolution." — the standing "512px terrain captures
owed" item was mis-specified; (2) the Decision-3 viewer toggle is approved, "conditioned so that
it doesn't appear at all unless the loaded file is a DMP".

**Toggle — what shipped.** A DMP-only `Master ESM terrain` checkbox in the 3D settings panel's
Terrain group (Visibility expander, outside `TerrainDependentControls` because it is a
data-source switch that also changes the 2D map). Semantics follow the user's own wording —
"preview placements the way the converted ESM will show them": ON (default) = dump-preserved
LAND where it survived + the Load Order masters' LAND where it did not (the in-game result of
installing the plugin over its master), OFF = exactly what the dump preserved. Mechanics:
- OFF threads a new `carryBaseTerrainIntoCells:false` through `MergeWith → MergeCells /
  MergeWorldspaces → MergeCellPair`, nulling the three `override ?? base` terrain carries for
  the DMP overlay only (every other caller keeps engine semantics).
- ON additionally runs a NEW viewer-shaped `EsmLandEnricher.EnrichCellsWithMasterEsmLandFallback
  (cells, masterCells)` after the captured-cell filter: grid-keyed (reaches FormID-diverged and
  synthetic cells the FormID-keyed merge cannot), per-category via `MergeForEmission` (partial
  LAND gains only its missing categories; Runtime layers lose to authored per the 08-31 ruling),
  per-half last-wins master index, a completeness fast path — and, the part the converter-shaped
  overload never did, a **heightmap fill** for cells with neither `Heightmap` nor
  `RuntimeTerrainMesh`, because the 3D decode reads exactly those two fields and visual
  categories alone leave recovered placements floating. The converter-shaped overload stays
  (ruled "keep"), documented as unwired; its inputs only exist in the conversion pipeline.
- Rebuild goes through the same reset path a Load Order change uses (`WorldRenderCache` is
  reference-keyed with no invalidate; 2D and 3D share one `WorldViewData`).
- Profiler parity: both headless data loaders run the same default-ON enrichment for dump
  primaries, so a capture cannot diverge from the app (standing directive).

**Adversarial review (8 agents; 1 confirmed major, 3 refuted, 9 minors, 8 of them fixed).**
The confirmed major was pre-existing and my feature added a trigger: the world-map populate is
strictly single-flight (`RunExclusiveAsync` returns the in-flight task), so a reset+re-fire that
landed mid-populate scheduled nothing while the running pass discarded its stale result — the map
stayed on the placeholder until a sub-tab switch (reachable via toggle → Load Order "Clear All",
and via two rapid Load Order applies before this pass). Fixed at the shared level:
`PopulateWorldMapAsync` now loops a `PopulateWorldMapPassAsync` while a pass ends STALE
(generation moved on, map unpopulated, session still has data); teardown stays terminal.
Minors fixed: `MergeWorldspaces` was not gated (latent `ws.Cells` disagreement, masked by the
relink); first-wins grid index (now per-half last-wins, a visual-only master cell can no longer
starve the heightmap); always-clone + lazy-BTD materialisation (completeness fast path);
profiler loaders lacked the enrichment; `--capture-topdown-terrain-color` accepted outside
`--capture-topdown` (now fails closed) and missing from the usage-agreement test; two stale
comments. Refuted: a save-file path claim (unreachable — saves return before the merge), a
focus-collapse a11y claim, and a "never-converging depth pre-pass" claim about the new
resolution (the gate is resolution-relative by design and the batch path ships that mode).
Deliberately not done: a mirrored checkbox on the 2D panel (the toggle is unreachable in the
default 2D view mode without switching to 3D; the effect is still visible there) and a
source-contract pin for the field/checkbox default mirror — both follow-ups if wanted.

**Capture resolution ruling applied.** `--capture-topdown` renders square at
`max(--capture-width, --capture-height)` with the batch path's max-dimension lift (the old
hardcoded 512 is gone); `CaptureWidth/Height` defaults 768×480 → 1920×1080 (the stale
`PreservesPerspectiveDefaultsForCompatibility` pin rewritten to the ruling); new
`--capture-topdown-terrain-color` opts the single-shot path into self-contained coloured
terrain (default keeps the 2D-map-overlay-comparable transparent ground).

**ATXT-affected cells captured at 1920×1920 (owed since round 8b).** The +9 restored layers
localise to four LANDs of xex44 — 0x000DB102 (+1, the doc's named record), 0x000DB111 (+4),
0x000DDF9F (+2), 0x000DDFA1 (+2) — plus two LANDs (0x000E262F, 0x000E2637) that exist only in
the final output (R9 re-homing). All six cells were captured A/B (793 baseline vs 802 final,
`TestOutput/atxt_captures/`, composites under `composites/`), the visibly-changed ones also
against the vanilla master. Findings: every restored layer is `TextureFormId = 0` (engine
default) at the L0 slot the baseline had visibly skipped; layer structure and VTXT content now
byte-match retail per `esm semdiff` (only the benign PlatformFlag pad byte and a 3-unit VHGT
offset differ), and the renderer already treats FormID 0 as
`CellLayerWeightTable.EngineDefaultSentinelFormId`. Visible effect is a small blend region in
0x000DB111 to nil elsewhere — correct, the default texture is the surrounding dirt. Both
re-homed LANDs diff clean against retail. One capture (0x000DDFA1 final) came out as a blue
"water" cell; the user identified it: the capture fired before terrain streaming converged
(the run took 2× its siblings), so the worldspace water plane drew unoccluded —
`--capture-topdown`'s `IsComplete` covers overlay/reference streaming, not terrain-colour
residency. Recaptured warm; the settle gap is recorded as an open item for the single-shot path
(the batch path already has a deadline + `settled` flag).

**Validation.** The shared tree was unbuildable for the test gate throughout (the concurrent session's files broke and healed minute to minute), so validation ran in a HEAD worktree carrying the full uncommitted set minus their two mid-refactor test files: full both-TFM build GREEN (main, WinUI App, both profilers, tests); fast suite 10,802 tests — 10,491 green, 268 skipped, 43 failed, every failure attributable to the concurrent session's uncommitted work (ten new Starfield/FO76/BendableSpline typed collections absent from the inventory + runtime-parity matrices, their PNDT keep-both merge, the XBSD parser, and rendering/water/Starfield source-contract pins) and none referencing cells, terrain, LAND, the enricher, capture options or the settings panel; the touched classes plus the accessibility and source-contract ratchets ran 65/65 green in a targeted pass. The shared tree's own build confirmed the App TFM and profiler compile (only their test files failed there). The profiler exe built with these changes produced every capture.


### Open items carried through (2026-09-02, third pass)

**pCellLand evidence gate — probe decoupled, gate LIVE, emission unchanged.** The W7 gate had
been tried and reverted because the WRLD/CELL shift probe scored the *typed* LAND link
(`RuntimeWorldCellLayoutProbe.ScoreCellSample` on `LandFormId is > 0`, and the world sample's
`LinkedCells` signal on the same field), so gating the follow starved the probe of a structural
signal and collapsed xex21's margin 11 → 0. The enumerator now follows `pCellLand` twice with
different consumers: a raw ungated follow (`RuntimeCellProbeSnapshot.RawLandFormId`,
`RuntimeCellMapEntry.HasLandPointer`) that only the layout probe scores — at the right shift the
slot holds null or a pointer to *some* TESForm, at a wrong shift garbage, which is all the probe
needs — and the gated follow (`LandFormId`, target FormType must equal the per-dump empirical
LAND byte from `RuntimeMemoryContext.ResolvedLandFormType`) that the cells CSV / cell map report.
`CreateWithAutoDetect` now logs whether the gate is live. Measured: xex21 `[LAND gate] pCellLand
follows gated on empirical LAND FormType 0x42 (36 LAND entries)`, probe winner World +0 / Cell −8
at score 184, **margin 9**, conversion **byte-identical** to the pre-change baseline (77,085
records, 11,510,605 B, same hash); xex44 reports `no LAND entries at reader creation` (the
documented low-confidence path — its LAND byte is mesh-yield-resolved after cell enumeration, so
the follow stays ungated there) and converts **byte-identical** to the round-9 final `rw_xex44.esm` (99,891 records, 14,150,622 B, probe margin 11 — so the "margin 11" the revert note quoted was xex44's; xex21's own is 9). The gate's blast radius is the CSV,
exactly as the revert note predicted.

**Single-shot `--capture-topdown` settle gate.** The loop broke on the first `IsComplete`; the
batch path never trusted that (quiescence can report true between one stream stage finishing and
the next being requested) and demands two consecutive complete passes agreeing on the drawn
count. The single-shot path now requires the same, plus pixel identity between the two passes
(FNV-1a over the readback; the overlay pins its water clock, so a settled scene is pixel-stable
while a mid-stream pass cannot repeat its image). This is the defect behind the 0x000DDFA1
"water" capture — complete=True at attempt 18 with terrain colour not yet resident.

**2D-panel mirror of the master-terrain toggle.** `WorldMapSettingsPanel` gets its own
`MasterTerrainCheckBox` (Visibility expander, DMP-only via `WorldMapControl.LoadData`), raising
the same-shaped `MasterTerrainPreviewToggled`; the tab owns the state and pushes `IsChecked` to
both checkboxes on any change (the source write is a no-op, the mirror's event re-enters only to
hit the equality early-out). The default view mode is 2D, so the toggle is now reachable where
its effect is visible.

**`TESWaterForm.pWaterWeatherControl` — read at last.** The parity matrix carried it as "unknown
PDB type"; the layout DB says `TESWaterForm *[]`, 12 bytes at +344 — three water pointers, i.e.
the engine's slot for WATR **GNAM Related Waters** (Daytime / Nighttime / Underwater in xEdit
order). `RuntimeWaterReader.ReadRelatedWaters` reads them type-validated as WATR into
`WaterRecord.RelatedWater` under the GNAM schema's field names (so `WatrEncoder` emits GNAM
unchanged); an unresolved slot records FormID 0 like retail, and a record with no evidenced slot
carries null so the runtime overlay (`RecordModelUnion.Fill`, fill-only) can never override an
ESM-parsed GNAM with zeros. Two tests. Matrix row updated.

**LSCR LNAM — exercised on real data, and a latent schema hole closed.** `esm semdiff` of every
LSCR in the whole-master 360→PC conversion against retail: all identical except 2 records whose
two LNAM subrecords appear in the opposite order — the three-way byte diff shows the **Xbox master
itself** stores them reversed (unordered location list; genuine content, now in CLAUDE.md's
known-differences list). The exercise also showed the 12-byte LSCR LNAM (Direct, Indirect, Grid Y,
Grid X) matched the generic 4-byte LNAM schema — byte-correct on retail only because every retail
tail is zero, wrong by construction for a nonzero grid pair (two int16s swapped as one dword). A
`(LNAM, LSCR, 12)` schema now names the four fields; pinned by `LscrLocationSchemaTests`.

**ASPC / MSET "missing from `HasSpecializedReader`" — by design, now documented.** Both typed
readers produce a `GenericEsmRecord` and are reached only through
`RuntimeStructReader.ReadGenericRecord`'s per-type switch, which `MergeRuntimeGenericRecords`
invokes precisely for the types the set does NOT contain; listing them would skip that merge and
neither reader would ever run. The set carries the explanation; the only cost of the omission is
one redundant shift-probe grouping.

**Validation.** Full both-TFM build green for every non-test assembly (main, WinUI App with the 2D mirror, both profilers with the settle rule); the shared tree's test project needed three of the concurrent session's mid-edit files sidelined to compile, then the fast suite ran 10,857 tests: 10,574 green, 273 skipped, 10 failed — all ten the concurrent session's rendering/water/Starfield source-contract pins (down from 43 in the previous pass as they progress), none touching terrain, cells, LAND, probes, the water reader, the LSCR schema, capture options or either settings panel. Every touched class (`LscrLocationSchemaTests`, `RuntimeWaterReaderRelatedWatersTests`, `RuntimeParityMatrixTests`, `XamlAccessibilityRatchetTests`, both settings-panel source-contract classes, `RendererProfilerOptionsTests`, `EsmLandEnricherMasterTerrainTests`, `RecordCollectionCellMergeTests`) passed. Conversions: xex21 and xex44 both byte-identical to their baselines with the probe decoupled and the gate live on xex21.


---

## Round 10 — the five follow-ups from the recovery-confidence review (2026-09-03)

The user read the domain-by-domain confidence assessment and came back with five items. Two were
rulings, one was a correction to my framing, and two were questions whose answers contradict what
that assessment had implied. All five are settled below.

### 1. Compiled scripts keep their bytecode; source is emitted, decompiled if not captured

**USER RULING:** "compiled scripts should be preserved (converted to PC format), while their
source text should be emitted if present, or as the decompilation if not."

`CapturedScriptEmissionContract` previously had four paths that shipped SCDA with no SCTX: no
captured text at all, text with no same-dump provenance, text that failed the statement comparer,
and text whose declaration block disagreed with SLSD/SCVR. All four now fall back to
`BuildDecompiledSource`, which renders the accepted SCDA as GECK-shaped source.

The part that needed real work is the declaration block. FNV bytecode carries **no** declaration
opcodes — VarShort/VarLong/VarFloat (0x12/0x13/0x14) never appear in a shipped SCDA, measured
against the retail PC master this round — so the scn-plus-locals block has to be synthesized from
the record's own SLSD/SCVR table. That is also what makes the result safe: it is generated from the
exact table shipping beside it, so it satisfies the declaration audit by construction, and
`EvaluateStandalone` therefore skips re-auditing synthesized text (re-running it would only
re-derive its own inputs, and on a table too incomplete to declare would discard the very text the
ruling exists to guarantee).

Two details worth recording:

- **ref vs float is not in SLSD.** The type bit is 0 for *both*; retail confirms it (`rBox01`,
  `ActivatorREF` and `fRange` are all type 0). Ref-ness comes from bytecode evidence instead — only
  a reference local can appear in member position (`name.Something`) in the decompiled statements —
  and a type-0 local with no such use is declared `float`. Both are non-integer storage, so either
  declaration satisfies the SLSD check; the banner tells a reader to trust SCDA if they disagree.
- **EDID wins over the decompiled name.** A script's identity is its EDID, and an SCTX whose scn
  line names something else is exactly what disqualifies a captured text. The synthesis therefore
  overrides the decompiler's ScriptName line with the record's resolved EDID.

`ScriptSourceTextOrigin.DecompiledFromBytecode` keeps the substitution visible: provenance reports
label it `decompiled-from-bytecode`; `SourceTextCorrespondenceStatus` still reports **Rejected** for
a capture that was replaced (the status describes the capture, not whether text shipped); and
`InlineScriptReferenceValidator` no longer reports "SCTX omitted" for a record that now has SCTX.

**Measured on xex44 (same dump, same master, before/after):**

| | before | after |
|---|---|---|
| script blocks carrying SCDA | 886 | 886 |
| ...of those with **no** SCTX | **224** | **0** |
| SCTX subrecords | 697 | 921 |
| records emitted | 99,891 | 99,891 |
| output bytes | 14,150,622 | 14,285,552 |

Validation and the full semantic check pass unchanged. The 224 scripts that shipped as opaque
bytecode now ship readable.

### 2. "We should likely have specialized readers for this?" — no; they already exist

This was the useful correction. The confidence assessment listed CAMS/IDLM/MSET/IPDS/EFSH/CHIP/
CSNO/DOBJ/INGR as an emission gap and implied missing readers. Checking rather than assuming:

- The **loader already recovers every one of them** from the dump. `stats` on the raw xex44 dump:
  CAMS 229, IDLM 185, MSET 79, IPDS 48, EFSH 32, CHIP 5, CSNO 5, DOBJ 1, INGR 1.
- Every one has an **encoder** (`CamsEncoder`, `IdlmEncoder`, ... all present), a
  **`NewTopLevelRecordEncoderDispatcher` row**, a **`DmpRecordSource` catalog row**, and a
  **`PluginConversionPipeline` yield**. Eight of the nine also have a planned encoder; INGR does
  not, and is the single "No planned encoder" warning in the log.
- The stale `artifacts/dmp-audit/census2026-08-25/mapping_coverage.csv` reports `has_encoder=NO` for
  these. That column is wrong now — LSCR/ASPC/MSTT/RGDL/TACT/ANIO all emit today.

The real cause is one line. Forty-plus types are registered through `PlannedEncoders.Simple<T>`,
whose `DelegatingPlannedEncoder` reads:

    RecordDisposition.New      => encodeNew(model),
    RecordDisposition.Override => _emptyEncoded,   // an empty record

Confirmed by walking the output: **every** emitted generic record is in the plugin's own 0x01
range — ASPC 58 new / 0 override, MSTT 96/0, LSCR 3/0, TACT 3/0, RGDL 2/0, ANIO 8/0. Only 14 types
emit real overrides at all (ALCH, AMMO, ARMO, BOOK, CELL, GLOB, GMST, NPC_, PGRE, QUST, SCPT, WEAP,
WRLD, NAVI). So the nine "missing" types emit nothing because **every instance the dump holds pairs
with a master record**, and a master-paired record of a `Simple<T>` type produces an empty override
that is then dropped.

**70 record types are registered this way.** For all of them a DMP-captured delta against an
existing master record is discarded by design. That is a far larger finding than the nine types, and
it is an emission-policy question rather than a defect — recorded for a ruling, not changed.

### 3. LZX — DDXConv already carries our decoder, and the probe now uses it

`src/DDXConv/DDXConv/Compression/LzxDecompressor.cs` is a 976-line pure-managed XMemCompress
decoder, mechanically translated from a C reference verified byte-for-byte against XnaNative.dll on
3,870 DDX files, and DDXConv is already a `ProjectReference` of the main assembly. The user was
right; nothing needed porting.

This matters because `dmp recovery-probe`'s compression branch tried **zlib**, and every candidate
failed with "unsupported compression method". That result measured the wrong decoder, not the dump,
so "is there compressed data in memory we are blind to?" was never actually answered.

New `Core/Recovery/LzxGapProbe.cs` runs the real decoder over gap bytes, and `dmp recovery-probe`
gained an LZX branch writing `recovery_probe_lzx.csv` beside the other two.

The design point that cost the most thought: **acceptance has to be content-based.** An
XMemCompress chunk header is a bare 2-byte big-endian length, so nearly every offset is a
syntactically valid header — and the decoder cheerfully inflates pseudo-random bytes into kilobytes
of pseudo-random output. Neither framing nor output size discriminates. The gate is therefore
whether the inflated payload is *identifiable* (container magic or text), pinned by a test that
feeds the probe pure noise, plus a companion test that runs with the gate off to document why the
gate exists. Stated limitation: a stream whose first chunk was paged out, or one holding a payload
with no magic and no text, is invisible — a miss is not proof of absence.

Two defects in the probe surfaced by running it rather than trusting it, both fixed:

- **Cost.** Inflating every coincidental decode to the 8 MB cap made a whole-dump sweep
  unfinishable (killed after ~20 min without a first CSV row). Screening now decodes into a 64 KB
  budget — the sniff only reads 512 bytes — and only survivors are re-decoded at full size.
- **The sniff was too loose.** A 90%-printable average over 512 bytes classed as "text" two
  payloads that began `00 00 00 00 00 00 …` and `61 04 24 00 …`. A recovered file starts with its
  own content, so the lead bytes must read as text too. Those three real payloads are now the
  test's `[InlineData]` cases.

**Measured on xex44 (231 MB, ~189 MB of it uncovered gap): one hit.** Offset `0x009C73EC`,
15,625 bytes consumed producing 11,321 — a "compression ratio" of 0.72, i.e. output *smaller* than
input, which is impossible for genuine compressed data. Its payload begins `,9=amb_vault_11_`: the
decoder walking an already-plaintext sound-name table and echoing it. Zero container magics, zero
streams with a real ratio. zlib, as before, scored 0 of 50.

**Conclusion — the lead is closed, now for the right reason.** The confidence assessment called
LZX "the one genuinely unopened door" in the asset domain. Opened with the correct decoder, there
is no meaningful body of XMemCompress-compressed data in this dump's uncovered gaps. That is what
should be expected in hindsight: the console decompresses assets at load, so compressed bytes live
on disc in the BSA and their streaming buffers are transient. Carried caveat: one dump, gaps only,
first 4 MB of each gap, identifiable payloads only.

### 4. Dialogue — the placeholder already ships, and the prune rule would fire once

The user's rule: dialogue with no player line **and** no response text is not worth emitting; with
no response text there is already the `(NOT FOUND IN CRASH DUMP)` placeholder.

Both halves check out, and the numbers correct something the confidence assessment got wrong. That
assessment counted raw NAM1 occurrences and concluded "essentially every INFO carries its response
text". Counting *content* instead:

| | xex44 | xex21 |
|---|---|---|
| INFO / DIAL records | 1,464 / 1,203 | 2,184 / 2,134 |
| responses with real text | **612** | **722** |
| responses that are the placeholder | **831** | **1,486** |
| TRDT with no NAM1 at all | 0 | 0 |
| topics carrying a player line (DIAL FULL) | 1,202 of 1,203 | 2,132 of 2,134 |
| INFOs with no real text **and** no topic player line | **1** | **1** |

So the proposed prune rule would drop exactly one record per dump — emission is already as tight as
that rule would make it. `DialogueTextBackfill.PlaceholderText` is emitted by the encoder and
respected by `DialogGrupBuilder` and `DialogueInfoOverlayWriter`, so the placeholder half is done.

The actual gap is the **831 placeholder responses**: INFOs that kept their TRDT (response slot,
emotion, sound) and their topic's player line, but whose spoken text was not resident. There is no
runtime fallback — the parity matrix records INFO response text as `esmOnly`, meaning it lives only
in ESM pages, never in a runtime struct. The built recovery route is `--dialogue-audio-csv`
(Bethesda Audio Transcriber exports, one row per voice file, keyed to a response index), which
requires `--pack-assets`. Running the transcriber over the captured dialogue audio is what turns
those 831 into real lines.

### 5. The owed ruling (W2), stated concretely

Two heuristic passes rescue placed refs whose parent CELL was never captured, and they disagree
about survival:

- `ReattachUnresolvedByBoundsInference` synthesizes **real** exterior cells (`IsVirtual = false`)
  and its own doc block explains why: "the planner removes virtual cells as parse-time buckets,
  which would silently kill the temporary refs this pass exists to save."
- The offset-cluster passes (`CellWorldspaceAuthorityApplier` ~line 1189 and
  `OffsetClusterOrphanResolver:82`) synthesize cells with `IsVirtual = true`, so the planner deletes
  the tile and the refs it rescued die with it.

The inconsistency is that the *weaker*-evidenced pass survives while the stronger one self-defeats.
The decision is one flag: should offset-cluster tiles be created as real cells like the
bounds-inference tiles? Stakes are smaller than the old headline suggested — Round 9's
reclassification showed 2,593 of 2,602 dropped children were `PersistentRedistributedSynthetic`
copies rather than unique content, and the current xex44 run reports 86 allocated refs across 18
cells unemitted. Nothing here changes without that ruling, because it changes what gets emitted.

---

## Round 11 — rescue-all ruling, and whether the dump's text ties back (2026-09-03)

### The W2 ruling, applied and then measured

**USER RULING:** "We should aim to rescue all refs. If that requires a real cell, then that's what
we should do."

Applied to every pass that fabricates a tile for an orphaned ref. The result splits cleanly:

**Kept — offset-cluster tiles are now real cells.** `CellWorldspaceAuthorityApplier`'s
offset-cluster tile and `OffsetClusterOrphanResolver` both built `IsVirtual` cells, which the
planner deletes as parse-time buckets, so each pass rescued refs into a cell that then took them
down with it — while the *weaker*-evidenced bounds-inference pass survived by building real cells.
Both now build the same thing: a real exterior cell keyed by grid + worldspace with no EditorId
(CK convention — the old `[Virtual x,y <ws>]` label would otherwise have been emitted into the
plugin). `OffsetClusterOrphanResolver` additionally gained the master-grid check it never needed
while its tiles were doomed: if a real cell already owns that grid, the ref is adopted into it
rather than a second cell appearing at the same coordinates, which is not a legal worldspace.
Measured on xex44: **no change** (99,891 records, 81,594 placed refs, 86 refs across 18 cells
unemitted) — this pass does not fire on that dump. It is now correct and consistent rather than
self-defeating, which is what the ruling asked for.

**Reverted — the redistributor tile cannot simply become real.** `PersistentRefRedistributionPass`
is where the great majority of dropped children actually die (Round 9: 2,593 of 2,602 are
`PersistentRedistributedSynthetic`), so it looked like the big win. Flipping it made things
measurably **worse**:

| xex44 | virtual (before) | real (flipped) |
|---|---|---|
| records emitted | 99,891 | 96,739 |
| placed refs | 81,594 | **78,379** (−3,215) |
| **overrides** | 79,659 | **76,490** (−3,169) |
| skipped | 6,143 | 8,686 |
| refs unemitted (WARN) | 86 across 18 cells | 69 across 12 cells |

The loss lands in **overrides**, which names the mechanism: this pass runs at parse time and knows
nothing about the master, so a tile it synthesizes at grid (gx, gy) collides with the master's own
cell there. Emitting the synthetic cell displaces the master-cell override and takes every child
that override was carrying — more refs lost than the flip rescues. Isolating the two changes
confirmed this pass alone accounts for the whole regression.

Rescuing these properly needs the master grid index
(`PluginConversionPipeline._masterExteriorCellByGrid`) consulted *before* a tile is synthesized, so
the ref lands in the master's own cell instead of a duplicate. That is a planner-side change, not a
flag flip, and the code now says so where the flag is set. The `[Unassigned Interior]` catch-all is
deliberately left virtual on different grounds: it names no specific cell, so emitting it would
invent an interior that never existed and fill it with objects from unrelated rooms.

### "Can the abundant text be tied back?" — we had never asked

The user's question: are we following pointers correctly, in both directions, and is the seemingly
abundant text really untieable?

**The pointer machinery is sound and genuinely bidirectional.** `RuntimeBufferPointerAnalyzer`
combines a *forward* pass (`BuildDirectOwnerClaims` — typed runtime structs and manager/global
walkers naming their own string fields) with a *reverse* pass (`ScanInboundPointers` — one sweep
over every memory region, testing each 4-byte-aligned word against the set of string VAs). A string
is Owned only with a typed claim; Referenced when something points at it but no owner matches;
Unreferenced when nothing points at its start.

**But it only ever ran on strings a shape heuristic had already recognised.**
`RunStringOwnershipAnalysis` filtered on `IsMeaningfulCategory`, i.e. anything but
`StringCategory.Other` — and the classifier is deliberately conservative in ways that matter:
a dialogue line needs 25+ characters *with spaces* (so "Yeah." and "I'm listening." are `Other`),
an EditorID needs 6+ characters starting uppercase. On xex44 that left **880,167 of 1,030,933
strings — 85% — never examined at all**. "Unattributed" was therefore substantially a statement
about our classifier, not about the dump.

The filter also bought nothing: the reverse scan is one pass over the dump testing a hash set, so
its cost tracks dump size, not hit count. Including everything means a bigger hash set.

**Measured on xex44 with every string analysed** (`dmp buffers` now prints this; it was computed
before and simply never shown):

| | total | owned | referenced, owner unknown | unreferenced |
|---|---|---|---|---|
| EditorId | 106,848 | **98,355** (92%) | 2,108 | 6,385 |
| GameSetting | 634 | **603** (95%) | 14 | 17 |
| DialogueLine | 11,533 | **8,735** (76%) | 465 | 2,333 |
| FilePath | 31,751 | **23,845** (75%) | 2,696 | 5,210 |
| **Other** | **880,167** | **15,863** | **133,784** | 730,520 |
| **all** | 1,030,933 | 147,401 (14.3%) | 139,067 (13.5%) | 744,465 (72.2%) |

Two conclusions, and they point opposite ways:

1. **Where we do follow pointers, it works.** 92% of EditorIDs, 95% of game settings, 76% of
   dialogue lines and 75% of file paths resolve to a typed runtime owner. That is the direct
   evidence the user asked for that forward and reverse resolution are behaving.
2. **The answer to "can the rest be tied back" is no longer "we assume not".** Of the 880,167
   strings previously excluded, **15,863 turn out to be *owned*** — already attributable, discarded
   purely because their shape did not match a heuristic — and a further **133,784 have live inbound
   pointers**. Roughly **150,000 strings, 17% of the excluded set, are demonstrably live data we
   were not asking about.**

The 730,520 unreferenced `Other` strings remain the honest floor, with one caveat worth stating:
"unreferenced" means no 4-byte-aligned pointer to the string's *exact start*, so text reached by an
interior pointer, an index into a pool, or a packed handle is indistinguishable from noise by this
test. It is a lower bound on liveness, not proof of garbage.

Also visible in the same run and worth its own follow-up: of **31,751 file paths the dump names,
only 31 matched a carved file.** The dump tells us the names of thousands of assets whose bytes we
did not recover.

The CSV dumps of the unknown/unreferenced sets are capped at 20,000 rows (the counts in the summary
report stay exact, and a capped file says so on its last line); the unknown-owner CSV is now ordered
most-referenced-first, so the strings most worth naming survive the cap.

### Round 11b — digging further into ownership (2026-09-03)

The user asked whether more owners could be assigned to the strings that have live inbound pointers
but no owner (129k–134k after Round 11). Two more strategies, each measured on xex44.

**Asset paths are closed as a question.** USER: the 31,751 named paths against 31 carved files is
"a known limitation of this being a memory dump and not a full build — hence the need to backfill
with other prototype and final data." No action; the donor pipeline already covers it.

#### 1. The exact-text matchers were gated on the same classifier

`SecondPassOwnershipResolver` already runs EditorID / GameSetting / DialogueLine text matching over
unresolved hits, and each is an **exact dictionary lookup** against an inventory we recovered. But
each also began with `hit.Category != StringCategory.X → return null`, so the shape classifier
gated evidence it has nothing to do with. A string that *equals* a known EditorID is that EditorID
whether or not it looked like one — and since the classifier needs 25+ characters with spaces for
dialogue and 6+ starting uppercase for an EditorID, exactly the short lines and lowercase IDs that
fall to `Other` were denied the one test that would have named them.

Unclassified text may now attempt these matches (`CanTryTextMatch`), subject to a 6-character
minimum: very short text is where an exact match stops being identity and starts being coincidence
("Yes" can equal a real dialogue line by accident). Classified hits are unaffected.

`TryCFormEditorIdFallback` stays gated, deliberately: it is **positional** — it infers ownership
from a string sitting at a plausible offset near a vtable, not from equalling anything known — and
its own comment notes TESForms are densely packed. Opening that to arbitrary text would let any
bytes near a form claim an owner on position alone.

#### 2. New strategy: runtime-object containment

Every existing strategy has to *recognise* something first — a BSStringT wrapper, a resolvable
vtable, text equal to a known value — so all of them fail on the ordinary case of a string held in
a field we have no layout for, which is most of what remains.

Containment needs none of that. A runtime TESForm occupies
`[TesFormPointer, TesFormPointer + StructSize)` — the start is a confirmed form, the size comes
from the PDB — so a referrer address *inside* that span is by construction a pointer field of that
form. `OwnershipContainmentResolver` builds a sorted span index over every runtime form with a
known layout and binary-searches each referrer. The claim is the form's identity plus the raw field
offset (`+0x4C`): weaker than a named field, strictly better than "owner unknown", and it is real
evidence rather than proximity — the address is inside the object or it is not.

Two deliberate conservatisms: overlapping spans are **dropped entirely** rather than arbitrated
(an overlap means some struct size is wrong for this build, and containment's whole claim to
strength is that it is unambiguous), and offset 0 is rejected because that is the vtable slot.
It is ordered after the field-naming strategies and before the positional fallback.

#### Measured, xex44

| stage | owned | referenced, owner unknown | `Other` owned |
|---|---|---|---|
| before Round 11 (only classified text analysed) | — | — | not examined |
| Round 11 (all text analysed) | 147,401 | 139,067 | 15,863 |
| + text matchers opened | 152,094 | 134,374 | 20,556 |
| + containment | **153,622** | **132,845** | **22,078** |

So the `Other` bucket went from never being asked, to **22,078 named owners** — a 39% gain over
the 15,863 that the first pass alone found. Category totals for classified text are unchanged, as
they should be: nothing about those paths was altered.

**Where the remaining headroom is, and why containment gained only ~1,500.** That small number is
itself the finding: most referrers do *not* live inside a known TESForm. The pointers into the
still-unowned 127,566 come from objects absent from the runtime form inventory — NetImmerse
scene-graph nodes, UI/menu objects, list and container nodes, allocator blocks. Naming those means
extending vtable/RTTI resolution beyond TESForm classes, which needs an RTTI class inventory rather
than the PDB form layouts, and is a materially bigger job than either change here.

The 730,519 unreferenced strings are unchanged and untouched by any of this, with the caveat from
Round 11 still standing: "unreferenced" means no aligned pointer to the string's exact start, so
text reached by an interior pointer, a pool index or a packed handle is indistinguishable from
noise by this test.

---

## Round 12 (2026-09-04) — RTTI object inventory: build the instrument, measure the prize, stop

Round 11b ended by naming the next lever: extend ownership resolution beyond TESForms, which needs
an RTTI class inventory. **USER RULING 2026-09-04: stop at the measurement gate** — build the
inventories, report the numbers, and let the claim resolver be a separate decision made from that
data. Second ruling: **reports now, emission-ready shape** — ownership stays diagnostic, but the
claim carries a confidence tier so a later ruling can promote strong claims without reworking the
type. Nothing in this round changes what gets emitted, and no string's ownership status changes
because of it.

All numbers below are measured on `Sample/MemoryDump/Fallout_Release_Beta.xex44.dmp`
(231,173,620 bytes) on 2026-09-04.

### The gate: 10.4%

| | count | of unowned |
|---|---:|---:|
| Unowned strings examined | 132,707 | |
| **…with a referrer inside a located object** | **13,756** | **10.4%** |
| …of those, inside an object of *declared* size | 1,407 | 1.1% |
| Module-only referrers (globals) | 495 | 0.4% |
| Mixed referrers / heap-only | 122 / 132,090 | |

**Read it as: 8.6× better than TESForm containment (1.2%), and still a tenth of the target.** The
honest qualifier is the second row — 12,349 of the 13,756 rest on an *inferred* ≤512-byte extent
(distance to the next object base, capped), which is a bound rather than a fact. Only 1,407 sit in
an object whose size the PDB actually declares.

⚑ **This is the strongest argument for the deferred `pdb_class_sizes.json` work.** Today 179,152 of
356,764 located objects (50.2%) have a declared size, because only 565 classes are in
`pdb_layouts.json`. `CvdumpParser.Structures` already parses 14,778 sized types and serializes none
of them; wiring that in is what would move the 1,407 rather than the 13,756.

⛔ **The globals lead is CLOSED as a negative — do not build it.** Module-space referrer VAs are
stored sign-extended, arrive negative, and are skipped by every strategy
(`SecondPassOwnershipResolver` ~154, `OwnershipContainmentResolver` ~174, `OwnershipTextMatcher`
~258). Real defect, and it costs **495 strings, 0.4%**. `PdbGlobalResolver` integration would have
been a phase spent on noise.

### What actually holds the unowned strings

Top classes by strings held (of 132,707 unowned):

```
?$NiTObjectArray@V?$NiPointer@VNiAVObject  2,296    hkpRigidBody             470
hkpFixedRigidMotion                        1,096    AnimSequenceSingle       373
BSAnimGroupSequence                        1,050    ?$BSSimpleArray@UPathingDebugEvent 350
?$NiTPrimitiveArray@PAUFaceGenUndo           823    bhkCollisionObject       326
?$BSSimpleArray@PAUValue@Tile                768    BSTreeModel              320
TESAnimGroup                                 740    ExtraScript              267
```

Scene-graph arrays, Havok bodies, animation sequences, UI tile arrays. They hold node and animation
names — real asset data, but **not ESM record content**, which is consistent with the standing
ruling that missing asset paths are a memory-dump limitation to be answered by donor backfill.
Note how many are templates: naming their *fields* would need a real MSVC demangler, and cvdump's
own `class name = …` comma truncation caps that regardless.

### `DumpRttiIndex` — forward walk beats the heap census

`Core/Minidump/DumpRttiIndex.cs` (+ `ModuleImage`, `MsvcNameDemangler`, `DumpRttiClass`,
`DumpRttiVtable`), exposed as `dmp rtti --census-forward`. Walks the module's own tables:
`.?AV`/`.?AU` name → TypeDescriptor → CompleteObjectLocator by back-reference → vtable by
back-reference → ClassHierarchyDescriptor for the base chain.

| | forward walk | `RttiReader.RunCensus` |
|---|---:|---:|
| classes | **1,795** (847 with a live instance) | 857 (`--all-regions`) / 452 (default window) |
| vtables | **2,386** (1,856 primary, 530 secondary/MI) | — |
| type descriptors | 2,030 (173 with no locator) | — |
| bytes read | 21,430,272 (one contiguous run) | ~220 MB over 4,014 regions |

Module image 21,430,272 of 24,156,160 bytes captured (88.7%); vtable band
`0x82000A44-0x8217C7B8` (1,555,828 bytes). Standalone `--census-forward` on xex44: **18.8 s**
including minidump parse. Full `dmp buffers`: **177.8 s**.

- ⚑ The 1.55 MB vtable band is what makes the whole-dump object sweep affordable: fused into the
  existing `ScanInboundPointers` pass, two comparisons reject nearly every word before any hash
  probe, so the dump is still read once.
- ⚑ **356,764 distinct object bases from 549,034 vtable words**, and only **9 bases claimed by more
  than one class** — multiple-inheritance ambiguity is a non-issue in practice, so
  `OwnershipContainmentResolver`'s "drop all overlaps" conservatism would cost almost nothing if
  generalised.
- ⚠ Class count is **1,795, not the 1,968** a design prototype reported: this counts classes reached
  through a *vtable*, which is the only kind detectable in memory. 173 type descriptors have no
  locator at all.
- ⚠ **135 TESForm-derived vs the offline scanner's 427** (different build, different tool). Not
  reconciled; treat neither as authoritative until someone does.
- Nested names now demangle (`Tile::RefValueAction`); templates are deliberately left in their raw
  form, exactly as `RttiReader.DemangleName` always produced, so nothing regresses.

### Three claim-source defects — the report was lying about its own evidence

`ClaimSourceCounts` was populated in two places and **rendered nowhere**, and three of its nine
values were wrong:

1. `SecondPassVtable` was emitted by BOTH the field-named RTTI hit and the positional cFormEditorID
   guess — strongest and weakest vtable evidence sharing one counter. Split out
   `SecondPassCFormEditorIdPosition`.
2. `ClaimSource.SecondPassReverseRelaxed` was **assigned nowhere**: `SecondPassOwnershipResolver`
   passed the strict value unconditionally, so the relaxed lookup (BSStringT length validation
   skipped) reported itself as strict.
3. `ClaimSource.RuntimeEditorId` was **assigned nowhere**: the claim omitted it and took the record
   default, so **50,351 claims — the single largest bucket — were reported as `ManagerGlobal`.**

New `OwnershipConfidence` tier (FieldNamed / ExactTextMatch / OwnerNamed / Positional), derived from
the claim source rather than stored so the two can never disagree, rendered in both `dmp buffers`
and `string_ownership_summary.txt`. This is the emission-ready shape from ruling 2. First reading of
the existing owned population:

```
FieldNamed        91,251  59.4%   RuntimeEditorId 50,351 · RuntimeStructField 28,995
                                  SecondPassVtable 10,172 · SecondPassReverse 1,733
ExactTextMatch    59,101  38.5%   TextContentMatch 59,101
OwnerNamed         2,993   1.9%   SecondPassContainment 1,528 · SecondPassReverseRelaxed 1,465
Positional           277   0.2%   SecondPassCFormEditorIdPosition 277
```

Also: owned `Other` strings now reach CSV (`string_owned_other.csv`) — 22,080 of them were counted
in the summary and then dropped on the floor — and the owned CSVs gained the row cap the
unattributed ones already had, since `Other` would otherwise turn a few-MB export into a
hundred-MB one.

### D1 — a real region-crossing defect whose measured impact is below the noise floor

`OwnershipVtableResolver` stepped back through **file-offset** space
(`referrerFileOffset - backOffset`) while computing the object base in **VA** space
(`referrerVa - backOffset`). Minidump regions are contiguous by file offset with arbitrary VAs, so
any referrer within 512 bytes of a region start read the **previous region's** bytes and attributed
that unrelated allocation's vtable — and its field label — to the string.
`OwnershipTextMatcher.TryCFormEditorIdAtReferrer` had the identical flaw at `-16`.

Both now read one VA-space window via `RuntimeMemoryContext.ReadBytesAtVaInto`, which fails closed
at boundaries and replaces up to 129 four-byte reads with a single stitched read.
`OwnershipRegionBoundaryTests.ReferrerAtRegionStart_DoesNotAdoptThePrecedingRegionsVtable` plants a
resolvable `TESModel` chain in the preceding region and **fails on the pre-fix code**.

⚠⚠ **But the A/B is inconclusive, and that is the honest result.** Same binary, same dump, only the
scan reverted: `SecondPassVtable` **10,172 in both**, gate **13,756 in both**, Owned **153,622 in
both**, FieldNamed 91,252 → 91,251. And the *Analyzed* count itself moved, 1,030,492 → 1,030,494 —
so **string extraction has run-to-run variance of the same order as the difference being measured**.
Conclusions: (a) D1 is a correctness/robustness fix, worth having and cheaper, but it is NOT worth
thousands of false claims and must not be described as such; (b) **ownership counts carry a small
run-to-run variance — treat single-digit deltas as noise, not signal.** The variance itself is a
new, unexplained finding; cause not identified (the machine was under heavy load from concurrent
sessions, so a racing MMF read is one candidate). Do not re-chase D1 for a bigger number.

### Verification

93 tests in `Core.RuntimeBuffer` + `Core.Minidump` pass (92 succeeded, 1 Bucket-B skipped). New:
`OwnershipConfidenceMapTests` (every claim source maps to a tier; tier ordering pinned),
`OwnershipRegionBoundaryTests`, `MsvcNameDemanglerTests`, `DumpRttiIndexTests`,
`RuntimeObjectCensusTests`. One of those caught a real error in its own fixture — a truncation point
that did not actually truncate the vtables — which is the behaviour the "a test must be able to
fail" rule is for.

⚠ **Build/test ran in an isolated copy at `C:\tmp\x360-rtti`, not the working tree.** A concurrent
session had mid-edit breakage in `NpcHeadPartAttacher.cs`, `FaceGenHairEgmPathResolverTests.cs` and
`ClassicGameRootMountTests.cs`, plus a WinUI build holding the obj/bin locks, so the working tree
was unbuildable for the duration. The copy carries only this round's 26 files over an otherwise
untouched tree; their files were never modified. **Absolute counts here are therefore not directly
comparable to Round 11b's** (which was measured on the full working tree with ~210 modified files) —
the A/B pairs above are internally consistent, cross-round absolute comparisons are not.

### Still deferred (unchanged by this round)

`pdb_class_sizes.json` + a `PdbAnalyzer classsizes` command; the claim resolver and its tiers; the
ancestry-keyed field index (the base chains are now recovered, so "derives from `NiObjectNET` ⇒ `+8`
is `m_kName`" is one step away); interval-containment field lookup to replace
`OwnershipVtableResolver`'s exact-offset `FirstOrDefault`; retiring `TryVtableReverseLookup`;
template demangling. Separately: the 70-type `Simple<T>` empty-override policy remains open.
