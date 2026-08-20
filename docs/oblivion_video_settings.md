# Oblivion video settings — engine grounding for the viewer's Video section

Source: `tools/GhidraProject/run_decompile_oblivion_video_settings.py` (PyGhidra over
`Oblivion.exe` v1.2.0.416; output `tools/GhidraProject/oblivion_video_settings_decompiled.txt`,
regenerable). The compiled-in defaults below were read from the SettingT static initializers
(value global = name-string xref − 4) and cross-checked against `Oblivion_default.ini`.
The viewer's per-game applicability table is `VideoSettingsProfile.ForGame`
(`src/BethesdaMultitool/Core/Formats/Nif/Rendering/VideoSettingsProfile.cs`); the UI is the 3D
viewer settings panel's **Video** expander.

## The launcher triple (Screen effects radio)

| Engine setting | Default | Viewer mapping |
|---|---|---|
| `bDoHighDynamicRange:BlurShaderHDR` | 1 | Radio **HDR** → `GpuTonemapSettings.ForOblivionWeather` (EngineTes4Defaults) |
| `bUseBlurShader:BlurShader` | — (`!HDR && this` = launcher "Bloom") | Radio **Bloom** → `GpuTonemapMode.ClassicSdrBloom`: SDR clamp + bright-pass bloom at neutral exposure. Retail's LDR `[BlurShader]` topology is UNRECOVERED (the section ships no bright-pass trio at all) — this is a documented stand-in |
| neither | — | Radio **None** → LegacyClamp (FO3/FNV keep their standalone cinematic grade) |
| `fSunlightDimmer:BlurShader` = 1.0 vs `:BlurShaderHDR` = 1.3 | | HDR-off states neutralize scene-side HDR multipliers (matches the parallel `[BlurShader]` set) |

## Water ([Water] section — all defaults byte-confirmed)

| Engine setting | Default | Viewer mapping |
|---|---|---|
| `bUseWaterReflections` | **1** | **Water reflections** toggle. ON = mirrored SCENE pass (terrain + captured references + sky about the dominant visible plane) consumed by the WATER007 projective arm in `water_oblivion.frag.hlsl`; OFF = the WATER003/013 RT-free additive composition |
| `bUseWaterReflectionsMisc/Statics/Trees/Actors` | **0/0/0/0** | Retail's DEFAULT reflection content is land + sky only. The viewer's mirror includes statics/trees (the maxed ini set); grass and decals are excluded (no grass ini exists at all) |
| `bUseWaterDisplacements` | **1** | **Water ripples** toggle (USER ruling: mapped to the animated surface normal field; OFF = flat-normal calm sheet). Retail semantics are the wading displacement sim — unimplemented |
| `bUseWaterDepth` | 1 | Already implemented (scene-depth shore fade; `uDepthRange=125` byte-confirmed) |
| `bUseWaterLOD` | 1 | Far-field flattening: the 8192-unit ripple attenuation converges to WATER013's LOD sheet |
| `fSurfaceTileSize` | 2048 | Feeds the mesh UV (t6 → DisplacementMap in WATER007); the NormalMap tile is the VS-hardwired 4096/3 |
| `uSurfaceTextureSize/FrameCount/FPS` | 128 / 32 / 12 | The synthesized surface animation honors all three |
| `fTileTextureDivisor` | 4.75 | Real key; acts in the (unrecovered) water-grid mesh UV generation, NOT between the WATER000 samplers |

## Display / shadows / windows

| Engine setting | Default | Viewer mapping |
|---|---|---|
| `bDynamicWindowReflections:Display` | 1 | **Window reflections** toggle → `ReferenceRenderer12.WindowReflectionsEnabled` suppresses the classic env-map term (FNV/FO3 bit-21 window glass AND authored NiTextureEffect sphere maps). ⚠ Retail TES4 authors ZERO NiTextureEffect blocks (all 9,612 BSA NIFs scanned) — the engine ATTACHES the effect at runtime, so on unmodded Oblivion the toggle currently has no visible retail target; the decode/shader pipeline serves Morrowind's heavily-authored effects (glass/chrome now reflect) and modded content. Synthesizing the runtime attachment for retail TES4 windows = open follow-up (needs the attachment-site decompile) |
| `bShadowsOnGrass:Display` | 1 | **Shadows on grass** toggle → grass RECEIVES the sun shadow map (diffuse term only, ambient never attenuated — the GRASS2002 rule). Grass never casts, matching retail |
| `bDoCanopyShadowPass:Display` + `iCanopyShadowScale=512` | 1 | **Tree canopy shadows** toggle → tree canopy geometry casts into the viewer's real cascaded shadow map (stands in for retail's projected canopy textures); `ReferenceRenderer12.TreeShadowsEnabled` |
| `bDrawShadows:Display` | 0 | Retail draws no dynamic object shadows by default; the viewer's full shadow map is an intentional upgrade gated by the existing Shadows master toggle |

Shared reach (`VideoSettingsProfile`): FO3/FNV expose the full set (identical launcher block +
classic materials); Skyrim/FO4/76 expose the tonemap selector + water rows; Morrowind and
Starfield hide the section.
