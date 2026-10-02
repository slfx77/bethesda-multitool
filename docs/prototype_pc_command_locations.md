# PC raw command locations

The existing `ShowMessage`, `MessageBoxEx`, and `GetButtonPressed` wrappers now append `commandLocation` evidence. Native22 compiled with zero warnings/errors; the fixtures passed, including 19 command-location cases. [Build and source pins](../artifacts/prototype-runtime/pc/native-generation-022/validation.json). Actual engine calibration remains pending.

Each observation retains the incoming `scriptData` pointer, the `opcodeOffsetPtr` address and value before/after the original handler, and the Script's type, FormID, flags, data pointer and length. A complete Script data body up to 65,536 bytes receives SHA-256 fingerprints before and after the call. Script identity and data fields are reread after each fingerprint; changed, unreadable, overflowing, empty and oversized inputs have explicit states. The trace contains hashes and bounded headers, not a bytecode dump.

`bytecodeStableAcrossCall` means both observed bodies and their Script identities agree. `normalizedOffsetStatus` remains `unverified`: the SDK permits expression-backed buffers, so raw offsets are preserved without subtracting a presumed header or assigning a reconstruction line. The shared mapper separately validates source namespace, physical block identity, bytecode and any future offset profile.

The original handler receives the original arguments once. No argument extraction is repeated. Its return, result storage, offset mutations and `LastError` remain intact; observation preparation restores the incoming `LastError` before dispatch. Events retain the admitted request and capture generation, and stale returns are suppressed. Message-instance candidates also carry generation ownership.

Nineteen focused native cases cover the three wrappers, fixed independent SHA-256, offset changes, foreign buffers, unreadable pointers, type and size limits, arithmetic overflow, changing code/identity, capture stop/disconnect/restart, inactive capture and request changes. Actual top-level and expression-command controls remain the offset-calibration gate.

Sources: pinned xNVSE commit `0ccd23ad885ddae533c1790a3fc56cd073e38de3`, `CommandTable.h` CommandExecute ABI and `GameScript.h` Script `dataLength` +0x20 / `data` +0x30. See [mapping audit](../artifacts/prototype-feedback/runtime-script-mapping-audit-001/FINDINGS.md). This layout is enabled only for the existing verified PC executable profile.
