# New Vegas recheck follow-up

The offline research recheck found three remaining implementation gaps addressed in this batch: script locals displayed as numeric variables despite SCRV reference evidence, message-button conditions missing from normal inspection, and aggregate reports limited to one plugin. The existing audit and its original captures remain historical evidence.

## Changes

- **TG08 — script-variable presentation:** one shared resolver identifies reference locals from the owning script's SCRV entries. TERM, INFO, SCPT and quest inspection, text reports, terminal graph JSON and script manifests use it. Raw storage bytes remain available. Integer/reference contradictions are labeled explicitly. The February dump's `myLink` should therefore display as `ref` without changing captured data or reconstructed source.
- **TG11 — message conditions:** parsing retains conditions under their preceding ITXT button, including empty labels and adjacent string operands. Normal `show` and message reports expose condition order, grouping, operands and Run On. Conditions preceding every button remain unassigned; new-record encoding refuses ambiguous ownership rather than emitting an unconditional message.
- **TG10 — aggregate load-order reports:** `esm reports --load-order` selects unique live physical winners, uses rebased typed IDs, and exports the supplied order and physical version provenance. Deleted, conflicting, ambiguous and unparsed winners do not revive older typed records. Child placements retain independent identity. Report directories cannot silently mix single-plugin and load-order provenance.

Example:

```powershell
BethesdaMultitool --plain esm reports DeadMoney.esm --load-order 'FalloutNV.esm;DeadMansHand.esm;DeadMoney.esm' -o merged-reports
```

Use a new or empty output directory. Read `report_sources.json` and `record_provenance.csv` alongside individual CSV/text reports. Typed IDs are global to the supplied order; stored/decompiled source and opaque bytes remain source-local. The report view omits per-source dialogue trees and terrain rather than claiming they describe the merged runtime.

## Remaining boundaries

- **TG09:** reference queries still do not execute scripts, evaluate dynamic lookups or gameplay conditions, infer opaque fields, inspect external assets, or reconstruct a full memory-dump pointer graph. A zero inbound count remains bounded evidence.
- **TG12:** runtime health, damage and effective level remain `NotComputed`. Stored statistics and template inheritance are available, but difficulty, effects, perks, scripts and spawn state are not supplied by a plugin-only query.
- **TG08:** some embedded-script external variable names still require matching numeric indexes to the owner script. Terminal graphs retain guards without simulating them. Reconstructed dump source and its bytecode are one witness, not independent evidence.
- **TG10:** a complete runtime dialogue/terrain merge and the meaning of repeated physical prototype records remain outside this report view. This batch does not replace the broader viewer/inspection merge behavior.

## Validation

Validation completed sequentially, with per-command deadlines and stdout/stderr receipts under [TestOutput/audit-recheck-fixes-20260929](../TestOutput/audit-recheck-fixes-20260929/). The final Development CLI/test build passed with zero errors in 96 seconds. It reported 173 warnings in the existing broader source tree; the new resolver's analyzer warning was corrected. No analyzer crashes or locked-output copy warnings were observed. This is not a warning-free repository build.

All **188 focused tests passed**, with zero skips, in approximately seven seconds on the final binary. The batch adds 19 cases across three small behavior-focused test classes, using parameterization for shared scenarios. Existing neighboring regressions cover INFO/TERM presentation, script manifests, full text, markup, terminal graphs and message encoding. No full-suite, GUI interaction, packaging or published/AOT validation was run.

All three original corpus retries succeeded:

| Input/check | Result | Time |
| --- | --- | --- |
| February partial dump, TERM `00132162` | `ref myLink (scrv-local-reference; storage type byte 0)`; unchanged 25-byte reconstructed unlock action | 9.4 s |
| Retail `vHDTurretMessageNCR01` | Leave It Alone has zero conditions; Repair Turret owns `GetActorValue(Repair) >= 45 [Run On: Subject]` | 8.4 s |
| 2011 FalloutNV + DeadMansHand + DeadMoney reports | Separate slots 0/1/2; DeadMoney local `0100DAD2` appears as global `0200DAD2` with correct owner/winner | 26.5 s |

The merged manifest records 547,658 physical versions and 546,959 identities: 517,091 included in the typed view, 29,817 not represented in that view, and 51 deleted winners excluded. Inclusion does not imply that every field has its own export column. No ambiguous or signature-conflicting winners occurred in this particular input set; synthetic tests exercise both exclusions.

The [capture validator](../TestOutput/audit-recheck-fixes-20260929/validate.py) passed all **29 output/hash/integrity checks**, including unchanged hashes for all five game inputs. Exact command arguments, elapsed times and capture hashes are in the adjacent JSON receipts; [validation.json](../TestOutput/audit-recheck-fixes-20260929/validation.json) contains the check results. The final executable is `src/BethesdaMultitool/bin/development/Release/net10.0/BethesdaMultitool.exe`; the separate Full-profile output was not rebuilt.

The first no-restore Development build stopped at a missing shared dependency assets file. A subsequent locked restore exposed stale lockfiles for dependency declarations already present in this checkout (the analyzer version and shared Blender project). Their prior copies are saved under `lockfiles-before/`; restoring the current declarations succeeded. No dependency version was chosen as part of these fixes.

All research inputs are opened read-only. No TCRF host is accessed.
