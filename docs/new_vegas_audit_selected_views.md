# Selected dialogue and world views

P2-L05 shares one physical record selection boundary between load-order inspection, reports, dialogue, and world views. Static selection and provenance are implemented and tested. Engine merge/order comparison, runtime enabled state and calculated actor profiles remain unfinished.

## Current validation — 2026-09-30

The focused integrated run passed `EsmReportsLoadOrderTests`, `LoadOrderDerivedViewTests`, `SharedGreetingLabelTests`, placement and appearance cases. Later GUI receipts verify July placement selection, duplicate physical offsets, GREETING attribution, and all three selected VR references with their own base/parent/transforms. November GUI extraction matches the CLI's 1,561 CELLs, 11,219 placements and 945 scripts, including identities, parentage, transforms and manifest content. [Focused receipt](../artifacts/prototype-feedback/focused-fifth.receipt.json) · [Placement GUI](../artifacts/prototype-feedback/gui-seventh-validation.json) · [Extraction parity](../artifacts/prototype-feedback/bmt-gui-script-extraction-retry002/comparison/validation.json) · [VR inspection](../artifacts/prototype-feedback/bmt-gui-vr-fourteenth/validation.json).

Remaining engine work is distinct from these passed checks: compare selected dialogue/terrain behavior against a controlled game load order; establish water/lighting and archive-priority rules before claiming engine equivalence. World ACHR/ACRE meshes remain excluded from the renderer. Runtime calculations, typed action coverage and scenario milestones are tracked in the [expanded-plan audit](../artifacts/prototype-feedback/plan-audit-20260930/AUDIT.md). No further runtime GUI replay is required.

## Selection

`LoadOrderSelectionView` selects unique live physical winners before merging typed records. An unparsed late winner cannot revive an older typed record or its names. Deletions, conflicting record signatures and duplicate physical winners remain in `record_provenance.csv` and are excluded from the selected typed view.

INFO, LAND and placed references have their own identities. A CELL or DIAL override does not replace all of its children. Child movement follows the selected child's parent evidence. LAND attaches only through physical cell-child GRUP framing. Multiple surviving LAND identities in one cell remain unresolved rather than receiving an invented precedence rule. A selected LAND or placed child with an unavailable parent remains in component provenance.

The CLI loads exactly the supplied order. The GUI reuses parsed snapshots and their retained headers/LAND data, avoiding another parse for each tab. It preserves the supplementary list's relative order and inserts the opened plugin at the first position consistent with the supplied master dependencies. The resulting order is visible in the load-order status tooltip and exported manifest. Missing masters reserve unresolved slots; they do not trigger directory discovery.

This explicit selection is limited to Fallout 3/New Vegas full plugins. A capture's world remains separate from the existing master-terrain preview. A plugin view cannot silently include a dump as another overriding plugin. Other games retain their existing paths.

## Dialogue and attribution

The selected graph is rebuilt from selected INFO/DIAL/QUST records. `dialogue_links.csv` distinguishes physical topic parentage, typed associations, serialized PNAM/TCLT/TCLF/NAME links, reverse-TCLF inference and observed runtime follow-ups. Missing targets are retained as unresolved edges.

FO3/FNV plugin parsing retains a small snapshot before cross-record attribution. Selection restores these record-local fields and derives quest/speaker defaults from the selected DIAL. An overridden child cannot leave a promoted title behind, and an overridden parent cannot leave its old quest/speaker on a surviving INFO. Sibling/quest-majority and EditorID-prefix guesses are not promoted into selected fields. Record-local conditions remain available. AMMO likewise retains its direct projectile link before weapon-derived enrichment, so cached GUI sources and freshly loaded CLI sources follow the same rule.

PNAM constraints guide the displayed reply order. A deterministic FormID tie-break supplies only a readable display order. Dangling predecessors, forks, cycles and unconstrained ties are written to `dialogue_ordering.csv`; replies are not discarded to force a linear chain.

