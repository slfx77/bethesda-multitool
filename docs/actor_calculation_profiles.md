# Actor calculation profiles

Select `--engine-profile fnv-pc-steam-1.4.0.525` (`pc-retail` alias) for the supplied PC Steam executable, SHA-256 `3a87f92f011e5dc9179ddf733cf08be2b39ea6e5b7a8a9e3a9a72dafcc1b104d`. Unknown profiles are rejected. Stored, inherited, calculated and observed values remain separate. Xbox builds require separate profiles.

## Inputs and outputs

`--scenario` supports `playerLevel` (the player's base ACBS level) for scaled level calculations. A conflicting `executableSha256` blocks the calculation. `--level` remains the inventory-preview level.

For callback stages, supply `runtimeTrace`, `referenceFormId`, `executableSha256` and `calculationObservationSequence`. The selected sequence must be one complete `actor-state` observation bound to the exact source plugin order/hashes, actor reference/base, record kind and executable. Inputs from other observations are never combined. Missing, duplicate, unavailable or conflicting fields remain unavailable; observed zero remains zero.

```json
{
  "runtimeTrace": "capture.ndjson",
  "referenceFormId": "0x00104C0F",
  "executableSha256": "3a87f92f011e5dc9179ddf733cf08be2b39ea6e5b7a8a9e3a9a72dafcc1b104d",
  "calculationObservationSequence": 619
}
```

The sequence above illustrates selection syntax; use a capture containing Native15's typed inputs. PC011 sequence 619 predates those fields.

For natural hit observations, select `damageObservationSequence` and/or `criticalObservationSequence` from a complete trace. Both bind to the attacking reference and base. Damage exposes `WeaponDamage.preTargetStage` as Reconstruction and Observed, plus the prepared hit’s `RuntimeDamage` as Observed. Critical stages, signed threshold, random remainder and decision are Observed; `RuntimeCriticalChance` remains Unavailable. [Reader fixtures](../artifacts/prototype-feedback/actor-calculation/melee-invocation-implementation-001/cli58-validation.json).

| Stage | Inputs | Status |
|---|---|---|
| EffectiveLevel | Resolved UseStats ACBS fields; player level for scaled mode | Reconstruction |
| Health.derived | Ordinary: permanent Endurance and callback/settings. Auto-calc NPC: stored-Health zero gate, stored Endurance, observed level and settings | Reconstruction; ordinary and auto-calc NPC controls verified |
| Health.base | Attributed override getter, callback/stored-component flags and record role | Reconstruction; player overrides, ordinary/auto-calc NPC and fixed/scaled/loaded-template creature controls verified |
| Health.current | Independently reconstructed player base Health and same-row raw selectors 0/1/2 | Reconstruction; PC028 180 → 179 → 180 verified |
| Health.permanent | Independently reconstructed player base Health, raw selector 1 and AV clamp flags | Reconstruction; PC028 permanent 180 with current 179 verified |
| CritChance.derived | Current Luck, AV flags/callback and named settings | Reconstruction; distinct from final weapon critical probability |
| CritChance.weaponStage | Owned player current AV14, equipped-or-null weapon, flags/rate/multiplier and exact profile | Reconstruction; three PC052 cases match the observed native stage |
| RuntimeDamage | Weapon, ammunition, condition, skills, effects, perks and target state | Pending |

Every calculated value includes its profile, contributing inputs, routine evidence, and selected trace hash/line/sequence. Observed Health is never a prediction input. Existing append-only numeric observations remain supported.

## Reconstruction

Level routine `0047DED0` consumes unsigned ACBS bits. Fixed mode returns them directly. Scaled mode stores `raw / 1000` as a float, multiplies the player's unsigned level, truncates, keeps the low 16 bits, applies a nonzero minimum with an immediate return, then applies a nonzero maximum. Zero bounds add no clamp; contradictory bounds preserve engine ordering. Source ownership follows the resolved UseStats chain.

Health callback `00643670 → 006436C0` uses clamped permanent Endurance and separate player/NPC settings. CritChance callback `00643B10` uses clamped current Luck. Native15 supplies typed settings, callback metadata, base fields and game-thread floating-point state. The profile requires x87 control word `0x007F`; other precision states are unavailable. The ordinary NPC route uses permanent Endurance. Auto-calc NPC route `00603FC0` first returns zero for stored Health zero; otherwise it uses stored Endurance, observed level floored at 1 and named settings, then truncates and clamps the result at zero. [Auto-calc NPC Health 45 + 50 = 95 matches PC050](../artifacts/prototype-feedback/actor-calculation/autocalc-routine-review-001/live-pc050-cli/attempt-001/validation.json).

`Health.base` follows `008803A0`: a present non-skill override returns directly, including zero. For player/NPC, an absent override reaches the callback through observed flag `0x80` or `0x800`; flag `0x40` adds stored Health. The creature route instead selects its stored Health component. Native16/17 getter evidence must match the subject/base IDs, base address, role-specific getter and vtable slot. Missing evidence, unresolved stored-value inheritance and other dispatcher branches remain unavailable. PC017 and PC018 verify the absent and present player branches. [Getter proof](../artifacts/prototype-feedback/actor-calculation/base-override-validation-016.json).

Fixed creature Health follows `008D0360 → 005FA180 → 005F0FB0 → 005F0B00 → 005F8E90`: with an absent override and unscaled component, use the unsigned 16-bit component value. [Fixed creature Health Reconstruction 137 matches PC051](../artifacts/prototype-feedback/actor-calculation/creature-dispatch-review-001/live-pc051-cli/attempt-001/validation.json). Its `Health.derived` callback stage remains Unavailable. Native42 repeated child fields also admit a loaded UseStats component; the getter reads that component directly. [Paired controls observed off/on Health 53/137 and Level 3/7](../artifacts/prototype-feedback/actor-calculation/scaled-health-review-001/inheritance-pc053-review-001/review.json). Scaled `005F8E90` uses the captured player base level and `0047DED0` result, signed-16 level floored at 1, then the low unsigned-16 Health product. [CLI57 loaded-child input and result proof](../artifacts/prototype-feedback/actor-calculation/inheritance-loader-review-001/live-pc053-cli/attempt-001/validation.json).

Player current Health follows `0093ACB0`: add raw selectors 0, 1 and 2 in that order to independently reconstructed base Health under the observed floating-point control. All inputs must share one attributed observation. [Player current Health Reconstruction 180 → 179 → 180 matches PC028 using raw selectors 0/1/2](../artifacts/prototype-feedback/actor-calculation/player-current-health-review-001/live-pc028-cli/attempt-001/validation.json).

Player permanent Health follows `0093AD60`: independently reconstructed base plus raw selector 1, then AV-flag clamping. Weapon stage `00646D80` uses current AV14, the automatic-weapon rate divisor (zero becomes 1), and the nonnegative critical multiplier. The owned native return is comparison-only; natural-hit modifier and threshold stages now have Observed readers; final critical probability remains Unavailable. [Implementation and tests](../artifacts/prototype-feedback/actor-calculation/grouped-stages-implementation-001/validation.json) · [Native observation controls](../artifacts/prototype-runtime/pc/native-generation-041/validation.json).

The retained Ghidra image is `FalloutNV_runtime_image.bin`, SHA-256 `e46b43cdaa32d9b79b7816fa45cb076c7ed59e335b1533118dcb6bf1d9da692d`. Exact routines, read-only export receipts and evidence are under [actor-calculation](../artifacts/prototype-feedback/actor-calculation/).

## Observed

Current CLI58 passed 392 focused actor and dialogue cases. [Build and tests](../artifacts/prototype-feedback/actor-calculation/melee-invocation-implementation-001/cli58-validation.json) · [Earlier CLI57](../artifacts/prototype-feedback/actor-calculation/inheritance-loader-review-001/cli57-validation.json) · [Earlier CLI55](../artifacts/prototype-feedback/actor-calculation/grouped-stages-implementation-001/validation.json) · [Earlier CLI54](../artifacts/prototype-feedback/actor-calculation/player-current-health-review-001/validation.json). [Player permanent Health reconstructs 180 while current Health is 179 in PC028](../artifacts/prototype-feedback/actor-calculation/grouped-stages-implementation-001/retained-pc028/attempt-001/validation.json). [PC052: permanent Health and the weapon critical stage match independent CLI55 calculations for no weapon, pistol and automatic weapon](../artifacts/prototype-feedback/actor-calculation/grouped-stages-implementation-001/live-pc052-cli/attempt-001/validation.json). [Inventory restoration, independent reload, normal closure and originals passed](../artifacts/prototype-runtime/pc/session-052-plan/control-result.json). [Auto-calc NPC Health 45 + 50 = 95 matches PC050](../artifacts/prototype-feedback/actor-calculation/autocalc-routine-review-001/live-pc050-cli/attempt-001/validation.json). [Fixed creature Health Reconstruction 137 matches PC051](../artifacts/prototype-feedback/actor-calculation/creature-dispatch-review-001/live-pc051-cli/attempt-001/validation.json). [Player current Health Reconstruction 180 → 179 → 180 matches PC028 using raw selectors 0/1/2](../artifacts/prototype-feedback/actor-calculation/player-current-health-review-001/live-pc028-cli/attempt-001/validation.json). [PC053: fixed 53, scaled 274 and loaded template-on 137 match independent SDK Health](../artifacts/prototype-feedback/actor-calculation/inheritance-loader-review-001/live-pc053-cli/attempt-001/validation.json). [Seven phases, restoration and normal closure passed; 12,789 events, zero loss/errors](../artifacts/prototype-runtime/pc/session-053-summary.json). [Parent provenance: Pending](../artifacts/prototype-feedback/actor-calculation/inheritance-loader-review-001/loader-source-review-001.json).

PC011 independently matched loaded Health/CritChance callback bytes, eight named settings, ACBS fields and x87 `0x007F` against retained routines. Player and Doc Mitchell were level 1, with Health 180/65, permanent Endurance 4/4 and current Luck 9/4. [Loaded-memory review](../artifacts/prototype-feedback/actor-calculation/live-011-memory-review.json). Earlier controlled Endurance changes produced player Health 180/200/220 and Doc Health 65/70/75, then restored the original state.

CLI18 built and passed **49 actor CPU cases** (23 typed-value, 17 level, nine service), within the successful 105-case affected-area run. [Pinned validation](../artifacts/prototype-feedback/batch18-cli-validation.json). These check reconstruction and input handling; engine acceptance is recorded separately.

CLI21 built with zero warnings/errors and passed **72 actor CPU cases** (46 typed-value, 17 level, nine service). The added cases cover attributed override presence/value, missing or conflicting ownership/getter evidence, callback selection, stored-value inclusion and unresolved inheritance. [Receipt](../artifacts/prototype-feedback/focused-twentyfirst-health.receipt.json) · [Frozen runtime](../artifacts/prototype-runtime/pc/tools/cli-generation-021.manifest.json). At the CLI21 checkpoint, engine calibration was pending.

PC012 supplies Native15's typed inputs. The shipped CLI18 replay passed against the exact ten-plugin order: player sequence 510 and Doc Mitchell sequence 668 produce Health callback stages 80/15 and CritChance callback stages 9/4. Observed total Health remains 180/65; base Health remains unavailable without override presence. Both fixed-level reconstructions return 1. Trace hash, source line, actor identity and contributing inputs are retained in [CLI acceptance](../artifacts/prototype-feedback/actor-calculation/live-012-cli/validation.json).

The separate loaded-memory review matched ten routine windows and fifteen named settings, including the damage routines at `00644CE0` and `00645380`. The damage condition helper reads eight-byte constants: `0101DE30 = 0.75`, `01051680 = 0.6700000166893005`. [Memory review](../artifacts/prototype-feedback/actor-calculation/live-012-memory-review.json).

PC017 / CLI32 reconstructs player Health.base as **180**, matching the independently observed engine value. The selected Native26 observation reports no override: callback 80 plus stored Health 100. All ten source plugins, trace sequence 572 and input/output hashes passed. [Live acceptance](../artifacts/prototype-feedback/actor-calculation/live-017-cli/validation.json).

PC018 / CLI32 / Native26 verifies the present-override branch: reconstructed Health.base **181 = observed 181**, using player observation sequence **1647** and the exact ten-plugin binding. [Live acceptance](../artifacts/prototype-feedback/actor-calculation/live-018-cli/attempt-001/validation.json).

## Pending

Hit capture: Implemented. One-hand melee operands and return, selected weapon/fallback, ordered critical stages and threshold decision are bound to the original hit. [Native43 fixtures](../artifacts/prototype-runtime/pc/native-generation-043/final/validation.json) and [CLI58 readers](../artifacts/prototype-feedback/actor-calculation/melee-invocation-implementation-001/cli58-validation.json) passed. Live damage/critical validation: Pending.

**Frozen tools:** 13.8 GiB reclaimed. Deduplication verified 15,751 files across 53 generations; content, paths and manifests retained. [Receipt](../artifacts/prototype-runtime/pc/frozen-tool-dedup-001/plan.receipt-20261002T045821515409Z.json).

**PC056: Partial.** Load, baseline, melee setup, placement review and cleanup passed. Combat lease accepted; no damage-stage or critical-invocation rows before the 4,514 ms prepared-hit timeout. Usable invocation capture: Pending. Normal closure passed; originals unchanged. [Outcome](../artifacts/prototype-runtime/pc/session-056-plan/control-result.json) · [Closure](../artifacts/prototype-runtime/pc/session-056-plan/normal-close.json) · [Capture gap](../artifacts/prototype-feedback/report-refresh-034-pc056-001/pc056-invocation-gap.json).

PC043 fixed/scaled/min/max/contradictory level controls and the scoped PC045 flags-zero hit/Health delta passed. [Level controls](../artifacts/prototype-feedback/actor-calculation/live-043-level-cli/attempt-001/validation.json) · [Scoped hit](../artifacts/prototype-runtime/pc/combat-idle-target-review-001/scoped-validation.json). Broader levels/inheritance and modifiers remain Pending. Native43 captures the original melee invocation and prepared hit. Live comparison and later damage adjustments remain Pending. [Damage route](../artifacts/prototype-feedback/actor-calculation/remaining-stages-review-001/damage-route-review.json). Critical fallback and threshold-decision capture is implemented; live validation remains Pending. [Critical caller](../artifacts/prototype-feedback/actor-calculation/critical-probability-review-001/export-followup-001.json). [Parent provenance: Pending](../artifacts/prototype-feedback/actor-calculation/inheritance-loader-review-001/loader-source-review-001.json). CLI18 includes the corrected float multiplication (`1.3f × 10` rounds before truncation).
