# FNV PC blend framebuffer model and mirrored-matrix culling, reverse-engineered (2026-09-27)

Written for the Blender writer's display-space blend (the framebuffer model its bounds assume) and for billboards
phase 2 (how the engine draws the mirrored matrix of NiBillboardNode mode 5). The image is `TestOutput/fnv-runtime-dump-20260927/FalloutNV_runtime_image.bin` (gitignored, local to the BMT worktree): the in-memory FalloutNV.exe 1.4.0.525 module of the vanilla Steam depot build 1510068, read with ReadProcessMemory at the main menu (flat, offset = RVA, base 0x400000, 17,281,024 bytes, SHA-256 a99059f0...809e), verified by RE-20's NiRotKey::GenInterp signature (one hit, VA 0xA28740). Each item was investigated by one agent and checked by a second, independent agent (workflow `wf_e764cd68-bff`); receipts are under `TestOutput/fnv-runtime-re-20260927/<topic>/` and `<topic>/verify/`.

| Item | Question | Verdict |
|---|---|---|
| Framebuffer | the render target, sRGB state, shader saturation and HDR path that alpha-blended world geometry draws into, HDR on and off | confirmed-with-corrections |
| Mirror cull | how the renderer culls and lights geometry under a mirrored (determinant -1) world matrix | confirmed-with-corrections (one retail claim refuted) |

## Framebuffer model

### Investigator

#### Question

The Blender display-blend work assumes that Gamebryo PC blends on display-encoded values in an 8-bit UNORM target, with
no sRGB write and a saturated pixel-shader output. Under that assumption a render-state blend gives `clamp(k0 + k1*Cd)`
per channel. A review flagged this as inferred, and the owner runs with `bDoHighDynamicRange=1`.

For the passes that draw alpha-blended world geometry (NiAlphaProperty blending), with HDR on and HDR off, this answer
establishes:

1. the render-target formats and where they are created;
2. whether D3DRS_SRGBWRITEENABLE or D3DSAMP_SRGBTEXTURE is ever set true;
3. whether blending into a float target can exceed 1, and how the tone-map maps the result to the back buffer;
4. whether texture colour is sampled raw.

**Sources and method.** Everything was read from the dumped runtime image
(`fnv-runtime-dump-20260927/FalloutNV_runtime_image.bin`, VA = 0x400000 + file offset). The only other inputs were the
installed shader package the owner's run used and the owner's `RendererInfo.txt` from the same session. The binary was
analysed by capstone disassembly, with the game's own functions emulated under unicorn. No build, Ghidra, Blender, game
launch or git write was used, and nothing outside this directory was modified.

#### Answer (the model)

The world, including every NiAlphaProperty-blended draw, renders into one offscreen main target, render-target
descriptor **ID 4**. This holds whenever `bImageSpaceEffects=1`, which is the owner's setting. That target's format is
the only thing the HDR switch changes.

