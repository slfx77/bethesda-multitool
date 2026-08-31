# Handoff — adversarial round 4 (2026-08-27)

> **STATUS 2026-08-29 — round 5 closed T2, T3, T4, T5, T6 and T7.** Read
> "Round 5" in `docs/adversarial_audit_followup_2026_08_25.md` before acting on §4 below; the
> targets there are kept for their reasoning, not as open work. Three were real defects:
> **T6** (the typed-pointer narrowing rejected covariant reads — 16 per dump on xex21/xex2),
> **T4** (a borrowed array cap silently dropped whole MODT lists), and **T7's** unresolved-swap
> rendering. **T2** measured at well under 1 % of a conversion — do not optimise it.
> **T3's pixel A/B was run and is closed**: three interiors and the WastelandNV worldspace render
> identically with and without the runtime merge. ⚠⚠ Doing it exposed a trap — the *first* batch
> capture after a cold decoded-asset disk cache differs from every later one, which reads exactly
> like a real visual change. Run a same-state control before believing a batch-capture diff.
> **Nothing in §4 remains open.**

For the agent picking up the next adversarial pass on the DMP recovery work.

Read this first, then `docs/adversarial_audit_followup_2026_08_25.md` (1,020 lines; rounds 3, 3b,
3c, 3d, 3e, and 4 are the recent ones) and the memory entry
`adversarial_dmp_recovery_audit_round3.md`.

---

## 1. Where things stand

**Repo state.** `HEAD` = `6dd0b2cf`. **159 files modified/untracked and NOT committed.** Rounds 1–2
of the audit are already *in* `HEAD` (commit `04dd5568`); rounds 3 / 3b / 3c / 3d / 3e are all
uncommitted working-tree changes, as is round 4. Other sessions have also left uncommitted work in
this tree — notably `WorldView3DControl.*` and `GeometryArenaAllocator.cs`. **Do not assume every
modified file is yours.** `GeometryArenaAllocator.cs:277` carries the only build warning (S3241)
and is not part of this work.

**Gates as of handoff** (all re-run after the final change):

| Gate | Result |
|---|---|
| Default suite | **9,013 / 0 failed** (234 skipped) |
| `RUN_BUCKET_B=1` sweep | **9,013 / 0 failed** (52 skipped), 7m12.6s |
| Full both-TFM Release build, analyzers on | **0 errors**, 2 warnings (1 pre-existing × 2 TFMs, not ours) |
| `dmp to-esm` on `xex44` | 97,014 records, 13,743,492 bytes, semantic check clean |

Reproduce:

```bash
dotnet build -c Release -p:BuildTestsOnly=true -p:SkipAnalyzers=true      # fast iteration
./tests/BethesdaMultitool.Tests/bin/Release/net10.0/BethesdaMultitool.Tests.exe
RUN_BUCKET_B=1 ./tests/BethesdaMultitool.Tests/bin/Release/net10.0/BethesdaMultitool.Tests.exe
dotnet restore && dotnet build -c Release                                  # both TFMs, before "green"
dotnet run --project src/BethesdaMultitool -f net10.0 -c Release --no-build -- \
    dmp to-esm Sample/MemoryDump/Fallout_Release_Beta.xex44.dmp \
    -o <out>/out.esm --pc-esm Sample/ESM/pc_final/FalloutNV.esm
pwsh -NoProfile -File artifacts/dmp-audit/round4-t1/Invoke-T1TopologyAudit.ps1
```

The `dmp to-esm` command does not create `<out>`; create that parent directory first. The retained
round-4 output is `TestOutput/round4_t1_20260827_xex44/out.esp`.

⚠ That filename is stale and misleading: `dmp to-esm` writes an **ESM-flagged** plugin (TES4 header
flags `0x00000001`; `to-esp` survives only as a back-compat command alias,
`DmpToEsmCommand.cs:134`). Name new output `.esm` — the ESM flag changes how the engine's startup
walker treats cell children, so an `.esp` name invites exactly the wrong mental model.

⚠ **Build contention is a live hazard in this repo.** A `BuildTestsOnly=true` build leaves
`project.assets.json` without the Windows TFM, so the next full build fails with `NETSDK1047`; a
stale `obj/` produces a XAML `WMC9999` internal error and a cascade of `CS0103 _performanceSampler`
in `WorldView3DControl.*`. Both clear on `dotnet restore` + one retry. **Retry once before believing
any of it — a previous 13-hour agent run was lost chasing exactly this as a code defect.**

