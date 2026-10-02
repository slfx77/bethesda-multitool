# UI feedback corrections — September 21, 2026

This application-owned batch follows BMT `0e35e31ba50e25d4f08c08b3ce79275aa8589201`
and retains Shared `20e4e2c0fb2d304ed60c05e48d8c97a9a730b684`.
The [full completion program](../../shared/Multitool.Shared/docs/completion-program-2026-09-12.md)
remains active, including independent application release acceptance.
These corrections do not complete the broader migration or admit the pending FLC consumer.

Status: the normal-analyzer publication passed independent MSBuild, PDB,
producer/consumer, bundle/notice and CLI-help checks, and its GUI startup smoke
passed on a fresh observation. The wrapper's separate post-publish guard failure
is preserved and explained below. Portable 39 + 2 tests passed on Windows and
WSL, and both unchanged-XAML accessibility cases passed again on each platform.
Visual/input acceptance remains open because the Computer Use helper pipe is
unavailable. All earlier failures and corrected observation records are retained.

| User request | Source behavior prepared for verification |
| --- | --- |
| 1. Match the help shortcut to the caption controls. | The trailing shortcut uses measured native caption dimensions, shared caption colors, keyboard focus, localized names and F1. Its actual position, drag region and display scaling need GUI verification. |
| 2. Add missing resize bars. | Shared column splitters separate workflow options, DMP inputs/progress, classic actors/statistics and hex/minimap panes. Minimum widths protect their contents. Statistics strips, forms and table cells remain ordinary layouts. |
| 3. Keep File Carver output below File Path on every tab. | The existing output field, Browse and Extract controls move to the common source panel's second row. Their handlers and stored path remain the same; both path fields have explicit accessibility labels. |
| 4. Rename Texture Tools. | Navigation, page heading and dependency reporting use “DDX to DDS Converter.” |
| 5. Put Batch Dump Analysis under Recovery. | The separate Memory Dumps heading is removed, leaving Batch in the Recovery navigation group. |
| 6. Correct Model Tools layout. | Source and texture paths share columns; the file count is inside the tree card, restoring bottom alignment. Tree and details use the corresponding dark card background. The empty viewer says “Selected mesh will be shown here”; renderer faults remain separate. |
| 7. Name the extraction tool accurately. | Navigation and heading use “Archive Extractor,” retaining the existing BSA/BA2 extraction workflow. |
| 8. Remove the lone World tab under Maps. | The implementation retains the original `WorldMapTab.Content` owner and keeps the TabView present. Only the verified pinned-template `TabContainerGrid` strip is hidden for Explore Maps; its separate content presenter, population and renderer lifetime remain unchanged. Windows compilation passed; visible and lifetime verification remain pending. |
| 9. Combine source discovery with Load Order. | One dialog offers discovered primary and supplementary plugins alongside drag ordering and Add Files. It retains the current primary, leaves ambiguous initial choices unselected, and excludes duplicate or selected-primary supplementary paths. |
| 10. Explain the subtitle CSV. | The label states that the CSV provides dialogue text, speaker and quest names for memory dumps. Existing CSV selection and loading remain available. |
| 11. Open a BSA from Assets. | A selected physical archive leaf offers an explicit Open archive command, routed through the workspace's existing source-opening workflow. The exact current node and snapshot are checked. |
| 12. Keep the current pane when changing ESM. | Record loading suppresses incidental Data/Maps navigation while preserving explicit startup view arguments. Combined-dialog changes and separate source opening retain the user's existing pane; initial source opening may select Data. |

The main implementation locations are the [window shell](../../src/BethesdaMultitool/App/MainWindow.xaml.cs),
[Explore coordinator](../../src/BethesdaMultitool/App/Tabs/Explore/ExploreTab.xaml.cs),
[record/map presentation](../../src/BethesdaMultitool/App/Tabs/SingleFile/SingleFileTab.Explore.cs),
[load-order dialog](../../src/BethesdaMultitool/App/Helpers/LoadOrder/LoadOrderDialogService.cs),
[File Carver layout](../../src/BethesdaMultitool/App/Tabs/SingleFile/SingleFileTab.xaml),
and [Model Tools layout](../../src/BethesdaMultitool/App/Tabs/NifConverterTab.xaml).

