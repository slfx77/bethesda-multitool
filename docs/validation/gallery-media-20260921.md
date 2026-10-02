# Gallery, thumbnail persistence and FLC adapter validation

This batch follows BMT `5caf8ee36acfcbdff0668743c1fe27c2c74c9ceb` and deliberately
adopts Shared `20e4e2c0fb2d304ed60c05e48d8c97a9a730b684`. It applies the reviewed
image gallery, real thumbnail cache observation, DDS/DDX persistence and clearing,
and bounded FLC decoded adapter. Classic-video dispatch and timers are unchanged.
The [gallery/cache note](../thumbnail-gallery-cache-adoption.md) and
[FLC adapter note](../flc-decoded-media-adapter.md) describe their limits.

Before compilation, all 21 changed source/test/resource files matched the final
reviewed candidate composition after newline normalization. The frozen manifest
includes those files and the application project plus both lock profiles: 24
inputs in total. Its SHA256 is
`634d4b5eabda5eead741dfea1d18f974f35ead62ecd19816e7101c4c6774abad`.

The first portable build was interrupted without a completion receipt. On resume,
its process handle was absent and the previously identified launcher/build/compiler
processes were gone. Its incomplete log and binlog are preserved; it establishes
no build or test success. The source freeze remained unchanged. A fresh retry uses
the same coordinated, isolated, analyzer-enabled build with locked restore,
one MSBuild node, four-GB admission and a 12-GB compiler heap cap.

The focused set passed all 424 cases: 321 earlier resolver/material/scene cases,
13 gallery/observation, 45 FLC parser/adapter, two bounded original FLC rows,
13 DDS persistence/clear cases and 30 affected scaler/cache/export/sprite cases.
Two accessibility checks also passed separately on both Windows and WSL. Actual
JUnit nodes confirm zero failures, errors or skips in all four runs. The FLC
originals and all 105 expected frame hashes are bound to the earlier
verified legacy baseline; they do not establish an independent engine oracle.

The retry completed in 16:18.52 with 103 warnings and zero errors. Six warnings
are in this batch's changed code/tests: cancellation-token ordering, an explicit
observer retirement precondition, one constant test array and three ValueTask
test usages. Their corrections require the next build; the other 97 warnings
are inherited. The app PDB verified 2,795 available source documents and separately
identified 93 virtual generated documents; the test PDB verified 1,542 documents.
All 24 frozen inputs and five dependency DLL/PDB pairs matched the actual test
payload. Windows and WSL used those same Windows-built assemblies on .NET 10.0.12;
this is not a new Linux source build. WSL synthetic fixtures used ext4 `/tmp`.

Build correspondence SHA256:
`362fba410db65a161f055d67903e50517c147ac85400bea04ead6cdfadf9f9d5`.
The compact actual JUnit counts and individual report hashes are preserved in
`TestOutput/windows-ui-adoption-20260921/actual-runtime-acceptance.json`.

The current Windows GUI build and local publish completed successfully in
833.73 seconds. Seven project PDB checks passed, including 3,099 available app
documents and 142 separately identified virtual generated app documents. All
24 source inputs and 14 producer/consumer DLL/PDB groups matched. Two application
warnings remain the same retirement/token-order findings above; existing Win2D
platform warnings also remain. No compilation error was reported.

The published executable SHA256 is
`7c87e860ddd395bb09fac078b0ecd2b276516357dd1b02c5667532b69f036b28`.
Its actual `--no-gui --help` invocation passed. The bundle/runtime audit passed
with 56 published files, 260 runtime asset correspondence entries and 46 readable,
source-identical project notices (including BMT's two). This closes the earlier
44 embedded-only Shared notice delivery gap for this Windows bundle. The Magick
notice check also passed; complete licensing and release acceptance remain separate.
Windows correspondence SHA256:
`2e3db6e5fb06795f342d1ef9fc39ab84764c2d740275031b83cbeddaf6f4b016`;
audit SHA256: `bd04410714a07cdae8fd83cfb339598b60a4a11a4d1eb24dfb55e464c7d9ef13`.

The user redirected work to restoring desktop control. The missing pipe occurs
before application selection: the desktop host was absent and this VS Code
session's MCP server retained its old configured address. The user started the
desktop plugin successfully and its saved address changed, but a fresh JavaScript
kernel still cannot connect. The documented extension restart remains pending;
no manual helper launch or guessed address change was made. Interactive gallery,
localization, replacement/cancellation,
shutdown, cache pressure and playback checks remain open. Native FLC consumer
work is a separate prerequisite and has not changed production dispatch.

Local evidence is under `TestOutput/windows-ui-adoption-20260921`; the restarted
build uses `portable-ui20-retry1-build` filenames and preserves the original
`portable-ui20-build` files. The earlier current-source Linux compile/publish
result remains bound to Shared `88326240` in the
[NIF/EGM validation note](nif-egm-linux-20260921.md).
