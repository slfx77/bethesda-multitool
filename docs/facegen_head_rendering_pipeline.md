# FaceGen Head Rendering Pipeline

## Status: PARTIALLY VERIFIED — core decompilation claims audited, conclusions still under review (2026-03-19)

This document traces the engine's head rendering pipeline from decompiled Xbox 360 code.
Most low-level claims cite a specific decompiled function and line range. Some higher-level
semantic conclusions were stronger than the raw evidence and are now called out explicitly.

**Decompilation sources** (regenerated 2026-03-19):
- `tools/GhidraProject/facegen_texture_bake_decompiled.txt` — 12 functions, texture bake path (Xbox 360)
- `tools/GhidraProject/facegen_textures_decompiled2.txt` — 16 functions, shader binding + orchestrator (Xbox 360)
- `tools/GhidraProject/facegen_memdebug_decompiled.txt` — 65 functions, comprehensive FaceGen pipeline (Xbox 360)
- `tools/GhidraProject/facegen_geck_bake_assembly.txt` — Full ASM of bake accumulator + FREGT003 parser (GECK x86)
- `tools/GhidraProject/facegen_geck_texture_bake_candidates.txt` — 8 functions, GECK bake path
- `tools/GhidraProject/facegen_geck_egt_generation_2.txt` — 8 functions, EGT generation orchestrator
- `tools/GhidraProject/facegen_geck_egt_generation_3.txt` — 9 functions, morph generation + morph context
- `tools/GhidraProject/facegen_geck_binary_reader.txt` — 12 functions, shared FaceGen reader + stream openers
- `tools/GhidraProject/facegen_geck_tri_section_order.txt` — 12 functions, FRTRI003 post-header section order
- `tools/GhidraProject/facegen_geck_tri_early_sections.txt` — 13 functions, FRTRI003 early section families + optional branch
- `tools/GhidraProject/facegen_geck_tri_early_record_helpers.txt` — 18 functions, early `0x08` / `0x10` record storage helpers
- `tools/GhidraProject/facegen_geck_tri_context_lifecycle.txt` — 5 functions, coord-context ctor/dtor + follow-up helper
- `tools/GhidraProject/facegen_geck_tri_followup_caller.txt` — 2 functions, caller classification for `FUN_00696ab0`
- `tools/GhidraProject/facegen_geck_bsfacegenmodel_helpers.txt` — 5 functions, BSFaceGenModel-side loader + cleanup helpers
- `tools/GhidraProject/facegen_geck_bsfacegenmodel_followups.txt` — 3 functions, BSFaceGenModel extra-data constructors + lazy loader caller
- `tools/GhidraProject/facegen_geck_bsfacegenmodel_morph_accessors.txt` — 4 functions, BSFaceGenModel morph accessor + extra-data attach helpers
- `tools/GhidraProject/facegen_geck_bsfacegenmodel_vector_source.txt` — 3 functions, BSFaceGenModel vector-source descriptor/accessor layer
- `tools/GhidraProject/facegen_geck_bsfacegenmodel_accessor_family.txt` — 5 functions, BSFaceGenModel sibling stream accessors above the vector-source layer
- `tools/GhidraProject/facegen_geck_bsfacegenmodel_runtime_stream_owner.txt` — 4 functions, runtime stream-owner swap/compare path + provider lookup
- `tools/GhidraProject/facegen_geck_runtime_stream_provider_neighborhood.txt` — neighborhood dump around `FUN_0081c060`, the external stream-helper provider
- `tools/GhidraProject/facegen_geck_runtime_stream_owner_neighborhood.txt` — neighborhood dump around the local stream-owner load/save methods
- `tools/GhidraProject/facegen_geck_runtime_stream_owner_helpers.txt` — helper layer under the local stream-owner load/save methods
- `tools/GhidraProject/facegen_geck_additional_geom_data.txt` — AdditionalGeometryData field-string pass, AGD serializer identification
- `tools/GhidraProject/facegen_geck_additional_geom_data_vtable.txt` — attempted AGD vtable reconstruction from serializer DATA refs
- `tools/GhidraProject/facegen_runtime_agd_decompiled_pdb_xenon.txt` — Xbox MemDebug raw-PDB pass for AGD + runtime FaceGen bridge
- `tools/GhidraProject/facegen_tri_runtime_bridge.txt` — Xbox MemDebug raw-address pass for TRI runtime container + morph builders
- `tools/GhidraProject/facegen_tri_runtime_tables.txt` — raw Xbox table dump for the runtime morph-builder globals
- `tools/GhidraProject/facegen_tri_runtime_table_initializers.txt` — xref scan for the runtime morph-builder globals
- `tools/GhidraProject/facegen_morph_data_decompiled_pdb_xenon.txt` — Xbox MemDebug raw-PDB pass for runtime FaceGen morph-data classes
- `tools/GhidraProject/facegen_ninode_morph_decompiled_pdb_xenon.txt` — Xbox MemDebug raw-PDB pass for `BSFaceGenNiNode` morph routing + `ApplyHairMorph`
- `tools/GhidraProject/facegen_slot_names_decompiled_pdb_xenon.txt` — Xbox MemDebug raw-PDB pass for debug FaceGen slot labels + bucket-facing getters
- `tools/GhidraProject/facegen_animation_slot_semantics_decompiled_pdb_xenon.txt` — Xbox MemDebug raw-PDB pass for `BSFaceGenAnimationData` slot consumers
- `tools/GhidraProject/facegen_animation_state_decompiled_pdb_xenon.txt` — Xbox MemDebug raw-PDB pass for `BSFaceGenAnimationData` state layout + `BSFaceGenKeyframeMultiple`
- `tools/GhidraProject/facegen_eye_tracking_semantics_decompiled_pdb_xenon.txt` — Xbox MemDebug raw-PDB pass for eye/head tracking helpers, clamp getters, and tracking globals
- `tools/GhidraProject/facegen_eye_tracking_globals_raw.txt` — raw EXE dump of shipped `fTrackEye*` / `fTrackDeadZone*` scalar values
- `tools/GhidraProject/facegen_slot_string_refs_decompiled_pdb_xenon.txt` — exploratory raw-PDB/xref pass on the FaceGen slot-name string pools
- `tools/GhidraProject/facegen_slot_string_pool_raw.txt` — raw EXE dump of the contiguous FaceGen slot-name string pool
- `tools/GhidraProject/facegen_control_symbols_pdb_xenon.txt` — raw-PDB symbol scan for the FaceGen coord/control manager path
- `tools/GhidraProject/facegen_coord_controls_decompiled_pdb_xenon.txt` — raw-PDB pass for `BSFaceGenManager` face-coordinate control + attribute methods
- `tools/GhidraProject/facegen_coord_rebuild_decompiled_pdb_xenon.txt` — raw-PDB pass for coord compare/copy/merge/offset rebuild helpers
- `tools/GhidraProject/facegen_coord_consumer_bridge_decompiled_pdb_xenon.txt` — raw-PDB pass for coord preprocessing + downstream mesh/texture consumer bridge
- `tools/GhidraProject/facegen_head_part_attach_decompiled_pdb_xenon.txt` — raw-PDB pass for runtime eye/hair attachment helpers and their immediate mesh/hair bridge calls
- `tools/GhidraProject/facegen_eye_path_decompiled_pdb_xenon.txt` — raw-PDB pass for runtime eye/LOD/refresh callers around `RefreshMeshFromBaseMorphExtraData`
- `tools/GhidraProject/facegen_compute_model_bound_decompiled_pdb_xenon.txt` — raw-PDB pass for `BSFaceGenManager::ComputeModelBound`, the iterator consumer under `ReplaceFaceMeshLOD`
- `tools/GhidraProject/facegen_create_new_mesh_decompiled_pdb_xenon.txt` — raw-PDB pass for `CreateNewMesh`, `ApplyCoordinateToNewMesh`, `ApplyCoordinateToExistingMesh`, and `LoadModel`
- `tools/GhidraProject/facegen_loadmodel_selector_bridge_decompiled_pdb_xenon.txt` — raw-PDB pass for `LoadModel` / `LoadModelMesh` and the named runtime callers that supply the TRI-part selector
- `tools/GhidraProject/facegen_model_cache_bridge_decompiled_pdb_xenon.txt` — raw-PDB pass for the `BSFaceGenModelMap` cache bridge under `LoadModel`
- `tools/GhidraProject/facegen_model_cache_lazy_loaders_decompiled_pdb_xenon.txt` — raw-PDB pass for the cache-side `LoadEGMData` / `LoadEGTData` and size/budget helpers
- `tools/GhidraProject/facegen_model_cache_loader_callscan_pdb_xenon.txt` — raw PPC call scan for the cache-side EGM/EGT loader helpers and size helpers
- `tools/GhidraProject/facegen_child_morph_invocation_decompiled_pdb_xenon.txt` — raw-PDB pass for the remaining `UpdateAllChildrenMorphData` owner set (`UpdateMorphing` and `PlayerCharacter::CloneInventory3D`)
- `tools/GhidraProject/facegen_child_fallback_predicates_decompiled_pdb_xenon.txt` — raw-PDB pass resolving the remaining child-loop helper trio (`UpdateEyeTracking`, `IsInMenuMode`, `IsActorNCloseToPlayer`)
- `tools/GhidraProject/facegen_ninode_layout_decompiled_pdb_xenon.txt` — raw-PDB pass for the small `BSFaceGenNiNode` constructor/getter/setter family to pin down local member layout
- `tools/GhidraProject/facegen_child_fallback_object_bridge_decompiled_pdb_xenon.txt` — raw-PDB pass for the remaining inherited-object bridge (`NiAVObject::AttachParent`, `NiObjectNET::GetController`, `NiTimeController::DontDoUpdate`, and the relevant constructors)
- `tools/GhidraProject/facegen_ninode_base_scan_pdb_xenon.txt` — raw-PDB neighborhood scan resolving the `BSFaceGenNiNode` base constructor callee to `NiNode::NiNode`
- `tools/GhidraProject/memdebug_scenegraph_accessors_pdb_xenon.txt` — raw-PDB symbol scan of likely `NiObjectNET` / `NiAVObject` / `NiNode` accessors around the remaining inherited-field question
- `tools/GhidraProject/facegen_ninode_flag_accessors_decompiled_pdb_xenon.txt` — raw-PDB pass for the small `BSFaceGenNiNode` flag accessors (`GetFixedNormals`, `GetAnimationUpdate`, `GetApplyRotToParent`)
- `tools/GhidraProject/facegen_ninode_flag_callscan_pdb_xenon.txt` — raw PPC call scan of those three `BSFaceGenNiNode` flag getters to see which ones have ordinary direct owners
- `tools/GhidraProject/facegen_create_new_mesh_helpers_decompiled_pdb_xenon.txt` — raw-PDB pass for the two helper callees directly under `CreateNewMesh`
- `tools/GhidraProject/facegen_base_morph_extra_data_decompiled_pdb_xenon.txt` — raw-PDB pass for the `BSFaceGenBaseMorphExtraData` method family
- `tools/GhidraProject/facegen_remaining_runtime_boundaries_decompiled_pdb_xenon.txt` — raw-PDB pass resolving the remaining anonymous runtime-boundary helpers under `BSFaceGenBaseMorphExtraData`
- `tools/GhidraProject/facegen_geometry_object_bridge_decompiled_pdb_xenon.txt` — raw-PDB pass resolving the owning mesh-side object bridge around `source + 0xDC` / `source + 0xE0`
- `tools/GhidraProject/facegen_skin_partition_runtime_decompiled_pdb_xenon.txt` — raw-PDB pass for `NiSkinPartition` / `BSDismemberSkinInstance` runtime load layout under the packed FaceGen branch
- `tools/GhidraProject/facegen_packed_stream_provenance_decompiled_pdb_xenon.txt` — raw-PDB pass for the `NiAdditionalGeometryData` / `NiGeometryData` bridge that sources the packed iterator consumed by runtime FaceGen
- `tools/GhidraProject/facegen_table_consumers_decompiled_pdb_xenon.txt` — raw-PDB pass for `BSFaceGenNiNode::GetViewerStrings` and other named consumers of the runtime slot tables
- `tools/GhidraProject/facegen_ppc_address_builds_pdb_xenon.txt` — raw PPC address-build scan that identifies the `HeadTable_Expressions` / `HeadTable_Modifiers` / `HeadTable_Phonemes` globals in named runtime owners
- `tools/GhidraProject/memdebug_target_callscan_pdb_xenon.txt` — point-in-time raw-PPC call scan of `NiGeometryData::GetVerticesIterator` owners for the current packed-stream investigation
- `tools/GhidraProject/facegen_updateallchildren_callscan_pdb_xenon.txt` — point-in-time raw-PPC call scan of `BSFaceGenNiNode::UpdateAllChildrenMorphData` owners
- `tools/GhidraProject/facegen_helper_8247e4c0_callscan_pdb_xenon.txt` — point-in-time raw-PPC call scan of `BSFaceGenAnimationData::UpdateEyeTracking`
- `tools/GhidraProject/facegen_helper_8253ae68_callscan_pdb_xenon.txt` — point-in-time raw-PPC call scan of `Interface::IsInMenuMode`
- `tools/GhidraProject/facegen_helper_827764b0_callscan_pdb_xenon.txt` — point-in-time raw-PPC call scan of `ProcessLists::IsActorNCloseToPlayer`
- `tools/GhidraProject/facegen_dia_virtual_offsets_pdb_xenon.txt` — DIA-backed virtual-slot notes for the remaining `BSFaceGenNiNode` child-fallback calls (`GetAnimationData`, `GetDead`, and `IsFadeNode`)
- `tools/GhidraProject/facegen_helper_niobject_isfadenode_callscan_pdb_xenon.txt` — point-in-time raw-PPC call scan of `NiObject::IsFadeNode`
- `tools/GhidraProject/facegen_helper_bsfacegenanimationdata_getdead_callscan_pdb_xenon.txt` — point-in-time raw-PPC call scan of `BSFaceGenAnimationData::GetDead`
- `tools/GhidraProject/facegen_runtime_name_attach_bridge_decompiled_pdb_xenon.txt` — raw-PDB/manual-address pass for the runtime name/attachment helper neighborhood (`NiGlobalStringTable::AddString`, `NiObjectNET::SetName`, `TES::CreateTextureImage`, and `GetObjectByName`)
- `tools/GhidraProject/facegen_helper_tes_createtextureimage_callscan_pdb_xenon.txt` — point-in-time raw-PPC call scan of `TES::CreateTextureImage`
- `tools/GhidraProject/facegen_helper_niavobject_getobjectbyname_callscan_pdb_xenon.txt` — point-in-time raw-PPC call scan of `NiAVObject::GetObjectByName`
- `tools/GhidraProject/facegen_helper_tesrace_createhead_callscan_pdb_xenon.txt` — point-in-time raw-PPC call scan of `TESRace::CreateHead`
- `tools/GhidraProject/facegen_helper_linearfgheadload_callscan_pdb_xenon.txt` — point-in-time raw-PPC call scan of `TESNPC::LinearFaceGenHeadLoad`
- `tools/GhidraProject/facegen_helper_tesnpc_inithead_callscan_pdb_xenon.txt` — point-in-time raw-PPC call scan of `TESNPC::InitHead`
- `tools/GhidraProject/facegen_head_postinit_near_scan_pdb_xenon.txt` — raw-PDB vicinity scan resolving the unnamed post-`InitHead` / post-`LinearFaceGenHeadLoad` helper addresses
- `tools/GhidraProject/facegen_head_wrapper_owners_decompiled_pdb_xenon.txt` — raw-PDB pass for the outer TESRace/TESNPC/head-queue/high-process owner chain around runtime head creation and attachment
- `tools/GhidraProject/facegen_biped_bridge_decompiled_pdb_xenon.txt` — raw-PDB pass for the `BipedAnim` bridge helpers plus the immediate TESNPC/high-process FaceGen owners
- `tools/GhidraProject/facegen_biped_attach_helpers_decompiled_pdb_xenon.txt` — raw-PDB pass for the next `BipedAnim` attach/helper layer above `TESNPC::AttachHead`
- `tools/GhidraProject/facegen_biped_dia_virtual_offsets_pdb_xenon.txt` — DIA-backed virtual-slot notes closing the `TESNPC::AttachHead` scenegraph bridge (`AttachChild`, `SetAnimationUpdate`, `SetApplyRotToParent`, `FixSkinInstances`)
- `tools/GhidraProject/facegen_biped_type_layouts_pdb_xenon.txt` — DIA-backed `BipedAnim` / `BIPBONE` / `BIPOBJECT` layout notes for the runtime head bridge
- `tools/GhidraProject/facegen_owner_accessors_decompiled_pdb_xenon.txt` — raw-PDB pass for the owner-side `TESObjectREFR` / `Actor` FaceGen node and animation-data accessors
- `tools/GhidraProject/facegen_owner_accessor_dia_virtual_offsets_pdb_xenon.txt` — DIA-backed virtual-slot notes for the owner-side FaceGen accessors (`GetFaceNodeBiped`, `GetFaceNodeSkinned`, `GetFaceNode`, `GetFaceAnimationData`, `GetCurrentBiped`)
- `tools/GhidraProject/facegen_node_name_builds_pdb_xenon.txt` — raw PPC address-build scan for the `BSFaceGenNiNodeBiped` / `BSFaceGenNiNodeSkinned` string owners
- `tools/GhidraProject/facegen_biped_skinned_owner_roles_decompiled_pdb_xenon.txt` — raw-PDB pass for the owner functions that explicitly distinguish the biped vs skinned FaceGen nodes (`FixDisplayedHeadParts`, `SurgeryMenu::UpdateFace`, `HideDismemberedLimb`, `DismemberLimb`)
- `tools/GhidraProject/facegen_process_face_bridge_decompiled_pdb_xenon.txt` — raw-PDB pass for the live `Actor` / `BaseProcess` / `MiddleHighProcess` FaceGen node and animation-data bridge
- `tools/GhidraProject/facegen_process_type_layouts_pdb_xenon.txt` — DIA-backed `BaseProcess` / `MiddleHighProcess` layout notes for `pFaceNode`, `pFaceNodeSkinned`, and `m_pFaceAnimationData`
- `tools/GhidraProject/facegen_owner_accessor_callers_pdb_xenon.txt` — point-in-time raw-PPC call scan of the owner/animation accessor layer (`GetFaceNodeBiped`, `GetFaceNodeSkinned`, `TESObjectREFR::GetFaceAnimationData`, `Actor::GetFaceAnimationData`)
- `tools/GhidraProject/facegen_process_node_lifecycle_decompiled_pdb_xenon.txt` — raw-PDB pass for `MiddleHighProcess::MiddleHighProcess`, `MiddleHighCopy`, and `Revert`, used to pin down how the process-side `pFaceNode` / `pFaceNodeSkinned` fields are preserved or cleared
- `tools/GhidraProject/facegen_process_node_virtual_slots_pdb_xenon.txt` — raw PPC load scan for the `BaseProcess` / `MiddleHighProcess` face-node getter/setter virtual slots (`+0x78C/+0x790/+0x794/+0x798`), used to surface owners beyond ordinary direct-call xrefs
- `tools/GhidraProject/facegen_process_population_owners_decompiled_pdb_xenon.txt` — focused raw-PDB decompile of the strongest process-population owners surfaced by the slot scan (`TESObjectREFR::Set3D`, `Script::ModifyFaceGen`, `RaceSexMenu::UpdatePlayerHead`, `TaskQueueInterface::TaskUnpackFunc`, `Character::Update`, `Character::PrecacheData`)
- `tools/GhidraProject/facegen_fixedstrings_init_decompiled_pdb_xenon.txt` — raw-PDB decompile of `FixedStrings::InitSDM`, used to identify which runtime-initialized fixed-string globals back the lazy process-side FaceGen node cache
- `tools/GhidraProject/facegen_fixedstring_probe_raw.txt` — small raw string probe of the nearby fixed-string literals around the `uRam8328ad10/uRam8328ad14/uRam8328ad18` globals
- `tools/GhidraProject/facegen_headanims_bridge_decompiled_pdb_xenon.txt` — focused raw-PDB pass for the cached `HeadAnims:0` runtime bridge (`NiObjectNET::GetController` plus `BSFaceGenAnimationData::{SetAnimHeadCulled, SetAnimExpressionValue, SetAnimModifierValue, SetAnimPhonemeValue}`)
- `tools/GhidraProject/facegen_animface_visibility_decompiled_pdb_xenon.txt` — focused raw-PDB pass for the `HeadAnims` visibility/cull bridge (`BSFaceGenNiNode::OnVisible`, `NiVisController::{GetTargetBoolValue, Update}`, `NiNode::OnVisible`)
- `tools/GhidraProject/facegen_helper_setanimheadculled_callscan_pdb_xenon.txt` — point-in-time raw-PPC call scan of `BSFaceGenAnimationData::SetAnimHeadCulled`
- `tools/GhidraProject/facegen_helper_setanimexpressionvalue_callscan_pdb_xenon.txt` — point-in-time raw-PPC call scan of `BSFaceGenAnimationData::SetAnimExpressionValue`
- `tools/GhidraProject/facegen_helper_setanimmodifiervalue_callscan_pdb_xenon.txt` — point-in-time raw-PPC call scan of `BSFaceGenAnimationData::SetAnimModifierValue`
- `tools/GhidraProject/facegen_helper_setanimphonemevalue_callscan_pdb_xenon.txt` — point-in-time raw-PPC call scan of `BSFaceGenAnimationData::SetAnimPhonemeValue`
- `tools/GhidraProject/facegen_helper_biped_attachtoparent_callscan_pdb_xenon.txt` — point-in-time raw-PPC call scan of `BipedAnim::AttachToParent`
- `tools/GhidraProject/facegen_helper_biped_attachtoskeleton_callscan_pdb_xenon.txt` — point-in-time raw-PPC call scan of `BipedAnim::AttachToSkeleton`
- `tools/GhidraProject/facegen_helper_biped_attachskinnedobject_callscan_pdb_xenon.txt` — point-in-time raw-PPC call scan of `BipedAnim::AttachSkinnedObject`
- `tools/GhidraProject/facegen_helper_biped_loadbipedparts_callscan_pdb_xenon.txt` — point-in-time raw-PPC call scan of `BipedAnim::LoadBipedParts`
- `tools/GhidraProject/facegen_helper_bsfadenode_ctor_callscan_pdb_xenon.txt` — point-in-time raw-PPC call scan of `BSFadeNode::BSFadeNode`
- `TestOutput/codex_tri_anchor_compare.txt` — local `inspect-tri` anchor comparison for `headhuman` vs `eyelefthuman`
- `TestOutput/codex_tri_nonhair_name_semantics.txt` — local `inspect-tri` probe of head/eye/mouth/tongue/teeth TRI identifier strings
- `TestOutput/codex_tri_hair_name_probe.txt` — local `inspect-tri` probe of beard/eyebrow TRI identifier strings
- `TestOutput/codex_tri_sparse_bucket_mapping.txt` — local sparse-TRI bucket/slot mapping report derived from the shared runtime tables plus the `inspect-tri` name probes
- `TestOutput/codex_skeleton_root_types.txt` — local `NifAnalyzer blocks` comparison for the shipped third-person vs first-person human skeleton roots
- `TestOutput/codex_headanims_skeleton_map.txt` — local `NifAnalyzer block` map of the shipped third-person `HeadAnims` / `HeadAnims:0` controller branch
- `TestOutput/codex_headanims_static_path_crosscheck.txt` — local negative cross-check showing no `HeadAnims:0` / `SetAnim*Value` references in the current named static head-build/apply artifacts
- `tools/GhidraProject/facegen_racesexmenu_decompiled_pdb_xenon.txt` — raw-PDB pass for RaceSexMenu slider construction + slider-apply path
- `tools/GhidraProject/facegen_racesexmenu_slider_labels_raw.txt` — raw EXE dump of the RaceSexMenu FaceGen slider label globals
- `tools/GhidraProject/facegen_geck_tri_record_helpers.txt` — 9 functions, TRI morph-record helper layer