---

## 2. What was actually done (so you don't re-do or re-litigate it)

The original 7-phase plan is **complete**. Recent work, newest first:

- **4** — T1 closed across the whole specialized-reader surface: 116 fixed/bounded reads across 27
  files now honor VA reassembly; the original 24/12 inventory was correct but incomplete. The
  all-50-dump topology audit found zero instances of the exact VA-contiguous/file-disjoint shape,
  while confirming 8,675 file-contiguous boundaries with a VA gap. See §4 and the audit document.
- **3e** — aux-struct walk changed from depth-3 to the full transitive closure (terminates on its own
  at depth 9 / 449 structs / 1.39 MB); the **specialized-reader gap closed** by one sweep instead of
  ~20 reader edits; the LSCR "layout shift" **retracted** and its real cause fixed (see §3).
- **3d** — `pdb_layouts.json` gained `auxStructs`, which unblocked all four remaining Phase-2 items
  at once (MODT / MODS / LNAM / DEST). Phase 7 closed, including the LSCR 153→152 lead (it is the
  Big Guns / Small Guns → Guns / Explosives **skill merge**: 2 renamed in place, 1 added, and
  `0x0002186E BigGunsLoadScreen02` + `0x001133AC BigGunsLoadScreen03` genuinely cut).
- **3c** — measured the DMP→ESM delta honestly (+1 type, +1–2 records; the overlay correctly keeps
  master's copy for master FormIDs).
- **3b** — the `DataLength >= N` guard family, and Oblivion CTDA being 24 bytes not 20.

Verified-against-retail claims (do not re-derive, but **do** feel free to attack the method):

- MSTT `Car02NoKD` destruction block == retail `Car02` byte-for-byte (`health=325 stages=6
  flags=0xCE`).
- WEAP `0x00109A0C` destruction == retail (`health=1 stages=1 flags=0x43`).
- STAT `0x000A473A 1stPersonCowboyRepeater` MODS: same 7 entries, same TXST `0x000A4733` on all,
  against retail.

---

## 3. The most recent fix, and why it matters for your pass

`RuntimeGenericReader.ReadPointerField` was calling the **untyped** `FollowPointerToFormId`. That
overload accepts any pointer into captured memory whose byte at `+4` is `<= 200` and whose word at
`+12` is non-zero — **every ASCII character passes the first and most text passes the second**. A
stale pointer landing in a string therefore returned that string's bytes as a "FormID". The symptom
was LSCR `pLoadScreenType = 0x20736B69`, which is the ASCII `" ski"`.

Fixed by demanding the FormType the layout already named. **248 of 535 pointer fields** in the
layout database name a record class (WEAP 31, NPC_ 10, TACT/CREA/WRLD 8 each).

⚠ **The general lesson to carry into your pass: this codebase has several validators that look
strict and are not.** `formType <= 200` and `formId != 0` is the pattern to hunt for. Ask of every
validator: *what fraction of random bytes passes this?*

---

## 4. Adversarial targets — ranked, with the specific claim to attack

### T1 (complete) — specialized-reader VA reassembly

The original **24 flat fixed-struct reads across 12 files** were all migrated, then the audit was
widened to the actual semantic surface: **99 fixed reads plus 17 bounded arrays/dereferences = 116
sites across 27 files**. `RuntimeMemoryContext.ReadTesFormBytes` now centralizes retained-pointer
authority, offset-to-VA mapping, VA reassembly, and the synthetic no-region fallback. Known-VA
windows and pointer arrays use `ReadBytesAtVa`, embedded headers use their stitched parent buffers,
and the `uint` overload sign-extends Xbox module addresses.

No non-Generic/non-Scanning reader retains a flat `ReadBytes`; only the explicitly region-segmented
`RuntimeRefrHeapSweep` retains raw accessor reads. The migration also fixed BSStringT member-header
reassembly, negative/overflow input handling, and an AVIF buffer-size regression caught during
review.

Measured artifact: `artifacts/dmp-audit/round4-t1/`. It covers **50 dumps / 9,137,881,518 bytes /
161,537 regions**. All **152,812 VA-adjacent region pairs are also file-adjacent**, so the exact
handoff mismatch shape occurs zero times in the retained corpus. There are nevertheless **8,675
file-adjacent/VA-gap pairs**, which make the old flat-read contract unsafe. Three retained EditorID
exports supplied 63,135 representative candidate ranges; their 69 cross-region reads all matched.
The artifact explicitly records that pointer-only LoadedLandData and DIAL-walk INFO targets are not
enumerable from those CSVs.

### T2 (highest remaining) — the sweep double-reads ~24k structs per dump, and the cost was never measured

`RuntimeNestedPayloadHandler` examines **~27,499 records on `xex44`**, of which **24,327 already had
their struct read** moments earlier by a specialized reader. The plan's Phase 5 discipline
explicitly said to measure parse time when adding a per-record read; **that was not done here.**

- Measure DMP parse time before/after (`[Semantic Parse] Complete. Time:` line), on `xex44` and a
  Debug dump.
- If it is material, the fix is to have the specialized readers hand their already-read buffer to
  the payload reader rather than re-resolving, or to cache `ResolveStruct` per entry for one pass.
- Conversion wall-clock is **not** a valid proxy — the two runs I have (40.70s vs 29.03s) differ by
  machine load, not by this change.

### T3 — a rendering behaviour change that has never been looked at

Runtime-derived MODS now flows into `RecordCollection.AlternateTexturesByFormId`, and
`WorldView3DControl.xaml.cs:633` / `WorldMapControl.xaml.cs:556` assign that straight to
`RenderCache.AlternateTextureIndex`. So **a DMP load now applies 422 texture swaps in the 3D viewer
that it previously did not**. This has only been verified as *data* (against retail, on one STAT).
Nobody has looked at a frame.

**Claim to test:** render a DMP-loaded cell before/after and confirm nothing is re-skinned wrongly.
⚠ Per project rule, **never `Read` a render ≥4096px — capture at 512px**. If a swap is wrong the
symptom is a mesh with another object's texture, which is visually obvious.

### T4 — `MaxCountedArrayItems = 50` may be too low for MODT

`ReadTextureHashes` is all-or-nothing and bails when `cTextureCount > 50`
(`RuntimeMemoryContext.MaxListItems`, borrowed from the BSSimpleList node budget). A model with more
than 50 textures silently yields nothing rather than a truncated list. **Nobody checked the real
distribution of `cTextureCount`.** Histogram it; if retail models exceed 50, the cap is wrong for
this use and should be separated from the list budget.

### T5 — two probe changes with no empirical before/after

Both are argued-from-first-principles in 3d/3e and neither was measured:

1. `GetFieldProbe` now returns **no check** for pointers whose target is a known non-record struct
   (previously `PointerToForm`, which could never pass). Argued as removing dilution. **Nobody
   compared the set of detected type shifts before and after.** Dump `ProbeAllTypeShifts` output on
   several dumps both ways.
2. `TryCorrectShift` now falls back to *any* non-`TESForm` `BSStringT` when `cModel`/`cFullName` are
   absent. A worse validator could in principle pick a worse per-record shift. Same treatment.

### T6 — the typed-pointer narrowing was regression-checked on exactly one dump and one game

`ReadPointerField`'s new strictness was validated by a byte-identical `xex44` conversion. **Not
checked:** other dumps, Oblivion / FO3 / Skyrim / FO4 paths, or the GUI world-view build. The risk
shape is a field declared as class `X` that legitimately holds a *derived* class at runtime — the
FormType byte would be the subclass's and the read would now be rejected. I could not find such a
case (the base classes involved — `TESObject`, `TESForm`, `TESActorBase`, `MobileObject` — are not
record classes and so stay untyped), but **that is an absence-of-evidence argument and deserves a
second look.**

### T7 — smaller, still real

- `TryAlternateTextures` requires *every* entry to resolve before emitting MODS, but the browse path
  keeps unresolved entries with `TextureSetFormId = 0`. Confirm `show` renders a `0x00000000` swap
  intelligibly rather than as a bogus link.
- `EncodeDestructionBlock` writes `Math.Min(stages.Count, 255)` into DEST's `u8` count. Unreachable
  today (`MaxDestructionStages = 32`) but the two limits are not tied together.
- The embedded `pdb_layouts.json` is now **1.39 MB**. Assembly size and cold-load cost unmeasured.
- LSCR `LNAM` emission is wired but **no dump in the corpus has a non-empty `LoadFormList`**, so the
  writer has only ever been exercised synthetically.
- `RuntimeNestedPayloadHandler` logs at Info only when it recovers something. Confirm that is silent
  on pure-ESM loads (it should be: it returns early when `RuntimeReader == null`).

---

## 5. Planned work still open

Nothing from the original 7-phase plan remains. These are the follow-ons it generated:

1. **Emission reach.** Reading is now complete for MODT/MODS/DEST across all owning types; *writing*
   is not, and the asymmetry is deliberate. Only 6 encoders take a `GenericEsmRecord` (ADDN, ANIO,
   CHIP, FLOR, MSTT, TACT). The 7 typed-model encoders that carry a model — BOOK, COBJ, HDPT, IMOD,
   INGR, CCRD, CMNY — and every specialized type get nothing. If this is wanted, the side-indexes
   are already populated; the work is in the encoders.
   ⚠ Check each record's xEdit schema first: `wbDEST` is used by exactly 21 records and **FLOR is
   not one of them**, despite `TESFlora` carrying the member at runtime.
2. **MODT is deliberately never written.** The hashes cover the source build's texture paths and do
   not transfer between the Xbox and PC builds; the file-conversion path already byte-preserves MODT
   for the same reason. This is a decision, not a gap — do not "fix" it without a ruling.
3. **M2 (LZX → BSA)** stays held: no XMem-flagged archive exists in the corpus to test against.
4. **AMEF** stays exempt from `GenericSweepEmissionExemptions` — 0 records corpus-wide.
5. **DSTD stages are walked but `DMDT` is not.** `DestructibleObjectStage.pReplacementModel` is a
   `TESModelTextureSwap`, so its own `TextureList` is reachable with the machinery that now exists;
   only `cModel` (DMDL) is read today.
   ⚠ Corrected 2026-08-29: **there is no `DMDS` subrecord in FO3/FNV** — it appears nowhere in the
   schema registry, the generated FO3 schema, or the merge policy. And `DMDT` is MODT-family, which
   is deliberately never written (item 2). So this item is browse-only value, not emission reach.

---

## 6. Traps this work has already paid for

- ⚠⚠ `IsVaRangeCaptured` is a **residency** predicate, not a contiguity one. Guard-then-flat-read
  still splices foreign bytes.
- ⚠⚠ `pdb_layouts.json`'s real source is `Sample/PDB/Aug_22_MemDebug/types_full.txt`, **not**
  `Proto/` — regenerating from Proto moves WEAP 920→924 and breaks pinned tests. The exporter now
  records the actual source file, so check the `source` / `sourcePath` fields.
- ⚠⚠ Always diff-gate a layout regeneration on **"the 116 `types` entries do not move"**. Additive
  only. Both 3d and 3e passed this; if yours does not, stop.
- ⚠⚠ Never add a probe check that cannot pass. `ScoreSample` puts every declared field in the
  denominator and only passing ones in the numerator, so a never-passing check dilutes the
  `Margin >= 2` gate and makes real layout shifts *harder* to find.
- ⚠⚠ New BSA flag reads need a `Version >= 104` gate. An unconditional 0x200 read broke the entire
  Oblivion mesh corpus in round 2, and **the default suite cannot catch it — only Bucket B can.**
- ⚠ New tests must be **synthetic**. Real-asset tests are opt-in behind `RUN_BUCKET_B=1` with the
  matching `[Trait]`, and every real-asset class needs
  `[Collection(SequentialIntegrationGroup.Name)]` + `RealAssetEsmCache.LoadAsync`.
- ⚠ A test that `return;`s when a fixture is missing is recorded as a **pass**. Use `Assert.Skip*`.

## 7. Relevant tests

| File | Covers |
|---|---|
| `RuntimeContainerFieldReaderTests` | container/nested walks; **pins the five aux struct layouts** so a bad regeneration fails with one clear message |
| `RuntimeNestedPayloadSweepTests` | the specialized/generic split; typed-pointer decline; untyped pointer keeps its raw value |
| `NestedPayloadSubrecordTests` | MODS writer checked against `AlternateTextureParser` as oracle; DEST/DSTD/DSTF shape; LNAM width |
| `SubrecordLengthToleranceTests` | the round-3b `DataLength >= N` family + both Oblivion CTDA widths |
| `PlannerRoutingConsistencyTests` | routing proof — a type is wired iff it is absent from `GenericSweepEmissionExemptions` |
| `RuntimeSpecializedReaderVaRunTests` | split-run AMMO/INFO/LAND/probe reads; all three AVIF BSStringT headers and payloads |
| `RuntimeMemoryContextTests` | VA stitching, gap failure, pointer authority, signed module VAs, invalid ranges, BSStringT contracts |
