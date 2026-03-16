# FaceGen EGT Blend Spec

## Scope

This document captures the Xbox 360 Fallout: New Vegas FaceGen texture blend path as recovered from the Aug. 22, 2010 MemDebug build and compared against the shipped final build.

The investigation is intentionally limited to EGT texture behavior:

- where NPC and race texture coefficients are sourced
- how those coefficients are combined
- how EGT basis deltas are accumulated
- where native-space vs resampled-space work happens
- how the shipped facemod texture path relates to the final applied diffuse result

## Evidence Inputs

Primary reverse-engineering inputs:

- `Sample\Full_Builds\Fallout New Vegas (Aug 22, 2010)\Diskuild_1.0.0.252\Fallout_Release_MemDebug.xex`
- `Sample\Full_Builds\Fallout New Vegas (Aug 22, 2010)\Diskuild_1.0.0.252\Fallout_Release_MemDebug.pdb`
- `Sample\Full_Builds\Fallout New Vegas (Aug 22, 2010)\Diskuild_1.0.0.252\Fallout_Release_Beta.pdb`
- `Sample\Full_Builds\Fallout New Vegas (360 Final)\default.xex`

Local Ghidra artifacts used as the working evidence set:

- `tools/GhidraProject/facegen_textures_decompiled2.txt`
- `tools/GhidraProject/facegen_memdebug_decompiled_pdb_xenon.txt`
- `tools/GhidraProject/facegen_merge_decompiled.txt`
- `tools/GhidraProject/facegen_memdebug_decompiled.txt`
- `tools/GhidraProject/facegen_image_decompiled.txt`
- `tools/GhidraProject/facegen_coord_controls_decompiled.txt`
- `tools/GhidraProject/facegen_coord_apply_decompiled.txt`
- `tools/GhidraProject/facegen_skin_path_decompiled.txt`
- `tools/GhidraProject/facegen_geck_texture_bake_candidates.txt`
- `tools/GhidraProject/facegen_geck_face_mod_export.txt`
- `tools/GhidraProject/facegen_geck_face_mod_upstream.txt`
- `tools/GhidraProject/facegen_pdb_final.txt`

## Function Map

### MemDebug Map

These are high-confidence symbol-backed MemDebug texture-path anchors.
The corrected primary artifact for the current pass is
`tools/GhidraProject/facegen_memdebug_decompiled_pdb_xenon.txt`.

| Function | PDB Offset | VA | Confidence |
| --- | --- | --- | --- |
| `BSFaceGenModel::LoadModelTexture` | `0x00240648` | `0x82490648` | high |
| `BSFaceGenModel::LoadEGTData` | `0x00241FF8` | `0x82491FF8` | high |
| `BSFaceGenModel::ApplyCoordinateTexturingToMesh` | `0x00241970` | `0x82491970` | high |
| `EGTData::Create` | `0x002418F8` | `0x824918F8` | high |
| `BSFaceGenManager::GetFaceCoordValue` | `0x0023AE00` | `0x8248AE00` | high |
| `BSFaceGenManager::SetFaceCoordValue` | `0x0023AF40` | `0x8248AF40` | high |
| `BSFaceGenManager::PrepareHeadForShaders` | `0x0023D3A8` | `0x8248D3A8` | high |
| `TESNPC::GetHeadPartModTexture` | `0x001EE8E8` | `0x8243E8E8` | high |
| `TESRace::GetFaceGenData` | `0x001FC570` | `0x8244C570` | high |
| `TESNPC::LoadFaceGen` | `0x001F4CB0` | `0x82444CB0` | high |
| `BSFaceGenManager::MergeFaceGenCoord` | `0x00239AC0` | `0x82489AC0` | high |
| `BSFaceGenManager::OffsetFaceGenCoord` | `0x00239E28` | `0x82489E28` | high |
| `BSFaceGenImage::GetCompressedImage` | `0x0022F6A0` | `0x8247F6A0` | high |

Notes:

- The texture-path decompilation files often show the real entry at `VA + 8` because of 8-byte register-save thunks.
- Example: `BSFaceGenModel::ApplyCoordinateTexturingToMesh` decompiles from `0x82491978`, while the PDB-backed start is `0x82491970`.
- The older PE-offset helper dumps are still useful as scratch artifacts, but the corrected investigation should prefer the raw-PDB Xenon output above when the two disagree.

### Final / Beta Map

These offsets came from the beta/final-era PDBs and were used to probe the shipped `default.xex`.

| Function | PDB Offset | Derived VA | Confidence |
| --- | --- | --- | --- |
| `BSFaceGenModel::LoadModelTexture` | `0x002CC4F0` | `0x824DC4F0` | medium |
| `BSFaceGenModel::LoadEGTData` | `0x002CD820` | `0x824DD820` | medium |
| `BSFaceGenModel::ApplyCoordinateTexturingToMesh` | `0x002CD1B8` | `0x824DD1B8` | medium |
| `EGTData::Create` | `0x002CD158` | `0x824DD158` | medium |
| `BSFaceGenManager::PrepareHeadForShaders` | `0x002C9120` | `0x824D9120` | medium |
| `TESNPC::GetHeadPartModTexture` | `0x00277E30` | `0x82487E30` | medium |
| `BSFaceGenImage::GetCompressedImage` | `0x002BAB58` | `0x824CAB58` | low |
| `Lighting30Shader::SetFaceGenMaps` | `0x0085A290` | `0x82A6A290` | medium |

Final-build mapping status:

- the higher-level FaceGen path is identifiable in the shipped final build
- low-level final XEX decompilation quality is materially worse than MemDebug
- the beta/final offsets are useful anchors, but not every function is yet decompiled cleanly enough to claim byte-for-byte final equivalence

Conclusion:

- use MemDebug as the authoritative behavior oracle
- treat the shipped-final low-level texture-function mapping as partially confirmed, not fully closed

## Recovered Behavior

### 1. Runtime coord assembly is base-plus-delta, not raw-FGTS-first

The higher-confidence runtime composition path is now:

- `TESRace::CreateHead(...)`
  - initializes a FaceGen coord blob
  - calls `TESRace::GetFaceGenData(...)`
  - passes that blob into `BSFaceGenManager::CreateFaceGenHead(...)`
- `TESRace::GetFaceGenData(param_2 != 0, ...)`
  - first calls `Function_8243CD70(param_2, param_3)`
  - then overlays race-selected metadata and basis tables, including the texture table at `param_3 + 0x94`
- `Function_8243CD70(actor, dest)`
  - initializes `dest` via `BSFaceGenManager::InitFaceGenCoord`
  - if `actor->0x120 == 0`, copies the global/default coord blob into `dest`
  - otherwise:
    - chooses the active actor coord blob from `actor->0x1A4` or fallback `actor + 0x144`
    - resolves the sex/head selector through `FUN_8242DC08(actor)`
    - chooses the race base coord blob from `actor->0x120 + 0x468` or `actor->0x120 + 0x408`
    - calls `BSFaceGenManager::MergeFaceGenCoord(0, baseCoord, actorDelta, dest, 0)`

The small helper closures are now direct, not inferred:

- `FUN_8243CCE8(actor)`
  - returns `actor->0x1A4` when present
  - otherwise returns `actor + 0x144`
- `FUN_8242DC08(actor)`
  - when `*(char *)(actor + 4) == '*'`, returns `(*(uint *)(actor + 0x44) & 1)`
  - otherwise returns `0xFFFFFFFF`

The persisted NPC load path is now materially clearer too:

- `TESNPC::LoadFaceGen(stream, npc)`
  - reads the inline FaceGen coord blob directly into `npc + 0x144`
  - the nested loop walks the four `0x18`-byte coord descriptors whose metadata begins at `npc + 0x158`
  - each element is read as a serialized `float` and written straight into the descriptor storage referenced by `ptr/count/stride`
  - after the coord blob, it resolves three tokenized object references and stores them as:
    - `npc->0x120`: race
    - `npc->0x1A8`: hair
    - `npc->0x1B0`: eyes
  - it then reads:
    - `npc->0x1AC`: one serialized `float` (semantic still unresolved)
    - `npc->0x1C8`: one serialized `int`
    - bit `0` of `npc->0x44`: one serialized byte/sex flag
  - if any loaded value changed, it rebuilds live FaceGen state and may call `FUN_82442470(...)` for extra NPC-backed face state

One of those scalar fields is now partially identified:

- `npc->0x1C8` is also written by `RaceSexMenu::ChangeHairColor(...)` as packed `RGB`
- that makes `LoadFaceGen`'s `+0x1C8` load a real persisted appearance field, not transient scratch state

The post-load extra branch is now narrowed too:

- when `LoadFaceGen` detects a material change and `npc[0x71] != 0`, it calls `FUN_82442470(...)`
- `FUN_82442470(...)`:
  - consumes already-loaded NPC state such as:
    - current resolved actor/NPC objects
    - methods at virtual slots `0x1A8 / 0x1AC / 0x1E0`
    - extra object groups rooted at `+0x1B4 / +0x1B8`
    - slot arrays/counts at `+0xC4 / +0xCA`
  - builds or clones additional object trees and attaches them through virtual calls such as `0xDC`, `0x128`, `0x114`, and `0x11C`
  - does **not** write back to:
    - `+0x144`
    - `+0x1A4`
    - `+0x120`
    - `+0x1A8`
    - `+0x1B0`
    - `+0x1AC`
    - `+0x1C8`

Operational conclusion:

- `FUN_82442470(...)` looks like extra head-part / attachment / accessory state rebuilding, not a hidden late rewrite of the core FaceGen coord blob
- that makes it a weaker candidate for the standalone shipped-facemod EGT mismatch than the main coord load/merge path

Operational conclusion:

