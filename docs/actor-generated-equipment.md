# Generated actor equipment previews

A selected plugin NPC can use the exact concrete inventory returned by
`ActorInspection.Generation` for its native preview and selected GLB/PNG exports.
The selected request retains one generation, including its seed and explicit
preview level. Appearance composition does not draw random values again.

`NpcRenderOptions.Generation` travels through `NpcBrowserWorkflowService`,
`NpcBrowserService`, `NpcAppearanceResolver`, and `NpcAppearanceFactory`. The
factory copies the concrete items once into the existing resolver list shape.
Both armor and weapon selection use that same list. An empty generated inventory
clears equipment; it never falls back to authored or template inventory. A partial
generation uses only its known concrete leaves and remains labelled incomplete.

The concrete inventory mode prevents recursive LVLI expansion in both equipment
resolvers. A package can prefer a weapon only when the generated inventory contains
that weapon with a positive count. Existing package unequipped flags, armor slot
selection, combat restrictions, weapon ranking, attachment, and mesh composition
continue to apply. The displayed weapon has `GeneratedPreviewInventory` provenance,
and the native scene label includes the supplied seed and preview level.

This is a reproducible inventory and visual selection preview. It does not reproduce
the engine's random state, actor/player level calculation, scripts, ammunition
granting, or live equipment decisions. The existing static weapon scorer still uses
its previous ammunition assumptions. Memory-dump equipment remains authoritative:
the browser rejects a generated override on that path. Unseeded NPCs and creatures
retain their existing behavior. Batch workflows require separate generation per
actor and cannot reuse the selected actor's inventory.

## Verification

`NpcGeneratedInventoryEquipmentTests` exercises Fallout 3 and New Vegas concrete
replacement, empty template inheritance, defensive rejection of residual leveled
references, positive-count package membership, incomplete results, and returning
to unchanged static resolution. Existing equipment, Oblivion leveled weapon, and
memory-dump tests remain part of the acceptance gate.

The first combined portable build completed in 56.54 seconds with no errors.
Application analyzers were disabled for this iteration. Its focused run passed
54 tests with no failures or skips, including all nine new concrete-equipment
cases and the existing actor, equipment, leveled-weapon, and DMP-list checks.
Retail Bucket B cases were explicitly excluded from that run.

Run these commands from the BMT checkout with `$sharedRoot` pointing to the shared
package checkout:

```powershell
& $sharedRoot/tools/scripts/build.ps1 -Project tests/BethesdaMultitool.Tests/BethesdaMultitool.Tests.csproj -BuildArgs @('--no-restore','-p:BuildTestsOnly=true','-p:SkipAnalyzers=true')
& tests/BethesdaMultitool.Tests/bin/Release/net10.0/BethesdaMultitool.Tests.exe --filter-class '*ActorInventoryGenerationTests' '*ActorInspectorTests' '*NpcGeneratedInventoryEquipmentTests' '*NpcEquipmentResolverTests' '*NpcWeaponLeveledListResolverTests' '*NpcBrowserDmpActorListTests' --filter-not-trait 'Category=BucketB' --minimum-expected-tests 50
```

The initial build reported test-only CS8892 from previously restored multitarget
NuGet metadata. A full test-graph restore, followed by `--locked-mode`, without
global target overrides restored the unconditional xUnit import and preserved all
eight application lock target groups. MSBuild evaluation then confirmed
`GenerateTestingPlatformEntryPoint=false`. No source suppression was added.

Local evidence is under shared `TestOutput/bmt-generated-inventory-tests*`.
The independent TRI run passed 17 tests including its explicit corpus manifest.
After restore repair, the final test-only compilation passed with zero warnings
and errors, and the same 54 focused cases passed again with no skips.

## Real FNV geometry and export gate

The opt-in `NpcGeneratedInventoryRetailTests` passed against the installed
`FalloutNV.esm`, resolved by `RealAssetPaths`, and its discovered archives. Retail
inputs were read in place. The production semantic loader and analyzed-record
browser initialization selected Craig Boone (`00092BD2`) and generated six
concrete inventory stacks at seed 7, preview level 5.

The generated native scene contains the hunting-rifle scope mesh, outfit, shades,
and beret. The explicit empty-generation scene contains neither the resolved weapon
nor armor source paths and retains nonempty body geometry. Both scenes were exported
through `NpcBrowserService.ExportViewerSceneToGlb`, the same scene export used by
the selected actor workflow.

| Output | Mesh parts | Triangles | GLB bytes | SHA-256 |
| --- | ---: | ---: | ---: | --- |
| Generated | 18 | 14,669 | 12,056,692 | `98B349167F96816AC333B142AF4565B6CCBD7EB76247C6EF9E81E5B5F3A7153B` |
| Empty | 12 | 8,008 | 9,095,872 | `A7DD5DD44374719FB9D38B15D96580B59A790DD017E8FB86E4E4664734EB2AA6` |

Both files passed Khronos glTF validation with zero errors, warnings, information
messages, or hints. The one retail test passed in 15.85 seconds with no skips.
Reproduce it by setting `RUN_BUCKET_B=1`, optionally setting
`BMT_GENERATED_EQUIPMENT_OUTPUT` to a private output directory, and running the test
executable with `--filter-class '*NpcGeneratedInventoryRetailTests' --fail-skips on`.
The local receipt, per-mesh source paths, exported files, xUnit result, and validator
JSON are under shared `TestOutput/bmt-generated-equipment-retail`.

## Windows controls gate

The final combined Windows build passed in 3m12.06 with zero warnings or errors.
Application analyzers and shader regeneration were explicitly disabled; this is
not an analyzer-clean claim for the repository. The command was:

