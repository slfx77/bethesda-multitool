# Decision Brief — xex21.v143 playtest findings

Three findings, all root-caused and adversarially re-verified. Headline: **nothing regressed in v142/v143**. Finding 1 predates every build on disk, finding 2 landed with v141's reparenting gate, finding 3 predates v140. One defect train (finding 2) needs your ruling; findings 1 and 3 each carry a policy fork.

## How the numbers fit together (findings 2 and 3 share a census)

- v140 → v141/v143 placed-ref census: **55,849 → 55,686 (−163, +0)**. All 163 are master-space FormIDs (157 ACHR + 4 ACRE + 2 REFR map markers) blocked by the v141 `MasterHomeAllowsMove` gate — this is **finding 2 in its entirety**. Split by intended destination: 31 TheStripWorld + 15 CampMcCarranWorld + 3 WastelandNVStrip (49 targeting plugin-new worldspaces) + 91 WastelandNV + 23 FreesideWorld (114 targeting master containers).
- The Strip's 31 are **all NPC/creature placements** in persistent cell 0x01001BAE — zero scenery. The sidewalk gaps (finding 3) come from populations that were **never emitted in any build**: 27 SCOL refs, 463 Utl* refs, 1 fountain ref, plus 123 emitted refs that only render with the July-21 Meshes BSA.
- Dialogue is byte-identical across v138–v143 (all 4,320 DIAL+INFO, single sha256 eed381e7…) — finding 1 is not part of any delta.

---

## Finding 1 — Ulysses' first menu has no Goodbye

**Classification: policy trade-off. No defect, no regression — and the current output is provably capture-faithful.**

**What it is.** The first menu is the TCLT choice list of converter-synthesized silent GREETING INFO **0x01007023** (one of exactly 16 synth entries, all under master GREETING 0xC8), listing his 3 topic roots [0x01003AB2, 0x01003AB7, 0x01003AB1] with no exit. Master GOODBYE 0xD4 is structurally inadmissible to the root selector (master record, TopicType 1), and `ExitTopicResolver` cannot resolve an exit for plugin-new quests like 0x01004FBB (`GreetingEntrySynthesizer.SelectScopedEntryTopics` gate at lines 128-130; `DialogGrupBuilder.cs:914` / `:1246`).

**The evidence.**
- The dump contains the authored answer: content-less Ulysses GREETING stub **0x00133FCD** (dropped at the 233-stub empty-stub gate) carries a captured runtime TCLT of exactly his three topics **with no exit entry**, plus AddTopics [0x00133FC9 "Tell me about root nodes."]. Cass's stub 0x00133FCE has the same locked shape (6 topics, no exit). **The Feb-2010 proto itself showed this exit-less menu.**
- His captured goodbye **is** emitted: INFO 0x01004BA2 "Custom goodbye.", Goodbye-flagged, GetIsID(0x01004EA8), under GOODBYE 0xD4 whose QSTI was extended with his quest. It is reachable from any non-TCLT menu.
- Retail precedent both ways: 290/1,072 retail GREETING choice lists are locked (incl. Boone first-meet 0x00096B90); 782/1,072 carry an in-list exit.
- "It was there in v140" is refuted at record level for every on-disk build (v138–v143 identical).

