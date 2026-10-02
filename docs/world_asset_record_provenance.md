# World asset record provenance

**Implemented; build and live validation pending.**

The World inspector separates a placed reference from its selected base record and model field. Plugin name, file-local ID, load-order ID, record offset and flags come from the retained `LoadOrderSelectionView`. An asset archive or loose-file receipt remains a separate physical source. Parsed-source hashes are **Unavailable** when the original parse did not retain one.

`WorldViewData.AssetRecords` handles selected, clamped, ambiguous, deleted, type-conflicting and unparsed records. An excluded base cannot borrow an older model. A placement with the wrong physical offset and a model path inconsistent with the selected base remain explicit. Ambiguous/type-conflicting physical candidates survive as candidate nodes.

Captured, master-preview and save-overlay views keep their mode. Where the selected physical record view is unavailable, model requests can still bind observed asset reads while the record owner stays **Unavailable**.

Capture output adds:

- `assetSelectedPlacementUsesJson`: placement → base reference → selected base/model declarations, with recent read bindings.
- `assetDeclaredModelUsesJson`: selected base-model declarations matching recent requests. This does not establish a placement or draw.
- `assetRecordView`, scope labels and `assetParsedSourceHashStatus`.

The complete physical receipt list remains `assetReadReceiptsJson`. NPC/creature component graphs are retained in `world-actor-mesh` events. Static texture/material dependency ownership is not inferred from model or texture filenames.

`WorldAssetRecordTests` contains 15 focused cases for distinct placement/base owners, excluded records, captured/preview modes, physical/path mismatch, shared reads and renderer path normalization. No GPU is required for those controls. Source changes are awaiting the coordinated build; GUI inspection and capture acceptance remain pending.
