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

### Legacy PS1 PP-Lighting Basic Diffuse-Bump

- The tracked PC-final oracle is `docs/fnv_basic_sls_shader_disassembly.txt`.
  Shader-package numbers are stage-local: PS 1009 is the base
  `PPAMBDIFFUSETEXTUREDIR` permutation, PS 1010 is its fog permutation, and PS
  1013 is its vertex-color permutation. The matching vertex-color vertex shader
  is VS 1012. Treating PS 1010's fog interpolation as a material vertex-alpha
  combine was incorrect. This bytecode is a legacy-tier equation oracle, not the
  shipped PC retail material route.
- The MemDebug pass enum brackets IDs 673 through 755 with
  `BSSM_UNUSEDPASSES_FIRST`/`BSSM_UNUSEDPASSES_LAST`; SLS1009 and SLS1013 map to
  unused IDs 700 and 701. `BSShaderPPLightingProperty::GetRenderPasses` selects
  `GetRenderPasses_1x` only when the global shader tier equals 1 and selects
  `GetRenderPasses_2x` for every tier above 1. The captured retail
  `RendererInfo.txt` reports `BSSM_SV_2_A`, PS version 300, and 3.0 Lighting
  enabled, directly excluding the PS1 pass builder from that run.
- The recovered vertex shader performs raw `dp3` operations between the authored
  tangent/bitangent/normal basis and its object-space light vector, then packs
  the result with `lightTs * 0.5 + 0.5`. The viewer's world-space equivalent
  removes only uniform placement scale. It does not normalize the three basis
  vectors, because their authored magnitudes are part of the interpolator.
  Decoded scene-node transforms now rotate/remove uniform scale while preserving
  each source vector's magnitude; this contract is persisted by decoded-mesh
  cache v60.
- The pixel shader unpacks both `NormalMap.rgb` and the interpolated light vector
  with `(x - 0.5) * 2`, performs a raw signed `dp3`, and computes
  `shade = AmbientColor + dp3 * PSLightColor` without normalization, saturation,
  a shadow sample, or a placed-light loop. SLS1009 outputs
  `BaseMap * shade`; SLS1013 outputs `BaseMap * vertexRgb * shade`. The A/AF
  variants only multiply output alpha by `AmbientColor.w`; vertex alpha is not
  an input to this family. The viewer retains its single common `ApplyFog`
  stage rather than replaying the SLS1010 fog permutation as another material
  combine.
- The retained audit classifier is deliberately narrow: ordinary static BS34
  `BSShaderPPLightingProperty` shader-type-1 geometry needs effective diffuse
  and normal paths plus finite, usable UV/T/B/N data at every decoded vertex.
  Alternate-texture overrides are evaluated first; shader type 29, raw
  skinned/single-pass flags, and specular/environment/parallax/glow/effect/LOD
  or malformed neighboring families fail closed. The decoded identity and
  vertex-color discriminator are persisted in cache v62. The classifier never
  activates the dormant PS1 family; the active ADT policy consumes it only
  behind the independent frame/draw gates below.
- A complete PC-final `Fallout - Meshes.bsa` census parsed 14,881/14,881 NIFs
  with zero errors and classified 4,696 audit candidates: 538 SLS1009 and 4,158
  SLS1013 identities across 1,772 assets. All candidate properties are BS34,
  whose layout structurally omits per-material Ambient/Diffuse RGB; all have
  zero emission and MaterialAlpha 1.0. The v60 transform audit has zero eligible
  rows above `1e-5` raw-vs-baked basis-length error, with a maximum of
  `2.3841858e-7`. These census counts describe the decoded classifier, not
  active-route submissions; runtime gates additionally reject alpha paths and
  other unsupported frame state.
- The legacy point pass is PS `SLS1001` / VS `SLS1003`. It unpacks the normal
  and a tangent-space cube-normal lookup, applies `dp3_sat`, multiplies the light
  color, then multiplies separate `AttMapXY` and `AttMapZ` samples. This differs
  from the viewer's analytic `saturate(1-d^2/r^2)` forward-light term, but it is
  also inside the unused PS1 family and must not be layered onto retail output.

### Active Retail PP-Lighting ADT Base

- The bounded shipped-tier route now implemented is pass ID 193 (`BSSM_ADT`),
  package013 `SLS2000`, for the zero-local-light base permutation. Eligibility
  requires FNV with lighting enabled and the ordinary static BS34/type-1
  classifier above; shader type 29 and raw skinned/single-pass inputs fail
  closed.
