# Classic media adoption prerequisites

This September 20, 2026 source review supplies BMT requirements for the Shared
owner's existing [decoded-session and native-bridge design][design]. It proposes
neither a competing contract nor an application timer replacement. No decoder,
native playback, corpus or release acceptance was performed for this document.

The Shared owner's later checkpoint `fd4fc9a` implements decoded track/session
ownership and the native bridge described in
[decoded media playback](C:/dev/Multitool/Multitool.Shared/.worktrees/media-process-v1/docs/decoded-media-playback.md).
Its recorded synthetic native probes cover replay, overlapping seeks, pause,
departure, replacement and shutdown. BMT remains pinned to `88326240`; no classic
codec adapter or timer replacement has adopted that checkpoint. The requirements
below remain the consumer acceptance criteria. Native buffers retained after a
seek add memory beyond active-generation reservations, and track changes require
a newly prepared input in that implementation.

## Existing capabilities and limits

| Family and source | Timing, geometry and palette behavior to retain | Audio capability and admission limits |
| --- | --- | --- |
| Arena FLC/CEL: [FlicFile][flic] | Header speed in milliseconds; persistent indexed canvas with per-frame palette; unchanged and palette-only blocks still occupy display time. The reader removes the trailing loop-back frame and ignores the CEL authoring prefix. | No audio track is exposed by this reader. Its implemented branch is 8-bit FLC; FLI is explicitly rejected. Frame materialization at open is existing behavior, not evidence of bounded streaming. |
| Daggerfall VID: [DaggerfallVidFile][vid] | Each displayed frame retains its own raw `Delay`, canvas snapshot and current VGA palette. Both 256- and 320-pixel-wide retail canvases are covered by existing tests. | Audio start/incremental blocks yield concatenated 11,025 Hz unsigned 8-bit mono PCM. The accepted audio-rate byte is 166. Raw delay interpretation and audio/video origin must be verified before deriving synchronized timestamps. |
| Battlespire/Redguard Smacker: [SmackerFile][smk], [SmackerVideoClip][smkclip] | Signed frame-rate field: positive milliseconds, negative units of 1/100,000 second; zero currently falls back to 10 fps. Playable duration excludes the ring frame. Palette state survives deltas and replay. Display height doubles stored height for Y-doubled/interlaced flags; the current clip repeats rows. | Seven header-indexed track slots distinguish present and absent tracks. [SmackerAudioDecoder][smkaudio] implements raw PCM and Huffman/DPCM, currently materializing a whole selected track. Existing retail tests concern SMK2; accepting SMK4 in the probe does not prove its decoding. |
| Tactics/Brotherhood of Steel/Van Buren Bink: [BinkFile][bink], [BinkVideoDecoder][binkdecode] | Preserve rational rate dividend/divisor and original dimensions. Sequential decoding advances state; repositioning replays from a preceding keyframe. `.mve` can contain Bink, so admission remains content-based. | Implemented video admission is BIKi without grayscale or alpha planes. Every audio track is metadata-only: rate, channels, depth, descriptor and native ID are exposed, but audio is not decoded. `HasDecoder` describes a historical DLL flag, not an implemented BMT capability. Xbox BOS evidence does not establish PS2 coverage. |
| Fallout 1/2 Interplay MVE: [InterplayMveDecoder][mvedecode], [InterplayMveVideoClip][mveclip] | Presentation uses send-buffer display ticks, including repeated frames and initial blank ticks, rather than decode count. Timer fields yield rate × subdivision / 1,000,000 seconds. Preserve palette/map state and distinguish decoded buffer dimensions from [container screen dimensions][mvefile]. | Raw PCM and DPCM decoding exist, including silence opcodes and stream-mask selection; default mask is bit 0. PCM is currently materialized. Video admission requires an 8-bit buffer and rejects video-data opcode versions below 3; this gate is not proof of every later revision. |

## Current GUI and reader gaps

