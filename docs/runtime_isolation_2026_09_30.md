# PC runtime isolation

The runtime bridge executes requests on the game's main thread and captures returned values. Console acceptance and SDK hit participants remain separate from observed quest changes or measured damage.

Use one working installation per build with `--reuse-installation`. Each run gets private configuration, its selected save and optional NVSE sidecar, and evidence for its binaries and fixtures. The controller reserves the installation for one run at a time and verifies its original assets in place. Source samples stay separate. Inputs receive SHA256 checks; source inventories include file and directory changes.

Critical binary and configuration snapshots are shared by SHA256 in the installation's sibling `<installation>.bmt-evidence` directory. Run manifests link to those versions. Repeated versions reuse verified bytes; changing the bridge retains a new version. Existing run evidence remains valid.

## Verified dependency check

The official usvfs release is pinned to `v0.5.7.2`, source commit `a50d84c64c9244f80dc67e9fe7af209bfe514d5b`. Its archive SHA256 is `c6252eed78ee1c307733a4412cb68522cffc48107be4795c4e38b2b8d7c76d01`. The four runtime DLL/proxy hashes are enforced before loading. Source and download receipts are in `tools/NvseRuntimeBridge/isolation-reference/v0.5.7.2/sources.json`.

The Win32 scratch preflight passed: redirected existing/new file writes, rename/delete, synthetic save creation, INI writes, and inherited child-process hooks left the physical originals unchanged. Receipt: `artifacts/prototype-feedback/runtime-isolation-preflight.stdout.json`; stderr was empty. This test launched only BMT's probe executable.

## Activation contract

Before starting the game, the controller maps the working installation and private user-data roots to their original logical paths and repeats the probe through those exact mappings. It then checks the original inventories again. The working xNVSE loader starts through `usvfsCreateProcessHooked`; the controller remains responsible for its VFS instance until the game exits.

Activation records the observed game PID, process start time, executable-file hash, loaded bridge module/hash, and VFS process membership. A prepared copy does not authorize mutating scenarios. `runtime run` requires a current matching controller observation. Captures keep identity/provenance separate from outcomes.

## Commands and pinned PC package

The shared service is exposed through `runtime prepare-isolated`, `runtime verify-isolation`, and `runtime launch`. The launch command remains the controller until the game exits. Cancellation requests a normal game-window close and retains the controller while the game is still running. `verify-isolation` runs only the BMT probe and cleans its temporary folders. Use `runtime release-installation` to release an unlaunched preparation; a verified closed run permits the next preparation automatically.

The local official xNVSE `6.4.9` package is at `artifacts/prototype-runtime/pc/nvse-6.4.9/package`. Its archive, `artifacts/prototype-runtime/pc/nvse-6.4.9/nvse_6_4_9.7z`, has SHA256 `81fb0638f87b2c822270e37cf2ca118d39e2425f13028f9173b172c5fdb2eb88`. Pass both `--nvse-directory` and `--nvse-archive` when preparing the session so its manifest records the package and copied binary hashes. Reuse refreshes the package NVSE INI; copied installations keep an existing INI.

Preparation arguments, from the repository root:

```text
runtime prepare-isolated --game <verified PC retail game directory>
  --reuse-installation artifacts/prototype-runtime/pc/reusable-retail/game
  --output artifacts/prototype-runtime/pc/run-001
  --save-file artifacts/prototype-runtime/pc/session-016/documents/Saves/BMT_PC016_MenuFree.fos
  --documents "C:\Users\mmc99\Documents\My Games\FalloutNV"
  --local-data "C:\Users\mmc99\AppData\Local\FalloutNV"
  --usvfs tools/NvseRuntimeBridge/isolation-reference/v0.5.7.2/portable/bin
  --probe tools/NvseRuntimeBridge/bin/Release/RuntimeIsolationPreflight.exe
  --bridge artifacts/prototype-runtime/pc/native-generation-038/bin/NvseRuntimeBridge.dll
  --nvse-directory artifacts/prototype-runtime/pc/nvse-6.4.9/package
  --nvse-archive artifacts/prototype-runtime/pc/nvse-6.4.9/nvse_6_4_9.7z
runtime verify-isolation --profile artifacts/prototype-runtime/pc/run-001/runtime-profile.json
runtime launch --profile artifacts/prototype-runtime/pc/run-001/runtime-profile.json
```

For a preparation that will not be launched:

```text
runtime release-installation --profile artifacts/prototype-runtime/pc/run-001/runtime-profile.json
```

The [storage cleanup](../artifacts/prototype-feedback/storage-cleanup-001/README.md) retained the reusable retail installation and retired duplicate media, save copies and obsolete generated packs.

CLI49 passed 54 focused cases. [Two sequential preparations](../artifacts/prototype-runtime/pc/reusable-installation-validation-001/attempt-001/validation.json) shared the retail installation and passed the isolated path probe, using 2,344,263 and 2,253,211 bytes per run. Both reservations were released; temporary probe folders were removed. Source hashes and shared asset identities remained unchanged. This check did not launch the game; launch snapshots are covered by the focused tests.

