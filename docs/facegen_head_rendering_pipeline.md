# FaceGen Head Rendering Pipeline

## Status: PARTIALLY VERIFIED — core decompilation claims audited, conclusions still under review (2026-03-18)

This document traces the engine's head rendering pipeline from decompiled Xbox 360 code.
Most low-level claims cite a specific decompiled function and line range. Some higher-level
semantic conclusions were stronger than the raw evidence and are now called out explicitly.

**Decompilation sources** (regenerated 2026-03-18):
- `tools/GhidraProject/facegen_texture_bake_decompiled.txt` — 12 functions, texture bake path (Xbox 360)
- `tools/GhidraProject/facegen_textures_decompiled2.txt` — 16 functions, shader binding + orchestrator (Xbox 360)
- `tools/GhidraProject/facegen_memdebug_decompiled.txt` — 65 functions, comprehensive FaceGen pipeline (Xbox 360)
- `tools/GhidraProject/facegen_geck_bake_assembly.txt` — Full ASM of bake accumulator + FREGT003 parser (GECK x86)
- `tools/GhidraProject/facegen_geck_texture_bake_candidates.txt` — 8 functions, GECK bake path
- `tools/GhidraProject/facegen_geck_egt_generation_2.txt` — 8 functions, EGT generation orchestrator
- `tools/GhidraProject/facegen_geck_egt_generation_3.txt` — 9 functions, morph generation + morph context
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
- The earlier `0x20` family is now somewhat clearer. `FUN_00869fa0` / `FUN_0086b150` manage a
  `0x20`-stride record array, and `FUN_0086b370` deep-copies one record by copying the first
  scalar field directly and then delegating the remaining `0x1C` tail to `FUN_00417d90`. That
  lower helper behaves like a short-string/range copy utility rather than a TRI-specific decoder,
  and the follow-up `FUN_00695400 -> FUN_00694da0` path simply clears or resets the copied
  small-string-style tail for each `0x20` record. So this family still looks like name/metadata
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
- That `FUN_008754d0` step still is not TRI-specific section decoding. It first asks
  `FUN_00870350` to allocate a bounded header-range buffer, then copies a validated subrange into
  reader-owned storage via `FUN_008756d0`, with `FUN_008756a3` just unwinding the helper frame.
  `FUN_008752c0` is the reusable lower-level primitive that reads typed element/block payloads from
  the binary stream once the header stage has succeeded.
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

6. **Our mesh/runtime path is still missing the shipped `.tri` family entirely.** The sample
   assets confirm that `FRTRI003` files exist alongside the real head, eye, mouth, teeth, tongue,
   and many hair/head-part NIFs, and our production code never loads them.

7. **The remaining error is not yet localized.** The current best hypothesis is a difference in
   per-morph weighting or encode-side treatment of stronger negative deltas, not a broad channel
   swap, container-format issue, or NPC-specific lookup bug.

This pipeline is much better understood than it was before the decompilation work, but it should
not be treated as fully closed. Any claim that the remaining mismatch is "just DXT1 noise" is
stale.
