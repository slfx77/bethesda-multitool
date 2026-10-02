# Claude worktree audit — 2026-10-02

**The old worktree is not safe to delete as-is.** Its tracked application implementation is present in the main working tree, with the reviewed later changes below, including pre-existing first-party attribution cleanup. It also contains unique, ignored, unfinished Claude work that a normal `git status` does not show. Keep the worktree and its branch for the intended session resume.

This is an integration/preservation audit, not build, test, GUI or release acceptance. The cleanup session records those checks separately. No worktree, branch, candidate, local capture or peer file was deleted, moved, reset or applied by this audit.

## Identity and snapshot

- Claude title: **BMT Shared Foundation Implementation**.
- Claude session ID: `0260887b-8cfa-44d9-9419-927f83483067`.
- Session transcript: `C:/Users/mmc99/.claude/projects/c--dev-Multitool-BethesdaMultitool/0260887b-8cfa-44d9-9419-927f83483067.jsonl`.
- Current interactive metadata: `C:/Users/mmc99/.claude/sessions/27864.json`, title confirmed, cwd `C:/dev/Multitool/BethesdaMultitool`, status `idle`; process 27864 was present. This metadata is a snapshot, not proof that an old task completed.
- Historical implementation cwd and preserved worktree: `C:/dev/Multitool-worktrees/bmt-material-preparation-20260912`.
- Branch: `work/bmt-material-preparation-20260912`; tip `3ea47b11b1ecd8fc6b21497718ca02fb7c19c481`.
- Main baseline at audit start: `c94ac2f1c18922919a231f689238bb768e427191`, also the merge base. The old branch has **239 commits** beyond that baseline. Its history was not yet an ancestor of main at audit start; copied content and merged ancestry are separate facts.
- Worktree status, including ordinary submodule/untracked checks, was clean. Old Shared pin: `9e8aff2c93ea5dedefb3ace11d4ebefd3c97a602`. Old DDXConv pin: `93ca3a2bd48be62d2882802bd029935d00f432fa`. Both pins matched the main checkout at audit start.
- Canonical coordination remains `C:/dev/Multitool/Multitool.Shared/INTER-SESSION.md`. Its worktree/checkpoint notes lag the actual old branch, so use verified Git identities above.

## Tracked-tree accounting

The audit compared every path changed between the merge base and the old branch tip, against main's working files. CRLF/LF differences were normalized; binary data was not rewritten. Old branch has 6,212 tracked entries. The 1,450 changed path records include a rename as separate old/new paths, whereas Git's rename-aware stat reports 1,449 files.

| Result | Paths |
| --- | ---: |
| Main matches old branch bytes after CRLF normalization | 1,362 |
| Main has later edits; three-way merge of the old branch makes no further change | 40 |
| Old deletions remain deleted | 7 |
| Matching submodule commits | 2 |
| Later overlapping/extended files manually reviewed below | 38 |
| Relocated private corpus profile | 1 |

No changed path was found reverted exactly to the main baseline. The only absent old-branch file is `tools/corpus/corpus.json`: `.gitignore`, `CLAUDE.md`, and fixture helpers explicitly relocate it to `../CorpusTool/profiles/BethesdaMultitool.json`. That file exists; all **55 old build IDs** remain among its **59 build IDs**. This verifies inventory identity, not byte-for-byte equivalence of a profile deliberately updated since relocation.

The 38 overlapping paths are accounted for below. These notes establish the disposition of old branch changes; they are not exhaustive review of the later features or substitutes for tests.

