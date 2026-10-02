# Typed PC runtime operations

[PC017](../artifacts/prototype-runtime/pc/session-017-summary.md) verified player position changes and restoration. [PC018](../artifacts/prototype-runtime/pc/session-018-summary.md) verified rotation, inventory/equipment, objective flags, a quest variable, message choice, Health override and reference enable state: 21 actions, 19 milestones and 12 restoration comparisons. [PC022](../artifacts/prototype-runtime/pc/session-022-summary.md) passed player MoveTo to the stationary chair and stage 0→10 on the scriptless control quest: **2 mutations, 4 milestones and 12 independent restoration comparisons**. Six captures contain **9,899 native events**, with zero errors/gaps/drops. Normal closure and original-file verification passed. Attributed combat remains pending. [Current acceptance](../artifacts/prototype-feedback/plan-audit-20260930-current/AUDIT.md).

Requests execute on the game thread. The exact executable profile and successful game load are required. Every identity pair resolves the current engine plugin slot, looks up the form and checks its actual FormID/type. Actor references retain their actual base identity. `@player` is accepted only with local ID `000014`. Payloads contain validated identities, operation names and bounded numbers; fixed SDK function bodies receive actual form pointers as arguments.

| Kind | Request | Payload after operation name | Terminal event |
|---|---|---|---|
| 15 | Reference state | No operation prefix: `plugin`, local ID; legacy `player` supported | `reference-state` |
| 16 | `start-combat`, `move-reference` | Subject identity, other identity | `action-result` |
| 16 | `stop-combat`, `enable-reference`, `disable-reference` | Subject identity | `action-result` |
| 16 | `set-position`, `set-rotation` | Subject identity, axis `X`/`Y`/`Z`, finite value within ±10,000,000 | `action-result` |
| 17 | `read-inventory` | Subject identity, optional selected item identity | `inventory-state` |
| 17 | `add-item`, `remove-item` | Subject identity, item identity, count 1–10,000 | `action-result` |
| 17 | `equip-item`, `unequip-item` | Subject identity, item identity | `action-result` |
| 18 | `read-quest-state` | Quest identity, optional objective index | `quest-state` |
| 18 | `set-quest-stage` | Quest identity, stage 0–65,535 | `action-result` |
| 18 | `set-quest-objective` | Quest identity, index 0–65,535, `completed`/`displayed`, state `0`/`1` | `action-result` |

Fields are tab-separated. Identity pairs are plugin filename and 1–6 hexadecimal local-ID digits. Extra fields, embedded control characters, zero IDs and load-order IDs are rejected. Item lists/leveled lists are excluded; unsupported item types retain unavailable rows. Combat requires two distinct actors; movement requires distinct references. Player disabling is unsupported.

`actionBoundaries` advertises a native `action-begin` event for requests 3 and 7–18 during capture. It carries the request ID, numeric `requestKind`, frame and sequence, and is emitted on the game thread immediately before validation/dispatch. Passive milestones use this boundary to exclude queued observations from before the action; acceptance remains a separate terminal result.

Reference observations retain disabled state, position, rotation in engine degrees, parent cell, and—on actors—combat state/target, Health, equipped weapon base form, current item health and weapon mod flags. Each field carries observed/empty/unavailable status. The equipment slot is the pinned SDK’s weapon slot 5. A mutation records before and after observations separately from SDK function acceptance. After mutation, the subject and applicable item are looked up again; FormID, type and base must still agree before readback. Missing identities produce unavailable state instead of reusing a stale pointer. Combat scheduling may change state on a later frame; scenario milestones must inspect subsequent observations.

Inventory enumeration retains the engine-reported number of item types, up to 128 rows, and a final count recheck. Missing rows, unsupported forms, changed count or a limit produce `partial`. An observed empty inventory remains `complete` with zero rows. Selected-item reads retain count and equipped state. The API currently exposes base-item identity, not individual inventory-stack instance identity.