- Submission additionally requires zero uploaded placed lights, no projected
  sun shadow or fog, no alpha blend/test, material alpha exactly 1, and no
  material-alpha controller. Any failed gate stays on the combined viewer
  fallback. Telemetry distinguishes frame gates
  (`per-geometry-local-light-selection-unrecovered`,
  `projected-shadow-permutation-unrecovered`,
  `per-vertex-fog-interpolator-unrecovered`) from per-draw
  `outside-active-adt-base-subset`; dormant SLS1009/SLS1013 counters remain
  zero.
- `SLS2000` normalizes both decoded `NormalMap.rgb` and the complete authored
  T/B/N-transformed directional-light vector, preserves their raw signed dot,
  and computes `shade = max(AmbientColor.rgb + PSLightColor.rgb * dot, 0)`
  component-wise. RGB is `BaseMap.rgb * shade`, optionally multiplied by vertex
  RGB when `Toggles.x` selects that branch. This route has no bump scale, shadow
  sample, local-light loop, directional ambient cube, emission term, or
  `AmbientColor.w` factor.
- Decoder cache v63 retains the v62 strict material/vertex-color discriminator
  and additionally persists each submesh's stable source-shape block index.
  Warm v62 entries are invalidated because they cannot identify a shape for
  property-associated light observations. Profiler scenario `fnv-active-adt-base` pins
  the mixed Primm facade and passes 24/24 assertions, covering base and
  `Toggles.x` submissions, alpha-tested fallback neighbors, dormant legacy
  counters, zero locals, isolated post-processing/fog/shadows, and facade
  signal.

### Active Retail PP-Lighting Local-Light Oracles

- Active pass selection is also recovered for the first grouped local-light
  tiers. One property-associated local selects ID220 (`BSSM_ADT2`),
  SLS2008/SLS2011; two or three select ID143 (`BSSM_ADT4`), SLS2022/SLS2031.
  Both are opaque first passes with alpha blend/test off, depth writes on, and
  the retail greater-equal depth comparison. The viewer records these as CPU
  oracles only: `FnvActiveLocalLightOracle.RuntimeSupported` is false and no
  production draw can select either pass.
- ID220 maps the object-space point delta to
  `q.xyz = 0.5 * ((P - X) / radius) + 0.5`, with `q.w = 0.5`, and evaluates
  `a = 1 - Att(q.xy).r - Att(float2(q.z, 0.5)).r`. Its aggregate is
  `Ambient + SunColor * dot(N,Lsun) + LocalColor * dot(N,Llocal) * a`.
  The sun projection is normalized in SLS2008 before interpolation and consumed
  directly by SLS2011. The point direction is normalized before the authored
  T/B/N projection and again after interpolation. Both dots and `a` remain
  signed; only the complete aggregate is clamped component-wise at zero.
- ID143 instead has SLS2022 interpolate object position `X`; SLS2031 computes
  `qi = (Pi - X) / Ri` per pixel and uses the analytic signed attenuation
  `ai = 1 - dot(qi, qi)`. Its sun projection is likewise normalized in the
  vertex shader and not renormalized after interpolation. Each local direction
  is normalized in object space before the authored T/B/N projection, then
  normalized again after interpolation. Each local term is
  `LocalColor[i] * dot(N,Li) * ai`, accumulated with ambient and sun before the
  one final clamp. `EmittanceColor.w` is the slot gate: values greater than
  1/2/3 enable local slots 0/1/2; shipped ID143 uses 3 for two locals and 4 for
  three. Disabled-slot CPU diagnostics return explicit zero sentinels rather
  than claiming literal dormant shader intermediates.
- The PC-final ID220 attenuation source is reconstructed as a 128x128 byte
  table. For integer texel `(x,y)`,
  `d2 = min((abs(x-63.5)/63.5)^2 + (abs(y-63.5)/63.5)^2, 1)` and each RGB byte
  is `floor(255*d2)`. The center four texels are 0, `(32,63)` is 62,
  `(31,47)` is 84, and the edge/corners are 255. The CPU oracle generates these
  source texels but deliberately does not claim a bit-exact live sampler:
  clamp/clamp and filter mode 6 are identified, while final maximum-anisotropy
  policy and direct Xenon content/inversion evidence remain open.
