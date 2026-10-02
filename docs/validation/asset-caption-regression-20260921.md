# Asset check crash and caption hover follow-up — September 21, 2026

Status: implementation and validation in progress. The preceding caption build
passed startup and geometry checks but **failed the user's hover review**.
No interactive acceptance is claimed for this follow-up yet.

Base: BMT `e38d0e3008646046e50d386b38a5ccf4e1d0878d`; retained Shared
`20e4e2c0fb2d304ed60c05e48d8c97a9a730b684`. All broader completion-program
gates and preservation requirements remain active.

## Crash evidence and repair

The user's Steam-disk asset browsing session was PID 63888. Its original dump
remains at `%LOCALAPPDATA%/CrashDumps/BethesdaMultitool.exe.63888.dmp`
(1,038,980,023 bytes, last written 2026-09-22 00:02:47.4386011 UTC).
No dump was uploaded, copied or removed. Windows Error Reporting identifies
the reviewed `caption-hitboxes-20260921` published executable and exception
`80131509`; report ID `06045616-33d5-400c-ad63-bec8eb70d564`.

`%TEMP%/BethesdaMultitool-gui-63888.log` records the managed exception at
19:02:27 local time: `Selection cannot be changed during an operation or its
notification.` The stack establishes this sequence:

1. The tree checkbox calls `AssetTreeSelection.SetChecked`.
2. Shared commits the selection and publishes `Changed`.
3. BMT mirrors the check to `AssetNode`; the gallery tile notifies its binding.
4. The native two-way checkbox binding writes the same value back through
   `AssetGalleryItem` and `AssetGalleryProjection`.
5. Shared rejects the nested selection operation while its notification runs.

`AssetGalleryItem.IsChecked` now returns before command or notification when
the requested value already equals its mirror. The portable gallery projection
also recognizes unchanged commands using the selection owner's committed state,
after verifying current source and exact folder membership. That authoritative
state matters because the notification may not yet have updated every node's
display mirror. Real nested changes still reach Shared's existing guard.

Three regression cases exercise the real selection owner and node notifications:
tree and gallery changes including a hidden row and partial parent selection;
feedback before later node mirrors update; and rejection of a conflicting nested
change followed by a successful ordinary change. Existing replacement, foreign
source and disposal cases also reject unchanged commands from stale rows.

## Caption input

The prior layout fix corrected a measured six-pixel native padding deficit.
It left the help passthrough region meeting the native caption boundary exactly.
The user's subsequent simultaneous hover and stuck minimize highlight show that
this measurement was insufficient to establish correct pointer transitions.

The new correction retains one physical pixel of the existing title-bar drag
spacer between the help presenter and native caption input. The construction
fallback is one DIP; settled layout adjusts it to `1 / RasterizationScale`,
alongside the retained native inset correction. It does not replace caption
buttons or install a second owner for the title bar's passthrough rectangles.

Verbose diagnostics now record native minimize/passthrough rectangles after up
to eight settled layouts and up to 64 help/native pointer entry or exit events.
These handlers observe events without handling or capturing them. The next
review run can therefore provide input-transition evidence if hover still fails.
This is a candidate input correction; geometry alone cannot establish its success.

Shared source and pins are unchanged. Independent source reviews found no
remaining blocker in either correction; installed native API signatures, the
pinned template, syntax and whitespace checks were reviewed.

## Validation admission failed; September 22 handoff

The source freeze contains 48 inputs and 15 validation helpers under
`TestOutput/asset-caption-regression-20260921`. Its pinned portable build was
queued at approximately 19:15 local time on September 21. It subsequently exited
1 after 2702.295 seconds with `Timed out waiting for build admission.` It never
started compilation. During earlier observation, available RAM had ranged from
1.18 to 2.69 GiB, below the default 4 GiB admission threshold.
The user was asked whether they could close unused applications; no other
session's processes were stopped and the threshold was not reduced.

The failed portable wrapper had a 45-minute admission timeout. Preserve its log
and command receipt; its launcher opens the log exclusively and cannot be rerun
against the same evidence path. Prepare a fresh attempt with consistent helper
paths and a new source freeze. After successful admission/build, the saved README
orders PDB checks, 19 focused cases plus two
accessibility checks on Windows and WSL, then one normal-analyzer Windows
publication and its audit. None of those execution results is claimed yet.
`TestOutput/asset-caption-observation-20260921/launch-and-observe.ps1` is prepared
for the fresh GUI after that audit; it will preserve any previous launch logs.
Actual checkbox replay and pointer acceptance remain separate open checks.

The source freeze was verified again at handoff. No owned asset-caption build
or GUI process was found running. Full continuation instructions and scope:
[September 22 goal and UI handoff](../session-handoff-20260922.md).
