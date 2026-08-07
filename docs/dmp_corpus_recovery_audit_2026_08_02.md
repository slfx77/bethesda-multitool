# DMP Corpus Recovery Audit — 2026-08-02

Corpus-wide audit of `Sample/MemoryDump` (50 dumps) asking three questions: what is
recoverable, where are the identification/mapping gaps, and how does availability vary
across the ~5 months of development the dumps span.

Reproduce with:

```bash
BethesdaMultitool.exe dmp formtype-census Sample\MemoryDump --csv artifacts\dmp-audit
BethesdaMultitool.exe dmp gap-inventory   Sample\MemoryDump -o artifacts\dmp-audit\gaps-corpus --fast
```

Artifacts: `artifacts/dmp-audit/{dump_inventory,formtype_by_dump,esm_record_by_dump,mapping_coverage}.csv`.

## 0. Corpus shape

50 dumps, **27 unique PE timestamps**, spanning **2009-11-17 → 2010-04-21**.

Two distinct date axes, and they are not the same thing:

- **Build date** = PE timestamp of the game module. The build's identity; 27 unique values.
- **Capture date** = file mtime. When that build actually crashed, 0–3 days later. This is
  the axis content questions want ("did this exist yet?").

Filename ordinals do **not** track chronology — `xex39` sits between `xex32` and `xex33`,
and `xex9` sorts after `xex17`. Any analysis keyed on filename order is wrong.

## 1. 18 of 50 dumps contain no game data at all

`has_runtime_form_data = no` for 18 dumps. These are **not** a tooling failure:

| Batch | Count | Build dates | Size |
|---|---|---|---|
| xex8–xex17 | 10 | 2010-01-07/08 | 146 MB |
| xex33–xex38 | 6 | 2010-04-09/12 | 145 MB |
| xex40, xex41 | 2 | 2010-04-14 | 148 MB |

Evidence they are genuinely dataless rather than unparsed:

- They parse fine — 2,600+ memory regions, all 8 modules present, game module resolved.
- `pAllForms` is absent, so zero runtime forms.
- A known EditorID (`SewardSquareLvlTalonCompanyGun01`) is **byte-absent**.
- `"Goodsprings"` returns exactly **3** hits in every one of them (static strings in the
  executable) versus **601** in a loaded dump.

They are pre-data-load captures — the game crashed before the master finished loading.
All are 145–148 MB; every dump with data is 163–264 MB. **Effective corpus = 32 dumps.**

This mattered immediately: before correcting for it, every record type in the census
reported "32/50 dumps", which reads as drift but is just the 18 dead dumps.

## 2. Content growth is real and datable

Runtime forms grow monotonically: **49,249 (2009-11-17) → 63,239 (2010-04-21)**, +28%.

Six record types are introduced mid-corpus, and the distinct-type count tracks them exactly
(86 → 89 → 90 → 92):

| Types | First build | Dump |
|---|---|---|
| CHIP, RCCT, RCPE | 2010-03-10 | xex22 |
| CSNO | 2010-03-25 | xex23 |
| ALOC, MSET | 2010-04-09 | xex32 |

**Worked example — Ulysses.** Hits for `"Ulysses"`, by build:

| Build | Dumps | Hits |
|---|---|---|
| 2009-11-17 → 2010-01-11 | 10 | **0** |
| 2010-01-27 (Debug) | 3 | 27 |
| 2010-02-03 (xex20) | 1 | 8 |
| 2010-02-16 (xex21) | 1 | 39 |
| 2010-03-10 (xex22) | 1 | 421 |
| 2010-03-25 → 2010-04-21 | 15 | 485–502 |

He does not exist before 2010-01-27 and his content is only substantially built out from
2010-03-10. **This has a direct bearing on v137** — see §6.

## 3. Engine layout drift

- **`pAllForms` VA moves three times**: `0x5100FE20` (2009-11-17 → 2010-02-03, 14 dumps) →
  `0x5000FE20` (2010-03-10 → 2010-04-09, 15 dumps) → `0x5000FE00` (2010-04-19 → 04-21, 3).
- **FormType enum drift affects exactly one dump** — `xex` (2009-11-17), shifted −1 across
  0x42–0x78. Every other dump matches the final enum. The enum stabilised early.
- **No unknown FormType bytes anywhere in the corpus.** Every observed byte maps to a known
  signature. Identification at the *type* level is complete.

Note the census reports raw bytes by design (that is what its drift report needs). The CSVs
now report drift-**corrected** codes; without that, an enum shift is indistinguishable from
"this content did not exist yet". Correcting removed six phantom rows (INFO, LAND, LVSP,
TOFT, ARMA, PCBE) that were pure drift artifacts.