---

## Audit Note (2026-03-18)

The strongest parts of this document are still Sections 4.1, 4.2, 6, 8.1, and 8.2:
- the Xbox bake accumulator structure
- the encode-side floor behavior in `GetCompressedImage`
- the shader slot copy behavior in `SetFaceGenMaps`
- the GECK bake accumulator and `FREGT003` runtime entry layout

The weaker parts are the end-to-end conclusions drawn from those pieces. Recent live
`EgtAnalyzer` verification runs still show residual mismatch patterns that are not explained
cleanly by DXT1 noise alone, so this document should not be treated as a final explanation of
the remaining texture differences.

---

## 1. Overview

FaceGen gives each NPC a unique face through two morph systems:
- **Mesh morphing (EGM)**: Deforms base head geometry vertices
- **Texture morphing (EGT)**: Creates a per-NPC **delta texture** from morph bases

Both use per-NPC coefficients stored in ESM `FGGS`/`FGGA`/`FGTS` subrecords.

**Critical finding**: The engine does NOT composite the delta texture with the base diffuse on the CPU.
Instead, it passes the delta texture and base texture as **separate inputs to the pixel shader**,
which composites them at render time. Our current implementation composites on the CPU
(`final = clamp(base + delta, 0, 255)`), which may not match the shader's compositing formula.

---

## 2. Data Files

### EGM (FaceGen Geometry Morph) — ASSUMED (high confidence from ApplyMorph decompilation)
- Magic: `FREGM002`
- 64-byte header: vertex_count at [8-11], sym_count at [12-15], asym_count at [16-19]
- 50 symmetric morphs + 30 asymmetric morphs
- Each morph: float32 scale + int16 XYZ deltas per vertex (interleaved, 6 bytes/vertex)
- Source: `EgmParser.cs`
- Parser decompilation (`EGMData::EGMData` at PDB [0004:00242428]) added to script but not yet run

### EGT (FaceGen Texture Morph) — VERIFIED
- Magic: `FREGT003`
- 64-byte header: **rows at [8-11], cols at [12-15]**, sym_count at [16-19], asym_count at [20-23]
  - Citation: `FgEgtFileIO_ParseEgtFile` (`egt_parser_decompiled.txt:620-646`)
  - `uStack_b0` (header offset 0) = row count (loop bound), `iStack_ac` (offset 4) = width (bytes per row read)
- 50 symmetric morphs (texture only, asymmetric typically 0)
- Each morph: float32 scale + 3 sequential int8 channel arrays (R, G, B), each `width*height` bytes
- Engine aligns row stride to 8 bytes: `stride = (width + 7) & ~7` (no-op for 256px)
- Engine V-flips during parse (reads rows bottom-to-top); our parser reads top-to-bottom with later flip
- Source: `EgtParser.cs` — **FIXED**: header fields swapped to match decompilation

### Base Head NIF + DDS
- Per-race, per-gender head mesh (e.g., `headhuman.nif`)
- Base diffuse texture (e.g., `headhuman.dds`) referenced by NiTexturingProperty
- This base texture is passed to the shader as a SEPARATE input from the facemod delta

### Face Tint Texture (`_sk`)
- Per-race, per-gender skin tint (e.g., `headhuman_sk.dds`)
- Bound to shader slot 2 in `PrepareHeadForShaders`
- Fallback: `BSFaceGenManager::GetDefaultDetailModulationTexture` (singleton at offset 0xDB8)

### Shipped Facemods (`_0.dds`)
- Pre-baked per-NPC face textures at `textures/characters/facemods/{plugin}/{formid}_0.dds`
- Created by GECK "Export FaceGen" or at runtime via `ApplyCoordinateTexturingToMesh`
- **Format: DELTA texture** — neutral value = **127** (byte encoding of float 0.0)
- NOT a composited texture. The base diffuse is added by the shader.

---

## 3. Mesh Morphing Path (EGM) — VERIFIED (from ApplyMorph decompilation)

**Entry**: `BSFaceGenModel::ApplyCoordinateToExistingMesh`
PDB [0004:00242788], VA 0x82492788, size 0xCE4 bytes.

Decompiled in `facegen_texture_bake_decompiled.txt`. Uses same coefficient system as texture path.
**This path is verified working correctly in our implementation.**

### Coefficient Merge — VERIFIED
- `BSFaceGenManager::MergeFaceGenCoord` (`egt_parser_decompiled.txt:2094-2097`): `fadds f12, f0, f13`
- Formula: `merged[i] = npc_coeff[i] + race_coeff[i]` (element-wise float addition)
- RMS clamping (`egt_parser_decompiled.txt:2048-2072`): `rms = sqrt(sum_sq / count)`, if `rms > threshold`: `coeff[i] *= threshold / rms`
- Implementation: `NpcFaceGenCoefficientMerger.cs` — matches exactly

---

## 4. Texture Morphing Path (EGT → Delta Texture) — VERIFIED

### 4.1 Entry: `BSFaceGenModel::ApplyCoordinateTexturingToMesh`

**Source**: `facegen_textures_decompiled2.txt` lines 399-735
**PDB**: [0004:00241970], VA 0x82491970, 1668 bytes

#### Initial setup (lines 524-525)
```c
uVar8 = GetDefaultBaseModulationTexture();  // func_0x8243e3c8
assign(param_3, uVar8);  // Set output to base texture as fallback
```
The output parameter is initially set to the base modulation texture.
This is overwritten on success (line 718-719) but serves as fallback if the function returns early.

#### Accumulation loop (lines 612-661)
```c
dVar34 = 256.0;
for (each morph basis, uVar30 = 0..param_4) {
    // Truncate coefficient to int: coeff256 = (int)(coeff * 256.0)
    iVar24 = (int)((double)coeff_float * 256.0);        // line 622-623
    if (iVar24 == 0) continue;                           // skip zero coefficients

    for (channel = 0; channel < 3; channel++) {          // line 626-656 (uVar9 < 3)
        // Truncate scale to int: scale256 = (int)(scale * 256.0)
        iVar26 = (int)((double)scale_float * 256.0);     // line 634

        for (y = 0; y < height; y++) {
            for (x = 0; x < width; x++) {
                // Core accumulation: int8 delta * scale256 * coeff256
                accum[offset] += (int8)delta_byte * iVar26 * iVar24;  // line 645-646
            }
        }
    }
}
```

**Key details** (VERIFIED — matches `AccumulateNativeDeltasQuantized256` in `FaceGenTextureMorpher.cs:383-428`):
- Accumulates from ZERO (buffer is memset to 0 at line 598)
- NOT from the base texture — pure morph delta accumulation
- Coefficient and scale are truncated to int via `(int)(float * 256.0)` — NOT rounded
- Inner product is `(int8)delta * scale256 * coeff256` (signed byte delta)
- Division by 65536 at end: `float_out = accum_int / 65536.0` (line 663-716)
- 3 channels processed in outer loop (uVar9 < 3, line 656)
- Each morph basis entry is 0x40 (64) bytes: 4 bytes scale + 4 bytes width + 4 bytes height + ...
- `BSFaceGenMorphStatistical::ApplyMorph` (`facegen_texture_bake_decompiled.txt:4583-4704`) confirms same formula at runtime

#### Float conversion (lines 663-716)
```c
for (channel = 0; channel < 3; channel++) {
    for (each pixel) {
        float_out[pixel] = (float)(longlong)accum_int[pixel] * 1.5258789e-05;  // = /65536.0
    }
}
```

#### Encoding (line 718)
```c
uVar8 = GetCompressedImage(-255.0, 255.0, 0.5, float_image);  // func_0x8247f6a0
assign(param_3, uVar8);  // Overwrite output with encoded delta texture
```

### 4.2 Encoding: `BSFaceGenImage::GetCompressedImage`

**Source**: `facegen_texture_bake_decompiled.txt` lines 1616-1761
**PDB**: [0004:0022F6A0], VA 0x8247F6A0, 1336 bytes
**Called as**: `GetCompressedImage(-255.0, 255.0, 0.5)`

#### Per-pixel encoding (lines 1662-1704)
```c
for (each pixel, 3 channels) {
    // Clamp to [min, max]
    clamped = clamp(float_value, -255.0, 255.0);

    // Floor (not truncate — handles negative correctly)
    floored = (double)(longlong)clamped;  // truncate toward zero
    if (clamped - floored < 0.0)          // if negative fraction
        floored = floored - 1.0;          // adjust to floor

    // Encode: byte = (byte)(long)((floor(clamped) - min) * scale)
    output_byte = (byte)(longlong)((float)((float)floored - (-255.0)) * 0.5);
    //          = (byte)(long)((floor(clamped) + 255.0) * 0.5)
}
```

#### Neutral value derivation
For zero delta (float = 0.0):
- `floor(0.0) = 0.0`
- `(0.0 + 255.0) * 0.5 = 127.5`
- `(long)127.5 = 127` (truncate toward zero)
- **Neutral = 127, NOT 128**

#### Output format
- RGB, 3 bytes per pixel (tightly packed, no alpha)
- Stored as NiPixelData, then DXT1-compressed and saved as `_0.dds`
- Float channel 0 → byte R, channel 1 → byte G, channel 2 → byte B (no swapping)

### 4.3 Output Summary

`ApplyCoordinateTexturingToMesh` produces a **pure delta texture**:
- Byte 127 = zero delta (no change from base)
- Byte 0 = maximum negative delta (-255 float → -254 after floor → 0 byte)
- Byte 255 = maximum positive delta (+255 float → 255 byte)
- Decoding (shader-side): `float_delta = (sample - 0.5) * 2.0` = `byte * 2 - 255` in byte-space — **VERIFIED** (SKIN2000.pso disassembly, `SKIN2000_annotated.txt:77-78`)

---

## 5. Head Assembly Orchestrator

### `BSFaceGenManager::PrepareHeadForShaders`

**Source**: `facegen_textures_decompiled2.txt` lines 1368-2053
**PDB**: [0004:0023D3A8], VA 0x8248D3A8, 3916 bytes

This is the main orchestrator. It processes 8 head part slots (loop uVar22 = 0..7, line 1692-1823).

#### Per-slot logic

1. **Slots 6, 7**: Skip to default texture (lines 1695-1698)
2. **Slots 0, 1**: Set shader pass `iStack_488 = 0x0E` (FaceGen shader). Other slots: `iStack_488 = 1` (lines 1700-1702)
3. **Validate**: Check model and texture data exist for this slot (lines 1704-1705)
4. **Get BSShaderProperty**: `func_0x82e22578(piVar10, 3)` — shader type must be 8-12 (lines 1725-1726)

#### Texture acquisition (slot 0 = head, lines 1730-1769)

Two paths depending on whether `BSFaceGenManager::ResolveFaceGenShaderTexture`
(`0x824881c8`) resolves an alternate texture path:

**Path A** (lines 1730-1745): alternate path resolves
- `ResolveFaceGenShaderTexture` takes the existing head diffuse path, strips the extension, and
  formats candidate sibling paths using a template at `0x82066648`
- It selects `'M'` or `'F'` based on the second argument (`param_2 + 0x70` at the call site) and
  uses the third argument (`cVar1`) as a numeric selector
- Search order is: exact selector first, then descending decade buckets (`selector/10 * 10`,
  `-10`, `-20`, ...) until `0`; each candidate is existence-checked through `func_0x82937230`
- If no shipped facemod exists: calls `ApplyCoordinateTexturingToMesh` (line 1739) to create runtime delta → `apiStack_4a0`
- Loads the resolved alternate texture into `piStack_484` from `uStack_478`
- Extracts two more path-derived textures from the same resolved path into `iStack_498` and `iStack_490`
- This path is about resolving a secondary FaceGen texture family from the base diffuse stem, not
  about `LoadModelTexture` success and not a direct “shipped facemod exists” predicate

**Path B** (lines 1746-1769): alternate path does not resolve (LAB_8248d78c)
- If no shipped facemod exists: calls `ApplyCoordinateTexturingToMesh` (line 1750) → `apiStack_4a0`
- Extracts diffuse and normal from the model's existing NiTexturingProperty
- `vtable+0x14` call → `iStack_498` (diffuse)
- `vtable+0x18` call → `iStack_490` (normal)

#### Related helper semantics

These helpers are adjacent in the decompilation and matter for interpretation:
- `BSFaceGenManager::ResolveFaceGenShaderTexture` (`0x824881c8`) is now decompiled. It is a path
  resolver, not a texture loader: it formats and existence-checks candidate sibling texture paths,
  writes the winning path into the output string object, and returns nonzero only when a candidate
  exists.
- `BSFaceGenModel::LoadModelTexture` (`0x82490648`) is a narrow lazy-load wrapper. It allocates a
  small object at `param_1 + 0x0C` and copies in a path/object reference. It is **not** the same
  function as `ResolveFaceGenShaderTexture`.
- `BSFaceGenModel::ForceLoadModelTexture` (`0x824921B0`) strips at byte `0x2E` (`'.'`) in a
  0x104 buffer, appends a constant suffix, then calls `LoadModelTexture` plus a follow-up helper.
  This strongly suggests sibling-texture derivation, but the exact suffix meaning is not resolved
  from the checked-in artifacts alone.
- `TESNPC::GetHeadPartModTexture` resolves a head-part-specific texture path, tries to load it,
  and falls back to `GetDefaultBaseModulationTexture()` if the load fails.
- `TESRace::GetBodyModTextureFileName` walks the race inheritance chain and formats a path from
  the deepest race ancestor's body-mod texture field.

#### Shader texture binding (lines 1770-1813)

After texture acquisition, binds textures to the BSShaderProperty (`piVar12`):

```
SLOT 0 — Base diffuse (lines 1770-1777):
    vtable+0x100(piVar12, 0, texture)
    = Set base diffuse texture on shader at index 0
    Source: iStack_498 loaded as NiSourceTexture → apiStack_480

SLOT 0 — Normal map (lines 1778-1784):
    vtable+0x104(piVar12, 0)
    = Enable normal map at index 0
    Source: iStack_490 loaded as NiSourceTexture → apiStack_468
```

Then **only for FaceGen shader** (when `iStack_488 == 0x0E`, lines 1786-1813):

```
SLOT 1 MAP A — Facemod delta texture (line 1787):
    vtable+0xFC(piVar12, 1, apiStack_4a0[0])
    = Set facemod delta at shader index 1

SLOT 1 MAP B — Secondary FaceGen texture (lines 1788-1792):
    vtable+0x100(piVar12, 1, texture)
    = Set the texture sourced from `piStack_484`, or fallback GetDefaultDetailModulationTexture()
    The common "detail modulation" label is plausible but still partly interpretive

ADDITIONAL FLAGS (line 1793):
    func_0x82284de8(piVar12, 10, 1)
    = Enable FaceGen-specific shader features

SLOT 2 — _sk face tint texture (lines 1799-1812):
    vtable+0xF0(piVar12, 2, 0, sk_texture)
    = Set face tint texture at shader index 2
    Only if iStack_498 != 0 (diffuse exists)
    Constructs path by appending "_sk" suffix to diffuse texture path
```

---

## 6. Shader Texture Binding

### `Lighting30Shader::SetFaceGenMaps`

**Source**: `facegen_textures_decompiled2.txt` lines 27-96
**PDB**: [0004:008ABCD0], VA 0x82AFBCD0, 156 bytes

Binds 3 textures to the shader's texture array:

```c
void SetFaceGenMaps(shader, param_2, shaderProperty, hasThirdTexture) {
    // Slot at tex_array+0x10: from shaderProperty offset 0xB4 (facemod delta)
    tex_array[0x10]->data = shaderProperty[0x2D]->field_4;     // line 81-82

    // Slot at tex_array+0x14: from shaderProperty offset 0xB8 (secondary FaceGen texture)
    tex_array[0x14]->data = shaderProperty[0x2E]->field_4;     // line 83-84

    // Slot at tex_array+0x18: from GetTexture(2) or fallback to property[0x2E]
    if (hasThirdTexture) {                                       // line 85
        tex = vtable+0xF4(shaderProperty, 2, 0);               // GetTexture(slot=2)
        if (tex == NULL) tex = shaderProperty[0x2E]->field_4;  // fallback
        tex_array[0x18]->data = tex;                            // line 91
    }
}
```

**Shader texture slots for FaceGen faces**:
| Slot Offset | Content | Source in PrepareHeadForShaders |
|------------|---------|-------------------------------|
| +0x10 | Facemod delta texture | vtable+0xFC at index 1 (line 1787) |
| +0x14 | Secondary FaceGen texture (`property[0x2E]`) | vtable+0x100 at index 1 (line 1792) |
| +0x18 | `_sk` face tint | vtable+0xF0 at index 2 (line 1810) |

**Note**: The base diffuse texture is set SEPARATELY on slot 0 (lines 1770-1777) and is NOT one of the 3 FaceGen-specific maps. The FaceGen shader receives **4 texture inputs** total:
1. Base diffuse (standard slot 0)
2. Facemod delta (FaceGen slot at +0x10)
3. Secondary FaceGen texture (FaceGen slot at +0x14)
4. Face tint `_sk` (FaceGen slot at +0x18)

---

## 7. Pixel Shader Compositing

**STATUS: RESOLVED — shader disassembled from PC shaderpackage003.sdp**

### 7.1 SDP File Format

Bethesda packages compiled shaders in `.sdp` (Shader Package Data) files:
- **Header**: 12 bytes — `uint32 name_field_size(=100)`, `uint32 shader_count`, `uint32 data_size`
- **Entries**: 256-byte name (null-terminated + 0xFD padding on PC, 0x00 on Xbox), 4-byte LE size, then raw bytecode
- PC shaders are DirectX 9 SM 2.x/3.x bytecode; Xbox shaders are Xenon microcode
- **Source**: `Sample/Full_Builds/Fallout New Vegas (PC Final)/Data/Shaders/shaderpackage003.sdp`

### 7.2 FaceGen Shader Variants

13 SKIN pixel shaders (SKIN2000–SKIN2012). Only 8 have FaceGen support:

| Variant | FaceGen | Shadows | Lights | Size |
|---------|---------|---------|--------|------|
| SKIN2000 | Yes | No | 1 dir | 1136B |
| SKIN2001 | Yes | Yes | 1 dir | 1320B |
| SKIN2002 | Yes | No | 2 (dir+atten) | 1672B |
| SKIN2003 | Yes | Yes | 2 | 1868B |
| SKIN2010 | Yes | No | 5 (dir+4 point) | 2920B |
| SKIN2011 | Yes | No | 4 (dir+3 point) | 2468B |
| SKIN2012 | Yes | No | 2 (dir+1 point) | 1980B |

Non-FaceGen variants (SKIN2004–2009) lack `FaceGenMap0`/`FaceGenMap1` samplers
and are used for non-head skin meshes (hands, body).

### 7.3 SKIN2000.pso Sampler Mapping (from CTAB)

CTAB (Constant Table) embedded in the shader bytecode provides HLSL variable names:

| Sampler | CTAB Name | Content | Source in PrepareHeadForShaders |
|---------|-----------|---------|-------------------------------|
| s0 | `BaseMap` | Base diffuse texture | vtable+0x100 at index 0 (line 1770) |
| s1 | `NormalMap` | Normal map | vtable+0x104 at index 0 (line 1778) |
| s2 | `FaceGenMap0` | Facemod delta texture (_0.dds) | vtable+0xFC at index 1 (line 1787) |
| s3 | `FaceGenMap1` | Secondary FaceGen texture from `property[0x2E]` | vtable+0x100 at index 1 (line 1792) |

**Constants**: `AmbientColor` (c1), `PSLightColor` (c3), `Toggles` (c27)

### 7.4 Delta Compositing Formula (THE KEY FINDING)

**Source**: SKIN2000.pso disassembly, instructions at tokens 227–235

```hlsl
// Step 1: Decode facemod delta from [0,1] texture sample to [-1,+1]
float3 delta = (FaceGenMap0.Sample(uv) - 0.5) * 2.0;

// Step 2: Additive compositing
float3 diffuse = BaseMap.Sample(uv).rgb + delta;
```

**Assembly**:
```
texld r3, t0, s2          ; r3 = sample(FaceGenMap0, uv)
add r3.xyz, r3, c2.x      ; r3 = facemod - 0.5       (c2.x = -0.5)
mad r1.xyz, r3, c2.y, r1  ; r1 = base + (facemod - 0.5) * 2.0  (c2.y = 2.0)
```

**In byte-space**: `result = base_byte + (facemod_byte * 2 - 255)`, clamped to [0, 255]

**Neutral verification**: For facemod byte 127 (from GetCompressedImage):
- `127/255 = 0.498`, `(0.498 - 0.5) * 2 = -0.004` → effectively zero
- The -0.004 bias is within DXT1 compression noise

### 7.5 Detail Modulation (FaceGenMap1)

**Source**: SKIN2000.pso disassembly, instructions at tokens 240–251

```hlsl
// Multiplicative application with 4× scale
float3 modulation = FaceGenMap1.Sample(uv).rgb;
float3 textured = diffuse * modulation * 4.0;
```

**Assembly**:
```
texld r2, t0, s3          ; r2 = sample(FaceGenMap1, uv)
add r2.xyz, r2, r2        ; r2 = modulation * 2
mul r1.xyz, r1, r2        ; r1 = diffuse * modulation * 2
add r1.xyz, r1, r1        ; r1 = diffuse * modulation * 4
```

The 4× scale means neutral modulation is at pixel value ~64/255 (0.25 × 4 = 1.0).
This is the texture bound through `property[0x2E]` / slot `+0x14`. The current best
interpretation is "detail modulation," but that semantic label is still under review.
It is not the slot-2 `_sk` texture in the audited SKIN2000 binding path.

### 7.6 Fog Blending (NOT Subsurface Scattering) — CORRECTED

**Source**: SKIN2000.vso + SKIN2000.pso disassembly

Previously identified as "subsurface scattering" — **actually distance fog blending**.
Vertex shader (SKIN2000.vso) output `aout1` maps to PS `v1`:
- `v1.xyz` = `FogColor` (constant c15 in VS, passed through as color interpolant)
- `v1.w` = fog factor (exponential distance fog: `exp(-(dist - FogParam.x) / FogParam.y * FogParam.z)`)

```hlsl
// Distance fog (controlled by Toggles.y)
float3 lit = shade * textured;
float3 fogged = lerp(lit, FogColor, fogFactor);  // v1.rgb = FogColor, v1.w = fogFactor
float3 result = (Toggles.y >= 0) ? lit : fogged;
```

**Assembly**:
```
mad r2.xyz, r0, -r1, v1    ; r2 = FogColor - shade * textured
mul r0.xyz, r0, r1          ; r0 = shade * textured (standard lit)
mad r1.xyz, v1.w, r2, r0   ; r1 = lerp(shade*tex, FogColor, fogFactor)
cmp r3.xyz, -c27.y, r0, r1 ; r3 = select based on Toggles.y (fog toggle)
```

**SKIN2000.vso register mapping** (from vertex shader disassembly):
| VS Output | Content | PS Register |
|-----------|---------|-------------|
| `o0.xy` | UV texcoord | `a0` (t0) |
| `o1.xyz` | light dir in tangent space (TBN * LightData) | `a1` (t1) |
| `o1.w` | 1.0 (constant) | `a1.w` |
| `o6.xyz` | view dir in tangent space (TBN * (Eye - pos)) | `a6` (t6) |
| `aout0` | vertex color (pass-through) | `v0` |
| `aout1.xyz` | FogColor (c15) | `v1.xyz` |
| `aout1.w` | fog factor (exponential) | `v1.w` |

**Impact on CPU renderer**: No subsurface scattering implementation needed. Fog is irrelevant
for sprite generation (objects are at camera distance, fog factor ≈ 0).

### 7.7 Lighting Model

SKIN2000 uses the same hemisphere ambient + Fresnel model documented in our GLSL shader:

```hlsl
float NdotL = saturate(dot(N, L));
float NdotH = saturate(dot(N, H));
float HdotNegL = saturate(dot(H, -L));
float fresnel = HdotNegL * (1 - NdotH) * (1 - NdotH);
float shade = saturate(min(light * NdotL + light * 0.5 * fresnel, 1.0) + ambient);
```

### 7.8 Complete FaceGen Rendering Pipeline (shader-side)

```
1. Sample BaseMap (s0)           → base diffuse
2. Sample NormalMap (s1)         → decode: (sample - 0.5) * 2, then normalize for bump
3. Compute lighting              → shade = hemisphere ambient + NdotL + Fresnel
4. Sample FaceGenMap0 (s2)       → decode delta: (sample - 0.5) * 2
5. Composite: diffuse = base + delta   [ADDITIVE]
6. Sample FaceGenMap1 (s3)       → apply: diffuse * modulation * 4  [MULTIPLICATIVE]
7. Optional vertex color         → diffuse *= vertexColor  (when Toggles.x set)
8. Optional fog blend            → blend toward FogColor by fog factor
9. Output: shade * diffuse       → oC0
```

---

## 8. GECK Pre-Bake Path — VERIFIED AT THE BAKE-FORMULA LEVEL (decompiled 2026-03-17)

The GECK export path directly calls bake routine `FUN_00695b50` while assembling FaceGen
head textures (`facegen_geck_face_mod_export.txt`, direct calls at the export path around
the `local_3b8` texture). That is strong evidence that shipped `_0.dds` facemods use the
same general delta-encoding family as the runtime bake path, including neutral 127 encoding.

What this does **not** prove by itself is full end-to-end parity between GECK export output,
runtime fallback output, and shipped game assets.

### 8.1 GECK Bake Formula — VERIFIED (x86 assembly traced)

**Source**: GECK.exe `FUN_00695b50` (1684 bytes), decompiled via PyGhidra from `GeckProject`.
Full disassembly in `tools/GhidraProject/facegen_geck_bake_assembly.txt`.

**Bake accumulator assembly (confirmed from FLD → FMUL → __ftol2_sse chain)**:
```
For each morph (0x58-byte entries at this->0xC->0x8->0x10 vector):
  coeff_int = __ftol2_sse(coefficient * double[0xd77048])   // FLD [coeff_array + stride*morph_idx*4]
  if coeff_int == 0: skip
  scale_int = __ftol2_sse(morph_scale * double[0xd77048])   // FLD [morph_entry + 0x00] — SAME float all 3 channels
  combined  = scale_int * coeff_int                          // IMUL at 0x695F9C
  For each channel (R=0, G=1, B=2) — loop via uStack_3c += 0x18, < 0x48:
    delta_ptr = *(morph_entry + 0x10 + channel*0x18 + 0x0C)  // per-channel delta byte array
    For each pixel (width * height):
      accum[pixel*3 + channel] += (signed_byte)delta * combined  // MOVSX + IMUL + ADD at 0x696016-0x69601D

Final normalization — For each channel:
  output[pixel] = accum[pixel] * 1.5258789e-05                   // = 1/65536
```

**Key assembly evidence** (from `facegen_geck_bake_assembly.txt`):
- **0x695EAB**: `FLD float ptr [EAX + EDX*4]` — loads coefficient from model vector at +0x4C
- **0x695EAE**: `FMUL double ptr [0x00d77048]` — multiply by scaling constant (256.0)
- **0x695EB7**: `CALL 0x00c5d220` — `__ftol2_sse` truncation → `coeff_int`
- **0x695F88**: `FLD float ptr [EDX + ECX*1]` — loads morph scale from entry +0x00
  - `EDX = [ESP+0x70]` = morph entry byte offset (advances by 0x58 per morph)
  - `ECX = [ESI+0xc]` = morph vector start pointer
  - **NOT channel-dependent** — same float loaded for all 3 channel iterations
- **0x695F8B**: `FMUL double ptr [0x00d77048]` — SAME constant as coefficient
- **0x695F91**: `CALL 0x00c5d220` — `__ftol2_sse` truncation → `scale_int`
- **0x695F9C**: `IMUL EDI, [ESP+0x3c]` — `combined = scale_int * coeff_int`
- **0x696016-0x696025**: Inner pixel loop: `MOVSX EBX, byte [EAX+EDX]` + `IMUL EBX, EDI` + `ADD [ECX], EBX`