P2-L06's misleading Watkins report label came from promoting the first GREETING child prompt/response into the shared DIAL title. GREETING now keeps its shared topic label in parsing, CLI detail/report output and the dialogue viewer. Each INFO retains its own prompt and response. A shared greeting used by several quests is not assigned exclusively to the first or most common child's quest. Existing captured metadata is retained as shared topic text rather than presented as the selected speaker's line.

## Surfaces and exports

- `dialogue tree` and `dialogue npc`, plus `world markers`, `world cell`, `world persistent` and `world heightmap`, accept `--load-order` and `--allow-missing-masters`.
- Bare FormIDs use the selected load-order namespace. Quest and cell selectors also accept `Plugin.esm:0xFileLocalID`.
- CLI world/dialogue output artifacts from an explicit order have a sibling `<output>.sources` directory. It contains the ordered source manifest, physical versions, dialogue links/order issues, world components and selected placements.
- The GUI record browser, dialogue viewer, world map and report generator consume the same cached selection. Its selected resolver is shared too.
- Lazy GUI details, navigation and quest-variable lookup use those selected records. Voice lookup uses the winning INFO's file-local ID and source plugin header, while captured original audio IDs require an explicit audio origin. Display namespace changes cannot silently redirect audio lookup.
- Actor statistics, inventory and appearance resolution use selected physical records. The appearance resolver keeps original plugin/local-ID asset provenance while the GUI uses selected display IDs. If selected appearance preparation fails, the actor list/details remain available with `Mesh preview: Unavailable.` Single-file and captured rendering keep their existing paths.
- Load-order report manifests use schema version 2. `world_components.csv` records source plugin/path/offset and separates CELL fields, LAND height/visual data, texture links, water/lighting links and declared asset paths. `selected_placements.csv` preserves full stored float precision and exposes unavailable parents.

Water/lighting inheritance is not calculated by this work. A declared model or texture path does not prove an asset is present or decoded. Unsupported or ambiguous records remain visible in provenance. File-local offsets and original opaque/script bytes are not rebased into the selected FormID namespace.

Actor assets retain the existing configured Data-directory or primary-directory archive discovery. This is an asset preview source, not a claim that its archive priority reproduces engine asset loading. Loading supplementary plugin records does not silently discover another plugin load order.

## Historical validation checkpoints

The following notes preserve the implementation sequence. Their pending-build statements are superseded by the current receipts above.

Root coordinates builds and focused tests. `PlacementInspectionTests` and `SharedGreetingLabelTests` passed at the second integrated checkpoint. The empty-dialogue report regression found by `EsmReportsLoadOrderTests` was corrected after that checkpoint; its rerun is pending.

`LoadOrderDerivedViewTests` adds small parameterized behavior fixtures for retained/moved/deleted/unparsed/duplicate INFO and LAND winners, multiple LAND identities, deleted parents, PNAM constraints and cycles, physical parentage, rebased texture/topic links, and primary-plugin insertion. Integrated CLI/Windows compilation, selected tests and actual GUI interaction checks are pending at this writing.

The third CLI build compiled the first selected-view checkpoint. Test compilation then found a missing required `LandTextureLayer.Kind` in the new fixture; it is corrected. Follow-up tests also cover unavailable physical parents, LAND parent namespace stamps, parent-attribution replacement, and direct versus inferred AMMO links. The newer attribution corrections await the next coordinated compilation.

The GUI follow-up preserves per-occurrence EDIDs from the same retained headers, caches the selected resolver, and drains old tab/actor work before resetting views when the load order changes. Added behavioral cases exercise shifted audio namespaces, captured audio identity, ambiguous audio source, and duplicate physical EDIDs. The Windows build before this follow-up failed on unrelated runtime JSON types; no build or test result is claimed for these last changes yet.

The actor service/workflow now accepts the shared selected appearance resolver rather than rescanning the primary source for merged display IDs. Root owns the resolver's physical-winner/asset-provenance implementation and tests; this lane wires the existing GUI path and preserves the details-only fallback.
