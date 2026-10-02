# World inspector navigation

The actual X-13 GUI check reproduced inert cell headings and placement-name links in both map modes. The controls combined selectable `TextBlock` content with `Tapped` handlers. The failed interactions and unchanged source hashes are retained in [the Fast13 receipt](../artifacts/prototype-feedback/bmt-gui-vr-thirteenth/validation.json).

Headings and navigable names now use `HyperlinkButton` controls with accessible names. Selecting a placement updates the active viewer. In 3D, the camera frames transformed object bounds, a resident mesh, or the existing scale-aware fallback. Selection preserves authored visibility.

**Validation:** Fast14 built successfully. Actual GUI checks matched the exported identity, base, parent cell, position and rotation of `0100EFF3`, `0100EFE8` and `01011E5C`. NPC heading expansion, a pointer click on Patrol8 and UIA invocation of Patrol6/Agent1 worked; the 3D camera moved to each reference. Patrol6 remained initially disabled. Screenshots were viewed, the app closed normally, and all 22 inputs plus the assembly retained their hashes. See the [validation receipt](../artifacts/prototype-feedback/bmt-gui-vr-fourteenth/validation.json) and [screenshots and replay notes](../artifacts/prototype-feedback/bmt-gui-vr-fourteenth/README.md).

**Historical Fast14 boundary:** ACHR/ACRE actor pixels were unavailable. The [Windows 17 actor replay](../artifacts/prototype-feedback/world-actor-rendering/README.md) now confirms stored NPC and creature meshes, including the three X13 controls, while preserving authored disabled state.

The 3D interior picker loads a scene without populating its cell inspector. The replay populated inspection through the existing 2D interior selection, switched to 3D, then tested the reference links. Pure 3D cell-browser inspection remains unavailable. Camera framing also does not remove room occlusion; nearby geometry obstructed Agent1's view.

The Windows 17 replay also reproduced cold-mesh fallback framing and the direct 3D parent-cell link leaving the old inspector selected. The [Windows 18 replay](../artifacts/prototype-feedback/world-actor-rendering/july-navigation-018-001/README.md) passed both corrections: one cold actor selection frames the resident mesh, and its parent-cell link populates the exact cell inspector. Viewed screenshots, normal closure and unchanged input/runtime hashes are preserved. The guard against concurrent manual navigation remains source-reviewed only.

After Windows18 was pinned, the shared 3D cell-browser activation handler was updated to clear the selected reference and raise its existing InspectCell event. This covers both Interiors and All Cells without changing selection-only behavior. The next Windows build and live route checks remain pending; [patch and acceptance controls](../artifacts/prototype-feedback/world-actor-rendering/PURE3D-CELL-INSPECTOR.md).