- The Xbox geometry-bound influence equation is recovered. For local lights,
  `delta = NiLight.worldTranslate + globalSceneOffset - bound.center`,
  `surfaceDistance = length(delta) - bound.radius`, and the light is within the
  bound when `surfaceDistance < effectiveRadius`; the corresponding luminance
  score is `surfaceDistance / effectiveRadius`. The PDB declares
  `NiLight+0x10C` as `m_kSpec.r`, but FNV intentionally repurposes all three
  specular components: `TESObjectLIGH::GenDynamic` (Xbox VA `0x8234C288`)
  writes the same effective radius to `+0x10C/+0x110/+0x114`.
  `TESObjectREFR::GetRadius` (VA `0x8239B698`) supplies base LIGH DATA radius
  plus signed REFR ExtraRadius/XRDS, without multiplying XSCL. All recovered
  association callers use bound scale 1 and consume the original full float;
  only the separate `SetLightAttenuation(NiLight*, unsigned int)` copy is
  truncated to an integer.
- A full retail `FalloutNV.esm` inventory found 8,376 LIGH REFRs; 6,802 carry
  XRDS, including 2,259 negative corrections. All negative corrections still
  produce positive effective radii. REFR `0x0011A1F9`
  `VFSSouthGateFloodlightREF` is the scale-sensitive gate: base radius 1500,
  XRDS -500, XSCL 0.84, retail effective radius 1000. The viewer now preserves
  signed XRDS in parsed, descriptor, direct-world, and runtime/DMP paths and
  applies the recovered FNV rule; other games retain their existing radius
  behavior until independently recovered.
- Candidate collection applies that bound predicate without a pre-attachment
  cap. Its preliminary qsort is descending by cached score, but each lighting
  property's final `ResortLights` pass re-evaluates the submitted geometry
  bound and performs a stable ascending insertion sort—lower scores are
  promoted and equal scores retain attached-list order. The active non-shadow
  traversal then preserves that order while requiring frustum-cull byte not
  `0xFF`, NiLight flag bit 0 clear, and `bCastShadow != 1`.
  `RenderPass::SetLights` snapshots the resulting pointers in argument order;
  the two/three-light cap belongs to later pass construction, not association.
- The ID220/ID143 prepared point-light color is also recovered. Starting from
  `signedRgb = (Negative ? -1 : 1) * DATA.color / 255`, retail uses
  `d = HDR ? DATA.fade : min(DATA.fade, 1)` (an upper clamp only), then computes
  `rgb = signedRgb * d * property.fForcedDarkness * light.fLODDimmer`. A point
  light is hard-replaced with black whenever `fForcedDarkness < 1`. Output W is
  the separate `fShadowLODDimmer` and does not scale RGB. Sunlight, interior,
  minimum/separate ambient, skin, and material modifiers do not enter this
  local-point RGB path. The CPU oracle pins negative Fade, Negative-light sign,
  HDR/non-HDR, forced-darkness, and both LOD channels without claiming that the
  viewer can yet supply the live property/light values.
- `FnvRetailLightAssociationOracle` now pins the exact equation, strict boundary,
  stable final order, no-cap behavior, and active filter as a CPU-only oracle.
  `RuntimeSupported` remains false: the viewer still lacks the retail candidate
  sources and proven world-light, scene-offset, and geometry-bound inputs needed
  to reproduce membership.
- Decoder/cache v63 now carries `SourceBlockIndex` through to the CPU-side
  cached submesh. A separate telemetry-only association contract keys
  `(geometry REFR FormID, source-shape block index)` and structurally separates
  unknown, proven-empty, and known-ordered emitter REFR lists. It accepts any
  positive list length without truncation, cannot drive rendering, and has no
  production consumer. The camera-nearest frame-global viewer list is expressly
  not evidence for this contract.

### Authored Enable State and Effect-Collision Boundaries

- Placed enable state is per REFR. Main-record flag `0x00000800` supplies the
  placement's own Initially Disabled state; `XESP` supplies an enable-parent
  REFR and bit 0 requests the opposite parent state. Resolution spans the full
  loaded cell set, applies every inverse edge, and uses an actual visited set so
  malformed self/multi-node loops fail disabled without the former depth-16
  parity artifact. The selected-reference inspector's session-only
  `Authored / Shown / Hidden` preview applies to viewer-supported placed meshes
  and LIGH emitters. It now reaches reference pixels and shadows, picking,
  placed-light emission, embedded NIF water, walk collision, and the collision
  overlay; independent layer/category/lighting/water switches still win. It does
  not simulate later quest/script state changes.
- The renderable Hoover Dam gate is REFR `0x0015E4A5` in `HooverDamExtMid`:
  MSTT `FXFireMed01`, model `Effects\Ambient\FXFireMed01.NIF`, normally linked
  to authored-disabled `VHDBattleEffectsMarker` `0x0015D98C`. Authored state
  hides it, `Shown` reveals the drawable reference, and `Hidden` suppresses it. The
  older `0x0017A277` example was only a SOUN reference with no `MODL` and is not
  used as renderability evidence.