Load-order boundaries remain explicit:

- Cancel does not apply the working selection. Clear All clears supplementary files and CSV while retaining the current primary; only Apply commits an edited primary from the dialog.
- Adding a discovered plugin requires an explicit selection and Add action. Changing the primary removes that same path from supplementary entries; it does not automatically add the former primary.
- Existing supplementary precedence remains unchanged: later supplementary entries override earlier ones, while the separately loaded primary retains its existing precedence.
- Same-source reload retains the chosen primary when still discovered and reuses the staged supplementary order/CSV. A different workspace source clears that staging.
- Supplemental application checks cancellation before disposing and replacing the previous order. The staged-load caller forwards the same cancellation token, including when a different primary is being loaded.
- A primary which disappears or becomes unrecognized before analysis reaches the Explore error status; it is not reported as a successful selection.
- Source changes continue through the existing cancellation, serialization and owner-retirement flow. This batch does not claim transactional rollback of an already cleared record document.

The [archive request](../../src/BethesdaMultitool/Core/AssetBrowse/AssetArchiveOpenRequest.cs)
accepts only a current archive node backed by a known loose filesystem, including the
first matching layer of an ordered filesystem. An opaque or archive-backed higher
layer prevents fallback to a lower loose file. The UI delegates the original physical
path to Explore; it does not extract an archive entry into a temporary file or copy
an archive within another archive. Normal payload decoding starts only in the existing
source-opening path after the explicit command.

## Complexity and ownership review

The archive request adds provenance and physical-path resolution only. It walks the
known filesystem layers until the first metadata winner and rechecks current tree
ownership; metadata lookup may perform filesystem I/O. It owns no payload, lease,
archive parser or cache. Opening remains the existing workspace owner's operation.

The dialog's discovered choices and file picker share duplicate/primary exclusion
and the existing format detector. Refreshing choices scans candidates against the
working supplementary list and allocates only the displayed candidate list. Selected
supplementary records and CSV still load through the existing semantic pipeline;
this change adds no parallel parser, implicit merge or separate resource owner.

The navigation guard separates rebuilding tabs for the current file type from an
explicit user navigation request. Source operations retain the existing serialized
opening and cancellation owner. The final Maps amendment preserves the original
TabView content ownership and hides only its verified template strip. It does not
reparent the map, add a renderer or change map population. The earlier retained
sibling-host approach is superseded. The strip-only implementation compiled in
the Windows publication; visible/lifetime verification remains pending.

Concrete GUI acceptance remains open: caption dimensions depend on actual DPI and
native insets; long plugin paths and the new choice rows may crowd the dialog; pane
minimum widths may expose clipping in a narrow window. The retained map must still
resume drawing and accept focus after Data/Assets navigation, and programmatic tab
rebuilds must not pull an Assets selection back to Data. Source review cannot prove
those visible behaviors or native input/retirement behavior.

Remaining validation:

- In the real GUI, check caption hover/focus/F1 and dragging at the active DPI/theme; drag and keyboard-resize the added splitters without clipping essential controls.
- Check all File Carver views, DDX/Archive names, Recovery grouping, Model Tools alignment/backgrounds/empty state, and Maps without the redundant strip.
- Exercise ambiguous discovery, explicit primary and supplementary selection, reordering, picker additions, removal, Clear All, Cancel, same-source reload and source replacement. Repeat ESM changes while Data, Maps and Assets are selected.
- Open an original loose BSA through Assets, reject a stale selection and an archive-contained archive, then replace/close the source. Confirm existing preview/check state follows the normal source lifetime.

Actual build/test artifacts and published binary identity are recorded below.
Source parsing, compilation and CLI execution alone are not GUI acceptance.