- race participation is definitely still present in runtime texture behavior
- shipped facemod files are NPC-keyed outputs, not proof that race data is absent at runtime
- the runtime source model is more specific than a simple “read NPC FGTS array and race FGTS array, then add them”

### 2. The runtime merge rule is additive at the coord-blob level

`BSFaceGenManager::MergeFaceGenCoord` is additive when used by the runtime coord composer above:

```text
mergedCoord = MergeFaceGenCoord(0, baseCoord, actorDeltaCoord, dest, 0)
```

Important details from the decomp-backed runtime call:

- `param_1 = 0`
  - the normalization branch inside `MergeFaceGenCoord` is disabled on this path
- the runtime merge-mode flag is clear
  - the special `Function_82489380(...)` copy branch for the outer `iVar6 == 1` group is not used on this path
- the active base coord blob is selected by sex/race state through the `0x408/0x468` choice

The helper behavior around this path is now closed:

- `FUN_82487980(lhs, rhs)`
  - compares all four `0x18`-byte coord descriptors
  - returns non-zero when the blobs differ, zero when they are identical
- `FUN_82489380(dst, src)`
  - is only a raw descriptor copy/resize helper
  - it copies `count * stride * sizeof(float)` bytes after resizing the destination storage
  - it does not apply tint, scaling, normalization, or FGTS-index-specific logic

Operational conclusion:

- the engine is doing plain additive coord composition on the runtime head path
- that composition happens over full FaceGen coord blobs
- the record `FGGS / FGGA / FGTS` chunks do feed that same blob structure, so they are not obviously a separate pre-runtime representation
- but treating the live renderer as “fully matched” just because it adds NPC + race texture coefficients is still too strong, because the engine merges complete blobs and the authored control path still edits them through richer page-based logic

### 3. The optional actor FaceGen override object is separate from the coord blob

`TESRace::GetFaceGenData` also consults `FUN_8243CE00(actor)` after the coord blob is built.

Recovered behavior:

- it starts from `actor->0x1A8`
- if `actor->0x1DC != 0` and the actor is not the active player actor:
  - it resolves the sex/head selector through `FUN_8242DC08(actor)`
  - it then calls `func_0x8244A408(actor->0x120, selector)`
- the resulting object is written into `param_3 + 0x60`

Operational conclusion:

- this object path is real runtime FaceGen state, but it is distinct from the active coord blob at `+0x144/+0x1A4`
- it is likely relevant to full head construction and metadata selection
- it is a weaker candidate for standalone shipped-facemod EGT mismatches than the merged coord blob, because the texture morph loop reads the first `0x60` bytes directly

### 4. The texture loop reads the merged coord blob directly

`BSFaceGenModel::ApplyCoordinateTexturingToMesh` does not fetch live texture coefficients from the later `param_2 + 0x94` texture-table block.

The relevant inputs are in the first `0x60` bytes of the `TESRace::GetFaceGenData` output:

- `BSFaceGenManager::InitFaceGenCoord` initializes the third coord descriptor at `+0x30`
  - count at `+0x40` = `0x32`
  - stride at `+0x44` = `1`
- `BSFaceGenModel::ApplyCoordinateTexturingToMesh` reads the active texture coefficient as:

```text
coeff = *(float *)(*(int *)(param_2 + 0x30) + *(int *)(param_2 + 0x44) * morphIndex * 4)
```

Operational conclusions:

- the live texture-morph loop consumes the merged coord blob's third descriptor directly
- the later `+0x94` block is still real FaceGen metadata, but it is not the coefficient source used by this loop
- if current code does not mirror the third descriptor exactly, a mismatch can exist even when race table selection is otherwise correct

### 5. A 7-control page is handled specially upstream of the final texture loop

The newly isolated helper at `0x826D9B10` adds an important boundary to the current model.

Recovered behavior:

- `FUN_826D9B10_apply_resolve_facegen_control_value(...)`
  - is called from `0x826DA00C`
  - immediately special-cases control IDs in the range `0x19 .. 0x1F`
  - for controls outside that range, it falls back to `func_0x8239ccb0(...)`
  - for controls inside that range, it first runs:
    - `FUN_8239CDD8_validate_facegen_control_page_state(...)`
    - `FUN_826A1C00_check_facegen_edit_gate(...)`
    - virtual gate `(*obj + 0x19C)(..., 0)`
  - it then looks up runtime control metadata through `func_0x82422920(...)`
    - reads flags from `metadata + 0x60`
    - reads a threshold/weight byte from `metadata + 0x65`
    - compares the stored runtime control ID at `metadata + 0x63`
  - it calls `func_0x82478690(...)` and may then walk up to `0xF` control slots, reapplying changes through:
    - `func_0x8239ccb0(...)`
    - `func_0x826D04B8(...)`
    - `func_0x826A1670(...)`

There is also now a clean relationship to the earlier control-ID enumerator:

- `FUN_824A2470_enumerate_facegen_control_id(4, index)` returns `index + 0x19`
- page/group `4` is therefore exactly the 7-control range `0x19 .. 0x1F`

The next layer down is now partly closed too:

- `FUN_82422920_lookup_active_facegen_control_metadata(base, slot)`
  - is just a bounded 15-entry lookup
  - returns `base[(slot + 0x11)]`
  - operationally, that is the active metadata slot table the special page uses
- `FUN_82478690_prepare_special_facegen_page_propagation(...)`
  - zeroes two output flags
  - draws a random value modulo `100`
  - when `param_6 == 0`, compares against a scaled threshold:
    - `param_3 * 0.01 * (global + param_5)`
  - when `param_6 != 0`, compares against an additive threshold:
    - `param_3 + param_5`
  - sets one of two propagation flags based on those thresholds
  - then overrides/masks them using a mode field at `param_2 + 0x138`
    - `1`: force first flag only
    - `2`: force second flag only
    - `3`: clear both
- `FUN_826D04B8_apply_facegen_control_via_primary_path(...)`
  - consumes the metadata returned by `FUN_82422920`
  - does not treat it as a bare coefficient index
  - reads structured parameters from that metadata, including:
    - `+0x88 / +0x8C / +0x90`
    - `+0x94 / +0x98 / +0x9C` as three floats
      - these are multiplied by `0.017453292` before a transform-builder call, so they look angle-like and likely represent degrees
    - optional resources at `+0x7C / +0x80`
    - enable/weight fields at `+0x78 / +0x84`
  - uses those values to build and apply a transform-oriented intermediate object before final downstream application
- `FUN_826A1670_apply_facegen_control_via_alternate_path(...)`
  - is currently only closed as a thin wrapper around `func_0x826A0B78(...)`
  - it does not yet add a contradictory interpretation to the primary path

Important interpretation boundary:

- this helper is not the final EGT texture-morph loop
- it is an upstream control-application path that runs before the final coord/blob values are consumed by `ApplyCoordinateTexturingToMesh`
- the exact human-readable names for control IDs `0x19 .. 0x1F` are still not directly named in the Xenon decomp
- however, lining that range up with the locally generated `si.ctl` tables strongly suggests this is the skin-color page that includes `Skin Shade` and the adjacent tint controls
  - that last name mapping is still an inference, not a direct symbol-backed fact

Operational conclusions:

- there is still no recovered proof that the final texture loop treats raw FGTS basis index `0` specially
- but there is now recovered proof that the engine has a dedicated upstream application path for a 7-control page that overlaps the skin-color controls
- that special page path uses structured metadata and stochastic propagation logic, not just a raw coefficient write
- that makes the current renderer's “build a raw merged `float[]` from ESM and feed it straight to the morpher” model even less faithful for the skin/mouth/lip cases

### 5a. Face coord writes are differential, not plain absolute stores

The corrected raw-PDB pass also clarifies how the generic coord manager writes values:

- `BSFaceGenManager::GetFaceCoordValue(page, subGroup, index)`
  - validates `page` and `subGroup`
  - resolves the descriptor record from the manager tables
  - returns the current float value for that coord slot
- `BSFaceGenManager::SetFaceCoordValue(page, subGroup, index, requestedValue)`
  - first calls `GetFaceCoordValue(...)`
  - computes `delta = requestedValue - currentValue`
  - packages that delta through `func_0x824894f0(...)`
  - writes it back with `func_0x82488478(...)`

Operational conclusions:

- at least one engine-level coord write path is explicitly differential relative to current state
- that strengthens the case that upstream control application is doing more than “overwrite raw FGTS[n] with slider value”
- it also makes the skin-page special-case path more relevant to the remaining mouth/lip mismatch than a generic late morph-loop bug

### 5b. The engine also has a separate paired-attribute write path

The corrected raw-PDB pass now also closes `BSFaceGenManager::SetFaceCoordAttribute`:

- `BSFaceGenManager::SetFaceCoordAttribute(value, which, coordBlob)`
  - does **not** write a scalar coord slot directly
  - first reads the other attribute component(s) for the same attribute pair through `func_0x82941288(...)`
  - replaces only the selected component
  - commits the full 2-float attribute pair through `func_0x82941608(...)`

That means the engine maintains at least two distinct upstream edit domains:

- scalar coord values through `Get/SetFaceCoordValue`
- paired attribute values through `Get/SetFaceCoordAttribute`

`TESNPC::RandomizeFaceCoord(...)` uses the paired-attribute path explicitly:

- it builds a temporary FaceGen coord blob from the race base through `func_0x8248b3f8(...)`
- it reads attribute group `0`, components `0` and `1`
- it randomizes and clamps them into the range `15.0 .. 65.0`
- it writes them back with `BSFaceGenManager::SetFaceCoordAttribute(..., 0, 0)` and `(..., 0, 1)`
- it then does the same for attribute group `1`, components `0` and `1`, clamped into the range `-2.0 .. 2.0`

By contrast, `BSFaceGenManager::CycleFaceGenCoordValue(...)` is centered on the scalar path:

