# New Vegas audit tools

These commands compare complete plugins and inspect cut-content evidence. Outputs distinguish stored data, reconstructions and runtime observations.

## Record inspection and comparisons

```powershell
BethesdaMultitool --plain show FalloutNV.esm BooneSCRIPT --full
BethesdaMultitool --plain show FalloutNV.esm 0x0015E9CC --full
BethesdaMultitool --plain show FalloutNV.esm 0x000FE923 --full
BethesdaMultitool esm packages FalloutNV.esm -f json
BethesdaMultitool esm semdiff old.esm new.esm -f 0x000E739E --format json
```

`show --full` (alias `--no-truncate`) prints complete source and multiline values in verbatim blocks. INFO and PACK inspection includes ordered condition logic. Terminal menus retain their item indexes, conditions, linked terminals, result text and complete action scripts. Game text containing square brackets is treated as text rather than console markup.

MESG inspection retains ordered conditions under the button that owns them, including empty button labels. Conditions found before any button remain explicitly unassigned. Message reports use the same condition descriptions, and MESG encoding preserves the retained conditions.

Script-variable presentation retains the raw SLSD storage type and resolved declaration type. SCRV identifies reference locals, including those stored with the numeric `float` type. Conflicting metadata remains visible. Generated source is labelled **Reconstruction**.

Semantic diffs include header flags and identities. A reused FormID with a different record type produces a signature mismatch, not a comparison under the wrong schema. `--match editorid` and explicit mappings allow deliberate matching across renumbered records. JSON output is a single document; progress, errors and optional resource statistics go to stderr.

For script diagnostics, `--record` alone means explicit records only. With neither `--actor` nor `--record`, the legacy actor defaults remain and the command states that it is using them. Blank actor arguments are rejected.

## Individual script exports

```powershell
BethesdaMultitool export scripts FalloutNV.esm -o scripts
BethesdaMultitool export scripts FalloutNV.esm -o boone --id BooneSCRIPT --decompiled all
BethesdaMultitool export scripts FalloutNV.esm -o compatible --ext txt
```

Individual FO3/FNV scripts default to `.gek`. The existing GUI Extract action uses the same writer. Aggregate reports remain `.txt`. Stored SCTX is exported without report wrappers, with its Windows-1252 bytes and line endings preserved; one trailing NUL terminator is omitted. Reconstructions use separate `.decompiled.gek` files with a comment banner. The default `--decompiled missing` emits a reconstruction when stored source is unavailable; `all` and `none` are also supported.

`scripts.manifest.json` records source identity/hash, FormIDs, recovery provenance, bytecode order, output hashes, filename adjustments and skipped scripts. Known BMT reconstruction banners are recognized after reopening a generated plugin. Existing targets require `--overwrite`.

## Typed references and bounded paths

```powershell
BethesdaMultitool refs FalloutNV.esm 0x000FE923 --in --format json
BethesdaMultitool refs FalloutNV.esm BooneSCRIPT --out --format json
BethesdaMultitool refs FalloutNV.esm 0x000FE923 --in --untyped-scan
BethesdaMultitool refs FalloutNV.esm TargetEditorID --root ExplicitRootEditorID --max-depth 3 --max-nodes 1000 --format json
```

`refs` reports typed inbound/outbound fields, subrecord occurrences, byte offsets, edge kinds and source-plugin provenance. Compressed records are inspected after decompression; their field offsets are relative to the inflated payload, with physical file offsets left null. Unresolved schema-union alternatives and unexplained byte matches are separate from typed evidence. SCRO entries are indexed; SCRV local-variable indexes are not FormIDs.

Roots are explicit. Bounded traversal follows indexed static references and retains matching paths. Runtime lookups, constructed IDs, condition evaluation, opaque fields and external assets remain outside this graph. Results include coverage and truncation.

## Terminal menu graphs

```powershell
BethesdaMultitool esm terminal-graph DeadMoney.esm 0x0100DAD2 --format json --output terminal.json
BethesdaMultitool esm terminal-graph DeadMoney.esm 0x0100DAD2 --format dot --output terminal.dot
```

This follows structural linked-terminal menus while retaining full conditions and actions. JSON supports further analysis; DOT can be opened in a Graphviz viewer. Cycles, shared children and unresolved links remain visible. `--max-depth` and `--max-nodes` bound the inspected terminals, with boundary edges and truncation retained. `--load-order` uses the same explicit source set described below. Script-driven navigation and runtime condition evaluation are outside the traversal. See [terminal graph details](new_vegas_terminal_graph.md).

## Explicit load order

```powershell
BethesdaMultitool --plain show DeadMoney.esm 0x0200DAD2 --load-order 'FalloutNV.esm;DeadMansHand.esm;DeadMoney.esm'
BethesdaMultitool refs DeadMoney.esm 'DeadMoney.esm:0x0100DAD2' --load-order 'FalloutNV.esm;DeadMansHand.esm;DeadMoney.esm' --format json
BethesdaMultitool esm actor-details OldWorldBlues.esm 'OldWorldBlues.esm:0x01014D8D' --load-order FalloutNV.esm
BethesdaMultitool esm reports DeadMoney.esm --load-order 'FalloutNV.esm;DeadMansHand.esm;DeadMoney.esm' -o merged-reports
```

The semicolon-separated option can be repeated. Relative paths resolve beside the focus plugin; the focus plugin is appended when absent. Duplicate filenames, masters placed after their dependents and missing masters are rejected. `--allow-missing-masters` explicitly reserves separate slots for absent masters and leaves their contents unresolved.

With `--load-order`, bare hexadecimal IDs are load-order IDs. `Plugin.esm:0xID` identifies an ID in that plugin's serialized master namespace. EditorIDs must resolve uniquely; ambiguous matches list the candidates. Without a load-order option, `refs` accepts native file-local IDs.

