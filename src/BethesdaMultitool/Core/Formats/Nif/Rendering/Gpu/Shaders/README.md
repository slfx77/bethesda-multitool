# Shader sources

Every HLSL file under this tree is embedded with a **flat logical name**
(`BethesdaMultitool.Shaders.<filename>`, see the csproj `EmbeddedResource` item) and looked up by
bare file name in `GpuShaderCompiler12`. Consequences:

- **Directory position is semantically inert.** Subdirectories exist for humans; moving a file is a
  no-op at runtime. Tests resolve shaders the same way (`SourceContract.ReadShaderSource`).
- **File names must be globally unique** across all subdirectories — a collision is a build error
  (duplicate LogicalName) and a startup error (`GpuShaderCompiler12.BuildIndex`).
- `#include` directives are resolved by `EmbeddedShaderInclude` against the same flat index:
  `#include "atmosphere.hlsli"` works from any file at any depth, and any directory component in the
  include path is ignored. Headers live in `Include/` and must end in `.hlsli` (enforced by
  `ShaderInventoryTests.EveryIncludeDirectiveResolvesToAnEmbeddedHeader`).

## Naming convention

```
<subject>[_<gametoken>][<recovered-program-id>].<stage>.hlsl     entry shaders
<topic>.hlsli                                                    shared headers (Include/)
```

- **Stage suffixes**: `.vert` (vs_5_1), `.frag` (ps_5_1), `.comp` (cs_5_1), all with entry `main`.
  Historical GLSL-derived names, kept deliberately — they are load-bearing in
  `ShaderPermutations`, tests, and 12 call sites, and renaming them buys nothing.
- **Game tokens name games, not engine families** (the per-game-shaders directive; `BethesdaGame`
  is the only game axis): `morrowind`, `oblivion`, `fo3`, `fnv`, `skyrim`, `fo4`, `fo76`,
  `starfield`. Do NOT use `tes4` — in this repo `Tes4` means "Oblivion **and later**"
  (`EngineFamily.Tes4`), a different set. No game token = shared across games.
- **Recovered-program ids** (e.g. `001` from retail `WATER001`) are the per-variant axis where a
  disassembled retail shader is the source of truth.
- **Preprocessor macros encode technique axes only** (`WATER_HARDWARE_OCCLUSION`,
  `ALPHA_TO_COVERAGE`, `SHADOW_CARD_LIGHT_FACING`) — never game identity. Per-game differences get
  per-game *files*, selected by a `ForGame` registry returning a `GameShaderPair`
  (see `GrassShaderProfile`).

## Layout

| Directory | Contents |
|---|---|
| `Include/` | Shared `.hlsli` headers (atmosphere cbuffer, fog, shadow sampling, scene lighting) |
| `Reference/` | Placed-object shaders (`reference.*`, instanced variant, shadow pass) |
| `Grass/` | Per-game grass pairs (`reference_grass_oblivion.*`) |
| `Water/` | Per-game water pixel shaders (`water_fnv` also Skyrim/default, `water_oblivion`, `water_fo4`, `water_morrowind`, retail-program `water_fnv001`; selected by `WaterProfile.PixelShaderFile`, shared plumbing in `Include/water_common.hlsli`) + shared VS + noise/simulation compute |
| `Terrain/` | Landscape (`terrain_textured.*` live; `terrain.*` legacy, kept compiling) |
| `Sky/` | Sky dome geometry + celestial billboards |
| `Post/` | Tonemap + bloom |
| `Overlay/` | Debug overlays (cell grid, collision lines, `triangle.*` device smoke test) |
| `Sprite/` | Headless CLI sprite renderer (SKIN2000.pso replica) |

Adding a per-game shader pair: create `<subject>_<gametoken>.{vert,frag}.hlsl` beside the shared
subject, return a `GameShaderPair` from the subject's `ForGame` registry (structural fallback =
shared path; `GameShaderPair.TryCompile` is fail-soft — a compile failure logs and degrades to the
shared shaders), give the consuming pipeline factory a `ShaderRoutePsos` field + Set method (grass
in `ReferencePipelineFactory12` is the template), and add `ShaderPermutations` entries —
`ShaderInventoryTests` fails until coverage exists. Water deliberately does NOT use this pattern:
it has no shared-path fallback; `WaterProfile.PixelShaderFile` is its per-game seam.