Quest observations retain current stage/running state and requested objective completion/display state. Stage mutations additionally inspect `GetStageDone`. Missing or invalid SDK returns remain unavailable. Accepted means the fixed expression returned a finite numeric command result; before/after values and later milestones establish outcomes.

Actor-state kind 12 adds Perception, Intelligence, Charisma, EnergyWeapons, MeleeWeapons, Unarmed, Explosives, DamageResistance and DamageThreshold to current/base/permanent queries. DR/DT command tokens come from the actual bounded engine AV-name table; PC011's unavailable DR rows remain preserved. Existing observed Level, Health, Endurance and critical chance remain independent values. Current loaded ammo, broader armor/effect state and calibrated combat calculations still need additional supported observations.

Native15 appends bounded `baseActorData`, eight named `gameSettings`, and six `actorValueInfo` rows to the same actor-state event. Raw bytes, addresses, identities and unavailable states are retained. Base fields include encoded level, clamps, flags, stored Health/SPECIAL and raw template links; inheritance is not evaluated. NPC fields, settings and Health/Critical Chance callbacks are corroborated by PC011 and retained routines. Common creature layout and template fields retain their SDK basis. Native16/17 add attributed `baseOverride` reads; PC017/018 verify the absent and present player branches. [Calculation profiles](actor_calculation_profiles.md).

The terminal actor state records `floatingPoint.x87ControlWord`, sampled using `FNSTCW` on the game thread before actor queries. This is the current x87 control state; SSE/MXCSR state and control changes inside individual engine routines remain separate inputs.

**NPC levels: Observed and calculated.** PC043 and five actual CLI40 calls agree on fixed 7, scaled 2, minimum 4, maximum 3 and contradictory bounds 4 at player Level 1. Independent reload restored 33 state groups and 48 raw selector fields. [Level comparison](../artifacts/prototype-feedback/actor-calculation/live-043-level-cli/attempt-001/validation.json).

**Attached reference locals: Implemented; live Pending.** `read-reference-variable` uses the `referenceScriptLocals` capability and request7 with `reference-local/1`, plugin, local FormID and variable name. Direct script/event storage preserves owner, slot/type, raw double and absence. CLI43 passed 99 focused cases; Native36 passed 46 new cases plus retained regressions. [Managed checks](../artifacts/prototype-feedback/cli43-reference-locals-001/validation.json) | [Native checks](../artifacts/prototype-runtime/pc/native-generation-036/validation.json).

**Offline script mapping: Verified owners and bytecode.** CLI47 mapped 632 named command observations in retained PC043 to their owners and bytecode, among 2,666 mappings. The 1,008 temporary-script mappings remain Unavailable. Ten physical SCDA readbacks passed. Normalized instruction offsets: Unavailable. [PC043 replay](../artifacts/prototype-runtime/pc/script-map-completeness-candidate-001/replay-pc043/attempt-001/validation.json) | [CLI47 checks](../artifacts/prototype-feedback/cli47-script-map-completeness-001/validation.json).

**Capture profile boundary: Implemented.** `runtime capture --profile` refuses before connection, with `runtime run` and `--guest-manifest` guidance. Twelve focused cases passed. Live recorder retry: Pending. [CLI46 checks](../artifacts/prototype-feedback/cli46-quest-assets-001/validation.json).

**Embedded stage export: Verified for VCG00.** `esm diagnose-scripts` exports stored `.gek`, reconstructed `.decompiled.gek`, SCDA bytes and an owner/stage/entry/occurrence manifest. Both supplied VCG00 editions passed 38 stored and 35 compiled fragment checks, including exact bytes, ordered references, physical offsets and external slot 6 (`bRunTimer`). [Two-edition export checks](../artifacts/prototype-feedback/quest-script-export-001/intro-readback-002/validation.json).

## Numeric game settings

