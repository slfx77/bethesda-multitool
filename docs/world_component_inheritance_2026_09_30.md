# World component inheritance

**Reconstruction.** Explicit load-order reports now include `world_inheritance.csv`. Each row retains the context, supplying record, target, value, selection path, plugin, physical offset and typed target-resolution state. Requested-source columns retain deletion, type-conflict and unparsed-winner states. Stored fields remain in `world_components.csv`.

FO3/FNV water, climate and image-space routes follow their individual PNAM bits through selected WRLD records. Missing parents, competing parent records and cycles remain unresolved. Parent overrides supply their winning fields; this does not alter the stored child record. Image-space selection shares this traversal, and the 3D climate selection uses it after a CELL climate override.

Interior lighting shares field selection between the renderer and reports. Inheritance chooses the source before resolving `FogPow`/`FogPower` or `FogClipDistance`/`FogClipDist`. This corrects inherited fog power taking the cell value when the template used the alternate spelling. Missing fields retain the existing fallback, with its source and requested inheritance recorded.

Text reports use a two-line reconstruction/source header. Detailed limitations remain in `report_sources.json`.

Water-plane integration, selected archive precedence and engine inheritance comparisons remain pending. Existing captured-world inputs remain separate from explicit load-order views.

Validation is recorded in [the batch checkpoint](../artifacts/prototype-feedback/world-component-validation-022.json). Existing [merged selection controls](merged_cell_exports_2026_09_30.md) and [runtime membership queries](runtime_membership_queries_2026_09_30.md) cover the adjacent record-selection paths.

All 140 focused checks passed. The [synthetic CLI readback](../artifacts/prototype-feedback/world-component-readback-022/FINDINGS.md) verified inherited fog power 2.5 and water height 12.75 with their supplying plugin and offsets; stored child water remained 100. All three text reports used the concise header. Inputs and the 107-file tool payload were unchanged.
