# PC Terrain Texture Engine Parity Notes

This note tracks terrain and NIF rendering evidence for Fallout: New Vegas PC
visual parity. The PC renderer and PC shader packages are the target behavior.
The Xbox 360 MemDebug XEX/PDB pair is still very useful because it provides
names, pass IDs, structure offsets, and material-binding control flow that are
harder to recover from the stripped PC executable.

The most reliable symbol-rich control-flow evidence is from
`Sample/PDB/Proto/Fallout_Release_MemDebug`; its `.text` image maps cleanly at
`0x82250000`. The final-build Xbox PDB symbols are useful for names, but a
simple section-base mapping does not align all functions cleanly, so final-build
addresses should not be trusted until OMAP/section translation is handled.

For shader math, prefer the PC `shaderpackage*.sdp` files because they contain
Direct3D 9 bytecode that can be disassembled locally. The Xbox shader package is
useful for record/name matching and confirming cross-platform shader families,
but Xenos microcode disassembly is no longer a blocker for PC parity.

Focused reports/artifacts used so far:

```text
tools/GhidraProject/terrain_texture_parity_decompiled_xenon.txt
tools/GhidraProject/memdebug_terrain_params_decompiled_xbox360.txt
tools/GhidraProject/texture_stage_configure_decompiled_xbox360.txt
tools/GhidraProject/texture_stage_tables_trace_xbox360.txt
tools/GhidraProject/land_texture_tiling_trace_xbox360.txt
tools/GhidraProject/landscape_preset_pass_trace_xbox360.txt
tools/GhidraProject/shadowlight_shader_init_decompiled_xbox360.txt
tools/GhidraProject/nif_shader_parity_decompiled_xbox360.txt
tools/GhidraProject/shader_probe_memdebug_report.txt
tools/GhidraProject/pc_land_shader_disassembly.txt
tools/GhidraProject/pc_nif_shader_disassembly.txt
tools/GhidraProject/pc_basic_sls_shader_disassembly.txt
```

## Parity Strategy

- Use the PC final build under `Sample/Full_Builds/Fallout New Vegas (PC Final)`
  as the visual target.
- Use PC `shaderpackage*.sdp` disassembly for exact shader-side math whenever a
  same-name record exists. `tools/disasm_shader.py` handles these D3D9 records.
- Use Xbox 360 MemDebug symbols/decompilation to map PDB enum names, pass IDs,
  material-property fields, texture binding, and high-level render-path decisions.
- Use PC executable/GECK decompilation only where platform differences can affect
  PC parity, especially render-state setup, INI/default resource choices, and
  any PC-only shader package selection.
- Treat Xbox-only details such as Xenos sampler encodings or microcode as
  corroborating evidence, not the final parity target.

## Confirmed

- `TESObjectLAND::GetMainTexture` validates quadrant `0..3` and vertex position
  `< 0x121` (`17 * 17`). It chooses the texture slot with the largest percent
  value. Slot `0` maps to the quadrant default/base texture; slots `1..5` map
  through the quadrant texture array.
- `TESObjectLAND::SetTexturePercent` uses the same quadrant `0..3`, position
  `0..288`, and texture slot `0..5` limits, and writes directly into the
  per-quadrant percent array.
- `TESObjectLAND::GetTextureUsePercent` sums all `289` percent samples for a
  quadrant/slot.
- `TESObjectLAND::AdjustTextureArrays` prunes very low-use slots, compacts the
  texture arrays, and fills missing per-vertex weight with a default slot before
  choosing a new default where needed. This supports an explicit per-slot weight
  model with default/base weight fill; same-name shader disassembly below points
  toward weighted accumulation rather than an ordered `lerp` chain.
- `TESObjectLAND::LoadedLandData` exposes `pDefQuadTexture`, `pQuadTextureArray`,
  and `ppPercentArrays`; the texture-selection functions use the first four
  entries as the four LAND texture quadrants.
- `BSShaderPPLightingProperty::SetForLandscapeTextures` expands landscape
  material texture storage before landscape rendering.
