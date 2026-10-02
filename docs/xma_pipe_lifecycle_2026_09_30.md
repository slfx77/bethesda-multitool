# XMA process lifecycle

The actual asset-pack attempt stopped making progress while BMT PID20788 waited on its Chocolatey FFmpeg shim59720 and decoder49816. The owned process tree and command lines were captured before termination. A bounded triage dump contains a blocked ReadFile worker and a blocked WriteFile worker, with other workers idle. The decoder/shim's internal wait was not profiled.

Evidence is retained under `artifacts/prototype-feedback/report-refresh/rechecks/asset-pack-triage*` and `asset-pack-processes-*`. The failed pack attempt remains under `artifacts/prototype-feedback/retry-checks/audio-asset-pack/`; selected clips were not validated as packed.

The three XMA converters previously had no completion deadline or cancellation and closed stdin only after a successful write. `PrototypeAssetConverter` accepted a cancellation token but omitted it from XMA calls.

`FfmpegPipeRunner` now drains stdout/stderr before submitting input, closes stdin in a finally block, applies a one-minute whole-conversion deadline, and kills its owned process tree on timeout/cancellation. WAV, OGG and MP3 use this shared lifecycle. Explicit asset-pack cancellation propagates; a timed-out conversion remains an individual asset failure with its reason.

Focused behavioral class: `FfmpegPipeRunnerTests` (seven cases). An opt-in C# role in the existing executable test assembly writes beyond both pipe capacities before reading input, exits early, stalls with a child process, or is cancelled. Assertions verify exact output, decoder error preservation, timeout/cancellation and live process-tree cleanup. A pre-cancelled packer call preserves cancellation. No external shell, separate test project or source-text assertions are used.

Cleanup, including cancellation and disposal, has a two-second return allowance. An exited-shim fixture retains a child holding inherited pipes: the runner returns a bounded failure, and the fixture tears down its known child. `Process.Kill` cannot discover descendants after their parent has exited; this patch does not add OS process containment. The observed Chocolatey shim remained alive, so its verified complete tree was terminated successfully. A fresh successful XMA/asset-pack replay is still required.

Build, focused execution and the actual asset-pack retry remain parent-coordinated.

The packer's existing JSONL event sink now records `asset-conversion-start` before each Xbox asset conversion and `asset-conversion-end` with its outcome and elapsed milliseconds. Metadata includes requested/resolved/source paths, source-folder index, input/output byte counts and any failure reason. Normal console output still suppresses these Info events unless verbose mode is enabled.
