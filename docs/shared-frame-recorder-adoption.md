# Shared native frame recorder adoption

Bethesda implementation checkpoints `a7e03df9` and `f317eae1` replace the native
recording implementation with the Shared `NativeFrameRecorder`, pinned at
`9e8aff2c93ea5dedefb3ace11d4ebefd3c97a602`. The verified application source is
`f317eae1b99270bce45d5b65894b0328d48ab6d1` in
`C:\dev\Multitool-worktrees\bmt-material-preparation-20260912`.

`GpuCommandRecorder12` remains the application adapter. Shared owns allocator
rotation, command submission, monotonic fences and retained resource retirement.
Bethesda retains its unlimited fence-wait policy, diagnostics, synchronous
disposal when no recording is open, conditional capture ownership and terminal
device-removal handling. Failed initialization stays owned by `GpuDevice12` until
cleanup succeeds. The duplicate conditional-lifetime implementation is removed;
its ownership regressions now test the Shared helper.

## Verified acceptance

Receipts are retained under
`C:\dev\Multitool\Multitool.Shared\TestOutput\archive-waveform-20260914`.
All three completed BMT runs used the exact source/pin above and recorded clean
source status before and after execution.

| Receipt | Result |
| --- | --- |
| `BmtFrameRecorderPortable02` | Portable Release build passes: 1,248 warnings, zero errors. |
| `BmtFrameRecorderTests01` | 36 selected adapter, WARP and source-contract cases pass; zero failures or skips; 5.877 seconds. GPU and shader tests were explicitly enabled. |
| `BmtFrameRecorderWindows01` | x64 Windows Release build passes: 846 warnings, zero errors; compilation took 20 minutes 39.82 seconds. |

The build logs have no diagnostics in this component's eight changed source/test
paths. The Windows build does not compile the test paths. No new BMT analyzer
suppression was introduced; the adapter keeps the existing narrow RCS1075
exception containing diagnostic-sink failures after established ownership.
Repository-wide warnings remain outstanding.

The test receipt's `payload.json` contains post-run application, test, Core and
Shaders hashes. The Windows receipt contains separate post-build executable,
application, Core and Shaders hashes from the exact x64 output reported in its
log. PE metadata in both application assemblies reports
`1.0.0+f317eae1b99270bce45d5b65894b0328d48ab6d1`. These metadata/hash reads do not
execute the application or claim a publish or GUI startup check.

`BmtFrameRecorderPortable01` was intentionally stopped before completion to
preserve Bethesda's wait policy. That receipt remains historical; it is not a
runtime failure or a completed build.

## Canonical review copy and remaining work

`BmtFrameRecorderCanonicalMirror01/result.json` records the earlier source-only
mirror into `C:\dev\Multitool\BethesdaMultitool`: eight component paths and the
exact Shared pin were synchronized after the independent workers ended. The
`GpuDevice12` hunk merge preserves the independent sample-count changes; the
outer index/staged diff and three existing local Blender test edits were retained.
No binary output was changed. Its original receipt still accurately states that
the isolated Windows build was running at mirror time; the later result above
does not rewrite that history.

The dirty canonical checkout is not identical to the verified isolated payload.
This note does not modify or resynchronize that checkout. Actual BMT GUI startup,
world/capture interaction for this increment, representative workload performance,
private-corpus coverage, and release/platform acceptance remain open. The broader
Shared modernization program is not completed by this recorder migration.