- it reads one scalar coord via `BSFaceGenManager::GetFaceCoordValue(...)`
- writes it back through `BSFaceGenManager::SetFaceCoordValue(...)`
- then reapplies the result through `BSFaceGenManager::OffsetFaceGenCoord(...)`

Operational conclusions:

- the engine does not treat all upstream FaceGen edits as raw scalar coeff writes
- some appearance state lives in a separate paired-attribute channel before the final merged coord blob is consumed
- this still does **not** prove that the remaining Crocker / Jean-Baptiste mouth mismatch comes from the attribute channel specifically
- but it does prove the current renderer's “raw merged scalar texture coeff array” model is still missing at least one real upstream state domain

### 5c. The authored `RaceSexMenu` path edits the runtime coord blob in control space

The corrected raw-PDB Xenon pass now also closes the menu-side authored control path in
[facegen_racesexmenu_decompiled_pdb_xenon.txt](/c:/Users/mmc99/source/repos/Xbox360MemoryCarver/tools/GhidraProject/facegen_racesexmenu_decompiled_pdb_xenon.txt):

- `RaceSexMenu::OnFaceSliderChange`
  - only accepts option types `0x22` and `0x23`
  - rebuilds a temporary merged runtime FaceGen coord blob through `func_0x8243cd70(...)`
  - converts the current slider integer back to float with `value * 0.1`
  - writes it through `BSFaceGenManager::SetFaceCoordValue(...)`
  - re-applies the race base through `BSFaceGenManager::OffsetFaceGenCoord(...)`
- `RaceSexMenu::SliderRelease`
  - routes option types `0x22` and `0x23` through `OnFaceSliderChange`
  - has a separate special branch for option type `0x1a`
    - converts the slider to a value with `value * 5.55 + 9.45`
    - reads two components through `GetFaceCoordAttribute(...)`
    - clamps them into `15.0 .. 65.0`
    - writes them back through `SetFaceCoordAttribute(...)`
    - re-applies the race base and, if needed, copies the rebuilt blob back into the active NPC coord blob
- `RaceSexMenu::AddFaceSliders`
  - constructs authored sliders from an already-merged runtime coord blob
  - slider type `0x22` is populated from `GetFaceCoordValue(coordBlob, 0, 0, index)` over the dynamic page-0 count
  - slider type `0x23` is populated from a fixed set of page-1 scalar coord indices:
    - `0x1d`, `0x1c`, `0x1e`, `0x1f`, `0x08`, `0x09`, `0x12`, `0x1b`, `0x16`, `0x05`, `0x06`, `0x03`
- `RaceSexMenu::SynchronizeFaceSliders`
  - overload `0x825D5390` reads values back out of a rebuilt coord blob through `GetFaceCoordValue(coordBlob, page, 0, index)`
  - overload `0x825D5590` uses:
    - `param_2 == 0x22` -> sync page `0`
    - `param_2 == 0x23` -> sync page `1` through the `0x825D5390` helper
  - both paths convert stored float coord values back to UI integers with `value * 10`
  - both also clamp to each slider's stored UI min/max before updating the visible control state

Important inference boundary:

- the numeric page-1 indices above are symbol-backed facts from the raw-PDB decomp
- mapping those indices to human-readable slider names is still an inference from the local `si.ctl` control tables
- that inference is strong, because those indices line up with the locally generated texture-control list:
  - `0x1d` = `Skin Shade`
  - `0x1c` = `Skin Flushed / Pale`
  - `0x1e` = `Skin Tint Orange / Blue`
  - `0x1f` = `Skin Tint Purple / Yellow`
  - `0x16` = `Lips Flushed / Pale`
  - plus darker/lighter surrounding controls like eyebrows, eyeliner, blush, beard circle, and beard moustache

Operational conclusions:

- the authored/edit path is working in control space on page-0/page-1 coord values
- but that does not, by itself, prove there is a separate hidden runtime texture-coefficient representation
- the page-1 skin controls are still special upstream of the final EGT bake, so they remain a credible place for authored behavior differences
- however, the remaining mismatch can no longer be explained purely by "we skipped a one-time raw-FGTS to runtime-coord projection"

### 5d. `TESNPC::Load` record tags and `TESNPC::LoadFaceGen` populate the same coord-blob structure

The corrected raw-PDB pass now closes the relationship between the NPC record tags and the serialized FaceGen load path:

- `TESNPC::Load` directly parses multiple FaceGen-related tagged record chunks
- specifically, it has a shared load path for tag values:
  - `0x53474746`
  - `0x41474746`
  - `0x53544746`
- those tag values are best read as big-endian record names:
  - `FGGS`
  - `FGGA`
  - `FGTS`
  - that ASCII decoding is an inference from the tag values, but it is a strong one

The shared load path does the following:

- validates the chunk size is a multiple of `4`
- stores `count = chunkSize / 4`
- allocates descriptor-backed storage
- copies the raw float values into internal NPC storage
- indexes the destination descriptor as `iVar13 = iVar9 * 2 + iVar13`
  - the destination metadata lands at `npc + 0x154/+0x158 ... +0x19C/+0x1A0`
  - the destination storage pointers land at `npc + 0x144/+0x15C/+0x174/+0x18C`

That now lines up directly with `TESNPC::LoadFaceGen`:

- `LoadFaceGen` starts its nested loop from `puVar12 = (uint *)(npc + 0x158)`
- for each descriptor, it reads serialized floats and writes them back through `puVar12[-5]`
- that means the FaceGen stream path is walking the same descriptor-backed storage layout that `TESNPC::Load` populated from `FGGS / FGGA / FGTS`

This also lines up with the runtime consumers:

- `TESRace::GetFaceGenData` merges `actor + 0x144` or `actor->0x1A4` as the live actor delta coord blob
- `BSFaceGenManager::GetFaceCoordValue` resolves page/subgroup metadata and reads a float out of that descriptor-backed coord storage
- `BSFaceGenModel::ApplyCoordinateTexturingToMesh` then consumes the merged blob's third descriptor directly for texture morph coefficients

Operational conclusions:

- the NPC record tags and the `LoadFaceGen` stream are not two obviously different coefficient representations
- both are populating the same `+0x144` FaceGen coord blob structure used by the runtime merge and texture loop
- that means the earlier "raw loaded FGTS must still be projected into engine-ready runtime coords" theory is no longer the strongest explanation
- the remaining mismatch is now more likely to live in:
  - authored control application before values are saved
  - merge/apply semantics on the runtime path
  - or an encode/decode/output boundary

### 5e. `BSFaceGenManager::ScaleFaceCoord` is only a uniform blob-scale helper

The corrected raw-PDB Xenon pass also closed `BSFaceGenManager::ScaleFaceCoord(...)`.

Recovered behavior:

- it returns immediately when the coord blob pointer is null or the scale is outside `0.0 .. 1.0`
- otherwise it walks the same 4-descriptor coord blob layout
- for each selected descriptor it multiplies every stored float by the same scalar
- it does not branch on individual control IDs or texture indices
- it does not add per-channel bias, per-index clamps, or skin-page-specific logic

Operational conclusion:

- there is a real engine-side coord-scaling helper
- but it is only a uniform multiplier over stored coord values, not a hidden mouth/lip/tint special case

### 6. EGT deltas are accumulated in native EGT space

`BSFaceGenModel::ApplyCoordinateTexturingToMesh` accumulates morph deltas before any upscale to the final diffuse resolution.

Operational conclusion:

- accumulation happens at native EGT dimensions
- resampling is a later step
- comparing native generated EGT to shipped 256x256 facemods is the correct first-stage verification

### 7. The multiply path is quantized before normalization

The MemDebug decompilation for `BSFaceGenModel::ApplyCoordinateTexturingToMesh` is consistent with:

```text
coeff256 = trunc(coeff * 256.0)
scale256 = trunc(morphScale * 256.0)
acc += deltaByte * coeff256 * scale256
delta = acc / 65536.0
```

Operational conclusion:

- the engine does not appear to be doing one pure float multiply per texel
- it quantizes coefficient and morph scale to 1/256 steps before the final normalization

### 8. The runtime texture loop is generic across morph indices

Within `BSFaceGenModel::ApplyCoordinateTexturingToMesh`, the engine iterates texture morphs linearly and applies the same accumulation rule to each one.

Operational conclusions:

- there is no decomp-backed late special-case branch for "texture coefficient 0" in the EGT application loop itself
- the remaining mouth and lip mismatch is therefore more likely to come from upstream coord population, coefficient semantics, or offline facemod generation than from a last-minute runtime special case in the texture loop

### 9. The on-disk EGT assets are tightly packed

The sampled shipped `.egt` files match the exact packed-size formula:

```text
64 + morphCount * (4 + 3 * width * height)
```

Examples checked locally:

- `headhuman.egt`: `256x256`, `50` morphs, exact match
- `headfemale.egt`: `256x256`, `50` morphs, exact match
- `eyelefthuman.egt`: `64x64`, `50` morphs, exact match

Operational conclusions:

- the shipped EGT assets do not contain extra per-row padding bytes on disk
- the current parser's tight packed-file assumption is consistent with the sampled assets
- the remaining mismatch is less likely to be caused by a simple on-disk row-padding bug

### 10. The facemod compression step is now partially sourced

The fresh Xenon decompilation of `BSFaceGenImage::GetCompressedImage` shows the float EGT image being clamped and quantized before it becomes the shipped facemod texture bytes.

The caller in `BSFaceGenModel::ApplyCoordinateTexturingToMesh` passes:

```text
min = -255.0
max =  255.0
scale = 0.5
```

The recovered encode path is consistent with:

```text
clamped = clamp(nativeDelta, -255.0, 255.0)
integral = floor(clamped)
encodedByte = trunc((integral + 255.0) * 0.5)
```

Operational conclusions:

- the `0.5` factor is not just an empirical fit in the morph loop
- it participates in the float-image -> compressed facemod encode stage
- neutral `0.0` does not encode to a simple `128`-centered round-trip path
- odd-valued deltas and negative deltas are quantized asymmetrically because the engine floors before the `0.5` scale

### 11. Shipped facemod output is a different stage than final applied diffuse

The shipped `textures\characters\facemods\<plugin>\<formid>_0.ddx` files are standalone EGT delta textures. They are not the fully applied head diffuse texture.

Operational conclusion:

- stage 1 verification should compare generated EGT delta vs shipped facemod delta
- stage 2 comparison should inspect the final applied diffuse result separately

### 12. The GECK/offline bake path reuses the same quantized accumulation core

The focused GECK editor pass adds a stronger offline clue, even though the first clean caller is body-mod oriented rather than explicitly `facemods\...` oriented.

Recovered editor-side chain:

- `FUN_00587b20`
  - resolves an output texture path
  - loads the mesh
  - prepares FaceGen export state
  - creates a missing mod texture "from scratch"
  - then calls `FUN_00695b50(...)`
- `FUN_00584550`
  - builds a stem like `%08XModBodyMale` or `%08X_%08XModBodyFemale`
- `FUN_00584620`
  - resolves `data\Textures\Characters\BodyMods\<plugin>\<stem>.dds`

Important scope note:

- this proves the GECK/editor has an offline texture bake path that is not just "apply the already-baked runtime texture"
- it does **not** yet prove that this exact caller is the final head facemod exporter, because the resolved path here is `BodyMods`, not `facemods`

The useful part is the shared bake helper:

- `FUN_00695b50` references `BSFaceGenModel.cpp`
- it walks `0x58`-byte FaceGen texture records
- it accumulates signed-byte deltas into an `int` working buffer
- it converts that buffer back to float with `1.5258789e-05` (`1 / 65536`)

The recovered inner shape is consistent with:

```text
for each texture basis record:
    accum[channelPixel] += signedDeltaByte * quantizedCoeff * quantizedScale

nativeDelta[channelPixel] = accum[channelPixel] / 65536.0
```

Operational conclusions:

- the editor/offline bake path is using the same broad quantized accumulation model as the Xenon runtime path
- this reduces the likelihood that the remaining Crocker / Jean-Baptiste mouth-color mismatch is caused by the basic `int * int / 65536` accumulation rule itself
- the remaining risk shifts further toward:
  - how the coefficient/source arrays are populated before the loop
  - how the basis records are selected/mapped
  - a head-specific bake/output stage that we have not fully isolated yet

### 13. The actual GECK head exporter is now identified

The first useful GECK head-specific export chain is:

- `FUN_00587880`
  - builds the FaceGen biped/skinned export nodes
- `FUN_00586ea0`
  - builds the head export texture-input descriptor
- `FUN_00691b10`
  - applies and bakes head textures onto those nodes
- `FUN_00695b50`
  - performs the shared quantized EGT accumulation and float-image bake
- `FUN_00574500`
  - writes the final `data\Textures\Characters\FaceMods\...` DDS/TGA outputs

The strongest head-export evidence is in `FUN_00574500`, which references:

- `data\Textures\Characters\FaceMods\%s`
- `data\Textures\Characters\FaceMods\%s\%08X_%i.dds`
- `data\Textures\Characters\FaceMods\%s\F%08X_%08X_%i.dds`
- `data\Textures\Characters\FaceMods\%s\M%08X_%08X_%i.dds`

Operational conclusions:

- the earlier `BodyMods` caller was a real offline bake path, but not the main head facemod writer
- the GECK now has a directly recovered head exporter path that terminates in `FaceMods`
- that exporter still routes through the same `FUN_00695b50` accumulation core rather than a separate head-only math implementation

### 14. The newly decompiled upstream helpers are plumbing, not the tint-math core

`FUN_00571b00` is not texture math. It is a small ref-counted slot setter:

- it updates an indexed pointer array
- it maintains non-null counts
- it increments/decrements object refs

Operational conclusion:

- `FUN_00571b00` is bookkeeping only and is not a plausible source of mouth-region color drift

`FUN_00586ea0` is the meaningful upstream builder. It populates the temporary head export descriptor consumed by `FUN_00691b10`.

Recovered responsibilities:

- selects the current/base face texture object into descriptor `+0x80`
  - from current head state when `param_1 != 0`
  - otherwise from a per-sex fallback at `in_ECX + 0xB0 + sex * 4`
- populates descriptor `+0x84` with a color/tint value
  - from `param_1 + 0x20C` on the actor-backed path
  - otherwise from a per-sex fallback byte at `in_ECX + 0xB8 + sex`
- populates descriptor `+0x88`, `+0x8C`, and `+0x90` from current head/editor state
  - `+0x88` from `param_1 + 0x1F0`
  - `+0x8C` from `param_1 + 0x1F4` with fallback to `Characters\Eyes\EyeDefault.dds`
  - `+0x90` from `FUN_0055d9e0()`
- fills the descriptor slot arrays from per-sex editor tables at:
  - `in_ECX + 0xCC + (slot + sex * 8) * 0x24`
  - `in_ECX + 0x30C + (slot + sex * 8) * 0x1C`
- optionally attaches overlay/accessory state when `DAT_00ED8264 != 0`
- flattens linked extra-texture state from `param_1 + 0x210` and nested child lists at `+0x6C`

It also writes descriptor pointers at `+0xD8` and `+0xDC` from:

- `base + 0x1A4`
- `base + 0x1C8`

Those looked promising at first, but downstream use in `FUN_00691b10` shows they are consumed for eye-mesh setup (`FaceGenEyeLeft` / `FaceGenEyeRight`) rather than as the shared EGT accumulation inputs.

Operational conclusions:

- these helpers still do not show a recovered special case for raw FGTS basis index `0` in the final shared bake loop
- but the newly isolated `0x826D9B10` path does show a real upstream special case for the 7-control range `0x19 .. 0x1F`
- they do not show mouth-only or lip-only branches before the shared bake loop
- the strongest remaining upstream candidates are now the data sources feeding:
  - the actor/editor fields at `+0x1F0 / +0x1F4 / +0x20C / +0x210`
  - the per-sex editor tables at `+0xCC` and `+0x30C`
- the field cluster at `+0x1EC / +0x1F0 / +0x1F4 / +0x20C / +0x210` now looks like a coherent current-head-state block rather than unrelated scratch state

Additional selector/accessor closures:

- `FUN_0055d9e0`
  - is just the current sex/head variant selector
  - when the owning object type byte is `0x2A ('*')`, it returns `(*(byte *)(obj + 0x58) & 1)`
  - otherwise it returns `0xFFFFFFFF`
- `FUN_0056f440`
  - is just `return *(void **)(obj + 0x1EC);`
- `FUN_0056a310`
  - checks that the candidate source is present in the current actor/editor list at `param_1 + 0x144 + 0xA8`
  - then gates it by the current sex flag from `FUN_0055d9e0()` against source flags at `source + 0x78`
- `FUN_0056f390`
  - clears the temporary descriptor
  - then rebuilds it from the current actor/editor state
  - it selects the source table from `actor + 0x144 + (sex ? 0x694 : 0x714)`
  - and uses the active head export source list at `obj + 0x1E8`, falling back to `obj + 0x168`

Operational conclusion:

- the selector/accessor layer is now largely closed and appears to be source/sex selection plumbing rather than hidden blend math
- that makes it less likely that the remaining standalone EGT mismatch is caused by a missed branch in these helper functions

### 15. Descriptor field `+0x84` is still unresolved and should not be treated as a settled tint input

There is real conflicting evidence around the source field copied into descriptor `+0x84`.

What is directly recovered:

- `FUN_00586ea0` copies `*(uint *)(param_1 + 0x20C)` into descriptor `+0x84`
- `FUN_00691b10` later reads descriptor `+0x84` bytewise in a way that looks color-like:

```text
value = *(uint *)(descriptor + 0x84)
r = (value & 0xff) / 255.0
g = ((value >> 8) & 0xff) / 255.0
b = ((value >> 16) & 0xff) / 255.0
```

But separate GECK evidence shows the owning object also uses offsets `+0x20C .. +0x214` as five `ushort` control points:

- defaults initialize them as `0, 600, 1400, 2600, 5000`
- `FUN_0089dcf0` writes them as:
  - `+0x20C`
  - `+0x20E`
  - `+0x210`
  - `+0x212`
  - `+0x214`
- those values are later interpolated as a monotonic curve, not an obvious RGB color

Operational conclusions:

- the earlier “this is definitely a separate RGB tint stage” conclusion was too strong
- descriptor `+0x84` may be:
  - a true packed color field that overlaps other editor data
  - a mixed-use/union-like field
  - or a decompilation interpretation that is semantically incomplete
- this field remains relevant to rendered-head comparisons
- it is still unlikely to explain standalone shipped-facemod EGT mismatches by itself, because it sits outside the shared `FUN_00695b50` accumulation loop

### 16. The observed `descriptor +0x84` consumer in `FUN_00691b10` is not the face-skin path

The narrowed `FUN_00691b10` pass makes one ambiguity materially smaller.

Recovered node handling:

- the function resolves named nodes such as:
  - `FaceGenEyeLeft`
  - `FaceGenEyeRight`
  - `FaceGenAccessory`
  - `FaceGenHair`
- the bytewise unpack of `descriptor + 0x84` only appears in the branch that handles:
  - `FaceGenHair`
  - `FaceGenAccessory`
- the eye nodes take separate branches
- this narrowed consumer does not show a corresponding face-skin branch using `descriptor + 0x84`

Operational conclusions:

- the currently observed `descriptor + 0x84` path is not a strong candidate for mouth/lip EGT drift
- it looks much more like accessory/hair tint handling than face-skin blend math
- this pushes the remaining Crocker / Jean-Baptiste mismatch back toward the true face texture source path