- `BSShaderPPLightingProperty::SetLandscapeTextureSet` copies seven texture-set
  slots per landscape texture entry and updates the landscape texture count.
- `BSShaderPPLightingProperty::SetLandscapeTextureSet` also inspects texture-set
  slot `1` for each landscape entry. This is the normal-map slot in the local
  NIF/TXST metadata model; the function flags the layer when the slot's type
  field is `1`, `5`, or `6`, so landscape materials are not diffuse-only in the
  engine material path.
- `BGSTerrainManager::GetNormalTextureForPoint` resolves a terrain texture set
  at a world point and returns texture-set slot `1`; the adjacent base-texture
  path returns slot `0`.
- `ShadowLightShader::SetupGeometryConstants_LandTextureMask` builds explicit
  channel masks from the pass's texture index byte. The shader path is
  pass/channel driven, not a single normalized splat-weight combine.
- `BSShaderPPLightingProperty::AddLandscapePasses_1x` adds separate landscape
  passes around IDs `0x1a8..0x1ab` after the base landscape pass, and adds
  additional LOD/detail-related passes around `0x2eb..0x2ec` when the landscape
  texture offset/fade/detail-scale parameters differ from defaults.
- In the 1x path, pass `0x1ab` is only added for a layer when both the layer's
  diffuse/base texture array entry and the slot-`1` texture array entry are
  non-null. This makes `0x1ab` the clearest current normal-map companion pass
  signal for landscape layers.
- `BSShaderPPLightingProperty::AddLandscapePasses_2x` has a more compact
  multi-texture pass path and can add detail pass `0x1e6` when the relevant
  shader flag/global allows it.
- `TESObjectLAND::InitializeStatics` reads `fLandTextureTilingMult` from setting
  object `0x8322AE80`; the default value at `0x8322AE84` is `0x40000000`
  (`2.0f`). The code computes `4.0 / fLandTextureTilingMult`, then stores UVs
  with a per-LAND-interval step of `1 / (4 / fLandTextureTilingMult)`. With the
  default value this is `0.5` UV per 256-world-unit LAND interval, or one diffuse
  repeat per 512 world units. A 4096-unit exterior cell therefore has 8 diffuse
  repeats per axis.
- `ShadowLightShader::SetupGeometryConstants_LODTexParams` uploads terrain
  texture offset/fade/detail-scale constants from the lighting property. This is
  another sign that the engine's final terrain shader can include detail/LOD
  texture contribution beyond the diffuse splat preview.
- Dynamic initializers and shader accessors confirm the real MemDebug
  `ShadowLightShader` static arrays:

  ```text
  pPixelShader[i]  = *(0x832B7CE0 + i * 4), count 0xCA
  pVertexShader[i] = *(0x832B8008 + i * 4), count 0x9C
  spPass[i]        = *(0x832B8278 + i * 4), count 0x7E * 6 words
  ```

  The direct PDB section-7 base calculation lands at the wrong globals for
  these arrays, so shader/pass globals should be tied back through dynamic
  initializer and code references instead of simple section-base addition.
- `ShadowLightShader::LoadVertexShaders` calls `BSShader::MakePath` and
  `BSShader::CompileVertexShaderHLSL`, then stores compiled objects in
  `pVertexShader`. `LoadPixelShaders` uses `BSShader::CompilePixelShaderHLSL`
  and stores into `pPixelShader`.
- `tools/ShaderProbe` parses the shipped Xbox 360 `shaderpackage.sdp`. The
  final, Aug. 22, and July 21 package copies currently checked all share SHA256
  `9D0505CA6C547A5A06355E0E21A1748603438DA3D715AB896D7BBC3B6FE39DB8`. The
  package header is big-endian, with name-field size `0x64`, record count
  `0x315` (`789`), and declared payload size `0xEA528`.
