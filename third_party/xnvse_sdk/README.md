# xNVSE SDK Snapshot

This directory contains a minimal vendored snapshot of the official xNVSE SDK
used as the ABI reference for `tools/NvseFaceGenProbe`.

Pinned upstream source:
- Repository: `https://github.com/xNVSE/NVSE`
- Commit: `228719143de3fb0d434b2f24f1c11ebb05d76ea1`

Included subset:
- `nvse/PluginAPI.h`
- `nvse/CommandTable.h`
- `nvse/ParamInfos.h`
- `nvse/GameAPI.h`
- `nvse/GameAPI.cpp`
- `nvse/GameForms.h`
- `nvse/GameObjects.h`
- `nvse/GameTypes.h`
- `nvse/Utilities.h`
- `nvse/SafeWrite.h`
- `nvse/SafeWrite.cpp`
- `nvse/nvse_version.h`

Notes:
- `NvseFaceGenProbe` does not use a CommonLib wrapper or yUI helper layer.
- The probe uses a small local interop header for the specific ABI surface it
  consumes at runtime, while this snapshot remains the pinned source-of-truth
  reference for offsets, interfaces, and version constants.
- Render-stage FaceGen detours are scaffolded but not yet enabled until the PC
  runtime hook addresses for `FalloutNV.exe 1.4.0.525` are closed.