- Collision lookup now carries two independent decisions: authoritative resolution
  (mesh/none/unresolved) and cold-warmup eligibility. Authored Havok wins over
  category/path exclusions. Lightweight node-lifetime state can republish an
  independently evicted collision entry from the exact resident variant without a
  second GPU upload; terminal decode failures retain OBND but stop consuming warmup
  slots. Visual fallback is still keyed by plain path rather than material-swap
  variant, and Windows forced-eviction validation remains active in
  `docs/backlog/shared-collision-walk.md`.
- Retail collision fixtures pin both sides: `NVLimestoneDustStormHalfViz.NIF`
  and `IndFXLightRaysRight01.NIF` contain no authored Havok and remain
  non-solid, while `effects\box03.nif` retains its authored 16-vertex,
  17-triangle soup. `CliffVerti_C2.NIF` remains warmup-eligible despite its
  placements' degenerate OBND and retains its 819-vertex, 1,460-triangle Havok
  soup.

### Classic PP-Lighting Environment Mapping

- `BSShaderFlags` bit 7 is the FO3/FNV environment-mapping route. It is not the
  FO4/FO76 BGSM `_s` convention. The classic texture set uses slot 4 for the
  cubemap and optional slot 5 for a custom mask.
- PC-final `SLS2057.pso` (`SLS_PS2_ENVMAP`) samples the normal map, transforms
  the unpacked tangent-space normal, and reflects the eye vector into the cube.
  Its mask is `lerp(normalMap.a, customMask.r, EnvToggles.w)`, followed by
  multiplication by `EnvToggles.z` (the authored `EnvMapScale`). The resulting
  cube RGB is also multiplied by `AmbientColor.w`; the vertex-color permutation
  additionally modulates it by vertex color. There is no FO4 `_s.G` smoothness,
  geometry/Fresnel approximation, or explicit cube-mip selection in this pass.
- `SLS1032.pso`/`SLS1033.pso` confirm the same normal-alpha/custom-red mask
  selection in the PS1 environment family. PDB enum names make the direction
  split explicit: ordinary bit 7 selects `SLS2057` (`SLS_PS2_ENVMAP`); bit 21
  `BSSP_FLAG_WINDOWREFLECT` selects `SLS2058` (`SLS_PS2_ENVMAP_W`), whose first
  eye-vector normalize is negated; bit 17 `BSSP_FLAG_EYEREFLECT` selects the
  separate `SLS2059` (`SLS_PS2_ENVMAP_EYE`) material family and is excluded from
  the classic world-material route implemented here.
- A complete PC-final `Fallout - Meshes.bsa` census found 42,317
  `BSShaderPPLightingProperty` blocks. 4,379 bit-7 properties occur in 1,655 of
  14,881 NIFs; 4,148 populate slot 4, 3,607 populate slot 5, and 3,486 populate
  both. The Helios One solar-reflector row is the focused retail fixture: it
  authors `textures\effects\chrome_e.dds`,
  `textures\architecture\helios_one\Solar_Reflector_M.dds`, and scale 1.
  Of the bit-7 properties, 745 across 483 meshes also set bit 21 and therefore
  use the window reflection sign. All 23 bit-17 eye properties also carry bit 7;
  the explicit eye exclusion prevents them from being misrouted as world metal.
- The reference D3D12 path now carries classic cube/mask/scale as a distinct
  extraction, persistent-cache, residency, and shader payload. That payload was
  introduced in decoder v54 and remains present in the current v62 format.
  Slot-5 red replaces normal alpha only for this route. FO4 retains its existing
  BGSM cube plus `_s.R/_s.G`, smoothness mip, and geometry-term behavior.
  Packed `TextureState` uses bits 6/7 for classic route/custom mask and bit 9 for
  the window direction; bit 8 remains reserved for the classic parallax route.

### Classic PP-Lighting Simple Parallax

- The safe FO3/FNV simple-parallax route is narrowly identified by a
  `BSShaderPPLightingProperty` with `BSShaderFlags` bit 11 set, a populated
  texture-set slot 3, and bit 28 clear. Bit 28 is the distinct parallax-
  occlusion (POM) family and is deliberately excluded: its PC shader performs a
  multi-sample height walk and consumes the serialized POM parameters, so it is
  not interchangeable with the simple offset.
