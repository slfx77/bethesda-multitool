# Shadowkey native offscreen capture

The applied command captures one admitted Shadowkey pack selection directly through Shared's native renderer. It does not serialize a GLB or start a WinUI window. Source/API review uses Shared 88326240d9603a2092886934eed6d63275fdbcf7. Portable tests and the Windows capture build pass, and three original Shadowkey selections now have successful native captures. The fern captures also pass independent selected-pixel checks. These results are bound to the [capture-build hashes](validation/shadowkey-native-preview-20260920.md); later analyzer/publish-notice edits require their own validation. GUI and release acceptance remain open. The preceding native neon checks exercise a separate route.

## Usage and admission

Use the Windows GUI build of BethesdaMultitool, passing the real pack path:

    BethesdaMultitool.exe capture-shadowkey-native "C:\...\models.huge" --slot 0 --output "C:\...\fern-opaque.png"
    BethesdaMultitool.exe capture-shadowkey-native "C:\...\models.huge" --slot 0 --output "C:\...\fern-keyed.png" --magenta-key

Both calls select frame zero and skin zero by default. Use --frame and --skin for explicit nonnegative values. Slot is an original zero-based pack ordinal, never a filename such as fern.bin. Existing empty-slot, invalid-range, corrupt-record, and animated-sequence admission is retained. Unsupported selected scenes produce no PNG or receipt.

The color key is explicitly hypothetical and defaults off. The existing 0RGB444 palette is expanded by the original decoder; it does not supply an authored alpha nibble. Geometry, separate position/UV indices, selected skin, normalized UV units, out-of-range Repeat/Repeat sampling and existing unlit material come from the same adapter used by the browser.

Output is fixed at 512×512. The PNG path must have a .png extension. Its sibling receipt appends .capture.json. Both paths must be absent, including directories, and there is no overwrite option.

| Exit status | Meaning |
| ---: | --- |
| 0 | Native capture and disposal succeeded, then PNG and receipt both committed. |
| 1 | Invalid arguments, input/output failure, native failure, or cleanup failure. Parser-level rejection also fails the command. |
| 2 | Native capture is unavailable in the portable build, or the complete selected scene is unsupported. |
| 3 | Command cancellation was observed by the handler. Framework cancellation may stop invocation before the handler runs. |

The portable build registers the same command and syntax but does not initialize native rendering or open input/output files. It still validates selection values and canonicalizes the supplied paths before reporting unavailability.

## Source ownership and bounded work

[ShadowkeyNativeCaptureOptions](../src/BethesdaMultitool/CLI/Commands/Render/ShadowkeyNativeCaptureOptions.cs) validates paths/selection without reading payloads. [ShadowkeyNativeCaptureCommand](../src/BethesdaMultitool/CLI/Commands/Render/ShadowkeyNativeCaptureCommand.cs) selects the compile-time platform behavior and maps outcomes. [ShadowkeyNativeCapture](../src/BethesdaMultitool/App/ShadowkeyNativeCapture.cs) owns the Windows operation.

Three required siblings are opened read-only with FileShare.Read. This diagnostic requires models.txt; the ordinary browser catalog allows missing names. Length admission occurs before hashing or decoding:

| Input/resource | Limit |
| --- | ---: |
| models.huge | Nonempty, at most 32 MiB |
| models.idx | Nonempty, at most 32,772 bytes |
| models.txt | Nonempty, at most 1 MiB |
| Catalog slots | At most 4,096 |
| Bounds pose workspace | 128 MiB numeric payload allowance |
| Native sampled geometry | 256 MiB |
| Per encoded PNG | 16 MiB |
| Retained decoded images | 256 MiB |
| GPU geometry | 256 MiB |
| GPU textures/staging | 512 MiB |
| GPU depth | 128 MiB |
| Capture color target/readback | 4 MiB each |
| Completed PNG reopened for receipt hashing | Nonempty, at most 8 MiB |

The mounted diagnostic tree has exactly one real statted models.huge leaf, preserving its path/size and normal Model kind. The source path is its containing directory. This avoids recursive directory enumeration and does not change GUI tree construction. The source transfers ownership of its LooseFileSystem once. It does not fabricate slot paths or establish GUI enumeration/selection acceptance.

The ordinary [ShadowkeyPackPreviewSource](../src/BethesdaMultitool/Core/AssetBrowse/ShadowkeyPackPreviewSource.cs) verifies exact source-tree ownership, reads bounded siblings, retains detached pack bytes, and prepares only the selected record. A record parser still reads that record's complete frames/skins. The entire bounded pack can coexist with its detached copy; parser arrays, expanded triangles, RGBA, encoded PNG, scene copies, codec scratch and driver storage add memory. These numerical allowances are not a process working-set cap.

The command hashes all three original handles before preparation and again after successful native disposal, before publishing anything. It never copies source payloads into a validation artifact. Handles remain open throughout the operation. BrowserSession retains the exact snapshot while catalog preparation borrows it. The NativeSceneInput factory acquires its own snapshot lease only when Shared invokes the factory; cancellation cannot strand a lease acquired in an uninvoked closure. Input construction failure releases the untransferred lease.

Capture receives the exact immutable ModelDocument from the browser's preparation path. [ShadowkeyNativePresentation](../src/BethesdaMultitool/App/Helpers/ShadowkeyNativePresentation.cs) measures its original normalized bounds and supplies FrameCamera at aspect 1. The request uses the browser's initial filled appearance, background, lighting, budgets and opaque-output convention. It does not perform another coordinate conversion, invent animation timing or reproduce a user's changed camera/aspect.