**HDR on (the owner's game; live effective-HDR flag `[0x11F941E]` = 1):**

- **Target.** ID 4 is **D3DFMT_A16B16G16R16F** (113), 2560×1440, drawn through a 4-sample MSAA surface of the same
  format and resolved into an FP16 texture.
- **Blending.** The FP16 target blends in half-float with BLENDOP ADD. The blend becomes
  `C <- Cs*Fs + C*Fd` with **no clamp**.
- **Source colour.** The world pixel shaders do not saturate. In shader package 013, 490 of 516 pixel shaders write
  unsaturated RGB to oC0. Values above 1 therefore survive from layer to layer.
- **Tone map.** The final HDR combine shader (ISHDRBLENDINSHADER) is **linear**:
  `out = C*E + max(bloom*0.5/m, 0)`, with `m = max(bloom.a, HDRParam.x)` and `E = HDRParam.x/m <= 1`.
  - It has no curve and no saturate.
  - Its result goes to 8-bit UNORM (the back buffer is X8R8G8B8), so the only clamp between the FP16 scene and the
    display is this final `clamp01` plus 8-bit quantisation.
- **Display.** After the back buffer comes the device gamma ramp: a display LUT with `fGamma` = 1.0, which does not
  enter blend math.

**HDR off (`[0x11F941E]` = 0), with ImageSpace still on:**

- **Target.** ID 4 is **D3DFMT_A8R8G8B8** (21) at the same size and MSAA. It is 8-bit UNORM.
- **Blending.** `C <- clamp01(clamp01(Cs)*Fs + C*Fd)`, quantised to 8 bits after every blend.
  - Neither clamp comes from the game: the shaders do not saturate. Both are the D3D9 behaviour of a UNORM render
    target.
  - Then comes the LDR ImageSpace chain to the X8R8G8B8 back buffer.
- **ImageSpace off.** With `bImageSpaceEffects=0` the world draws straight into the X8R8G8B8 back buffer. Blending is
  clamped the same way, but destination alpha is not stored.

**sRGB, both modes.** There is no sRGB anywhere:

- D3DRS_SRGBWRITEENABLE (194) is set only once, to 0, by the default-state table.
- D3DSAMP_SRGBTEXTURE (11) is never set; the sampler-state wrapper even discards it.
- No shader linearises texture colour.

So blending always operates on the stored, display-encoded texture values.

**What this means for the display-blend fits.**

- **HDR off.** The assumed model `clamp(k0 + k1*Cd)` per blend is correct. The only addition is 8-bit quantisation of
  each result.
- **HDR on (the owner's game).** The target is **not** clamped between layers:
  - each blend is `k0 + k1*Cd` with no clamp;
  - `k0 = Cs*Fs` may exceed 1;
  - one `clamp01(E*C + bloom)` happens at the end.
- **Where the two agree.** They agree exactly only when all of these hold:
  - the source and the base are at most 1;
  - there is a single layer;
  - E = 1, i.e. the adapted luminance is at or below the target;
  - bloom is about 0.
- **Where they differ.** They differ for:
  - over-bright sources (emissive, specular, additive stacks);
  - multi-layer stacks, where HDR clamps once and LDR clamps after every layer;
  - bright, eye-adapted scenes (E < 1);
  - bloom.
- **Recommendation.** A fit meant to reproduce the owner's game should drop the per-layer clamp. It should keep only the
  final clamp, after an exposure factor E (<= 1) and an optional additive bloom.

#### Evidence

##### (1) Target formats, where they are created, and which setting selects them

**Setting and effective flag.**

- The `bDoHighDynamicRange:BlurShaderHDR` Setting object is at 0x11C76C0; its live value is 1.
- Renderer init (Renderer.cpp, function 0x4DA670) probes `IDirect3D9::CheckDeviceFormat(0, HAL, X8R8G8B8, usage,
  D3DRTYPE_TEXTURE, A16B16G16R16F)` twice:
  - with `D3DUSAGE_QUERY_POSTPIXELSHADER_BLENDING` at **0x4DAE3D**, result in `[0x11C70EA]`, printed as "FP16ARGB
    blending";
  - with `D3DUSAGE_QUERY_FILTER` at **0x4DAE87**, result in `[0x11F941F]`.
- **0x4DB0A6–0x4DB0DE** sets `[0x11F941E] = bDoHighDynamicRange && FP16-blending-supported`. **HDR cannot run without
  FP16 post-pixel-shader blending.**
- 0xB4F7CB (shader-model setup 0xB4F710) forces it to 0 below PS/VS 2.0.
- The live flag is 1. The owner's `RendererInfo.txt` from the same session (copied here) reports "High dynamic range:
  yes", "FP16ARGB blending: yes", "Multisample Type: 4".
- The MSAA check runs against A16B16G16R16F (0x4DAA88 → 0x4DD180, which is
  `IDirect3D9::CheckDeviceMultiSampleType`). When HDR is on and FP16 MSAA is unsupported, 0x4DAB32 logs "Multisample
  setting [%i] is not supported…" and drops MSAA. The owner's file has no such line.

**Is the world drawn offscreen?**

- The main frame 0x8707C0 calls 0x872F50.
- If `[0x11F91A4]` is set (tested at 0x8709F0), it requests target **ID 4** through the pooled getter 0xB6E110 (call at
  **0x872FCC**, `push 4`). It stores the target in `[0x11F9438]` and binds it with 0xB6B8D0(7, group), which is
  `BeginUsingRenderTargetGroup` with a colour, depth and stencil clear.
- Otherwise it binds the renderer's default (back-buffer) group.
- The world render (0x875110 …) follows the bind.
- 0x874B90 hands the target to ImageSpace (0xB97550), and 0x876850 returns it to the pool.
- `[0x11F91A4]` is a copy of `bImageSpaceEffects:Display`, whose Setting value lives at 0x11F94E4. The copy is made at
  0xB4F984 and 0xB4F997, and the live value is 1.

**Format of every target: descriptor switch 0xB6C2C0.**

This 52-case switch returns (w, h, format, flags, MSAA) per target ID. It was emulated per ID (`emu_rt.py`) using the
game's own code, with a 2560×1440 back buffer:

| ID | size | format HDR=1 | format HDR=0 | MSAA | role |
|---|---|---|---|---|---|
| **4** | 2560×1440 | **A16B16G16R16F** | **0 → A8R8G8B8** | 4 | main scene (0x872FCC) |
| 9, 22 | 1024×1024 | 0 → A8R8G8B8 | 0 → A8R8G8B8 | 4 | water reflection (requested from TESWater.cpp, 0x4EB330) |
| 5 | 2560×1440 | A16B16G16R16F | A16B16G16R16F | 0 | full-res FP16 (user 0xB65C60, not classified) |
| 35, 36 | 2560×1440 | A8R8G8B8 | A8R8G8B8 | 4 | not classified |
| 0, 1, 2, 20 | sub-screen | A16B16G16R16F | A16B16G16R16F | 0 | not traced |

- The ID 4 case sets `flags = 0x22 | (msaa ? 0x40 : 0)` and `fmt = HDR ? 0x71 : 0`, at 0xB6C4F4–0xB6C510. This is the
  function's only read of `[0x11F941E]`, at **0xB6C502**.
- The pool pre-warm 0xB6E450 creates the HDR luminance chain only when HDR is on: three 1×1 targets, then 4×4, 16×16
  and 64×64, all in 0x71.

**Format 0 → A8R8G8B8.**

- The creator 0xB6D170 turns a non-zero format into an override: it sets `[0x11F4AC0]` = 1 and `[0x11AB3C8]` = fmt
  around the call.
- Otherwise the texture is created with FormatPrefs {TRUE_COLOR_32, SMOOTH alpha, MIP_DEFAULT}.
- In `NiDX9RenderedTextureData::Create` (0xE8FE10), those prefs select rendered-texture format slot 5 (0xE89ED0 over
  renderer+0x758).
- That table is filled by 0xE6DFC0 with usage = RENDERTARGET. Slot 5 is built from the game's NiPixelFormat template at
  0x11AAAC8, which the game's own mapper 0xE7BD20 turns into **A8R8G8B8** (`emu_pf.py`).

**Creation calls.** In 0xE8FE10, with MSAA:

- `IDirect3DDevice9::CreateRenderTarget` is called at **0xE8FEEF** (vtable 0x70) with (w, h, fmt, MultiSample,
  quality 0, not lockable).
- `CreateTexture` is always called, at **0xE8FF16** (vtable 0x5C), with (w, h, 1 level, D3DUSAGE_RENDERTARGET, fmt,
  D3DPOOL_DEFAULT).

**End-to-end emulation.**

- `emu_factory.py` runs descriptor → factory 0xB6D5E0 → creator 0xB6D170 → 0xB6B610 and stops at
  `NiRenderedTexture::Create` (0xA7FC00).
- `emu_rtd.py` runs 0xE8FF60 → 0xE8FE10 with a fake device.

For ID 4:

- **HDR=1**: override on with fmt 113, which produces `CreateRenderTarget(2560x1440, A16B16G16R16F, 4 samples)` and
  `CreateTexture(2560x1440, A16B16G16R16F, usage 0x1, pool 0)`.
- **HDR=0**: override off with prefs (2,2), which produces the same two calls with **A8R8G8B8**.

**Back buffer.**

- `NiDX9Renderer::Create` is called at 0x4DAA4C with eFBFormat = `[0x1189470]` = 3 (statically initialised).
- The game's mapper 0x4DCBE0, emulated for all 18 enum values (`emu_fbfmt.py`), maps 3 to **D3DFMT_X8R8G8B8**.
- The depth format is `[0x1189474]` = 75 (D24S8).

##### (2) sRGB states

**D3DRS_SRGBWRITEENABLE (194).**

- The `NiDX9RenderState` default table at 0x11BFCE8 (103 pairs, applied by slot 22 at 0xE88000) sets 194 = **0**. The
  device default is also FALSE.
- No code path sets 194. Each of the following was checked:
  - **Cached setter.** The cached SetRenderState (slot 26, 0xE88780; cache at `[this + state*8 + 0x120]`) has 197
    call-shaped sites. Their immediate states include 190, 191, 192 and 195, but **never 194** (`rscensus.py`).
  - **Direct device calls.** Direct `Device::SetRenderState` sites: none with 194. The non-immediate ones are other
    classes' vtables or the state-cache internals.
  - **Render-state groups.** Bethesda shader passes register their states into `NiD3DRenderStateGroup` lists through
    0xE7F430: 1,953 calls using 25 distinct states, **none 194** (`groupcensus.py`). Their BLENDOP registrations are all
    1 (ADD). The 3 calls through the pass wrapper 0xB71A10 set states 19, 20 and 168 only.
  - **State tracker.** Bethesda's render-state tracker table (0x11FFA30) leaves 194 untracked (-1).
  - **Byte search.** A 5-byte `push 0xC2` occurs only twice in `.text` (0xBC426A is a Havok call; 0xE5B7C9 is an assert
    line number).

**D3DSAMP_SRGBTEXTURE (11).**

- The sampler wrapper (slot 51, 0xE910A0) remaps the type through the word table 0x126F92C and **returns without
  calling the device** when the slot is 5 or more.
- The static initialiser at 0xE885E1 fills that table with 0xFFFF and maps only ADDRESSU, ADDRESSV, MAGFILTER,
  MINFILTER and MIPFILTER (types 1, 2, 5, 6, 7). Type 11 stays 0xFFFF in the live table.
- The sampler default loop (0xE88060, table 0x11C0538) sets only types 1, 2, 5, 6 and 7.
- The immediate census of direct device SetSamplerState sites finds types 1, 2, 5, 6, 7, 8 and 10, never 11.
- The two non-immediate sampler-offset sites (0xA8F0A0, 0xBE0826) are not device calls.

##### (3) Float blending and the tone map

**Blend state for NiAlphaProperty.**

- `UpdateAlphaState` (slot 3, 0xE87B60) sets:
  - ALPHABLENDENABLE (flag bit 0);
  - SRCBLEND and DESTBLEND, indexed by flag bits 1–4 and 5–8 through the table at `[this+0x20]`;
  - the alpha-test states.
- The constructor 0xE881A0 fills that table as ONE, ZERO, SRCCOLOR, INVSRCCOLOR, DESTCOLOR, INVDESTCOLOR, SRCALPHA,
  INVSRCALPHA, DESTALPHA, INVDESTALPHA, SRCALPHASAT.
- BLENDOP stays at ADD, the init-table default of 1, and SEPARATEALPHABLENDENABLE stays 0. The only immediate BLENDOP
  write outside groups (0xB7CF63) takes its value from an ImageSpace effect parameter.

**Shader outputs are not saturated.**

- Shader package 013 is the one RendererInfo names; it parses exactly (1,007 records, 1,380,772 of 1,380,772 bytes).
- Following each oC0 RGB component back to its last writer (`satrgb.py`):
  - 490 of 516 pixel shaders are unsaturated, 25 are saturated, 1 is partly saturated.
  - The world families are mostly unsaturated: SLS 186/198, PAR 28/33, SKIN 13/13, SM 50/50, NOLIGHT* 7/7,
    PARTICLE 1/1, GDECAL 1/1, WATER 38/38.
- Packages 002 and 019 behave the same (`satrgb_sp002/019`).

**Consequence per target.**

- On the FP16 target, source and blended values above 1 are kept, up to the half-float range. This is D3D9
  floating-point render-target semantics.
- On the A8R8G8B8 target, the output merger clamps to [0, 1] and stores 8 bits.

**HDR chain.** The following was disassembled from the package, with CTAB names (`sdis.py`):

- ISHDRBRIGHT: `max(C - HDRParam.x, 0) * HDRParam.y`.
- ISHDRDOWN* and ISHDRDS*: weighted downsamples.
- ISHDRLUMCLAMP and ISHDRDS*LUMCLAMP: clamp the RGB vector length to `HDRParam.x`.
- ISHDRADAPT and ISHDRDS*ADAPT: `lerp` with weight `1 - pow(HDRParam.z, TimingData.z)`, length clamp `HDRParam.w`.
- ISHDRBLUR: a 15-tap blur.
- Every pass writes alpha = `BlurScale.z`.

**Final combine (ISHDRBLENDINSHADER).**

```
L = max(Src0.a, HDRParam.x);  E = HDRParam.x / L
out.rgb = DestBlend.rgb * E + max(Src0.rgb * 0.5 / L, 0);  out.a = BlurScale.z;  mov oC0 (no _sat)
```

- DestBlend (s1) is the scene and Src0 (s0) is the blurred bloom.
- The CIN variants add the ImageSpace "Cinematic" saturation/contrast/brightness, Tint and Fade. These are affine per
  channel, applied before the output.
- The engine loads these effects by name at 0xB900D6 and 0xB900EE ("HDRBLENDINSHADER" and its "T" variant, ps_2_a).
- The HDR parameter block, as read by the `PrintHDRParam` console command (0x5C9A00), is: fEyeAdaptSpeed +0x0,
  fEmissiveHDRMult +0xC, fTargetLUM +0x10, fUpperLUMClamp +0x14, fBrightScale +0x18, fBrightClamp +0x1C.
- Where the chain meets 8 bits: the chain ends in the X8R8G8B8 back buffer. All full-size targets are 8-bit (A8R8G8B8 or
  format 0 → A8R8G8B8) except IDs 5 and 32 (FP16) and 33 (R32F).

**Gamma.** `fGamma:Display` (live 1.0) builds a 256-entry D3DGAMMARAMP and calls `IDirect3DDevice9::SetGammaRamp(0,
D3DSGR_CALIBRATE, ramp)` at 0x4DD119. This LUT applies after the frame buffer and does not enter blend math.

##### (4) Texture colour is sampled raw

- SRGBTEXTURE is never set true (see (2)), so samplers return the stored, display-encoded values.
- In the shaders:
  - no `def` constant is a gamma exponent (2.2, 1/2.2, 2.4 or 1/2.4). The only near misses are grass's 0.4 and the
    DOF/motion-blur sample weights (`gammacensus.py`).
  - every `pow` exponent in SLS, PAR, SM and SMLL is a named specular/glossiness constant ("Toggles",
    "ToggleNumLights", "fVars") or a literal 30, 2 or 3 (`powsrc.py`).

#### Controls (each had to be able to fail)

| # | control | result |
|---|---|---|
| C1 | Force the HDR byte 1→0 and re-run the descriptor for all 52 IDs (`perturb_rt.py`) | only **ID 4** changes (113 → 0); the other 51 IDs are unchanged |
| C2 | Perturb iWaterReflectWidth, Height and MultiSamples | exactly IDs 9 and 22 change; ID 4 does not (the attribution method is specific) |
| C3 | Factory emulation on control IDs | ID 5 stays FP16 and ID 35 stays A8R8G8B8 in both modes; only ID 4 flips override on/off |
| C4 | Device-level emulation | CreateRenderTarget and CreateTexture formats flip 113 ↔ 21 for ID 4 only |
| C5 | Emulate the pixel-format mapper on all 22 table slots | reproduces all 20 populated Gamebryo slots; an enum misreading would mismatch |
| C6 | Emulate the FBFMT mapper for 0..17 | the ordered map UNKNOWN, R8G8B8, A8R8G8B8, X8R8G8B8 … confirms that 3 = X8R8G8B8 |
| C7 | `push 0xC3` (DEPTHBIAS, the neighbour of 194) as a positive control | 42 hits in FNV against 2 unrelated `push 0xC2`; the immediate census sees 190–192 and 195 but not 194 |
| C8 | `_sat` decoder positive control | 1,877 temp writes decoded with `_sat` (package 013); 2 oC0 `_sat` writes found in 019 (PRECIP), so a saturated output is detectable |
| C9 | Shader package parse | exact tiling, 1,380,772/1,380,772 bytes, 1,007 records |
| C10 | Sampler remap: live table against static init code (0xE885E1) | agree; type 11 is never written |

#### Cross-checks

**Fallout 3 (`Fallout3_PC.exe`).** Same design (`xcheck_pc.py`):

- The same FP16 caps probe at 0x6BA4E5; the blending result goes to `[0x1223BC6]`.
- The same effective flag `[0x131A3D0] = bDoHighDynamicRange([0x1104B20]) && FP16 blend`, at 0x6BA63B–0x6BA664.
- The same descriptor case at 0xB79A17: `flags = msaa ? 0x62 : 0x22`, `fmt = HDR ? 0x71 : 0` (cmovne).
- The same pre-warm of 1×1 0x71 targets at 0xB7948D.
- The same default-table tail (193, -1), (194, **0**), (195, 0), at 0x1202368.
- Its three `push 0xC2` hits are two assert/log line numbers and a Havok call.

**FNV GECK.** Same HDR select at 0x905872 (flag `[0xF23E6E]`) and same table tail (194 = 0) at 0xECACC0. Its
`push 0xC2` hits are a Win32 EM_REPLACESEL, a Havok call and an assert.

**FNV X360 MemDebug.** Checked by symbol names only (`globals.txt`):

- `BSShaderManager::bHDR`, `BSRenderedTexture::Create`, `BSTextureManager::CreateRenderedTexture`.
- `EFFECT_SHADER_HDR_BLENDINSHADER` (plus `_CINEMATIC` and `_ALPHAMASK`) and the HDR downsample, lum-clamp and
  light-adapt set.
- `HDR_PPARAM_HDR_PARAM`, `HDR_PPARAM_BLUR_SCALE`, `HDR_TARGET_LUM`, `HDR_UPPER_LUM_CLAMP`.

This is the same architecture. The X360 target formats (EDRAM) and microcode were not compared.

#### Confidence

- **(1), high.**
  - The ID 4 formats come from emulating the game's own code from descriptor to the D3D calls, both HDR states.
  - The live flags come from the dump and agree with the owner's RendererInfo.txt.
  - Fallout 3 has the identical case.
  - That NiAlphaProperty draws go into ID 4 is medium-high. The main-frame bind to the world render to the ImageSpace
    order was read, but individual draw calls inside the window were not traced.
- **(2), high.** There are five independent searches, each with a positive control.
- **(3), high for the shader math, the formats and the absence of `_sat` and curves.** The per-frame exposure E is not
  quantified (see Open).
- **(4), high at the sampler.** At shader level it is high for pow and gamma constants; an `x*x` approximation was not
  searched.

#### Open

1. **The value of E per frame.** It is not determined which engine values fill `HDRParam` (c1) and the bloom alpha
   (`BlurScale.z`) for the combine pass. The constants are uploaded by register, not by name. The IMGS field order and
   shader usage suggest (TargetLUM or BrightClamp, BrightScale, EyeAdapt, UpperLUMClamp), but that is not proven.
   Consequently, "E = min(1, TargetLUM/adapted)" is plausible, not established. What is established is that E ≤ 1 for
   HDRParam.x > 0 and that the combine is linear.
2. **The combine's own render target.** Whether the combine writes straight to the back buffer or to an A8R8G8B8
   ImageSpace target was not traced. Both are 8-bit UNORM, so the clamp location relative to later LDR effects is
   open.
3. **Unclassified full-resolution targets.** Users of IDs 5, 31, 32, 35, 36 and 47 were not classified. The 0x20
   requester is 0xB63C60, 0x1F is 0xBD9380 and 5 is 0xB65C60. If any of them receives blended world geometry (for
   example first-person, refraction or VATS), its format is given in `emu_rt.out.txt`. IDs 5 and 32 are FP16 even
   with HDR off.
4. **D3D9 UNORM and FP16 clamping.** That a UNORM target clamps source and result to [0, 1], and that an FP16 target
   clamps neither, is Direct3D 9 API and hardware semantics. It was not measured from the game.
5. **Destination alpha.** Its content in ID 4 was not traced. COLORWRITEENABLE defaults to 7 (RGB only) in the init
   table, and 56 group registrations set 0xF. It matters only for DESTALPHA and INVDESTALPHA factors.
6. **Gamma ramp formula.** The ramp builder's formula (0x4DCF0A–0x4DD0E3) was not read. It is assumed to be identity
   at fGamma = 1.0.
7. **Alpha-blended draws after ImageSpace.** Draws that might happen after ImageSpace (UI, some effects) were not
   enumerated.

#### Files (this directory)

**Loaders and helpers.** `imgload.py`, `fbdis.py`, `callers.py`, `rtti.py`, `sweep.py` (+ `sweep_fnv/fo3.*`).

**Emulation.**

- `emu_rt.py`: descriptor, all IDs.
- `perturb_rt.py`: C1 and C2.
- `emu_factory.py`: through `NiRenderedTexture::Create`.
- `emu_rtd.py`: D3D creation calls.
- `emu_pf.py`: pixel-format slots.
- `emu_fbfmt.py`: back-buffer format.

**State censuses.**

- `rscensus.py`: SetRenderState and SetSamplerState immediates.
- `groupcensus.py`: NiD3DRenderStateGroup registrations.

**Shaders.**

- `sdp.py`: package reader and token walker.
- `sdis.py`: disassembler with CTAB.
- `satcensus.py`, `satflow.py`, `satrgb.py`: saturate census.
- `gammacensus.py`, `powsrc.py`: gamma check.
- `sdis_hdr*.out.txt`: HDR shader listings.

**Cross-check.** `xcheck_pc.py`: FO3 and GECK.

**Receipts.**

- `receipt.py` → `receipt.json`: live values plus the collected results.
- `RendererInfo.owner-20260927-0952.copy.txt`: a copy of the owner's file.
- `*.lst.txt`: listings of 0xB6C2C0, 0xB6D170, 0xB6E450, 0x872F50, 0xE6DFC0 and 0xE8FE10.

### Verifier

Subject: `../ANSWER.md` (the investigator's answer). Verdict: **CONFIRMED WITH CORRECTIONS.** The model holds:

- **HDR on:** the world blends unclamped into an FP16 target, and one clamp comes at the end.
- **HDR off:** the world blends clamped into an A8R8G8B8 target.
- **Both modes:** there is no sRGB anywhere, and texture colour is sampled raw.

The corrections below are labelling and precision fixes. None of them changes the model.

#### Method

Everything was re-derived from the same runtime image. Its SHA-256 is `a99059f0…809e`, which matches `dump-receipt.json`. I did not use the investigator's scripts, classifiers or emulation drivers. Everything in this directory is my own:

| Script | What it does |
|---|---|
| `vimg.py` | Image loader. |
| `vscan.py` | One capstone linear sweep of all 4,134,733 instructions in `.text`. It records every register-indirect call whose target was loaded from `[obj+0x28/0x2C/0x40/0x54/0x5C/0x70/0x74/0x88/0x94/0xE4/0x114]`, every operand that uses immediate 0xC2 or 0x71, and every reference to the HDR globals. |
| `vargs.py` | Recovers pushed arguments backward from each call. A call counts as COM-shaped (`this` pushed as arg 0) when the pushed `this` matches the vtable source. |
| `vimm.py` | Census of render-state immediates (171, 168, 206–209, 194). |
| `vxref.py` | Finds direct call xrefs. |
| `vdis.py`, `vfn.py` | Listings. |
| `vemu_pf.py` | My own unicorn driver for the game's pixel-format mapper. |
| `vsdp.py` | Shader-package reader. It uses **Microsoft's `D3DDisassemble` (d3dcompiler_47.dll, via ctypes)** as an independent disassembler, instead of a hand-written token decoder. |
| `vsat.py` | oC0 saturate census. |
| `vlin.py` | Linearisation check. |
| `vhdr.py`, `vhdr_cross.py` | A small ps_2_x interpreter that runs the HDR combine shaders. |

Receipts: `verify_receipt.json` (live values and key listings), `listings.txt`, `*.json`, `*.out.txt`.

#### Claims

##### 1. The HDR switch and the effective flag: CONFIRMED

- **The Setting.** Object 0x11C76C0 (`bDoHighDynamicRange:BlurShaderHDR`) has a live value of 1.
- **The two probes.**
  - 0x4DAE3D is `CheckDeviceFormat(0, HAL, X8R8G8B8, 0x80000 POSTPIXELSHADER_BLENDING, RTYPE_TEXTURE, 0x71)`, and its SUCCEEDED result goes to `[0x11C70EA]`.
  - 0x4DAE87 is the same call with usage 0x20000 (FILTER).
- **The flag.** At 0x4DB0A6–0x4DB0DE, `[0x11F941E] = setting && [0x11C70EA]`.
- **The shader-model override.** 0xB4F7B7–0xB4F7E8 zeroes the flag on both branches of the shader-model < 2 path, and only there.
- **Live values.** Effective HDR is 1, FP16-blend caps is 1, and MSAA is 4. These match `iMultiSample=4`, `bDoHighDynamicRange=1` and `bImageSpaceEffects=1` in the owner's `FalloutPrefs.ini`, which I read directly.
- **MSAA probe.** 0x4DAA9B pushes 0x71 and calls 0x4DD180 (the multisample probe). The unsupported-case string 0x1021518 is referenced at 0x4DAB8D.

##### 2. The world, including NiAlphaProperty draws, renders into target ID 4 when bImageSpaceEffects=1: CONFIRMED (medium-high, not traced per draw)

**The frame function** 0x8707C0 runs this sequence:

1. It calls 0x872F50.
2. The gate 0x8709F0 returns `[0x11F91A4]`.
3. If the gate is set, the pooled getter 0xB6E110 is called at 0x872FCC with ID 4. The target is stored to `[0x11F9438]` and bound with 0xB6B8D0(7, group) at 0x8730F0.
4. The world render 0x875110 is called at 0x87093D with that target as its fourth argument.
5. Inside the world render, the **same** target is re-bound with flag 6 (depth/stencil only) at 0x8758B5.
6. The post-world ImageSpace hand-off 0x875FD0 runs only when the gate is set.
7. 0x876850 returns the target to the pool.

**The else branch** (0x873109) binds the renderer's default back-buffer group.

**The gate value.** `[0x11F91A4]` is set to `[0x11F94E4]` (bImageSpaceEffects) at 0xB4F984 and 0xB4F997, and reset to 1 at 0xB5458F.

**Discriminating control.** I enumerated every ID requester:

- the 23 direct callers of the getter 0xB6E110;
- the 24 callers of the acquire wrapper 0xBA3840.

ID 4 is requested only at 0x872FCC. Every other requester asks for a different ID, so the attribution is specific.

##### 3. ID 4 is A16B16G16R16F with HDR on and A8R8G8B8 with HDR off, 2560×1440, 4× MSAA: CONFIRMED

**Descriptor.** The jump table at 0xB6D048 sends case 4 to 0xB6C4A5. At 0xB6C4F4–0xB6C510:

- `flags = 0x22 | (msaa ? 0x40 : 0)`;
- `fmt = -(HDR != 0) & 0x71`.

0xB6C502 is the only read of `[0x11F941E]` in the whole descriptor function (from the global census). The live `[0x11F9490]` is 4; 0xB6C200 maps it to 4 samples, so the flags are 0x62.

**How format 0 becomes a real format.**

- The creator 0xB6D170 raises the override only when fmt ≠ 0: it sets `[0x11F4AC0]=1` and `[0x11AB3C8]=fmt` at 0xB6D1E8–0xB6D1F7.
- `NiRenderedTexture::Create` copies the override to +0x41/+0x44 (0xA7FCDD–0xA7FCF0).
- 0xE8FEB0 uses `[tex+0x44]` only when `[tex+0x41]` is set.
- Otherwise the format is the renderer hint of the slot that 0xE89ED0 picks. For FormatPrefs (2,2,2), that is slot 5 (0xE89FC5), falling back to slot 4.

**Mapper emulation (`vemu_pf.py`).** I ran the game's own mapper 0xE7BD20 under unicorn on every template that 0xE6DFC0 copies:

- slot 5 (0x11AAAC8, B8 G8 R8 A8 normalised) gives **21 = A8R8G8B8**;
- slot 18 (four half-float channels) gives **113**.

*Control:* all 20 populated slots map to the format their channel layout predicts: X1R5G5B5, R5G6B5, X8R8G8B8, A1R5G5B5, A4R4G4B4, A8R8G8B8, DXT1/3/5, V8U8, L6V5U5, X8L8V8U8, L8, A8, R16F, G16R16F, A16B16G16R16F, R32F, G32R32F and A32B32G32R32F. A misread enum or slot would break that pattern.

**Device calls.**

- `CreateRenderTarget(w, h, fmt=edi, MS, 0, 0)` at 0xE8FEEF, through vtable +0x70.
- `CreateTexture(w, h, 1, usage, fmt=edi, pool)` at 0xE8FF16, through vtable +0x5C.

**A detail the investigator did not mention.** The factory 0xB6D5E0 replaces format 36 (A16B16G16R16) with 0x71 when its caps byte is clear (0xB6D716–0xB6D71B). This does not affect ID 4.

##### 4. The back buffer is X8R8G8B8 and the depth buffer is D24S8: CONFIRMED

- `[0x1189470]` = 3. It is only read, by the getter 0x4DC270, and nothing in `.text` writes it.
- In the mapper 0x4DCBE0, case 3 goes to 0x4DCC13, which returns 0x16 (X8R8G8B8).
- `[0x1189474]` = 75 (D24S8).

##### 5. D3DRS_SRGBWRITEENABLE is never set true: CONFIRMED (one mislabel corrected)

**The default table.**

- Table 0x11BFCE8 in `.data` holds 103 pairs, including (194, **0**) at 0x11BFFB0.
- Only two code sites write into the table: 0xE911E0 drops states 190–192, and 0xE915F0 edits state 7 only.

**Device call sites.**

- The only device-level `SetRenderState` sites (COM-shaped, vtable +0xE4) are the five in NiDX9RenderState: 0xE8804C (default loop), 0xE887BC (cached setter, slot 26), 0xE887F2 and 0xE88818 (states 19 and 20), and 0xE88852 (state 27).
- The other COM-shaped hits (0x457AFE, 0x4ACAD4, 0x65BCEB) are thiscall C++ calls.
- None of the 93 +0xE4 call sites passes 194.

**Immediate and data search.**

- All 36 instructions carrying immediate 0xC2 were listed. None is `mov reg, 0xC2`.
- There are only two `push 0xC2`: 0xE5B7C9 is a Havok assert line (string `hkpConstraintChainUtil.cpp`), and 0xBC426A is covered by correction 1.
- `.rdata` and `.data` contain no (194, 1) pair.
- No string anywhere in the image contains "srgb".
- There is no D3DX effect framework: no `D3DXCreateEffect*` import exists.

**Positive control.** The same immediate scan finds BLENDOP (171) 9 times and COLORWRITEENABLE (168) 136 times.

##### 6. D3DSAMP_SRGBTEXTURE is never set: CONFIRMED

**The wrapper drops type 11.**

- The wrapper (slot 51 of vtable 0x10F088C, function 0xE910A0) returns without a device call when `remap[type] >= 5`. The sibling 0xE890C0 does the same.
- In the live remap table 0x126F92C, entry 11 is 0xFFFF. Only types 1, 2, 5, 6 and 7 are mapped.
- The only writes to the table are the static initialiser at 0xE885E1–0xE8863A.

**Device calls.** Direct device `SetSamplerState` calls (vtable +0x114) set these types only:

| Type | Call sites |
|---|---|
| 8 | 0xB7C1E6, 0xB98951 |
| 10 | 0xBADD2F, 0xBADE26, 0xE7EAF3 |
| 1, 2, 5, 6, 7 (the default table 0x11C0538) | 0xE88167 |
| 6, 5, 7, 1, 2 | 0xEC04E9–0xEC0545 |

No device call sets type 11. The wrapper's own device call is at 0xE910E3.

##### 7. The NiAlphaProperty blend state is ADD with factors from a fixed table: CONFIRMED

**UpdateAlphaState** (0xE87B60, slot 3) sets these states through slot 26:

- 27 (ALPHABLENDENABLE);
- 19 (SRCBLEND) = `tbl[(f>>1)&15]`;
- 20 (DESTBLEND) = `tbl[(f>>5)&15]`;
- 15, 25 and 24 (alpha test).

It never sets BLENDOP or SEPARATEALPHABLENDENABLE.

**The factor table.** 0xE8834E–0xE88376 fills it with 2, 1, 3, 4, 9, 10, 5, 6, 7, 8, 11: ONE, ZERO, SRCCOLOR, INVSRCCOLOR, DESTCOLOR, INVDESTCOLOR, SRCALPHA, INVSRCALPHA, DESTALPHA, INVDESTALPHA, SRCALPHASAT.

**Defaults.** BLENDOP = 1 (ADD), 206 = 0, 168 (COLORWRITEENABLE) = 7.

**Every immediate BLENDOP use:**

| Site | What it is |
|---|---|
| 0xC00D6F, 0xC00F4F, 0xC0A854, 0xC0A8F8 | Group registrations (0xE7F430) of (171, **1**, 1) |
| 0xB7CF5C | ImageSpace; the value is a variable |
| 0x76CE4F, 0x7EAE89, 0x7EAF3F | A UI Tile value (0x700320 → `SetFloat` 0xA012D0), not a render state |

##### 8. The world pixel shaders do not saturate oC0: CONFIRMED

This census uses Microsoft's disassembler and my own last-writer walk (`vsat.py`) over package 013.

**The package.** It tiles exactly (1,007 records, 1,380,772 bytes). Those records hold 514 unique pixel-shader names, because 4 names are duplicated.

**Results.**

| Class | Count |
|---|---|
| Saturated | **25** |
| Unsaturated, computed | 454 |
| Pass-through (a texture or constant written straight to oC0) | 33 |
| Partial | 2 |

- The partial pair is ISMAP, which uses `min` against a constant ≤ 1, and ISNOISESCROLLANDBLEND.
- My 25 saturated shaders are the investigator's 25 family for family: SLS 16, PAR 5, STB 1, PRECIP 2, ISTV 1.
- World families with no saturated shader at all: SM (74), SKIN (13), NOLIGHT* (7), PARTICLE, GDECAL, WATER (38), GRASS (28).

**Controls.**

- The detector does find `_sat` (all 25 saturated shaders, including PRECIP).
- The plain HDR combine is classed as unsaturated.

##### 9. The HDR combine is linear with no curve and no saturate, and E ≤ 1: CONFIRMED WITH CORRECTION

**The shader.** Microsoft's disassembly of ISHDRBLENDINSHADER (`hdr_combine_d3ddisasm.txt`) matches the investigator's formula exactly:

```
m = max(Src0.a, HDRParam.x);  E = HDRParam.x / m
out.rgb = DestBlend.rgb * E + max(Src0.rgb * 0.5 / m, 0);  out.a = BlurScale.z;  mov oC0 (no _sat)
```

**The interpreter test (`vhdr.py`).** I ran the shader in my ps_2_x interpreter while varying the scene value C from 0 to 8:

- the deviation from an affine map is 0.0 for the plain, CIN and CINAM variants;
- outputs reach 4.4, so the shader itself applies no clamp;
- a hand-computed value matches to 1e-9.

*Control:* a Reinhard curve, C/(1+C), deviates by 3.11 under the same test.

**Correction.** CIN and CINAM are **affine in RGB but not per channel** (`vhdr_cross.py`):

- The saturation lerp and the tint both go through the luminance dot product (0.299, 0.587, 0.114).
- At a non-identity cinematic, raising only the scene's R channel moves out.G by +0.153 and out.B by +0.132.
- At the identity cinematic (saturation 1, tint weight 0, contrast 1, brightness 1, fade 0), CIN equals the plain combine exactly.

**What stays inferred.** That s1 (DestBlend) is the scene and s0 (Src0) is the bloom comes from the CTAB names; I did not trace the binding.

##### 10. The gamma ramp is a display LUT outside the blend math: CONFIRMED, and investigator Open item 6 is closed

**How the ramp is built.** At 0x4DCF4E–0x4DD0E3, for R, G and B:

```
ramp[i] = trunc(pow(i/255, g) * 65535 + 0.5)
```

- `pow` is reached through 0x4DD130 → 0x4DD150 → 0xEC9AC0, which takes two doubles.
- The constants are 255.0 (0x101E568), 65535.0 (0x10217D0) and 0.5 (0x1011588).
- g is `[0x118945C]`, live 1.0.

**Result.** At g = 1 the ramp is `i*257`, which is the identity.

**The call.** It is `SetGammaRamp(0, D3DSGR_CALIBRATE, ramp)` at 0x4DD11C, through vtable +0x54. The builder's only caller is 0x870073.

##### 11. Texture colour is sampled raw: CONFIRMED, and the investigator's "x*x not searched" gap is closed

- **At the sampler.** SRGBTEXTURE is never set (claim 6).
- **In the shaders.** `vlin.py` follows every `texld` result forward to any `pow`, `log`, or `mul`/`mad` of a value by itself.
  - It finds 264 such uses. All are on single channels (alpha, gloss, heights), with 15 exceptions.
  - The 15 exceptions are vector squares in SM3* shaders on sampler s1, whose CTAB name is **NormalMap**.
  - None is on a BaseMap colour.

*Positive control:* the package holds 239 `pow` and 18 `log` instructions, all visible to the detector.

##### 12. The Fallout 3, GECK and X360 cross-checks: CONFIRMED (one mislabel repeated)

**Fallout 3:**

| Item | Address |
|---|---|
| FP16-blend probe | 0x6BA4E5 |
| `[0x131A3D0] = [0x1104B20] (bDoHighDynamicRange:BlurShaderHDR) && [0x1223BC6]` | 0x6BA63B–0x6BA664 |
| Descriptor case (`fmt = HDR ? 0x71 : 0`, `flags = msaa ? 0x62 : 0x22`) | 0xB79A17–0xB79A3D |
| Default table containing (194, 0) | 0x1202368 |

**GECK:**

- Descriptor case at 0x905872 (flag `[0xF23E6E]`, `and eax, 0x71`).
- Default table at 0xECACC0.

**X360.** The `globals.txt` names are present: `BSShaderManager::bHDR`, `EFFECT_SHADER_HDR_BLENDINSHADER*`, `HDR_PPARAM_*`, `HDR_TARGET_LUM`, `HDR_UPPER_LUM_CLAMP`. Note that the X360 runtime also carries its own D3D gamma machinery (`D::Gamma_sRGB`, the PWL gamma functions). That platform differs and was not compared.

#### The model after verification

**HDR on (the owner's game).**

- Blending is `C <- Cs*Fs + C*Fd` into FP16, with no clamp between layers. The shaders do not saturate, the factor table is fixed and the op is ADD.
- The display gets `clamp01(Cinematic(E*C + bloom))`, quantised to 8 bits, followed by an identity gamma ramp.
- Cinematic is an affine map in RGB. It is the identity when there are no ImageSpace modifiers.

**HDR off (ImageSpace on).**

- Each blend is `clamp01(clamp01(Cs)*Fs + C*Fd)`, stored in 8-bit A8R8G8B8.

**For the fits.**

- With HDR on, drop the per-layer clamp and keep one final clamp.
- The single-layer agreement case also needs the cinematic at identity, besides E = 1 and no bloom.

#### Corrections

1. **0xBC426A `push 0xC2` is not a Havok call.**
   - It builds a Bethesda shader render-pass record. 0xBA9EE0 and 0xBA8EC0 allocate a 16-byte {geometry, u16 pass id = 0xC2, …, light-pointer array} from the small-block pool.
   - The same builder takes pass ids 0x71, 0xAB, 0xAC, 0xB7, 0xCE, 0xCF and 0xD1 from the shader code at 0xBA0000–0xBDFFFF, which references BSShader constant names.
   - The same mislabel applies to Fallout 3's 0xBF16E3 (builder 0xB84F70) and the GECK's 0x957AAA (builder 0x909B60).
   - The conclusion, that no SetRenderState of 194 exists, is unchanged.
2. **The CIN and CINAM combines are not "affine per channel".** They are affine in RGB, and saturation and tint mix channels through luminance. A per-channel display-blend fit matches only at saturation 1 and tint weight 0.
3. **0x874B90 is not the post-world ImageSpace hand-off.**
   - Before the world render, and only when `[0x11DED3C]` is set, it runs ImageSpace effect index 0x22 into the scene target. 0xB97550 dispatches `effects[arg1]->Render` at 0xB975B6/0xB975C2.
   - The post-world hand-off is 0x875FD0, gated by `[0x11F91A4]`.
4. **Shader counts.** Package 013 has 514 unique pixel-shader names; 516 counts duplicates. By unique name, the split is 487 without a saturate (454 computed, 33 pass-through), 25 saturated and 2 partial. ISMAP, which clamps with `min(x, c ≤ 1)`, was counted as unsaturated. This does not change the conclusion.
5. **The single-layer agreement condition** in "What this means for the display-blend fits" needs one more term: the cinematic at identity. It is inside the final clamp.
6. **Open item 6 is resolved.** The gamma ramp is `trunc(pow(i/255, fGamma)*65535 + 0.5)`, which is the identity at fGamma 1.0.
7. **The "x*x not searched" gap is resolved.** No shader squares a base-map colour.

#### Still unsettled

- **Individual NiAlphaProperty draws** were not traced into ID 4. The claim rests on the frame's bind, the world render call, the re-bind and the hand-off order.
- **E per frame** is not known: which values fill `HDRParam` and `Src0.a`. The s0/s1 roles come from CTAB names.
- **The combine's own render target** is not known. It could be the back buffer, an A8R8G8B8 ImageSpace target, or the FP16 ID 5.
- **The D3D9 clamp behaviour** (a UNORM target clamps and an FP16 target does not) is API semantics. It was not measured in the game.
- **Unclassified full-resolution targets** (IDs 5, 31, 32, 35, 36, 47) and any alpha-blended draws after ImageSpace were not traced.
- **Details I did not re-verify:** the HDR parameter-block offsets read by `PrintHDRParam` (0x5C9A00), and the users of IDs 5, 0x1F and 0x20.

## Mirror cull

### Investigator

Binary: the FalloutNV.exe 1.4.0.525 runtime image dumped on 2026-09-27
(`fnv-runtime-dump-20260927/FalloutNV_runtime_image.bin`, image base 0x400000, file offset = RVA, sha256 a99059f0…).
All addresses below are VAs in that image unless marked X360. The X360 cross-checks use the PDB-named 2010-8-22
MemDebug build. Its procedure list comes from `cvdump -s` and is kept in `x360_20100822_memdebug_procs.txt`.

#### Question

RE-25 showed that NiBillboardNode mode 5 gives the node a world rotation with determinant −1:
F = [[−y, x, 0], [x, y, 0], [0, 0, 1]], which is a mirror.

1. List every D3DRS_CULLMODE (22 = 0x16) write in the renderer and what selects its value.
2. Who sets and clears the render state's left/right-swap flag? Does any code compute a world-matrix determinant, or
   test for negative scale, per geometry?
3. What does a single-sided card look like in game, lit or normal-mapped, compared with a both-sided unlit one? Do
   tangent frames or lighting use the world matrix in a way that the mirror would flip?

#### Answer

1. **No cull-mode path in FNV looks at a geometry's transform.**
   - Every CULLMODE value the game writes is one of three things:
     - a constant: CW, or NONE for certain shader passes;
     - the table value `T[drawMode][swap]`;
     - the Bethesda-shader rule: NONE if the NiStencilProperty draw mode is BOTH or the current pass is a strip particle
       pass, otherwise `T[CCW][swap]`.
   - The table: CCW_OR_BOTH → CW/CCW, CCW → CW/CCW, CW → CCW/CW, BOTH → NONE/NONE (swap 0 / swap 1).
2. **The swap flag is cleared once, by the constructor, and nothing in the game ever sets it.**
   - It is Gamebryo's switch for a horizontally mirrored *projection*: the camera setup negates the projection's x
     scale when it is set, and the cull table flips to compensate.
   - It is not a per-object handedness flag, and FNV never turns it on.
   - No code tests a world-matrix determinant or a negative scale on the way to the cull state:
     - only two functions in all of .text compute a 3×3 determinant, and neither is on the render path;
     - the emulated cull writers read no geometry memory.
3. **Single-sided card:** it is culled when seen from its authored front.
   - The mirror reverses the screen winding of every triangle. The camera-facing front face then fails the default
     CULLMODE CW, and a flat card disappears. This holds lit or unlit, normal-mapped or not.
   - Faces authored to point away from the camera are drawn instead ("inside-out"). They are shaded by their own
     away-pointing normals, so they look lit from behind.
   - **Lighting is not flipped.** Light and eye vectors reach model space through the *transposed* rotation
     (NiTransform::Invert) or D3DXMatrixInverse, and both are exact for a mirror. N·L and tangent-space terms therefore
     match the mirrored geometry exactly.

   **Both-sided card (stencil draw mode BOTH → CULLMODE NONE on every path):** drawn as a left-right mirror image of
   the proper rotation, with consistent lighting and normal-map detail.
   - All 24 retail mode-5 primitives are of this kind: `nukagrenadeexplosion01`, LiaFlameMesh14-25, LE and BE, all
     draw mode BOTH and unlit (RE-25 `verify/v_mode5_mat.out.txt`).
   - So the only retail effect of the mirror is the flipped image.

#### Evidence

##### 1. Every CULLMODE write path

**Objects, found by RTTI.**
- NiDX9RenderState vtable `0x10F088C` (66 slots):
  - slot 9 `0xE87EF0` ApplyStencil;
  - slot 19 `0xE88750` GetLeftRightSwap;
  - slot 20 `0xE88760` SetLeftRightSwap;
  - slot 26 `0xE88780` SetRenderState(state, value, arg3, lock).
- SetRenderState keeps a cache at `+0x120 + 8*state` and a lock byte per state at `+0x920 + state`, and calls
  IDirect3DDevice9 `+0xE4` on the device at `+0x10F8`.
- NiDX9Renderer vtable `0x10EE4BC` (115 slots): slot 71 `0xE757E0` and slot 72 `0xE757F0` forward to render-state
  slots 19 and 20 through `[this+0x8B8]`.
- The renderer singleton is `[0x11F9508]`. More render-state holders are `[0x126F714]` and `[0x126F6C8]` (`mc_7`).

**Enumeration by two independent methods, which agree.**
- `mc_1_cullwrites.py` decodes from heuristic function starts. `mc_24_sweepcheck.py` does a full linear sweep of .text.
- Both find **173 `push 0x16` sites** that reach a call.
- Exactly **12** of them are shaped like a CULLMODE write: 3 render-state slot-26 calls and 9 state-list entries.
- **0** sites write CULLMODE straight to the device.
- The other first-argument-0x16 calls go to non-render-state functions (`mc_21_a2targets.out.txt`). 0x4A0E90 only
  returns the renderer pointer; the 0x16 pushed before it is the argument of the next call, 0xB6E110, a switch over
  values 9..0x2B on another manager object (`[0x11F91A8]`), not a render state.
- Only two `mov r32, 0x16` sites exist, and both are unrelated.
- Calls whose state argument is not an immediate were checked separately (`mc_4`, `mc_2`). They are group-apply and
  restore loops, and the default-list writer.

**The write paths.** Values: 1 NONE, 2 CW, 3 CCW. T is the table built by the constructor.

| # | Writer | Value | Reached from |
|---|---|---|---|
| W1 | NiDX9RenderState::ApplyStencil `0xE87EF0` (write at `0xE87FEF`, lock 0) | `T[(flags>>10)&3][swap]` | slot 2 UpdateRenderState `0xE88BB0` with property state +0x10. Shader SetupGeometryConstants at `0xB7DD39`, `0xB8A173`, `0xBBF594` and `0xBC9CB0`, only when the stencil property's enable bit 0 is set (`mc_10`). X360 names these Lighting30Shader, ShadowLightShader, BSShaderNoLighting and SkyShader ::SetupGeometryConstants, and they have the same bit-0 gate (`mc_9_x360_*`). |
| W2 | `0xE879B0` SetCull(drawMode, lock) (write at `0xE879D2`) | `T[drawMode][swap]` | only from `0xB984D0` = BSRenderState::SetCullMode (X360 name, same shape), which passes lock 0 and ignores its 2nd argument. On the X360 that argument feeds a lock counter; the PC build has no counter. |
| W2a | ↳ `0xBE20E0` = BSShader::SetupGeometryRenderStates (X360 name, same shape) | drawMode 3 if stencil `(flags & 0xC00) == 0xC00` **or** `[0x11F91E4]` (BSShaderManager::eCurrentPass) ∈ {0x56 BSSM_NOLIGHTING_STRIP_PSYS, 0x57 …_SUBTEX}, else 1 | per geometry, Bethesda shaders |
| W2b | ↳ `0xB99E20` and `0xB9A090`, the batch renderer. X360 BSBatchRenderer::RenderNextRenderPass and RenderBatches make the same calls. | constant drawMode 1 or 3 per batch group | batch groups |
| W3 | slot 64 `0xE911E0` re-initialisation (write at `0xE9129B`) | constant 2 (CW) | device re-init |
| W4 | slot 22 `0xE88000`: default list `0x11BFCE8` → device, cache and restore value `+0x124` | 2 (CW), list entry 8 (`mc_20`) | init |
| W5 | state lists: `0xE7F430` SetRenderState(state, value, bSave), applied by `0xE7EED0` (lock 0) or `0xBE0EB0`→`0xB97D90` | 1 (NONE) in all 9 entries | Entries at `0xBC131E`, `0xBC4D1F`, `0xBC57D0`, `0xC01DA9`, `0xC01F1C`, `0xC073B1`, `0xC0A6FA` and `0xC0A799` hold the immediate 1. The entry at `0xBE2427` holds `edi`, which is 1 from `0xBE23BB` on the fall-through path. X360 shows 8 such entries (value 0 = Xenon NONE): Bolt, Beam, VolumetricFog, ShadowLight velocity passes and Lighting30Shader::PresetStages (`mc_18`). |
| W6 | restores: group restore `0xE7EF40` and switch `0xB98610` (state 0x16 → case 21 → `0xB98757`) | `+0x124` = CW, ignoring swap | pass cleanup |

- The table comes from the real constructor bytes. `mc_15` E1 emulates `0xE881A0`→`0xE88415`:

  | draw mode | swap 0 | swap 1 |
  |---|---|---|
  | 0 CCW_OR_BOTH | CW | CCW |
  | 1 CCW | CW | CCW |
  | 2 CW | CCW | CW |
  | 3 BOTH | NONE | NONE |

- The swap flag at `+0xF4` is 0 after construction.
- The CULLMODE lock is never taken: every CULLMODE call passes lock 0 (`mc_4`: 94 of the 106 SetRenderState-shaped
  calls it collected pass a literal 0).
- Within the render-state code, the lock bytes are written only by SetRenderState itself (`mc_3` section 4).

##### 2. The swap flag, determinants and negative scale

**Writers of `+0xF4`** (`mc_3`):
- the constructor at `0xE8840B` (sets 0);
- slot 20 at `0xE88769` (sets it to the bool argument).

**Callers of slot 20.**
- Following every load of a render-state holder forward (92 loads), `mc_7` finds slot 20 called only by the renderer's
  slot 72 wrapper `0xE757F0`.
- `0xE757F0` is referenced only by the NiDX9Renderer vtable (`0x10EE5DC`), with no direct call (`mc_5`).

**Callers of renderer slot 72 (+0x120).**
- All 17 `+0x120` virtual call sites in .text were typed (`mc_8`). They are made in BSFaceGenAnimationData (on itself
  and its members), BSTreeModel, Actor/Character slot 129 (on the actor's `+0x68` process object), HighProcess and
  MiddleHighProcess `this`-calls (their slot 72 is the no-op `0x4534F0`), a game object at `0x6057E4`, a shader-array
  walk at `0xB55813`, and shader-pass lists (`0xB8B3DF`, `0xB97520`, `0xBB3326`).
- Several pass 0 or 2+ arguments. The setter takes one.
- None of these objects is the renderer.

**X360 (named).**
- `NiXenonRenderer::SetLeftRightSwap` (0x8285EBF0) writes `renderState(+0x718)->+0xF4`.
- It has **0** `bl` callers, and so does `GetLeftRightSwap`.
- There are 32 `+0x124` and 18 `+0x120` virtual-call-shaped sites. None of them has the renderer
  (`NiRenderer::ms_pkRenderer` = 0x83327548) as its object; they are shader `this`-calls and geometry and game objects
  (`mc_6`, `mc_6b`).
- The string table holds no mirror, cull-mode or left/right-swap setting either. "LeftRight" occurs only in AI dodge,
  debug text and joystick settings, and "leftHanded" only as a Havok hkxCamera field (`mc_25_strings.out.txt`).

**Reads of the flag.**
- ApplyStencil, `0xE879B0`, and the camera setup `0xE6C780`.
- At `0xE6CA67` the camera setup calls GetLeftRightSwap and chooses −2/(r−l) instead of 2/(r−l) for projection [0][0].
  The constants are 2.0 at `0x1011590` and −2.0 at `0x106D4C8` (`mc_20`).
- So the flag describes a mirrored *view*, not a mirrored object.

**Determinant census** (`mc_12_detscan.py`, symbolic x87 and scalar-SSE evaluation of the whole of .text).
- The detector flags any value equal to ±det of a 3×3 grid, in row-major, column-major or 16-byte-row layout.
- It finds exactly **2 functions**:
  - `0x4B46A0` = NiMatrix3::Inverse. It is independently identified as the callee of NiGeometry::ApplyTransform
    (NiGeometry vtable slot 36 = `0xA7FF70`), matching the X360 PDB's slot 36 → NiMatrix3::Inverse.
  - `0xD5BCE0`, a Havok-style triple product with one caller.
- Callers of `0x4B46A0` (`mc_13`): NiGeometry and NiBSPNode ApplyTransform, projectile and actor code, and gameplay
  helpers. None is in the shader or renderer code.
- Named X360 callers of NiMatrix3::Inverse (26), hkMatrix3::getDeterminant (7) and `_determinant3x3` (1) are likewise
  ApplyTransform, gameplay and Havok (`mc_6`).
- **D3DXMatrixInverse:** 18 calls, all with `pDeterminant = NULL`. D3DXMatrixDeterminant and D3DXMatrixDecompose are
  not imported, and neither is an effect framework (`mc_11`, `mc_19`).

**Negative scale** (`mc_14`).
- The world scale is NiAVObject `+0x98`. The world rotation is at `+0x68`, confirmed by `0xB7DE77`, which passes
  `lea ebx, [esi+0x68]`.
- It is loaded as a float 47 times, and none of those loads is compared with 0.
- There are 7 integer `cmp [reg+0x98], 0` sites. All are in game code (0x4F–0x81 range), on other classes'
  pointer fields.

**Read-set test** (`mc_15`).
- When ApplyStencil, SetCullMode and SetupGeometryRenderStates run on the real bytes, their data reads are limited to:
  - the render state's table and swap flag;
  - the renderer pointer and its `+0x8B8`;
  - the stencil property's flags (+0x18, plus ref/mask +0x1C/+0x20 when stencil is enabled);
  - `[0x11F91E4]` eCurrentPass.
- No geometry or transform memory is ever read, so there is no input through which a determinant could act.

##### 3. Lighting and tangent frames

**Sun direction** (`mc_22`, with the code read by hand).
- `0xB7DE7D` calls `0xC4C2D0(&geom->world (+0x68), 0, &M)`.
- With second argument 0, `0xC4C2D0` builds the D3D matrix of `NiTransform::Invert(world)` (`0x4B4880`). That function
  uses `0x4768C0` for the rotation, and `0x4768C0` builds the transpose from GetCol ×3. The scale becomes 1/scale.
- `0xB7C7B0` then applies `D3DXVec3TransformNormal(−L, M)` and `D3DXVec3Normalize`, and passes the result to
  `SetVertexShaderConstantF(25)` (device +0x178).

**Eye position and other shaders.**
- The eye position goes through TransformCoord with matrices from the same helper, or from D3DXMatrixInverse
  (determinant not requested).
- The X360 Lighting30Shader and ShadowLightShader `_Lights` and `_EyePos` routines use the same TransformNormal and
  TransformCoord calls.

**Why this is exact for a mirror.**
- For an orthonormal mirror, transpose = inverse. The vertex shader gets a model-space light, and N·L_model equals the
  mirrored world normal's (F N)·L exactly: max error 3.3e−16 over 200 trials (`mc_16` B).
- Model-space T, B and N give the same dot products as the mirrored world frame (error 3.3e−16). So the normal-map
  detail is mirrored together with the diffuse image, and lighting is not flipped.

**What would flip, and doesn't here (controls).**
- An adjugate inverse, which assumes det = +1, flips N·L on 200/200 mirrored trials.
- A world-space `cross(N, T)` bitangent flips on 200/200 mirrored trials and 0/200 proper ones.
- FNV feeds model-space vectors from the CPU, so neither applies on the CPU side. The shader bytecode was not read
  (see Open).

##### 4. What the mode-5 card looks like

- **Winding** (`mc_16` A: 200 random trials × 2 triangles; retail-shaped quad, front +Y, CCW about +Y).
  - The camera always sees the authored +Y side: 400/400.
  - Winding-front agrees with the authored front: proper 400/400, mirrored **0/400**.
  - Screen sign of the camera-facing front: proper +1 on all 400, mirrored −1 on all 400.
- **Why that means culled.** The default CULLMODE CW keeps the winding that proper camera-facing fronts produce;
  otherwise every ordinary mesh would render inside-out. The mirrored card's camera-facing front is therefore culled.

| Card | Path | CULLMODE | In game |
|---|---|---|---|
| single-sided: no stencil property, or draw mode CCW_OR_BOTH or CCW; any non-BOTH on the BSShader path | W2a (and W1) | CW | camera-facing front culled: a flat card is **invisible**, lit or unlit, normal-mapped or not. Faces authored toward −Y would show **inside-out**, shaded by their away-pointing normals (looks lit from behind). This is not a lighting flip. |
| draw mode CW with stencil test enabled | W1 | CCW | the camera-facing front **is** drawn, because the two reversals cancel. Without the stencil enable bit the BSShader path treats it as CCW, so it is culled. |
| both-sided (draw mode BOTH), or a strip particle pass | all | NONE | drawn; left-right mirrored image; lighting consistent with normals facing the camera; normal map mirrored with the texture. This is the retail case: 24/24 mode-5 primitives, all unlit. |

#### Controls and their results

| Control | Must | Result |
|---|---|---|
| Emulated ApplyStencil (16 cases), each model scored against it: Gamebryo table G vs swap-ignoring G0 vs Bethesda rule B | only G fits | G 16/16, G0 10/16, B 12/16 |
| Emulated SetCullMode (8 cases), same models | only G fits | G 8/8, G0 5/8, B 6/8 |
| Emulated SetupGeometryRenderStates (50 cases), same models | only B fits | **B 50/50**, G 28/50, G0 22/50 |
| Determinant detector on a hand-assembled x87 det (row- and column-major) | flagged | flagged |
| The same bytes with one minor's `fsubp` changed to `faddp` | not flagged | not flagged |
| Detector on the real NiMatrix3::Inverse (unoptimised code) and on optimised mixed x87/SSE `0xD5BCE0` | flagged | both flagged |
| The first, adjacency-only detector draft | must find Inverse | failed: one unrelated cross-product window, no determinant, not even Inverse. It was replaced by the symbolic evaluator; the draft is not kept as a file. |
| Swap-flag setter's reachability, confirmed on X360 with names | no caller | 0 callers, 0 renderer-object virtual sites |
| Winding: proper twin F' = F·diag(−1, 1, 1) | front agrees 400/400 | 400/400 (mirror 0/400) |
| Lighting: adjugate inverse; world-space bitangent | must flip | 200/200 flips; 200/200 flips (proper 0/200) |
| Enumeration: function-start decode vs full linear sweep | same site set | 173 = 173; CULLMODE-shaped 12 = 12 |

#### Confidence

- **(1) High.** Two independent enumerations agree, the real bytes were emulated, and the models discriminate.
- **(2) High** that no per-geometry determinant or scale sign reaches culling: there is no input path.
- **(2) High but not absolute** that the swap flag is never set. All static references and typed virtual call sites are
  accounted for, and the named X360 build agrees. A call through a data-driven function pointer would not show up.
- **(3) Medium-high.** Derived from the code and exact geometry, not observed in game. The game was not launched.

#### Open

- **Shader bytecode** (the .sdp shader packages) was not read. Two things stay unknown:
  - whether any shader uses VFACE-style two-sided lighting;
  - whether it rebuilds the bitangent from world-space vectors.
  The CPU side feeds only model-space vectors, so either would have to happen in the shader to matter.
- **In game.** A single-sided lit test card under mode 5 was not shown in game; that would confirm "invisible from the
  front".
- **Batch groups.** How BSBatchRenderer assigns a geometry to a group, which decides W2b's 1 or 3, was not traced. On
  PC the per-geometry W2a call runs later and wins, because `0xB984D0` has no lock counter. The X360 build does lock
  at batch level, so the platforms can differ for geometry whose group and stencil disagree.
- **State blocks.** IDirect3DDevice9 state blocks (CreateStateBlock/Apply) were not audited. They could only replay a
  value from the paths above.
- **Detector blind spots.** Packed-SSE determinants, and a cross product followed by a dot product in another
  function, would be missed. The 7 `cmp [reg+0x98], 0` sites were not typed.
- **Names by shape only.** The PC function names (BSShader::SetupGeometryRenderStates, BSRenderState::SetCullMode,
  the BSBatchRenderer functions) and the BSSM_* pass names come from the X360 2010-8-22 build by matching code shape.
  The PC 1.4 enum could have shifted, although the code compares the same 0x56/0x57.
- **Reflection camera.** Whether the water-reflection camera itself uses a mirrored view matrix was not examined. If it
  does, it gets no cull compensation, because the swap is never set.

#### Files (all under this directory; run with `python -B <script>`, each takes under 20 s, one process at a time)

| File | What it shows |
|---|---|
| `rt.py` | shared read-only image helpers |
| `emu_mc.py` | unchanged copy of RE-25's `emu25.py` |
| `mc_1_cullwrites.*` | all `push 0x16` sites by function-start decode. Section C is a raw, noisy +0xE4 dump that no conclusion uses. |
| `mc_24_sweepcheck.*` | the same census by full linear sweep, which agrees |
| `mc_2_groups.*` | state-list entries; slot-26 calls via `[r+0x8B8]`; drawMode helper callers |
| `mc_3_swap.*` | renderer slots 71/72; `+0xF4` writers; lock-array writers |
| `mc_4_passthrough.*` | non-immediate state arguments, lock arguments, device calls in render-state code |
| `mc_5_swapcallers.*`, `mc_8_sites120.*` | every `+0x120`/`+0x11C` virtual call site and its object type |
| `mc_7_rsuse.*` | every virtual call made on a render-state holder |
| `mc_6_x360.*`, `mc_6b_x360_sites.*`, `mc_9_x360_*.lst`, `mc_18_x360_groups.*`, `x360_20100822_memdebug_procs.txt` | named X360 cross-checks |
| `mc_10_applystencil.*` | PC shader ApplyStencil callers (stencil-enable gated) |
| `mc_11_d3dx.*`, `mc_19_imports.*` | D3DX usage (no determinant requested), imports |
| `mc_12_detscan.*` (+ `mc_12.run.log`), `mc_13_invcallers.*` | determinant census with controls; callers |
| `mc_14_negscale.*` | world-scale sign tests |
| `mc_15_emulate.*` | emulation of the real cull code, model scores, read-sets |
| `mc_16_consequence.*` | winding, lighting and tangent-frame consequences, with controls |
| `mc_17_groupowners.*`, `mc_20_restore.*`, `mc_21_a2targets.*`, `mc_22_lightxf.*`, `mc_23_unresolved.*`, `mc_25_strings.*` | typing and closing loose ends |

### Verifier

Verifies `../ANSWER.md`. Binary: the FalloutNV.exe 1.4.0.525 runtime image dumped 2026-09-27 (image sha256 a99059f0…,
base 0x400000, file offset = RVA). The X360 cross-checks use the PDB-named 2010-8-22 MemDebug exe. Names come only
from the raw `cvdump -s` procedure list kept beside the answer, and I disassembled that exe myself with capstone PPC
(`x360.py`).

#### Method

- Everything was re-derived with my own scripts in this directory, and none of the investigator's classifiers were used.
- `emu_v.py` is an unchanged copy of RE-25's `emu25.py`, used only as an x86 emulator class.
- I located functions my own way:
  - **Vtables:** RTTI plus a census of every RTTI vtable (`v1`, `v8`).
  - **Call census:** a linear sweep of all 4,135,373 .text instructions that rebuilds each call's pushed arguments (`v3`).
  - **Device calls:** a device-level classification of every `+0xE4` call site (`v6`).
  - **Taint:** a forward taint of the render-state pointer (`v11`).
  - **Batch-renderer order:** read from the PC code and from the named X360 twins.
- Real content and shaders were checked too:
  - a census of every retail .nif in the installed FNV archives (`v19`, `v20`, `v23`);
  - a decode of every retail compiled shader (`v22`).
- Read-only throughout: one Python process at a time, and no builds or game launch.

#### Verdicts

| # | Claim (ANSWER.md) | Verdict |
|---|---|---|
| 1 | No cull-mode path looks at a geometry's transform. Every CULLMODE value is a constant, `T[drawMode][swap]`, or the Bethesda rule (NONE if BOTH or strip-particle pass, else `T[CCW][swap]`) | **CONFIRMED WITH CORRECTIONS** (reach list, write order, lock counter, and one census count) |
| 1a | The cull table: CCW_OR_BOTH and CCW → CW/CCW, CW → CCW/CW, BOTH → NONE/NONE | **CONFIRMED** |
| 2a | The swap flag is cleared by the constructor, set by nothing, and is the mirrored-projection switch | **CONFIRMED** |
| 2b | No code tests a world determinant or negative scale on the way to the cull state | **CONFIRMED**, with two sub-claims corrected or left unsettled (below) |
| 3a | Single-sided mode-5 card: the camera-facing authored front is culled, and faces authored away are drawn "inside-out" | **CONFIRMED**, also by retail content (below) |
| 3b | Lighting is not flipped: light and eye vectors reach model space through an exact inverse, and tangent frames stay consistent | **CONFIRMED**; the shader-bytecode open item is now largely settled |
| 3c | "All 24 retail mode-5 primitives are both-sided and unlit, so the only retail effect is the flipped image" | **REFUTED** |
| 3d | Per-card outcome table: single-sided → CW → invisible; stencil-enabled CW → CCW → drawn | **CONFIRMED WITH CORRECTIONS** (it depends on the render path and pass) |

#### 1. The CULLMODE write paths: confirmed with corrections

**The objects, found by RTTI (`v1_rtti.out.txt`, `v8_slot26_census.out.txt`).**
- NiDX9RenderState's vtable is 0x10F088C. Slot 9 is ApplyStencil 0xE87EF0, slots 19 and 20 are Get/SetLeftRightSwap
  0xE88750/0xE88760, and slot 26 is SetRenderState 0xE88780.
- NiDX9Renderer's vtable is 0x10EE4BC. Slots 71 and 72 (0xE757E0/0xE757F0) forward through `[this+0x8B8]`.
- Of the 3,268 RTTI vtables in the image, only two hold 0xE88780, 0xE88760 or 0xE87EF0: NiDX9RenderState and its base
  NiD3DRenderState (0x10EF60C).

**Census, by a method independent of the investigator's (`v3_sweep.out.txt`).**
- 174 call sites pass an argument that resolves to the immediate 0x16. The investigator counted 173 `push 0x16`
  sites; my count also includes register-resolved arguments.
- The CULLMODE-shaped ones are the same 12: three slot-26 calls (0xE879D4, 0xE87FF3, 0xE9129F) and nine
  0xE7F430 state-list entries.

**Device level (`v6_com_e4.out.txt`).**
- Only 14 of the 93 `+0xE4` call sites are COM-style calls with the device pushed. The rest are thiscalls on game
  classes.
- 12 of the 14 pass an immediate state: 7, 14, 0x13, 0x14, 0x1B, 0xA8 or 0xAE. None is 0x16.
- The other two carry their state in a variable: the default-list loop (0xE8804C) and the slot-26 wrapper (0xE887BC).
- **Nothing writes CULLMODE straight to the device.**

**Cull table (`v4_ctor_table.out.txt`).**
- The constructor 0xE881A0 stores edi=2, ebp=3, ebx=1 into +0xD4..+0xF0, which gives exactly the table in 1a. It
  stores 0 to the swap flag at 0xE8840B.
- A register-definition audit shows no other definition of those three registers before the stores.

**The real bytes, emulated (`v12_emulate.out.txt`).**
- The render-state object is the real class (vtable 0x10F088C). Its device is a recording stub. The swap flag and the
  inputs are varied.

| Function | Cases | G = `T[dm][swap]` | G0 (ignores swap) | B (Bethesda rule) |
|---|---|---|---|---|
| ApplyStencil 0xE87EF0 | 16 | **16** | 10 | 12 |
| 0xBAA700 (see correction C1) | 16 | **16** | 10 | 12 |
| 0xB984D0 → 0xE879B0 | 8 | **8** | 5 | 6 |
| SetupGeometryRenderStates 0xBE20E0 | 48 | 28 | 24 | **48** |

- With no stencil property or a NULL property state, 0xBE20E0 gives CW, or CCW when swap is set. For passes 0x56 and
  0x57 it gives NONE.
- **Read-sets.** Every read is one of these:
  - render-state fields (table, swap flag, cache, lock bytes, device);
  - `stencilprop+0x18/0x1C/0x20`, `propstate+0x0/0x10` and `alphaprop+0x18`;
  - `[0x11F9508]`, `renderer+0x8B8` and `[0x11F91E4]` (eCurrentPass);
  - the vtable.
- No geometry or transform memory is read.

**The other writers, confirmed by listing (`v26_typing_and_listings.out.txt`).**
- W3: 0xE9129B pushes (0x16, 2, 0, 0).
- W4: the default list at 0x11BFCE8, entry 8 = (22, 2), is applied by 0xE88000. That function is the only writer of the
  restore array `+0x124+8·state`.
- W5: all nine state-list entries hold value 1. At 0xBE2429 that value comes through edi = 1, set at 0xBE23BB.
- W6: the restores at 0xE7EF58 and 0xB98766 read `+0x124+8·state`.
- Every CULLMODE call passes lock 0.

**Corrections to claim 1:**

- **C1: the W1 (ApplyStencil) reach list is incomplete.**
  - An RTTI-less shader vtable 0x10B8980 (81 slots) has as slot 21 `0xBAA700 = [this+0x1C]->ApplyStencil(arg4->+0x10)`.
    This call is **unconditional**, with no stencil-enable test.
  - Its X360 named twin is **TallGrassShader::PreProcessPipeline** (0x82A99C78). The body is the same: take the property
    state's +0x10, call `[this+0x1C]->ApplyStencil` on it, return 0.
  - The X360 ApplyStencil callers are 7: the four shaders' SetupGeometryConstants, UpdateRenderState,
    Lighting30Shader::SetupGeometry_Opt and TallGrassShader::PreProcessPipeline (`v14_x360_swap.out.txt`).
  - UpdateRenderState is reached from NiD3DShader::UpdatePipeline 0xE81170 when `[this+0x24]` is set.
  - On PC, the four SetupGeometryConstants call sites (0xB7DD39, 0xB8A173, 0xBBF594, 0xBC9CB0) are gated on stencil
    flag bit 0, as the investigator says.
- **C2: "only two `mov r32, 0x16` sites exist" is wrong.**
  - There are 10 (0x4B17D8, 0x4DCC13, 0xB81E0B, 0xD86C64, 0xD92597, 0xDCE955, 0xE69CA6, 0xE75B20, 0xE7BE0F, 0xE90D69).
  - None is a render state. The renderer ones are format returns (22 = X8R8G8B8) and a loop count. 0xB81E0B feeds
    0xB815E0, a shader-flag table lookup.
- **C3: the PC lock counter is not absent; it is dead.**
  - BS's generic setter 0xB97D90 maps each state through `[0x11FFA30+4·state]`, where `map[0x16] = 19`. It then skips
    the write if the counter at `[0x11FF9D8+4·19] = 0x11FFA24` is non-zero.
  - The batch renderers decrement that counter when it is non-zero (0xB9A01E, 0xB9A356).
  - Nothing increments counter 19: no setter loads `map[0x16]`, 0xB984D0 never touches it, and 0xB97D90's only caller
    passes 0.
  - So CULLMODE is never locked on PC. The investigator's conclusion stands; "no counter" is imprecise.
- **C4: "On PC the per-geometry W2a call runs later and wins" is wrong for the main opaque path.**
  - 0xB98E80 is the per-geometry pass, the counterpart of X360 RenderPassImmediately_Standard. It runs
    SetupGeometryConstants (slot 31, where W1 lives), then SetupGeometryRenderStates (slot 34, W2a) **only if its 4th
    argument is set**.
  - That argument is RenderPassImmediately 0xB994F0's 5th (`v13_rpi_callers.out.txt`):
    - **0** from the opaque batch paths RenderBatches 0xB9A090 (0xB9A2A1) and RenderNextRenderPass 0xB99E20 (0xB99FE4;
      ebx = 0 there);
    - **1** from 0xB64FD1 (alpha geometry) and 0xB999C8 (persistent pass list);
    - `edi` at 0xB9B1FE.
  - The named X360 twins agree: RenderAlphaGeometry, RenderPersistentPassList and RenderNextAlpha pass 1;
    RenderBatches and RenderNextRenderPass pass 0.
  - **In the opaque batch path the per-geometry W2a never runs.** The batch group's SetCullMode (W2b) stands unless W1
    (stencil-enabled geometry, later) or W5 (below) overrides it.
- **C5: batch-group assignment is now traced (it was listed as Open).**
  - PC RegisterPass 0xB99C90 (X360 BSBatchRenderer::RegisterPass, same logic) picks the group:

    | Condition | Group |
    |---|---|
    | shader-property flag 0x400000 | 4 |
    | alpha-tested, two-sided | 2 |
    | alpha-tested, single-sided | 3 |
    | not alpha-tested, two-sided | 1 |
    | not alpha-tested, single-sided | 0 |

  - "Two-sided" means the stencil test `(flags & 0xC00) == 0xC00`.
  - RenderNextRenderPass then calls SetCullMode(1) for groups 0, 3 and 4 (CW) and SetCullMode(3) for groups 1 and 2
    (NONE). There is no transform input.
- **C6: W5 order.**
  - BeginPass 0xB99390 is called inside RenderPassImmediately whenever the pass type or shader changes, which is after
    the group SetCullMode.
  - Through slot 64 (0xBE0EB0 → 0xB97D90) it applies the pass's render-state list.
  - So geometry drawn in a pass whose list sets CULLMODE NONE **can** be drawn with no culling, whatever its sidedness.
    That happens from the geometry whose pass or shader change triggers BeginPass until the next SetCullMode (a later
    group in the same bucket) or a W1/W2a write. The exact extent depends on batch order, which I did not simulate.
  - The X360 NONE lists are 8 (`v17_x360_statelists.out.txt`): BoltShader, BeamShader, VolumetricFogShader,
    ShadowLightShader::PresetVelocityPasses_2x ×2, and Lighting30Shader::PresetStages ×3.

#### 2. The swap flag, determinants and negative scale

##### 2a. The swap flag: confirmed

**Writers of +0xF4.**
- In the renderer (0xE60000–0xEA0000), only the constructor's store of 0 (0xE8840B) and the setter (0xE88769) write it
  (`v3` section E).
- The render-state taint over all of .text (`v11_rsflow.out.txt`) finds no write to render-state +0xD4..+0xF7. It
  follows [renderer+0x8B8] and five render-state globals to a fixed point.

**Slot-20 calls.**
- The only slot-20 call on a render-state object is the renderer wrapper 0xE75800 (control: found).
- An over-approximating run (`v11_rsflow_members.out.txt`) also treats every `[reg+0x1C|0x28|0x40]` load as a render
  state. It adds one candidate, 0xE37337, which passes 2 arguments and so is not the setter.

**References.**
- 0xE88760 is referenced only by the two render-state vtables.
- 0xE757F0 is referenced only by NiDX9Renderer slot 72.
- Neither has a rel32 call site (`xref.py`).

**The 17 `+0x120` sites (`v9_slot120_ctx.out.txt`, `v26` A).**
- The wrapper takes exactly one stack argument (`ret 4`).
- The one-argument sites are on process objects: HighProcess or MiddleHighProcess `this`, and Actor slot 129 on
  `[this+0x68]`. Every process class's slot 72 is 0x4534F0.
- The BSFaceGenAnimationData `this`-calls go to its own slot 72 (0x64B5E0).
- The rest pass 0 or 2 arguments.

**X360.** SetLeftRightSwap and GetLeftRightSwap have 0 `bl` callers and exactly one pointer reference each (the
vtable). The setter writes `[this+0x718]->+0xF4` (`v14`).

**Reader.**
- At 0xE6CA67 the camera setup calls slot 19 and picks `fdivr qword [0x106D4C8]` (−2.0) over `[0x1011590]` (+2.0) for
  projection [0][0].
- Both constants are doubles, not the floats a quick read might suggest.
- So the flag means a mirrored projection.

**Strings (`v25_strings.out.txt`).**
- No INI setting or console command names a swap or mirror.
- "cull mode" belongs to BSMultiBoundNode's debug text (ALLFAIL/ALLPASS).

**Side note.** The renderer singleton is NiRenderer::ms_pkRenderer `[0x11F4748]`, written by the NiRenderer
constructor at 0xA62B42. `[0x11F9508]` is BS's copy, written at 0xB54A0C. The investigator names only the latter; its
site-based typing is unaffected.

##### 2b. Determinants and scale sign: confirmed, with two sub-claims corrected or unsettled

**Determinant helpers.**
- NiGeometry and NiTriShape vtable slot 36 is 0xA7FF70, which calls 0x4B46A0. That identifies NiMatrix3::Inverse, as
  on X360 (`v18_det_helpers.out.txt`).
- 0x4B46A0 has 12 call sites in 9 functions. 0xD5BCE0 has 1. None is in the renderer or BS shader and batch ranges.
- X360 `_determinant3x3` and `hkMatrix3::getDeterminant` are called only from Havok (`v14`).

**D3DX.**
- D3DXMatrixInverse is called 18 times, all with `pDeterminant = 0` (`v24_d3dxinverse.out.txt`).
- D3DXMatrixDeterminant, D3DXMatrixDecompose and any effect framework are not imported (`v2_imports.out.txt`).

**Not re-derived.** I did not rerun a whole-.text determinant census, so the claim "only two functions compute a
3×3 determinant" stays unverified by me. It is not load-bearing: the cull writers' full input set is identified
above, and none of those inputs is written from a transform.

**Scale-sign sub-claim, partly corrected (`v21_scale_sign.out.txt`).**
- My broader scan finds 104 float loads of `[reg+0x98]` (the investigator reported 47).
- Loads compared with 0.0 do exist:
  - 0x9E0DED, a DetailedActorPathHandler timer;
  - 0x50609C, a TESEffectShader slot-8 field;
  - 0xC62553, owner not identified, after a `≠ 1.0` test.
- So "none is compared with 0" does not reproduce as stated. None of these is in the render or cull path.
- The renderer's only world-scale test (0xE6FFD8) is a 0.99 ≤ s ≤ 1.01 unit-scale check.
- Mode 5 writes a rotation, not a scale, so no scale-sign test could detect its mirror anyway.

#### 3. What a mode-5 card looks like

##### 3a. The winding consequence: confirmed

`v16_winding.out.txt` covers 400 random camera and node placements (elevations up to ±69°), 2 triangles each:

| Case | Vertex normal faces camera | On-screen winding equals the proper twin | Survives the default single-sided cull |
|---|---|---|---|
| Proper twin P, wound about +Y | 800/800 | 800/800 | **800/800** |
| Mode 5 F, wound about +Y | 800/800 | 0/800 | **0/800** |
| Mode 5 F, wound about −Y | 0/800 | 0/800 | **800/800** |
| Proper twin P, wound about −Y | 0/800 | 800/800 | 0/800 |

- The camera always sees the +Y side. The mirror reverses the screen winding of every triangle.
- A card wound about +Y (facing the camera) is culled. One wound about −Y is drawn, with away-pointing vertex normals.

**Retail content confirms that the engine does not compensate (`v20`, `v23`).**
- **The 14 single-sided mode-5 meshes are wound away from the camera, −Y (14/14).** Their vertex normals agree
  with the winding (14/14). This is the only orientation that survives the mirror.
- **Control, proper billboards (modes 0–4):** single-sided geometry is wound *toward* its camera axis (+Z) in
  **813 of 816** unmixed cases:

  | Mode | Toward | Away |
  |---|---|---|
  | 1 | 214 | 1 |
  | 2 | 52 | 0 |
  | 3 | 477 | 2 |
  | 4 | 69 | 0 |
  | 0 | 1 | 0 |

- **Double-sided mode-5 geometry**, which the cull does not constrain, is wound toward the camera: 29/29 unmixed.
- The authors of exactly the single-sided mode-5 population wound it the other way from every other billboard. That is
  what they would have to do if the engine culled the camera-facing winding under the mirror.

##### 3b. Lighting and tangent frames: confirmed

**The light-transform helper, emulated (`v15_lightxf.out.txt`).** The real 0xC4C2D0(world, 0, out), whose output
0xB7C7B0 feeds to D3DXVec3TransformNormal before `SetVertexShaderConstantF(25)`, was run on 60 proper and 60 mode-5
transforms with random translation and scale.

| Candidate | Proper matches | Mirrored matches |
|---|---|---|
| Inverse (R^T/s, −R^T t/s) | 60/60 | 60/60 |
| Adjugate (assumes det = +1) | 60/60 | **0/60** |
| Forward | 0 | 0 |
| Transposed inverse | 0 | 60 |

- The transposed-inverse candidate matches the mirrored trials only because mode-5 F is symmetric; the proper trials
  exclude it.
- max |N·L_model − (F N)·L_world| = 1.05e−7 over 300 pairs.
- The adjugate control flips the sign on 300/300.

**Shader bytecode, the investigator's Open item (`v22_shaders.out.txt`).** All 15,791 shaders in the 16 retail
packages were decoded (vs_1_1 through ps_3_0).
- **vFace** is used only by `VolumetricFog001.pso` (16 copies). That shader also has the VolumetricFog CULLMODE-NONE
  pass list, an independent positive control. No lit shader does two-sided lighting.
- **`crs`** appears nowhere.
- **The HLSL `cross()` pattern** (mul/mad with .zxy/.yzx swizzles) appears only in the GRASS2* and GRASS23x* grass and
  PRECIP vertex shaders: 20 names in 257 shaders, the detector's positive control. No lighting shader rebuilds a
  bitangent.
- Caveat: the cross-product detector matches the compiler's pattern and could miss hand-written forms.

**What this means.** The mirror does not flip lighting. The CPU gives the shaders model-space light and eye vectors
through an exact inverse, and the tangent frame is model-space vertex data.

##### 3c. "All retail mode-5 primitives are double-sided and unlit": refuted

**The census covers all retail meshes (`v19_retail_mode5.out.txt`).**
- 20,746 .nif files in 11 archives: Fallout - Meshes, Update, and every `* - Main.bsa`.
- A flags-width self-check reproduced every NiBillboardNode block size (4-byte NiAVObject flags, bsver 34) in all 280
  files that contain a NiBillboardNode.
- Modes: {0: 16, 1: 304, 2: 165, 3: 35, 4: 120, **5: 41**, 8: 8}.

**What sits under the 41 mode-5 nodes.**
- They are in 19 files: oasistorch01; fireball01–04; dlcanchactorshock01–10; dlc04lighthousebeamfx; fxfiremeshsmall;
  nukagrenadeexplosion01 and the nvdlc05 copy, 12 nodes each.
- They carry 45 geometries: **30 double-sided (stencil BOTH) and 15 single-sided (no stencil property)**. All 45 use
  BSShaderNoLightingProperty.
- The 15 single-sided geometries are dlcanchactorshock01–10, dlc04lighthousebeamfx FXLightBeam04:2 and :3,
  fxfiremeshsmall FireBall09:0 and :1, and oasistorch01's Smoke01 particle system.

**Why the investigator's statement is wrong.**
- The "24" is RE-25's gate sample: one file, LE and BE. RE-25's verifier itself said it checked only the 220 gate
  samples.
- "Unlit" holds for all retail mode-5 geometry. So no retail mode-5 geometry is lit or normal-mapped, and 3b is
  hypothetical for retail.

**The retail effect is more than a flipped image.**
- The 14 single-sided triangle meshes are drawn in game only because their reversed winding cancels the mirror.
- A renderer that implements mode 5 as a proper rotation with back-face culling culls all 14 (P wound about −Y
  survives 0/800 in `v16`).

##### 3d. The per-card table: confirmed with corrections

- **Single-sided card, +Y winding, no stencil property.**
  - Opaque path: group 0/3/4 gives CW, so it is culled.
  - Alpha path: W2a gives CW, so it is culled.
  - **Except** in a pass whose render-state list sets NONE (C6): there it can be drawn for part of a batch.
  - None of the eight X360 NONE lists belongs to BSShaderNoLighting, so the retail outcome is unchanged. Which
    Lighting30 passes carry NONE is unresolved.
- **"Draw mode CW with stencil enabled → CCW → drawn."**
  - True in the opaque batch path, where W1 runs after the group cull and W2a does not run. It is also true through
    TallGrass's unconditional PreProcessPipeline.
  - In the alpha and persistent paths W2a runs after W1 and rewrites CW, so the card is culled.
- **Double-sided (BOTH):** NONE on every path, so it is drawn as a left-right mirror image. Confirmed.

#### Controls (must fail, and did)

| Control | Expected | Result |
|---|---|---|
| G0 and B models against the real ApplyStencil and SetCull | fail | 10/16 and 12/16; 5/8 and 6/8 |
| G and G0 models against the real SetupGeometryRenderStates | fail | 28/48 and 24/48 |
| Slot-20 taint must find the known wrapper 0xE75800 | found | found |
| Member over-approximation must find the known shader ApplyStencil calls | found | 0xB7DD39, 0xB8A173, 0xBBF594, 0xBC9CB0 found |
| Adjugate reading of 0xC4C2D0 on mirrored inputs | fail | 0/60; the N·L sign flips 300/300 |
| Proper twin P wound +Y must survive; F wound +Y must not | 800 / 0 | 800 / 0 |
| Single-sided proper billboards must be wound toward their camera axis | toward | 813/816 |
| NIF flags width: the wrong width must fail the exact block-size check (`v27_flagwidth_control.out.txt`) | 2-byte fails | 4-byte reproduces 689/689 NiBillboardNode blocks; 2-byte reproduces 0/689 |
| vFace detector must find the one double-sided pass shader; cross() detector must find the grass shaders | found | VolumetricFog001; GRASS and PRECIP |

#### Corrections

1. **The W1 reach list omits TallGrassShader::PreProcessPipeline.** PC 0xBAA700, slot 21 of the RTTI-less vtable
   0x10B8980, calls ApplyStencil unconditionally; the X360 twin is 0x82A99C78. NiD3DShader::UpdatePipeline 0xE81170
   also reaches UpdateRenderState.
2. **There are 10 `mov r32, 0x16` sites, not 2.** None of them is CULLMODE.
3. **The PC CULLMODE lock counter exists but is dead.** Counter 19 is at 0x11FFA24; 0xB97D90 checks it and the batch
   renderers decrement it, but nothing increments it. The PC is not "without a counter".
4. **The per-geometry SetupGeometryRenderStates (W2a) does not run in the main opaque batch paths.**
   RenderBatches and RenderNextRenderPass pass 0 to RenderPassImmediately's gate argument. There the batch group
   (W2b) decides, overridden later by W1 for stencil-enabled geometry. W2a runs after W1 only in the alpha and
   persistent-list paths.
5. **The batch-group assignment is resolved.** It is keyed on stencil BOTH, alpha test, shader flag 0x400000 and pass
   type (PC 0xB99C90, X360 RegisterPass). There is no transform input.
6. **The per-card table must include W5.** Pass render-state lists with CULLMODE NONE are applied by BeginPass inside
   RenderPassImmediately, after the group cull, on a pass or shader change. A single-sided card drawn in such a pass
   can escape culling until the next cull write.
7. **"All 24 retail mode-5 primitives are both-sided … the only retail effect is the flipped image" is refuted.**
   - Retail has 41 mode-5 nodes in 19 files, with 45 geometries, all unlit.
   - 15 of those geometries are single-sided, and all 14 triangle meshes among them are wound −Y, inside-out.
   - They are visible in game only because of the mirror. A proper-rotation implementation with back-face culling
     hides them.
8. **"None of the world-scale float loads is compared with 0" does not reproduce.** Zero compares exist in game code
   (0x9E0DED, 0x50609C, 0xC62553). None is on the cull path, and a scale-sign test cannot see mode 5 anyway.
9. **The renderer singleton is `[0x11F4748]`** (NiRenderer::ms_pkRenderer). `[0x11F9508]` is BS's copy.
10. **The investigator's Open item on shader bytecode is largely settled.**
    - No lit shader reads vFace; only VolumetricFog does.
    - No `crs` appears anywhere, and no lighting shader contains the HLSL `cross()` pattern.

#### Still unsettled

- **The determinant census.** I did not rerun a whole-.text census. The "exactly two functions" count is not
  independently confirmed, though it is not load-bearing.
- **Which Lighting30 passes carry the CULLMODE-NONE lists.** The PC functions holding the nine W5 entries were located
  but not named.
- **State blocks.** Direct3D state blocks (CreateStateBlock/Apply) were not audited.
- **The water-reflection camera.** Whether it uses a mirrored view was not checked.
- **No in-game observation.** Nothing was observed in game. The retail winding census is the strongest real-world
  evidence.
- **Cross-product detection is pattern-based.** A hand-written cross product in a shader could be missed.

#### Files

All files are in this directory. Run each with `python -B <script>`, one at a time.

| File | Purpose |
|---|---|
| `img.py`, `lst.py`, `xref.py`, `fnclass.py`, `x360.py` | Image, listing, cross-reference, RTTI-class and X360 PPC helpers (my own) |
| `emu_v.py` | Unchanged copy of RE-25's `emu25.py` |
| `v1_rtti`, `v8_slot26_census` | RTTI vtables; which classes hold the render-state slot functions |
| `v2_imports` | D3D and D3DX imports |
| `v3_sweep` (+ .json), `v5_e4_context`, `v6_com_e4`, `v7_slot26_ctx` | Call census and device-level SetRenderState classification |
| `v4_ctor_table` | The cull table from the constructor, with a register audit |
| `v9_slot120_ctx`, `v10_slot50`, `v11_rsflow` (+ `_members`), `v26_typing_and_listings` | Swap-flag reachability, site typing, listings |
| `v12_emulate` (+ .json) | Emulated cull writers, model scores, read-sets |
| `v13_rpi_callers`, `v17_x360_statelists` | Render-path gate arguments; X360 CULLMODE state lists |
| `v14_x360_swap`, `v18_det_helpers`, `v21_scale_sign`, `v24_d3dxinverse`, `v25_strings` | Swap-flag and determinant checks, scale-sign scan, strings |
| `v15_lightxf` (+ .json) | Light-transform emulation with the adjugate control |
| `v16_winding` (+ .json) | Winding consequence with the proper-twin controls |
| `v19_retail_mode5`, `v20_mode5_winding`, `v23_all_modes_winding` (+ .json), `v27_flagwidth_control` | Retail billboard census, winding control, NIF-reader control |
| `v22_shaders` | Retail shader bytecode: vFace and cross-product scan |