| Path | Disposition |
| --- | --- |
| `CLAUDE.md` | Corpus/profile/media relocation documentation and the newer analyzer-profile workflow replace the old paths and direct build examples; retained feature instructions remain present. |
| `Directory.Build.props` | Analyzer controls and analyzer PackageReferences move to the imported Shared analyzer policy; the old independent settings are superseded. |
| `Directory.Packages.props` | Analyzer versions move to Shared imports; BCnEncoder.Net uses the existing shared dependency version. |
| `THIRD_PARTY_LICENSES` | Pre-existing cleanup removes the NeversoftMultitool and JimmyPCTool/AweMultitool first-party attribution sections alongside main LICENSE changing from MIT to 0BSD. External third-party notice sections remain. Preserve this existing cleanup; the old text remains in Claude branch history. This records observed changes without inferring their authorization. |
| `src/BethesdaMultitool/App/Tabs/SingleFile/SingleFileTab.ActorDetails.cs` | Actor details retained; selected effective records and asset-use rows extend the old display. |
| `src/BethesdaMultitool/App/Tabs/SingleFile/SingleFileTab.DialogueAudio.cs` | Dialogue controls retained; selected effective records and load-order audio origin replace single-file assumptions. |
| `src/BethesdaMultitool/App/Tabs/SingleFile/SingleFileTab.DialogueViews.cs` | Reference navigation retained and extended to effective records and EditorID lookup. |
| `src/BethesdaMultitool/App/Tabs/SingleFile/SingleFileTab.NpcBrowser.cs` | Actor viewer retained; selected load order, loose/archive source discovery, and explicit unavailable states extend the old path. |
| `src/BethesdaMultitool/CLI/Commands/Dialogue/DialogueCommand.cs` | Adds audio-catalog registration; no old registration removed. |
| `src/BethesdaMultitool/CLI/Commands/Esm/EsmActorDetailsCommand.cs` | Old actor/level/seed/output contract retained; adds load-order identity, template resolution, statistics profile and runtime-scenario support. |
| `src/BethesdaMultitool/CLI/Commands/Esm/EsmCommand.cs` | Actor-details registration retained. Reports moved to EsmReportsCommand; terminal graph added. JSON output and exit-status handling supersede old reporting code. |
| `src/BethesdaMultitool/Core/Actors/ActorInspection.cs` | Only new source/template/effective-statistics metadata; existing result members retained. |
| `src/BethesdaMultitool/Core/Actors/ActorInspectionJsonWriter.cs` | Old statistics/inventory/generated-preview fields retained; optional context/statistics writers and template data added. |
| `src/BethesdaMultitool/Core/Actors/ActorInspector.cs` | Authored values and bounded/seeded inventory retained; template resolution now delegates to ActorTemplateResolver by field group; level multiplier and creature statistics extended. |
| `src/BethesdaMultitool/Core/Formats/Esm/Parsing/DialoguePluginIdentity.cs` | Existing owner resolution delegates to newly reusable complete master-slot reading. |
| `src/BethesdaMultitool/Core/Formats/Nif/Rendering/D3D12/ReferenceMeshCache12.cs` | Shared recorder/resource retirement retained; actor scene decoding, cancellation, provenance and generated-texture resolution extend the cache. |
| `src/BethesdaMultitool/Core/Formats/Nif/Rendering/Export/GlbWriter.cs` | Old geometry/material/skin/winding logic retained inside read-capture scopes; asset provenance is added to output extras. |
| `src/BethesdaMultitool/Core/Formats/Nif/Rendering/Npc/Appearance/NpcEquipmentResolver.cs` | Equipment choice/mesh handling retained; source ownership is carried through leveled armor and duplicate mesh references. |
| `src/BethesdaMultitool/Core/Formats/Nif/Rendering/Npc/NpcBrowserService.cs` | Existing resolver/viewer retained; selected-source plans, read receipts, cancellation, world composition and retryable retirement extend it. |
| `src/BethesdaMultitool/Core/Formats/Xngine/Flic/FlicDecodedMediaDecoder.cs` | Decoder retained; exact reduced nominal frame rate metadata added. |
| `src/BethesdaMultitool/Core/Media/Audio/Dialogue/DialogueAudioIndex.cs` | Index retained; deterministic entry enumeration and reusable audio-extension predicate added. |
| `src/BethesdaMultitool/Program.cs` | Old CLI registrations and resource reporting retained; refs/runtime commands, JSON stdout isolation and cancellation added. |
| `src/BethesdaMultitool/Strings/en-US/Resources.resw` | XML dictionary comparison finds zero removed old resource keys. Two old values shortened (ActorDetails_AuthoredHelp.Text, ActorStat_Level); other apparent deletions are formatting/reordering. |
| `src/BethesdaMultitool/packages.lock.json` | Old dependency graph retained with analyzer upgrades Roslynator 4.15.0 to 5.0.0 and Sonar 10.27.0.140913 to 10.35.0.4138. |
| `src/BethesdaMultitool/packages.net10.0.lock.json` | Same analyzer upgrades in portable lock graph; no old package removed. |
| `tests/BethesdaMultitool.Tests/Core/Formats/DiscImage/ChdCorpusRetailTests.cs` | CHD sweep follows shared sibling Media root and restricts it to this project build names. |
| `tests/BethesdaMultitool.Tests/Core/Formats/DiscImage/ClassicDiscImageRetailTests.cs` | Retail fixture resolution updated from Sample/Media to shared sibling Media. |
| `tests/BethesdaMultitool.Tests/Core/Formats/Nif/Rendering/D3D12/ReferenceFixedPipelineGpuTests.cs` | Assertions retained; collection changed to ProcessEnvironmentGroup. |
| `tests/BethesdaMultitool.Tests/Core/Formats/Nif/Rendering/D3D12/ReferencePipelineGpuFixture.cs` | WARP sample count is now an explicit factory argument instead of a temporary process environment variable. |
| `tests/BethesdaMultitool.Tests/Core/Formats/Nif/Rendering/Shaders/ShaderInventoryTests.cs` | Same forbidden-compilation assertions use maintained production source enumeration/cache. |
| `tests/BethesdaMultitool.Tests/Core/Formats/Xngine/Flic/FlicDecodedMediaDecoderTests.cs` | Existing unsigned-delay case retained as a theory row; exact-rate and out-of-range metadata row added. |
| `tests/BethesdaMultitool.Tests/Core/Repack/RepackerBaselineTests.cs` | Same assertions retained; asynchronous Progress callback replaced by synchronous evidence collection. |
| `tests/BethesdaMultitool.Tests/Helpers/RealAssetPaths.cs` | Shared Media path migration, evidenced Oblivion J2ME name and New Vegas build-specific helpers extend existing fixture resolution. |
| `tests/BethesdaMultitool.Tests/Helpers/SampleCorpus.cs` | Existing legacy mapping retained; optional private profile aliases add renamed-build candidates. |
| `tests/BethesdaMultitool.Tests/Helpers/SampleCorpusTests.cs` | Assertions unchanged; messages point to relocated corpus profile/media. |
| `tests/BethesdaMultitool.Tests/packages.lock.json` | Same analyzer upgrades in test lock graph; no old package removed. |
| `tools/scripts/build.ps1` | Shared coordinator wrapper retained; analyzer profile, compatibility switch and compiler concurrency parameters added. |
| `tools/scripts/coverage.ps1` | Existing coverage process retained; explicitly builds/queries Fast profile to avoid stale Full binaries. |