**Critical finding: NO per-channel scale factor exists.**
The per-channel `__ftol2_sse` call inside the channel loop loads from
`morph_vector_start + morph_entry_offset` (the morph's +0x00 float), which is the
SAME value for all 3 channels. The compiler simply didn't hoist the FLD out of the loop.

### 8.2 FREGT003 Runtime Morph Entry Layout (0x58 bytes)

**Source**: GECK.exe `FUN_0085fb40` (FREGT003 parser, 2046 bytes).

```
+0x00: float32 scale        — single per-morph scale (read as 4 bytes from file)
+0x04: uint32  width         — texture width (e.g., 256)
+0x08: uint32  height        — texture height (e.g., 256)
+0x0C: int32   row_padding   — alignment: (width+7)&~7 - width
+0x10: channel 0 (R) sub-struct   (0x18 bytes — std::vector-like with delta byte data)
+0x28: channel 1 (G) sub-struct   (0x18 bytes)
+0x40: channel 2 (B) sub-struct   (0x18 bytes)
Total: 0x10 + 3×0x18 = 0x58 ✓
```

Each channel sub-struct (0x18 bytes):
- +0x0C from channel start: delta data begin pointer (accessed as entry + channel*0x18 + 0x1C)
- +0x10 from channel start: delta data end pointer (accessed as entry + channel*0x18 + 0x20)

### 8.3 GECK Bake-Input Load Chain — partially verified

Audit note (2026-03-18):
- The actual GECK head-texture input load path is narrower than some earlier notes implied.
- `FUN_0068cb60` derives the facemod source path by replacing the selected head mesh extension with
  `.egt`.
- `FUN_0068fe90` is the cache/load/generate orchestrator for the head FaceGen model package.
- `FUN_0068d670` does cache lookup and post-load initialization for that package.
- `FUN_00696280` lazily populates model `+0x0C` from its stored path string via
  `FUN_00695ae0 -> FUN_00695a10 -> FUN_0085fb40`. This is the real `FREGT003` loader path used
  by `FUN_00695b50`, which then iterates the loaded `0x58` texture morph entries during the bake.
- `FUN_00695b50` consumes already-loaded EGT morph data; it does not resolve the `.egt` filename
  itself.
- The previously suspicious `FUN_00696750 -> FUN_00861d60` branch is a different loader. The
  decompiled file header in `FUN_00861d60` is `FREGM002`, so that branch should not be treated as
  the head texture EGT loader.
- Follow-up decompilation now makes that branch look more specifically like a model-side
  `FREGM002` / `BSFaceGenModel` lazy-loader rather than a texture-generation helper.
  `FUN_006975c0` allocates a `0x34` object when its cached `+0x08` field is null, calls
  `FUN_00696750` with the existing path string, and stores the returned object at `+0x08`; the
  constructor in `FUN_00696750` itself first builds two `0x18` subrecords and then delegates the
  real file load to `FUN_00861d60`.
- When the GECK generates data instead of reusing a cached package, `FUN_00697a10` also loads the
  selected head mesh (`FUN_004c0040(param_2 + 7, ...)`) and loads coord data via `FUN_00865fb0`
  (string ref `FRTRI003`) before morph generation.
- Focused decompilation of `FUN_00865fb0` and its helper layer shows that `FRTRI003` is not a
  single opaque coord blob. `FUN_00696680` first initializes a `0xF0` output object as nine
  contiguous `0x18`-byte container slots, and `FUN_00865fb0` then populates those slots with
  multiple typed sections using helper families whose container record widths are:
  - `0x0C` vector arrays via `FUN_0086a9a0`
  - `0x10` scalar/structured arrays via `FUN_0086ab90`
  - `0x08` nested sections via `FUN_0086ae70`
  - `0x20` name/metadata-like records via `FUN_0086b260`
  - `0x2C` name-table-like records via `FUN_0086b4b0`
  - `0x34` inline-vector morph records via `FUN_0086b7f0`
  - `0x38` indexed morph records via `FUN_0086bbd0`
- A tighter loader-order pass now makes the post-header materialization sequence explicit.
  Immediately after the shared FaceGen header layer succeeds, `FUN_00865fb0` does this:
  - `param_2 + 0x00`: first `0x0C` float3 family via `FUN_0086a9a0`
  - `param_2 + 0x18`: second `0x0C` float3 family via `FUN_0086a9a0`
  - `param_2 + 0x30`: `0x10` record family via `FUN_0086ab90`
  - `param_2 + 0x90`: `0x20` metadata family via `FUN_00869fa0`
  - `param_2 + 0xA8`: `0x2C` named metadata family via `FUN_0086b4b0`
  - `param_2 + 0xC0`: `0x34` differential morph family via `FUN_0086b7f0`
  - `param_2 + 0xE4`: `0x38` statistical morph family via `FUN_0086bbd0`
  There is also an optional mid-structure branch controlled by `local_34/local_38` that appears
  to materialize a nested `0x08` family plus another late `0x0C` / `0x10` pair before the later
  record families are consumed.
- The main temp-wrapper helper `FUN_008647e0` clones mesh-aligned section data into that same
  multi-section object before additional nested parsing, so this path is doing structured
  generation-context assembly, not just file I/O.
- A deeper pass on `FUN_008647e0` makes the section order more concrete. It clones the early
  `+0x0C/+0x10`, `+0x24/+0x28`, `+0x3C/+0x40`, `+0x54/+0x58`, `+0x6C/+0x70`, and `+0x84/+0x88`
  containers directly, then uses wrapper iterators to copy the later record families, including
  string payloads and per-record arrays for the `0x2C`, `0x34`, and `0x38` sections.
- The strongest current mapping is:
  - `+0xB4/+0xB8`: `0x2C` named metadata records
  - `+0xCC/+0xD0`: `0x34` inline-vector morph records
  - `+0xE4/+0xE8`: `0x38` indexed morph records
  - the earlier sections appear to support assembly, naming, and remapping, but are not consumed
    directly by the final morph-generation functions we traced.

Practical takeaway:
- If the question is "what code loads the files used for the final facemod texture bake?", the
  texture side is `mesh path -> FUN_0068cb60 (.egt) -> FUN_0068fe90/FUN_0068d670 ->
  FUN_00696280 -> FUN_00695ae0 -> FUN_00695a10 -> FUN_0085fb40`, not
  `FUN_00696750 -> FUN_00861d60`.
- If the question is "what upstream data does the GECK assemble before morph generation?", the
  answer now clearly includes `FRTRI003` plus mesh-derived structured section data, and we do not
  currently mirror that path in our own codebase.

### 8.4 Morph Generation Paths (FUN_00697a10)

The GECK's EGT generation orchestrator now looks materially clearer:
- `FUN_00697a10` allocates the `0xF0` morph/coord context via `FUN_00696680`, populates it via
  `FUN_00865fb0`, and only then dispatches to one of the morph builders.
- **`FUN_00698be0`** (4692 bytes): Full morph set — categorizes morphs by name
  (expressions: 15, modifiers: 17, phonemes: 16, vampire: 1) into `BSFaceGenMorphDataHead`.
  Its direct inputs are:
  - `+0xCC/+0xD0` `0x34` records with inline `float3` payloads
  - `+0xE4/+0xE8` `0x38` records with indexed `uint32` payloads plus an extra field at `+0x1C`
- **`FUN_00699e50`** (647 bytes): Filtered — extracts only "HairMorph" from the
  `+0xCC/+0xD0` inline-vector family. In the current decompilation it does **not** read the
  later `+0xE4/+0xE8` indexed family.

Field-level shape from the current decompilation:
- The post-header `FRTRI003` order is now explicit. `FUN_00865fb0` materializes two early
  `0x0C` families, then a `0x10` family, then `0x20`, `0x2C`, `0x34`, and `0x38`. A guarded
  mid-structure branch can also inject an additional `0x08` family and, on one side of that
  branch, another late `0x0C` / `0x10` pair.
- That guarded branch is clearer now. When `(local_34 & 1) != 0` and `local_38 == 0`, the loader
  materializes the optional `0x08` family and then clones the earlier `0x0C` / `0x10` families
  via `FUN_00869dc0` / `FUN_00869eb0`. When `local_38 != 0`, it still materializes the optional
  `0x08` family but then reads a late `0x0C` family at `+0x60` and a late `0x10` family at
  `+0x78` instead.
- The early `0x10` family still looks like a plain fixed-width 16-byte record array, not a
  nested TRI payload. `FUN_0086ab90` resizes it, `FUN_0071ccd0` allocates `count * 0x10` bytes,
  `FUN_00871440` / `FUN_00872df0` do backward/forward four-dword copies, `FUN_00871990` is the
  relocate loop, and `FUN_00870b80` / `FUN_00872b30` trim/destroy by adjusting or walking the
  same 16-byte stride. No per-record nested cleanup or decode appears in this layer.
- The optional `0x08` family also still looks like a plain fixed-width 8-byte record array.
  `FUN_0086ae70` resizes it, `FUN_0071cc70` allocates `count * 0x08` bytes, `FUN_00871560` /
  `FUN_00872ed0` do backward/forward two-dword copies, `FUN_00871a70` is the relocate loop, and
  `FUN_008ec0c0` / `FUN_00872b80` / `FUN_00a08180` trim or destroy by walking the same 8-byte
  stride. This layer also does not expose any nested TRI-specific semantics.
- Several helpers around those families now look definitively generic rather than format-specific:
  `FUN_0086a460` is byte-range growth/shrink for helper-backed inline/heap strings,
  `FUN_00869dc0` / `FUN_00869eb0` are clone/copy helpers over already-materialized early arrays,
  and `FUN_008df040` is just an iterator-range comparison helper reused in many containers.
- The larger follow-up helper we checked after this, `FUN_00696ab0`, does **not** rescue those
  early `0x10` / `0x08` families as texture-generation consumers. Its nearest traced caller is
  `FUN_00697910`, and the sibling destructor `FUN_00697980` identifies that object family as
  `BSFaceGenModel`. In the current decompile, `FUN_00696ab0` operates on the model-side object at
  `*(context + 8)` and iterates two `0x18`-stride groups rooted under that subobject, but it does
  not directly read the coord-context slots at `+0x30`, `+0x48`, or `+0x78`. So within the traced
  `FUN_00697a10` EGT-generation branch, those early families still look lifecycle-managed rather
  than semantically consumed.
- The next `BSFaceGenModel` layer now also looks render/model-side rather than bake-side.
  `FUN_006941c0`, which is called by `FUN_00697910`, requires a live model object at
  `(this + 8)->+0x10->+0xB8`, then attaches two extra-data objects through `FUN_00818480`.
  `FUN_006940a0` constructs a `BSFaceGenModelExtraData` object that only holds a ref-counted
  pointer to the current model-side object, and `FUN_006989b0` constructs a
  `BSFaceGenBaseMorphExtraData` object by copying `count` `float3` entries from the source at
  `+0xB8`, with optional appended vectors supplied by the caller. The later raw-PDB
  `CreateNewMesh` helper pass now identifies the matching Xbox runtime constructors directly at
  `0x8248FDC8` and `0x824965E0`, so this extra-data interpretation is no longer just an anonymous
  helper inference.
- The accessor layer under that `+0xB8` source narrows the meaning further. `FUN_0080c4d0` /
  `FUN_0080c540` open and close guarded access around a helper object at `+0x30` using two flags
  at `+0x3A/+0x3B`, and `FUN_0080c580` then returns either a fetched vector span from
  `FUN_0082aba0` or a fallback span rooted at `+0x20` with stride `0x0C`. So this branch does
  expose base morph vectors to the model pipeline, but it still does so through an already-
  materialized runtime/model-side accessor interface rather than a direct `FRTRI003` reader.
- One more layer down, that `+0x30` helper now looks like an indexed descriptor table over cached
  span providers rather than a raw morph-file decoder. `FUN_0082aa10` and `FUN_0082aa50` acquire
  and release entries from an array at `+0x20`, bounded by a count at `+0x26`; each non-null
  entry exposes a small interface with methods at vtable `+0x08` and `+0x0C`, plus size/pointer
  fields at `+0x04/+0x08`. `FUN_0082aba0` then indexes a parallel `0x1C`-stride descriptor table
  at `+0x14`, checks the selected descriptor against the same entry array, and resolves the final
  returned pointer as `entry->data + descriptorOffset`. So this layer is the first concrete
  runtime morph-span resolver we have, but it still looks like descriptor/caching logic built on
  already-materialized sources rather than the place where `FRTRI003` or `FREGM002` bytes are
  initially interpreted.
- The sibling accessors above `FUN_0082aba0` reinforce that interpretation. `FUN_0080c620` and
  `FUN_0080c6a0` are thin wrappers that request descriptor indices `1` and `2` only when bit `0`
  of the helper flags at `+0x2C` is set. `FUN_0080c720`, `FUN_0080c7d0`, and `FUN_0080c890`
  choose other descriptor indices from the same flag word and otherwise fall back to local spans
  rooted at `+0x24`, `+0x28`, and `+0x2C` with element widths `0x0C`, `0x10`, and `0x08`
  respectively. That shape looks much more like multiple runtime geometry streams exposed through
  one cached accessor object than a hidden second-stage FaceGen file decoder.
- The owner/provider side now points in the same direction. `FUN_0080c3d0` is just the dedicated
  ref-counted setter for the `+0x30` helper. `FUN_0080c950` can swap that helper in from
  `param_1->FUN_0081c060()` once `param_1 + 0xD8` passes a version gate, and
  `FUN_0080c9b0` compares the full runtime stream-owner state by checking local spans at
  `+0x20/+0x24/+0x28/+0x2C`, flags at `+0x38/+0x39`, and finally delegating `+0x30` helper
  equality through the helper vtable `+0x5C` method.
- `FUN_0081c060` itself does not look FaceGen-specific. It just increments an index at `+0x260`,
  looks up the next selector entry from `+0x254`, and returns a helper pointer from `+0x1F0`.
  The surrounding neighborhood shows a sibling `FUN_0081c090` with the same pattern over
  `+0x264/+0x270`, plus a very broad caller surface well outside FaceGen code. So the current read
  is that the `+0x30` helper is supplied by a generic engine stream-provider object, not built
  directly by the FaceGen runtime branch itself.
- The local stream-owner side is now also much clearer and strongly generic. `FUN_0080d010`
  reads the object from a binary stream at `param_1 + 0x24C`, populating the local geometry spans
  at `+0x20`, `+0x24`, `+0x28`, and `+0x2C`, plus flags at `+0x38/+0x39`; `FUN_0080d690` writes
  the same fields back out; and `FUN_0080da20` serializes them under the member names
  `m_pkVertex`, `m_pkNormal`, `m_pkColor`, `m_pkTexture`, `m_kBound`, and
  `m_spAdditionalGeomData`. The helper layer under this (`FUN_0080cc10`, `FUN_0080cc50`,
  `FUN_0081b440`, `FUN_0081b510`) is just binary stream IO and base-object preamble logic.
- That member/offset layout matches the repo's existing
  `src/FalloutXbox360Utils/Core/Formats/Esm/Runtime/Readers/RuntimeGeometryScanner.cs`
  documentation for `NiGeometryData`: refcount at `+0x04`, bound at `+0x10`, vertex/normal/color/UV
  pointers at `+0x20/+0x24/+0x28/+0x2C`, and `m_spAdditionalGeomData` at `+0x30`. So this branch
  is now best interpreted as generic mesh/geometry-data serialization plus an optional additional
  geometry helper, not as FaceGen-specific runtime decoding.
- A focused AdditionalGeometryData pass now narrows that helper more concretely. `FUN_0082c2c0`
  serializes `m_usVertexCount`, `m_uiDataStreamCount`, repeated `DataStream Index` entries,
  `m_aDataBlocks.GetSize()`, repeated `DataBlock Index` entries, `m_uiDataBlockSize`, and
  `m_pucDataBlock`. That is an exact semantic match for the engine's
  `NiAdditionalGeometryData` / `BSPackedAdditionalGeometryData` family rather than a FaceGen-only
  helper. On the asset side, the shipped Xbox sample base head mesh `headhuman.nif` has block `7`
  typed as `BSPackedAdditionalGeometryData`, so for the main Xbox head mesh the runtime object
  hanging off `m_spAdditionalGeomData` is now best interpreted as the packed AGD subtype or a
  very thin wrapper around it.
- The Xbox MemDebug PDB now strengthens that class-family match materially. The raw-PDB pass
  identifies named game-side methods for `NiAdditionalGeometryData` and
  `BSPackedAdditionalGeometryData`, and the first concrete behavioral split is useful:
  `NiAdditionalGeometryData::GetVBReady` returns `0`, while
  `BSPackedAdditionalGeometryData::GetVBReady` returns `1`. That is strong evidence that the
  packed subtype is the GPU-ready/render-path form, while the base AGD class is the more generic
  serializable container.
- The same Xbox PDB-backed decompile also makes the base AGD layout more concrete. The game-side
  `NiAdditionalGeometryData::{SetDataStreamCount,SetDataStream,GetDataStream,LoadBinary,SaveBinary}`
  methods confirm a `0x1C`-stride stream-descriptor array at `+0x14`, a stream count at `+0x10`,
  a D3D stream selector at `+0x18`, and a separate data-block table rooted at `+0x20`. That
  aligns with the serializer field names we already recovered from the GECK side and makes the
  current `NiGeometryData::m_spAdditionalGeomData` interpretation much harder to dismiss as a
  false positive.
- The game-side FaceGen path also sharpens the TRI/runtime link. In the Xbox MemDebug PDB,
  `BSFaceGenModel::LoadModelMesh` still takes separate EGM/NIF/TRI filename parameters, and the
  decompiled body shows the TRI branch validating its vertex count against the loaded mesh before
  materializing extra per-vertex `float3` data. That does not look like dead tooling residue; it
  looks like live runtime/model input.
- That is reinforced one step later by `BSFaceGenManager::RefreshMeshFromBaseMorphExtraData`.
  The named Xbox function copies `float3` vectors out of an attached base-morph extra-data object
  into the destination mesh positions and then invalidates/refreshes the geometry data if the
  mesh has an update sink. So the runtime branch we had been tracing anonymously really is a
  mesh-refresh path fed by FaceGen base morph extra data, not just a generic helper with no
  practical impact on head rendering.
- A deeper Xbox runtime pass now narrows the TRI side much further. The helper object under
  `BSFaceGenModel::LoadModelMesh` is not a mystery polymorphic class; `TRI_Helper_CreateObject`
  just zeroes a plain aggregate container, `TRI_Helper_DestroyObject` tears down a fixed set of
  dynamic-array fields, and the accessors above it are simple pointer arithmetic:
  `TRI_Helper_GetRecord2C(index) = base + index * 0x2C`,
  `TRI_Helper_GetRecord30(index) = base + index * 0x30`, and
  `TRI_Helper_GetVector3At(index) = base + index * 0x0C`.
- The loader above that container is also now much clearer. `TRI_Helper_LoadIntoObject` reads the
  raw TRI stream into several section families rooted at object offsets `+0x00`, `+0x10`,
  `+0x20`, `+0x60`, `+0x70`, `+0x80`, and `+0x90`, then materializes per-record vector and index
  payloads through those simple accessors. That makes the remaining implementation gap much more
  concrete: most of the remaining unknown is no longer low-level file decode, but the exact
  semantic meaning of each already-materialized runtime section.
- The two runtime builders above the container are now easier to classify too. The larger
  `TRI_Helper_BuildExtendedMorphObject` and smaller `TRI_Helper_BuildCompactMorphObject` are no
  longer anonymous: the Xbox MemDebug PDB resolves them to
  `BSFaceGenMorphDataHead::BSFaceGenMorphDataHead` and
  `BSFaceGenMorphDataHair::BSFaceGenMorphDataHair` respectively. That is a meaningful narrowing:
  the runtime TRI bridge is not building some generic helper object, it is building the concrete
  head- and hair-morph data classes consumed by the game-side FaceGen path.
- The payload wrappers under those builders also now have concrete names. The runtime
  `float3[]` wrapper is `BSFaceGenMorphDifferential::BSFaceGenMorphDifferential`, and the
  runtime `uint32[] + base-index` wrapper is
  `BSFaceGenMorphStatistical::BSFaceGenMorphStatistical`. That matches the earlier GECK-side
  conclusion that the `0x2C`/`0x34` family is differential/vector-bearing while the
  `0x30`/`0x38` family is statistical/index-bearing.
- The named head constructor makes the runtime grouping behavior clearer too. The
  `BSFaceGenMorphDataHead` constructor walks both TRI morph families rooted at `+0x80` and `+0x90`,
  compares their record names against several category tables, and materializes grouped morph slots
  as `BSFaceGenMorphDifferential` or `BSFaceGenMorphStatistical` objects. The category ownership is
  now much tighter than it first looked: the constructor loops the `0x832451d0` table for `15`
  expression slots, the `0x83245210` table for `17` modifier slots, the `0x83245258` table for
  `16` phoneme slots, and the singleton `0x8324520c` for the custom slot. Named runtime consumers
  now agree with that split too: `BSFaceGenNiNode::GetViewerStrings`, `Script::ModifyFaceGen`, and
  the `DebugTextFaceGen::*Info` helpers all build those same three globals as
  `HeadTable_Expressions`, `HeadTable_Modifiers`, and `HeadTable_Phonemes`. So the remaining
  uncertainty is no longer which category tables the head constructor is using; it is the exact
  apply semantics for partial/empty sparse TRI subsets inside those shared categories.
- The local TRI anchor comparison also narrows the raw-tail question. The base head
  `headhuman.tri` advertises header hints `0x34 = 38`, `0x38 = 8`, `0x2C = 238`, while the
  non-head sibling `eyelefthuman.tri` advertises `0x34 = 0`, `0x38 = 4`, `0x2C = 196`. So the
  eye TRI already diverges from the head family at the record-family-count level: it still carries
  named metadata and indexed/statistical families, but no differential/vector family at all. A
  newer selector-bridge pass now shows that raw family shape does **not** directly choose the
  runtime builder: `BSFaceGenModel::LoadModel` is just a thin wrapper that forwards the NIF/EGM/TRI
  inputs plus a selector/flag pair into `LoadModelMesh`, and `LoadModelMesh` routes
  `selector == -1` into `BSFaceGenMorphDataHead` while any non-negative selector takes the compact
  `BSFaceGenMorphDataHair` path. Named runtime callers line up with that split: `BipedAnim`,
  `CreateFaceGenHead`, `GetFaceGenModel`, and `AttachEyesToHead` all pass `-1`, while
  `AttachHairToHead` passes `0` and later `2`. So the eye family's lack of `0x34` differential
  records does **not** imply the compact/hair builder. The remaining Wave 2 question is now
  narrower: how the extended/head builder semantically uses the sparser non-hair TRI families
  (eyes, mouth, teeth, tongue), not which top-level builder owns them.
- A focused `inspect-tri` name probe now makes that remaining question more concrete. The sparse
  non-hair TRI families are not carrying arbitrary part-local identifiers; they expose subsets of
  the same shared FaceGen slot-name universe used by the head path:
  - `eyelefthuman.tri`: only `LookDown`, `LookLeft`, `LookRight`, `LookUp`
  - `mouthhuman.tri`: `Aah`, `BigAah`, `Anger`, `Surprise`
  - `tonguehuman.tri`: `Aah`, `BigAah`, `Fear`
  - `teethlowerhuman.tri`: `Aah`, `BigAah`, `Fear`, `CombatAnger`
  - `teethupperhuman.tri`: no identifier-like names and no `0x34`/`0x38`/`0x2C` hints
- That is a useful narrowing. Eyes now look like a pure modifier-subset TRI family, while the
  mouth/tongue/lower-teeth set looks like sparse phoneme/expression subsets, and upper teeth look
  closer to geometry/topology-only support data. So the extended/head builder is very likely still
  matching these sparse part TRIs against the same global head-category tables rather than against
  a separate part-local naming scheme.
- The sparse-subset mapping is now concrete enough to write down. Combining the constructor table
  sizes, the named runtime table consumers, and the static slot-order evidence yields:
  - `eyelefthuman.tri` -> modifier slots `8..11` (`LookDown`, `LookLeft`, `LookRight`, `LookUp`)
  - `mouthhuman.tri` -> phoneme slots `0..1` (`Aah`, `BigAah`) plus expression slots `0` and `4`
    (`Anger`, `Surprise`)
  - `tonguehuman.tri` -> phoneme slots `0..1` plus expression slot `1` (`Fear`)
  - `teethlowerhuman.tri` -> phoneme slots `0..1` plus expression slots `1` and `14`
    (`Fear`, `CombatAnger`)
  - `teethupperhuman.tri` -> no recognized runtime slot names; current best fit is
    geometry/topology support data rather than named morph-slot data
- The constructor's second pass narrows that further. The `+0x80` / `0x34` family builds
  `BSFaceGenMorphDifferential` objects against those same shared tables, while the `+0x90` /
  `0x38` family builds `BSFaceGenMorphStatistical` objects against the same slots and replaces any
  previously-populated slot on collision. That means the current sparse non-hair families are now
  best read as:
  - eyes: **statistical-only** modifier subset (`0x34 = 0`, `0x38 = 4`)
  - mouth / tongue / lower teeth: **differential-only** mixed phoneme/expression subsets
    (`0x34 > 0`, `0x38 = 0`)
  - upper teeth: no recognized named morph-slot payloads
- That does not fully explain how the extended/head builder treats missing slots or empty families
  at apply time, but it retires the older uncertainty about whether sparse non-hair TRIs needed
  their own part-local naming scheme. They do not; they are now best read as partial population of
  the same global head-category tables the full head TRI uses.
- The runtime apply side also makes the missing-slot behavior much less mysterious. The constructor
  zero-initializes the per-bucket slot arrays and only populates a slot when a TRI name matches one
  of the shared table entries; `BSFaceGenMorphDataHead::ApplyMorph` then null-checks both the
  bucket pointer and the selected slot pointer before dispatching. So sparse part TRIs are not
  relying on a hidden alternate apply path for the unmapped slots: absent entries are simply
  left null and become no-op morph requests at runtime.
- The corresponding runtime interface is also now clearer. `BSFaceGenMorphDataHead::ApplyMorph`
  is a thin bucket dispatcher: it accepts a bucket id `0..3`, bounds-checks the requested morph
  index against four fixed slot ranges (`<16`, `<15`, `<17`, and `<1>` respectively), and then
  forwards to the selected morph object's virtual apply method. So the head-morph runtime shape is
  already mostly exposed: four grouped channels, with the fourth being a singleton slot.
- The next `BSFaceGenNiNode` pass resolves those four head buckets semantically. The named runtime
  callers are:
  `BSFaceGenNiNode::ApplyFacialPhonemes -> bucket 0 (16 slots)`,
  `BSFaceGenNiNode::ApplyFacialExpressions -> bucket 1 (15 slots)`,
  `BSFaceGenNiNode::ApplyFacialModifiers -> bucket 2 (17 slots)`, and
  `BSFaceGenNiNode::ApplyFacialCustomMorphs -> bucket 3 (singleton slot 0)`.
  So the bucket sizes inferred from `BSFaceGenMorphDataHead::ApplyMorph` now line up exactly with
  named runtime behavior instead of anonymous helper routing.
- The named hair constructor shows the compact case is genuinely narrower, not just “same object
  with fewer fields.” `BSFaceGenMorphDataHair` only builds a single
  `BSFaceGenMorphDifferential` from a fixed-name match against the first TRI named-record family.
  That lines up with the older GECK-side `HairMorph` filtering path and strengthens the case that
  hair uses a dedicated reduced morph path rather than the full head-morph bucket set.
- The selector bridge makes that split materially less ambiguous. `LoadModel` keeps the texture
  input separate by forwarding only the mesh/EGM/TRI inputs plus selector/flag into
  `LoadModelMesh`, then calling `LoadModelTexture` afterwards. Inside `LoadModelMesh`, the same
  selector participates in TRI record filtering before morph-data construction and then gates the
  top-level runtime morph object choice: `-1` means “extended/head-style,” while non-negative
  selectors enter the compact/hair-style branch. In practice, the compact branch is now best read
  as a caller-selected hair path, not a generic non-head path.
- A later cache-side loader pass reinforces that ownership split. `BSFaceGenModelMap` caches the
  mesh/TRI-backed `BSFaceGenModel` as the core runtime object, while `BSFaceGenModelMap::GetAt`
  only side-loads EGM and EGT afterwards by calling `BSFaceGenModel::LoadEGMData` and
  `BSFaceGenModel::LoadEGTData`. The companion call scan shows the ordinary runtime apply owners
  line up with that: `ApplyCoordinateToExistingMesh -> BSFaceGenManager::LoadEGMData ->
  BSFaceGenModelMap::LoadEGMData -> BSFaceGenModel::LoadEGMData`, and
  `ApplyCoordinateTexturingToMesh -> BSFaceGenManager::LoadEGTData ->
  BSFaceGenModelMap::LoadEGTData -> BSFaceGenModel::LoadEGTData`. The loader bodies themselves are
  narrow: `LoadModelTexture` lazily allocates the texture-side wrapper, `LoadEGTData` lazily fills
  the texture payload object at `model + 0x0C`, `LoadEGMData` lazily fills the morph payload object
  at `model + 0x08`, and `GetEGMDataSize` / `GetEGTDataSize` only compute those payload sizes for
  cache budgeting via `GetTotalDataSize` / `FreeLRUData`. So the runtime cache structure now points
  even more strongly to the packed mesh/TRI-backed model state as the canonical core, with EGM/EGT
  treated as supplementary lazy data rather than as the owning model object.
- A hair-family asset probe also supports that separation. `beardfull.tri` and `eyebrowf.tri`
  still expose many shared head-slot names (`Anger`, `BigAah`, `Fear`, `Mood*`, `Squint*`,
  and, for brows, `Blink*` / `Brow*` / `Look*`) despite belonging to the caller-selected compact
  hair path. So raw TRI identifier names alone do **not** distinguish compact/hair ownership from
  extended/non-hair ownership; the runtime selector/caller path is the stronger classifier.
- `BSFaceGenMorphDataHair::ApplyMorph` is correspondingly minimal: if its single stored morph is
  present, the requested hair-morph index is non-zero, and the weight is non-negative, it simply
  forwards to that one morph object's virtual apply method. That makes the runtime hair path look
  implementation-ready once the constructor-side name match is understood.
- The runtime hair path is also now named end-to-end. `BSFaceGenManager::ApplyHairMorph` obtains
  the active FaceGen morph data object, dynamic-casts it to the hair morph-data subtype, refreshes
  the mesh from base morph extra data if needed, and then forwards into
  `BSFaceGenMorphDataHair::ApplyMorph`. So hair morphing is not routed through the `BSFaceGenNiNode`
  head-bucket dispatcher at all; it is a separate manager-owned path.
- The remaining non-hair child invocation path is now much less ambiguous too. A focused owner scan
  of `BSFaceGenNiNode::UpdateAllChildrenMorphData` found only two named owners:
  `BSFaceGenNiNode::UpdateMorphing` and `PlayerCharacter::CloneInventory3D`. So the regular live
  runtime path for sparse TRI-backed child parts is no longer speculative: `UpdateMorphing`
  decides whether child morph updates are needed and, when they are, dispatches into the generic
  child loop in `UpdateAllChildrenMorphData`.
- That child loop is broader than “the head mesh only.” It iterates the `BSFaceGenNiNode` child
  array, resolves each child through a virtual `+0x1c` accessor, checks for morphable geometry
  (`child[0x37]`), looks up matching base-morph extra data, optionally refreshes the child mesh via
  `RefreshMeshFromBaseMorphExtraData`, and then applies the same shared bucketed morph calls to that
  child. So eyes, mouth, tongue, and lower-teeth sparse TRI subsets do not appear to need their own
  dedicated high-level owner path; they ride this same generic child loop once they have been
  materialized into `BSFaceGenMorphDataHead` slots.
- The loop also now explains the main live LOD behavior more concretely. On the broader gate it
  applies phonemes, and on the tighter gate it additionally applies expressions, modifiers, and the
  singleton custom morph. Outside that tighter gate there is still one extra fallback: a secondary
  predicate can trigger `ApplyFacialModifiers` alone without the full expression/custom path. That
  fallback is now much better bounded than before. A focused helper pass resolved the surrounding
  unnamed calls to `BSFaceGenAnimationData::UpdateEyeTracking`, `Interface::IsInMenuMode`, and
  `ProcessLists::IsActorNCloseToPlayer`. The companion call scans show that
  `UpdateEyeTracking` is only directly called from `UpdateMorphing`, while the latter two are a
  broad global UI helper and a local-player-relevance check respectively, not hidden FaceGen-
  specific subsystems. So this modifiers-only path now looks much more like upkeep for
  eye-style sparse modifier subsets than a secret expression/custom side path.
- The second owner, `PlayerCharacter::CloneInventory3D`, is now best read as a clone-side/preview
  path rather than a third live animation owner. It resolves named child nodes from the cloned
  inventory/head graph and explicitly forces `UpdateAllChildrenMorphData(..., 1)` on them, so the
  same sparse TRI-backed child semantics are reused there too.
- The broader child-update gate in `UpdateMorphing` is now also much less opaque. With the helper
  trio named, the branch reads as a runtime-relevance test: update child morphs when eye-tracking
  activity is present, when the actor is close to the player, when the actor is the player, or
  when menu mode is active. That rules out the older interpretation that this branch might have
  been hanging off a separate hidden TRI-only or renderer-only subsystem.
- A small `BSFaceGenNiNode` layout pass also ruled out the easiest remaining field guess for the
  modifiers-only fallback. `GetAnimationData` is just the pointer at `BSFaceGenNiNode + 0xD0`, and
  `GetAnimationUpdate` is only the byte flag at `+0x112`, so the child-side predicate
  `piVar5[6]->+0x10` is **not** the named animation-update member. That means the last unresolved
  predicate now appears to hang off inherited/base-class state rather than the FaceGen-owned
  `BSFaceGenNiNode` members we can name directly.
- A follow-up inherited-object pass resolved that base-class field identity much more cleanly.
  `BSFaceGenNiNode` is built on `NiNode::NiNode`, `NiObjectNET::GetController` / `SetControllers`
  still point at `+0x0C`, `NiAVObject::AttachParent` uses the parent pointer at `+0x18`, and
  `NiAVObject::SetCollisionObject` uses `+0x1C`. So `piVar5[6]` is now strongly identified as the
  inherited parent pointer, not the controller chain and not a hidden FaceGen-owned helper object.
- A final DIA-backed slot pass closed the remaining identity gap cleanly.
  `BSFaceGenNiNode::GetAnimationData` is the `vbase=0x100` slot, so the child-loop call
  `(**(code **)(*piVar5 + 0x100))(piVar5)` is now directly named. On the animation-data side,
  `BSFaceGenAnimationData::GetDead` is the `vbase=0xD4` slot and the `bDead` field lives at
  `this+0x18E`, while `UpdateEyeTracking` is a named ordinary method in the same class rather than
  that `+0xD4` virtual. On the parent side, the low-slot call matches the `NiObject::IsFadeNode`
  family slot (`vbase=0x10`), and `BSFadeNode` provides the expected override. So the remaining
  child-side fallback is no longer blocked on anonymous-slot identity at all: it now reads as a
  `GetAnimationData()` / `GetDead()` / parent `IsFadeNode()` gate around the modifiers-only child
  refresh path.
- Two direct-call follow-up scans narrowed the semantics a little further. `NiObject::IsFadeNode`
  is a very broad engine-side RTTI/family test with many unrelated owners, so the parent-side
  fade-node predicate is best treated as a coarse object-class check rather than a FaceGen-specific
  semantic signal on its own. By contrast, `BSFaceGenAnimationData::GetDead` produced no ordinary
  direct-call owners at all in the scanned image, which fits the current read that it matters here
  mainly through the virtual child-fallback branch rather than through a broad named call surface.
- A final small accessor pass narrowed the likely `BSFaceGenNiNode`-side candidates too.
  `GetFixedNormals`, `GetAnimationUpdate`, and `GetApplyRotToParent` are now confirmed as byte
  getters over `+0x111`, `+0x112`, and `+0x114` respectively. The companion direct call scan only
  found ordinary branch owners for `GetFixedNormals`, and they were unrelated debug/weapon paths
  (`DebugTextWeapon::RenderPage`, `Actor::HandlePostAnimationActions`, and
  `MiddleHighProcess::ForceWeaponDrawnSheathed`). There were no ordinary direct call hits for
  `GetAnimationUpdate` or `GetApplyRotToParent`. So if the parent-side low-vtable call ultimately
  resolves on dynamic `BSFaceGenNiNode`, `GetFixedNormals` is now the weakest candidate, while
  `GetAnimationUpdate` and `GetApplyRotToParent` remain the plausible FaceGen-side flag getters,
  with semantics still favoring `GetAnimationUpdate`.
- A later runtime name/attachment bridge pass retired another false lead. The helper at
  `0x82E213C8` is not an unnamed FaceGen-specific wrapper; it is `NiGlobalStringTable::AddString`,
  i.e. the interned-string handle builder used before runtime naming calls. `SetAVObjectName`
  is then just a thin formatting wrapper that constructs a temporary C string, interns it through
  `NiGlobalStringTable::AddString`, and forwards that handle into `NiObjectNET::SetName`.
- The other nearby anonymous helper was also a false scenegraph lead. `0x822A52C8` is
  `TES::CreateTextureImage`, not a named-node or parent-lookup bridge. Its direct call-owner set
  is broad engine-side texture infrastructure (`TES`, UI menus, water, trees, scripts, and the
  FaceGen head/eye/hair shader paths), which makes it a good fit for the resource/property setup
  around head-part assembly but a poor fit for the missing fade-node parent question.
- The generic scenegraph name lookup helpers are now anchored too:
  `NiAVObject::GetObjectByName` is the trivial self-name equality test, and
  `NiNode::GetObjectByName` is the recursive child traversal over that predicate. But the direct
  owner scan for `NiAVObject::GetObjectByName` is tiny (`NiNode::GetObjectByName` plus one
  unrelated unnamed owner), so this helper family is no longer the main suspect for where the
  runtime `BSFadeNode`-class parent is introduced.
- So this branch is cleaner now: the remaining runtime unknown is not hidden inside
  FaceGen-local naming or texture-creation helpers. It has moved outward to the higher-level
  actor/biped graph assembly path that wraps the FaceGen meshes after `CreateFaceGenHead`,
  `AttachEyesToHead`, and `AttachHairToHead` finish their local work.
- A broader owner pass then corrected one important assumption: the apparent
  `NiPointer<BSFadeNode>::operator=` evidence is not safe type evidence on its own. The same
  helper is used by `QueuedHead::Run` to store the direct outputs of `TESNPC::InitHead`, and those
  outputs are already constrained by `TESRace::CreateHead` to be the two `BSFaceGenNiNode*`
  head-part nodes. So this helper is now best treated as a COMDAT-folded generic `NiPointer`
  refcount/assignment routine whose symbol name happens to come from the `BSFadeNode`
  instantiation, not as proof that the owning fields are literally `BSFadeNode*`.
- The outer runtime owner chain is now much more concrete too. `TESRace::CreateHead` has only two
  direct owners (`TESNPC::InitHead` and `TESNPC::LinearFaceGenHeadLoad`), and the latter two now
  have named outer owners as well: `TESNPC::InitHead` is reached from `QueuedHead::Run`,
  `Script::ModifyFaceGen`, `RaceSexMenu::UpdatePlayerHead`, and `HighProcess::Update3dModel`,
  while `TESNPC::LinearFaceGenHeadLoad` is reached from `TESNPC::InitParts` and
  `TESNPC::LoadFaceGen`.
- The next anonymous helper cluster is no longer anonymous either. A PDB vicinity scan resolved
  the immediate post-head helpers to `TESNPC::AttachHead` (`0x8243F290`),
  `TESNPC::InitDefaultWorn` (`0x8243EA68`), `TESNPC::InitWorn` (`0x82441B00`),
  `TESNPC::FixDisplayedHeadParts` (`0x8243EF30`), and `TESNPC::FixNPCNormals` (`0x82444018`).
  That gives the high-level runtime sequence much better shape:
  `HighProcess::Update3dModel` now reads as `InitHead -> AttachHead -> FixNPCNormals`, while
  `TESNPC::InitParts` reads as `InitDefaultWorn -> LinearFaceGenHeadLoad -> InitWorn` plus an
  optional `FixDisplayedHeadParts`.
- `TESNPC::AttachHead` is important because it is the first named outer-stage mesh hookup after
  FaceGen node creation. It takes the newly created head/eye nodes, applies the expected
  `BSFaceGenNiNode` setup (`+0x114` / `+0x11C`, cached constants, and the `+0xDC` / `+0x128`
  model-side bridge), stores them into the owner-side slots through the generic `NiPointer`
  helper, and refreshes normals/bounds state.
- `TESRace::CreateHead` now makes the two output nodes less ambiguous. It allocates two
  `BSFaceGenNiNode` instances, returns them through the two out-pointers later passed into
  `TESNPC::AttachHead`, and initializes them asymmetrically: the primary output gets
  `SetAnimationUpdate(true)` / `SetApplyRotToParent(true)`, while the secondary output gets
  `SetAnimationUpdate(false)` / `SetApplyRotToParent(false)`. So the `param_4` / `param_5`
  split in `AttachHead` is not two identical head roots; it is a deliberate primary/secondary
  FaceGen-node pairing established by `CreateHead` itself.
- A focused `BipedAnim` pass closed the remaining local ambiguity. `0x822F8AF0` is
  `BipedAnim::GetParentBone`, so `param_3` in `TESNPC::AttachHead` is the live `BipedAnim`
  owner and the initial bridge node is fetched directly from the biped parent-bone table.
  `BipedAnim::GetParentBone(index)` is just `*(this + (index + 1) * 8)`, while
  `BipedAnim::FindBipObject` / `IsFaceGenObject` show the separate `0x10`-stride object table
  that tracks the actual biped part objects.
- The DIA type layouts now make that storage model explicit instead of inferred.
  `BipedAnim` has `root` at `+0x0`, `BIPBONE[5]` at `+0x4`, and `BIPOBJECT[0x14]` at `+0x2C`;
  `BIPBONE` is just `{ cFlags, NiNode* pParent }`, and `BIPOBJECT` is
  `{ TESForm* pParent, TESModel* pPart, NiAVObject* pPartClone, bool bSkinned }`.
  So the local bridge is now clearly operating on biped-owned `NiNode` parents plus a separate
  per-part clone table, not on an opaque FaceGen-only wrapper object.
- A follow-up DIA-backed slot pass then retired the last anonymous `AttachHead` calls:
  `+0xDC = NiNode::AttachChild`, `+0x114 = BSFaceGenNiNode::SetAnimationUpdate(bool)`,
  `+0x11C = BSFaceGenNiNode::SetApplyRotToParent(bool)`, and
  `+0x128 = BSFaceGenNiNode::FixSkinInstances(NiNode *, bool)`.
- So this local bridge is now semantically closed. `TESNPC::AttachHead` is not hiding a
  `BSFadeNode` construction step or a FaceGen-specific parent wrapper; it is taking the newly
  created `BSFaceGenNiNode` head/eye nodes, setting their local FaceGen flags, attaching them to
  biped-owned `NiNode` parents through ordinary scenegraph `AttachChild` calls, and then fixing
  skin instances against the chosen root.
- A follow-up attach-helper/owner pass retired the strongest remaining local wrapper suspicion.
  `BipedAnim::AttachToParent` and `BipedAnim::AttachToSkeleton` are owned by generic biped/model
  replacement, helmet, add-on, dismemberment, and impale paths, while the one constructor-sized
  allocation inside `BipedAnim::AttachSkinnedObject` resolves to plain `NiNode::NiNode`, not
  `BSFadeNode::BSFadeNode`. The `BSFadeNode` constructor itself is owned by generic scene/build/
  clone paths (`TESObject::LoadGraphics`, primitive builders, water, tree nodes, and the
  `BSFadeNode` factory helpers), not by the local FaceGen or biped attach bridge.
- The critical correction is that `TESNPC::AttachHead` attaches the primary FaceGen node to
  `BipedAnim::GetParentBone(param_3, 0)`, i.e. parent-bone slot `0`, not to some later wrapper.
  On shipped assets, the third-person human skeleton root
  (`meshes\\characters\\_male\\skeleton.nif`) is already a `BSFadeNode`, while the first-person
  skeleton root (`meshes\\characters\\_1stperson\\skeleton.nif`) is only a `NiNode`. That makes
  the parent-side `IsFadeNode()` test much less mysterious for the normal third-person actor path:
  the fade-class parent is very likely entering through the loaded skeleton/biped graph itself,
  not through a separate FaceGen-owned wrapper node.
- The secondary `param_5` branch is narrower now too. `AttachHead` routes that secondary node to
  `piVar4` instead of the root parent-bone slot, and `piVar4` is recovered from the existing
  owner-side object before the new attachments happen. Combined with the `CreateHead` flag split,
  that makes the remaining fallback question much more likely to be about the secondary child path
  than about the primary third-person head-root attachment.
- A focused owner-accessor pass now retires the identity question for that secondary node.
  `TESRace::CreateHead` names the primary output `BSFaceGenNiNodeBiped` and the secondary output
  `BSFaceGenNiNodeSkinned`, and the owner-side accessors match that split exactly:
  `TESObjectREFR::GetFaceNodeBiped` searches for `BSFaceGenNiNodeBiped`,
  `TESObjectREFR::GetFaceNodeSkinned` searches for `BSFaceGenNiNodeSkinned`, and the DIA-backed
  slot map is now explicit too: `GetFaceNodeBiped = +0x1A8`, `GetFaceNodeSkinned = +0x1AC`,
  `GetFaceNode = +0x1B0`, `GetFaceAnimationData = +0x1B4`, `GetCurrentBiped = +0x1E8`.
- That same accessor pass also sharpened, rather than closed, the animation-data question.
  `TESObjectREFR::GetFaceAnimationData` calls the generic virtual `GetFaceNode()` and then reads
  `BSFaceGenNiNode::GetAnimationData()` from that returned node. In the base owner accessor family,
  the `GetFaceNode()` intro thunk immediately forwards to the `+0x1AC` slot, i.e.
  `GetFaceNodeSkinned()`. So the current best read is that the base owner-side animation-data path
  defaults to the skinned node unless a class-specific override intervenes.
- A follow-up raw PPC node-name scan makes the skinned-vs-biped ownership pattern much cleaner.
  The `BSFaceGenNiNodeBiped` string is only owned by `FixedStrings::InitSDM`,
  `TESRace::CreateHead`, `TESObjectREFR::GetFaceNodeBiped`, `TESNPC::FixDisplayedHeadParts`, and
  `Actor::DismemberLimb`, while `BSFaceGenNiNodeSkinned` is additionally owned by
  `SurgeryMenu::UpdateFace` and `Actor::HideDismemberedLimb`. That makes the secondary/skinned
  branch look more like a named skinned-part maintenance path than the main animation-data root.
- A later focused owner-role pass strengthens that read with direct decompilation instead of just
  string ownership. `TESNPC::FixDisplayedHeadParts` explicitly resolves **both**
  `BSFaceGenNiNodeBiped` and `BSFaceGenNiNodeSkinned` under the same head graph and toggles their
  visibility together, while `SurgeryMenu::UpdateFace` explicitly resolves the **skinned** node
  only. The dismemberment path does the same thing from the actor side:
  `Actor::HideDismemberedLimb` operates on both named head nodes plus additional named head-part
  nodes, and `Actor::DismemberLimb` later checks cloned/detached branches for both the biped and
  skinned node names. So the secondary/skinned node is now strongly evidenced as a real
  visibility/edit/dismember maintenance branch, not just a dead naming split.
- The live NPC actor path is now clearer too. `Actor::GetFaceAnimationData` does **not** bypass the
  owner-side accessor chain; it calls the current-process virtual at `+0x144`, and
  `MiddleHighProcess::GetFaceAnimationData` then lazily caches
  `TESObjectREFR::GetFaceAnimationData(actor, 0)` into `m_pFaceAnimationData` at `+0x178`.
  So the normal actor runtime path still depends on the owner-side `GetFaceAnimationData` lookup.
- A stable direct-call scan now narrows the accessor layer too. There are ordinary direct callers
  of `Actor::GetFaceAnimationData` across the live process/update path
  (`HighProcess::Update3dModel`, `ProcessSleep`, `ProcessTravel`, `UpdateKnockState`,
  `ProcessUseFurniture`, `PlayerCharacter::Update`, and a couple of reanimate-effect owners), and
  there is the expected direct bridge from `MiddleHighProcess::GetFaceAnimationData` to
  `TESObjectREFR::GetFaceAnimationData`. There are **no** ordinary direct calls to
  `GetFaceNodeBiped` or `GetFaceNodeSkinned` in the same scan, which fits the current read that
  those are primarily virtual/name-probe helpers rather than a stable directly-called API layer.
- The process-side layout now exposes a separate split that mirrors the owner-side names but is not
  yet tied back to the attachment path. `MiddleHighProcess` has `pFaceNode` at `+0x248`,
  `pFaceNodeSkinned` at `+0x24C`, and `m_pFaceAnimationData` at `+0x178`; the getters/setters for
  all three are just plain field accessors. So the remaining runtime ambiguity is no longer whether
  the engine distinguishes primary vs skinned nodes at process scope; it clearly does. The
  remaining question is who populates those two process-side pointers, and whether the generic
  owner-side `GetFaceNode()` / `GetFaceAnimationData()` path and the process-side `pFaceNode`
  state always agree.
- The process lifecycle side now narrows that question further. `MiddleHighProcess::MiddleHighCopy`
  copies the source process fields straight across:
  `dest->pFaceNode = src->pFaceNode` and
  `dest->pFaceNodeSkinned = src->pFaceNodeSkinned` at `+0x248/+0x24C`, while the constructor
  zeros both fields and the traced `Revert` overloads clear them back to null. So the missing
  bridge is no longer “does process lifecycle preserve the split?”; it does. The unresolved point
  is the **first nonzero population path** before copy/revert, i.e. where the live actor or head
  load path initially transfers the owner-side `TESNPC` head-node pair into the process-side pair.
- The next slot-scan / owner pass now closes most of that bridge. A raw PPC scan of the process
  getter/setter vtable slots (`+0x78C/+0x790/+0x794/+0x798`) surfaces a wider owner set than the
  earlier direct-call scans, including `TESObjectREFR::Set3D`, `Script::ModifyFaceGen`,
  `RaceSexMenu::UpdatePlayerHead`, `Character::Update`, and `Character::PrecacheData`.
  Decompilation of those owners shows a split in role:
  `Set3D`, `Script::ModifyFaceGen`, and `RaceSexMenu::UpdatePlayerHead` are still mainly
  clear/rebuild sites that null the cached process nodes before a local face rebuild, but the live
  NPC path is now much clearer: `Character::Update` and `Character::PrecacheData` lazily populate
  `pFaceNode`, `pFaceNodeSkinned`, and the sibling `+0x7A0` process slot when they are null.
  They do that by:
  - resolving the actor 3D/root through `(*this + 0x1CC)()`
  - looking up three runtime-initialized keys (`uRam8328ad10`, `uRam8328ad14`, `uRam8328ad18`)
    with `func_0x829cfac8(...)`
  - materializing the found objects with `func_0x82285ff0(...)`
  - caching them through the process virtual setters `SetFaceNode`, `SetFaceSkinnedNode`, and
    the sibling `+0x7A0` setter
- `FixedStrings::InitSDM` now resolves those keys concretely. The globals are seeded as:
  - `uRam8328ad10 -> BSFaceGenNiNodeBiped`
  - `uRam8328ad14 -> BSFaceGenNiNodeSkinned`
  - `uRam8328ad18 -> HeadAnims:0`
  with the nearby fixed-string probe also showing the next literals in the same cluster
  (`EntryPoint`, `ArrowBone`, `Arrow:0`, `grabRight`). That means the lazy process-side cache is
  no longer “some unnamed runtime lookup table”; it is explicitly keyed by the named FaceGen
  biped node, the named FaceGen skinned node, and the sibling `HeadAnims:0` owner node.
- That means the “first nonzero process population” question is now mostly answered for the normal
  NPC runtime path: the process-side face-node cache is populated lazily from the live actor 3D in
  `Character::Update` / `Character::PrecacheData`, not during process copy/revert.
- The `HeadAnims:0` object itself is no longer a structural mystery. Asset-side inspection of the
  shipped third-person human skeleton now makes the branch concrete:
  - `HeadAnims` is a `NiNode`
  - `HeadAnims:0` is its child `NiTriShape`
  - the `HeadAnims` node carries a transform + visibility controller chain
  - the `HeadAnims:0` shape carries a transform + `NiGeomMorpherController` chain backed by
    `NiMorphData`
- The runtime bridge now matches that asset structure. In `Character::Update`, the cached
  `HeadAnims:0` object at process slot `+0x7A0` is used in three concrete ways:
  - a parent-side branch flag is forwarded into `BSFaceGenAnimationData::SetAnimHeadCulled(...)`
    for both the biped and skinned FaceGen animation-data objects
  - when that branch is not culled, `NiObjectNET::GetController(...)` retrieves the live
    controller from `HeadAnims:0`; the accessed fields line up with `NiGeomMorpherController::Data`
    and `NiMorphData::{Num Morphs, Morphs}`
  - the runtime then walks the named morph targets on `HeadAnims:0` and routes them into the
    correct FaceGen animation-data buckets through
    `SetAnimExpressionValue(...)`, `SetAnimModifierValue(...)`, and
    `SetAnimPhonemeValue(...)`
- The parent-side cull flag is now much less mysterious too. `NiVisController::GetTargetBoolValue`
  reads the low bit of the target node's `+0x30` flags, and that is the same flag word
  `Character::Update` reads from the parent `HeadAnims` node before forwarding the result into
  `SetAnimHeadCulled(...)`. So this branch is now best read as ordinary `HeadAnims`
  visibility/cull state, not a hidden FaceGen-only mode bit.
- The owner scans also narrow the runtime role. `SetAnimHeadCulled(...)` has only two direct calls,
  both from `Character::Update`, while the per-category setters
  `SetAnimExpressionValue(...)`, `SetAnimModifierValue(...)`, and
  `SetAnimPhonemeValue(...)` have no ordinary direct callers at all, which matches the existing
  decompile where `Character::Update` reaches them through the animation-data virtual interface
  rather than via direct calls.
- A negative cross-check also now favors the same interpretation: the core static head
  build/apply artifacts (`CreateNewMesh`, `LoadModelMesh`, `ApplyCoordinateToExistingMesh`,
  the coord-consumer bridge, and the head-part attach path) do not reference
  `GetFaceAnimationData`, `HeadAnims:0`, or the `SetAnim*Value` methods in the current named
  decompile set.
- So the `+0x7A0` process slot is now best read as the cached skeleton-side facial animation
  source branch, not another hidden FaceGen owner object and probably not a static head-build
  dependency. The remaining uncertainty is narrower still: whether this `HeadAnims:0` bridge is
  parity-critical for our static NPC renderer at all, or mostly relevant to live facial animation
  playback on already-built heads.
- The runtime eye path also needs to be kept conceptually separate from that local secondary node.
  `BSFaceGenManager::AttachEyesToHead` builds left/right eye meshes as normal `BSFaceGenModel`
  outputs and attaches them through its own eye-part assembly path; it does not obviously reuse
  the `TESRace::CreateHead` secondary node as the eye mesh itself. So the remaining local runtime
  question is now precise: whether the `param_5` secondary `BSFaceGenNiNodeSkinned` in
  `AttachHead` directly participates in the modifiers-only `UpdateMorphing` fallback, or whether
  that fallback mostly operates on the primary/process `pFaceNode` child meshes while the skinned
  node stays a parallel maintenance/visibility branch.
- That retires most of the old “where is the fade parent introduced?” question for the standard
  third-person human head path. The remaining uncertainty is narrower: whether all relevant
  runtime variants (preview paths, alternate skeletons, first-person paths, and the secondary
  `param_5` attachment branch) preserve that same `BSFadeNode`-rooted parentage, and how that
  maps onto the modifiers-only child fallback in `BSFaceGenNiNode::UpdateMorphing`.
- A focused head-part attachment pass (`facegen_head_part_attach_decompiled_pdb_xenon.txt`) now
  makes the runtime eye/hair assembly branch more concrete. `BSFaceGenManager::AttachHairToHead`
  is not a pure material helper: it resolves or builds a cached `BSFaceGenModel`, materializes a
  mesh through `BSFaceGenModel::ApplyCoordinateToNewMesh`, then applies the dedicated hair morph
  path, resolves a named hair node with `GetHairName`, and only afterwards layers on optional
  shader/alpha-property setup.
- The supporting helpers are narrower than they first looked. `GetHairName` just builds a
  `FaceGenHair...` node name from a tiny selector family, while `GetHairTeethAlphaProperty`
  simply returns the manager-owned cached alpha property at `BSFaceGenManager + 0xDB0`. So the
  interesting part of the hair path is the mesh/morph bridge, not those naming/property helpers.
- `BSFaceGenManager::AttachEyesToHead` follows the same broad pattern in two mirrored eye-part
  branches. Each branch resolves or builds a cached `BSFaceGenModel`, calls
  `BSFaceGenModel::ApplyCoordinateToNewMesh` to materialize the eye mesh from the active FaceGen
  coordinate state, and only then proceeds into eye-specific resource/property setup before the
  final attach.
- That makes the runtime classification cleaner: eye/hair attachment is still downstream of the
  same FaceGen coord state, but it belongs to **runtime head-part assembly / render parity**, not
  to the raw `_0` facemod texture-generation path.
- `BSFaceGenNiNode::UpdateAllChildrenMorphData` also exposes the runtime LOD split. It computes two
  distance thresholds and, based on those, always applies phonemes on the wider gate and only
  applies expressions, modifiers, and custom morphs on the tighter gate. This is an inference from
  the control flow, but it fits the named calls exactly:
  `ApplyFacialPhonemes` runs when the broader `bVar2` gate is open, while
  `ApplyFacialExpressions`, `ApplyFacialModifiers`, and `ApplyFacialCustomMorphs` only run inside
  the narrower `bVar3` branch.
- The next builder layer down is now also simpler than it looked. The subhelpers used by those
  builders are plain typed payload wrappers, not hidden decode stages:
  `BSFaceGenMorphDifferential::BSFaceGenMorphDifferential` stores a count and allocates
  `count * 0x0C` bytes, while `BSFaceGenMorphStatistical::BSFaceGenMorphStatistical` stores a
  count plus one extra scalar and allocates `count * 0x04` bytes. In other words, the
  runtime-side `0x2C`/`0x30` record builders are wrapping already-materialized `float3[]` and
  `uint32[]` payloads, not reinterpreting raw TRI bytes.
- One caution from the raw image dump: the global table addresses used by the head constructor
  (`0x832451d0`, `0x83245210`, `0x83245258`, and the singleton at `0x8324520c`) are zeroed in the
  imported MemDebug file image, and a direct xref scan did not recover their initializer. So the
  exact string/category contents of those runtime group tables are still unresolved. What is now
  established is the class boundary around them, not the final table contents.
- The raw MemDebug EXE still exposes a contiguous FaceGen name pool in file order at
  `0x82066198..0x82066330`: custom `VampireMorph`; phoneme/viseme labels
  `Th`, `OohQ`, `Oh`, `FV`, `Eh`, `Eee`, `DST`, `ChJSh`, `BMP`, `BigAah`, `Aah`;
  modifier labels `HeadYaw`, `HeadRoll`, `HeadPitch`, `SquintRight`, `SquintLeft`, `LookUp`,
  `LookRight`, `LookLeft`, `LookDown`, `BrowUpRight`, `BrowUpLeft`, `BrowInRight`,
  `BrowInLeft`, `BrowDownRight`, `BrowDownLeft`, `BlinkRight`, `BlinkLeft`; expression-like
  labels `CombatAnger`, `MoodSad`, `MoodAngry`, `MoodPleasant`, `MoodDrugged`, `MoodCocky`,
  `MoodAnnoyed`, `MoodAfraid`, and `MoodNeutral`; plus category labels `Modifier`,
  `Expression`, and `Phoneme`. This comes from the raw file string pool, not from a recovered
  runtime table initializer.
- The animation-side consumers now confirm the live category sizes independently of the TRI
  constructor path: `AddPhonemeKeyframe` materializes a `16`-slot phoneme buffer,
  `AddModifiersKeyframe` materializes a `17`-slot modifier buffer,
  `SetExpressionTarget*` materializes `15` expression slots, and `AddCustomMorphKeyframe`
  materializes a singleton custom-morph buffer. So the bucket sizes seen in
  `BSFaceGenMorphDataHead::ApplyMorph` line up with the higher-level animation code too.
- The important caveat is that animation indices are **not** a naive copy of the raw string-pool
  order. `BSFaceGenAnimationData::GetModifierValuesAsRotation` reads the last three modifier
  slots (`14..16`) as the head-rotation triple, which would not line up with the raw
  `HeadYaw` / `HeadRoll` / `HeadPitch` string run if that run were already the live slot order.
  So there is still a remap or initializer layer between the raw string pool and the live
  runtime index layout.
- A raw-image pass finally surfaced one concrete piece of that bridge layer. The MemDebug image
  contains a static pointer cluster at `0x832327c0..0x83232890`; while the expression and phoneme
  portions are still only partially understood, the 17-entry modifier sub-run at
  `0x83232810..0x83232850` is clean and exactly matches the live modifier count:
  `BlinkLeft`, `BlinkRight`, `BrowDownLeft`, `BrowDownRight`, `BrowInLeft`, `BrowInRight`,
  `BrowUpLeft`, `BrowUpRight`, `LookDown`, `LookLeft`, `LookRight`, `LookUp`,
  `SquintLeft`, `SquintRight`, `HeadPitch`, `HeadRoll`, `HeadYaw`.
  So the modifier side is no longer just “some remap exists”; it now has a concrete static order
  even though the live slot-table globals at `0x832451d0..0x83245258` are still zero in the raw
  imported image. The named table consumers above also now make the category ownership concrete:
  `0x832451d0 = HeadTable_Expressions`, `0x83245210 = HeadTable_Modifiers`, and
  `0x83245258 = HeadTable_Phonemes`.
- The expression side shows the same mismatch. `BSFaceGenAnimationData::SetExpressionTargetFromMood`
  writes mood selections into expression slots `5..12` for mood ids `0..7`, so the raw
  `Mood*` labels appear to occupy the middle of the `15`-slot expression space rather than the
  first indices. That leaves several expression slots still unnamed from the currently-audited
  data.
- The static cluster now makes that expression layout much more concrete. If the 15-slot
  expression run is read from `0x832327d0..0x83232808`, then the mood block lands exactly where
  `SetExpressionTargetFromMood` says it should: slots `5..12` become
  `MoodNeutral`, `MoodAfraid`, `MoodAnnoyed`, `MoodCocky`, `MoodDrugged`, `MoodPleasant`,
  `MoodAngry`, and `MoodSad`. That leaves the surrounding slots as
  `Anger`, `Fear`, `Happy`, `Sad`, `Surprise` before the moods, and `Pained`, `CombatAnger`
  after them. This is still an inference from the static cluster plus the decompiled setter, not a
  recovered live-table initializer, but it is much tighter than the earlier “middle of the space”
  conclusion.
- The custom side is now the strongest concrete match: the animation path uses category `3`
  with count `1`, and the only clearly FaceGen-specific singleton name surfaced in the raw pool
  is `VampireMorph`. So bucket `3`, slot `0` is now strongly indicated to be the vampire morph.
- The static cluster sharpens that too: `Custom` sits immediately before the inferred expression
  run and `VampireMorph` sits immediately after it at `0x8323280c`, which fits a standalone
  singleton custom-morph slot much better than it fits the 15-slot expression run.
- The phoneme side remains only partially named. The raw pool exposes `11` FaceGen viseme labels,
  but both the runtime head bucket and the animation keyframe path still treat phonemes as
  `16`-slot arrays. The current best read is that there are additional unnamed or currently
  unresolved phoneme slots beyond the visible FaceGen viseme string run.
- The static cluster also narrows the phoneme side. A 16-entry run at `0x83232858..0x83232894`
  lines up cleanly with the known runtime phoneme count and yields:
  `Aah`, `BigAah`, `BMP`, `ChJSh`, `DST`, `Eee`, `Eh`, `FV`, `I`, `K`, `N`, `Oh`, `OohQ`,
  `R`, `Th`, `W`. So the remaining uncertainty there is no longer “do we have any plausible
  16-slot ordering?” but rather “what do the extra one-letter phoneme labels mean semantically,
  and does the runtime initializer permute this static order further?”
- A focused pass on `BSFaceGenAnimationData` state layout makes the runtime buffer topology much
  clearer and, importantly, does **not** reveal a hidden name-remap table there. The constructor
  simply instantiates repeated `BSFaceGenKeyframeMultiple` buffers with category/count pairs:
  category `1` with count `15`, category `2` with count `17`, category `0` with count `16`,
  and category `3` with count `1`. The small `BSFaceGenKeyframeMultiple` constructor only stores
  that category id and quantity and allocates a float array; it does not populate or consult any
  name table.
- The `BSFaceGenAnimationData::Update` path also gives the first concrete state-field grouping:
  `+4` is the expression target-transition buffer, `+9` is the animation-driven expression buffer,
  and `+1d` is the combined expression buffer. The speech/keyframe-list side then runs in three
  parallel tracks: list head `+22` updates speech modifiers into `+25`, list head `+2f` updates
  speech phonemes into `+32`, and list head `+3c` updates speech custom morphs into `+3f`.
  Each of those is then merged with the corresponding animation-driven buffer
  (`+0e`, `+13`, `+18`) into combined runtime buffers (`+2a`, `+37`, `+44`).
- That is useful for parity because it means the runtime animation layer is now mostly exposed as
  state topology, not just anonymous vtable traffic. It also narrows the missing remap question:
  if a name-to-index remap exists, it is likely **upstream** of these `BSFaceGenKeyframeMultiple`
  buffers, not hidden inside their constructor, update, or merge logic.
- A focused eye/head tracking pass now makes the modifier-side layout more concrete. The shipped
  tracking globals are `fTrackEyeXY = 28.0`, `fTrackEyeZ = 20.0`, `fTrackDeadZoneXY = 20.0`, and
  `fTrackDeadZoneZ = 10.0` degrees (`0.488692`, `0.349066`, `0.349066`, `0.174533` radians in the
  raw image). `GetTrackEyeXYMinMax` / `GetTrackEyeZMinMax` clamp those globals into signed
  `[-max,+max]` ranges, and the dead-zone helpers clamp to the smaller of the dead-zone scalar and
  the corresponding eye max. So the runtime eye-tracking limits are no longer anonymous globals.
- `BSFaceGenAnimationData::SetEyePosition` also makes the eye-look modifier group concrete. It
  writes four sign-paired modifier slots: horizontal eye look uses slots `9` and `10`, while
  vertical eye look uses slots `8` and `11`. The code zeroes one slot in each pair and writes a
  normalized ratio into the opposite slot depending on the sign of the requested eye position.
  Combined with the static modifier-name run above, that resolves the sign mapping:
  negative horizontal writes `LookLeft` (slot `9`), positive horizontal writes `LookRight`
  (slot `10`), negative vertical writes `LookDown` (slot `8`), and positive vertical writes
  `LookUp` (slot `11`).
- The head-tracking path is separate from those eye-look modifier writes. `SetHeadTrackVector` and
  `ClearHeadTracking` manage a dedicated vector at `+0x124/+0x128/+0x12C` plus state flags around
  `+0x174`, `+0x178`, `+0x188`, and `+0x189`; they do not directly write the modifier array.
  `GetModifierValuesAsRotation` remains the only confirmed direct consumer of the final three
  modifier slots (`14..16`), which it treats as the head-rotation triple before passing them into
  `NiQuaternion::FromEulerAnglesXYZ`. Combined with the static modifier-name run, that means
  slot `14 = HeadPitch -> Euler Z`, slot `15 = HeadRoll -> Euler Y`, and
  slot `16 = HeadYaw -> Euler X`. So the live modifier layout is now concretely split into
  eye-look (`8..11`) and head-rotation (`14..16`) subfamilies, even though the exact higher-level
  runtime initializer that seeds the live tables is still unresolved.
- A focused pass on the manager bootstrap now narrows that initializer question substantially.
  `BSFaceGenManager::BSFaceGenManager` is not just copying a built-in name table into the live
  globals; it loads `FACEGEN\SI.CTL` through the anonymous helper at `0x8293ffd0`, and that helper
  instantiates a dedicated `FRCTL001` reader. The startup sequence is:
  `std::string("FACEGEN\\SI.CTL") -> FrctlReaderCtor -> FrctlReaderOpen -> FrctlHeaderFixup ->
  FrctlDecodePrimarySections -> FrctlDecodeSecondarySections`.
- `FrctlHeaderFixup` endian-fixes a small header consisting of two leading scalars plus a `2x2`
  scalar block. `FrctlDecodePrimarySections` then uses that `2x2` block to populate the manager's
  primary control arrays at `+0x88`: for each `2x2` bucket, it reads a variable-length array of
  `0x34`-stride records, then for each record reads a `count`-sized `uint32` payload and a second
  byte payload rooted at `record + 0x18`. `BSFaceGenManager::GetMaxControlID` queries that same
  primary storage and returns `((end - begin) / 0x34) - 1`, so the live per-bucket control counts
  are now clearly CTL-file-driven.
- `FrctlDecodeSecondarySections` is separate and larger. It populates the `FanGenCtlsGrpC`
  structure rather than the manager's `+0x88` table, and it does so in three phases:
  a `5 x 2 x 2` family at `+0x25c`, a cross-family `5 x 2` block at `+0x4dc`, and derived
  normalized/aggregate structures at `+0xa54` and `+0xc34`. The decompiled loops show repeated
  count-sized `uint32` reads, square-matrix reads sized by the earlier `2x2` header block, and
  post-load normalization helpers over those arrays. So the CTL path is materially richer than a
  simple static name remap.
- The practical implication is that the static FaceGen name cluster is probably **not** the primary
  runtime initializer for the live control tables. It still looks useful for labeling or remap
  semantics, but the runtime control counts and control-group storage are now clearly coming from
  the shipped `FRCTL001` control file path instead of from a straight static copy into
  `0x832451d0..0x83245258`.
- The RaceSexMenu control path now makes those CTL-backed buckets much less abstract.
  `BSFaceGenManager::GetFaceCoordValue`, `SetFaceCoordValue`, `GetFaceCoordAttribute`,
  `SetFaceCoordAttribute`, and `RandomizeFaceGenetic` all operate on the same manager object that
  `FRCTL001` initializes. `RaceSexMenu::OnFaceSliderChange` only accepts slider types `0x22` and
  `0x23`, and it writes them through `SetFaceCoordValue(selector, 0, controlIndex)` where:
  - slider type `0x22` -> selector `(0, 0)`
  - slider type `0x23` -> selector `(1, 0)`
- `RaceSexMenu::AddFaceSliders` then shows how the UI uses those two selector buckets:
  - the broad slider page is selector `(0, 0)`: the code calls `GetMaxControlID(0,0)` and loops
    over that count to add a generic `0x22` slider for each exposed control
  - selector `(1, 0)` is **not** enumerated generically: instead the menu hardcodes a smaller
    `0x23` subset and binds concrete labels to specific control indices
- The raw `.data` globals feeding `RaceSexMenu::AddFaceSliders` now resolve that selector `(1, 0)`
  subset to stable UI strings:
  - `0x1D` -> `Shade`
  - `0x1C` -> `Flushed`
  - `0x1E` -> `Blue Tint`
  - `0x1F` -> `Yellow Tint`
  - `0x08` -> `Eye Sockets`
  - `0x09` -> `Eyebrows`
  - `0x12` -> `Eyeliner`
  - `0x1B` -> `Nose`
  - `0x16` -> `Lips`
  - `0x05` -> `Moustache`
  - `0x06` -> `Cheeks`
  - `0x03` -> `Beard`
- That split is a strong semantic correction to the earlier investigation. The FRCTL primary
  `2 x 2` control tables are now best understood as the **editor face-coordinate system**, with
  selector `(0,0)` holding the broad geometric face-shape sliders and selector `(1,0)` holding a
  curated appearance/tint/facial-feature subset. This is materially different from the live
  runtime morph-category tables (`phoneme` / `expression` / `modifier` / `custom`).
- The broad `(0,0)` side is now partially reconstructed too. The first 20 stack records that
  `RaceSexMenu::AddFaceSliders` feeds into the generic `0x22` slider loop resolve to these active
  help strings:
  - `0x00` -> `High/Low`
  - `0x01` -> `Up/Down (inner)`
  - `0x02` -> `Up/Down (outer)`
  - `0x03` -> `Low/High`
  - `0x04` -> `Shallow/Pronounced`
  - `0x06` -> `Concave/Convex`
  - `0x08` -> `Forward/Backward`
  - `0x09` -> `Broad/Recessed`
  - `0x0B` -> `Shallow/Deep`
  - `0x0D` -> `Tall/Short`
  - `0x0F` -> `Down/Up`
  - `0x10` -> `Small/Large`
  - `0x12` -> `Together/Apart`
- A later correction closed the bucket-name gap. The apparent `func_0x82eac940()` /
  `func_0x82eadc78()` calls at the start of `RaceSexMenu::AddFaceSliders` are just PPC save
  thunks (`__savegprlr` / `__savefpr`), not real FaceGen helper functions. The generic bucket
  source is the `RaceSexMenu` object itself: the loop indexes `this[(bucket + 10)]`, and
  `RaceSexMenu::_Create` seeds those submenu objects from a fixed 20-entry title table.
- That `_Create` title table is now resolved from the raw `.data` globals:
  - `this + 0x4C` -> `Shape`
  - `this + 0x50` -> `General`
  - `this + 0x54` -> `Forehead`
  - `this + 0x58` -> `Brow`
  - `this + 0x5C` -> `Eyes`
  - `this + 0x60` -> `Nose`
  - `this + 0x64` -> `Mouth`
  - `this + 0x68` -> `Cheeks`
  - `this + 0x6C` -> `Jaw`
  - `this + 0x70` -> `Chin`
  - `this + 0x74` -> `Tone`
- A fuller reconstruction of the authored stack-record table corrected the early partial list too:
  the first active control is actually `0x00 -> High/Low`. The earlier provisional list had
  started one slot late.
- Because `AddFaceSliders` indexes `this[(bucket + 10)]`, the generic `(0,0)` buckets now map
  linearly and concretely:
  - bucket `0x0A` -> `General`
  - bucket `0x0B` -> `Forehead`
  - bucket `0x0C` -> `Brow`
  - bucket `0x0D` -> `Eyes`
  - bucket `0x0E` -> `Nose`
  - bucket `0x0F` -> `Mouth`
  - bucket `0x10` -> `Cheeks`
  - bucket `0x11` -> `Jaw`
  - bucket `0x12` -> `Chin`
- The current active `(0,0)` control map is:
  - `General`:
    `0x15 Heavy/Light`, `0x17 Thin/Wide`
  - `Forehead`:
    `0x18 Small/Large`, `0x19 Tall/Short`, `0x1A Tilt Forward/Back`
  - `Brow`:
    `0x00 High/Low`, `0x01 Up/Down (inner)`, `0x02 Up/Down (outer)`
  - `Eyes`:
    `0x0F Down/Up`, `0x10 Small/Large`, `0x12 Together/Apart`
  - `Nose`:
    `0x29 Bridge Depth`, `0x2B Down/Up`, `0x2C Flat/Pointed`, `0x2D Nostril Tilt`,
    `0x2F Nostrils Wide/Thin`, `0x31 Sellion Height`, `0x35 Short/Long`
  - `Mouth`:
    `0x23 Large/Small`, `0x27 Underbite/Overbite`
  - `Cheeks`:
    `0x03 Low/High`, `0x04 Shallow/Pronounced`, `0x06 Concave/Convex`
  - `Jaw`:
    `0x1B Retracted/Jutting`, `0x1C Wide/Thin`, `0x1D Slope High/Low`, `0x1E Concave/Convex`
  - `Chin`:
    `0x08 Forward/Backward`, `0x09 Broad/Recessed`, `0x0B Shallow/Deep`, `0x0D Tall/Short`
- Selector `(1,0)` is also slightly clearer in structure now: the hardcoded tint/feature subset is
  attached under the `Tone` submenu at `this + 0x74`, not through a separate anonymous bucket
  table.
- Placeholder bucket `0x14` is the inactive authored slot bucket. It appears repeatedly in the
  authored record table but is skipped by the live menu because those rows carry null help
  pointers.
- Range semantics are firmer now than in the first pass. Most active geometric controls use
  `[-5.0, +5.0]`; `General` uses `[-2.0, +2.0]`; and two `Nose` controls (`Sellion Height`,
  `Short/Long`) use `[-5.0, +3.0]`.
- The attribute-table path rooted at manager `+0xC8` is also now concretely exercised by the menu.
  `RaceSexMenu::SliderRelease` handles a separate slider type `0x1A` by converting the UI value
  into a `15..65` range, reading attribute pair `(0,0)` / `(0,1)`, recentring that pair around
  the requested value, writing it back with `SetFaceCoordAttribute`, and then rebuilding the face
  through `OffsetFaceGenCoord`. So the attribute path is not just randomization scaffolding; it is
  an actively edited face-coordinate parameter family. It is strongly age-like, although the exact
  shipped label for this slider was not recovered in this pass.
- A focused persistence pass (`facegen_persistence_decompiled_pdb_xenon.txt`) now makes the save
  layout concrete. `TESNPC::GetFaceGenSaveSize` computes the serialized payload as the sum of four
  float-array products:
  `(+0x154 * +0x158) + (+0x16C * +0x170) + (+0x184 * +0x188) + (+0x19C * +0x1A0)`, all `* 4`,
  then `+ 0x15`. `TESNPC::SaveFaceGen` and `TESNPC::LoadFaceGen` walk those same four buckets as
  `2 x 2` generic `6`-word descriptors rooted from NPC fallback storage at `+0x144`, with the
  dimension pair for each bucket sitting at the last two words of each descriptor.
- `TESNPC::SaveFaceGen` is therefore **not** serializing the visible RaceSexMenu pages directly.
  It writes:
  - the four derived float buckets described above
  - three selected face-data refs/IDs rooted at `+0x120`, `+0x1A8`, and `+0x1B0`
  - one float at `+0x1AC`
  - one integer at `+0x1C8`
  - one face flag byte from `func_0x8242dc08`
- The manager-side value path now lines up with those four saved buckets. `BSFaceGenManager::
  GetFaceCoordValue` / `SetFaceCoordValue` index a `2 x 2` selector grid with
  `group = selector0 * 2 + selector1`, query the CTL-loaded `0x34` control records for that
  group, and then resolve the live value storage from `coordBase + group * 0x18`. That is the
  first clean bridge between the CTL primary buckets and the generic `2 x 2` save/load loops.
- The attribute path is separate. `BSFaceGenManager::GetFaceCoordAttribute`,
  `SetFaceCoordAttribute`, and `RandomizeFaceGenetic` all operate on helper storage rooted at
  `coordBase + 0xC8` through `0x82941288` / `0x82941608` / `0x82942780`. So the age-like
  attribute slider family is not the same storage path as the four serialized value buckets, even
  though it feeds into them later through rebuild logic.
- `TESNPC::RandomizeFaceCoord` now ties the two paths together. It:
  - creates a temporary FaceGen coord struct with `InitFaceGenCoord`
  - randomizes or edits the separate attribute helper
  - randomizes the extra float at NPC `+0x1AC`
  - calls `OffsetFaceGenCoord` to apply the race/base FaceGen data against those inputs
  - compares the resulting temporary coord struct against either external NPC face-coord storage
    at `+0x1A4` or the NPC fallback struct at `+0x144`
  - copies the rebuilt coord struct back only if it changed
- A focused coord-rebuild pass (`facegen_coord_rebuild_decompiled_pdb_xenon.txt`) now makes the
  helper naming explicit. `TESRace::GetFaceGenData` seeds the outgoing coord struct by calling
  `CopyFaceGenCoord` on the race default block at `+0x468` or `+0x408`, then fills the adjacent
  ref/scalar fields. That means the earlier anonymous `0x824899c8` helper is now resolved as the
  race/base coord materializer, not just a generic blob copy.
- `CompareFaceGenCoord` is the persistence gate after rebuild. It walks the same four `0x18`
  buckets, requires matching dimensions, and then performs a bytewise payload compare over the
  float arrays. `TESNPC::RandomizeFaceCoord` treats a nonzero result as "coord changed" and only
  then copies the rebuilt struct back into NPC storage.
- The raw helper layer under those named functions is narrower now too:
  - `func_0x82489380` is a single-bucket deep copy helper: copy dims, allocate payload, memcpy the
    float array.
  - `func_0x824892a8` is an append-one-float helper with overlap-safe behavior, used by both
    `MergeFaceGenCoord` and `OffsetFaceGenCoord` when they materialize new payload arrays
    element-by-element.
- `OffsetFaceGenCoord` is now confirmed as the subtractive rebuild path over the same four
  buckets. In the normal path it allocates each destination bucket and writes
  `currentCoord - baseCoord`; for selector family `1` with the extra flag set, it takes a direct
  copy fast path through `func_0x82489380`.
- `MergeFaceGenCoord` is the additive sibling. It walks the same four buckets and usually writes
  `lhs + rhs`; on selector family `1` with the extra flag set it also takes the direct-copy fast
  path, and when the caller supplies a positive bound it applies the same post-pass magnitude clamp
  that earlier anonymous decomp snippets had hinted at.
- That is the current best explanation for persistence semantics: the saved four float matrices are
  **derived FaceGen coord values**, not raw UI slider state. The menu edits both direct value
  buckets and a separate attribute helper, and the engine materializes the saved coord struct from
  those pieces.
- A second focused downstream-consumer pass
  (`facegen_coord_consumer_bridge_decompiled_pdb_xenon.txt`) now closes the bridge from those
  derived coord structs into runtime mesh/texture application.
- `BSFaceGenManager::GetFaceGenCoord` is the shared live accessor for that derived coord state. It
  lazily initializes the global manager if needed and then returns `manager + 8`, so the active
  FaceGen coord blob is not a transient editor object.
- `BSFaceGenManager::FixExclusiveValues` is now better characterized too. It does not preserve a
  single signed value; it splits an opposing slider pair into two nonnegative outputs by computing
  `rhs - lhs` and then routing the positive side into one slot and the negative side into the
  other.
- `BSFaceGenManager::ScaleFaceCoord` is a bounded post-pass over the same four `2 x 2` coord
  buckets. When the caller supplies `0.0 <= scale <= 1.0`, it multiplies the in-place float
  payloads for each eligible bucket, with the selector-family `1` buckets still gated by the
  extra flag.
- `BSFaceGenManager::GetFaceGenModel` and `BSFaceGenManager::CreateFaceGenHead` now look like the
  runtime assembly bridge above the apply routines. `GetFaceGenModel` ensures the model cache at
  manager `+0xDAC`, tries to reuse an existing model if possible, and caches newly built models;
  `CreateFaceGenHead` repeatedly resolves that bridge while iterating the head/race component
  families.
- The cache-side loader bridge now makes the cache ownership model more explicit too.
  `BSFaceGenModelMap::GetAt` returns an already-cached `BSFaceGenModel` and then lazily calls
  `LoadEGMData` / `LoadEGTData`, while `VerifyData`, `GetTotalDataSize`, and `FreeLRUData` only
  budget those supplementary payload objects rather than rebuilding the underlying mesh/TRI state.
  So for runtime parity purposes, the cached `BSFaceGenModel` itself still looks like the primary
  mesh/TRI-backed object, with EGM/EGT loaded on demand around it.
- Most importantly, the rebuilt coord blob is **not** just persisted editor state. Both
  `BSFaceGenModel::ApplyCoordinateTexturingToMesh` and
  `BSFaceGenModel::ApplyCoordinateToExistingMesh` explicitly fall back to
  `BSFaceGenManager::GetFaceGenCoord()` when the caller does not supply a coord pointer.
- In the texture path, those coord buckets are then dimension-checked against the loaded texture
  morph descriptors and consumed as weights during accumulation into the temporary signed-int image
  buffer before the final image object is created.
- In the mesh path, the same coord buckets are dimension-checked against the loaded morph records
  and then consumed as weights while morph deltas are accumulated back into vertex XYZ storage.
- The named head-part attach pass extends that conclusion one step farther downstream.
  `BSFaceGenModel::ApplyCoordinateToNewMesh` is now exposed as a thin wrapper that first creates a
  new mesh object and then forwards into `ApplyCoordinateToExistingMesh(..., newMesh = 1)`. The
  runtime eye and hair attachment helpers both use that wrapper before attaching those child parts
  to the assembled head.
- A dedicated `CreateNewMesh` pass narrows that wrapper further. `BSFaceGenModel::CreateNewMesh`
  is not another coord/morph stage; it is the runtime mesh-construction step beneath the wrapper.
  It validates that the loaded source mesh at `BSFaceGenModel + 0x08 -> +0x10 -> +0xDC` exists,
  allocates a fresh mesh object, copies/adopts the loaded source mesh through a virtual call at
  the new object's `+0xE4` slot, mirrors an existing nested ref under the new mesh's `+0xE0`
  branch when present, and then attaches two additional heap objects via `func_0x824965e0(...)`
  and `func_0x8248FDC8(...)`.
- Those two helper callees are no longer anonymous. `0x824965E0` is
  `BSFaceGenBaseMorphExtraData::BSFaceGenBaseMorphExtraData`, and `0x8248FDC8` is
  `BSFaceGenModelExtraData::BSFaceGenModelExtraData`.
- The second helper is the simpler one: `BSFaceGenModelExtraData` just stores a ref-counted pointer
  to the source model-side object. So that attachment is a straight model-reference bridge, not a
  hidden morph operation.
- `BSFaceGenBaseMorphExtraData` is the more important attachment. It builds the runtime
  base-morph extra-data object from the source mesh/model data rooted at `param_2 + 0xDC`, copies
  or expands a `float3` vector payload into owned storage, optionally appends extra vectors when
  the caller supplies the `(param_3, param_4)` pair, and then returns that fully materialized
  base-morph object for attachment to the newly created mesh.
- A dedicated method-family pass makes that object less opaque. `CreateMorphModelDataClone`
  (`0x824950B0`) is **not** the `float3` payload builder; it just clones/adopts the model-data ref
  from `source + 0xDC` into the extra-data object's `+0x10` field. The owned morph-vector payload
  is built separately by the constructor in the longer `0x824965E0` path.
- The same pass also clarifies the supporting runtime lifecycle:
  `GetVertexCount(flag)` returns either the base count at `+0x14` or alternate count at `+0x18`;
  `InitSDM` / `KillSDM` manage a small global singleton at `puRam8328f644`; and
  `ResetAllMorphExtraData` either refreshes the current node's clone from that singleton or
  recursively walks child objects before doing so.
- A follow-up boundary pass resolves the last four anonymous helpers almost entirely into generic
  Gamebryo/Xbox runtime APIs. The `source + 0xDC` accessor trio
  `0x82E39080 / 0x82E391F8 / 0x82E39150` is
  `NiGeometryData::LockPackedVertexData`, `NiGeometryData::GetVerticesIterator`, and
  `NiGeometryData::UnlockPackedVertexData`.
- That accessor layer is now much less mysterious. `GetVerticesIterator` returns a `(ptr, stride,
  packedFlag)` view of the current vertex stream, and when no packed stream is exposed it falls
  back directly to `NiGeometryData + 0x20` with count from `+0x08` and stride `0x0C`. So the
  constructor is consuming ordinary geometry-data vertex storage, not a hidden FaceGen-only buffer
  format.
- The alternate packed branch also resolves cleanly: `0x82481AD8` is `XMConvertHalfToFloatStream`.
  So the constructor's packed/unpacked split is just "read `float3` directly" versus "expand
  half-float vertex data into temporary `float3` values", not a second custom FaceGen decode path.
- The refresh/matcher side is similarly generic. `0x82E26088` is `NiObjectNET::GetExtraData`,
  which performs a keyed lookup over the object's attached extra-data array, and `0x82E27AC8` is
  `NiObject::CreateDeepCopy`. So `ResetAllMorphExtraData` is fetching an attached extra-data
  object and cloning it, not calling a hidden FaceGen-specific matcher/clone layer.
- A second geometry-bridge pass narrows the owning object further. The mesh-side helper under
  `0x82E278F8` is `NiObject::Clone`, `0x82E25E90` is `NiObjectNET::AddExtraData`,
  `0x82E37868` is `NiGeometryData::SetConsistency`, `0x82E37900` is
  `NiGeometryData::MarkAsChanged`, and `0x82E22578` is `NiAVObject::GetProperty`. So the
  surrounding bridge under `CreateNewMesh` / `ApplyCoordinateToExistingMesh` is standard
  Gamebryo mesh/object plumbing, not a FaceGen-only object family.
- That geometry-bridge pass also makes the field interpretation stronger. Our local runtime notes in
  `RuntimeSceneGraphWalker` already document `NiGeometry.m_spModelData` at offset `+220`
  (`0xDC`), and the packed/runtime decomp now shows that exact field being fed into
  `NiGeometryData::{LockPackedVertexData,GetVerticesIterator,SetConsistency,MarkAsChanged}`. So
  `source + 0xDC` is now very strongly identified as the owning mesh's `NiGeometryData` pointer.
- The adjacent `source + 0xE0` field is not named directly by PDB in this pass, but the remaining
  evidence points in one direction. The Bethesda/Gamebryo `NiGeometry` schema places
  `Skin Instance` immediately after `Data`, which matches the runtime `0xDC` / `0xE0` pairing,
  and the skinning path reuses the same pair while cloning `*object->0xE0` through `NiObject::Clone`
  before continuing with skinned-object attachment. So the strongest current interpretation is that
  `source + 0xE0` is the mesh's `NiSkinInstance`-family slot (possibly `BSDismemberSkinInstance`
  on some meshes), not a FaceGen-specific wrapper.
