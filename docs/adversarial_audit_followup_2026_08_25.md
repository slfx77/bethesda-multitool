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
- The two DDX-adjacent carver defects logged above (`CarveWriter` GUID/manifest race; 1,854 `.ddx`
  rejected on a `0xFF` priority byte).
- M2 (LZX→BSA), held pending a real XMem-flagged archive.

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