`read-game-setting` and `set-game-setting` use `name` and an optional write `value`. They require the respective `gameSettingRead` and `gameSettingWrite` capabilities. Names use the numeric `b`, `i`, `u` or `f` prefix. Integer writes must survive the engine command's float parameter exactly; Boolean writes accept 0 or 1.

```json
{"kind":"read-game-setting","name":"fVanityModeAutoDelay"}
{"kind":"set-game-setting","name":"fVanityModeAutoDelay","value":1800}
```

Reads retain lookup candidates, setting identity and raw value. Writes retain the original, requested, submitted and readback values. Run writes through `runtime run` with the isolated profile, then restore the observed original value. The wire uses `gmst/1\tread\tNAME` on request 7 and `gmst/1\twrite\tNAME\tVALUE` on request 3; terminal events remain `snapshot` and `action-result`.

CLI40 passed 22 numeric-setting regression cases; Native33 passed 55 native setting cases. PC032 verified the typed read of fVanityModeAutoDelay=120. PC038 passed the 120 to 1800 setting change, inverse to 120, and independent reload/readback, including six state groups and 48 raw selector fields. Camera behavior is Unperformed. [PC038 summary](../artifacts/prototype-runtime/pc/session-038-summary.json). [PC032 replay](../artifacts/prototype-runtime/pc/session-032-review-001/validation.json) | [PC035](../artifacts/prototype-runtime/pc/session-035-plan/startup-failure.json) | [CLI40](../artifacts/prototype-feedback/cli40-owner-conditions-001/validation.json).

## Owner-condition pilot

`read-owner-conditions` uses request22 and requires `ownerConditionListProbe`. The owner is `BMTConditionControl.esp:000800`, the subject is player, and the target is player or Doc Mitchell's reference `FalloutNV.esm:104C0F`.

```json
{"kind":"read-owner-conditions","plugin":"BMTConditionControl.esp","formId":"000800","target":"player","otherPlugin":"FalloutNV.esm","otherFormId":"104C0F"}
```

The terminal is `condition-list-exit`. Preserve its `nativeResult` and `partial`/`unavailable` status. PC037 observed seven raw OR/frame samples in the fixed pilot; `conditionTrace` remains false. Positive-first OR skipping was later observed in PC042. CLI40 passed 22 managed pilot cases. Native33 passed 121 condition controls plus regressions. PC037 passed both list outcomes and independent reload/readback. [Scoped acceptance](../artifacts/prototype-runtime/pc/session-037-plan/root-scoped-acceptance-001.json). PC034 passed the fixed Doc/player pilot (true/false) in retained replay. Reload passed; final read verification was Unperformed after an output-name collision. [PC034 scoped acceptance](../artifacts/prototype-runtime/pc/session-034-plan/root-scoped-acceptance-001.json). [Native33](../artifacts/prototype-runtime/pc/native-generation-033/validation.json).

## Evidence and validation

The source basis is pinned xNVSE commit `0ccd23ad885ddae533c1790a3fc56cd073e38de3`, especially `PluginAPI.h`, `FunctionScripts.cpp`, `CommandTable.cpp`, `Commands_Inventory.cpp/.h`, `GameForms.h`, `GameAPI.cpp` and `Commands_MiscRef.h`. The repository’s FNV command table supplies vanilla operation signatures. [PC011 calibration memory](../artifacts/prototype-runtime/pc/session-011/calibration-memory.json) preserves actual routine windows, setting names and field observations. New raw inputs are read-only and require the verified PC executable profile.

Native behavioral fixtures extend `RuntimeCommandHookTests.cpp`: bounded payload cases, resolved owner/target argument forwarding, combat before/after values, signed position readback, stale-generation rejection, unavailable values, empty/limited inventory status and game-thread dispatch boundaries. They simulate SDK calls and never launch the game. [Generation014 validation](../artifacts/prototype-runtime/pc/native-generation-014/validation.json) pins the unchanged sources, separate fresh build outputs and successful fixture result. Plugin build: 7.333s; fixture build: 5.897s; fixture run: 0.798s.