- A follow-up check against the named skinning pass now makes that interpretation materially
  stronger. `BSFaceGenNiNode::FixSkinInstances` calls the mesh accessor, reads `mesh + 0xE0`,
  requires `*(skin + 0x08) != 0`, treats `skin + 0x14` as a bone-pointer array, writes the owning
  FaceGen node back to `skin + 0x10`, and uses `*( *(skin + 0x08) + 0x54 )` as the loop bound
  while rebinding those bones. That is exactly the shape expected for
  `NiSkinInstance::{Data,Skeleton Root,Bones}` being walked through runtime offsets, with the
  `+0x08` child now behaving like the attached skin-data object and the adjacent `+0x0C` child
  still left as the most likely skin-partition branch.
- That same `+0x0C` branch is also the one consulted by the packed path in
  `BSFaceGenBaseMorphExtraData::BSFaceGenBaseMorphExtraData`. When packed vertex access is active,
  the constructor switches away from the direct `NiGeometryData` vertex count and instead walks
  `*(skin + 0x0C)` through a `(count, pointer)` pair before summing a per-record halfword into the
  alternate `+0x18` count field. So the current best read is no longer just "there is some unknown
  object at `+0xE0`"; it is "the packed branch is consulting the `NiSkinInstance` side,
  probably through its partition payload, to derive alternate materialization counts."