```powershell
& $sharedRoot/tools/scripts/build.ps1 -Project src/BethesdaMultitool/BethesdaMultitool.csproj -BuildArgs @('--no-restore','-p:SkipAnalyzers=true','-p:GenerateShaderBytecodePackOnBuild=false')
```

An initial PRI packaging failure identified a collision between the existing plain
Authored status resource and the new button scope. The new button uses a distinct
resource prefix. The first GUI run then caught a stale enabled-state issue when a
fractional level cleared generated mode without mesh archives. Refreshing the
interaction controls when the level changes fixed it.

The strict final GUI run used two synthetic NPCs and three authored item records,
without requiring meshes. Scoped UI Automation verified:

- Seed 7 at level 5 produced six concrete stacks; repeated Generate retained them.
- Editing an unapplied seed preserved the current generation; changing the level
  recomputed with the applied seed.
- Fractional levels cleared generated mode, and fractional levels/seeds were rejected
  on Generate. Show authored returned to declarations and candidates.
- The chance-none NPC produced an explicit empty generated inventory.
- Selecting another actor and reloading the shared source cleared retained generation.

Owned process 60476 exited, and its application log contained no ERR/FTL entries.
The final Windows DLL SHA-256 was
`925444D30BDC6414FB18F97D7F70D628E0E6791F1E7D1A20C37F3508E2E9360F`.
Local helpers and evidence are shared `TestOutput/test-actor-generated-ui.ps1`,
`TestOutput/prepare_actor_generated_gui.py`, and
`TestOutput/actor-generated-gui/{inputs.json,controls-final.log,final-receipt.json}`.
No private game inputs were copied into those synthetic fixtures. The retail GLB
outputs above remain private local acceptance artifacts.

## Portable paths and analyzer follow-up

The first Linux run of this increment passed 212 of 214 selected tests. Both
failures exposed an existing creature-path defect: host filesystem helpers treated
Bethesda's backslashes as ordinary filename characters on Linux. The actor list
therefore returned `daedroth.nif` without its skeleton directory. The same defect
also affected actual creature body composition and idle-path inspection.

`CreatureAssetPath` now resolves these virtual directories independently of the
host. Body paths that already contain directories retain their authored identity
in the inspector. Render lookup normalizes either separator convention and adds
the meshes root once, including inputs with a leading separator. The two original
failing assertions remain unchanged; 21 additional cases pin body, idle, mixed
separator, and meshes-root behavior.

The analyzer pass reported 106 unique diagnostics. Exact pre-actor and accepted
source hashes attributed 35 to this actor increment, including nine unchanged
inspector tests affected by the new cancellation parameter. These were addressed
without suppression: generated-label formatting uses a separate conditional,
completeness derives from the retained first failure notice, tests pass their
runner cancellation token, and immutable expectations/serializer options are
reused. Two additional tests pin incomplete empty generations caused by missing
or cyclic templates. The TRI cancellation diagnostic was corrected separately;
70 diagnostics belonged to unchanged files outside this increment. This
attribution does not claim those remaining diagnostics are resolved.

The final portable build passed in 52.81 seconds with zero warnings or errors,
using the same `BuildTestsOnly=true`, `SkipAnalyzers=true`, and `--no-restore`
command above. The selected actor/path run passed **77/77**, with no skips, in
2.115 seconds:

```powershell
& tests/BethesdaMultitool.Tests/bin/Release/net10.0/BethesdaMultitool.Tests.exe --filter-class '*ActorInventoryGenerationTests' '*ActorInspectorTests' '*NpcGeneratedInventoryEquipmentTests' '*NpcEquipmentResolverTests' '*NpcWeaponLeveledListResolverTests' '*NpcBrowserDmpActorListTests' '*CreatureAssetPathTests' --filter-not-trait 'Category=BucketB' --minimum-expected-tests 77 --fail-skips on
```

The real Boone composition gate passed again in 15.972 seconds. Both generated
GLBs were byte-for-byte identical to the previously validated outputs in the table
above. The test now uses the repository's shared real-master cache and leaves its
parsed result owned by that cache.

The final combined Windows build passed in 2m51.77 with zero warnings or errors,
using the Windows command above with analyzers and shader regeneration disabled.
Scoped UI Automation repeated every generated-inventory control/state check above.
Owned process **11216** exited, and its application log had no ERR/FTL entries.
The checked Windows DLL SHA-256 is
`AF249C5024DA850F0F3E76513DA46DF5120DD57BB55AE96D5ED3538762812868`.

Local evidence is under shared `TestOutput/bmt-actor-path-*` and
`TestOutput/actor-path-gui/{controls.log,application.log,receipt.json}`. Exact
diagnostic attribution and applied file hashes are under
`TestOutput/pending-actor-acceptance-fixes`. The subsequent analyzer-enabled Linux
test/publish gate is recorded separately; the fast Windows checks above do not
substitute for it.


## Linux publication

The final analyzer-enabled Linux suite passes 247 focused tests with no skips,
zero errors and the 70 attributed existing test diagnostics. A fresh publication
snapshot differs only in the DDXConv project-reference deployment fix and two
documentation files. Every tested C# source file is unchanged. Full committed
framework/RID locked restore and self-contained trimmed publication pass with
zero warnings/errors, followed by 46 actual executable commands: 17 existing
browser/dialogue checks, 12 generated-inventory checks and 17 TRI checks.
All four committed package locks remain byte-identical.

The Linux executable SHA-256 is
`a1d178838c767a0cf89bc346cc0dd12eb6705babdbb73502c71ad42595252b2d`.
Shared evidence labels are `acceptance-20260909-bmt-actors-tri-egm-v2` and
`acceptance-20260909-bmt-publish-v3`; the latter includes the exact tested-source
comparison and individual command receipts. These synthetic published checks do
not replace the private Boone geometry/appearance gate above.
