# Actor statistics output correction

NPC critical chance is now **Unavailable** in the GUI, text and typed reports unless separate runtime evidence supplies a value. The CSV retains its `CritChance` column as empty and appends `CritChanceStatus`; stored Luck remains unchanged. These surfaces use the shared `ActorStatisticsService` availability value. Bound traces continue to expose individual current/base/permanent observations with their source lines.

ACBS level encoding is preserved. The signed New Vegas stored multiplier is divided by 1,000, as specified by xEdit's NPC_ and CREA definitions. The actor inspector also exposes `LevelEncoded` when this mode is set. Legacy raw-value views label it **Level (encoded)**; CSVs retain their original `Level` columns and append `LevelEncoding`. Effective spawned level remains separate.

The source reference is retained at `artifacts/prototype-feedback/report-refresh/actor-rules-primary/wbDefinitionsFNV.pas`, SHA-256 `67d83348bec55c76a025598c7b13950464ded52347f517f49a137effabcf63d8`, with its official-source download receipt. Relevant lines are 5666/8380 (signed division) and 5603/8317 (mode flag). This confirms plugin encoding; early dump layout validation remains capture-specific.

The NPC CSV keeps its 75-column prefix and appends two columns; creature CSV keeps its 20-column prefix and appends one. Faction, spell, inventory and package subrows retain alignment. Tests cover Luck 0/5/10 across CSV and both report paths, raw-level preservation, all existing subrow kinds, and signed multipliers 1500/0/−1500.

Checkpoint: Batch 10 patch applied after all ten preimages matched. `git diff --check` passed. Parent-coordinated Development and Windows builds passed. The corrected focused run passed 116/116 cases, including `ActorStatisticsServiceTests`, `ActorTemplateResolutionTests`, `CsvNpcColumnAlignmentTests` and `ReportGeneratorTests`. Receipt: `artifacts/prototype-feedback/focused-tenth-retry.receipt.json`; frozen CLI DLL `42790d4e37f2ab7d50d830954da7643159b7d2133b1ea67912d53f3931ac82d9`. The new 50-capture export is running.

Weapon detail/profile and CLI labels now show the stored critical multiplier as `x…`. Three parameterized cases in `ProfileParityTests.WeaponCriticalChance_RemainsAMultiplierAcrossDisplaySurfaces` cover 0/0.5/2 through all three paths; all three passed. The applied patch and hashes are under `artifacts/prototype-feedback/report-refresh/weapon-multiplier-labels.*`; validation is retained in `artifacts/prototype-feedback/focused-tenth-weapon.receipt.json`.

Remaining review: the runtime ACBS reader's older ×100 comment and 1000 upper bound, plus other uncalibrated derived-stat formulas. The runtime reader was not changed in this patch. PC actor snapshots are being developed separately.
