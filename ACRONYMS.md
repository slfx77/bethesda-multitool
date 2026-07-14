# Acronyms & Domain Glossary

A reference for the abbreviations that appear throughout Bethesda Multitool's code and
comments. Many are Bethesda/Gamebryo file-format terms; others come from Xbox 360 reverse
engineering or the GPU renderer. This is orientation for newcomers — authoritative behavior
lives in the code.

## File formats & containers

| Term           | Meaning                                                                                                   |
| -------------- | --------------------------------------------------------------------------------------------------------- | ----------------------------- | -------------- |
| **ESM**        | Elder Scrolls Master — a Bethesda master plugin (base game data).                                         |
| **ESP**        | Elder Scrolls Plugin — a mod/add-on plugin (same record format as ESM).                                   |
| **BSA**        | Bethesda Softworks Archive — the loose-asset archive used through Skyrim LE (versions 0x100/103/104/105). |
| **BA2**        | The `BTDX` archive format used by Fallout 4, Fallout 76, and Starfield.                                   |
| **NIF**        | Gamebryo/NetImmerse model file (meshes, scene graph, materials, skinning).                                |
| **DDX**        | Xbox 360 tiled/swizzled texture format (`3XDO`/`3XDR`); converts to DDS for PC.                           |
| **DDS**        | DirectDraw Surface — the standard PC texture container.                                                   |
| **DMP**        | A `.dmp` memory dump (Xbox 360 minidump / crash dump) analyzed by the runtime readers.                    |
| **PDB**        | Program Database — Microsoft debug-symbol file; supplies struct layouts for reading DMP memory.           |
| **EGT**        | FaceGen Geometry/Texture data (per-actor head morph + texture coefficients).                              |
| **BIK**        | Bink video. **XMA**                                                                                       | Xbox 360 audio codec. **LIP** | lip-sync data. |
| **GLB / glTF** | Khronos 3D model interchange format that `export`/`render` emit.                                          |

## ESM record & subrecord codes

Four-character record/subrecord tags from the plugin format (a non-exhaustive set of the ones
that recur in comments):

| Tag                    | Meaning                                                                                            |
| ---------------------- | -------------------------------------------------------------------------------------------------- |
| **GRUP**               | Group — the container that nests records (TES4 engine family; Morrowind/TES3 has none).            |
| **REFR / ACHR / ACRE** | Placed object reference / placed actor / placed creature.                                          |
| **CELL / WRLD**        | Interior or exterior cell / worldspace.                                                            |
| **LAND**               | Landscape (terrain heightmap + texture layers) for a cell.                                         |
| **NAVM / NAVI**        | Navmesh geometry / the navmesh-info map that indexes navmeshes.                                    |
| **DIAL / INFO**        | Dialogue topic / a single dialogue response line ("dialogue" is this domain term, not a spelling). |
| **OFST**               | Offset table inside WRLD records (cell streaming layout).                                          |
| **TOFT**               | An Xbox 360-only streaming-cache region.                                                           |
| **FormID**             | 32-bit record identifier (load-order byte + 24-bit index).                                         |
| **HEDR / TES4**        | Plugin header record and its version/author/master-list data.                                      |

## Xbox 360 reverse engineering

| Term             | Meaning                                                                                   |
| ---------------- | ----------------------------------------------------------------------------------------- |
| **XEX**          | Xbox 360 executable format (the game binary loaded into Ghidra).                          |
| **XG**           | Xbox 360 GPU — used for its tiled/atlas texture memory layouts.                           |
| **VMX / VMX128** | The Xenon CPU's SIMD vector instruction set.                                              |
| **PPC**          | PowerPC — the Xbox 360 Xenon CPU architecture.                                            |
| **RTTI**         | Run-Time Type Information — C++ vtable/type metadata scanned to identify runtime structs. |
| **cvdump**       | Microsoft tool that dumps symbol/function addresses from a PDB.                           |

## Renderer / GPU (Direct3D 12)

| Term        | Meaning                                                                                |
| ----------- | -------------------------------------------------------------------------------------- |
| **SRV**     | Shader Resource View — a read-only binding of a texture/buffer to a shader.            |
| **PSO**     | Pipeline State Object — a compiled D3D12 graphics pipeline.                            |
| **MSAA**    | Multi-Sample Anti-Aliasing.                                                            |
| **VCLR**    | Vertex Colors (a NIF vertex-data channel).                                             |
| **LOD**     | Level of Detail (lower-resolution mesh variants).                                      |
| **FaceGen** | The facial-generation system that morphs a base head mesh from per-actor coefficients. |
