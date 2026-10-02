# Shared Explore adoption and acceptance

Verified on 2026-09-09 in the isolated `Multitool.Shared/.worktrees/Xbox360MemoryCarver` snapshot. This increment uses the exact `Slfx77.Multitool.Core` and `Slfx77.Multitool.WinUI` package version `0.1.0-preview.20260909.4`. The accepted Explore source increment was subsequently integrated into the original checkout with hash conflict checks and backups; private `Sample` corpus bytes remain external. New increments are validated in the isolated checkout before integration. Local fixture bytes and detailed logs remain under ignored `TestOutput`; this document records the durable acceptance scope.

## Behavior and preservation

Explore presents one source across persistent Data, Maps, and Assets views. Data retains the rich SingleFile record views and native map navigation; classic maps use the existing asset viewer. Opening a plugin, supported dump, or save uses its parent directory for assets. Folder selection considers immediate validated `.esm`, `.esp`, and `.esl` children, then an immediate `Data` folder when needed. A sole plugin opens automatically; multiple candidates require an explicit primary selection. Reload preserves a still-valid explicit primary. Archive sources expose their asset catalog; the planner does not extract and silently merge embedded plugins or establish a new load order. Existing native loose-file/archive precedence remains authoritative.

The shared browser session cancels retired work and retains sources until leases end. The application also avoids disposing a newly published replacement when an old source's cancellation callback fails. Explore changes leave File Carver, Game Repacker, DDX conversion, DMP-to-ESM conversion, and batch recovery inputs independent. Diagnostics and application theme/language settings remain separate; workflow options are shown beside the workflow that uses them. Localization infrastructure currently ships en-US resources.

Gallery decoding uses the shared bounded worker with newest pending requests first and a bounded positive/negative thumbnail cache. Retiring a source drains its decoder before releasing the filesystem. Dispatcher callbacks check source identity, cancellation, and tile realization. Fresh source identities prevent same-path reloads from showing previous bytes. BMT retains resource-registry statistics and memory-pressure trimming.

Actors exposes authored statistics and inventory inspection without requiring meshes. Template inventory follows the existing NPC resolver, and leveled entries show a bounded tree of eligible candidates for the selected level. The subsequent [generated-equipment increment](actor-generated-equipment.md) adds explicit repeatable seeds, bounded concrete inventory, visible unresolved branches, and the same generated result for selected native preview and GLB/PNG export. Its Windows actor controls and actual FNV scene/equipment/export checks pass; full actor animation remains open.

FO3/FNV dialogue responses can resolve voice candidates from the current source and play supported native audio. The LIP inspector exposes decoded metadata and selectable raw source frames. [LIP format evidence](formats/lip-fo3-fnv.md) records supported encoding and limits. This increment does not apply audio alignment, engine facial settings, or actor morph mapping. Optional Creation Kit XML/ZIP/folder sidecars preserve authored nodes, bounds, route extents and labels; missing record references and missing diagram endpoints remain explicit.

## Reproduction commands

Run from the isolated BMT repository. The shared build wrapper serializes compiler work using the machine-wide mutex. The path below is the actual package feed used for this acceptance run; set it to the local shared checkout when reproducing elsewhere.

```powershell
$sharedRoot = 'C:/Users/mmc99/source/repos/Multitool.Shared'
$feed = '-p:RestoreAdditionalProjectSources=' + $sharedRoot + '/TestOutput/packages'
& "$sharedRoot/tools/scripts/build.ps1" -Project tests/BethesdaMultitool.Tests/BethesdaMultitool.Tests.csproj -Framework net10.0 -BuildArgs @('-p:BuildTestsOnly=true','-p:SkipAnalyzers=true',$feed)
& "$sharedRoot/tools/scripts/build.ps1" -Project src/BethesdaMultitool/BethesdaMultitool.csproj -BuildArgs @('-p:SkipAnalyzers=true','-p:GenerateShaderBytecodePackOnBuild=false',$feed)
```

The focused test executable was `tests/BethesdaMultitool.Tests/bin/Release/net10.0/BethesdaMultitool.Tests.exe`. The 103-test run supplied a repeated `--filter-class` argument for these classes:

```text
BethesdaMultitool.Tests.Core.Actors.ActorInspectorTests
BethesdaMultitool.Tests.Core.AssetBrowse.ExploreSourcePlannerTests
BethesdaMultitool.Tests.Core.AssetBrowse.BethesdaBrowseSourceTests
BethesdaMultitool.Tests.Core.Ui.ExplorePanePolicyTests
BethesdaMultitool.Tests.Core.Ui.AnalysisSubTabPolicyTests
BethesdaMultitool.Tests.Core.AssetBrowse.AssetBrowseSessionTests
BethesdaMultitool.Tests.Core.AssetBrowse.AssetExportServiceTests
BethesdaMultitool.Tests.Core.Formats.Dialogue.DialogueViewReaderTests
BethesdaMultitool.Tests.Core.AssetBrowse.SpriteDecodeBytesTests
BethesdaMultitool.Tests.Core.AssetBrowse.ThumbnailCacheTests
BethesdaMultitool.Tests.Core.AssetBrowse.DialogueAudioIndexTests
```

`DialoguePluginIdentityTests` separately passed four header-origin tests. The accessibility ratchet passed its two applicable checks and skipped its explicit generator fixture. The shared Core suite passed 32 tests, skipped one Windows symlink-privilege test, and failed none; its nine new worker/cache tests exercise bounds, cancellation, draining, callback failures, negative caching, and concurrent trimming.

The local GUI helpers were `TestOutput/actor-explore-smoke/test-thumbnail-ui.ps1` and `test-dialogue-ui.ps1`. They launch an owned application process, use scoped UI Automation, and close it in `finally`. Binary DDS fixtures are generated with Python. Actor CLI acceptance used `esm actor-details <synthetic-plugin> 0x1 --level 5`; the GUI used `--file <synthetic-plugin> --view actors --actor 0x1`. Dialogue used `--file <fixture-folder> --view dialogue`, with a synthetic three-record plugin and one privately supplied FNV voice/LIP pair. Private corpus bytes are not distributed with these tests.

## Executed results and limits

| Check | Observed result |
| --- | --- |
| Combined Windows build | Zero warnings/errors, 2m55.19s; no MSB3026 copy retries. Application analyzers and shader regeneration were explicitly disabled. |
| First-open actor view | Authored health 50 and SPECIAL 1–7 appeared; level-5 eligible inventory count was 6; CLI stdout parsed as JSON. |
| Workspace and recovery | Data/Maps/Assets navigation and visible options passed; a staged Recovery path survived Explore navigation and reload. No recovery execution was claimed for that nonexistent staged path. |
| Same-path thumbnail reload | BMT changed from 96×96 to 96×48 after fixture replacement; deleting the generated fixture and reloading cleared the gallery. The AWT adapter passed the equivalent 4×4 to 8×4 and empty-source checks. |
| Dialogue transport | Response `0014DFB7/1` resolved; repeated Find remained playable; native play/pause/seek showed `00:01 / 00:02`. Source replacement removed the transport and deleted that player's temporary audio cache. |
| LIP inspection | 124 frames, 33 tracks, 30 Hz, starting frame -5, 4,412 encoded bytes and declared size 16,392. Selecting frame 30 displayed 1.000000 seconds and 33 track rows. |
| Sidecar geometry | Two nodes and two links retained; missing endpoint count was one. UIA bounds confirmed node offset `(380,180)`, sizes `280×140` and `240×180`, and the lower route label after scrolling. FormID `00ABCDEF` reported unresolved. |

Final GUI processes 119376 and 23356 exited, and their logs contained no ERR/FTL entries. Their Windows obj/bin DLL copies shared SHA-256 `BF40FF9F3BB14F65C0357D66C72C9C6D13C1B77C19AD0DC0D7ECFB5B59D33FE3`. The key local logs are `dialogue-lip-windows-build.log`, `dialogue-lip-ui.log`, `sidecar-geometry-ui.log`, `thumbnail-tests.log`, `dialogue-plugin-tests.log`, and `accessibility-tests.log`, all under `TestOutput/actor-explore-smoke`.

These results cover the original Explore increment, not every corpus or GUI path. Its subsequent Linux acceptance passed 154 focused tests and 17 actual published-binary synthetic workflow commands; full-matrix locked restore retained all eight application target groups. The generated-inventory and strict TRI additions have their own Windows evidence and require their subsequent Linux snapshot gate. Full analyzer cleanup, complete method complexity review, and synchronized actor animation remain open. Owned-HWND PrintWindow captures were black for WinUI and were discarded; dialogue and geometry claims above rely on UI Automation assertions.