## 4. Mapping gaps — types we can see but cannot emit

Present in the runtime of all 32 usable dumps, **no encoder** (so unrecoverable into an ESM):

| Type | Max/dump | | Type | Max/dump |
|---|---:|---|---|---:|
| MSTT | 323 | | ADDN | 37 |
| CAMS | 229 | | RGDL | 37 |
| IDLM | 185 | | EFSH | 32 |
| LSCR | 153 | | CLMT | 27 |
| ANIO | 148 | | PGRE | 21 *(has planner, no encoder)* |
| ASPC | 79 | | CSNO | 5 |
| MSET | 79 | | CHIP | 5 |
| TACT | 74 | | DOBJ | 1 |
| IPDS | 48 | | | |

**Encoder but no planner**: GRAS, IMGS, PWAT, RADS, TREE.

**Visible only as embedded ESM record bytes** (no live form ever seen — these types carry no
EditorID, so they never enter the EditorID-keyed hash table):

| Type | Dumps | Max records | Encoder |
|---|---:|---:|---|
| INFO | 33 | 6,990 | yes |
| GMST | 32 | 1,389 | yes |
| GRUP | 35 | 485 | n/a |
| LAND | 31 | 196 | yes — *see correction* |
| NAVM | 27 | 30 | yes — *see correction* |