- The shipped sample meshes now also make the subtype question much less abstract. On the Xbox
  base head meshes `headhuman.nif` and `headold.nif`, the block order is
  `NiTriShapeData -> BSPackedAdditionalGeometryData -> BSDismemberSkinInstance -> NiSkinData ->
  NiSkinPartition`, while the adjacent eye mesh `eyelefthuman.nif` has no skin-instance block at
  all. So for the main head path we are no longer choosing blindly between generic
  `NiSkinInstance` and a Bethesda subtype: the shipped base head assets we care about are using
  `BSDismemberSkinInstance`, and the FaceGen-only runtime question is now how that skinned branch
  changes the packed vertex/materialization path.
- The remaining partition-side layout is now mostly resolved too. The runtime
  `NiSkinPartition::LoadBinary` path stores `NumPartitions` at `+0x08`, stores the partition array
  pointer at `+0x0C`, and materializes each partition as a fixed `0x2C` runtime record through
  `NiSkinPartition::Partition::Partition` and `NiSkinPartition::Partition::LoadBinary`.
- That fixed runtime partition record now has a concrete field map:
  - `+0x1C` = `NumVertices`
  - `+0x1E` = `NumTriangles`
  - `+0x20` = `NumBones`
  - `+0x22` = `NumStrips`
  - `+0x24` = `NumWeightsPerVertex`
  - `+0x04` = `Bones` pointer
  - `+0x0C` = `VertexMap` pointer
  - `+0x08` = `VertexWeights` pointer
  - `+0x18` = `StripLengths` pointer
  - `+0x14` = `Strips/Triangles` index pointer
  - `+0x10` = `BoneIndices` pointer
