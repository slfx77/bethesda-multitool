# Runtime trace import and source binding

Instrumented runtime capture and trace import are CLI features. The Records and Actors GUI views do not offer trace selection, import, or observation panels.

Use `runtime import TRACE.ndjson` to validate a saved trace and print its summary as JSON. Optional reports include `--script-calls`, `--commands`, `--script-map`, `--condition-map`, and `--gamepad`; run `runtime import --help` for their requirements. Import preserves the trace bytes and does not contact an engine. See [controller capture and reports](runtime_controller_runs.md) for capture commands.

Trace integrity and source binding are independent. **Complete** means the capture sequence/footer checks passed; it does not mean attempted engine actions succeeded. Errors, dropped events and sequence gaps remain in the summary and shared import model. **Matched** requires every active plugin slot, name and SHA-256 to match the complete inspected plugin order. Missing masters, a lone DLC without its masters, incomplete captured plugin identity and memory dumps remain **Unbound**. Different files or load orders show **Mismatch**. Unbound traces retain their observations without automatic FormID association.

Capture-start plugin identity takes precedence over earlier handshake identity. The native PC lane supplies `identity.activePlugins` and `activePluginIdentityStatus`; source binding does not infer an active load order from a directory. Xbox register/memory prefixes remain raw evidence. An explicit native `engineScriptFormId` is indexed only with `scriptIdentityStatus: Resolved`, and still requires the same source binding before record joins.

Actor statistics from the CLI use the same binding gate before adding observed values; an executable hash and coincident raw FormID alone do not suffice. Stored and inherited statistics remain separate from observations. The shared import model preserves exact UTF-8 source bytes and the complete event index under the existing 256 MiB trace limit. Enriched NDJSON lines allow up to 1 MiB; the bridge wire limit remains 64 KiB.

`RuntimeTraceDocumentTests` covers exact UTF-8/BOM/CRLF spans, preserved zero observations, capture-start precedence, legacy identity and changed/incomplete plugin orders. `ActorStatisticsServiceTests` covers missing-source binding. `RuntimeImportExeTests` and `RuntimeConditionMapImportExeTests` exercise the CLI reports, trace preservation and refusal to overwrite existing reports.