## First compilation and regression result

The first locked portable build completed in 739.55 seconds with 104 warnings and
zero errors. Its PDBs matched 2,796 application source documents (93 virtual generated
documents) and 1,543 test documents. All 39 frozen inputs and five dependency
DLL/PDB producer/consumer pairs matched. The first focused run passed 21 of 22
cases with zero skips; it stopped before accessibility execution.

The failing archive-precedence case exposed an inherited BSA root-entry lookup
mismatch: an empty folder produced a leading backslash in `BsaFileRecord.FullPath`,
while VFS enumeration removed it. The subsequent lookup could miss that archive
entry and fall through to a lower loose file. The final candidate repairs the
root-entry path, retains the root fixture and adds a nonempty-folder case. It also
replaces a synchronous test-setup text write with its awaited equivalent to remove
the one newly reported S6966 warning. The later platform validation below passed
the retained root and nonempty-folder cases on both operating systems.

Original evidence is retained under `TestOutput/ui-feedback-20260921`; its source
freeze SHA-256 is `45b781af9304a9803b1aa09d0be332090882f2a312fc5baf04add76663fa5418`.
Final-candidate evidence uses `TestOutput/ui-feedback-final-20260921`.

## Second compilation and Linux regression

The incremental locked portable build completed successfully in 801.38 seconds
with 77 warnings and zero errors. It retained normal analyzers, the one-node build,
12 GiB compiler heap cap and default 4 GiB coordinator admission. The final build
and correspondence receipts verify 40 frozen inputs and 12 recipe helpers. The
source-freeze SHA-256 is
`cc1502a3ec44020f1a3066c994952657eb33f97ed60bc50865971ad7f301d420`.

The application PDB matches 2,796 available source documents, with 93 virtual
generated documents recorded separately; the test PDB matches 1,543 available
documents and has no virtual generated documents. The five dependency DLL/PDB
producer/consumer pairs and the final test payload match the recorded hashes.
These are compilation/source-correspondence results, not GUI acceptance.

Windows passed all 39 focused cases and both accessibility checks with zero skips.
WSL passed 38 of 39 focused cases with zero skips and stopped before its
accessibility run. `BsaWriterFlagsTests.CreateWithAutoFlags_MiscContent_MatchesRetailMiscBsa`
expected file flags 256 but observed 260: host `Path.GetDirectoryName` did not
recognize the canonical backslash separator in archive paths on Linux, causing the
writer's content classification to differ. The existing behavioral case covers the
fix; this result is not waived or reclassified as a platform skip.

Both failed runs and their original build/test receipts remain intact. The new
`TestOutput/ui-feedback-platform-20260921` recipes retain 39 + 2 cases and normal
build settings, with fresh output labels. The platform correction and all five
App amendments precede that new portable source freeze. The GUI-only follow-up
then binds the platform PDB/correspondence receipts, verifies no source delta and
confirms the App Compile/Page exclusions before publishing to a fresh directory.
The third compilation, passing platform validation and first Windows publication
follow; successful GUI startup and visible acceptance remain pending.

## Third compilation with the platform correction

The incremental portable build succeeded with two inherited warnings and zero
errors. The build reported 14 minutes 41.40 seconds of elapsed compilation work;
the wrapper recorded 2,095.11 seconds overall, including approximately 20 minutes
waiting for normal memory admission. The user chose to let the unrelated primary
build finish; no process was stopped to obtain admission.

The two reported warnings are the existing CA1068 in `AssetThumbnailSource.TryRender`
and S3877 in `ThumbnailCacheObservation`. This incremental run's smaller warning
count does not establish that the earlier inherited test warnings were resolved.
Normal analyzers, locked restore, the one-node build, 12 GiB compiler heap cap and
default 4 GiB admission remained enabled.