**Your call** (I'm not picking — B/D touch "never invent data"):

| Option | Effect | Cost |
|---|---|---|
| **A. Keep locked menu** | Matches captured authoring exactly; matches retail locked-first-meet pattern | The reported symptom stays, for all 16 synth-greeting NPCs |
| **B. Append 0xD4 to synth TCLTs** | "Goodbye." on first menu; Ulysses' captured line is wired and passes | Provably deviates from captured authoring; which of the 274 master + 5 plugin GOODBYE INFOs plays is engine-priority-dependent (unproven his wins). Measured: no interaction with the v141 fixes |
| **C. Exit only on choice RINGS** | Honors the inescapable-ring invariant; the recruit ring (0x01004C4E) is purely converter-authored (captured struct 0x0013408B has a NULL ConversationData ptr), so capture-fidelity constrains it less | First menu unchanged — symptom persists |
| **D. Build synth TCLT from the captured stub's links** | Provenance becomes capture-derived (order preserved); recovers the lost AddTopics edge to 0x01003AB6, currently emitted with zero INFOs | Visible menu unchanged (capture has no exit) — symptom persists. Combinable with A or B |

*No call needed:* `dialogue tree` hides content-less INFOs — that's what hid stub 0x00133FCD's captured links during the first pass. I'd surface them; pure tooling.

---

## Finding 2 — Emily Ortal, MP Fretwell, and the 163 silently dropped refs

**Classification: mixed — two plain defects plus a policy fork touching both v141 rulings.**

**What it is.** v141's `MasterHomeAllowsMove` (PersistentCellReparenting.cs:156-177) requires the master to file an override in a non-interior cell **of the target worldspace** before a container re-home is allowed. Because the proto predates retail's worldspace re-partition (TheStripWorldNew→TheStripWorld, WastelandNVmini→WastelandNV, FreesideNorthWorld→FreesideWorld, …), **all 163 fail**: 44 interior-homed, 119 exterior in a different worldspace, **zero** the gate would ever pass. Blocked refs then die at `CellChildVerdictPlanner.DecideOverride` (`refr.parent-cell-mismatch`) or silently with orphan-bucket removal. The v141 fix note counted only the 3 parent-cell-changed refs.

**The evidence.**
- **Emily Ortal** ACHR 0x0011904F: v140 enabled in Strip container 0x01001BAE; **absent** v141/v143. Actor ledger: DMP=1 OUT=0, captured live in runtime cell 0x0010B9AE. **Not a capture gap.**
- **MP Fretwell** ACHR 0x0013BAF4: survives via the DuplicateActorPlacementMerge fold but is dead twice — master 0x800 preserved (0xC00) and emitted into grid cell 0x01001BA2's Persistent-Children GRUP, which the converter's own doc (PersistentCellReparenting.cs:11-19) says the loader never reads. Same fate for 0x0013BAC1 and Victor ACRE 0x0013BB18 (enabled, but in an unread GRUP — the move never applies).
- Proto Strip today: 24 refs, only 21 loadable generic actors (securitrons/troopers/promoters). v140 had 55, 53 enabled — Emily, the Phebuses (0x0011E6B9/0x0011EB8E), Billy Knight (0x0011F9BF), etc.
- Powder-Ganger separation is measured, not assumed: overlap between the 163 drops and both flip populations (40 gained 0x800 / 15 lost) = **0**.
- xex44.v143 (same-era worldspaces) emits Emily/Fretwell in-place in master container 0x0013B310 — the gate works as intended when capture topology matches retail.

**No call needed (plain defects):**
1. **Silent drops.** 163 refs vanished with 3 attributed. Gate refusals should emit events (hook before orphan-bucket removal at PersistentCellReparenting.cs:285-288). I'll add this regardless of the policy outcome.
2. **Dead-letter GRUPs.** Emitting Reparented survivors into GRUPs the loader never reads produces inert bytes by the converter's own documentation. Where they should go depends on your option below, but "emit into a known-unread GRUP" shouldn't survive as-is.

**Your call** (C touches "runtime state never overwrites authored file state"; D touches retail-worldspace protection):

| Option | Effect | Cost / risk |
|---|---|---|
| **A. Status quo** | Consistent with both v141 rulings | 163 captured placements unrepresented (144 were visible in v140); Strip stays generic |
| **B. Allow re-homes into plugin-NEW worldspaces only** (gate also passes when target has no master container) | Restores 49 (31 Strip + 15 McCarran + 3 Strip-mini); re-routes the 3 dead-GRUP survivors into the container. 25 master-enabled become visible; 24 master-disabled (incl. Emily; Fretwell via preserved 0x800) emit but stay dark | 1 interior-homed ref (0x00116834) re-enters a plugin container — same *shape* as the xex44 cow-crash; the plugin-container variant shipped in v140 without a crash report but isn't proven safe |
| **C. B + captured enable-state for these re-homes** (new plan flag, distinct from `Reparented`) | 47/49 emit enabled — Emily, Fretwell, Phebuses, Billy Knight visible again; Vulpes 0x00131F78 and messenger 0x0011909B stay dark (captured disabled, dark in v140 too) | Carves a scoped exception into the 0x800 ruling: 24 refs diverge from master-authored enable state. For: master authored **no cells** in these worldspaces, so no authored placement is overwritten. Against: a mid-session snapshot can read quest-gated actors as enabled. Powder-Ganger flips untouched (0/37, 0/15) |
| **D. Also relax for master-container targets** (drop same-worldspace test, keep !IsInterior) | Restores 71 of the 114 (48→WastelandNV, 23→FreesideWorld); 58 appear enabled | Puts proto placements at proto positions **inside retail worldspaces** — the territory the v141 rulings protect; cross-worldspace claims on master containers untested for the crash mechanism; 43 interior-homed stay blocked |

---

## Finding 3 — Strip sidewalk gaps

**Classification: mixed — a converter-side SCOL pipeline gap (the core), one capture-edge policy item, one unexplained single-ref loss, one mesh-layer note. None is a v141+ regression; the 491-ref dump-vs-v143 delta decomposes exactly as 24 + 3 + 1 + 463.**

**What it is.** The DMP pipeline has **no SCOL ingestion path**: `StaticCollections` is populated only by the serialized-record parser, the dump has zero serialized SCOL headers (247MB scanned, BE and LE), and unlike STAT there is no `MergeRuntimeOverlayRecords([0x21], …)` call — yet 133 BGSStaticCollection runtime structs are captured and readable. The 4 proto-only bases (SWDirtMidTrimmed 0x0011F93F, SWDirtEnd01/02Trimmed 0x0011F93E/3D, SCOLParkingLotLinesM 0x00111DA4) are never emitted, the EditorID-stem rescue can't strip "Trimmed"/"LinesM", and all **27 refs drop as `refr.dangling-base`** — 24 on the main drag, 3 parking-lot paint at (1,-1)/(2,-1). The bases and refs are both captured; **the incompleteness policy does not cover this.**

**The evidence.**
- v143 Strip STAT/MSTT/SCOL non-persistent: 2,149 vs 2,640 captured; every nonzero per-cell delta accounted for.
- The rescue mechanism works when the suffix rule permits: 37 SCOLParkingLotChunk01→01b-class remaps succeeded; "swdirtmidtrimmed" matches no master stem (retail has SWDirtMid 0x0010EA83 etc., no *Trimmed).
- The 463 Utl* placements sit in unresolved bucket 0xFE100001 (parent CELL never captured; runtime-cell-map bounds don't contain grids (1,-3)/(1,-2)); skipped as `no-master-worldspace`.
- NVULfountain: base MSTT 0x01006FA4 emits in every build with zero refs; captured REFR 0x001199A6 is lost pre-planner (no event, semantic loader never materializes it) — absent from v140 too.
- All 27 dropped refs are new non-persistent REFRs — measured disjoint from both v141 fix populations.

**Your call:**

| Option | Effect | Cost / risk |
|---|---|---|
| **A. Runtime SCOL ingestion** (mirror STAT's overlay for FormType 0x21; relax `ScolEncoder`'s validParts==0 drop for part-less DMP SCOLs) | Restores all 27 refs with the captured bases | Engine behavior for a SCOL with EDID+OBND+MODL but no ONAM/DATA parts is untested; renders only with the July-21 BSA (all 4 NIFs are July-21-only). Side effect: typed index then supersedes B for these refs |
| **B. Widen stem normalization** (add "Trimmed" to `EditorIdStem.Normalize`) | Restores the 24 SWDirt refs as cross-type remaps onto retail STATs 0x0010EA83/84/85 — retail meshes, no BSA dependency | Multi-tile collections replaced by single tiles (geometry approximate); the 3 LinesM refs stay dropped (no master "LinesM"); widens a deliberately conservative suffix list — though this drop *is* the census evidence that design said to wait for |
| **C. Document as known gap** | No change | The gaps you flagged remain; the data says converter-side, not capture-side |
| **D. Utl* block: captured-cell-bounds inference** (unique containment, as cell-inventory already computes) | Recovers 463 captured placements south-east of the gate | Worldspace membership is a position guess; Utl* is an interior tileset — if the proto filed these in an uncaptured interior, this fabricates an exterior utility complex. Current skip is the conservative no-fabrication reading. Not sidewalks |
| **E. Mesh-layer check (no converter change)** | — | 123 emitted sidewalk refs (7 STAT bases, e.g. SWBrickCurve 0x0100590E ×35) point at NIFs that exist **only** in the July-21-2010 Meshes BSA. If your test install lacks it, these render as gaps on top of the 27 |

*Follow-up diagnostics, no decision:* NVULfountain REFR 0x001199A6 needs a targeted semantic-loader trace to name its drop point (it's one ref, pre-v141, mechanism unconfirmed).

---

## What I'd need from you

1. **Finding 1:** A / B / C for the menu shape, and yes/no on D (capture-derived TCLT provenance; combinable with A or B).
2. **Finding 2:** A / B / C / D for the gate. If C: confirm you accept the scoped exception to the enable-state ruling for plugin-new-worldspace re-homes (24 refs). Separately: suppress or re-route the 3 dead-GRUP emissions regardless of the option?
3. **Finding 3:** A / B / C for the SCOL refs; yes/no on D (Utl* inference); and confirm whether your test install has the July-21 Meshes BSA (decides how much of the visual gap is mesh-layer vs converter).
4. No decision needed, will proceed unless you object: gate-refusal drop events (finding 2), content-less INFOs surfaced in `dialogue tree` (finding 1), NVULfountain loader trace (finding 3).