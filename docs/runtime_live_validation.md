# PC live-control validation

**2026-10-02.** Persistent control, background gameplay input, save reset, live script replacement, and a retail article interaction are verified. Message-menu selection requires game focus. Shutdown crashes remain reproducible with a minimal SDK-only plugin.

**Follow-up scope:** minimal automated gameplay captures only. Dead Money and Honest Hearts gameplay validation is reserved for the user's manual review; their static findings remain available for those checks.

The demonstrations reused one installation and private profile. Run 011 used the user's verified 4GB retail executable and bridge `68f60876ba2dcccbfb5a31b1ee105d2a2f287adb454cd43028b9b38dbe54a631`; run 012 added owned-setting restoration in bridge `426dced6c747702db4bfd9378e86658bcf883d64a5a956e0ed9b050f0a81df13`. [Run 011 identity](../artifacts/runtime-live/identity-011.json), [run 012 identity](../artifacts/runtime-live/identity-012.json), and their [command log](../artifacts/runtime-live/demo-011.ndjson) / [command log](../artifacts/runtime-live/demo-012.ndjson) retain the inputs and results.

## Measured gates

Times are client round trips unless marked **engine**.

| Gate | Observed result | Evidence |
| --- | --- | --- |
| Persistent console and readback | Position change: **17.11 ms**. Evaluation confirmed it: **18.14 ms**. | [Control](../artifacts/runtime-live/control-011.json) |
| Background movement and turning | With VS Code focused, the 2,600 ms sequence completed in **2,656 ms engine / 2,670.30 ms client**. Movement: **175.140 game units**; heading change: **10.084 degrees**. | [Input](../artifacts/runtime-live/input-011.json) |
| Input Stop | A 30-second hold stopped in **17.54 ms**; input became inactive. | [Input](../artifacts/runtime-live/input-011.json) |
| Typing isolation | Two physical W holds typed `ww` in VS Code while the game advanced frames. Player pose stayed unchanged. | [Typing](../artifacts/runtime-live/typing-006.json) |
| Named baseline and repeated reset | Created a **1,767,518-byte .fos / 270-byte .nvse** pair in **296.99 ms**. Repeated resets restored cell/pose in **349-402 ms**, without restarting. | [Control](../artifacts/runtime-live/control-011.json), [F5](../artifacts/runtime-live/quicksave-011.json), [article reset](../artifacts/runtime-live/article-close-011.json) |
| F5 baseline protection | F5 wrote the private quicksave pair. Both named-baseline hashes remained unchanged; reset and the loaded script still worked. | [F5](../artifacts/runtime-live/quicksave-011.json) |
| Live replacement | **100 replacements** while repeating, with each revision observed. Loads: **10.95-22.70 ms**. Final value: **100**. | [Scripts](../artifacts/runtime-live/scripts-011.json) |
| Compile failure, retirement, Stop | Broken syntax returned a diagnostic and retained revision 100. Counts: **102 compiled, 101 retired, 1 owned**. Stop: **16.64 ms**; subsequent call counts stayed unchanged. | [Scripts](../artifacts/runtime-live/scripts-011.json) |
| Owned settings | `fMoveRunMult`: **4 -> 5 -> 6 -> reset -> 4**, retaining the first original value. Readback matched raw bytes `00008040`; ownership cleared and a second reset succeeded. Restoration plus reset: **721.13 ms**. | [Settings](../artifacts/runtime-live/settings-012.json) |
| Article interaction | Opened retail `HVRadioMsg` (`000AF687`) with Explosives 40; rendered three buttons. Foreground Enter closed it, and reset restored Explosives 15. | [Open](../artifacts/runtime-live/article-open-011.json), [image](../artifacts/runtime-live/hvradio-011.png), [close/reset](../artifacts/runtime-live/article-close-011.json) |

The article scenario verifies the retail message and reset workflow. The surrounding quest's repair branch remains untested. The [background menu probe](../artifacts/runtime-live/menu-input-011.json) delivered Enter through buffered input, but neither the wrapper nor `MenuTapKey` closed this menu. Foreground Enter worked. Background gameplay movement and turning passed separately.

Identity caching also passed: the first refresh hashed ten loaded plugins; the next reused all ten. [Initial refresh](../artifacts/runtime-live/identity-006.json), [cached refresh](../artifacts/runtime-live/identity-006-cached.json).

## Automated validation

