# Native runtime bridge

For persistent console control, live `.gek` replacement, timed background keyboard/mouse input, and save reset, see the [live-control workflow](../../docs/runtime_live_control.md).

Capture status: **Partial**. Isolated PC controls have verified quest variables/stages, position/rotation, inventory/equipment, objective flags, message choice, reference enable state and player Health overrides, with restoration. [PC018](../../artifacts/prototype-runtime/pc/session-018-summary.md) and [PC022](../../artifacts/prototype-runtime/pc/session-022-summary.md) retain the results. [PC023](../../artifacts/prototype-runtime/pc/session-023-summary.md) verified owner-bound GetDead/IsEssential for the player and fixture NPC; its effects check exposed an unsupported NPC getter. Full CTDA results, broader actor calibration and attributed combat remain pending. See [remaining dependencies](../../docs/runtime_remaining_dependencies_2026_09_30.md).

The Win32 xNVSE plugin targets FalloutNV.exe 1.4.0.525 (non-NG), xNVSE major 6 or later, Console interface 2+, and Messaging interface 4+. Numeric observations additionally require the installed xNVSE DLL file version 0.6.4.9 or newer because the Script interface has no size/version field. The SDK source and hashes are pinned under reference/sources.json.

Native39 adds verified creature Health-override and effects readers. The build, 19 new controls and retained fixtures passed; [PC051](../../artifacts/prototype-runtime/pc/session-051-summary.json) verified creature Health 137 with restoration and normal closure. [Fixed creature Health Reconstruction 137 matches PC051](../../artifacts/prototype-feedback/actor-calculation/creature-dispatch-review-001/live-pc051-cli/attempt-001/validation.json). Scaled Health and inheritance: Pending. [Validation](../../artifacts/prototype-runtime/pc/native-generation-039/validation.json). PC050 observed auto-calc NPC Health95 with restoration and normal closure. [Control](../../artifacts/prototype-runtime/pc/session-050-plan/control-result.json).

## Engine behavior

A secured local named pipe, BMT.Runtime.<pid>, admits the current Windows user and rejects remote clients. Its I/O thread queues bounded actions; one action executes per MainGameLoop callback. Public SDK commands handle ordinary observations. The explicit quest-storage reader and Script::Execute wrapper additionally require the exact verified executable hash and pinned runtime layouts; the hook also checks the measured live prologue.

- Console actions record the SDK boolean as acceptance only.
- Typed quest-local/global/current/base/permanent actor-value reads compile numeric expressions and capture their returned values, including zero.
- Explicit quest actions take a plugin filename and local FormID, resolve the engine plugin slot and form, and cross-check the pinned form map against the SDK-resolved player pointer. Bounded metadata/local-storage reads distinguish missing variables from zero and reject reference variables. SDK GetVariable/SetVariable functions take the actual quest reference; numeric results must agree with resident storage. A write records its before value, requested value and observed readback.
- The optional Script::Execute wrapper records nested entry/return calls, actual script/reference/event-list identity, and the original boolean return. Entering the function does not prove that a particular script block or opcode executed. It preserves existing xNVSE ScriptRunner patches and declines installation on a different executable or prologue.
- Script tracing defaults to request-owned temporary scripts (zero ID or the engine temporary flag). A scenario can set `scriptTrace` to `{"mode":"selected","quests":[{"plugin":"FalloutNV.esm","formId":"000E61A4"}]}`; `scripts` selects SCPT records the same way. The engine resolves the actual owner script and reports all selections in capture-start.scriptTraceScope. Modes `off` and `all` are also explicit. Session004's unrestricted ten-second load capture dropped170,163events; full traces remain opt-in and must be checked for drops. Skipped/delayed Execute calls are not block execution evidence.
- Actor snapshots carry statistic, targetKind, component, and actual owner FormID/type when the SDK returns a form. Base FormID is added only if the installed command table has GetBaseObject and evaluation returns a form.
- Actors identified by numeric snapshots are watched for the SDK native onhit/onhitwith/ondeath events. Callbacks record participant FormIDs and filter to those watched actors; they do not infer damage amounts. The pinned EventManager.cpp establishes the callback argument layout. Registration success is exposed as actorHitEvents/actorDeathEvents capabilities.
- Reversible SDK command-table wrappers observe ShowMessage/MessageBoxEx calls and GetButtonPressed results without evaluating arguments a second time. A message call is not proof that the UI appeared. The most-recent-matching-owner association emits `candidateMessageInstance` and `attributionConfirmed:false`: queued dialogs can overwrite the shared script globals, as session005 demonstrated. It does not identify the chosen visible message; no MESG FormID is invented.
- Selected SDK condition-function handlers report actual returned numbers, reference identity, raw parameters and caller address/RVA. They do not recover the enclosing CTDA comparison, run-on rule or record owner. Opcode traces, full CTDA results, UI-only choices, temporary/damage actor-value components and measured combat attribution remain unavailable.
- The controlled message probe supplies its own engine message callback and records that callback's exact instance/button. It requires the pinned executable hash and checked live routine bytes. A cancelled or disconnected pending dialog retains its callback slot until dismissal; its late callback cannot become a later capture's choice. The older same-owner function mechanism is known to misattribute queued dialogs. The controlled condition probe invokes the published zero-parameter GetDead evaluator on the actual player reference; it does not establish a full CTDA comparison.
- NVSE lifecycle and runtime-script-error messages are observations.
- Legacy capture retains up to 128 compiled expressions. Live control separately owns and retires its temporary scripts using the pinned engine destructor and xNVSE cache cleanup.