The post-build check verified 41 frozen inputs and eight recipe helpers under
`TestOutput/ui-feedback-platform-20260921`. Its source-freeze SHA-256 is
`1b3a437f3c73391f11a822960e9307f3d3ff77b760825021dc5faafa3271f23a`.
PDB verification passed for 2,796 application source documents plus 93 separately
recorded virtual generated documents, and 1,543 test documents with no virtual
generated documents. Five DLL/PDB producer/consumer pairs and the test DLL/PDB
payload matched. The correspondence receipt SHA-256 is
`4d5f5a115be5d74ec576f2d47d0e0c482fc7eaeadabe23d4a50987b9fde8350c`.

The platform retry passed all 39 focused cases and both accessibility checks on
Windows and on WSL. Each of the four JUnit reports has zero failures, errors and
skips; their hashes match `actual-runtime-acceptance.json` in the platform evidence
directory. The 39-case filter covers `ExplorePanePolicyTests`,
`ExploreSourcePlannerTests`, `BethesdaBrowseSourceTests`, `AssetBrowseSessionTests`,
eight `AssetArchiveOpenRequestTests`, four `Core.Formats.Bsa.ArchiveReaderTests`
and twelve `BsaWriterFlagsTests`. Runs used at most four threads and rejected skips.
WSL executed the same Windows-built managed payload; this is not a Linux source
build. The accessibility checks read the final amended XAML.

The GUI freeze verified the same 41 source inputs and eleven GUI recipe helpers,
with no source delta from the accepted platform freeze. Its SHA-256 is
`3fc00aeb237878b25054e4deedb1233fec0cb1e801ef87c3335fac8f8da97d34`.
It binds the portable receipts and confirms all five App amendment paths are
excluded from the portable Compile/Page graph and absent from the portable PDB
documents. The Windows publication results follow; this freeze and portable
acceptance do not validate the GUI implementation by themselves.

A fresh Computer Use availability check still reports the missing helper pipe,
recorded in `TestOutput/ui-feedback-gui-20260921/gui-availability.json`. No GUI
interaction or visible acceptance is inferred from compilation or that failed
connection check.

## First Windows publication and published-output verification

The Windows publish completed with exit code 0 in 946.17 seconds. Its log contains
six warning lines: three inherited WIN2D0001 instances, inherited CA1068 and S3877,
and one new S3264 report for `ArchiveOpenRequested`. Source review confirms the
event is invoked through the pattern-captured `handler(request)`, its button is
wired, and Explore subscribes/unsubscribes and routes the request through
`OpenSourceAsync`. The S3264 report from SonarAnalyzer.CSharp 10.27.0.140913 is
therefore recorded as a source-confirmed false positive; no warning was suppressed
and this review does not prove that the GUI interaction works.

All seven PDB audits passed. The Windows app PDB verified 3,101 available source
documents and separately recorded 142 virtual generated documents. Fourteen
DLL/PDB producer/consumer comparison groups matched. The published executable at
`TestOutput/ui-feedback-gui-20260921/publish/windows-ui-feedback-gui/BethesdaMultitool.exe`
has SHA-256
`64f6b8e0e19ef5fad9551f50510b5dbfe254dcf97db1d2320dd809ee413dfec6`.

The published-output audit passed with no issues: 901 bundle entries, all seven
project assemblies, the shader pack, 260 selected runtime assets, 46 scoped
project notices and 56 physical publication files were checked. The dependency
inventory contains 42 library rows. DDXConv's complete notice text and the exact
Magick.NET-Q16-AnyCPU 14.14.0 package notice also matched. These are concrete
byte/dependency/notice checks, not a complete redistribution or release review.

The actual published executable's `--no-gui --help` invocation returned 0 in
2.53 seconds and included the expected native-capture command. Its 4,101-byte log
has SHA-256
`bc2e82eb7edce40d7a8fc456a0bf793bafe2d6a4ada006df4a0aac47c1e63d0d`.
All publication bytes remained unchanged. The combined receipt
`windows-ui-feedback-gui-published-audit.json` has SHA-256
`130e8c42d2cec49d1ccc572723cb1943c5927759281631187efdfeb80deef4df`.