[PC046](../artifacts/prototype-runtime/pc/session-046-plan/control-result.json) then launched from that installation, loaded the selected save, captured observations, reloaded and closed normally. [Originals unchanged](../artifacts/prototype-runtime/pc/session-046-plan/normal-close.json). Its SDK variable comparison failed to compile, and the reload validator rejected a different valid DLC order; PC047 corrected both. [Doc Mitchell's ordinary base Health](../artifacts/prototype-feedback/actor-calculation/live-046-doc-cli/attempt-001/validation.json) matched the independent calculation: 65.

[PC047](../artifacts/prototype-runtime/pc/session-047-summary.json) reused the installation with CLI50/Native37. Direct and SDK local reads agreed before and after independent reload; closure and original-file checks passed. Its run files and reports occupied 52.99 MiB, including binary evidence. The exterior capture lost 15 events during scene notifications; terrain reads remain unperformed.

[PC048](../artifacts/prototype-runtime/pc/session-048-summary.json) verified CLI51's shared binary-evidence cache: 22 blobs totaling 33,310,830 bytes, with roughly 3.9 MB of run files. All 63 focused reuse/cache checks passed. The late startup handoff prevented gameplay checks; the game closed normally and originals were unchanged.

[PC049](../artifacts/prototype-runtime/pc/session-049-summary.json) reused all 22 blobs unchanged and added zero cache bytes. Its five phases verified exterior CELL/LAND identity, restored the player baseline and closed normally with originals unchanged. Run files occupied 7.9 MB plus 3.5 MB of helper receipts.

The service and CLI compile, and focused protocol/isolation tests pass. Sessions003/005 verified numeric reads, a quest-variable 0→1→0 roundtrip, a controlled Health damage/restore delta, GetDead's evaluator return, capture-time plugin identity, and cancellation/disconnect behavior. Originals remained unchanged after normal exit. Message-choice checks passed in [PC010](../artifacts/prototype-runtime/pc/session-010-summary.md) and [PC018](../artifacts/prototype-runtime/pc/session-018-summary.md). [PC045](../artifacts/prototype-runtime/pc/combat-idle-target-review-001/scoped-validation.json) matched an attributed ordinary melee hit with the player's Health decrease; the full run failed validation. Broader CTDA coverage and final damage calculation remain pending. Early receipts: `artifacts/prototype-runtime/pc/session-005-summary.json`.

Use the reuse options above for new runs. The earlier `artifacts/prototype-runtime/pc/prepare-session.py` runner is retained with its historical receipts. Controlled baseline, message and condition scenarios are under the adjacent `scenarios` directory. Quest-change and health-delta scenarios are generated only after a valid owned engine baseline has been captured.

The first actual PC launch reached the game and loaded the bridge. The controller rejected admission before scenarios because its module check did not handle the plugin's virtual loader path. It then closed its copied game normally. An independent check verified all 565 original input hashes and three directory inventories unchanged. The module check now compares the mapped section's backing file with the verified copy using Windows file-handle identity paths, and records both logical and backing paths. Later sessions verified this correction; the rejected attempt remains preserved.

The second launch exposed a separate delayed-module race: `Process.Modules` cached a collection from before xNVSE loaded the bridge. The bounded admission loop now calls `Refresh()` before each inspection. Session-002 closed normally at the 60-second admission limit. Its independent post-exit check verified all 559 original game/user files plus the three original directory inventories unchanged (`session-002-original-game-user-files-after-exit.json`). Bootstrap tool files outside those roots were subsequently rebuilt intentionally for session-003.

Capture-time plugin identity uses SDK `GetModIndex` queries for the verified copy's candidate filenames, plus `GetNumLoadedMods`. Only a complete unique contiguous engine slot set can bind to the copies' current matching SHA256 hashes. Candidate directory membership alone cannot bind a trace. The bridge waits for an observed successful load/new-game lifecycle event; pre-load captures and older bridges remain unbound. The trace retains the raw engine query and a `capture-start.identity` snapshot with `activePluginIdentityStatus` and ordered `activePlugins`. Session003 observed ten active engine plugins even though the loaded save named only the base plugin, validating why save/directory lists cannot substitute for the actual namespace.

Session006 failed during controller monitoring with `Access to the path is denied.`, before runtime actions or UI inputs. A scratch probe reproduced that exact exception when a status reader omitted Windows delete sharing during atomic replacement. The old diagnostics lack a phase/stack, so this remains a reproduced matching mechanism rather than proof of that attempt's exact failing call. The corrected writer preserves the old complete JSON while retrying access/sharing contention for a bounded 800ms, supports cancellation, and cleans its unique temporary file. Persistent failures retain their failure behavior; launch diagnostics now include phase, exception type and HRESULT. Three parameterized real-file tests cover released, cancelled and persistent contention; all passed in the batch9 focused run (99/99 overall, `artifacts/prototype-feedback/focused-ninth.receipt.json`). Status readers should use `FileShare.ReadWrite | FileShare.Delete`.