## Protocol and trace

Protocol v1 has a 24-byte little-endian header: BMT1 magic, u16 version, u16 kind, u64 request ID, u32 payload byte length, zero u32 reserved. Payloads are at most 65536 UTF-8 bytes. Requests: hello=1, start=2, execute=3, stop=4, cancel=5, ping=6, evaluate=7, message-probe=8, condition-probe=9, quest-read=10, quest-set=11, actor-state=12, message-menu=13, actor-set=14, reference-state=15, reference-action=16, inventory=17, quest-state=18, record-membership=19. Event=256 carries JSON. Backends expose capabilities and reject unsupported requests. Quest payloads are tab-separated plugin, six-digit local FormID, variable name and (for writes) finite numeric value. The controller waits for terminal results; an intermediate script/condition observation does not complete an action.

Each event has protocol, kind, sequence, requestId, frame, qpc and cumulative dropped count. The hello supplies qpcFrequency and capabilities. The queue holds 128 actions and 4096 events; overflow is explicit. Cancellation drops queued actions and runs at the next game-loop boundary. It cannot undo an action already executing.

`runtimeNotificationAggregation` advertises the `lifecycle-raw-scene-counts-v1` policy. SDK notifications 24 and 26–32 are counted per capture. Start/end records retain every count, first/last frame and QPC, capture identity and saturation state. Other notifications retain their existing rows. Queue drops and sequence gaps still fail capture validation. Per-reference notification payloads are unavailable under this policy.

[PC049](../../artifacts/prototype-runtime/pc/session-049-summary.json) verified the policy across five captures: 18,311 aggregated notifications, 3,617 native events and zero event loss. The exterior capture resolved CELL `000DAEBB` to LAND `000DB00E`; independent reload restored the player baseline.

Core/RuntimeSession supplies the CLI transport, capture and import services. NDJSON traces include a header, engine events, action requests and terminal footer. The importer hashes exact input bytes, checks order/gaps/drop counts and footer agreement, and distinguishes completed, cancelled, disconnected and timeout results.

## CLI

- runtime connect --pid PID
- runtime capabilities --pid PID
- runtime capture --pid PID --seconds 10 --output new-trace.ndjson
- runtime capture --pid PID --scenario read-only.json --output new-trace.ndjson
- runtime prepare --config Fallout.ini FalloutPrefs.ini --saves SAVE_DIRECTORY --output NEW_PROFILE_DIRECTORY
- runtime run --pid PID --scenario scenario.json --profile PROFILE/runtime-profile.json --output new-trace.ndjson
- runtime import new-trace.ndjson

Scenario JSON contains actions (kind, target, name, value/command) and observeMilliseconds, the capture deadline. Examples of kinds: read-quest-variable, read-global, read-actor-value, read-base-actor-value, read-permanent-actor-value, read-actor-state, set-quest-variable, set-global, damage-actor-value, console, message-probe, read-condition-probe. Explicit quest actions use `plugin` and `formId` instead of `target`, for example `{"kind":"read-quest-variable","plugin":"FalloutNV.esm","formId":"000E61A4","name":"DishHutAttack"}`. The two controlled probes use fixed payloads. Console requests are ASCII; unsupported encoding is rejected. Identifiers are validated names, never arbitrary expressions. Legacy EditorID-based quest requests remain readable for compatibility but can fail in retail engines that omit those names.