- The Xbox 360 shader package contains the same named land shader records used
  by the MemDebug `ShadowLightShader` pass setup. The record metadata confirms
  terrain is not diffuse-only:

  | Record | PDB name | Package constants |
  | --- | --- | --- |
  | `SLS1040.pso` | `SLS_PS1_LANDAD` | `BaseMap`, `NormalMap` |
  | `SLS1041.pso` | `SLS_PS1_LANDAD_A` | `BaseMap`, `NormalMap` |
  | `SLS1042.pso` | `SLS_PS1_LAND_Si` | `BaseMap`, `GlowMap` |
  | `SLS1043.pso` | `SLS_PS1_LAND_SiA` | `BaseMap`, `GlowMap`, `PSLightColor` |
  | `SLS2108.pso` | `SLS_PS2_LAND2xAD_Shp` | `NormalMap` present |
  | `SLS2109.pso` | `SLS_PS2_LAND2xAD_AShp` | `NormalMap` present |
  | `SLS2110.pso` | `SLS_PS2_LAND2xDIFF` | `NormalMap` present |
  | `SLS2113.pso` | `SLS_PS2_LAND2xDIFF_A` | `NormalMap` present |
  | `SLS2128.pso` | `SLS_PS2_LANDAD` | `BaseMap`, `NormalMap` |
  | `SLS2129.pso` | `SLS_PS2_LANDAD_A` | `BaseMap`, `NormalMap`, shadow maps |
  | `SLS2132.pso` | `SLS_PS2_LAND_SiA` | `BaseMap`, `NormalMap` |
  | `SLS2133.pso` | `SLS_PS2_LANDDIFF` | `BaseMap`, `NormalMap` |
  | `SLS2134.pso` | `SLS_PS2_LANDDIFF_A` | `BaseMap`, `PSLightDir` |

  The package also exposes LOD/detail inputs such as `LODLandNoise`,
  `LODParentNormals`, `LODParentTex`, and `LODTexParams` in related land records.
  Runtime strings confirm default resources named `BSShader_DefHeightMap`,
  `BSShader_DefGlossMap`, `BSShader_DefNormalMap`, `BSShader_DefErrorMap`, and
  `BSShader_DefTexEffectMap`.
- The landscape preset pass setup maps the key pass IDs to named shader IDs:

  | Pass | PDB pass name | 1x shader | 2x shader | Notes |
  | --- | --- | --- | --- | --- |
  | `0x03f` | `BSSM_LANDAD` | `SLS_VS1_LANDAD` / `SLS_PS1_LANDAD` | `SLS_VS2_LANDAD` / `SLS_PS2_LANDAD` | Base land AD pass. |
  | `0x1a9` | `BSSM_LANDAD_A` | `SLS_VS1_LANDAD_A` / `SLS_PS1_LANDAD_A` | `SLS_VS2_LANDAD_A` / `SLS_PS2_LANDAD_A` | Alpha variant. |
  | `0x1a8` | `BSSM_LAND_G` | `SLS_VS1_LAND_Si` / `SLS_PS1_LAND_Si` | `SLS_VS2_LAND_Si` / `SLS_PS2_LAND_Si` | Layer pass. |
  | `0x1ab` | `BSSM_LAND_GA` | `SLS_VS1_LAND_Si` / `SLS_PS1_LAND_SiA` | `SLS_VS2_LAND_Si` / `SLS_PS2_LAND_SiA` | Slot-1 normal companion signal in the 1x path. |
  | `0x2eb` | `BSSM_LANDDIFF` | `SLS_VS1_LANDDIFF` / `SLS_PS1_LANDDIFF` | `SLS_VS2_LANDDIFF` / `SLS_PS2_LANDDIFF` | Detail/LOD-related pass. |
  | `0x2ec` | `BSSM_LANDDIFF_A` | `SLS_VS1_LANDDIFF_A` / `SLS_PS1_LANDDIFF_A` | `SLS_VS2_LANDDIFF_A` / `SLS_PS2_LANDDIFF_A` | Detail/LOD alpha variant. |
  | `0x040` | `BSSM_LANDAD_Shp` | n/a in 1x trace | `SLS_VS2_LAND2xAD_Shp` / `SLS_PS2_LAND2xAD_Shp` | Four-stage 2x shadow variant; stage 3 is clamp/aniso. |
  | `0x1a7` | `BSSM_LAND2xDIFF` | n/a in 1x trace | `SLS_VS2_LAND2xDIFF` / `SLS_PS2_LAND2xDIFF` | Three-stage 2x pass; stage 2 uses fixed resource `0x832B3774`. |
  | `0x1ac` | `BSSM_LAND2xDIFF_A` | n/a in 1x trace | `SLS_VS2_LAND2xDIFF_A` / `SLS_PS2_LAND2xDIFF_A` | Alpha variant; stage 2 uses fixed resource `0x832B3774`. |
  | `0x1e6` | `BSSM_LANDLO_A` | n/a in 1x trace | `SLS_VS2_LANDLO_A` / `SLS_PS2_LANDLO_A` | Three-stage LOD/detail pass; uses fixed resources around `0x832B377C/0x832B3780`. |

  The larger `0x1ad..0x1e5` family maps to the PDB `BSSM_LAND1O..LAND7O`
  simple/shadow variants and the `SLS_PS2_LANDnO*` pixel shader sequence, with
  `SLS_VS2_LANDO` or `SLS_VS2_LANDO_Shp` vertex shaders.