[IVideoFrameSource and ClassicVideoClip][clip] expose only dimensions, frame count,
constant `SecondsPerFrame` and synchronous `GetFrame`. In particular, `FromVid`
discards each frame's `Delay`, retains only pixels/palette, and uses
`GlobalDelay / 1000` or a 15 fps fallback. Existing parser tests prove raw delay
values, not that GUI conversion's units. A new adapter must use the format reader
and establish timing semantics rather than promote this approximation to authority.

[AssetVideoPreview][preview] plays **video only**: a dispatcher timer decodes on the
UI thread, updates one bitmap, loops at the end, and seeks by rounded frame
fraction. It initially displays frame zero while paused; Stop resets to zero;
recognized decode failures stop playback and become visible errors. None of the
decoded VID, Smacker or MVE audio is attached to this transport. Bink's audio must
remain visibly unsupported, distinct from a genuinely absent soundtrack.

[AssetBrowserTab][tab] stops/resets video when its transport becomes inactive,
disposes the timer and detaches the bitmap on replacement/source close, and keeps
native standalone audio separate. [RunPreviewAsync][explore] holds a source lease
until preparation returns and rejects obsolete results. Opening reads the whole
compressed payload; FLC/VID additionally materialize frames. Clip interfaces do
not own asynchronous disposal, and decoder replay loops lack cancellation
checkpoints. These limits must remain explicit during adaptation.

MVE has two further representational gaps: construction retains only the final
timer value, and display ticks retain decoded-frame indices without independent
palette snapshots. Timer changes or palette-only updates between repeated displays
therefore need targeted interpretation/tests. Initial blank ticks currently return
zero RGBA, including zero alpha; native presentation must verify the intended black
preroll. These are source-review findings, not observed retail regressions.

## Requirements for the existing Shared design

- Describe track occurrence identity, native index/ID, codec and verified game,
  platform and revision coverage. Preserve supported/absent/unsupported status and
  reason. Leave unknown language, pixel aspect and channel labels unknown.
- Carry stored/display geometry, timeline origin, nullable duration and exact
  sample timestamps/durations. Derive audio time from integer sample-frame counts;
  retain variable video durations and repeated displays without cumulative
  floating-point drift. Keep format interpretation in BMT.
- Use the planned coordinated cancellable seek with an actual aligned result and
  explicit none/replay/index capability. Serialize mutable decoder state; newer
  requests cancel old work and reject stale samples. Preserve palette preroll and
  selected audio position. Do not run backward replay synchronously on the UI.
- Extend existing [IVideoSource][ivideo]/[IAudioSource][iaudio] through the owning
  session already planned by Shared. `IVideoSource.SeekAsync` currently returns no
  actual position; `IAudioSource` is a sequential float-PCM cursor without a
  timestamp/seek contract. [VideoFrame][frame] pixels expire at the next read, so
  the bridge needs bounded owned/retained samples through native consumption.
- Reuse [PreparedMediaInput][prepared] and [NativeMediaSession][native] for native
  ownership. Drain canceled reads/seeks, request deferrals and in-flight sample
  owners before decoder/source release. Independent bounded PCM cursors prepare
  waveforms without seeking playback. Whole-track PCM helpers are not streaming
  acceptance, and waveform preparation must not gate playable media.
- Reuse NMT [Vid1MediaSource][nmt] as a donor for bounded queues, sample ownership,
  preroll and seek generations after separating its codec state. Its constant-rate
  assumptions, whole-track PCM and timed worker join are not BMT guarantees. The
  common bridge belongs to Shared WinUI; BMT's format code and clean-room notices
  remain application-owned.

## Required acceptance before replacing the timer

Use bounded synthetic cases for variable delays, repeated/palette-only displays,
ring-frame duration, missing versus unsupported audio, channel/track selection,
malformed input, cancellation during replay, overlapping seeks and disposal with
queued/in-flight samples. Validate exact pixels, timestamp/duration sequences and
PCM sample counts; a frame-count or first-frame assertion is insufficient.