Output identifies defining ownership, winning plugin, override versions, deleted winners and signature conflicts. Default reference searches use usable winners; `--all-versions` is available for historical file evidence. Typed fields are rebased. Stored source, decompiled text and raw schema values retain the winning plugin's local namespace and are labelled accordingly.

Multiple physical records with the same ID inside the winning plugin are explicitly ambiguous. Prototype INFO fragments must not be assumed to follow a last-record-wins rule. Default merged inspection and traversal exclude these identities and report the limitation; `refs --all-versions` retains the physical evidence and offsets for separate inspection. Single-file typed INFO parsing can still display recovered fragments, but that is not a proven engine override model.

This explicit mode supports FO3/FNV full plugins. Raw reference inspection supports up to 255 loaded/reserved slots. Typed merged inspection and reports currently allow 128, because the existing script model reserves the high bit for SCRV indexes. Modern light/medium plugin namespaces are not inferred.

Aggregate `esm reports --load-order` requires an empty output directory. It selects each unique live winning record before combining typed records, preventing a missing parsed winner or a cleared field from reviving older data. Independently identified child placements retain their own winners. `report_sources.json` records the supplied order, namespace, coverage counts and limits; `record_provenance.csv` records physical versions and exclusions. Deleted, conflicting and ambiguous winners are excluded explicitly. Stored/decompiled text and opaque bytes remain source-local; typed FormID fields use load-order IDs. Per-source derived dialogue trees and earlier CELL/WRLD field or terrain fallback are omitted from this record report view. Quest variable names and placement names/models/bounds are refreshed from surviving winners. Weapon projectile-physics caches are omitted; use the winning PROJ reports. Single-plugin reports also refuse directories containing load-order metadata.

## Actor templates and statistics

`esm actor-details` returns local declarations separately from `effectiveStatistics`, per-group template chains, source plugins and unresolved statuses. Creature DATA health and SPECIAL are retained. Inheritance follows each FO3/FNV template flag independently; leftover local inventory does not override Use Inventory. Cycles, missing masters, missing records, wrong types, missing fields and partial captures have distinct outcomes. Leveled actor templates expose candidates rather than choosing a spawn.

`--engine-profile pc-retail --scenario scenario.json` adds calculated levels and statistics from a bound runtime trace. Scenario inputs include `playerLevel`, `executableSha256`, `runtimeTrace`, `referenceFormId` and `calculationObservationSequence`. Output separates **Stored**, **Inherited**, **Observed** and **Calculated** values and lists missing dependencies.

PC retail player Health and one initialized fixed NPC Health calculation match retained engine captures; [the NPC control](../artifacts/prototype-feedback/actor-calculation/live-027-npc-cli/README.md) calculated 65 from stored Health 50 and the captured Endurance inputs. PC043 fixed/scaled/minimum/maximum/contradictory levels matched [five CLI calculations](../artifacts/prototype-feedback/actor-calculation/live-043-level-cli/attempt-001/validation.json) at player Level 1. [Auto-calc NPC Health reconstructs 95](../artifacts/prototype-feedback/actor-calculation/autocalc-routine-review-001/live-pc050-cli/attempt-001/validation.json) and [fixed creature Health reconstructs 137](../artifacts/prototype-feedback/actor-calculation/creature-dispatch-review-001/live-pc051-cli/attempt-001/validation.json), matching PC050/051. [PC053: fixed 53, scaled 274 and loaded template-on 137 match independent SDK Health](../artifacts/prototype-feedback/actor-calculation/inheritance-loader-review-001/live-pc053-cli/attempt-001/validation.json). [Parent provenance: Pending](../artifacts/prototype-feedback/actor-calculation/inheritance-loader-review-001/loader-source-review-001.json). Broader levels and final critical probability remain pending. One-hand melee pre-target Reconstruction and hit Observed readers are implemented; live validation and later damage calculations remain **Pending** ([invocation operands and later hit adjustments](../artifacts/prototype-feedback/actor-calculation/remaining-stages-review-001/damage-route-review.json)). [CLI58 checks](../artifacts/prototype-feedback/actor-calculation/melee-invocation-implementation-001/cli58-validation.json) · [Native43 checks](../artifacts/prototype-runtime/pc/native-generation-043/final/validation.json). Other games retain their own template semantics.

**Frozen tools:** 13.8 GiB reclaimed. Deduplication verified 15,751 files across 53 generations; content, paths and manifests retained. [Receipt](../artifacts/prototype-runtime/pc/frozen-tool-dedup-001/plan.receipt-20261002T045821515409Z.json).

**PC056: Partial.** Load, baseline, melee setup, placement review and cleanup passed. Combat lease accepted; no damage-stage or critical-invocation rows before the 4,514 ms prepared-hit timeout. Usable invocation capture: Pending. Normal closure passed; originals unchanged. [Outcome](../artifacts/prototype-runtime/pc/session-056-plan/control-result.json) · [Closure](../artifacts/prototype-runtime/pc/session-056-plan/normal-close.json) · [Capture gap](../artifacts/prototype-feedback/report-refresh-034-pc056-001/pc056-invocation-gap.json).

## Source provenance

The Nov 2009–Apr 2010 memory dumps are partial base-game prototype captures whose complete builds are unavailable. July 2010 is a complete base-game prototype. The 2011 corpus is a complete base-game plus DLC build, whose individual plugins can have different development histories. In particular, the 2011 bundle contains both the earlier `DeadMansHand.esm` and `DeadMoney.esm`; their overlapping local IDs must not be merged accidentally.

Capture recovery reports missing fields and retained text withheld by the emission policy. Decompiled text is labelled **Reconstruction**. Corpus validation reads the original files; generated evidence uses a separate output directory.