- Managed focused tests: **168 passed, 0 failed**: [166 live-control cases](../artifacts/runtime-live-tests/runtime-live-identity-final.trx) and [two mapped-executable cases](../artifacts/runtime-live-tests/runtime-live-executable-identity.trx). The matching Development [build](../artifacts/runtime-live-identity-build.log) completed with zero errors.
- Native `RuntimeCommandHookTests.exe --live-only`: passed scheduler completion, failed revision retention, replacement retirement, setting restoration, reset cancellation, and priority Stop. [Result](../artifacts/runtime-live/build/live-tests-final.json).
- Native `RuntimeLiveInputTests.exe`: passed immediate/buffered input, background isolation, release, device-failure, and snapshot-limit boundary cases. [Result](../artifacts/runtime-live/build/input-tests-final.txt).
- Native builds: zero warnings/errors. [Final receipt](../artifacts/runtime-live/build/final-native-receipt.json), [bridge](../artifacts/runtime-live/build/native-plugin.log), [live tests](../artifacts/runtime-live/build/native-tests.log), [input tests](../artifacts/runtime-live/build/native-input-tests.log).

Final review corrected executable identity when the loader path differs from its mapped backing file, and capped retained sequence snapshots at 48 KiB. These changes passed focused tests. The staged [final generation](../artifacts/runtime-live/final-generation.json), bridge `b833ef42db10a60994ec737b3fa43330bb3916a71360e96773df2753ae67f319`, has not had an additional gameplay run; measured gameplay results above identify their tested generations.

## Crash logger findings

The user's Yvile's Crash Logger DLL and PDB were staged from MO2 into the existing private installation. No other MO2 mods were added. [File identities](../artifacts/runtime-live/crash-logger-files.json).

Runs 006 and 011 crashed during `QQQ` shutdown in xNVSE at RVA **0xE6789**. Matching symbols identify the exit-time destructor of the static `NVSECellLoadedData` string, at its reference-count decrement. [Crash log](../artifacts/runtime-live/run011-CrashLogger.log), [symbol evidence](../artifacts/runtime-live/shutdown-symbols-006.json).

**Run 013 reproduced the same module/RVA with the minimal SDK-only plugin.** It loaded the same save, waited **30 seconds / 673 post-load game loops**, then issued `QQQ`. This plugin has no live scripts, input wrapper, execution hooks, or IPC. [Control events](../artifacts/runtime-live/startup-control-settled/run-41072.ndjson), [crash log](../artifacts/runtime-live/run013-control-settled-CrashLogger.log), [exit result](../artifacts/runtime-live/run013-control-settled-exit.json). The shutdown fault reproduces independently of those BMT features; its underlying cause is unresolved.

The [comparison receipt](../artifacts/runtime-live/shutdown-control-comparison-013.json) retains the matching instruction, module, symbols, source, and control hashes. Other shutdown attempts failed in audio/task-manager code: [minimal run 008](../artifacts/runtime-live/run008-control-CrashLogger.log), [minimal run 010](../artifacts/runtime-live/run010-control-4gb-CrashLogger.log), and [full run 012](../artifacts/runtime-live/run012-CrashLogger.log). These were unsuccessful shutdowns.

Earlier renderer-initialization failures occurred before gameplay and yielded no exception log. Their cause remains unknown. The [user executable comparison](../artifacts/runtime-live/laa-only/user-executable-comparison.json) confirmed unchanged engine sections, with differences confined to PE headers and the 4GB Steam/xNVSE bootstrap. That exact executable hash is now supported; subsequent runs reached gameplay. The unsuccessful single-bit LAA experiment remains rejected.

## Save-isolation incident and correction

F5 testing initially overwrote personal `quicksave.fos`, `quicksave.nvse`, and `quicksave.fos.bak`. Existing real names absent from the private overlay bypassed its create target. Exact pre-run copies restored all three files. [Restoration receipt](../artifacts/runtime-live/save-isolation-incident/restoration.json).

The fix assigns a unique profile-specific `SLocalSavePath` and maps it explicitly to private saves. The [USVFS preflight](../artifacts/runtime-live/save-route-preflight-result.json) passed overwrite, backup, rename, original-autosave isolation, and baseline preservation checks. The [actual F5 rerun passed](../artifacts/runtime-live/quicksave-011.json). The [final integrity check](../artifacts/runtime-live/save-isolation-incident/source-integrity-final.json) confirms all **95** tracked personal Documents/LocalData files match their pre-run hashes.

## Delivery

The game, launcher, broker, and diagnostic processes are stopped. The production bridge has been restored. One reusable installation and the private save profile remain available; no new installation copies were created for these runs. The user's Steam executable and original sample executable retain their [original hashes](../artifacts/runtime-live/source-executable-integrity.json).

See [usage and examples](runtime_live_control.md) for the CLI workflow. Screenshots and detailed traces remain opt-in; default session logs are bounded at 64 MiB.