Required original fixture coverage includes Arena FLC/CEL holds and palette
updates; Daggerfall `ANIM0000.VID` with its 633 frames and 475,054 PCM bytes;
Redguard `Intro.smk` for doubled display geometry/audio; Battlespire `JUMP.SMK`
for palette changes and absent audio, and `ANCHORS.SMK` for negative-rate timing;
Tactics and Xbox BOS BIKi with unsupported audio descriptors; Van Buren
`BISlogo.mve`/`IPlogo.mve` for extension ambiguity; and Fallout 1/2 MVE with actual
preroll, repeats, palette updates and silence/audio opcodes. Existing
[VID][vidtests], [FLC][flictests], [Smacker][smktests] and [Bink][binktests] tests
provide source fixtures/oracles to reuse. This review did not check fixture
availability or run them; missing platform/revision and MVE timing fixtures remain
explicit gaps, never successful skips or substitute files.

Finally require actual native first-frame readiness, pause/replay/loop/stop,
scrubbing, A/V alignment, failure recovery, visibility changes, replacement and
shutdown. Retire each old timer/player only after that consumer's parity passes.

[design]: C:/dev/Multitool/Multitool.Shared/docs/architecture/shared-workflow-design.md
[flic]: ../src/BethesdaMultitool/Core/Formats/Xngine/Flic/FlicFile.cs
[vid]: ../src/BethesdaMultitool/Core/Formats/Daggerfall/DaggerfallVidFile.cs
[smk]: ../src/BethesdaMultitool/Core/Formats/Smacker/SmackerFile.cs
[smkclip]: ../src/BethesdaMultitool/Core/Formats/Smacker/SmackerVideoClip.cs
[smkaudio]: ../src/BethesdaMultitool/Core/Formats/Smacker/SmackerAudioDecoder.cs
[bink]: ../src/BethesdaMultitool/Core/Formats/Bink/BinkFile.cs
[binkdecode]: ../src/BethesdaMultitool/Core/Formats/Bink/BinkVideoDecoder.cs
[mvedecode]: ../src/BethesdaMultitool/Core/Formats/Interplay/InterplayMveDecoder.cs
[mveclip]: ../src/BethesdaMultitool/Core/Formats/Interplay/InterplayMveVideoClip.cs
[mvefile]: ../src/BethesdaMultitool/Core/Formats/Interplay/InterplayMveFile.cs
[clip]: ../src/BethesdaMultitool/Core/AssetBrowse/ClassicVideoClip.cs
[preview]: ../src/BethesdaMultitool/App/Tabs/AssetBrowser/AssetVideoPreview.cs
[tab]: ../src/BethesdaMultitool/App/Tabs/AssetBrowser/AssetBrowserTab.xaml.cs
[explore]: ../src/BethesdaMultitool/App/Tabs/AssetBrowser/AssetBrowserTab.Explore.cs
[ivideo]: ../shared/Multitool.Shared/src/Slfx77.Multitool.Core/Media/IVideoSource.cs
[iaudio]: ../shared/Multitool.Shared/src/Slfx77.Multitool.Core/Media/IAudioSource.cs
[frame]: ../shared/Multitool.Shared/src/Slfx77.Multitool.Core/Media/VideoFrame.cs
[prepared]: ../shared/Multitool.Shared/src/Slfx77.Multitool.WinUI/Playback/PreparedMediaInput.cs
[native]: ../shared/Multitool.Shared/src/Slfx77.Multitool.WinUI/Playback/NativeMediaSession.cs
[nmt]: C:/dev/Multitool/NeversoftMultitool/src/NeversoftMultitool/Core/Formats/Vid1/Vid1MediaSource.cs
[vidtests]: ../tests/BethesdaMultitool.Tests/Core/Formats/Classic/DaggerfallVideoRetailTests.cs
[flictests]: ../tests/BethesdaMultitool.Tests/Core/Formats/Xngine/Flic/FlicFileTests.cs
[smktests]: ../tests/BethesdaMultitool.Tests/Core/Formats/Smacker/SmackerRetailCorpusTests.cs
[binktests]: ../tests/BethesdaMultitool.Tests/Core/Formats/Bink/BinkRetailCorpusTests.cs
