# Caption shortcut route B: native caption input, September 22, 2026

Status: accepted by the user on 2026-09-22 on the eighth build ("The ? glyph is smaller than I'd like but
otherwise all looks good"); the glyph size is an open cosmetic follow-up, deliberately not rebuilt now so the
completion program continues. Measured on that instance (PrintWindow of its own window, ink boxes of bright
pixels in the top-right 260x48 strip at scale 1.00): the ? glyph (caption icon font, size 10) inks 6x9 px at
y=20..28, the Maximize and Close glyphs ink 10x10 px at y=19..28, Minimize is a 10x1 px line at y=23, and all
four glyph centers sit on the same 48 px pitch (x=1273.5, 1321.5, 1369.5, 1417.5 in window coordinates), so
centering is right and only the em size differs. A ? occupies about 0.9 em in height and 0.6 em in width,
so size 12 would ink about 7x11 px, matching the caption glyphs' height; that is the value to try when the
next App-only publish happens. This record continues [the retry validation](asset-caption-retry-20260922.md), whose
fourth attempt is the accepted evidence for the asset check crash repair and for the 8-pixel native strip
that fixed the simultaneous help/Minimize hover. The user confirmed that fix ("too far away to both be
highlighted at once now") and reported four cosmetic points on the strip build: the ? glyph is not centered
with the caption glyphs, it renders gray, the hover box sits 8 px from Minimize, and it is 1 px taller than
the caption hover box. Asked whether seamless was possible, the user chose route B from three routes.

Base: BMT `e38d0e3008646046e50d386b38a5ccf4e1d0878d` plus the uncommitted retry sources; Shared
`20e4e2c0fb2d304ed60c05e48d8c97a9a730b684` unchanged. Only `src/BethesdaMultitool/App/MainWindow.xaml`
and `MainWindow.xaml.cs` change relative to the fourth attempt; the freeze in
`TestOutput/asset-caption-retry5-20260922` asserts exactly that delta.

## Why a strip was needed and why route B removes it

The shared shell places the shortcut in the WinUI TitleBar's `RightHeader`, and the TitleBar registers
that whole presenter as a native passthrough rectangle on every layout pass. Pointer input inside a
passthrough rectangle goes to the XAML island; the system caption buttons learn that the pointer left
them only through a non-client leave notification that the passthrough boundary suppresses. A native
drag strip between the two regions gives the system one pointer report in its own territory, which is why
the 8-pixel strip works and a one-pixel strip only works when a report happens to land on it.

Route B removes the passthrough region entirely. The shortcut is no longer inside the TitleBar; it is an
overlay in the window's root grid, positioned over the TitleBar's minimum-drag column beside the system
buttons, with `IsHitTestVisible="False"`. That area is part of the caption region the shell already
declares through `Window.SetTitleBar`, so the system owns hover and clicks there exactly as it does for
Minimize, Maximize and Close, and the two share one native hover state machine. The window's
`InputNonClientPointerSource` reports enter, move, exit, press, release and caption tap for that region;
the application mirrors the hover and pressed visual states onto the button and opens the shortcuts
dialog on a caption tap inside its bounds. Keyboard focus, Space and Enter, F1 and UI Automation still use
the ordinary button and its Click handler.

Known consequences of living in the caption region, accepted by the user when choosing this route:
a double-click on the ? is a caption double-click and toggles maximize; a press-and-drag on the ? moves
the window (the pending press is cancelled when the move starts); and the hover tooltip, which needs
XAML pointer events, no longer appears on hover.

## What changed

- `MainWindow.xaml`: the window content is a Grid holding the shell and the overlay button. The button
  keeps its automation name, help text, tooltip resource, Click handler and focus visuals; its glyph is
  the caption icon font at size 10 (`SymbolThemeFontFamily`, which resolves to Segoe Fluent Icons on
  Windows 11). `TitleBarMinDragRegionWidth` is 48 so title content never runs under the overlay.
- `MainWindow.xaml.cs`: the layout callback sizes the overlay from the native caption metrics (width =
  one caption button, height = caption height minus one physical pixel, margin = the system buttons'
  inset on the flow-direction side) and keeps the TitleBar's minimum-drag column equal to that width.
  `AttachCaptionShortcutInput` subscribes to the native pointer source; hover is derived from the
  reported point against the button's physical bounds; the press and release events drive the Pressed
  state; `EnteringMoveSize` cancels a press; `CaptionTapped` inside the bounds opens the dialog. The
  foreground brush is assigned directly (the style had already resolved its normal foreground when the
  lightweight overrides were added). The verbose diagnostics keep their log formats: "Help pointer
  entered/exited" lines are now derived from native caption events, so the existing analyzer applies.

Symbols verified in `Microsoft.InteractiveExperiences.Projection` 2.1.6 before the freeze: `PointerMoved`,
`PointerPressed`, `PointerReleased`, `CaptionTapped` (`NonClientCaptionTappedEventArgs.Point`) and
`EnteringMoveSize` (`EnteringMoveSizeEventArgs`).

## Evidence

- Freeze (`windows-asset-caption-retry5-source-freeze.json`): 48 source inputs, 10 helpers; the delta from
  the fourth attempt's verified portable freeze is exactly `App/MainWindow.xaml` and `App/MainWindow.xaml.cs`.
- Accessibility (`run-accessibility-only.ps1`, the fourth attempt's Windows-built portable payload, hashes
  checked before and after): Windows 2 of 2 passed, 0 skipped; WSL (`/tmp` on ext4) 2 of 2 passed, 0 skipped.
- Windows publication (`run-windows-ui-publish.py`, same settings and admission ruling as the fourth
  attempt): exit 0 in 801 s, no error, seven warning lines: the six inherited ones (three WIN2D0001,
  CA1068, S3877, the reviewed S3264) plus one new S3358 at `MainWindow.xaml.cs(450,70)`, a nested ternary
  in the hover-state line. That is a behavior-neutral cleanup, deferred until after the user's review so it
  can ride with any change that review asks for; it is recorded as open until then. Published executable
  `publish/windows-asset-caption-retry5/BethesdaMultitool.exe`, 121,524,645 bytes, SHA-256
  `4ea9572225765186bf9f3a2c9e80ee7d6f8835e854b66baf242627aa7ac0cc98`.
- Seven PDB audits passed (BethesdaMultitool 3,101 verified documents with 142 virtual generated recorded
  separately; Core 237/17; Media 77; WinUI 119/32; WinUI.Direct3D12 82/3; SharpGLTF.Core 118; DDXConv 21).
  Publish correspondence binds 48 frozen inputs, fourteen producer/consumer groups and the published
  executable hash. The published-output audit passed with no issues over 56 files; the actual
  `--no-gui --help` returned 0 in 2.82 s with the accepted 4,101-byte help text and unchanged bytes.
- Candidate crash replay (`replay-candidate-r5.json`) and startup observation
  (`startup-and-caption-regions-r5.json`), both with verbose diagnostics: **both instances crashed** in the
  layout callback with `NullReferenceException` in `LogCaptionInputRegion`, exit code 0xC000041D. Cause:
  with route B no Passthrough rectangle is registered, and `InputNonClientPointerSource.GetRegionRects`
  returns null for an empty kind through the projection (the pre-freeze review had asserted an empty array;
  the crash refutes it). Only verbose runs execute that diagnostic. Before the crash the observation logged
  the geometry route B intends: shortcut 1242..1290 DIP against native content 0..1290 (gap 0), the
  min-drag column 48, and the Minimize rectangle `x=1290, width=48, height=48`. The fifth attempt's
  publication is therefore not the reviewed artifact.
- Sixth attempt (`TestOutput/asset-caption-retry6-20260922`): null guards for the region rectangles and
  `ChangedRegions`, plus the S3358 cleanup. Freeze: 48 inputs, 9 helpers, delta exactly the two App files.
  Accessibility on the fourth attempt's portable payload: Windows 2 of 2, WSL 2 of 2, no skips. Windows
  publication: exit 0 in 620 s, no error, exactly the six inherited warning lines (the S3358 line is gone).
  Published executable `publish/windows-asset-caption-retry6/BethesdaMultitool.exe`, SHA-256
  `ffe6aa74dc4bb5744948cf5270cfa6934034a2c29a383ac83b457bb813f8f364`. Seven PDB audits passed (the same
  document counts as the fifth attempt); correspondence binds 48 frozen inputs, fourteen producer/consumer
  groups and that hash; the published-output audit passed over 56 files with the actual `--no-gui --help`
  returning 0 in 2.84 s and unchanged bytes. Candidate crash replay (`replay-candidate-r6.json`): every one
  of the twelve replayed actions survived with consistent, expected tree and gallery states, exit code 0.
  Startup observation with verbose diagnostics (`startup-and-caption-regions-r6.json`, process 39136): no
  crash, startup smoke passed, the shortcut arranged at 1242..1290 DIP against native content 0..1290 (gap
  0), Minimize registered at x=1290 width 48 height 48 after the Tall transition, no Passthrough rectangle.
  The window was left open for review and closed by this session before the seventh attempt's launch.
- Independent review of the frozen route B code (two lenses, refuted per finding) found no compile fault
  and one verified major runtime finding: `EnteringMoveSize` is raised for every caption left-press
  (WM_NCLBUTTONDOWN), not when a drag starts, so the pressed visual either never shows or can persist after
  a click, and nothing resets a press on `CaptionTapped` or `ExitedMoveSize`; a touch tap can leave the
  hover state on. This affects the pressed and hover visuals only; the tap still opens the dialog. It is
  fixed in the seventh attempt together with press/release/tap diagnostics that pin the real event order
  from the observation log. The review's smaller findings taken into the same attempt: the right-to-left
  branch double-mirrored the overlay (alignment stays at the logical end, only the inset source swaps); the
  overlay was re-measured only from XAML layout (now also, queued, from the native region-change event);
  the Caption rectangle and the overlay's own physical bounds were never logged; the region-change budget
  was spent by the eight startup registrations; the release and move-size handlers lacked the closing
  guard; the overlay was last in tab order; the drag-region recompute in the layout callback was
  redundant. Refuted by the review: none of the orderings changes the build, geometry, stability, hover
  detection or the click outcome, so the sixth attempt's evidence stands for those.
- Seventh attempt (`TestOutput/asset-caption-retry7-20260922`): see its README for the change list;
  every native callback now runs on the UI thread (queued when raised elsewhere, with the raising thread
  recorded in the new "Caption input" diagnostic lines) because the input source's thread affinity is
  not documented. Freeze: 48 inputs, 9 helpers, delta exactly the two App files. Accessibility on the
  fourth attempt's portable payload: Windows 2 of 2, WSL 2 of 2, no skips. Windows publication: exit 0 in
  586 s, no error, exactly the six inherited warning lines, no copy-lock line. Published executable
  `publish/windows-asset-caption-retry7/BethesdaMultitool.exe`, SHA-256
  `35ea97fe43c8a146aa8ef71475a71147493bfa652f7fe3e7446d25720448e394`. Seven PDB audits passed with the
  same document counts; correspondence binds 48 frozen inputs, fourteen producer/consumer groups and that
  hash; the published-output audit passed with the actual `--no-gui --help` returning 0 in 2.67 s and
  unchanged bytes. Candidate crash replay (`replay-candidate-r7.json`): all twelve actions survived with
  consistent, expected states, no error line, no new dump, exit 0. Startup observation with verbose
  diagnostics (`startup-and-caption-regions-r7.json`, process 32452): startup smoke passed, the shortcut
  arranged at 1242..1290 DIP (gap 0), Minimize at x=1290 width 48 height 48, no Passthrough rectangle, and
  the two new lines the review asked for: the Caption rectangle `x=0, y=0, width=1434, height=48` and the
  overlay's physical bounds `x=1242, y=0, width=48, height=47`, which the analyzer confirms lie inside it
  (`shortcutInsideCaption: true`). **That review instance then crashed while idle** (dump
  `BethesdaMultitool.exe.32452.dmp`, 1,005,562,978 bytes, written 14:07:24 local; no `[CRASH]` or `[ERR]`
  line, no pointer, press or tap event in its log, no Application-log or WER record). Its last log line,
  at 14:07:15, one second after a native Minimize region change (the only activity since a matching change
  at 14:04:14), is `Refresh caption padding: ... right=144.00->-8.00`: the queued native-metric refresh read
  a right inset of -8 physical pixels, most plausibly while the window was being restored, and applied a
  negative `GridLength`. **Proven from the dump** (`dotnet-dump analyze`, `dumpheap -type
  System.ArgumentException` then `pe`): the heap holds exactly one `System.ArgumentException`, "Invalid
  argument." (HRESULT 0x80070057), thrown by `Microsoft.UI.Xaml.GridLength..ctor(Double, GridUnitType)`
  from `MainWindow.UpdateCaptionInsets` from `MainWindow.UpdateCaptionShortcutGeometry`, invoked through
  `ABI.Microsoft.UI.Dispatching.DispatcherQueueHandler.Do_Abi_Invoke`: the queued native-metric refresh.
  A dispatcher-queue callback's exception is not routed through `Application.UnhandledException` (hence no
  `[CRASH]` line); the ABI boundary turned it into a failed HRESULT and the process was terminated. The
  seventh attempt's native-metric refresh (a review item) made that transient reachable outside a XAML
  layout pass; the arithmetic was equally unguarded in every earlier attempt. The dump is preserved.
- Eighth attempt (`TestOutput/asset-caption-retry8-20260922`): the geometry refresh skips a minimized
  presenter and any pass whose scale, caption height or insets are unusable (logged as "Skipped caption
  metrics", at most eight times), the padding refresh refuses a negative or non-finite width, and the queued
  refresh catches an argument or state failure and logs a warning instead of ending the process. Its
  helpers were rebound from `e38d0e30` to the checkpoint `b34ac0e7` before the freeze (recorded in its
  `recipe-derivation.json`; the source hashes it compares are unaffected). Freeze: 48 inputs, 9 helpers,
  delta exactly the two App files. Accessibility: Windows 2 of 2, WSL 2 of 2, no skips. Windows publication:
  exit 0 in 510 s, no error, exactly the six inherited warning lines, no copy-lock line. Published executable
  `publish/windows-asset-caption-retry8/BethesdaMultitool.exe`, SHA-256
  `d4e60419648ea227ca04f91c0a48243d4c96dfffc95ba1bbe54c93f7f71c4361`. Seven PDB audits passed with the same
  document counts; correspondence binds 48 frozen inputs, fourteen producer/consumer groups and that hash;
  the published-output audit passed with the actual `--no-gui --help` returning 0 in 2.23 s and unchanged
  bytes. Candidate crash replay (`replay-candidate-r8.json`): all twelve actions survived with consistent,
  expected states, exit 0. Startup observation with verbose diagnostics (`startup-and-caption-regions-r8.json`,
  process 67532): startup smoke passed, the shortcut arranged at 1242..1290 DIP (gap 0), Minimize at x=1290
  width 48 height 48, no Passthrough rectangle, overlay physical bounds `x=1242, y=0, width=48, height=47`.
  The window is open for the user's review. Not exercised by this session: hover, press, tap, drag,
  minimize/restore (the seventh attempt's crash path; the guard now logs "Skipped caption metrics" when
  it fires) and the rendered look, all of which the user's review and `analyze-hover-log.ps1 -ProcessId
  67532` cover afterwards.