The shared managed isolation service copies the complete game, Documents and LocalAppData inputs, hashes them and verifies actual path redirection using a pinned usvfs release. Mutating runs require a verified-usvfs profile tied to the current process start, executable/bridge hashes and live controller membership. The controller remains active until game exit and checks the original inventories and hashes. See docs/runtime_isolation_2026_09_30.md for prepare-isolated, verify-isolation and launch. The older prepare command produces copies only and cannot authorize a mutating run.

## Build and remaining validation

Build NvseRuntimeBridge.vcxproj, Release|Win32, with the installed v145 toolset. It links only Windows system libraries. Do not install into an active game without the coordinating session's chosen isolated launch.

Engine results and remaining acceptance criteria are tracked in [remaining dependencies](../../docs/runtime_remaining_dependencies_2026_09_30.md). Native fixture results and engine observations are recorded separately.

## Typed actor state

`{"kind":"read-actor-state","target":"player"}` requests engine Level and current/base/permanent Health, Endurance, Strength, Agility, Luck, Guns and CritChance through public SDK functions with an explicit actor reference argument. A plugin/local reference can replace `target`: `{"kind":"read-actor-state","plugin":"FalloutNV.esm","formId":"00000014"}`. Runtime indices are resolved from the actual engine namespace; an NPC base record is not silently used as an actor reference.

Each successful number uses the existing typed `snapshot` schema and actual reference/base IDs. The terminal `actor-state` record contains every field with an observed/unavailable status, bounded effect and modifier records, and raw bytes. Lists stop after64modifier nodes or32effect nodes and mark cycles, unreadable memory or limits as partial. Effects and hit getters additionally match the live prefixes measured in session007. Session007's damage-modifier candidate failed a memory bound; this is retained as unavailable, never an empty list or zero.

Watched OnHit/OnHitWith events also preserve a bounded `actor-hit-state` record when the verified process getter is available. Participant agreement and changes since a prior observation are separate fields; `attributionConfirmed` remains false. A retained last-hit record can be stale. Independent controlled hits and health deltas are required before reporting measured weapon damage; `weaponBaseDamage` is the SDK's intermediate field, whose comment includes skill and weapon-condition adjustments. No health/critical/damage formula is inferred from these records.

Focused native checks cover zero, malformed/cyclic/oversized lists, missing fields and target identity. Managed lifecycle coverage keeps intermediate numeric snapshots separate from the terminal actor-state record. PC023 observed the new safety fields on both actors; NPC effects and live combat acceptance remain pending.

## Capture-time plugin namespace

A verified isolated run supplies plugin filenames as candidate query inputs in the optional newline-delimited Start payload after the session ID. The bridge queries engine GetModIndex and GetNumLoadedMods on the game thread after an observed successful game load/new game. A complete result requires every active engine slot exactly once; unrecognized candidates do not become loaded plugins. The controller checks each resulting copied file against its preparation hash and places the ordered verified list in capture-start.identity.activePlugins. Missing, partial, pre-load and changed-file results stay unbound. Session003 confirmed ten actual engine slots and their prepared-file hashes; the original save listed only the base plugin, demonstrating why a save or directory inventory cannot substitute for capture-time engine queries.

## Dialogue and terrain membership

Read-only actions `read-dialogue-state` and `read-cell-terrain` require `plugin` and local `formId`. Request 19 contains the action name, plugin and six-digit local ID separated by tabs; its terminal event is `record-membership`. These PC readers use the admitted executable and pinned SDK layouts. Engine control validation is pending.

Dialogue output preserves quest groups, ordered INFO slots, holes, duplicates, record flags, actual addresses and competing pointer paths. Selection requires one structurally validated path. Reads stop at 16 groups or 256 total slots; cycles, invalid identities, count disagreements and changing list/header values produce Partial results.

Terrain output preserves CELL load state, raw fields and the actual `CELL+0x4C` LAND link. A null LAND pointer in an unloaded cell is Unavailable; a null pointer in a loaded or attached cell is Empty. Height values require a separate reader. Control runs must repeat queries across frames and compare the recorded identities/order against each explicitly loaded plugin variant.