The subsequent GUI launch did not succeed: PID 44612 exited, and
`BethesdaMultitool-gui-44612.log` reported a `XamlParseException` assigning
`RuntimeLocalization.Uid` during `MainWindow.InitializeComponent`. The launch
receipt records process creation only. This failure is retained separately from
the successful publish and CLI checks; a corrected GUI retry and visible
acceptance are pending.

## MainWindow initialization repair and Windows retry

The repair changes only `App/MainWindow.xaml` and `App/MainWindow.xaml.cs`:
the shortcut no longer applies `RuntimeLocalization.Uid` during XAML loading.
Static accessible name, help text and tooltip values remain available, and the
constructor explicitly binds `AutomationProperties.NameProperty`,
`AutomationProperties.HelpTextProperty` and `ToolTipService.ToolTipProperty` to
the unchanged resource keys. No resource, portable Core, project or test source
changed. Static review of 207 UID prefixes and 220 property bindings in the
changed XAML found no remaining suffix outside the pinned localization vocabulary;
this source check does not instantiate the GUI.

The retry freeze contains the same 41 source inputs and twelve recipe helpers.
It verifies that exactly the two MainWindow hashes differ from the accepted
platform freeze. The non-Windows Compile/Page exclusions and actual portable
PDB documents confirm those files are outside the compiled portable payload;
the preceding five App amendment paths remain covered by the same proof. The
existing portable DLL/PDB bytes are unchanged. The retry freeze SHA-256 is
`a046620ae6f6a1b79eed88c867e581fbf5e124b0f731566290ce397d6606522d`.

Exactly two accessibility cases passed on Windows and two on WSL, with zero
failures, errors or skips. They use the accepted Windows-built portable test
payload to inspect the amended XAML, with at most four threads and skip
rejection; they are neither an App recompilation nor a native GUI test. The
check receipts under `TestOutput/ui-feedback-gui-retry-20260921` have SHA-256:

- `ui-feedback-gui-retry-accessibility.check.json`:
  `2057fb48310f9be34a8bb23f6d25320c8783577b38ab279bc7c3f45481d8272a`.
- `ui-feedback-gui-retry-linux-accessibility.check.json`:
  `03ec2d41180ab7ab518e54d3b71a042455e0981353ff295d143abe385df68a4a`.

Both receipts bind the retry source freeze, the accepted platform build
correspondence and the actual two-case JUnit reports. The corrected Windows
publish results and the next startup failure follow. The first failed startup
log and all earlier receipts are retained.

## Second Windows publication and caption brush failure

The MainWindow localization retry published successfully in 647.30 seconds with
six warning lines. All seven PDB audits and fourteen DLL/PDB producer/consumer
groups passed. The bounded published-output audit and actual published CLI help
also passed; the bundle again contained 901 entries and the scoped notice check
covered 46 files. The retry executable SHA-256 is
`cf6905d1c202e6287fa358d16860f88ed06165e5bd87c7b0c7aabc25a0310675`;
its published-audit receipt SHA-256 is
`76dd5ecd0dfd28ed50557d3a51217aef196e729cb53193dd567051cab72c0be1`.

GUI startup for PID 14860 failed after `InitializeComponent` completed.
`UnauthorizedAccessException` arose from `ISolidColorBrushMethods.set_Color`
through `SetCaptionShortcutBrush` and `UpdateCaptionShortcutColors`. The launch
receipt explicitly records `startupSmokePassed: false`; its initial observation
of a responding process with no main window does not demonstrate a usable GUI.
The preserved `windows-ui-feedback-gui-retry-launch.stdout.log` SHA-256 is
`277b0f4f24527e1ced59286b4644b08b2ce5f5ec70eb11a39cd5194efda9214c`.

