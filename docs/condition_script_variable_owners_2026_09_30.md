# Condition variable names

`ConditionDisplayContext.From` now builds a lazy owner index from the current record collection. `GetScriptVariable` follows typed REFR/ACHR/ACRE base links and attached SCRI fields to the script's SLSD/SCVR table. Selected load-order views rebuild the index after FormID rebasing; parser-context links are not reused.

The motivating BMT evidence is in `artifacts/prototype-feedback/research-chains/research-findings.md`: Christine's terminal checks `Reprogram` at slot 3 of `01006624 NVDLC01SuitesSetupREF`; Honest Hearts packages check `iState`, `bPlayerInterference` and `bPlayerRushes` at slots 1, 7 and 11 of `01010008 NVDLC02ZionArrivalBox`. Those names previously required manual joins of exported records.

Typed script owners covered are NPC_, CREA, ACTI, CONT, TERM, DOOR, LIGH, FURN, QUST, AMMO, ALCH, CCRD and CHAL. The later T03 checkpoint adds stored WEAP, ARMO, BOOK and MISC SCRI links. Placements come from the collection's cells and map markers. Unsupported base types and placements absent from these typed views retain numeric variables.

Missing links, conflicting copies, cleared scripts and incomplete variable tables also retain `var N`. No ownership comes from EditorID similarity, ordinary script references or neighboring records. Conditions remain static descriptions.

Focused coverage: `ConditionDescriberTests` adds parameterized reference/base variants, incomplete and ambiguous ownership, an oversized index, and selected/rebased namespace isolation. Existing `ScriptSymbolResolutionTests` cover the shared resolver. The coordinated Development build and 228 focused cases passed (`artifacts/prototype-feedback/focused-eighth.receipt.json`, DLL `64d0729b…`).

Actual BMT replays passed in `artifacts/prototype-feedback/retry-checks/condition-owner-replay-001/validation.json`: Christine's terminal resolves `Reprogram`; Honest Hearts packages resolve 68 uses of `iState`, 10 of `bPlayerInterference` and four of `bPlayerRushes`. Source and tool hashes remained unchanged. These describe stored conditions; engine evaluation remains separate.

## Stored item owners: Development14 checkpoint

WEAP, ARMO, BOOK and MISC now retain a nullable `ScriptFormId` from an exactly four-byte stored SCRI. The shared owner index follows this explicit link. Absent or malformed fields remain null; an explicit zero remains zero. Runtime item readers currently supply no validated attached-script field for these types, so runtime-only instances stay unresolved rather than guessing a pointer layout.

The local xEdit source `artifacts/prototype-feedback/report-refresh/actor-rules-primary/wbDefinitionsFNV.pas` defines SCRI as a SCPT FormID at line4006 and includes it in ARMO5058, BOOK5144, MISC8250 and WEAP9864. SHA-256: `67d83348bec55c76a025598c7b13950464ded52347f517f49a137effabcf63d8`. No network lookup was used for this change.

`ScriptFormId` already belongs to `EsmFormIdPropertyRegistry`; the reflection-based collection rebaser clones and remaps the new properties. All eight parameterized `StoredItemScriptOwnershipTests` cases passed in Development14, covering both byte orders, explicit reference→base→script ownership, absent/zero/truncated links, unrelated reference rejection, conflicting copies, numeric fallback and nonzero namespace rebasing. The [focused run](../artifacts/prototype-feedback/focused-fourteenth-message-script.receipt.json) passed 101 cases with no failures.

The [bounded retained-report review](../artifacts/prototype-feedback/report-refresh/stored-item-owner-real-control.json) found no real item-owned external-variable control to replay. This extension therefore has fixture validation; the earlier Christine and Honest Hearts replays validate other owner types. Missing matches in those retained reports do not establish absent game content.
