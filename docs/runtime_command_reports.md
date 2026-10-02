# Runtime command reports

`runtime import TRACE --commands NEW_JSON` exports command results and script-call attribution. It also accepts `--script-calls` and `--script-map`. Each output must be a new, distinct file; stdout remains the trace summary.

Every command retains the trace hash, source line, byte offset, sequence, frame and complete original event. Coverage headers and `command-hook-coverage` chunks are preserved, including skipped entries and truncation. They describe the captured hook installation; the importer does not infer missing coverage.

`resultBits` is a 16-digit hexadecimal unsigned 64-bit value: double `1.0` is `3ff0000000000000`. Numeric interpretation requires an observed SDK Default return type, a finite `numericValue`, and an exact bit match. Form, string, array, array-index and ambiguous return types retain their raw bits. Legacy `value` fields remain in the original evidence.

Script-call attribution requires matching request, thread, Script FormID and address, plus an event inside the innermost observed entry/exit pair. Available before/after Script identities must agree. Missing identity is **Unavailable**; incomplete scopes or event loss are **Partial**; conflicting identities are **Ambiguous**. Temporary script ID zero can identify an observed call. Physical source ownership is handled separately by `--script-map`.

The report includes generic `command-execute`, the existing message commands and choice reads, and selected `condition-function` events. Eval events retain their distinct ABI and evidence. Result and call statuses are independent; the report status summarizes both. Call counts count attribution statuses.

Native command hooks use the published xNVSE script-command table and the existing requested/selected/all/off script filters. [Native implementation and controls](prototype_pc_general_commands.md). Executable-specific bytecode offsets and full CTDA outcomes still require calibration. No runtime GUI is required.

CLI24 validation passed: [1,281 retained PC010 events](../artifacts/prototype-feedback/runtime-command-import-024/attempt-001/validation.json) preserve exact source bytes and legacy availability states. The [native fixture import](../artifacts/prototype-feedback/runtime-command-import-024/native-interop-001/validation.json) preserves typed result bits, three coverage records and the observed fixture call pair. [Focused checks](../artifacts/prototype-feedback/batch24-validation.json).