The next prepared repair changes only `MainWindow.xaml.cs` relative to that
retry. A retained dictionary owns the caption shortcut's brushes: first use
creates an application-owned brush and assigns the local resource; later theme
changes mutate only those owned instances. The code no longer obtains a
framework-owned resource brush and attempts to modify its color. XAML, resource
keys, portable Core, projects and tests remain unchanged.

Fresh normal-analyzer recipes are prepared under
`TestOutput/ui-feedback-gui-brush-20260921`. Their proof requires exactly the
MainWindow code file to differ from the previous retry freeze, while retaining
the exact two MainWindow differences from the accepted platform freeze. It
binds both prior two-case accessibility receipts and their JUnit reports; the
source XAML is unchanged, so those identical checks need no repeat. The user
requested a faster development iteration, so a separately labelled development
publish with `SkipAnalyzers=true` preceded the normal-analyzer acceptance run.
Its measured result and startup scope follow.

## Development-mode publication and successful startup

The development publication used `SkipAnalyzers=true`, retained locked restore,
and completed with exit code 0 in 581.54 seconds. Its executable SHA-256 is
`58e68c9240bae29e4a1d1c6e9fed0fa377dab333d556dad6f92047de65c987e6`.
The command receipt under `TestOutput/ui-feedback-gui-fast-20260921` explicitly
classifies this as development iteration, not normal-analyzer or release
acceptance. `fast-compiler-payload.json` preserves the fourteen compiled DLL/PDB
hashes before the subsequent normal pass for later comparison.

The published GUI startup smoke passed: at the recorded observation, PID 61984
was responding, had a nonzero main-window handle of 29364504, and its window title
was `Bethesda Multitool`. The startup output records constructor completion and
window activation; the native startup log contained no error. The process was
kept open for user review. This proves startup only: no automated screenshot,
caption/splitter interaction, map navigation, localization switching or other
visual/input acceptance is claimed. The launch receipt is
`windows-ui-feedback-gui-fast-launch.json` in the development evidence directory.

### Compilation performance and a possible Core boundary

The repository already supports `SkipAnalyzers=true` for development iteration;
it retains required compilation/source generation. Keep the existing locked
graph, coordinator admission and compiler settings, and run normal analyzers for
final acceptance. The timing reports under
`TestOutput/ui-feedback-build-timing-20260921` show 581.54 seconds in that mode
versus 647.30 seconds for the preceding normal publication, a 10.2% reduction
in whole wrapper elapsed time. The final Csc phase fell from approximately 433 to 227
seconds, while the intermediate phase increased from approximately 135 to 236
seconds. These observations include phase and machine-state variation and are
not a controlled causal benchmark of analyzer cost. The development-mode timing
does not justify changing the final acceptance settings.

The recorded WinUI build performs two large app C# compilations: 3,072 sources
for XamlPreCompile and 3,076 for CoreCompile. Each includes 2,595 Core files, 226
App files and 196 CLI files. The intermediate pass already skips analyzers.
Consequently, analyzer suppression alone does not eliminate repeated compilation
of unchanged portable code; these reports do not quantify individual analyzer
cost or a future architectural speedup.

The bounded `core-split-feasibility.md` review identifies prerequisites for an
application-owned portable Core library. First remove the demonstrated reverse
dependency from Core into CLI rendering settings/helpers while preserving
behavior. Keep the 33 Windows-gated implementation files in the Windows app
until their type/partial dependencies are reviewed. Move embedded resources with
their reader types and preserve logical names, package dependencies and deliberate
internal access for the app/tests/tools. Retain executable-owned shader-sidecar
generation, native assets and the GUI profiler's control/content contracts.

A later extraction must compile each production source once per target, use
neutral library deployment properties, update portable/full locked graphs and
assembly/dependency inventories, and pass portable, Windows, profiler and actual
publish/resource checks. Compare compiler input lists and measure a subsequent
App-only edit before claiming a gain. This is a dependency plan, not a source or
project change authorized by the current UI repair.