Live checks remaining: controlled combat with independent Health delta, broader actor calibration and full CTDA observations. Each session uses copied saves and verifies originals after normal exit.

## SetPos dispatch diagnostic

The `referenceCommandTrace` capability exposes request-scoped `reference-command` events around the SDK-published SetPos execute handler. It forwards the original argument stream once. Each event retains handler address/prefix, caller/script, offsets, reference/base identity and raw before/after transforms; stored rotations use radians. `argumentEcho` independently observes the UDF numeric parameter. Player position changes and restoration passed in PC017; PC018 extended the mutation controls.

**PC039 membership:** CLI40 / Native33 observed retail Doc Mitchell INFO order 00105598 → 00105599 and the interior CELL 00103DF9 null LAND pointer. Later-frame reads and independent reload/readback agreed. Four captures retained 7,269 events without errors or loss; normal closure and originals passed. Response selection and positive exterior LAND remain unperformed. [PC039](../artifacts/prototype-runtime/pc/session-039-summary.md).

**PC036 combat preparation:** Camera/view stopped with 520.729 seconds remaining versus 720 required. Setup and combat did not run. Load, baseline, inverse, reload, independent verification and normal closure passed; originals were unchanged. PC040 later reached combat; its failure and restoration are recorded above. [PC036 result](../artifacts/prototype-runtime/pc/session-036-plan/control-result.json) · [PC040 plan](../artifacts/prototype-runtime/pc/session-040-plan/README.md).

**PC040:** Matched hit callbacks and participant-bound records were captured, but an incorrect prior-record predicate timed out after 4.505 seconds. [Predicate review](../artifacts/prototype-runtime/pc/session-040-combat-review-001/review.json). Immediate cleanup still observed both actors in combat; disabling the fixture succeeded. Seven other phases passed, including explicit restoration, independent reload and all 25 final checks (48 raw player selectors included). Normal close and original-file integrity passed. The combat interval, hit classification and damage calibration remain unaccepted. [PC040](../artifacts/prototype-runtime/pc/session-040-summary.md).

**Current QUST-v2: Observed.** PC042 (CLI41 / Native34) passed three contexts, positive-first OR skipping and negative termination, followed by independent reload and normal closure. CLI42 mapped nine observed row instances to three physical CTDA rows in one winning QUST. Full condition trace: Partial. [PC042](../artifacts/prototype-runtime/pc/session-042-summary.json) | [Source mapping](../artifacts/prototype-runtime/pc/quest-condition-source-map-candidate-001/live-pc042-001/validation.json).

## Bounded combat cleanup

`start-combat-leased` adds an explicit nonplayer attacker, distinct target and `timeoutMilliseconds` of 1–5,000. It requires the `combatCleanupLease` capability and an active capture. Native cleanup stops both actors and disables the attacker, with separate identity checks and readbacks. Deadlines, stop, cancellation and disconnect request cleanup on the game thread. A stalled game retains an unavailable cleanup state.

The trace binds admission and cleanup through the request, session, lease, capture/connection generations, load epoch and actor identities. Import rejects missing or mismatched cleanup.

CLI35 Development passed **220 focused cases**, including 34 cleanup and seven capture-lifecycle cases; build/source/output verification passed. Native27 built with zero warnings/errors and passed **67 combat, 11 dispatch and existing regression cases**. The retained PC018 replay reproduced the prior import result exactly (7,917 events). PC022 movement/stage validation passed; live combat remains pending. [Managed checks](../artifacts/prototype-feedback/combat-cleanup-managed-001/attempt-002/validation.json) · [CLI35 verification](../artifacts/prototype-feedback/gui-reconciliation-001/portable-development-combat-verification-004.json) · [Native27](../artifacts/prototype-runtime/pc/native-generation-027/validation.json) · [PC018 replay](../artifacts/prototype-feedback/combat-cleanup-managed-001/legacy-pc018-replay-001/validation.json).
