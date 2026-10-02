# Shared asset selection

CLI25 passed all 43 focused asset cases. CLI25 and Windows23 built with zero warnings/errors. [CLI25 / 43 tests](../artifacts/prototype-feedback/asset-selection-audit-002/validation.json) · [Windows23](../artifacts/prototype-feedback/asset-selection-audit-002/windows-validation.json) · [Toolbar outcome](../artifacts/prototype-feedback/gui-toolbar-025/windows23-replay-003/validation.json) · [Native outcome](../artifacts/prototype-runtime/xenia/gamepad-integration-001/full-build-004/validation.json).

World references, world actors, standalone selected-record NPCs, 2D terrain and 3D terrain share an immutable source plan. The declared policy is `bmt-selected-loose-archives-then-donors-v1`: primary/selected-source loose directories, their discovered archives in existing order, donor loose directories, then donor archives. It is a BMT policy; engine activation/priority is unverified. Plugin record order remains separate. Conversion rename/fuzzy/baseline policy remains explicit.

Reads retain every physical candidate, duplicate occurrence, bounded read/decode attempt, actual selected source and extracted-byte SHA-256. Duplicate entries in an admitted mount are ambiguous. Failed reads/decodes can fall through to the next mount. Archive discovery stats and actual-read pre/post stats are distinct from payload hashes; matching file length/time is not an atomic content snapshot. Changed archive inputs require a new plan.

Candidate-null attempts now carry one immutable `DeclaredMount` snapshot per layer: source path, kind, role, origin and nullable plan-time file stats. Missing/removed loose roots are unavailable mounts; missing entries in existing roots are ordinary misses. Scene/GLB/trace serialization and cached receipt reuse retain this identity. [43-case validation](../artifacts/prototype-feedback/asset-selection-audit-002/validation.json).

CPU/GPU decode keys include current candidate stats, authored-extension alternatives, paired normal/specular paths and actual material/texture dependencies. Failed higher-priority attempts are retried. Cached payloads retain their original receipts. Selected-plan disk caches that cannot persist actual-read receipts are bypassed. Operation admission drains before archive disposal; nested companion reads remain valid. Dependency/receipt histories are bounded. Failed selected-plan decode keys and keys superseded during decoding are evicted; returned payloads and their receipts remain usable.

Existing output routes expose provenance:

- Scene capture JSON: `assetReadReceiptsJson`, bounded scene-session scope, truncation and unavailable component-owner status.
- Renderer trace: per-actor base-record source and `assetReadReceipts`; texture resolve events include contributing receipts and normal/specular derivation status.
- NPC/creature GLB export: `extras.BMT_asset_reads`, covering composition and export reads.

CLI NPC and creature exports preserve the explicit archive argument order (`bmt-declared-order-v1`) and exports without textures. Composition caches are local to each export; shared texture-cache hits retain their original receipts. These adapters retain their existing single-plugin record selection.

The per-component RACE/ARMO/HDPT ownership chain is not yet attached to each asset receipt. The actor base-record source and physical asset source remain separate. Automatic refresh of already-resident terrain tiles/draw bindings after files change is not established by the new-request cache checks; real replay must distinguish reopened views from live replacement. Modern material/CDB behavior has focused fixtures but no new real-game replay.

Focused classes: `AssetSelectionTests` (31 cases), `SelectedAssetMaterialTests` (6 cases), `AssetSelectionRetryCacheTests` (4 cases), `AssetExportReceiptTests` (2 cases); relevant existing controls are `MeshArchiveSetTests`, `NifTextureResolverTests`, `TextureResolverSingleFlightTests`, `XboxNormalSpecularPairTests`, `WorldActorRenderingTests` and selected viewer-scene roundtrip tests. No broad suite was run in this lane.

Source checkpoint: [implementation-source-checkpoint.json](../artifacts/prototype-feedback/asset-selection-audit-001/implementation-source-checkpoint.json). Initial source audit remains [historical](../artifacts/prototype-feedback/asset-selection-audit-001/AUDIT.md).

July Mole Rat exports passed through both CLI adapters: two meshes and four images each. The creature route verified seven assets; the NPC pipeline creature branch verified six. Their shared six payload hashes and offsets match; the creature route also reads its existing idle animation. [Readbacks and input integrity](../artifacts/prototype-feedback/asset-selection-audit-001/real-export-001/README.md).