The normal-analyzer publication result follows. Development files are preserved;
the later observation of its process is recorded separately below.

## Normal-analyzer publication and post-publish guard failure

The normal brush publication produced its application and fresh publish output,
but the complete wrapper recorded exit code 1 after 928.41 seconds. Its final
source/helper guard rejected an additional `compare-development-publication.py`
script which had been placed in the frozen evidence directory during the run.
The original failed command receipt and log remain unchanged; this is not
reported as a successful wrapper run.

`post-publish-guard-diagnosis.json` records no source differences and no missing
or changed original helpers: all 41 source inputs and ten original helpers
remained intact. Only the ancillary comparison script was added. That script
was moved to the separate timing directory and the exact original freeze then
verified again. The diagnosis receipt SHA-256 is
`bb6aad8b1ee5531ad6628f75d88aca1dd7711d2c36220adcdc020a6f4e1218b8`;
the original source-freeze SHA-256 is
`de6dfa8cd4f5bfa30c69363edd8950618b2df2ad198c4478efc20ecde6cc23e4`.

After that diagnosis, all seven PDB audits and fourteen DLL/PDB producer/consumer
groups passed. The Windows app PDB verified 3,101 available source documents,
with 142 virtual generated documents recorded separately. The build
correspondence receipt SHA-256 is
`b8e8f2b4fe13db51ebdc58b3b95404ef02a74314b546cfb12ba9c34d201e752d`.
The normal published executable SHA-256 is
`e26da649f1ec66280e22c95e4fd23a44b603fa471d7160a3ec9b9cc7fcf7bbf3`.
It differs from the development executable, so that earlier startup observation
does not validate this artifact's startup.

Independent binlog inspection confirms `BuildFinished.succeeded: true`, six
warning events and zero errors, with a 901.509-second MSBuild span. The warnings
remain three WIN2D0001 instances and one each of CA1068, S3877 and the reviewed
S3264 report. `gui-brush-build-result.json` and `gui-brush.json` in the timing
directory bind the actual binlog. These successful MSBuild results do not change
the wrapper's recorded exit code 1.

The normal published audit passed with no issues: 901 bundle entries, 56 physical
files, 260 runtime assets, 46 scoped notices and 42 dependency inventory rows.
The actual published `--no-gui --help` returned 0 in 3.508 seconds with a 4,101-byte
log; publication bytes remained unchanged. The audit receipt SHA-256 is
`bd561154edc89c3f293463a2c4f3176b7610d1b1f3d9f9aba955657bd94b2cf3`.
The development/normal comparison found different application DLL/PDB bytes;
only the executable and application PDB differed among the 56 published files.
A separate normal-artifact startup observation was therefore required.

## Normal-artifact startup observation and remaining GUI scope

The fresh startup recheck passed for PID 22124: the normal executable was
responding, its title was `Bethesda Multitool`, and its main-window handle was
39588222. Constructor completion and window activation were logged, with no
startup error. `windows-ui-feedback-gui-brush-startup-recheck.json` binds the
normal executable hash and the observed stdout/startup-log hashes.

The original launch receipt remains unchanged with `startupSmokePassed: false`:
its checker expected `MainWindow activated`, while the application actually logs
`Main window activated`. The recheck documents that checker error. An initial
attempt to hash the open stdout file also encountered a file-sharing error;
the successful recheck used read/write file sharing. Neither observation error
is classified as an application startup failure.

Development PID 61984 was present and preserved when the normal process launched,
but absent at the later recheck. The session did not close it and its exit cause
was not observed. The recheck's `existingDevelopmentProcessPreserved: false`
records that later absence, not an intentional closure. Its published files and
earlier startup evidence remain intact.

This establishes normal-artifact startup only. No GUI input, screenshots, caption
geometry/theme switching, splitter operation, map navigation or archive/dialog
interaction is accepted from these checks. The missing Computer Use helper pipe
remains the barrier to automated visible/input verification; the explicit GUI
checks listed above and the broader completion program remain open.