## Unfinished work that exists only in the old worktree

The last substantive Claude update, September 29, reported three staging/research streams. Claude then hit its usage limit. The October 2 stopped-task notification concerns an old log monitor (`bbcs5xinp`), not completion of these streams.

1. **Cut 1c slice 7 follow-ups:** `TestOutput/cut1c-20260928/slice7/` contains `FOLLOWUP.md`, `staging.diff`, complete staging/work copies, measurements and receipts. `staging.diff` is 163,721 bytes, covering **19 files: 17 modified and 2 added**. All 19 staging files differ from current main and old committed files. `git apply --check` succeeds forward against main, while reverse checking fails: this is demonstrably **unapplied work**, not an already-landed patch. The new `XnGineRepeatedPointPlane.cs` and `Cut1cRedguard3DcBudgetTests.cs` are absent from main. The candidate changes repeated-point faces and Redguard morph animation budgeting. Its own report says it has not been built/tested and records design decisions, estimated GLB regressions, retail and Blender checks still required. Preserve for review/resume; do not silently equate it with accepted progress.
2. **3XDR mip-chain investigation:** `TestOutput/cut2-3xdr-20260928/` contains `MEASURE.md`, `LOCATE.md`, `ghidra/RUN.md`, `decode/ANSWERS.md`, the header census and its JSONL/summary receipts. The decode directory has research/scripts/evidence, not a completed implementation patch. The full proposed decode still needs implementation and validation.
3. **Fixture resolution census:** `TestOutput/phasef-fixture-resolution-20260929/CENSUS.md` identifies old unpacked-file tests whose bytes are available in retail archives. It is explicitly census-only; no implementation was staged there.

None of those three main-checkout counterpart paths exists. A bounded patch inventory found **98 `.diff`/`.patch` files** under old `TestOutput`, including historical applied/superseded candidates and published notice copies. This is not a claim that 98 pending changes exist, nor an exhaustive ignored-file inventory. Keeping the worktree preserves those records and unenumerated outputs without guessing which can be discarded.

## Main checkout Shared edits

The old Shared checkout is clean at the same pin. Its five corresponding files match that committed pin. Main's five dirty Shared files are later edits, not missing Claude work:

| Files | Disposition |
| --- | --- |
| `Playback/NativeMediaTimeline.cs` | Rounded/clamped tick formatting; exactly matches canonical Shared commit `d3954a9ab2749769a63c1fb266e18bfb7997d3be`. |
| `Playback/DecodedMediaStreamSource.cs` | Private, opt-in `BMT_DECODED_SAMPLE_TRACE=1` diagnostics: first eight video requests, weak request/sample metadata, pixel hashes, bounded clock observations. Canonical Shared still has the pinned baseline for this file. |
| `BlendAdmissionImporterParityTests.cs`, `BlendAdmissionSceneRowTests.cs`, `ModelBlendWriterPlanTests.cs` | Behavior-preserving formatting, expression-bodied helpers, target-typed construction and explicit argument names/defaults. `Image()` and `Image(0)` are equivalent because the helper defaults to zero. Assertions were not removed. Canonical Shared still has the pinned baseline for these files. |

Canonical Shared was independently dirty on unrelated foundation files. It was only read, never moved or changed. Adopting its moving tip is unnecessary to preserve these five consumer-local edits.

## Resume and deletion conditions

Resume the existing named session with the ID above. Explain that main now contains the imported committed implementation plus subsequent cleanup, while the three unfinished streams remain in the old worktree. The transcript contains many absolute old-worktree paths; the current session metadata already points to main, so explicitly establish the intended cwd before further writes. Do not apply old staging copies wholesale over newer main code.

Before a later deletion: preserve or deliberately dispose of the unique ignored candidates and their evidence; provide Claude a durable pointer to the new locations; verify no new old-worktree/submodule changes appeared; preserve branch history or record its final integration; and recheck the final categorized main commits. No newly tagged release or public foundation availability is implied by this audit.

Detailed local evidence remains under ignored `artifacts/cleanup-20261002/`: `worktree-audit.json` (all path hashes/classification and overlap diffs), `worktree-overlap-review.json`, `shared-dirt-audit.json`, `claude-slice7-candidates.json`, `claude-candidate-patches.txt`, and `claude-session-resume-extract.json`. These are local artifacts, not required public build inputs.
