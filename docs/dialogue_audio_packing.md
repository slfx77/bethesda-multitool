# Dialogue audio packing

Pass Audio Transcriber CSV files to `dmp to-esm --dialogue-audio-csv` with `--pack-assets` and the source data folders. The normal path binds selected CSV rows to emitted or captured INFO records.

Add `--include-unbound-audio` to include selected rows without an INFO match. In the converter UI, select **Include audio without INFO matches**. These assets keep their original plugin folder and filename stem. Xbox XMA is converted to OGG, and the corresponding LIP is requested. Exact paths and extension equivalents are accepted; unrelated fuzzy donors are excluded. No INFO records are created for these rows.

`<output.bsa>.dialogue-audio.json` records CSV path/row, source FormID/response, transcription model identity, binding status, requested/resolved paths, source container, output SHA-256 and outcome. A completed archive write changes **Prepared** to **Packed**. Failed or cancelled jobs retain their partial audit.

Converted assets and archive payloads use temporary disk spools. The packer releases them on completion, failure or cancellation.