### 17. The current-head-state block now has identifiable init/copy/load helpers

The GECK sweep found several helpers that treat `+0x1EC / +0x1F0 / +0x1F4 / +0x20C / +0x210` as one coherent block.

`FUN_005721b0` behaves like a state initializer:

- zeros:
  - `+0x1EC`
  - `+0x1F0`
  - `+0x1F4`
  - `+0x1E8`
  - `+0x208`
- clears and rebuilds the per-sex source tables around `+0x168`
- initializes `+0x20C` to `0x19324B`
- resets the linked-list root at `+0x210`

`FUN_00571cc0` behaves like a state-copy helper:

- replaces the destination block's:
  - `+0x1EC`
  - `+0x1F0`
  - `+0x1F4`
  - `+0x20C`
  - linked-list contents at `+0x210`
- then rehydrates the per-sex texture/source tables via `FUN_0068e960(...)`

`FUN_00575d70` behaves like a serialized loader:

- it parses tagged NPC/masterfile data
- the matched field writes land in the same block:
  - `+0x1EC`
  - `+0x1F0`
  - `+0x1F4`
  - `+0x20C`
  - `+0x210`

Operational conclusions:

- `+0x20C` now looks more like a default packed state value than a hidden face-EGT coefficient source
- `+0x1EC`, `+0x1F4`, and the linked `+0x210` list are better interpreted as current head-source selections than as transient export-only scratch fields
- the remaining open question is not whether these fields belong together, but which parts of this block affect face skin versus eyes, hair, and accessories

## Recovered Pseudocode

The currently recovered runtime path is closer to:

```csharp
FaceGenCoord baseCoord = actor.BaseFaceGenCoordForCurrentSex;   // actor->0x120 + 0x408/0x468
FaceGenCoord actorDelta = actor.OverrideFaceGenCoord ?? actor.InlineFaceGenCoord; // actor->0x1A4 or actor+0x144
FaceGenCoord mergedCoord = MergeFaceGenCoord(0.0, baseCoord, actorDelta, param5: 0);

RaceFaceGenData raceData = TESRace.GetFaceGenData(...);
raceData.Coord = mergedCoord;

FaceGenCoordDescriptor textureCoord = raceData.Coord.TextureSymmetric; // +0x30 / +0x40 / +0x44

for each morphIndex:
    int coeff256 = trunc(textureCoord[morphIndex] * 256.0f);
    int scale256 = trunc(egtMorph[morphIndex].Scale * 256.0f);

    for each texel:
        accumR[texel] += deltaR[texel] * coeff256 * scale256;
        accumG[texel] += deltaG[texel] * coeff256 * scale256;
        accumB[texel] += deltaB[texel] * coeff256 * scale256;

for each texel:
    nativeDelta = accum / 65536.0f;

for each texel:
    clamped = clamp(nativeDelta, -255.0f, 255.0f);
    integral = floor(clamped);
    encodedByte = trunc((integral + 255.0f) * 0.5f);

generatedEgt = EncodeEngineCompressed(nativeDelta);
finalDiffuse = Apply(nativeDelta, baseDiffuse);
```

Important open points:

- the fresh Xenon evidence now sources the `0.5` factor in the compression stage
- it does not yet prove that a separate `0.5` exists earlier in morph accumulation
- the current renderer still bypasses the engine's coord-blob assembly step and instead builds texture coefficients directly from ESM-level data
- `TESNPC::LoadFaceGen` is now shown to deserialize the inline coord blob at `actor->0x144` directly from the record stream, but the exact on-disk record labels for each descriptor block are still not named
- the exact mapping from runtime control IDs `0x19 .. 0x1F` to named `si.ctl` controls is still inferential rather than directly symbol-backed
- within the special-page metadata path, the exact semantic meaning of fields `+0x78 / +0x7C / +0x80 / +0x84 / +0x88 .. +0x9C` is still only partially named
- the optional heap override path at `actor->0x1A4` is still only partially closed outside save/load flows
- on the GECK side, the exact meaning of the head/editor fields at `+0x1F0 / +0x1F4 / +0x20C / +0x210`, the object at `+0x1EC`, and the per-sex tables at `+0xCC / +0x30C / actor+0x694 / actor+0x714` still needs to be closed
- specifically, the semantic meaning of `+0x20C .. +0x214` is still unresolved because the same region looks both curve-like and color-like in different code paths
- however, the narrowed `FUN_00691b10` consumer evidence now reduces the likelihood that `+0x20C -> descriptor +0x84` is the face-skin mismatch source

## Current Code Delta Table

| Area | Current Code | Recovered Behavior | Match |
| --- | --- | --- | --- |
| `NpcAppearanceFactory` texture coeff selection | direct ESM-level additive `npc + race` | runtime deserializes the inline NPC coord blob via `TESNPC::LoadFaceGen`, optionally substitutes `+0x1A4`, then merges race base coord + actor delta coord and reads texture coeffs from that merged coord | partial |
| `NpcFaceGenCoefficientMerger.Merge` | additive merge over raw coefficients | additive merge exists, but over runtime FaceGen coord blobs rather than proven raw ESM arrays | partial |
| `FaceGenTextureMorpher` coefficient input | receives a plain `float[]` built before the morph loop | runtime morph loop reads the third descriptor of the merged FaceGen coord blob at `+0x30 / +0x40 / +0x44` | partial |
| upstream skin-color control handling | no page/control-specific preprocessing; raw merged coefficients go straight into morphing | recovered helpers `0x826D9B10`, `0x82478690`, and `0x826D04B8` special-case control page `0x19 .. 0x1F` with structured metadata and propagation flags before the final coord/blob is consumed | no |
| `FaceGenTextureMorpher` accumulation math | quantized `int * int / 65536` style accumulation | quantized `int * int / 65536` style accumulation | yes |
| `FaceGenTextureMorpher` delta encoding | clamp `[-255,255]`, `floor`, then `trunc((value + 255) * 0.5)` | clamp `[-255,255]`, `floor`, then `trunc((value + 255) * 0.5)` | yes |
| `FaceGenTextureMorpher` accumulation space | native EGT, then upscale | native EGT, then upscale | yes |
| `EgtParser` on-disk morph packing | assumes `scale + R plane + G plane + B plane`, tightly packed | sampled shipped `.egt` assets match the exact packed-size formula with no extra row padding | yes |
| shipped-facemod verification target | standalone EGT delta | standalone EGT delta | yes |
| applied diffuse export | post-EGT applied diffuse | post-EGT applied diffuse | yes |

## Investigation Harness

The repository now includes an internal investigation-only replay harness:

- `NpcEgtBlendInvestigator`

It replays two policies without changing the normal render pipeline:

- `current`
  - texture coefficients: additive `npc + race`
  - accumulation: current float path
  - delta encoding: recovered `GetCompressedImage` clamp/floor/half-scale path
- `recovered`
  - texture coefficients: additive `npc + race`
  - accumulation: `EngineQuantized256`
  - delta encoding: recovered `GetCompressedImage` clamp/floor/half-scale path

Per NPC, it exports:

- `shipped_egt.png`
- `current_generated_egt.png`
- `current_diff_egt.png`
- `current_applied_diffuse.png`
- `recovered_generated_egt.png`
- `recovered_diff_egt.png`
- `recovered_applied_diffuse.png`
- `current_vs_recovered_applied_diffuse_diff.png`
- `metadata.txt`

The sample-backed test writes the fixed 4-NPC bundle to:

- `artifacts\egt-blend-investigation\`

## Validation Set

The fixed validation set for this pass is:

- Boone `0x00092BD2`
- Sunny Smiles `0x00104E84`
- Ambassador Crocker `0x00112640`
- Violet `0x000F56FD`

## Investigation Status (March 2026)

### What Has Been Confirmed

| Component | Engine behavior | Our implementation | Match |
|-----------|----------------|-------------------|-------|
| Coefficient merge | `merged[i] = npc[i] + race[i]` (additive) | `NpcFaceGenCoefficientMerger.Merge` | ✅ Exact |
| Coeff quantization | `(int)(coeff * 256f)` | `AccumulateNativeDeltasQuantized256` | ✅ Exact |
| Scale quantization | `(int)(morph.Scale * 256f)` | `AccumulateNativeDeltasQuantized256` | ✅ Exact |
| Accumulation | `int32 += (int8)delta * coeff256 * scale256` | Same int32 path | ✅ Exact |
| Normalization | `float = accum * 1.5258789e-05` (1/65536) | `accum * (1f / 65536f)` | ✅ Exact |
| Encode clamp | `clamp(delta, -255, 255)` | `Math.Clamp(delta, -255f, 255f)` | ✅ Exact |
| Encode floor | PPC `fctidz+fcfid+fsel` = floor | `MathF.Floor(clamped)` | ✅ Exact |
| Encode output | `(byte)((floor + 255) * 0.5)` | `(byte)((integral + 255) * 0.5f)` | ✅ Exact |
| FGTS coefficients | Xbox ESM == PC ESM for both NPC and race | Parsed identically | ✅ Exact |
| DDX wrapper overhead | 4–11% of total error | N/A — compression floor | ✅ Measured |

### DDX Isolation Results

Both Xbox DDX and PC DDS facemods use DXT1 as the underlying compression format. The DDX wrapper adds only 4–11% of total error:

| NPC | Xbox vs PC MAE | Xbox vs Gen MAE | PC vs Gen MAE | DDX fraction |
|-----|---------------|-----------------|---------------|-------------|
| Boone (0x00092BD2) | 0.055 | 1.378 | 1.371 | 4.0% |
| Sunny Smiles (0x00104E84) | 0.074 | 1.318 | 1.283 | 5.6% |
| Crocker (0x00112640) | 0.084 | 1.697 | 1.675 | 5.0% |
| Violet (0x000F56FD) | 0.110 | 0.983 | 1.001 | 11.2% |

### Region-Based Error Analysis — Structural Mismatch Confirmed

Region-based MAE shows error concentrated in mouth/lip areas, ruling out DXT1 as the sole explanation (DXT1 would produce spatially uniform error):

| NPC | Overall MAE | Mouth MAE | Lower Face MAE | Neck MAE | Mouth/Overall |
|-----|------------|-----------|----------------|----------|---------------|
| Crocker | 1.70 | **3.12** | 2.65 | 1.82 | **1.84x** |
| Jean-Baptiste | 2.55 | **5.81** | 4.47 | 3.46 | **2.28x** |

Per-morph mouth basis contribution analysis (Crocker):

- **Morph 0 ("Skin Shade Dark / Light")** dominates with mouth MAE contribution = 57.87 — 4x larger than any other morph
- **Error alignment for morph 0 is strongly negative** (-25.92) — generated output systematically diverges where this morph pushes
- Large skin-specific texture controls present: Skin Shade=-3.52, Lips Flushed/Pale=-5.70, Lipstick=-7.83
- These map to FaceGen control indices 0x19–0x1F (skin controls)

### Skin Control Hypothesis — RULED OUT (March 2026)

Full Xenon decompilation of the bake path from the Aug 22 MemDebug PE confirms:

1. **Skin controls 0x19–0x1F special handling is GECK UI only.** The function at `0x826D9B10` (Apply/resolve FaceGen control value) special-cases controls 0x19–0x1F with page validation, edit gates, exclusive-value logic, and bit-6 flag toggling for control 0x1a — but this is called from the interactive slider system (`BSFaceGenManager::GetFaceGenCoord`, `CycleFaceGenCoordValue`, etc.), NOT from the bake path.

2. **The bake accumulation (`ApplyCoordinateTexturingToMesh`) is identical to our implementation.** Decompiled from `[0004:00241970]` VA `0x82491970` (1668 bytes). Inner loop: `accum += (sbyte)delta * (int)(coeff*256) * (int)(scale*256)`. Normalization: `float = accum * 1.5258789e-05`. No special-casing of any morph index.

3. **The coefficient source is raw ESM FGTS.** `TESNPC::GetFaceCoord` (`[0004:001ECD70]`) reads NPC offset coefficients from `npc+0x1a4` (or inline at `npc+0x144`), race base from `race+0x408` (male) or `race+0x468` (female), and merges via `MergeFaceGenCoord` (additive). This is the same data path as our implementation.

4. **`InitFaceGenCoord` confirms stride=1** for all three pages (FGGS/FGGA/FGTS). The texture page at coord+0x30 uses count=50, stride=1. The coefficient read in `ApplyCoordinateTexturingToMesh` is `coeffArray[1 * morphIndex]` — a simple 1:1 mapping.

5. **EGT files are byte-identical between Xbox and PC.** All 12 EGT files in `Fallout - Meshes.bsa` verified by MD5 hash. No per-NPC EGT files exist in the base game.

**All open questions from the skin control hypothesis are now answered definitively. The entire bake pipeline matches our implementation exactly.**

New decompilation artifacts:
- `tools/GhidraProject/facegen_texture_bake_decompiled.txt` — ApplyCoordinateTexturingToMesh, GetCompressedImage, GetUncompressedImage, TESNPC::GetFaceCoord, GetOffsetFaceCoord, SaveFaceGen, EGTData functions, ApplyMorph functions
- `tools/GhidraProject/facegen_coord_apply_decompiled.txt` — func_0x826D9B10 (the skin control special path — UI only)

### Remaining Error Attribution

With the bake pipeline confirmed identical, the remaining ~1.7 MAE structural mismatch has only two possible explanations:

1. **DXT1 block compression** — the shipped facemods are DXT1-compressed, our output is uncompressed ground truth. DXT1 is lossy with 4:1 compression ratio, typically producing MAE ~1.5–2.0.

2. **DXT1 is NOT spatially uniform** — despite being block-based, DXT1 error is content-dependent. Each 4×4 block picks 2 reference colors and interpolates. Regions with high-contrast edges (mouth/lips with strong skin-shade morphs) will have higher compression error than smooth gradients. This explains the 1.8–2.3x mouth/overall MAE ratio.

The mouth concentration of error is consistent with DXT1 behavior because:
- Morph 0 (Skin Shade) has the largest magnitude in the mouth region (merged coeff = -3.52)
- The mouth area has the steepest color gradients (lips vs surrounding skin)
- DXT1's 2-endpoint palette per 4×4 block handles smooth gradients well but high-contrast transitions poorly
- Darker skin (Crocker, Jean-Baptiste) pushes morph 0 harder, creating sharper gradients → higher DXT1 error

### Open Questions

None remaining for the texture bake path. The investigation is complete.

### Artifacts

- `artifacts/egt-ddx-isolation/` — DDX isolation comparison (Xbox DDX vs PC DDS vs generated, per NPC)
- `artifacts/egt-ddx-isolation/{formid}_{editorid}/coefficients.txt` — dumped float[50] arrays
- `artifacts/egt-region-diagnostics/` — region-based error with per-morph contributions and texture controls
- `artifacts/egt-blend-investigation/` — original blend investigation artifacts

## Authoritative Reconciliation (2026-03-14)

This section supersedes the older "Investigation Status (March 2026)" block above,
the user-provided stale handoff summary, and
`C:\Users\mmc99\.claude\plans\moonlit-noodling-narwhal.md` wherever they conflict.

### Source Precedence

Use these sources in order:

1. raw-PDB Xenon artifacts plus current repo code
2. this reconciliation section
3. current verifier artifacts from `artifacts/egt-handoff-reconcile-3npc.*`
4. older notes in this file and external plan notes

### Contradiction Matrix

| Claim | Older note(s) | Current evidence | Classification | Reconciled conclusion |
| --- | --- | --- | --- | --- |
| PNAM/UNAM is the top remaining lead | User handoff summary recommends tracing PNAM/UNAM first | PNAM/UNAM is parsed into `RaceRecord`, but current appearance/verification flow drops it before `RaceScanEntry`; raw-PDB symbol search for `facegen clamp` only surfaced `BSFaceGenManager::uiEGTClampSize`, not a record-backed PNAM/UNAM consumer | `still unverified` | PNAM/UNAM is worth keeping on the list, but it is not the primary next-session target unless a concrete consumer is recovered |
| Raw `FGGS/FGGA/FGTS` needs a separate runtime projection into engine-ready coords | Older notes and prior hypotheses treated this as likely | Raw-PDB Xenon evidence now shows `TESNPC::Load` record tags and `TESNPC::LoadFaceGen` feed the same descriptor-backed `+0x144` coord blob layout | `contradicted by newer Xenon evidence` | Do not spend the next session on a generic raw-FGTS-to-runtime projection theory |
| The bake path is closed and the mismatch is fully explained | `moonlit-noodling-narwhal.md` and the stale summary trend toward "done" | Current spec and current verifier still show structured mouth/lip/skin drift and no exact matches; the repo still carries investigation helpers and diagnostics for the mismatch | `stale` | The issue is still open |
| DDX/DXT1 was fully ruled out | User handoff summary says DXT1 was explicitly rejected as the full explanation | Current evidence only rules it out as the sole explanation; DDX wrapper overhead is measured as small, but encode/decode/output boundary remains open | `current and evidence-backed` | Demote DDX/DXT1 as the primary theory, but do not mark the full encode/decode boundary as closed |
| DDX/DXT1 alone explains the remaining gap | Older Claude plan says the remaining ~1.3 MAE is purely DXT1 compression | Current region diagnostics, signed bias, and raw-PDB-backed investigation still point to structured mismatch beyond a simple "compression floor" story | `contradicted by newer Xenon evidence` | Treat compression as a contributor, not the full explanation |
| Skin control handling is fully ruled out because the special path is GECK UI only | Older notes treat the special skin page as irrelevant to authored NPCs | Raw-PDB Xenon evidence shows authored/runtime control application is richer than a plain raw-float write, but a direct causal path from that page logic to the remaining shipped facemod mismatch is still not closed | `still unverified` | Do not claim the skin-control/control-space boundary is closed; just stop treating the final morph loop as the likely culprit |
| The repo currently treats the renderer as complete | Older handoffs imply the pipeline is effectively solved | Current code still compares shipped facemods, emits diagnostics, and the spec still tracks unresolved causes | `contradicted by current repo state` | The repo treats the EGT mismatch as an active investigation |
| Channel permutation, coefficient source comparisons, region metrics, and morph ablation are "not investigated yet" | User handoff summary lists these as future work | `NpcFaceGenTextureVerifier` already runs them, and the current 3-NPC verifier log confirms live output for each | `stale` | These diagnostics are already active and should be treated as existing evidence, not future work |
| RMS clamp behavior is currently part of the verifier pass | Older notes discuss clamp/RMS testing as if it were a live verifier mode | Current verifier code does not run an RMS clamp sweep; current logs only emit RMSE as an error metric | `stale` | RMSE is reported, but RMS clamp experimentation is not an active verifier mode today |

### Current Repo-State Validation

Command rerun for reconciliation:

```text
dotnet run --project src/FalloutXbox360Utils -f net10.0 -- render npc verify-egt \
  "Sample/Full_Builds/Fallout New Vegas (PC Final)/Data/Fallout - Meshes.bsa" \
  --esm "Sample/ESM/pc_final/FalloutNV.esm" \
  --npc 0x00092BD2 --npc 0x0001816A --npc 0x000156F0 --limit 3 \
  --report artifacts/egt-handoff-reconcile-3npc.csv