- That closes the loop on the packed FaceGen constructor. The branch inside
  `BSFaceGenBaseMorphExtraData::BSFaceGenBaseMorphExtraData` that walks `*(skin + 0x0C)` and sums
  `*(ushort *)(record + 0x1C)` is summing **partition `NumVertices`**, not reading the base mesh
  vertex count again. On the shipped Xbox `headhuman.nif` / `headold.nif` samples, that means the
  alternate count is `1086 + 152 = 1238`, while the base `NiTriShapeData` vertex count is `1211`.
  So the packed/skinned branch is materially different from the direct `NiGeometryData` path, and
  the difference is large enough to matter.
- The source-iterator side is now much less mysterious too. `NiGeometryData::GetVerticesIterator`
  materializes a tiny `{ pointer, stride, packedFlagByte }` struct, not a richer hidden payload:
  `+0x00 = vertex data pointer`, `+0x04 = stride`, `+0x08 = packed-vertex flag byte`. If no
  packed source is available it falls back to `NiGeometryData + 0x20`, sets stride `0x0C`, and
  leaves the flag byte clear. That means the constructor branch in
  `BSFaceGenBaseMorphExtraData::BSFaceGenBaseMorphExtraData` is not switching on a guessed
  anonymous field anymore; it is switching on the iterator's explicit packed-data flag.
- That also makes the constructor behavior clearer. The primary vector payload at `+0x0C` is
  always built from the iterator source first: in the packed branch it walks the packed iterator
  with the returned stride and expands each source position through `XMConvertHalfToFloatStream`,
  while in the unpacked branch it copies `float3` positions directly from the plain
  `NiGeometryData` vertex stream. The optional `(param_3, param_4)` pair is only a tail append
  after that; it is **not** where the constructor invents the partition-expanded vertices.
- The shipped base-head assets now show that the packed iterator source itself already matches the
  partition-expanded count. `BSPackedAdditionalGeometryData` block `7` on Xbox `headhuman.nif` and
  `headold.nif` reports `NumVertices = 1238`, exactly matching the sum of the two
  `NiSkinPartition` record `NumVertices` values (`1086 + 152`) and not the base
  `NiTriShapeData` vertex count (`1211`). So the runtime is not synthesizing that larger count in
  a later FaceGen-only step; it is reading it from the packed head-mesh vertex stream itself.
- That narrows the refresh path too. `BSFaceGenManager::RefreshMeshFromBaseMorphExtraData` takes
  the same iterator-shaped stack object and branches on that packed flag byte: when clear, it
  copies exactly `NiGeometryData.NumVertices` positions from the base morph buffer; when set, it
  iterates `BSFaceGenBaseMorphExtraData.+0x18` instead. A caller could in principle force the
  mesh-order route by clearing that byte before the refresh call, but we no longer have evidence
  that any named runtime FaceGen caller actually does so.
- The runtime caller picture is cleaner after checking the named callsites. The main
  `BSFaceGenModel::ApplyCoordinateToExistingMesh` / `ApplyCoordinateToNewMesh` branch preserves the
  iterator returned by `NiGeometryData::GetVerticesIterator`, so it keeps the packed flag and runs
  through the partition-ordered `1238`-vertex path when head meshes expose packed data. The named
  sibling refresh callers we checked so far, including `BSFaceGenNiNode`-side morph updates,
  `SetEyePosition`, and the head-part attach helpers, also follow the same pattern: they call
  `GetVerticesIterator`, keep the returned `{ pointer, stride, packedFlagByte }` triple intact, and
  pass that same iterator into `RefreshMeshFromBaseMorphExtraData`.
- The per-morph paths now line up with that same read. Both
  `BSFaceGenMorphDifferential::ApplyMorph` and `BSFaceGenMorphStatistical::ApplyMorph` call
  `NiGeometryData::GetVerticesIterator`, then branch on the returned packed-flag byte instead of
  flattening back to a plain mesh-order iterator. The unpacked side walks the iterator directly,
  while the packed side switches into the alternate head-data branch under `param_3 + 0xE0`.
- The symbol-level names make that cleaner still. The repeated refresh callsites in the older
  `body_tint_decompiled.txt` and `facegen_texture_bake_decompiled.txt` artifacts are not separate
  body-only or texture-only branches; they sit inside the same raw-PDB-named
  `BSFaceGenModel::ApplyCoordinateToExistingMesh` body at `0x82492788`, and the short wrapper at
  `0x82493860` is `BSFaceGenModel::ApplyCoordinateToNewMesh`. So the surviving packed-iterator
  behavior is not a quirk of one dump path; it is the main named coordinate-apply routine itself.
- An exhaustive raw-PPC call scan now makes the direct owner set concrete. There are exactly
  **8** direct `bl 0x82485FB0` callsites in the Aug 22 MemDebug build, owned by **7** named
  procedures:
  - `BSFaceGenManager::ApplyHairMorph`
  - `BSFaceGenModel::ApplyCoordinateToExistingMesh`
  - `BSFaceGenNiNode::ApplyFacialExpressions`
  - `BSFaceGenNiNode::ApplyFacialModifiers`
  - `BSFaceGenNiNode::ApplyFacialPhonemes`
  - `BSFaceGenNiNode::ApplyFacialCustomMorphs`
  - `BSFaceGenNiNode::UpdateAllChildrenMorphData` (2 callsites)
- That owner set strengthens the previous read. `ApplyHairMorph`, `ApplyCoordinateToExistingMesh`,
  and `UpdateAllChildrenMorphData` are the iterator-building entry points we traced, and the four
  `ApplyFacial*` helpers only consume an iterator struct passed down from those higher-level paths.
  So within the complete direct caller set we now have, there is still no confirmed caller that
  bypasses `NiGeometryData::GetVerticesIterator` or intentionally flattens the packed iterator back
  to a plain mesh-order `{ pointer, 0x0C, packedFlag=0 }` struct before refresh.
- The higher-level `BSFaceGenNiNode::UpdateMorphing` gate also fits that same picture. It decides
  whether to call `UpdateAllChildrenMorphData`, but it does not construct or normalize a separate
  iterator blob before the `ApplyFacial*` helpers run. So it does not currently restore a hidden
  mesh-order-only branch above the named refresh owners either.
- The remaining sibling path under `ReplaceFaceMeshLOD` now looks non-mysterious too. The unnamed
  iterator consumer at `0x824861E8` resolves to `BSFaceGenManager::ComputeModelBound`, and it also
  branches on the iterator's packed-flag byte: unpacked iterators are read directly as `float3`
  streams, while packed iterators go through the same half-float expansion helper
  `XMConvertHalfToFloatStream`. So `ReplaceFaceMeshLOD` does not currently expose a hidden
  mesh-order normalization path either; it forwards the live iterator into bound recomputation.
- A direct raw-PPC call scan now closes that branch further: `BSFaceGenManager::ComputeModelBound`
  has exactly one direct caller in the Aug 22 MemDebug build, `BSFaceGenManager::ReplaceFaceMeshLOD`.
  So there is not a second named FaceGen caller feeding it a synthetic mesh-order iterator either.
- The extra base-mesh count locals sitting nearby in those functions (`uStack_a8`, `0x88(r1)`,
  etc.) are therefore **not** the iterator's third field. They track the plain
  `NiGeometryData.NumVertices` value separately, but they are not currently evidence of a caller
  clearing the packed flag before refresh. So the current best read is stronger than before: the
  named runtime refresh paths we have traced all preserve the packed iterator branch.
- The Bethesda subtype loader also looks less suspicious than before.
  `BSDismemberSkinInstance::LoadBinary` first delegates to `NiSkinInstance::LoadBinary`, then
  reads its own extra array at `+0x34/+0x38` and collapses it into a byte mask at `+0x3C`. So the
  main FaceGen-relevant data path still appears to be the shared `NiSkinData` / `NiSkinPartition`
  side, while the dismember-specific extension is adjacent rather than central.
- The packed-stream provenance boundary now looks materially cleaner too. The new focused AGD pass
  shows that `NiGeometryData::GetVerticesIterator` does not conjure a FaceGen-private stream; it
  first checks `NiGeometryData + 0x30` (`m_spAdditionalGeomData`), verifies that object through its
  `GetVBReady` virtual, then calls `NiAdditionalGeometryData::GetDataStream(0, ...)`. On the packed
  subtype, `BSPackedAdditionalGeometryData::GetVBReady` simply returns `1`, so the packed branch is
  explicitly enabled by the geometry-owned AGD object rather than by a later FaceGen-only helper.
- `NiAdditionalGeometryData::GetDataStream` also closes the pointer provenance. It resolves stream
  `0` through the `0x1C`-stride descriptor table rooted at `+0x14`, then indexes the data-block
  table behind `+0x20` / `+0x1C` to produce the returned `{pointer, stride, count}` payload.
  `NiAdditionalGeometryData::LoadBinary` is the routine that allocates and fills those same stream
  descriptors and backing data-block objects from the serialized NIF payload. So the packed
  iterator consumed by FaceGen is now best understood as the normal AGD-backed geometry stream for
  the mesh, not as a synthetic stream introduced later by the FaceGen manager/model layer.
- A raw-PPC call scan of `NiGeometryData::GetVerticesIterator` also reinforces that split. The
  direct owner set is not FaceGen-only; it includes both the already-traced FaceGen owners
  (`ApplyHairMorph`, `ReplaceFaceMeshLOD`, `ApplyCoordinateToExistingMesh`,
  `BSFaceGenMorphDifferential::ApplyMorph`, `BSFaceGenMorphStatistical::ApplyMorph`,
  `BSFaceGenBaseMorphExtraData::BSFaceGenBaseMorphExtraData`, `UpdateAllChildrenMorphData`) and
  unrelated engine consumers such as decals, beam geometry, particle emitters, and collision
  traversals. So this iterator path now looks like the normal geometry-wide packed-stream API that
  FaceGen consumes, not a special-purpose FaceGen bridge with its own hidden normalization stage.
- That narrows the remaining runtime unknown substantially. The unresolved part is no longer
  object identity on the `+0xE0` branch, iterator layout, whether the larger count is "real," or
  where the packed stream comes from. We now know the packed head path is reading a
  partition-ordered `1238`-vertex stream from `BSPackedAdditionalGeometryData`, propagating that
  count through `BSFaceGenBaseMorphExtraData.+0x18`, and preserving that packed iterator through
  the named FaceGen refresh/apply owners we have scanned. The main remaining question is therefore
  implementation parity: whether our renderer must preserve that partition-ordered packed head
  stream instead of normalizing back to mesh-order `NiGeometryData` positions too early.
- The local implementation gap is now clearer too. Our Xbox NIF conversion path explicitly treats
  `BSPackedAdditionalGeometryData` as partition-ordered input that should be remapped back to mesh
  order through `NiSkinPartition.VertexMap` before normal rendering: `NifSkinPartitionParser`,
  `NifSkinDataExpander`, and `NifGeometryWriter` all document or implement that remap, and
  `NpcHeadBuilder` ultimately consumes the extracted/renderable mesh through `NifGeometryExtractor`.
  So if the real FaceGen runtime path is intentionally operating on the packed partition-ordered
  head stream during morph refresh, our current renderer is likely normalizing that distinction
  away too early.
- That makes the runtime split cleaner. `CreateNewMesh` is responsible for cloning and
  FaceGen-specific mesh setup, while `ApplyCoordinateToExistingMesh` remains the place where the
  coord buckets are actually consumed for vertex morph accumulation.
- That closes one major uncertainty in the pipeline: coord parity is now clearly on the critical
  path for **both** runtime mesh morphing and runtime/generated facemod texture application. The
  remaining question is no longer whether the rebuilt coord blob matters downstream; it is whether
  our implementation matches how the engine builds and feeds that blob into the apply routines.
- One useful structural inference also fell out of this pass. `InitFaceGenCoord` preallocates
  three visible value families with sizes `0x32`, `0x1E`, and `0x32`, while the menu only
  exercises `(0,0)` and `(1,0)` directly, and `(1,0)` clearly needs IDs through `0x1F`
  (`Yellow Tint`). So the `0x1E` family is almost certainly one hidden secondary selector bucket,
  not the `Tone` bucket, and one of the four persisted selector buckets is likely latent or
  zero-length in the default editor path.
- The sibling slider type `0x18` looks separate again. Its release path swaps a face-data entry
  through `func_0x824418a0`, refreshes the visible slider set, and then re-synchronizes the
  `0x22` / `0x23` sliders. So it looks more like a preset/template or face-data selection path
  than another direct primary-bucket face-coordinate slider.
- The expression-to-eye-bias path is also narrower now. `ModifyEyeHeadingAndPitchBasedOnExpression`
  only special-cases a sparse subset of expression indices (`0`, `1`, `2`, `3`, `4`, `5`, `6`,
  `8`, `9`, `11`, `12`, plus the sentinel `-1` path), and each case writes preset offsets into
  the temporary eye-heading/pitch fields at `+0x180/+0x184` after a cooldown timer at `+0x17C`.
  So not every live expression slot contributes to eye motion, and this path still looks like a
  small preset table rather than a general expression remap layer.
- The remaining uncertainty is now narrower than before. The direct DATA reference feeding
  `FUN_0082c2c0` did **not** reconstruct into a clean in-memory vtable when treated as a raw slot
  address, so the exact runtime vtable mapping for the packed subtype is still unresolved. But the
  class-family identification itself is now strong from both the serializer field names and the
  shipped Xbox head asset layout.
- The earlier `0x20` family is now somewhat clearer. `FUN_00869fa0` / `FUN_0086b150` manage a
  `0x20`-stride record array, and `FUN_0086b370` deep-copies one record by copying the first
  scalar field directly and then delegating the remaining `0x1C` tail to `FUN_00417d90`. That
  lower helper behaves like a short-string/range copy utility rather than a TRI-specific decoder,
  and the follow-up `FUN_00695400 -> FUN_00694da0` path simply clears or resets the copied
  small-string-style tail for each `0x20` record. The top-level loader now also makes its string
  layout more concrete: variable bytes are copied into storage rooted at `+0x08`, with the
  length/capacity checks at `+0x18/+0x1C`. So this family still looks like name/metadata
  scaffolding, not the place where raw morph payload bytes get interpreted.
- `0x2C` records carry a secondary name/metadata table at `+0xB4/+0xB8`. During `FUN_00865fb0`,
  each record copies a string field rooted at `+0x14`; the string length/capacity checks land at
  `+0x24/+0x28`, which looks like another small-string-style container.
- A focused pass on the `0x2C` family makes that shape more concrete. `FUN_0086b4b0` manages a
  `0x2C`-stride record array and `FUN_0086b6e0` deep-copies one record by copying the first four
  dwords directly, then delegating the tail at `+0x10` to `FUN_00871180`, which in turn copies a
  small-string-style object via `FUN_00417d90`. The cleanup side is the matching
  `FUN_00695420 -> FUN_00694df0` path, which resets the copied tail for each `0x2C` record. The
  grow/trim helpers (`FUN_0086eab0`, `FUN_008ce310`) mostly coordinate generic `0x2C` block
  copy/append/relocate helpers (`FUN_00872c20`, `FUN_00871c20`, `FUN_00871670`, `FUN_008716f0`)
  rather than introducing new TRI-specific per-element decoding. So this family also still looks
  like a named metadata/name-table layer, not yet raw morph-payload interpretation.
- `0x34` records are name-bearing inline-vector morph records. The name string lives at `+0x04`
  (inline or heap-backed depending on the small-string discriminator at `+0x18`), and the payload
  vector array lives at `+0x28/+0x2C` as `0x0C` entries. `FUN_00697f40`, which consumes them,
  constructs an output object whose vtable is labeled `BSFaceGenMorphDifferential` and allocates
  `count * 0x0C` bytes for the copied vectors.
- The top-level `FUN_00865fb0` loader now shows more of the raw on-disk read order for `0x34`.
  Each record is materialized by repeated `FUN_008752c0` reads rather than by one opaque block
  copy: first a name/header read, then an optional string payload read into the small-string tail
  at `+0x04`, then another record header read, then `FUN_0086a9a0` sizes the destination vector
  array, and finally a loop reads compressed vector payload entries that expand into the
  `float3` array. The current decompile strongly suggests each stored vector is encoded as three
  signed byte-like components multiplied by a per-record scale factor before landing in the final
  `float3` payload.
- `0x38` records are name-bearing indexed morph records. They preserve an extra field at `+0x1C`,
  and their payload array is copied from `+0x2C/+0x30` as `0x04` entries. `FUN_00697fd0`, which
  consumes them, constructs an output object whose vtable is labeled `BSFaceGenMorphStatistical`
  and stores both the element count and that extra `+0x1C` field.
- `FUN_00865fb0` also makes the raw read order for `0x38` more concrete. Each record again starts
  with a name/header read plus an optional string payload read into the small-string tail, then
  the loader writes the record's `+0x1C` field from a running accumulator before reading the
  record's index-count header, sizing the `uint32` payload array with `FUN_0086a7c0`, and finally
  reading the index payload block. The current interpretation is that `+0x1C` is not read
  directly from disk for each record; it is derived as a running base offset into the combined
  statistical index stream.
- Focused helper decompilation tightened one important boundary: the `+0x04`, `+0x1C`, `+0x28`,
  and `+0x2C/+0x30` offsets above are **materialized generation-context object layout**, not yet
  proven raw on-disk `FRTRI003` byte offsets. `FUN_0086ba20` and `FUN_0086be00` are copy helpers
  over already-materialized `0x34` and `0x38` records, and they delegate the actual per-record
  deep copy to lower helpers (`FUN_00871230` / `FUN_008712f0`).
- The next helper layer is now partially confirmed. `FUN_00871230` walks `0x34` records, clears
  each materialized record, and delegates nested payload copy to `FUN_00871fa0` rooted at `+0x1C`.
  `FUN_008712f0` walks `0x38` records, preserves the extra scalar at `+0x1C`, and delegates nested
  payload copy to `FUN_00872480` rooted at `+0x20`. Those two lower helpers are the next boundary
  between confirmed generation-context layout and still-unknown raw `FRTRI003` byte interpretation.
- One more layer is now confirmed inside those nested helpers. `FUN_00871fa0` treats the `0x34`
  payload as a `0x0C`-stride dynamic array container with begin/end/capacity pointers at
  `record + 0x28/+0x2C/+0x30` via the nested root at `record + 0x1C`. `FUN_00872480` treats the
  `0x38` payload as a `0x04`-stride dynamic array container with begin/end/capacity pointers at
  `record + 0x2C/+0x30/+0x34` via the nested root at `record + 0x20`, while still preserving the
  separate scalar field at `record + 0x1C`. These are still materialized generation-context
  offsets, not yet proven raw on-disk `FRTRI003` offsets.
- The next range-copy layer is now also confirmed and is notably uninteresting from a file-format
  perspective. `FUN_00872400` is just a direct contiguous copy of `float3` entries from
  `[begin, end)` into the destination buffer, `FUN_00872d10` is the append variant for the same
  `0x0C` differential payload entries, and `FUN_008c2940` / `FUN_008d9210` are the matching
  contiguous copy helpers for `0x04` statistical payload entries using `_memmove_s`. So this
  branch does **not** introduce any per-element transform, coefficient remap, or hidden
  TRI-specific decoding step; it only confirms that the nested payloads are materialized as plain
  contiguous typed arrays once the higher-level readers have decided what ranges to copy.
- The allocator/reset layer is now also mostly explained and is similarly generic. `FUN_0071ea40`
  allocates differential payload storage as `count * 0x0C` bytes and initializes the
  begin/end/capacity triple at `+0x0C/+0x10/+0x14`. `FUN_0086c210` does the same for statistical
  payload storage as `count * 0x04` bytes. `FUN_00870ca0` is just the differential prefix-copy
  variant used when the destination already has enough capacity, and `FUN_008c04c0` is the
  statistical clear/reset wrapper that delegates to `FUN_008c0960`. So these helpers still do not
  expose any TRI-specific per-element semantics; they only narrow the remaining unknowns to the
  higher-level readers/builders that choose which source ranges get copied into these containers.
- The raw `FRTRI003` decode boundary is now much clearer. `FUN_00874a60` initializes the shared
  `BSFaceGenBinaryFile` / `FutBinaryFileC` reader object, `FUN_00874b10` opens the underlying
  stream, `FUN_008753d0` reads the fixed-size header block, and `FUN_008754d0` is the first
  interpreted-header step above raw reads.
- A focused reader pass makes the transport split explicit. `FUN_008733c0` constructs the shared
  `FutBinaryFileC` base and seeds the embedded filename buffer, `FUN_00874a60` upgrades that
  object to `BSFaceGenBinaryFile`, and `FUN_00874b10` then chooses between two generic transport
  backends:
  - archive-backed: `FUN_004bb960` resolves a handle from a global manager object and
    `FUN_004bb290` is just the lock/refcount helper for that handle; `reader + 0x44` stores the
    archive handle and `reader + 0x40` is set to the handle's embedded stream/interface at `+0x08`
  - direct-file-backed: `FUN_008a1e10` opens one of several file stream implementations directly
    from the path
- That means `reader + 0x40` is now best interpreted as a generic stream interface pointer, not a
  TRI-specific decoder object, and `reader + 0x44` is only the optional archive wrapper used when
  the asset is coming from a packed source rather than loose files.
- A deeper schema-object pass now makes the shared header layer more concrete. `FUN_008733c0`
  constructs `FutBinaryFileC` with two string-like fields: the filename buffer at `+0x04` and an
  expected 8-byte FaceGen family tag at `+0x20`. It asserts that tag length is exactly `8`,
  zeros the file handle at `+0x3C`, and forces byte `+5` of the expected tag to `'0'`. That
  matches known FaceGen family headers such as `FRTRI003`, `FREGT003`, and `FREGM002`, where
  offset `+5` is the first version digit.
- `FUN_00874a60` does not add any format-specific parsing. It just upgrades the base object to
  `BSFaceGenBinaryFile` and zeros the two transport slots at `+0x40/+0x44`.