Cancellation is cooperative. Existing synchronous parsing and GPU work have their existing cancellation boundaries; there is no hard wall-clock deadline. Shared native retirement must complete even after cancellation.

## Retirement and publication

The command awaits NativeSceneCapture.DisposeAsync before opening an output. If capture and disposal both fail, it preserves both exceptions. If only capture fails, it rethrows the original failure after successful cleanup. A failed native retirement publishes no outputs.

A terminal cleanup failure can leave the background capture worker, pending native resources and exact source lease retained until process exit. This one-shot command does not retain a retry handle or claim reusable-host recovery. BrowserSession retirement cancels its snapshot and releases the owner reference without waiting for outstanding leases; it does not deadlock this terminal path. The separate read-only file handles unwind normally, while copied scene resources remain retained by native ownership.

PNG publication uses Shared PngExporter with overwrite:false. Receipt publication uses Shared StagedStreamExport with overwrite:false. Both use their existing temporary-file, cancellation, and final no-replacement behavior. No command cleanup deletes or replaces an existing output.

These are two separate atomic publications, not one transaction. If PNG succeeds and receipt fails or is canceled, the completed PNG remains and the command reports failure. A PNG without a successful receipt is not accepted capture evidence.

The receipt records exact source paths, lengths and raw SHA-256; actual catalog provenance; source ID/generation/reference; original slot offset/size/name; frame/skin/key; scene identity/index; materials, samplers and encoded image hashes; camera/appearance; and final PNG/RGBA hashes. Successful native disposal and unchanged-input flags are written only after those checks succeed. The machine receipt contains no hardcoded build or Shared pin: pair it with externally verified source, assembly and pin records.

## Validation status and next checks

The source-hashed [capture complexity/ownership receipt](complexity/shadowkey-native-capture-20260920.json) records the applied files and authored callable spans. It is source review, not compiler, test, pixel, race or cleanup-fault evidence. Related source reviews cover the [Core catalog](complexity/shadowkey-pack-preview-source-20260920.json) and [native UI](complexity/shadowkey-native-preview-ui-20260920.json); the [GUI workflow note](shadowkey-pack-native-preview.md) lists its separate acceptance gates.

The portable filter FullyQualifiedName~ShadowkeyNativeCaptureCommandTests passes all 19 cases on Windows and Ubuntu WSL, with zero skips. Cases cover invalid selection, real pack versus display-label paths, PNG-only output, canonical options, preservation of existing output files/directories, required/default/explicit options, and actual portable invocation returning 2 without opening missing inputs or creating outputs. All 14 CliLaunchModeTests cases also pass after adding the capture root command; the new row verifies ordinary CLI routing. The [source-bound receipt](validation/shadowkey-native-preview-20260920.md) records the complete 152-case focused batch and two separate accessibility cases. Ubuntu executes the Windows-built portable payload; no Linux source-build claim is made.

The Windows capture checkpoint ran all six planned command cases: fern slot 0/frame 0/skin 0 with key off and on, arrow slot 175/frame 0/skin 0 opaque, empty slot 19, animated slot 18, and a repeat of the existing opaque fern destination. The first three returned 0 with new 512×512 PNG/receipt pairs and successful native disposal. Empty/animated selections returned 2 without outputs; the repeat returned 1 and preserved both existing files. Original inputs and command binaries retained their hashes. Independent review decoded all three PNGs and matched their complete RGBA hashes to the sidecars. Arrow supplies a second renderability case, including its six repeated-position triangles among 19 source triangles; it has no independent pixel-parity result.

For fern, the existing adapter oracle pins 24 nondegenerate triangles, a 32×32 texture with 694 magenta texels, and authored V outside [0,1]. The independent native pixel oracle passes 111 stable opaque samples and 90 stable keyed samples, including 24 keyed holes to background and respectively 88/54 repeat-versus-clamp discriminators. There are zero errors above the fixed four-sRGB-byte tolerance; maximum channel error is 3.1385181496. The oracle independently reads 11,093 original bytes, uses source/camera-defined exclusions and reports every remaining mismatch. These are selected interior-sample checks, not whole-image, raster-edge, original-game or GUI parity. Background is measured at geometry-free corners rather than independently predicted. Full counts, build identity, PNG/receipt hashes and limits are in the [native checkpoint](validation/shadowkey-native-preview-20260920.md) and `TestOutput/shadowkey-native-preview-20260920/fern-pixel-comparison.json`.

Independently rehashed source fixture, without payload copies:

    C:/dev/Multitool/BethesdaMultitool/Sample/Builds/The Elder Scrolls Travels - Shadowkey (2004-10-27, N-Gage - Final)/The Elder Scrolls Travels - Shadowkey/system/apps/6r51

| File | Bytes | Raw SHA-256 |
| --- | ---: | --- |
| models.huge | 4,907,880 | 3b49517651b9edbf78e99660ef0a0f5f9d398146d11eecb7ee12df81c8fbd310 |
| models.idx | 1,900 | 7b67e1d26b3d0037b748622fceb340fb4556bb638490b985b2c4b0f62bf0ae71 |
| models.txt | 6,433 | a595b31863e96777083b6c1294c81c27d7ba4334a6ffb8332162246c96fffb24 |

The repeated destination, empty slot and animated decline now have actual command evidence. Cancellation and injected cleanup failure remain untested native command paths. Capture tests cannot establish tree selection, visible layout, focus, input, same-slot camera preservation, rapid source replacement, inactive-pane behavior, localization, native resource retirement during window close, or HWND/graphics prerequisite retention on failure. Those GUI gates and release acceptance remain open; normal successful offscreen disposal does not prove the terminal cleanup-failure contract.
