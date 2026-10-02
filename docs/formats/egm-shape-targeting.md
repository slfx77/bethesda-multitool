# Optional verified pre-skin head-shape routing

The renderer and export extractor previously let the first decoded skinned shape consume the legacy EGM displacement array. An earlier skinned attachment in a head NIF could therefore consume deltas intended for the head. The new optional `NifPreSkinMorphTarget` carries an exact owner/data-block pair through head composition and both extraction paths. When the selected TRI proves one matching owner, only that owner consumes the deltas, before skinning.

`NpcHeadMorphTargetLoader` reads the appearance's explicit `BaseHeadTriPath` and base-head NIF through the existing ordered archive/loose resolver. It preserves actual resolved source locators and original NIF/TRI SHA-256 identities. No TRI path, head name, game, coefficient family or basis is inferred from vertex counts. The new Core composition helper introduces no Core-to-CLI dependency.

## What the proof establishes

The source must be a little-endian NIF with one eligible visible legacy skinned owner whose base vertex count and oriented indexed triangle topology match the selected TRI through the existing `TriNifBinding`. That topology comparison permits facet reordering and cyclic corner rotation, preserves facet multiplicity and excludes repeated-index degenerate facets. It does not equate arbitrary coordinates or reverse winding.

The target retains owner/data indices, source identities, base V, complete V+K and topology diagnostics. Both extractors check the exact original NIF hash and full displacement length before extraction. A stale source or truncated domain fails before deltas are applied. Excluding the proven head with a shape filter cannot redirect its deltas onto another shape. Original input arrays are not modified.

Unavailable, unsupported or ambiguous proof retains the existing first-skinned route. This includes missing explicit TRI data, no unique matching owner and more than eight eligible owner candidates. The change therefore fixes proof-admitted routing; it does not claim that every legacy fallback now identifies the head correctly. Existing callers that omit the optional target retain their behavior.

The extractor still trusts its internal caller to supply a `NifInfo` corresponding to the original bytes. Hash validation does not independently validate a separately supplied or mutated parsed object.

## Statistical-domain policy remains unchanged

This is geometry routing, not adoption of the diagnostic [EGM statistical domain](egm-domain-validation.md). It does not independently establish actor coefficient basis, the EGM-to-TRI pairing, actor neutral coordinates or an arbitrary coordinate-frame conversion. The raw TRI V+K reference used while deriving topology is not published as an actor-shaped statistical reference.

The existing `EgmParser` and `FaceGenMeshMorpher` retain coefficient-family truncation, accumulation order, the `1e-7` coefficient epsilon, near-zero-result policy and complete V+K displacement storage. Geometry still consumes the visible V prefix. A header word, equal family counts or game label cannot supply the missing coefficient provenance. A later statistical-domain adoption needs independently established source/basis evidence before comparison with the EGM header.

## Bounded proof inputs and costs

Optional proof reads admit at most 64 MiB of NIF bytes and 16 MiB of TRI bytes. The new bounded `MeshArchiveSet` operation uses existing resolution order and reports the actual resolved source. It delegates BSA bounds to its bounded reader, checks both stored and decoded BA2 General sizes before extraction, and length-checks loose files before allocation, reads them exactly and checks for growth. It does not add bounds to existing archive indexing, EGM parsing or the legacy extraction route.

The TRI reader limits V+K to 500,000. At most eight eligible owners reach topology binding; classification still examines the parsed NIF before enforcing that candidate cap. Each binding attempt can reparse/hash the NIF and sort triangle keys. The proof also constructs a V+K reference array. These are bounded repeated attempts, not a constant-cost lookup. Inherited parser and decompressor allocations remain delegated; the input limits are not a measured peak-memory or latency guarantee. There is no new persistent proof cache, retained source payload or long-lived source handle, and cancellation is not newly threaded through synchronous composition.

The exact changed declarations and incremental/delegated costs are recorded in [the scoped complexity review](../complexity/egm-shape-targeting-20260920.json).

## Validation status at source review

Thirteen new synthetic cases distinguish routing, timing and admission: two same-count skinned shapes with only the later owner matching topology; a noncommuting rotation/translation under linear and dual-quaternion skinning; export-local displacement; filtered targets; duplicate owners; unsupported quads; full V+K requirements; changed source bytes; and bounded archive/loose provenance. The fixture retains both shapes and real skin/shader structure instead of hiding the earlier attachment.

The focused filter contains 50 cases: ten new `NifPreSkinMorphTargetTests`, seven `MeshArchiveSetTests` (three new), and 33 existing cases across `EgmReaderTests`, `EgmShapeDomainTests`, `TriReaderTests`, `TriGeometryBindingTests`, `FaceGenMeshMorpherTests` and `PackedHeadRuntimeParityTests`. The coordinated Linux `20260920-nif-egm` batch includes these cases and was still running at receipt creation. No compilation or execution success is asserted by this source-review note.

The subsequent [Linux execution record](../validation/nif-egm-linux-20260921.md) records all 50 cases passing in the 321-case retry after repair of an existing virtual-path basename portability defect. Both accessibility cases also passed. The later publish-restore failure and remaining platform/native checks are tracked separately.

Retail actor selection, actor-specific coefficients, BA2 runtime coverage, native rendering, current GUI/CLI workflow acceptance and performance remain separate gaps. This increment does not complete M2.1 or authorize statistical-basis adoption.