- `FUN_00874b10` then uses that expected 8-byte family tag during open. After transport setup and
  the fixed block read via `FUN_008753d0`, it calls `FUN_008754d0` to materialize a bounded copy
  of the header bytes, then copies byte `+5` from the expected tag into the temporary header
  buffer before doing the string comparison. If the comparison still fails but the file header's
  byte `+5` is greater than `'0'`, the code treats that byte specially and may propagate it back
  into the stored expected tag before surfacing the incompatibility. So this layer is now best
  understood as shared FaceGen family-tag/version compatibility handling, not TRI-specific section
  decoding.
- That `FUN_008754d0` step itself is even more generic than the earlier notes suggested.
  `FUN_009fd760` only allocates a tiny range/view helper, `FUN_00870350` allocates a bounded
  buffer for the `[begin, end)` header subrange, and `FUN_008756d0` is just the `_memmove_s`
  copy into reader-owned storage; `FUN_008756a3` only unwinds the helper frame. `FUN_008752c0`
  remains the reusable lower-level primitive that reads typed element/block payloads from the
  binary stream once the shared family-tag/header stage has succeeded.
- The reader-side virtual/interface calls also divide cleanly now:
  - `FUN_008753d0` uses the `BSFaceGenBinaryFile` object-level method at vtable `+0x10` for the
    fixed header block read
  - `FUN_008752c0` uses the stream interface callback at `stream + 0x08` for typed payload reads
  - `FUN_00875460` uses the stream vtable `+0x14` for seek
- So the remaining unknown is no longer "how does it get bytes from disk/BSA?" or even "how does
  it validate the FaceGen family header?"; it is "how do the post-header readers interpret the
  copied family-specific payload blocks?"
- The call graph also matters here: `FUN_008754d0` is shared with `FUN_00873980` and
  `FUN_008740d0`, not just the `FRTRI003` path. So the best current interpretation is that this is
  a generic FaceGen binary-header decoder/materializer that sits underneath multiple FaceGen binary
  families, while the later `FUN_0086a9a0` / `FUN_0086ab90` / `FUN_0086b4b0` / `FUN_0086b7f0` /
  `FUN_0086bbd0` layer is where TRI-specific section materialization begins.

Correction from the earlier notes:
- The provisional `0x34 = statistical` / `0x38 = differential` naming appears to have been
  inverted. The constructor vtables and overwrite behavior in `FUN_00698be0` point the other way:
  the `0x34` pass builds `BSFaceGenMorphDifferential`-typed outputs first, then the `0x38` pass
  logs `"Only statistical will be used."` and overwrites them with
  `BSFaceGenMorphStatistical`-typed outputs when both exist for the same morph name.

When both statistical and differential morphs exist for the same name, the GECK logs a warning
and uses only statistical: `"MODELS: Statistical and Differential FaceGen morphs found for
expression '%s'. Only statistical will be used."` (string at 0x00D96540).

### 8.5 Our Implementation Match — CONFIRMED

`FaceGenTextureMorpher.AccumulateNativeDeltasQuantized256` (lines 383-428):
```csharp
coeff256 = (int)(textureCoeffs[morphIndex] * 256f);  // matches FLD + FMUL + __ftol2_sse
scale256 = (int)(morph.Scale * 256f);                 // matches (same formula)
accum[pixel] += delta * coeff256 * scale256;          // matches MOVSX + IMUL + ADD
result = accum * (1f / 65536f);                       // matches 1.5258789e-05
```

**Exact formula match.** Minor precision difference: our `* 256f` uses float32 (23-bit mantissa),
GECK uses `FMUL double ptr` (52-bit via x87 80-bit FPU). This could cause ±1 in truncated integers
for values near rounding boundaries, translating to at most ~0.001 per pixel aggregate error over
50 morphs — far below the observed MAE.

### 8.6 Verification Results

Earlier verifier passes supported the coefficient merge and bake accumulator strongly, but the
older conclusion here was too strong.

Audit note (2026-03-18):
- Recent `EgtAnalyzer` runs against sample shipped assets still show residual mismatch above the
  earlier "DXT1 noise only" explanation.
- The strongest remaining signals are bake-amplitude / saturation differences and occasional
  local opponent-axis drift, not a broad channel-order or compression-only failure.
- The formula-level match for coefficient merge and bake accumulation is still credible, but it
  does **not** close the investigation.

**Conclusion**: The coefficient merge and accumulator formula are likely correct at a structural
level, but the remaining texture discrepancy is still unexplained.

---

## 9. Implementation Gaps

### 9.1 FIXED: Shipped facemod decode was wrong (2× scale was missing)

**File**: `FaceGenTextureMorpher.cs` line 619

**Was**: `delta = byte - 128` → applied delta at half strength
**Now**: `delta = byte * 2 - 255` → matches shader's `(sample - 0.5) * 2.0`

Fixed 2026-03-17. The encode/decode is now symmetric with `EncodeEngineCompressedChannel`:
- Encode: `byte = (delta + 255) * 0.5`
- Decode: `delta = byte * 2 - 255`

### 9.2 Runtime EGT path is correct

The runtime path accumulates float deltas and adds directly to base pixels, bypassing
the encode/decode cycle. The round-trip `encode → texture → shader decode` is identity
(within DXT1 precision), so our direct addition is mathematically equivalent.

### 9.3 FaceGenMap1 / detail modulation path — partially verified

**STATUS: Binding chain is supported by decompilation; practical impact is still under review**

**Binding chain (verified 2026-03-17 from decompilation)**:
- `PrepareHeadForShaders` line 1792: sets property[0x2E] = secondary FaceGen texture
  - Fallback: `GetDefaultDetailModulationTexture()` (BSFaceGenManager +0xdb8) when NULL
- `SetFaceGenMaps` copies property[0x2E] → shader texture array +0x14
- CTAB confirms: s3 = FaceGenMap1 at array offset +0x14
- `SetupGeometryTextures` returns immediately for pass 0x0E (no texture remapping)

The `_sk` face tint is a **separate** third slot (+0x18), bound via vtable+0xF0 at index 2.
SKIN2000.pso only uses s0–s3 (4 samplers); the _sk tint at +0x18 is not consumed by this shader.

**Default texture content** (from `BSFaceGenManager::BSFaceGenManager` decompilation, 2026-03-17):
- `+0xdb4` (base modulation, FaceGenMap0 default): 32×32, all pixels `0x80` (128).
  At shader's `×2.0` multiplier: 128/255 × 2.0 ≈ 1.004 → **neutral**.
- `+0xdb8` (detail modulation, FaceGenMap1 default): 32×32, all pixels `{R:62, G:65, B:62, A:64}`.
  At shader's `×4.0` multiplier: {0.97, 1.02, 0.97} → **near-neutral** (imperceptible warm-green tint).

Both textures are procedurally generated in the constructor — no file on disk.
Most NPCs appear to use the default path, and the default values look near-neutral.
**Conclusion**: The default detail modulation texture does not look like an obvious primary cause of
the remaining mismatch, but this section should not be treated as proof that FaceGenMap1 is
irrelevant in every path.

### 9.4 Missing upstream `FRTRI003` / coord-context assembly

**STATUS: Confirmed implementation gap**

Our code starts from an already-existing `.egt` and merged FGTS coefficients. We do **not** have a
source-side equivalent of the GECK path that:
- loads `FRTRI003` via `FUN_00865fb0`
- materializes a multi-section `0xF0` coord/generation context
- copies mesh-aligned section data via `FUN_008647e0`
- then feeds that context into `FUN_00697a10` before texture morph generation
- specifically supplies the `+0xB4/+0xB8` named metadata records, the `+0xCC/+0xD0` inline-vector
  morph records, and the `+0xE4/+0xE8` indexed morph records that
  `FUN_00698be0` / `FUN_00699e50` consume directly

External context note:
- FaceGen's own documentation describes `.tri` as the **base mesh** for a model part, including
  UVs and morph-target information, but **not** the statistical shape changes. In that same model
  set layout, `.egm` carries statistical shape information and `.egt` carries statistical texture
  information.
- Their morph documentation also matches the two-morph-class split we are seeing in decompilation:
  delta morphs are plain vertex displacements, while target/statistical morphs preserve specific
  target positions for things like blinks, eye movement, and the `th` phoneme.
- So, from the external FaceGen side, `.tri` is not just "the mesh file" in a generic sense; it is
  the morph-bearing geometry input that FaceGen uses before applying statistical shape (`.egm`) and
  texture (`.egt`) layers. That lines up well with the New Vegas decompilation path where
  `FRTRI003` is assembled into the upstream FaceGen generation context rather than treated as a
  runtime texture file.

Local asset audit note (2026-03-18):
- This is not just a decompilation hypothesis. The shipped sample assets contain real sibling
  `.tri` files for the same head-adjacent meshes we use elsewhere: `headhuman.tri`,
  `headold.tri`, `headghoul.tri`, eye `.tri`, mouth `.tri`, tongue `.tri`, teeth `.tri`, and many
  hair / eyebrow / beard `.tri` files.
- `headhuman.tri` begins with `FRTRI003`, and its first two `uint32` fields are `1211` and `2294`,
  which match the vertex and triangle counts from `headhuman.nif`'s `NiTriShapeData`.
- A broader local scan of the 28 sibling head/eye/mouth/tongue/teeth `.tri` files in the sample
  PC mesh tree shows that vertex counts are stable against the sibling `.nif` geometry in all
  sampled cases, but the important triangle comparison is **declared strip topology**, not the
  post-conversion rendered triangle list.
  - Render-exact TRI-vs-NIF geometry matches occurred for 11/28 sampled files (main head family
    plus lower teeth), where the sibling `.nif` uses explicit triangles or strip topology that
    survives conversion unchanged.
  - The remaining 17/28 (eyes, mouth, tongue, upper teeth) were not arbitrary mismatches. In all
    of those cases, the TRI header triangle count matched the sibling `NiTriStripsData` block's
    declared `NumTriangles` field exactly, while the rendered triangle count was lower because the
    strip-to-triangle conversion drops degenerate restart windows.
  - Example: `eyelefthuman.tri` is `49 verts / 116 tris`; the sibling `eyelefthuman.nif`
    `NiTriStripsData` block is also declared as `116` strip triangles, but only renders as `81`
    explicit triangles after `35` degenerate windows are removed.
- So the corrected assumption is: for strip-based assets, `.tri` appears to track the source strip
  topology count, not the final degenerate-filtered render triangle list. Future investigation
  should compare against `NiTriStripsData.NumTriangles` before assuming any TRI/NIF topology
  mismatch.
- A minimal local parser now confirms one stable cross-sample payload pattern: after the 64-byte
  header, both `headhuman.tri` and `eyelefthuman.tri` begin with two contiguous `float3` blocks
  whose counts are `header[0x08]` and `header[0x1C]`.
- That local parser now also exposes the decomp-confirmed record families as **layout metadata**
  only: `0x2C` named metadata, `0x34` differential morph, and `0x38` statistical morph. Those
  counts and sizes are useful for integration planning, but they should still be treated as
  generation-context layout facts rather than raw file-offset facts.
- The next payload region is not yet uniform enough to label generically. `headhuman.tri` switches
  into a large non-float tail immediately after those two blocks, but `eyelefthuman.tri` continues
  with more float-like data there, so we should not assume a single "triangle block starts here"
  rule yet.
- So the on-disk sample assets strongly support the current interpretation: `.tri` is a real
  mesh-linked FaceGen generation input, not a speculative side format.

Current implementation audit:
- `NpcAppearanceFactory` resolves the head path from race/body data, builds the fallback
  `FaceGeom` NIF path, and merges FaceGen coefficients. It now also resolves and stores the
  sibling head `.tri` path, but that path is still plumbing, not an active morph input.
- `NpcHeadBuilder` and `NpcExportSceneBuilder` still do not consume `.tri` generation data in the
  actual morph/render path. They continue to start from `BaseHeadNifPath`, optional `.egm`,
  optional `.egt`, and optional prebuilt `FaceGenNifPath`.
- There is now a minimal `FRTRI003` parser in `src` that reads the stable header, the first two
  confirmed `float3` blocks, and the decomp-confirmed record-family layout metadata. Mesh archive
  helpers can load sibling `.tri` files, but the pipeline still does not apply that data.
- For base race head meshes specifically, the current pre-skin EGM path is probably not being hurt
  by our "first skinned shape" shortcut: sampled heads such as `headhuman.nif` and `headold.nif`
  contain a single skinned `NiTriShape`. So the larger mesh-side parity gap is not head-shape
  splitting; it is the absence of **consumed** `.tri`/`FRTRI003` generation-context handling.

That still does **not** prove this path explains the shipped `_0` texture mismatch, because it is
editor / generation-side rather than the downstream `.egt` apply path. But it is no longer just
adjacent infrastructure; it is a concrete missing upstream dependency in any claim of end-to-end
GECK generation parity.

### 9.5 RESOLVED: "Subsurface scattering" is actually distance fog

**Previously**: Believed to be warm backlight/subsurface blend from `_sk` tint.
**Actually**: Standard distance fog blending (FogColor, fogFactor) from vertex shader.

SKIN2000.vso `aout1` (→ PS `v1`):
- `v1.xyz` = FogColor (VS constant c15, passed through)
- `v1.w` = exponential fog factor

`Toggles.y` (c27.y) toggles fog on/off. No subsurface scattering exists in SKIN2000.
Irrelevant for sprite generation (camera distance → fog factor ≈ 0).

---

## 10. Remaining Open Questions

1. **End-to-end parity is still unresolved.** The bake accumulator and encoder behavior look
   structurally correct, but live verification still shows residual mismatch that is too large
   and too patterned to dismiss as simple DXT1 noise.

2. **`PrepareHeadForShaders` still needs a narrower semantic audit, but the branch helper itself is
   no longer the main unknown.** `ResolveFaceGenShaderTexture` is now clearly a path resolver with
   `'M'/'F'` plus numeric-bucket fallback behavior. The remaining uncertainty is what the resolved
   path family semantically represents, and what the follow-up helpers (`func_0x822df198`,
   `func_0x822df238`) derive from it before the texture is bound as FaceGenMap1.

3. **Darker / stronger-negative deltas remain a real signal.** Alternate encode-side rounding
   modes fit darker facemods better in many cases, but no single global mode fixes the full batch.

4. **Platform-source differences exist but do not explain everything.** Xbox `DDX` and PC `DDS`
   shipped facemods decode differently, but that source-format gap is smaller than the remaining
   bake mismatch and does not remove the darker-delta pattern.

5. **Upstream GECK generation context is still only partially modeled.** The `FRTRI003` loader is
   now confirmed to assemble multiple typed sections before morph generation, and the generated
   morph builders directly consume the `+0xCC/+0xD0` (`0x34`) inline-vector family and the
   `+0xE4/+0xE8` (`0x38`) indexed family from that context. We still do not know whether the
   content of those sections materially changes the final baked texture values in the cases where
   our output diverges from shipped facemods.

6. **Our mesh/runtime path still does not consume the shipped `.tri` family semantically.** The
   sample assets confirm that `FRTRI003` files exist alongside the real head, eye, mouth, teeth,
   tongue, and many hair/head-part NIFs. The runtime selector split is now mostly understood:
   `LoadModelMesh` uses `selector == -1` for the extended/head-style morph-data path and
   non-negative selectors for the compact/hair path, with eyes still going through the extended
   path. The sparse non-hair subset mapping is also now mostly understood: eyes populate modifier
   slots, mouth/tongue/lower teeth populate sparse phoneme/expression subsets, and upper teeth
   still look closest to geometry/topology support data. Missing slots are now also understood as
   simple null/no-op entries at apply time. The main live owner path is now also mostly pinned
   down: `UpdateMorphing -> UpdateAllChildrenMorphData` is the regular runtime child-morph
   invocation path, while `PlayerCharacter::CloneInventory3D` reuses the same child loop on
   clone/preview graphs. The fallback-helper side is now also mostly resolved:
   `UpdateEyeTracking`, `IsActorNCloseToPlayer`, and `IsInMenuMode` bound the runtime relevance
   gate around that child path. A follow-up `BSFaceGenNiNode` layout pass ruled out the obvious
   local-field guess, and the next inherited-object pass resolved the remaining field identity:
   `piVar5[6]` is the inherited parent pointer at `NiAVObject + 0x18`, not the controller chain
  and not a hidden FaceGen-owned object. A final DIA-backed slot pass then retired the last
  anonymous-call question: the child branch now resolves to
  `BSFaceGenNiNode::GetAnimationData` (`+0x100`), `BSFaceGenAnimationData::GetDead` (`+0xD4`),
  and a parent-side `NiObject::IsFadeNode` family test (`+0x10`). A later runtime
  name/attachment bridge pass also ruled out the two nearest anonymous helpers as parent-assembly
  clues: `0x82E213C8` is `NiGlobalStringTable::AddString`, and `0x822A52C8` is
  `TES::CreateTextureImage`. A later outer-owner pass then resolved the immediate post-head helper
  chain too: `0x8243F290 = TESNPC::AttachHead`, `0x8243EA68 = TESNPC::InitDefaultWorn`,
  `0x82441B00 = TESNPC::InitWorn`, `0x8243EF30 = TESNPC::FixDisplayedHeadParts`, and
  `0x82444018 = TESNPC::FixNPCNormals`. That same pass also showed the apparent
  `NiPointer<BSFadeNode>::operator=` clue is not reliable type evidence, because the identical
  helper is used to store direct `BSFaceGenNiNode*` outputs from `TESNPC::InitHead`. So the
  remaining Wave 2 unknown is no longer any unnamed local helper or folded refcount helper
  boundary. A later biped bridge pass then closed the `AttachHead` semantics too:
  `0x822F8AF0 = BipedAnim::GetParentBone`, and the remaining virtuals under `AttachHead`
  resolve to ordinary `NiNode::AttachChild` plus `BSFaceGenNiNode` flag/fixup methods
  (`SetAnimationUpdate`, `SetApplyRotToParent`, `FixSkinInstances`). So the remaining Wave 2
  unknown is now the higher-level runtime meaning of the dead/fade-node modifiers-only fallback.
  The identity question for the secondary branch is now mostly retired: `CreateHead` names the
  two nodes `BSFaceGenNiNodeBiped` and `BSFaceGenNiNodeSkinned`, the owner-side accessors expose
  the same split, and the process-side runtime layout mirrors it with `pFaceNode` and
  `pFaceNodeSkinned` on `MiddleHighProcess`. The actor runtime path is also clearer now:
  `Actor::GetFaceAnimationData -> MiddleHighProcess::GetFaceAnimationData ->
  TESObjectREFR::GetFaceAnimationData`, so the live NPC path still depends on the owner-side
  lookup rather than bypassing it. In the base owner accessor family, generic `GetFaceNode()`
  currently forwards to `GetFaceNodeSkinned()`, but the process layout keeps a separate
  `pFaceNode` / `pFaceNodeSkinned` split, and the attachment path still marks the primary node as
  `AnimationUpdate=true` while the skinned node gets `false`. A later owner-role pass narrowed the
  secondary branch much further: `TESNPC::FixDisplayedHeadParts` explicitly locates **both**
  `BSFaceGenNiNodeBiped` and `BSFaceGenNiNodeSkinned` and toggles their visibility together,
  `SurgeryMenu::UpdateFace` explicitly searches for the **skinned** node only, and the
  dismemberment path (`HideDismemberedLimb` / `DismemberLimb`) also touches the skinned node
  directly as part of the clone/hide/detach flow. The direct-call scan for the accessor layer is
  also now informative by omission: there are ordinary direct callers of `Actor::GetFaceAnimationData`
  and the expected `MiddleHighProcess -> TESObjectREFR::GetFaceAnimationData` bridge, but no
  ordinary direct calls to `GetFaceNodeBiped` or `GetFaceNodeSkinned`, which fits the current read
  that those are mostly virtual/name-probe helpers rather than stable external APIs. So the
  remaining Wave 2 uncertainty is now narrower than before. The process-side cache population is
  no longer mysterious at a structural level: a later raw slot-scan plus owner decompile shows
  that `Character::Update` and `Character::PrecacheData` lazily populate `pFaceNode`,
  `pFaceNodeSkinned`, and the sibling `+0x7A0` slot from the live actor 3D when they are null,
  while `TESObjectREFR::Set3D`, `Script::ModifyFaceGen`, and `RaceSexMenu::UpdatePlayerHead` are
  the corresponding clear/rebuild-side owners. `FixedStrings::InitSDM` then closes the key-name
  gap: the lazy process path is keyed by `BSFaceGenNiNodeBiped`, `BSFaceGenNiNodeSkinned`, and
  `HeadAnims:0`. A later focused bridge pass plus shipped-skeleton inspection now ties the
  `HeadAnims:0` branch back to the owner-side animation-data family: it is the cached
  skeleton-side facial animation `NiTriShape` under the `HeadAnims` node, and `Character::Update`
  uses its live `NiGeomMorpherController` / `NiMorphData` state to feed
  `SetAnimHeadCulled`, `SetAnimExpressionValue`, `SetAnimModifierValue`, and
  `SetAnimPhonemeValue` on both FaceGen animation-data objects. That retires the old
  “what is the `+0x7A0` object?” question. A later visibility pass also tightens the parent-side
  flag semantics: the branch now lines up with `NiVisController` state on the parent `HeadAnims`
  node, and the direct-call scans show `SetAnimHeadCulled` is owned only by `Character::Update`
  while the three per-category setters have no ordinary direct callers, which is consistent with a
  virtual-only live animation bridge rather than a static head-build path. The skinned node still
  looks more strongly tied to visibility/edit/dismember maintenance than to the main
  process-owned face node, so the
  remaining reconciliation question is now mostly about the biped/skinned split rather than the
  `HeadAnims:0` branch. A later process-lifecycle pass also rules out one tempting branch:
  `MiddleHighProcess::MiddleHighCopy` preserves `pFaceNode` / `pFaceNodeSkinned`, but the
  constructor and `Revert` just clear them, so initial population still belongs to the live
  update path rather than lifecycle copy/revert. A later attach-helper/asset pass narrowed the
  parentage side too:
  `AttachHead` uses `GetParentBone(param_3, 0)`, the shipped third-person human skeleton root is
  already `BSFadeNode`, and the traced local biped attach helpers do not construct a separate
  fade-node wrapper. So the remaining gap is no longer “where is the fade parent introduced?” in
  the normal third-person path; it is the exact relationship between the skinned-node branch, the
  owner-side generic face-node lookup, the process-side `pFaceNode` / `pFaceNodeSkinned` split,
  and the modifiers-only child fallback. A final flag-accessor pass also ruled one local candidate
  down: `GetFixedNormals` (`+0x111`) has only unrelated debug/weapon direct owners, so it is a
  poor fit for this FaceGen fallback. The production head/runtime path still does not use
  TRI-backed semantics when assembling or applying FaceGen. A later `HeadAnims:0` cross-check also
  now makes one negative conclusion stronger: the cached skeleton-side animation bridge looks much
  more like live facial animation playback than static head construction, so it is probably a
  lower-priority parity gap for our NPC renderer than the packed base-head stream and TRI-backed
  morph application.

7. **The packed Xbox base-head stream now looks canonical on the runtime side.** The remaining
   runtime question is no longer provenance; it is parity. The named engine path appears to preserve
   the packed, partition-ordered head iterator sourced from `BSPackedAdditionalGeometryData`, while
   our renderer normalizes that distinction away by remapping back to mesh order before FaceGen. A
   later cache-side loader pass strengthens that prioritization: the runtime cache now looks like it
   treats the mesh/TRI-backed `BSFaceGenModel` as the core object and EGM/EGT as lazy supplementary
   payloads layered onto it, not the other way around.

8. **The remaining error is not yet localized.** The current best hypothesis is a difference in
   per-morph weighting or encode-side treatment of stronger negative deltas, not a broad channel
   swap, container-format issue, or NPC-specific lookup bug.

This pipeline is much better understood than it was before the decompilation work, but it should
not be treated as fully closed. Any claim that the remaining mismatch is "just DXT1 noise" is
stale.
