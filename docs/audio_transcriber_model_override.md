# AudioTranscriber model override

Set `BMT_WHISPER_MODEL_PATH` in the process environment before launching AudioTranscriber to use an existing GGML model file. With the variable absent or blank, the existing `ggml-base.en.bin` cache remains the default. An explicit missing path fails visibly; it does not download a replacement or silently fall back.

The service hashes the selected file with SHA-256 while holding a read-only handle, then loads it on a worker thread. Initialization messages identify the filename and size, followed by a hash prefix. Each new Whisper transcript can retain the complete model filename, absolute path, SHA-256 and byte count. JSON stores this as `whisperModel`; CSV appends model columns; text exports list the model identities. Legacy records remain readable and lack model provenance when it was not recorded. Filename alone is not a verified model family.

Validated local large model:

- Path: `C:\dev\Media\youtube-tools\work\models\ggml-large-v3.bin`
- Bytes: `3095033483`
- SHA-256: `64d182b440b98d5203c4f9bd541544d84c605196c4f7b845dfa11fb23594d1e2`
- The hash matches the existing downloader's `model_verified` receipt in `large-v3-download.stdout.log`.

`PlaylistView` passes the model identity for single and batch Whisper results. Manual/ESM text has no Whisper model attached.

The Fast Windows companion build passed in 32.5 seconds with zero warnings/errors. Actual large-v3 GUI checks passed for all six selected July clips: playback, seek, pending transcription, immediate status refresh and JSON persistence. CSV export produced 45,548 rows and 11 aligned columns. All six model identities match the source model hash above. Sources and the prior base.en run remain unchanged. [Validation and listening queue](../artifacts/prototype-feedback/audio-ui-large-v3/validation.json).

On the known INFO control, word errors against its stored text fell from 7/15 with base.en to 5/15 with large-v3. Large-v3 recovered “Tear you up” but omitted the written barking/laughter. The five orphan-line results were substantially unchanged. Human listening remains pending.

The follow-up GUI replay passed default base.en initialization, one new transcription, immediate Clear Whisper enablement, actual clearing and empty sidecar persistence. The explicit missing-model error remained visible. [GUI replay](../artifacts/prototype-feedback/audio-gui-fixes-validation.json).
# Startup status follow-up

Actual GUI validation exposed a missing-model status race: queued indexing progress replaced the initialization error, and automatic Playlist navigation cleared it. LoadingView now retires progress before BuildLoaded; model initialization retires late progress before ready/error. PlaylistView retains that status during setup, and MainWindow preserves it through initial Playlist navigation. The Windows Fast build and both error/ready GUI replays passed.