- `BSShader::SetupDefaultStage` sets clamp mode `3` when its third argument is
  not `2`, and filter mode `6` when anisotropy is requested. The local NIF schema
  maps those enum values to `WRAP_S_WRAP_T` and `FILTER_ANISOTROPIC`.
- `NiD3DTextureStage::ConfigureStage` consumes those clamp/filter bytes when it
  writes Xbox sampler state. A focused PowerPC constant-propagation scan found
  the table initializer around `0x8285D394..0x8285D554`. The imported image still
  shows zeroed tables, but the initializer writes the raw table values below.

  Filter table at `0x832AB880` (`mode -> min, mag, mip`):

  | Mode | NIF name | Raw min | Raw mag | Raw mip |
  | --- | --- | ---: | ---: | ---: |
  | 0 | `FILTER_NEAREST` | 0 | 0 | 2 |
  | 1 | `FILTER_BILERP` | 1 | 1 | 2 |
  | 2 | `FILTER_TRILERP` | 1 | 1 | 1 |
  | 3 | `FILTER_NEAREST_MIPNEAREST` | 0 | 0 | 0 |
  | 4 | `FILTER_NEAREST_MIPLERP` | 0 | 0 | 1 |
  | 5 | `FILTER_BILERP_MIPNEAREST` | 1 | 1 | 0 |
  | 6 | `FILTER_ANISOTROPIC` | 4 | 4 | 1 |

  Clamp table at `0x832AB8E0` (`mode -> addressU, addressV`):

  | Mode | NIF name | Raw U | Raw V |
  | --- | --- | ---: | ---: |
  | 0 | `CLAMP_S_CLAMP_T` | 2 | 2 |
  | 1 | `CLAMP_S_WRAP_T` | 2 | 0 |
  | 2 | `WRAP_S_CLAMP_T` | 0 | 2 |
  | 3 | `WRAP_S_WRAP_T` | 0 | 0 |

- `BSShaderPPLightingProperty::GetDiffuseTexture` reads the per-index texture
  pointer from the array at `this + 0xb4`; `GetNormalTexture` reads the matching
  normal texture pointer from `this + 0xb8`. `SetDiffuseTexture` and
  `SetNormalTexture` write those same arrays through `NiPointer` assignment.
- `Lighting30Shader::SetNormalMap` reads the normal texture array at
  `property + 0xb8`, binds the resolved texture object into the target texture
  stage at `stage + 8`, falls back to global `0x832B374C` when no normal map is
  present, and applies the source texture's clamp mode to the stage. This proves
  ordinary NIF lighting has an explicit normal-map binding path, not only
  landscape material handling.
- `BSShaderPPLightingProperty::SetFlagsFromTextures` inspects normal texture-set
  slot metadata, especially type values `5` and `6`, and updates the lighting
  property's shader flags plus per-layer normal-presence bytes. It can also build
  an auxiliary texture when the expected normal-side texture is absent. The exact
  generated texture semantics still need deeper tracing.
