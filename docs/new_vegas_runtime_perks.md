# Runtime perk recovery (P2-T12)

The old runtime reader read a speculative 12-byte entry, recognized an ability pointer, and otherwise defaulted to an entry point. It did not recover entry-point identifiers, result functions, parameter objects, quest stages, or per-entry condition tabs.

`RuntimePerkEntryReader` now identifies each captured entry through its own MSVC RTTI. An unavailable or unsupported class retains the eight-byte base header and rank/priority with an explicit limitation; it does not become an entry point by default. Reading the base record remains in `RuntimeMagicReader`.

## Calibration

Official Microsoft `cvdump -t` was run against the supplied July 2010 Beta, August 2010 MemDebug, and February 2011 Beta PDBs. All 15 selected class layouts agree in size and named field offsets. Full output, source/tool hashes, command receipts, selected excerpts, and the automated comparison are in `artifacts/prototype-feedback/runtime-perk/`.

| Runtime structure | Size | Recovered members |
| --- | ---: | --- |
| BGSPerkEntry | 8 | Rank +4, priority +5 |
| BGSAbilityPerkEntry | 12 | Ability pointer +8 |
| BGSQuestPerkEntry | 16 | Quest pointer +8, byte stage +12 |
| BGSEntryPointPerkEntry | 20 | Entry point +8, result function +9, tab count +10, function pointer +12, TESCondition array pointer +16 |
| OneValue function data | 8 | Float +4 |
| TwoValue function data | 12 | Floats +4 and +8 |
| LeveledList function data | 8 | Form pointer +4 |
| ActivateChoice function data | 112 | Label pointer +4, inline Script +8, uint16 flags +108 |
| TESCondition | 8 | Inline linked-list head; one head per condition tab |
| TESConditionItem | 28 | Full type/flags, comparison union, function/parameters, run-on/reference |

The PDB distinguishes the result function (`EntryPointPerkEntryData.cFunction`) from the parameter-data kind (`BGSEntryPointFunctionData::ENTRY_POINT_FUNCTION_DATA`). The latter is 0=None, 1=OneValue, 2=TwoValue, 3=LeveledList, 4=ActivateChoice. Shared display, plugin parsing and encoding now make this distinction too. A leveled-list FormID cannot be classified as a float merely because the same bits represent a finite number.

Independent BMT checks on `Fallout_Release_Beta.xex.dmp` (SHA-256 `056a99a8ab9b93bf90ef8ca35fecf6f86f1499bb0632bbf603cdb08cc0a445a8`) confirm the early capture has these classes: ActionBoy's entry resolves to `BGSAbilityPerkEntry`; BetterCriticals resolves to `BGSEntryPointPerkEntry`, with entry point 2, result function 3, three condition tabs, and OneValue data 1.5. Its three tab heads are captured and empty. These commands used the unchanged existing CLI; they are calibration evidence, not a test of the new decoder. The `perk-nov-*.receipt.json` and stdout files are under `artifacts/prototype-feedback/research-chains/receipts/`.

## Evidence and boundaries

- CLI `show`, text reports and CSV share runtime entry/function bytes, class names, virtual addresses, recovery limitations, function parameters, and ordered condition groups. Existing CSV columns retain their order; new columns are appended. Top-level perk conditions also have explicit CSV rows.
- Runtime bytes are stored separately from plugin DATA/EPFD bytes. Padding, vtables and heap pointers are not passed off as serialized plugin payloads.
- Empty captured condition heads differ from uncaptured heads. Node walks count visits, cap at 256, and report cycles or missing tails. Conditions retain OR/UseGlobal flags, raw bytes, pointer-resolution limitations and semantic run-on references.
- Activation labels and flags are decoded; inline scripts use the existing atomic source/bytecode reader. Source, decompilation and compiled bytes remain separate. Unknown classes and uncaptured pointed-to objects remain unavailable.
- This recovers authored/runtime structures, not an evaluation of whether a perk currently applies to an actor or a proof of an engine combat calculation.
- The November 2009–April 2010 dumps span the Fallout 3 to New Vegas transition. PDB agreement is not a match to these unavailable builds. Every runtime entry exposes its layout basis and states that RTTI proves the captured class name, while field-offset compatibility with that executable remains unverified. The direct November checks above validate only the selected observed structures; other decoded values remain documented-layout interpretations that require per-capture corroboration.
- Runtime entries with incomplete evidence and runtime activation entries are withheld from plugin encoding with a warning. Activation-script serialization remains unsupported in this encoder; raw evidence remains available in reports.
- No network sources or source dumps/builds were modified. Matching class layouts across these PDBs does not identify an unrelated executable as belonging to a particular PDB.

## Validation status

Source is ready for the parent-coordinated build. No build or test suite was run by this lane. Focused tests: `RuntimePerkEntryReaderTests` (11 parameterized/behavior cases), `PerkParameterDataTests` (7), existing `IntenseTrainingPerkRankRegressionTests`, `PerkEffectReportTests`, and `QustPerkCtdaSanitizerTests`. The new cases cover concrete output/encoding behavior, both plugin byte orders, independently selected function classes, unknown/truncated captures, ordered tabs, empty/missing lists, cycles, globals and references. Two existing sanitizer fixtures now use EPFT=1 for their one-float payload.

Parent-coordinated Development build 5 compiled the implementation, and the focused run passed all 463 cases across 37 selected classes, including runtime perks and rebasing. The actual November Better Criticals retry recovered entry point 2, result function 3, one-value parameter kind 1, value 1.5 and three captured empty condition tabs (`artifacts/prototype-feedback/retry-checks/november-bettercriticals-fixed.stdout.txt`). A full 93-PERK export comparison is separate from that selected-record verification.

The load-order rebaser now classifies perk parameters through the game-specific condition table, keeping decoded numeric FormIDs and their resolved references in the same namespace. Scalars, ignored reference storage, runtime addresses and captured bytes retain their original values. Five parameterized cases in `TaggedFormIdRebaseTests` cover both top-level and entry-group conditions, including two-reference functions and unknown functions; they passed in the coordinated run above.

Human presentation now uses the concise labels “PDB layout; capture compatibility unverified” and “RTTI matched”. The full basis/status text remains in the raw model and dedicated CSV columns.