> ### ⚠ Correction (same day): `has_encoder` used the wrong oracle
>
> The column is computed from `RecordEncoderRegistry.SupportedRecordTypes`, which only covers
> `IRecordEncoder` registrations. The converter has **at least four independent emission
> paths**, and two of them were invisible to it:
>
> - **Cell-child static encoders** — `LandEncoder` is a plain static class with a bespoke
>   signature, reached via `PlannedLandEncoder` from
>   [PlanCellSectionBuilder.cs:348](src/BethesdaMultitool/Core/Formats/Esm/PlannedWriter/Cells/PlanCellSectionBuilder.cs#L348)
>   and [LandOverrideBuilder.cs:95](src/BethesdaMultitool/Core/Formats/Esm/Plugin/Nav/LandOverrideBuilder.cs#L95).
>   **LAND is fully wired and emitted.**
> - **Byte-rewriter path** — NAVM is handled by the whole
>   [Plugin/Nav/](src/BethesdaMultitool/Core/Formats/Esm/Plugin/Nav/) subsystem
>   (`NavMeshByteRewriter`, `NavInfoMapBuilder`, `NavMeshAdjacencyRebuild`, winding/reciprocity
>   repair), which rewrites captured record bytes instead of encoding from a model.
>   **NAVM is emitted.**
>
> So the original "LAND and NAVM are the Phase C/D blockers" claim was **wrong** — both are
> false positives. Every other `has_encoder = NO` row in §4 is therefore also unverified
> against the planner / static / byte-rewriter paths and is being re-checked before any
> implementation work. A third case is already known to be subtler than the column suggests:
> `PgreEncoder` **and** `PlannedPgreEncoder` both exist, but
> [CellChildAllocator.cs:201](src/BethesdaMultitool/Core/Formats/Esm/Planner/Cells/CellChildAllocator.cs#L201)
> notes PGRE has "no production caller" — implemented but unreachable, which is a wiring
> problem, not a missing encoder.

**`runtime_land_forms` is 0 in all 50 dumps.** This is a deliberate bail, not silent
breakage: `EditorIdLookupTables` uses a 0xFF sentinel meaning "LAND FormType detection did
not reach confidence — populate nothing rather than wrong entries" (a previous default of
0x45 mis-classified DIAL as LAND). The consequence is that the runtime LAND path is dead
corpus-wide; only embedded ESM LAND records are available.

## 5. Script text (SCTX) — the largest single recovery gap

The SCTX detector finds **≤2 script sources per dump**, including in Debug builds. The raw
data says otherwise:

| Probe | Debug dumps | Release dumps |
|---|---:|---:|
| `ScriptName` | **158** | 2 |
| `begin GameMode` | **162** | 2 |
| `begin OnActivate` | **135** | — |

Cause: `EsmMiscDetector.TryAddSctxRecord` only matches text framed inside an `SCTX`
subrecord header. Runtime-resident script source lives as **bare strings in heap buffers**,
with no subrecord framing, so essentially none of it is detected.

Two consequences:

1. Script source text is a **Debug-build-only** asset — the 3 dumps of the 2010-01-27 build
   (`Fallout_Debug.xex{,1,2}.dmp`) are the only ones carrying it. Every Release dump has 2
   hits (static strings in the executable).
2. We currently recover roughly **1%** of what is there (~2 of ~158).

## 6. Recoverable volume varies enormously per capture — and the baselines are poor

Embedded ESM record bytes are the other half of a dump's content. Big-endian is the real
signal (BE = native Xbox record bytes); the near-constant 1–9 on the opposite side is a
noise floor.

| Dump | Build | BE records | INFO | REFR | NAVM | LAND |
|---|---|---:|---:|---:|---:|---:|
| xex21 *(baseline)* | 2010-02-16 | 6,901 | 4,671 | 665 | 5 | 10 |
| xex22 *(baseline)* | 2010-03-10 | 6,084 | 4,885 | — | — | — |
| xex29 | 2010-04-08 | 10,173 | 5,322 | 3,842 | 23 | 25 |
| xex44 | 2010-04-21 | **12,829** | **6,990** | **4,880** | **30** | 9 |
| xex42/43 | 2010-04-19 | ~1 | — | — | — | — |

This is **not** a chronological trend — xex42/43 (2010-04-19) have almost none while xex44
two days later has 12,829. It is a property of what happened to be resident at crash time.

Two observations for the converter, both for the user to decide on:

- **The current baselines are among the poorer captures.** xex21 carries 665 REFR record
  bytes; xex44 carries 4,880 (7.3×) and 50% more INFO. xex21/xex22 were presumably chosen
  for the game states they capture, which is a separate and legitimate axis — but if the
  goal is maximal record-byte recovery, they are not the richest source.
- **v137's writer-synthesis premise deserves a re-test.** The four-agent census that
  concluded "no `bUlyssesHired` writer exists in any captured data" ran against **xex21** —
  which at 39 Ulysses hits is one of the *poorest* Ulysses dumps in the corpus. Dumps from
  2010-03-25 onward carry 485–502. Since the synthesized writer was an explicit
  never-invent exception, re-running that census against xex23+ (or Jacobstown/xex31 at 502)
  before treating synthesis as necessary seems worth the cost.

## 7. Fragmentation

Across the 32 live dumps: **75.5% of captured bytes are unrecognized on average**
(range 18.1%–83.9%), spread over **~3,005 gaps per dump**.

For xex21 (representative), against 247 MB of captured memory regions:

- **Recognized: 23.5%** · **Unrecognized: 76.5%**, spread over **3,770 separate gaps**.
- Gap composition: PointerDense 74.3 MB, StringPool 50.0 MB, BinaryData 48.7 MB,
  AsciiText 10.9 MB, ZeroFill 5.1 MB.
- Inside those gaps the recovery scanner still finds **77,407 candidates** — 70,876 TESForm,
  25,790 dialogue, 10,685 placed-reference (corpus max: 85,049 TESForm candidates).

So the data is heavily fragmented and the large majority of captured bytes are not attributed
to any known structure, while the gap scanner shows a substantial amount of that is
structured data we simply have not claimed.

**The one outlier is instructive**: xex23 (2010-03-25) is 81.9% *recognized* with only 361
gaps — an order of magnitude cleaner than every other dump. It is also the smallest live
dump (114 MB). Worth understanding what makes it different before assuming 75% gap is
inherent to the format.

### 7a. The gap scanner finds nothing at all in Debug builds

| Dump | Build | Gap % | TESForm candidates | Dialogue candidates |
|---|---|---:|---:|---:|
| Fallout_Debug.xex | 2010-01-27 | 68.4% | **0** | **0** |
| Fallout_Debug.xex1 | 2010-01-27 | 61.9% | **0** | **0** |
| Fallout_Debug.xex2 | 2010-01-27 | 67.9% | **0** | **0** |
| *(every Release dump)* | — | ~76% | 6,727 – 85,049 | up to 33,393 |

All three Debug dumps hold 53,299 runtime EditorIDs, so the data is plainly there. The zero
is solid; **the cause is not yet established**, and there are two very different candidates:

1. **Heuristics fail** — Debug struct layouts differ (debug padding shifts fields) and the
   candidate tests are layout-sensitive. This would be a real recovery gap.
2. **Nothing left to scan** — the gap scanner only examines regions the coverage analyzer
   marked as *gaps*. Debug dumps are *more* recognized than Release (62–68% gap vs ~76%), so
   the form-bearing regions may already be claimed as recognized and therefore excluded.
   This would be benign.

These must be distinguished before acting. Either way it compounds §5: **the Debug builds are
simultaneously our only source of script source text and the builds where gap-based recovery
currently returns nothing.**

## Encoder gaps — resolved 2026-08-03

A 30-agent verification pass (8 verifiers × 3 types, then one adversarial refuter per claimed
gap) checked every candidate against **five** emission surfaces. **Zero of 21 gap claims were
overturned.** Findings and fixes:

**The structural cause.** Every top-level GRUP reaches disk only through a key in
`grupBytesByType`, and the only encoder-gated writer is the `EnumerateModelsByType` loop in
[PluginBuilder.cs](src/BethesdaMultitool/Core/Formats/Esm/Plugin/Pipeline/PluginBuilder.cs).
A type absent from that yield set is **structurally unemittable regardless of what encoders
exist** — and because the drop happens *before* the encoder gate, `stats.IncrementSkipped` and
the `"No encoder for {type}"` warning never fire. **These records vanished with zero
diagnostic.** That is why GRAS/IMGS/PWAT/TREE had registered encoders and emitted nothing.

**Fixed — 8 types now emit** (each: encoder + registry row + dispatcher row + yield):

| Type | Forms/dump | Work | Emitted from xex21 |
|---|---:|---|---:|
| MSTT | 323 | new encoder | **92** |
| ANIO | 148 | new encoder | **8** |
| ASPC | 79 | new encoder | **52** |
| TACT | 74 | new encoder | **3** |
| ADDN | 37 | new encoder | 0 *(none proto-new in xex21)* |
| CLMT | 27 | new encoder (typed `ClimateRecord`) | 0 *(no CLMT bytes in xex21)* |
| GRAS | 20 | wiring + scanner signature | 0 *(no GRAS bytes in xex21)* |
| IMGS | 66 | wiring + dispatcher row | 0 *(no IMGS bytes in xex21)* |

155 records that were previously impossible to emit now reach the output; full suite green
(6,531 tests) and `--validate` semantic check clean (74,656 records, no duplicate FormIDs, all
refs resolve).

GRAS additionally needed `"GRAS"` adding to `RecordScannerDispatch.RuntimeRecordTypes` —
`RecordCollection.Grasses` was empty for *every* dump, so its encoder could never have fired.

**Still open** (11 runtime-present types, all blocked on runtime *decode* work rather than
encoders — `RuntimeGenericReader.ReadEmbeddedStruct` returns a descriptor string, not bytes,
for structs >8 B): CAMS 229, IDLM 185, LSCR 153, MSET 79, IPDS 48, RGDL 37, EFSH 32, PGRE 21,
CHIP 5, CSNO 5, DOBJ 1. Plus PWAT (72) and TREE (10), which have encoders but whose
`PlaceableWaterRecord`/`TreeRecord` models are never constructed — they need a
`GenericEsmRecord` adapter.

**Tooling fixed at the root.** `PluginBuilder.EmittableTopLevelRecordTypes` now exposes the
yield set, and `mapping_coverage.csv` reports a real `emission_status` (plus separate
`has_encoder` / `reachable` / `has_dispatcher` columns) instead of the misleading
registry-only boolean that produced the LAND/NAVM false positives.

## Summary of gaps found

| # | Gap | Evidence | Severity |
|---|---|---|---|
| 1 | Script source text unrecovered | 158 present vs 2 detected, Debug builds | High — Debug-only asset, ~99% lost |
| 2 | ~~LAND/NAVM have no encoder~~ | **RETRACTED** — both fully wired (see §4 correction) | — |
| 3 | Types identified but not encodable | MSTT 323, CAMS 229, IDLM 185, … — count pending re-verification against all four emission paths | Medium |
| 4 | Runtime LAND path dead corpus-wide | `runtime_land_forms` = 0 in 50/50 | Medium |
| 5 | Baselines are record-byte-poor | xex21 6,901 BE vs xex44 12,829 | Medium |
| 6 | v137 census ran on a Ulysses-poor dump | xex21 = 39 hits vs 502 available | Medium |
| 7 | Gap scanner yields 0 on Debug builds | 0 candidates vs 6.7k–85k on Release | High — compounds #1 |
| 8 | 5 types have encoder but no planner | GRAS, IMGS, PWAT, RADS, TREE | Low |
| 9 | 75.5% of bytes unattributed (avg) | ~3,005 gaps/dump, 77k candidates inside | Context |

**Not gaps** (verified clean): FormType identification is complete (no unknown bytes);
enum drift affects only the 2009-11-17 dump; the 18 dataless dumps are genuine pre-load
captures, not parse failures.