- `BSShaderPPLightingProperty::StoreTextureSplatVertexDataPointers` stores the
  splat vertex data pointer at `this + 0xe0`, toggles shader flag bit `0x4000`,
  and invalidates the cached pass value at `this + 0x38` when the flag changes.
- PDB enums and `NiVertexColorProperty` code confirm the vertex-color property
  state layout. `SourceVertexMode` values are `0=SOURCE_IGNORE`,
  `1=SOURCE_EMISSIVE`, `2=SOURCE_AMB_DIFF`; `LightingMode` values are
  `0=LIGHTING_E`, `1=LIGHTING_E_A_D`. The 16-bit `m_uFlags` is stored at
  offset `0x18`; source mode lives in bits `0x30` (`>> 4`) and lighting mode in
  bit `0x8` (`>> 3`).
- `NiVertexColorProperty::NiVertexColorProperty` initializes source mode to
  `SOURCE_IGNORE` and lighting mode to `LIGHTING_E_A_D`. `LoadBinary` preserves
  legacy stream behavior by reading old packed flags for versions below
  `0x0A000102`, and old separate source/lighting fields below `0x14010002`.
- `Lighting30Shader` PDB pass enums include explicit vertex-color families such
  as `L3S_PASS_LIGHTING_Vc`, `L3S_PASS_LIGHTING_VcS`,
  `L3S_PASS_LIGHTING_VcG`, and matching `BSSM_3XLIGHTING_Vc*` pass names. This
  confirms vertex-color rendering is a shader-pass selection axis in the engine.

## PC Shader Evidence

The PC final `shaderpackage003.sdp` contains same-name `SLS####` records in
Direct3D 9 bytecode. For PC parity, these disassemblies are direct shader-math
evidence. Their constant names and record names match the Xbox package closely,
so the Xbox PDB enum names can be used to label the PC records.

- `SLS1040.pso`/`SLS1041.pso` decode both the sampled `NormalMap` and the
  interpolated light vector with `(sample - 0.5) * 2`, dot them, then compute
  `BaseMap * vertexOrLandColor * (AmbientColor + NdotL * PSLightColor)`.
- `SLS1042.pso`/`SLS1043.pso` sample `GlowMap` and multiply it by `BaseMap` and
  the interpolated color. In the landscape context this `GlowMap` is best treated
  as a mask/control sampler until the PC pass state and material binding are
  fully decoded; it is not enough evidence for emissive terrain.
- `SLS2128.pso`/`SLS2129.pso` show the compact multi-texture land path. They
  sample five `NormalMap` slots (`s7..s11`), decode each by `(sample - 0.5) * 2`,
  weight them by `v0.xyzw`/`v1.xy`, sum them, normalize the result, then compute
  lighting. Diffuse textures (`s0..s4`) are accumulated with the same weight
  channels. Shadow variants then apply `ShadowMap`/`ShadowMaskMap`.
- `SLS2132.pso`/`SLS2133.pso` do the same for six base/normal layers. Some land
  variants such as `SLS2130.pso`, `SLS2131.pso`, and `SLS2134.pso` omit
  `NormalMap` and use vertex/land normals for lighting.
- `SLS1004.pso` (`SLS_PS1_TEXTURE_Vc`) is the simplest vertex-color texture
  proof: it samples `DiffuseMap` and multiplies RGB by interpolated vertex color
  `v0`. `SLS1009.pso` is the basic bump-lit texture path with `BaseMap` and
  `NormalMap`.

## Viewer Implications

- The 2D renderer now uses weighted diffuse accumulation for BTXT/ATXT samples,
  with the quadrant base/default texture filling the remaining VTXT weight. This
  matches the same-name PC land shader evidence better than the older ordered
  `lerp` preview. It is still diffuse-only: exact 2D parity should wait until PC
  pass blend states and base/default weight handling are traced, using the
  Xbox-symbol decompilation as the map for named functions and fields.
- The 2D renderer's quadrant model should remain `0=SW`, `1=SE`, `2=NW`,
  `3=NE`, with each quadrant backed by a dense `17x17` VTXT opacity grid.