```

Current outputs:

- `artifacts/egt-handoff-reconcile-3npc.csv`
- `artifacts/egt-handoff-reconcile-3npc.log`

Current metrics:

| NPC | FormID | MAE | RMSE | Max |
| --- | --- | ---: | ---: | ---: |
| CraigBoone | `0x00092BD2` | `1.3711` | `1.7125` | `12` |
| CGPresetAfricanAmericanF01 | `0x0001816A` | `1.6774` | `2.4261` | `24` |
| DoctorBarrows | `0x000156F0` | `1.5762` | `2.0788` | `18` |

Summary:

- verified: `3`
- failed: `0`
- exact RGB matches: `0`
- mean MAE(RGB): `1.5416`
- mean RMSE(RGB): `2.0725`
- worst MAE(RGB): `1.6774`
- worst max channel error: `24`

What the current verifier actively does:

- dumps NPC/race/merged FGTS coefficients
- compares `merged` vs `npc_only` vs `race_only`
- tries all 6 channel permutations
- emits region metrics with signed and absolute bias
- runs per-morph ablation

What it does not currently do:

- no active RMS clamp sweep
- no PNAM/UNAM-aware replay path

### PNAM/UNAM Audit

Current parser behavior:

- `ActorRecordHandler` parses `PNAM` and `UNAM` into `RaceRecord.FaceGenMainClamp` and `RaceRecord.FaceGenFaceClamp`
- representative parsed race values from a one-off `dotnet-script` read of `UnifiedAnalyzer`:

| Race | FormID | PNAM / FaceGenMainClamp | UNAM / FaceGenFaceClamp |
| --- | --- | ---: | ---: |
| Caucasian | `0x00000019` | `5.0` | `3.0` |
| Ghoul | `0x00003B3E` | `5.0` | `3.0` |
| AfricanAmerican | `0x0000424A` | `5.0` | `3.0` |

Current usage boundary in repo code:

- parsed: yes
- preserved in `RaceRecord`: yes
- carried into `RaceScanEntry`: no
- carried into `NpcAppearanceIndex`: no
- consumed by render/verify path: no

Operational conclusion:

- PNAM/UNAM is currently a parsed-but-unused field in the repo's NPC appearance and verification pipeline
- raw-PDB Xenon evidence has not yet identified a concrete runtime or offline bake consumer for these record fields
- therefore PNAM/UNAM should be demoted until a real consumer is recovered

### Next-Session Priority

Unless a concrete PNAM/UNAM consumer is recovered first, the next-session primary target should be:

1. the offline facemod bake/output path
2. the encode/decode/output boundary (`GetCompressedImage`, `GetUncompressedImage`, DDX/DDS/DDX-to-runtime path)
3. authored/runtime control-space behavior only where it directly touches saved or baked facemod output

PNAM/UNAM should not be the default next branch from this point.

### Eye Path Branch (2026-03-14)

The eye-specific reverse-engineering branch is now grounded enough to state two
things clearly.

First, coefficient-driven eye behavior is not limited to eye-facing textures or
late animation tracking. The raw-PDB Xenon eye-path decomp shows:

- `BSFaceGenManager::AttachEyesToHead`
  - loads/creates left and right eye meshes
  - calls `BSFaceGenModel::ApplyCoordinateToNewMesh` for each eye
- `BSFaceGenModel::ApplyCoordinateToNewMesh`
  - creates a fresh mesh
  - immediately calls `BSFaceGenModel::ApplyCoordinateToExistingMesh(..., param_4 = 1)`
- `BSFaceGenModel::ApplyCoordinateToExistingMesh`
  - consumes the active FaceGen coord blob
  - applies short XYZ morph deltas into mesh vertex data
  - may call `BSFaceGenManager::RefreshMeshFromBaseMorphExtraData(...)`

Operational conclusion:

- the engine does morph eye meshes from FaceGen coefficients
- the eye path is not just "attach a rigid eyeball mesh and assign a texture"

Second, the current renderer is close in spirit but not equivalent. In
`NpcExportSceneBuilder.AddEyes(...)`, the repo currently:

- loads the left/right eye NIFs
- applies `Path.ChangeExtension(eyePath, ".egm")` through `LoadAndApplyEgm(...)`
- attaches the result with a simple head translation
- assigns `npc.EyeTexturePath` to the eye submeshes

That means the current renderer already assumes eye geometry responds to the
same symmetric/asymmetric coefficients, but it does **not** mirror the engine's
`AttachEyesToHead -> ApplyCoordinateToNewMesh -> ApplyCoordinateToExistingMesh`
path. The strongest remaining eye-specific question is whether the current
direct `.egm` application is equivalent to the engine mesh path, or whether the
engine is doing extra work through:

- base-morph extra data refresh
- mesh partition/remap handling inside `ApplyCoordinateToExistingMesh`
- different attachment/basis handling than the renderer's simple translation

There is also now a concrete texture-side omission in the repo eye path. The
shipped mesh archive contains per-eye FaceGen assets such as:

- `meshes\characters\head\eyelefthuman.egm`
- `meshes\characters\head\eyelefthuman.egt`
- `meshes\characters\head\eyerighthuman.egm`
- `meshes\characters\head\eyerighthuman.egt`

and the equivalent female/child variants.

Current repo behavior:

- eye `.egm`: applied
- eye `.egt`: not applied anywhere in the render/export path

What is not yet closed is whether the engine eye path reaches those eye `.egt`
files during mesh creation/model load, or whether they are only used by a
separate path. So the current state is:

- skipping eye `.egt` in the repo is a real omission relative to the shipped asset set
- it is not yet proven whether that omission is part of the live engine eye path or a parallel authoring/runtime path

Separate from that, the decomp also isolated a runtime eye-tracking branch:

- `BSFaceGenAnimationData::SetEyePosition`
- `BSFaceGenAnimationData::UpdateEyeTracking`
- `BSFaceGenAnimationData::ModifyEyeHeadingAndPitchBasedOnExpression`

Those functions move eye heading/pitch for live gaze behavior. They should not
be confused with the authored coefficient-driven eye mesh bake path above.

New eye-path artifacts:

- `tools/GhidraProject/run_decompile_facegen_eye_path_pdb.py`
- `tools/GhidraProject/facegen_eye_path_decompiled_pdb_xenon.txt`

Recommended next eye-specific step:

1. compare `ApplyCoordinateToExistingMesh` against the repo's current
   `FaceGenMeshMorpher.Apply` path for the eye `.egm`
2. determine whether the engine's extra base-morph/mesh-data handling changes
   final eye vertex positions
3. only then decide whether eye rendering needs a behavior change

### Race Base Lifecycle Branch (2026-03-15)

The runtime probe changed the race-side question materially:

- NPC texture coeffs match the live game almost exactly for Crocker,
  Jean-Baptiste, and Sunny
- race texture coeffs do not
- so the next useful decompilation target was the lifecycle for the race-owned
  male/female FaceGen base blobs at `race + 0x468` and `race + 0x408`

New raw-PDB Xenon artifacts:

- `tools/GhidraProject/run_decompile_facegen_race_base_pdb.py`
- `tools/GhidraProject/facegen_race_base_decompiled_pdb_xenon.txt`

Target set closed in that pass:

- `TESRace::TESRace`
- `TESRace::~TESRace`
- `TESRace::InitializeData`
- `TESRace::ClearData`
- `TESRace::Load`
- `TESRace::Save`
- `TESRace::Copy`
- `TESRace::KillEGTData`
- `TESRace::GetFaceGenData`
- `TESRace::CreateHead`

Secondary symbol sweep results:

- `TESRace::fRaceGeneticVariation`
  - only surfaced as a dynamic initializer / atexit teardown in the current
    raw-PDB symbol scan
- `TESRace::bFaceGenTexturing`
  - same result
- lightweight caller sweep for the primary `TESRace::*` targets did not return
  useful callers in the current helper workflow

Operationally, that means neither `fRaceGeneticVariation` nor
`bFaceGenTexturing` earned promotion to a primary lead from this pass.

#### 1. `TESRace::InitializeData` is the first owner/initializer of the male and female base blobs

The constructor path now closes cleanly:

- `TESRace::TESRace` calls `TESRace::InitializeData`
- `TESRace::InitializeData` seeds both `race + 0x468` and `race + 0x408`
  from the default FaceGen coord template and finalizes them as descriptor-backed
  coord blobs

Operational conclusion:

- the male and female race FaceGen bases are first-class owned state on
  `TESRace`
- they are not lazily synthesized only at `GetFaceGenData` time

#### 2. `TESRace::Load` writes raw `FGGS` / `FGGA` / `FGTS` directly into those race-owned blobs

The race loader handles the FaceGen tags directly:

- `FGGS` (`0x53474746`)
- `FGGA` (`0x41474746`)
- `FGTS` (`0x53544746`)

and writes them into the race-local descriptor-backed storage for:

- male `FGGS` / `FGGA` / `FGTS` at `+0x468 / +0x480 / +0x498`
- female `FGGS` / `FGGA` / `FGTS` at `+0x408 / +0x420 / +0x438`

The same loader also tracks section/state tags:

- `FNAM` (`0x4d414e46`)
- `NAM0` (`0x304d414e`)
- `NAM1` (`0x314d414e`)
- `NAM2` (`0x324d414e`)

Recovered conclusion:

- the core `TESRace` load path is not using a separate hidden intermediate
  "race FGTS format"
- it is loading the serialized race FaceGen arrays directly into the runtime
  race-owned base blobs
- however, the loader does appear to have section/mirroring behavior around the
  `FNAM` / `NAM0` / `NAM1` / `NAM2` state that is richer than the repo's
  current "flip on empty `MNAM` / `FNAM`, then assign arrays directly" parser

That parser gap matters because both current repo readers:

- `ActorRecordHandler`
- `RaceRecordScanner`

currently only switch sex sections on empty `MNAM` / `FNAM` markers and do not
model any additional `NAM2`-style mirroring/default semantics.

#### 3. `TESRace::Copy` and `TESRace::Save` preserve the race base blobs directly

`TESRace::Copy`:

- directly copies `+0x468 -> +0x468`
- directly copies `+0x408 -> +0x408`
- carries the clamp-like fields with defaults `5.0` and `3.0`

`TESRace::Save`:

- serializes the male/female FaceGen arrays back out as `FGGS`, `FGGA`, and
  `FGTS`
- also writes the race link and clamp fields (`ONAM`, `YNAM`, `PNAM`, `UNAM`)

Operational conclusions:

- this pass did not recover a hidden arithmetic transform or scale step in the
  normal copy/save lifecycle
- the race FaceGen bases behave like ordinary serialized owned state

#### 4. `TESRace::KillEGTData` is cache invalidation, not a coeff transform path

`TESRace::KillEGTData` walks path-owned EGT-like resources and invalidates cached
entries by path identity.

Operational conclusion:

- `KillEGTData` does not explain the runtime race texture coeff drift
- it looks like cache teardown only, not a writer of the race base texture page

#### 5. `OlderRace` / `YoungerRace` links did not show up as direct race-base substitution inside the lifecycle pass

The race lifecycle pass recovered `ONAM` / `YNAM` serialization, but it did not
recover a direct "replace current race base blob with linked older/younger race
blob" path inside:

- `InitializeData`
- `Load`
- `Copy`
- `Save`
- `KillEGTData`

This does not prove linked races are irrelevant everywhere. It does mean the
core race lifecycle itself is not where that substitution is obviously happening.

#### 6. Cross-check against the runtime captures

Current empirical capture comparisons:

- Crocker / `AfricanAmericanOld`
  - NPC mean abs delta: `0.000001`
  - race mean abs delta: `0.889934`
- Jean-Baptiste / `AfricanAmerican`
  - NPC mean abs delta: `0.000002`
  - race mean abs delta: `1.008638`
- Sunny / `Hispanic`
  - NPC mean abs delta: `0.000001`
  - race mean abs delta: `1.515441`

Those captures establish:

- the runtime NPC texture page matches the repo's current NPC-side resolution
- the runtime race texture page does not match the repo's current race-side
  lookup

The new lifecycle decomp changes how that mismatch should be classified:

- `direct linked-race substitution`
  - not supported by the recovered core race lifecycle
- `transformed/scaled race-base write inside TESRace::Load/Copy/Save`
  - not supported by this pass
- `post-load mutation of the race base after TESRace::Load`
  - still plausible
- `repo parser/scanner not reproducing race load semantics`
  - now strongly plausible

#### 7. Updated race-base conclusion

The current best explanation is no longer "the race FGTS must live in some
different hidden format inside `TESRace`."

What this pass actually supports is narrower:

- the race male/female FaceGen bases are owned, initialized, loaded, copied,
  saved, and consumed as first-class descriptor-backed blobs on `TESRace`
- the core lifecycle does not show a hidden scale or transform step
- the repo's current race readers still look simpler than the recovered
  `TESRace::Load` section/mirroring behavior

So the mismatch is still unresolved, but it is now narrowed to a smaller writer
set:

1. repo-side `RACE` parsing / race-base selection semantics
2. a post-load race-base writer or mutator outside the core `TESRace` lifecycle

That makes the next branch unambiguous:

- first compare the repo's `RACE` parsing behavior against the recovered
  `TESRace::Load` section/mirroring semantics
- if that still does not explain the capture mismatch, trace post-load writers
  touching `race + 0x408 / 0x468`

#### 8. Sample record cross-check: the current mismatch cases already have explicit male and female FaceGen sections on disk

The next practical check after the lifecycle decomp was whether the runtime
capture mismatch could still be explained by the repo simply missing a female
or mirrored FaceGen block in the source record.

Using the current PC final ESM and `esm dump`, the three probe-backed mismatch
families were checked directly:

- `AfricanAmericanOld (0x000042BF)`
- `AfricanAmerican (0x0000424A)`
- `Hispanic (0x000038E5)`

Evidence artifact:

- `artifacts/race-dump.txt`

Observed raw record pattern for all three:

- `NAM2` appears early in the record
- the record still contains an explicit FaceGen tail later:

```text
MNAM
FGGS
FGGA
FGTS
SNAM
FNAM
FGGS
FGGA
FGTS
SNAM
```

The sample-backed integration test added in:

- `tests/FalloutXbox360Utils.Tests/Core/Formats/Esm/RaceFaceGenSectionIntegrationTests.cs`

locks in the same conclusion against the parsed semantic model:

- all three races parse to non-null male and female `FGGS`
- all three races parse to non-null male and female `FGGA`
- all three races parse to non-null male and female `FGTS`

Operational conclusion:

- for Crocker, Jean-Baptiste, and Sunny, the current mismatch is not explained
  by a trivial "repo failed to read an explicit female block" issue
- the recovered `TESRace::Load` mirroring/default semantics are still real, but
  they are not enough on their own to explain these particular runtime capture
  mismatches

So the parser/mirroring branch is now demoted for the current sample set, and
the highest-value next branch is:

- trace post-load writers or mutators touching the race base coord blobs after
  `TESRace::Load`

#### 9. Post-load `TESRace` mutator pass: named runtime/setup methods did not expose a second race-base writer

The next targeted pass after the parser cross-check was a raw-PDB Xenon decomp
of likely post-load/setup `TESRace` methods:

- `TESRace::InitItem`
- `TESRace::Compare`
- `TESRace::GetBodyTexture`
- `TESRace::LoadCharacterModels`
- `TESRace::GetHeadPartModel`
- `TESRace::GetHeadPartTexture`
- `TESRace::GetBodyModTextureName`
- `TESRace::GetBodyModTextureFileName`
- `TESRace::GetBodyTextureFileName`

Artifacts:

- `tools/GhidraProject/run_decompile_facegen_race_postload_pdb.py`
- `tools/GhidraProject/facegen_race_postload_decompiled_pdb_xenon.txt`

Recovered result:

- `TESRace::InitItem` is link/model resolution and final item init work
- `TESRace::Compare` only compares the already-owned race state, including
  `+0x468` and `+0x408`
- `TESRace::GetBodyTexture*` and headpart getters are path/name helpers
- `TESRace::LoadCharacterModels` is effectively a stub in this build

Operational conclusion:

- this pass did **not** recover a second named runtime/setup writer that
  mutates the race FaceGen base blobs after `TESRace::Load`
- the post-load writer hypothesis is therefore narrowed to unnamed helpers or
  invalid capture/selection assumptions rather than obvious `TESRace::*`
  methods

#### 10. `TESRace::Load` section semantics are now explicit: female pages are the unconditional sink

Re-reading the `TESRace::Load` FGGS/FGGA/FGTS block in detail closed the exact
write semantics:

- `FNAM` sets the active section selector to `1`
- `MNAM` resets it to `0`
- `NAM2` flips `bVar7` from `true` to `false`
- `FGGS`, `FGGA`, and `FGTS` all flow through the same shared writer block

That writer block does:

- conditional write to `+0x468 / +0x480 / +0x498` when
  `(activeSection == male) || preNam2MirroringEnabled`
- unconditional write to `+0x408 / +0x420 / +0x438`

Because:

- `0x2b * 0x18 = 0x408`
- `0x2f * 0x18 = 0x468`

the shared writer block can now be mapped directly:

- unconditional target = lower blob = `+0x408 / +0x420 / +0x438`
- conditional target = upper blob = `+0x468 / +0x480 / +0x498`

Operational interpretation:

- before `NAM2`, the loader mirrors FaceGen arrays into both page sets
- after `NAM2`, male-section arrays still write both page sets
- after `NAM2`, female-section arrays only write the lower page set

For records whose final FaceGen tail is:

```text
MNAM
FGGS
FGGA
FGTS
SNAM
FNAM
FGGS
FGGA
FGTS
SNAM
```

the final in-memory result is:

- upper page set = male arrays
- lower page set = female arrays

So this richer loader behavior is real, but for the current sample records it
still does not, by itself, imply a hidden transform step.

#### 11. Sex-aware runtime capture comparison points at a page-label / selection issue before it points at an unnamed writer

The runtime-capture comparer was then extended to rank each capture against both
male and female FaceGen texture arrays for every parsed race candidate, instead
of only the actor's current sex.

Artifacts:

- `artifacts/runtime-capture-compare-sex/summary.csv`
- `artifacts/runtime-capture-compare-sex/20260315_195203_00112640_00112640_npc_male/summary.txt`
- `artifacts/runtime-capture-compare-sex/20260315_203442_0010C681_0010C681_npc_male/summary.txt`
- `artifacts/runtime-capture-compare-sex/20260315_212349_00104E84_00104E84_npc_female/summary.txt`

New results:

- Crocker / `AfricanAmericanOld`
  - best match: `AfricanAmericanOld`, **sex=female**, `MAE 0.000001`
- Jean-Baptiste / `AfricanAmerican`
  - best match: `AfricanAmerican`, **sex=female**, `MAE 0.000002`
- Sunny / `Hispanic`
  - no exact same-race sex match; best candidate remains non-runtime and noisy

Operational conclusion:

- for both male African American captures, the runtime race page we currently
  capture matches the **female** parsed race texture array essentially exactly
- that is stronger evidence for a page-label or sex-selection mistake in the
  probe / current race-page assumptions than for a hidden post-load writer
- Sunny remains unresolved, so this does not fully close the race-side issue,
  but it materially changes the next step

Updated next branch:

1. verify the PC runtime race-page mapping in the probe by dumping **both** race
   FaceGen texture pages per capture rather than one sex-selected page
2. only if the dual-page probe still fails to explain the African American
   captures should the investigation return to unnamed post-load writers
