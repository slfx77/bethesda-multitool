# Caption hitbox correction — September 21, 2026

Subsequent manual review **failed**: the user still observed simultaneous help
and minimize hover, leaving minimize highlighted. PID 63888 also crashed while
checking assets. The geometry and startup evidence below remains historical;
it does not establish pointer correctness. See the
[follow-up investigation](asset-caption-regression-20260921.md).

Status: normal Windows publication, artifact checks, actual CLI help and GUI
startup passed. Runtime measurements confirmed and corrected a six-pixel caption
reservation deficit. Visible hover/click acceptance remains pending.

This follow-up starts from BMT `12498100c703c892463a7f59e9fe22a8d6ea7e64`
and retains Shared `20e4e2c0fb2d304ed60c05e48d8c97a9a730b684`.
The [prior UI validation](ui-feedback-20260921.md) records the earlier corrections,
startup failures, successful normal-artifact startup and remaining GUI scope.

## Regression and correction

The user reported overlap between the shortcut and native caption controls after
the preceding UI changes. Source review of the pinned TitleBar template and
official implementation suggested stale caption padding. Runtime diagnostics
confirmed a 138-pixel reservation while the current native inset was 144 pixels.
The likely initial Standard-to-Tall transition is a source-based inference; that
transition itself was not captured. The deficit and corrected boundary were
measured in the actual published application.

Only [MainWindow.xaml.cs](../../src/BethesdaMultitool/App/MainWindow.xaml.cs)
changes. Its existing layout callback checks the exact `PART_LayoutRoot` Grid
and its own `LeftPaddingColumn`/`RightPaddingColumn` definitions. It updates those
reservations from current native insets, dividing physical pixels by the current
XAML rasterization scale and respecting flow direction. It continues sizing the
shortcut from the retained three-button caption metrics.

A changed size or inset marks layout pending. On the following stable layout
callback, a typed `TitleBar` reference calls `RecomputeDragRegions()` after the
relocated header has been arranged. Verbose diagnostics record changed padding,
arranged shortcut bounds, native content bounds and scale for runtime comparison.
This preserves the existing caption colors, owned brushes, localization and
window/native resource lifetime. No Shared, XAML, resource, portable Core,
project or test source changes are included.

## Evidence and current limits

The new source freeze verifies 41 source inputs and nine new recipe helpers.
Exactly `App/MainWindow.xaml.cs` differs from the verified brush-publication
baseline; the other forty source hashes and all ten original baseline helper
hashes match. Its SHA-256 is
`b1f9ba155a7821f8459458ba53117cdbe4013fb26efed9b299137d7295259f55`.
The changed file's LF-normalized SHA-256 is
`45b88628e5ab052e6783212b73c6878d896a2d69d70351448f67435ae274d660`.

The earlier portable 39 focused cases plus two accessibility checks passed on
both Windows and WSL. They have not been rerun for this App-code-only change.
The unchanged project excludes `App/**/*.cs` from portable compilation; the
changed file is absent from the accepted portable PDB documents, and the accepted
test payload remains byte-identical. This preserves that earlier evidence without
claiming it exercises the caption implementation.

The normal-analyzer publication exited 0 in 777.82 seconds, including its final
source/helper guard. It used locked restore, one MSBuild node, a 12 GiB compiler
heap cap and default 4 GiB coordinator admission. The six existing warning lines
remain (WIN2D0001 three times, CA1068, S3877 and the reviewed S3264); no new warning
or compiler error was reported. Seven PDB audits and fourteen producer/consumer
groups passed; the app verified 3,101 source and 142 virtual generated documents.

The published audit passed: 901 bundle entries, 56 physical files, 260 runtime
assets, 46 scoped notices and 42 dependency rows. Actual published CLI help
returned 0 in 2.512 seconds; published bytes stayed unchanged. Evidence is under
`TestOutput/caption-hitboxes-20260921`; its published-audit SHA-256 is
`304ea89a815638e98ebf41034722e3db4b19b5867bbc1f9e520c90c87bc7b154`.
The executable at `publish/windows-caption-hitboxes/BethesdaMultitool.exe` has
SHA-256 `dadc02b0a74daa76580aad764cc5c897bb66f3471b98596efcdee05b91dff4d3`.

## Actual startup and measured boundary

The normal published GUI started successfully as PID 63888, responding with title
`Bethesda Multitool` and main-window handle 17894822. Constructor and activation
completed without errors. Existing verbose diagnostics were enabled only for
this launched process. At scale 1.00, the recorded correction was:

```text
Refresh caption padding: left=0.00->0.00, right=138.00->144.00, scale=1.00.
Arranged shortcut: left=1242.00, right=1290.00, native content bounds=0.00..1290.00, scale=1.00.
```

The shortcut's right edge meets the native caption boundary exactly: zero-pixel
gap and no measured overlap. The observation checks positive finite geometry,
containment and adjacency within one physical pixel. It binds the launched
executable to the publication audit and verifies its bytes remain unchanged.
`TestOutput/caption-hitboxes-observation-20260921/startup-and-caption-layout.json`
has SHA-256 `1f4daba7b0ac195ab83eca45af06220a5e08b35caa89c5b65b4aba4d640a9b42`.
Live-log hashes cover the exact captured prefixes, which may later gain content.
No BMT process existed immediately before launch; the earlier PID 22124 had
exited without an observed cause, and this session did not close it.

The user was asked to check the new window's adjacent hover highlights; no answer
had arrived when this record was saved. Pointer input, dragging, native caption
commands, keyboard focus and other DPI/theme configurations remain unverified.
Computer Use still reports the missing helper pipe, so no automated screenshot
or pointer acceptance is claimed. The broader completion program remains active.