- Missing VTXT entries should remain zero opacity in the viewer. The engine only
  writes nonzero values through `SetTexturePercent`; absent entries are not
  synthesized during rendering.
- Diffuse terrain texture sampling should use `1 / 512` world-units-to-repeat
  scale. The 2D palette uses `512` world units per wrapped tile; the 3D terrain
  shader receives `TerrainRenderer.DefaultDiffuseUvScale = 1f / 512f`.
- Diffuse terrain sampling should wrap and use anisotropic filtering where the
  renderer can support it. Opacity masks should remain clamped and bilinear.
- The current 3D terrain shader is still a simplified visualizer: it blends
  diffuse textures with opacity masks, multiplies vertex color, and applies a
  simple Lambert term. It does not yet reproduce terrain normal maps,
  detail/LOD textures, noise, specular, or the engine's exact lighting.
- The 2D terrain texture layer should be treated as a diffuse/albedo preview. It
  intentionally omits lighting, LAND normals, terrain detail maps, and water or
  atmospheric lighting effects.
- The world-view REFR/NIF shader path now binds one diffuse texture plus an
  optional texture-set slot-`1` normal map. It uses uploaded tangents and
  bitangents, flips the normal-map green channel for DirectX convention, and
  scales the tangent-space XY perturbation by `0.35`, matching the existing
  NPC/offscreen renderer convention. It remains a simplified viewer shader:
  alpha-test, vertex-color multiply, bump-mapped simple Lambert lighting, but no
  glow maps, specular maps, environment maps, shader-specific pass families, or
  exact PC shader-family lighting.
- The NPC/offscreen NIF renderer remains more advanced than the world-view REFR
  path for character-focused rendering: it has tint/emissive/FaceGen/eye
  approximations and more lighting terms. Those are useful viewer behavior, but
  still not proof of exact PC shader-family parity.
- Current NIF vertex-color policy treats vertex color RGB as active when present
  for extracted geometry, because shipped art uses vertex colors even when the
  nominal shader flag is not consistently set. For no-lighting/effect cases the
  helper can preserve vertex alpha while leaving RGB neutral.
- The engine evidence supports keeping normal maps and vertex colors in the 3D
  viewer. PC shader disassembly supports a direct normal decode of
  `(sample - 0.5) * 2` and simple diffuse times vertex-color multiplication for
  ordinary textured vertex-color passes. The current viewer's normal-map
  strength, DirectX green-channel flip, and simple Lambert lighting remain
  approximations until the relevant PC shader families and render states are
  implemented.
- The current zoom buckets and viewport-limited 2D texture builds are viewer
  performance policy, not engine behavior.
- The zoomed-out NavMesh cell-summary overlay and 2D water tint overlay are also
  viewer policy, not engine behavior.

## Still Open

- Final-build Xbox symbol/address mapping needs proper OMAP/section translation;
  direct base addition produced mid-function/wrong-function decompiles.
- MemDebug direct PDB data-section mapping is also unreliable for some globals.
  The shader/pass arrays above are verified from dynamic initializers and
  accessor code, not the naive `[0007:offset] + .data base` mapping.
- Max-anisotropy runtime policy is not fully resolved. The imported global
  `0x8326A444` is `1`, but other initializer/configuration sites write `16` and
  `1`; `ConfigureStage` uses the current global when filter mode is `6`.
- `PresetLandscape_2x` is large and more complex. The broad MemDebug pass
  decompiled it, but split helper targets will be easier if exact PC 2x pass
  selection/render-state parity becomes necessary.
- The fixed landscape stage resources at `0x832B3774`, `0x832B377C`, and
  `0x832B3780` still need identity tracing.
- Exact PC vertex-color combine, normal-map math, detail-map math, noise
  contribution, specular/glow/environment behavior, and final lighting should be
  taken from PC shader bytecode first, then tied back to material/pass selection
  with Xbox-symbol decompilation and PC executable checks where needed.
- `tools/ShaderProbe` now extracts Xbox shader records and their printable
  metadata. Xenos microcode disassembly would still be useful for cross-platform
  comparison, but it is optional for PC parity as long as the PC D3D9 shader
  record and render-state path can be recovered.