- PC-final `SM3004.pso` samples the height map once at the original material UV,
  computes `offset = height * 0.04 - 0.02`, transforms the unnormalized
  eye-minus-world vector by individually normalized tangent, bitangent, and
  normal rows, normalizes that tangent-space view vector, and uses
  `materialUv = originalUv + viewTS.xy * offset`. Diffuse/alpha and the normal
  map are then sampled at `materialUv`. The equation has no serialized scale
  multiplier.
- PDB names keep the two routes separate (`bParallax` versus
  `bParallaxOcclusion`) and identify the tail fields as
  `fParallaxOccMaxPasses` and `fParallaxOccScale`. The decoder records those
  fields only as audit evidence for the excluded POM family; the simple route
  does not feed them to the shader.
- A complete PC-final `Fallout - Meshes.bsa` census found 481 bit-11 properties
  with slot 3 across 255 of 14,881 NIFs: 464 are simple and 17 also set bit 28
  for POM. All 481 author four maximum passes; 467 store scale 1 and 14 store
  scale 20. None overlap the classic environment route through bit 7, slot 4,
  or slot 5. Thus none of the audited height maps competes with a classic slot-5
  mask for `TexIndices.z`; the remaining union occupant, FO4 `_s`, belongs to
  the separate external BGSM material family and cannot occur on these classic
  PP-lighting properties. Focused fixtures cover the Silver Rush simple
  material, a sulfur-cave material that also enables ordinary FNV specular, and
  the retaining-wall stair POM material that must remain excluded.
- The reference D3D12 path carries the height map through extraction, decoder
  v55 persistent cache, texture residency/readiness/release, and the shared
  reference pixel shader. Packed `TextureState` bit 8 selects parallax, while
  `TexIndices.z` is a state-discriminated union of the classic height map,
  classic environment mask, and FO4 specular map. Extraction requires usable
  UV/TBN data and the shader retains a per-pixel degenerate-basis guard. Both
  direct/blended and instanced reference draws use the same shifted diffuse,
  alpha, normal, and applicable specular sampling path.

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
- The world-view REFR/NIF shader path now includes the bounded FNV active
  ID193/`BSSM_ADT`/`SLS2000` base route in addition to classic Lighting30
  emission, FNV specular, environment mapping, and simple parallax.
  SLS1009/SLS1013 remain audit-only; materials outside the active ADT gates stay
  on the combined viewer fallback. Classic environment mapping uses the
  recovered slot-5-red/normal-alpha mask equation above; simple parallax uses
  the recovered one-sample SM3004 UV equation, while POM remains unsupported.
  This is not a claim of full PS2/PS3 multi-pass parity: the viewer remains a
  combined shader rather than a literal D3D9 pass replay.
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
  ordinary textured vertex-color passes. Outside the recovered active ADT
  branch, normal-map strength and combined Lambert lighting remain viewer
  approximations. The active branch instead uses the exact unscaled `SLS2000`
  decode/equation above and performs no green-channel flip.
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
- FNV active local-light runtime routing remains open even though the ID220/143
  CPU equations, PC attenuation source table, bound-influence equation,
  property-side sort/traversal order, effective radius, and prepared-color
  equation above are recovered. The zero-light gate currently sees the
  frame-global uploaded placed-light count because the viewer does not yet
  reconstruct each property's retail candidate set or provide the proven
  world-light, scene-offset, and geometry-bound inputs needed to evaluate its
  influence membership. Consequently any visible uploaded local light forces
  every classifier candidate to fail closed with
  `per-geometry-local-light-selection-unrecovered`, including geometry not
  associated with that light; this is a conservative false negative. Recover
  those association inputs, the per-geometry `fForcedDarkness` value, current
  `ShadowSceneLight.fLODDimmer`/writer semantics, batch splitting, and the final
  sampler policy before enabling positive local-light routes. The stable
  source-shape identity, tri-state observation model, association oracle, and
  prepared-color oracle are groundwork only; none is connected to production
  routing.
  Projected-shadow and per-vertex-fog permutations also remain deliberately
  fallback-only.
- Remaining terrain vertex-color variants, detail-map math, noise contribution,
  and final multi-pass lighting should be taken from PC shader bytecode first,
  then tied back to material/pass selection with Xbox-symbol decompilation and
  PC executable checks where needed. The bounded classic world-material
  specular, environment, and simple-parallax routes above now have direct
  PC-bytecode evidence. SLS1009/SLS1013 remain legacy-tier audit evidence only.
- `tools/ShaderProbe` now extracts Xbox shader records and their printable
  metadata. Xenos microcode disassembly would still be useful for cross-platform
  comparison, but it is optional for PC parity as long as the PC D3D9 shader
  record and render-state path can be recovered.
