# PC command capture

Status: native fixtures passed; live SetPos dispatch observed; movement unresolved.

Current checkpoint: [Native25 validation](../artifacts/prototype-runtime/pc/native-generation-025/validation.json). Plugin/test builds passed with zero warnings or errors; the fixture ran in 0.375 seconds. The SetPos observer now preserves incoming and returned LastError and x87/SSE state. Eight floating-point controls passed, including active and inactive SetPos observation.

SetPos events retain the forwarded `handlerAddress`/`handlerPrefixHex` and add `publishedHandlerAddress`, `publishedHandlerPrefixHex` and `handlerRoute`. These identify the published handler behind BMT's general wrapper. Direct and wrapped dispatch, single forwarding and teardown were checked.

[PC014](../artifacts/prototype-runtime/pc/session-014-summary.md) observed one actual SetPos call through that wrapper to published handler `0x005C05F0`, with the correct chair/base identity and requested argument echo. The handler returned true; raw coordinates and later readbacks remained unchanged. Independent cleanup matched the baseline. Nine traces imported complete with 11,601 native events, 196 snapshots and no event loss. The ten-minute watchdog closed the game normally; originals and frozen binaries remained unchanged. The independent player control was not run.

Retained PC code identifies a successful no-op path for furniture (type `39` / `0x27`): after an earlier allow gate, eligibility returns false and SetPos returns true without reaching either movement call. The saved PC014 handler prefix matches that image. PC014's helper return and taken branch remain unobserved. [Offline control-flow review](../artifacts/prototype-runtime/pc/session-014-plan/setpos-retained-project-001/offline-analysis-001/analysis.json); [callee evidence and diagnostic scope](../artifacts/prototype-runtime/pc/session-014-plan/setpos-diagnostic-docs-001/DIAGNOSTIC.md).

The earlier [Native24 validation](../artifacts/prototype-runtime/pc/native-generation-024-retry002/validation.json) and [synthetic event bundle](../artifacts/prototype-runtime/pc/native-generation-024-retry002/command-fixture.json) retain capture boundaries, coverage and a fixture Script entry → command → return sequence.

New focused cases cover six general capture transitions, six Eval transitions, four Eval scope cases, ten return representations and six FP controls, plus table selection, escaped chunk bounds and later-hook preservation. Earlier Native22 cases remain included. The first two Native24 fixture failures are preserved: their expected x87 template omitted the CPU-normalized ES/B status bits. The final fixture compares state sampled inside the original handler.

The bridge enumerates the published xNVSE script command table after registration, using SDK `Start()`, exclusive `End()` and `GetByOpcode()`. Each eligible named execute handler receives a typed wrapper; the three existing message wrappers retain their event kinds. General capture follows the existing requested/selected/all/off script scope.

`command-execute` preserves the original handler return, raw result bits, SDK return type, Script/reference/caller context and Native22 before/after command-location evidence. `resultBits` is the 64-bit pattern in 16 high-digit-first hexadecimal characters: double `1.0` is `3ff0000000000000`. `numericValue` is emitted only for finite SDK Default-type results. Form, string and array returns retain raw bits.

`threadId` and `observedScriptCallId` come from the admitted thread and same-capture Script::Execute scope. `scriptCallStatus` is `observed-scope` or `Unavailable`; `resultStatus` and `returnTypeStatus` use lowercase `observed`/`unavailable`. Shared import additionally checks request, thread, Script identity/address and entry/exit containment. Raw command offsets remain uncalibrated.

The handler receives its original arguments exactly once. Argument extraction is never repeated. The disabled/unselected path forwards without observation; active paths preserve incoming and returned `LastError` and complete x87/SSE state. Observation uses masked exceptions, nearest rounding and an empty x87 stack. Execute and the eight existing condition-function wrappers retain their admitted request/capture generation. Stop, disconnect and restart suppress stale return events. Condition-function values retain `comparisonObserved:false` and `conditionOwnerRecovered:false`.

## Coverage

`commandExecuteTrace` advertises installed general wrappers. `commandTraceCoverage` reports an installation snapshot: active table addresses/count, installed and special handlers, skipped reason counts, and limits. `command-hook-coverage` events carry the full per-entry inventory in bounded chunks at capture start. Padding, missing/nonexecutable handlers, duplicate opcodes, mismatched SDK lookup, changing entries and installation failures remain explicit.

The table scan admits up to 65,536 entries and the typed wrapper pool supports 4,096 eligible named commands. Capacity exhaustion is partial coverage. This SDK interface supplies the script command table; console-only commands and VM structural opcodes are outside that table. Later third-party table replacements remain outside the installation snapshot.

## Source evidence

Pinned xNVSE commit: `0ccd23ad885ddae533c1790a3fc56cd073e38de3`, retained in [sources.json](../tools/NvseRuntimeBridge/reference/sources.json).

- `PluginAPI.h:527`: `Start`, `End`, `GetByOpcode`, `GetByName`, `GetReturnType` function pointers.
- `CommandTable.h:125`: eight-argument execute and four-argument eval ABI; `CommandInfo` size `0x28`, execute offset `0x18`, eval offset `0x20`.
- `CommandTable.h:97`: Default=0, Form=1, String=2, Array=3, ArrayIndex=4, Ambiguous=5.
- `CommandTable.cpp:438`: exclusive-end seed read. Retail addresses `0x01190910`–`0x01196D10` contain 640 seed entries; padding and NVSE/plugin registration follow. Coverage uses the actual published runtime table count.
- `CommandTable.cpp:2316`: SDK accessors return the published `g_scriptCommands` table.

Live checks: published-table identity/count and skipped reasons; selected ordinary and expression-backed commands; caller/result/source-map readback; general/Eval lifecycle transitions. PC013 and all previous native generations remain pinned separately.

The separate [CTDA model fixture](../artifacts/prototype-runtime/pc/ctda-prerequisites-003-native-validation/validation.json) passed 40 cases in 0.233 seconds. It has no bridge inclusion, hooks or engine calls.
