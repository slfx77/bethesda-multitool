# RE-1: world scale, gravity and player constants read from the executables (2026-09-23)

Scope: design §4.3 item RE-1 (`docs/design/model-document-design-20260923.md`, lines 440-446).
Method: Python only (pefile 2024.8.26 + capstone 5.0.6, Python 3.14.7); no Ghidra, no emulator.
Scripts and logs: `%LOCALAPPDATA%\Temp\claude\c--dev-Multitool-BethesdaMultitool\0260887b-8cfa-44d9-9419-927f83483067\scratchpad\cut1a-prep\re1\` (listed in §9).

Every number below was read from the named file at the named address, or is marked
**inferred** / **computed**. Values are quoted with their IEEE-754 bit patterns because several
of them differ by one ULP between platforms and that is the level at which they were measured.

## 1 Result in one paragraph

Every Havok-era Bethesda executable carries the same four statics. The Xbox 360 MemDebug PDB names
them bare (its `S_LDATA32` records carry no scope: `fHkScaleSC`, `fHk2BSScaleSC`, `fBS2HkScaleSC`,
`fGravitySC`); the namespaces below come from the initializer functions the PDB and `TESV.map` name
(`bhkConvert::`dynamic initializer for 'fHk2BSScaleSC''`, `bhkConvert::`dynamic initializer for
'fBS2HkScaleSC''`, `HavokWorldProps::`dynamic initializer for 'fGravitySC''`;
`??__EfHkScaleConversionSC@bhkConvert@@YAXXZ` and friends in the map). `fHkScaleSC` has no
initializer of its own, so its `bhkConvert::` attribution is **inferred** from the two initializers
that read it; the other three are read from their own initializer names:

| Static | Meaning (read from the initializer code) | Oblivion | FO3 | FNV (PC and X360) | Skyrim LE / SE |
|---|---|---|---|---|---|
| `bhkConvert::fHkScaleSC` | Havok units per meter (the multiplier applied to the literal `-9.81`) | 10 (inferred, §5) | 10 (inferred, §6) | **10.0** (read, PDB-named) | **1.0** (read; SE inferred) |
| `bhkConvert::fHk2BSScaleSC` | game units per Havok unit = `(128/6) × ft_per_m / fHkScaleSC` | **6.99904** (read) | **6.99904** (read) | **6.999125** (read) | **69.99125** (read) |
| `bhkConvert::fBS2HkScaleSC` | `1 / fHk2BSScaleSC` | **0.1428767** (read) | **0.1428767** (read) | 0.142875 (computed at start-up from the read formula) | 0.0142875 (LE computed; SE read as a literal) |
| `HavokWorldProps::fGravitySC` | Havok world gravity, z component, Havok units/s² | **-73.575** (read) | **-98.1** (read) | **-98.1** (read, `= -9.81 × fHkScaleSC`) | **-9.81** (read) |

So the engine's own length chain is **128 game units = 6 feet**: `fHk2BSScaleSC × fHkScaleSC`
= 69.99125 units per meter in FNV and Skyrim (`(128/6) × 3.2808399`) and 69.9904 in Oblivion and
FO3 (`(128/6) × 3.2808`, four-digit feet-per-meter). That gives **0.0142875 m per unit**
(exactly 1.8288 m / 128) for FNV/Skyrim and **0.01428767 m per unit** for Oblivion/FO3, and
explains both figures the design quoted as hypotheses: 1/70 is a rounding of 69.99125 units per
meter, and 0.0142875 = 0.9144/64 is the same chain written per yard. Gravity is Earth's by the
code's own literal `-9.81` in FO3, FNV and Skyrim; in Oblivion the world gravity is `-73.575`
Havok units/s² = 0.75 × 9.81 × 10 (its literal `0xC2932667` is exactly 0.75 × the FO3/FNV `-98.1`
constant in single precision, not a hand-typed decimal, §5), so under Oblivion's own length chain
its gravity is 0.75 g. No player-height, eye-height or capsule constant exists in any of the
executables (§8).

Nothing here was measured on the retail X360 `default.xex` (XEX2, compressed; not opened) or on
the PS3 build.

## 2 Files read (SHA-256)

| Key | File | Size | SHA-256 | Version / stamp |
|---|---|---|---|---|
| fnv_steam | `Sample/DebugSymbols/Fallout - New Vegas (2022-5-24, Steam - Final)/FalloutNV.exe` | 16,549,704 | `3a87f92f011e5dc9179ddf733cf08be2b39ea6e5b7a8a9e3a9a72dafcc1b104d` | 1.4.0.525, COFF 0x4E0D50ED |
| fnv_runtime | `Sample/ReverseEngineering/Fallout - New Vegas (PC)/FalloutNV_runtime_image.bin` | 17,281,024 | `e46b43cdaa32d9b79b7816fa45cb076c7ed59e335b1533118dcb6bf1d9da692d` | memory dump of the above (SizeOfImage-sized; sections at their RVAs) |
| fnv_pc_re | `Sample/ReverseEngineering/Fallout - New Vegas (PC)/FalloutNV_PC.exe` | 16,549,704 | `518c87f58a6c4d9826e9ef8fbb7f4213882fa70822675610d45aea2464502a57` | 1.4.0.525; `.text`/`.rdata`/`.data` byte-identical to fnv_steam, only 4 pages of the DRM stub differ |
| fnv_disc10 | `Sample/DebugSymbols/Fallout - New Vegas (2010-9-16, Steam Disc - Final)/FalloutNV/FalloutNV.exe` | 16,397,640 | `6f164adbd2d635798957e64d412ae08a344efdf1dc66be28e4d3b0f6222c54d6` | 1.0.0.238, COFF 0x4C91713A (constants scanned only) |
| fo3 | `Sample/DebugSymbols/Fallout 3 (2026-2-15, Steam - Final)/Fallout3.exe` | 16,855,040 | `c3f97c2255fa041a851c17cf372d69aaadd8694e2dc4230ba556001bbfbd2f3e` | 1.7.0.4, COFF 0x60A80E56 (the VC2019 rebuild: path string `C:\dev\Fallout3_VC2019\PCBranch\...` at 0x00FA88B0) |
| oblivion | `Sample/DebugSymbols/The Elder Scrolls IV - Oblivion (2022-6-18, Steam - Final)/Oblivion.exe` | 7,898,624 | `a8f313845c1545e9a60e1e995961eef4c033115da9443f6d756341df3c2b7dc6` | 1.2.0416, COFF 0x462392C7 |
| skyrim_le | `Sample/DebugSymbols/The Elder Scrolls V - Skyrim (PC)/TESV.exe` | 20,807,680 | `311e71737b597ddc02a8d26d83bb5b0b2896c9041a69f580e1b4de875c4bb8bd` | resource says 1.1.21.0, COFF 0x51E657AD = the map's timestamp |
| skyrim_le map | `.../The Elder Scrolls V - Skyrim (PC)/TESV.map` | 41,662,359 | `fed7f0b964ea752fe677f4c413c37c97b5cad21541755c69c701a66423b288b2` | "Timestamp is 51e657ad", 283,449 publics; every symbol used here resolved to code of the matching shape |
| skyrim_le_steam | `Sample/DebugSymbols/The Elder Scrolls V - Skyrim (2026-6-16, Steam - Final)/TESV.exe` | 18,024,240 | `69362f77bc56bf17570a33106d020b6c67fd8f32a294b82f8b687b9cf71efc72` | 1.9.32.0, COFF 0x51437CE5 (constants scanned only) |
| skyrim_se | `Sample/DebugSymbols/The Elder Scrolls V - Skyrim Special Edition (2026-8-31, Steam - Final)/SkyrimSE.exe` | 37,910,440 | `846efccf0c1374d71f892907f46549560f2fcb0a75cb87a3eed438baa0f1402f` | 1.7.104.0, x64 |
| fnv_x360_memdebug | `Sample/ReverseEngineering/Fallout - New Vegas (X360)/Fallout_Release_MemDebug.exe` | 20,815,360 | `ff2188b9dc168d49a765d01cb8ed0376ac06b5cce0846b6c521cf8923cdd2312` | PE, machine 0x1F2 (Xenon), COFF 0x4C70AF4A (2010-08-22), base 0x82000000 |
| x360 PDB | `C:/dev/Multitool/BethesdaMultitool/tools/GhidraProject/Fallout_Release_MemDebug.pdb` | 104,729,600 | `d7564dc593ef7fa442ee3aca9212f913df4901cbe69691c130a9008538db2721` | GUID 97DEA974-924D-46FA-84F0-8907FB7A1384, age 1; dumped with `tools/microsoft-pdb/cvdump/cvdump.exe -s` (74 MB) |
| fnv_x360_debug | `Sample/ReverseEngineering/Fallout - New Vegas (X360)/Fallout_Debug.exe` | 36,407,296 | `5017e6b4f13edac976006c45ebf4b60dda7aa484ab9cf577894e86de8f99890e` | constants scanned only |
| default.xex | `Sample/DebugSymbols/Fallout - New Vegas (2010-8-22, X360 - Final)/default.xex` | 17,641,472 | `e590e7c109b5a79dd25037266d353c45217594b3990d3278e4e5358c4646bb6a` | XEX2 container, not opened |
| morrowind | `.../The Elder Scrolls III - Morrowind (2026-6-15, Steam - Final)/Morrowind.exe` | 4,325,376 | `eac72e1b3524ee0012d080d3c2e2e00e401464e41ee54f9afd6aaaadf2c221f5` | scanned for completeness; no Havok, nothing found |

PDB section offsets map to VAs through the MemDebug PE's own section table (section 1 `.rdata`
0x82000600, 4 `.text` 0x82250000, 7 `.data` 0x83210400); the CLAUDE.md `TEXT_BASE` agrees.

## 3 Fallout: New Vegas, Xbox 360 MemDebug (PDB-named, read from code)

The PDB names the statics per compilation unit (`S_LDATA32`): 462 × `fHkScaleSC`, 462 ×
`fHk2BSScaleSC`, 462 × `fBS2HkScaleSC`, 449 × `fGravitySC` (`ppc_step3.log`, `ppc_step6.log`).

| Static | Where | Value (read) |
|---|---|---|
| `fHkScaleSC` | `.rdata`, 462 copies 0x820017D0..0x820D01E0 (const) | 10.0 (`0x41200000`) on every copy |
| `fHk2BSScaleSC` | `.data`, 462 copies 0x83220678..0x832AC594 | 6.999125003814697 (`0x40DFF8D5`) on every initialized copy |
| `fGravitySC` | `.data`, 449 copies 0x8322067C..0x832AC59C | -98.10000610351562 (`0xC2C43334`) on every initialized copy |
| `fBS2HkScaleSC` | BSS, 462 copies 0x832890F8..0x832AC598 | computed at start-up (below) |

The formulas, read from the three initializer functions the PDB names:

```
bhkConvert::`dynamic initializer for 'fHk2BSScaleSC''  @0x831BE028  (ppc_step5.log)
  lfs f0,  [0x820D0284]   ; = 69.99124908447266 (0x428BFB85), an unnamed .rdata literal
  lfs f13, [0x820D01E0]   ; = fHkScaleSC = 10.0
  fdivs f12, f0, f13      ; 69.99125 / 10
  stfs f12, [0x832AC594]  ; -> fHk2BSScaleSC (this CU's copy)

HavokWorldProps::`dynamic initializer for 'fGravitySC''  @0x831BE068
  lfs f0,  [0x820D0288]   ; = -9.8100004196167 (0xC11CF5C3)
  lfs f13, [0x820D01E0]   ; = fHkScaleSC = 10.0
  fmuls f12, f0, f13      ; -9.81 * 10 = -98.1000061 (0xC2C43334), the value found in the other 448 copies
  stfs f12, [0x832AC59C]  ; -> fGravitySC

bhkConvert::`dynamic initializer for 'fBS2HkScaleSC''  @0x830FF4B8 (one of 462, ppc_step1.log)
  lfs f0,  [0x82001AFC]   ; = 1.0
  lfs f13, [0x83220678]   ; = fHk2BSScaleSC = 6.999125
  fdivs f0, f0, f13       ; 1 / 6.999125
  stfs f0, [0x832890F8]   ; -> fBS2HkScaleSC   (IEEE single: 0.14287500083 = 0x3E124DD3, computed)
```

Only one CU keeps the first two initializers alive (the other 461 copies were folded by the
Xbox compiler into the `.data` values above); the 69.99125 and -9.81 literals have exactly one
code reference each, both in those initializers (`ppc_step4.log`).

Consumers (`ppc_step4.log`, 1,362 references swept over the whole `.text` by the forward
`lis`+D-form tracker; ⚠ that tracker does not invalidate a pending `lis` when the register is
overwritten, so every count from it is an UPPER bound — see §11 and the review corrections in §12):
- `fGravitySC`: `TESObjectCELL::CreateWorld` (0x82394388) and `TESObjectCELL::InitHavok`
  (0x82395498). Both call the unnamed `bhkWorldCinfo` constructor at 0x82B13750 (which itself
  calls `hkpWorldCinfo::hkpWorldCinfo` and stores a default gravity of (0, fGravitySC, 0, 0)),
  then build the vector **(0, 0, fGravitySC, 0)** on the stack (`CreateWorld` 0x823943D8-
  0x823943F4: `lfs f31,[0x82000FA0]=0.0` to x, y, w and `lfs f0,[0x8322BA1C]=fGravitySC` to z) and
  store it with a VMX128 pair capstone does not decode. Decoded by the public VMX128 encoding
  (VX128_1 form, xop = low 11 bits & 0x7F3; the Xenia/free60 tables, recalled, so Ghidra's
  `PowerPC:BE:64:Xenon` should confirm the xop table — §10 item 1): `0x13E048C7` = `lvx128 v63, r0,
  r9` and `0x13E041C7` = `stvx128 v63, r0, r8` at 0x823943F8/FC and again at 0x82395548/4C in
  `InitHavok`, with r9 = r1+0x60 (the stack vector) and r8 = r1+0xB0 = cinfo+0x10 =
  `hkpWorldCinfo::m_gravity` exactly as the preceding `addi` instructions set them. The constructor's
  own pair (0x82B137D0/D4) is `lvx128 v63, r0, r6` / `stvx128 v63, r31, r4` with r4 = 0x10 (the
  (0, fGravitySC, 0, 0) default into this+0x10). So the store to `m_gravity` is **decoded by
  encoding, Ghidra confirmation pending**, no longer register analysis alone; the Havok world gravity
  is (0, 0, -98.1) Havok units/s², Z-down.
- `fHk2BSScaleSC`: `bhkRigidBody::GetPosition` (0x823999F8: `lvx128 v63,r0,r3` / `stvewx128` to the
  stack, then `lfs f0,[0x8322C088]=fHk2BSScaleSC` and `fmuls` on each component),
  `bhkRigidBody::GetLinearVelocity`, `HK2NI`, `bhkPickData::GetTo`, `TES::PickNI`,
  `DecalCaster::GetCollisionData`, `TESWaterListener`, and the 462 `fBS2HkScaleSC` initializers —
  i.e. Havok -> game units multiplies by 6.999125.
- `fBS2HkScaleSC`: the named consumers (`TESObjectCELL::AttachReference3D`,
  `TESObjectREFR::InitHavokForPrimitiveTrigger` / `InitHavokForPlaceableWater`,
  `TESWaterListener::Update`, `Interface::*` pick code, ...) are confirmed; the COUNT is
  sweep-dependent and approximate: a backward `lis`-tracking sweep with register-write invalidation
  finds 105 consumer references in 92 functions with a 16-word window, 193 in 157 at 64 words and
  455 in 294 at 256 words, plus the 462 initializer stores (`verify-re1/v2b_window.log`); the "818 in
  306" first published came from the forward tracker and is at best an upper bound. Game -> Havok
  multiplies by 1/6.999125 across roughly 100-300 functions.
- `fHkScaleSC` directly: ONLY the two initializers. (`HighProcess::ProcessTravel` was listed here
  in the first revision; that was a forward-tracker false positive — at 0x826F4A68 the instruction
  is `lwz r28, 0x50(r11)` where r11 was reloaded by `lwz r11, 0x140(r31)` at 0x826F4A5C after the
  `lis r11,-0x7dff` at 0x826F4A44, and the naive sum 0x82010050 merely happens to be one of the 462
  named `fHkScaleSC` copies; `verify-re1/v2d_processtravel.log`, `v2e_falsepos.log`.)

`bhkWorld::GetGravity` (0x827B4108) returns a pointer to `hkpWorld+0x10` (the Havok world's own
gravity vector) or a static default; it does no scaling.

## 4 Fallout: New Vegas, PC (runtime dump vs. Steam exe)

The PC build computes the same statics at start-up in x87, one CU at a time. One complete
initializer run, read from the runtime image (`pc_fnv_step3.log`):

```
0x00F35270  fld   dword ptr [0x01012050]  ; = 10.0  (fHkScaleSC, one of 492 .rdata copies)
0x00F35279  fdivr qword ptr [0x010120E8]  ; = 69.99125671386719 (0x40517F70C0000000)
0x00F3527F  fstp  dword ptr [0x011C3270]  ; -> fHk2BSScaleSC = 6.9991254806518555 (0x40DFF8D6)
0x00F35290  fld   dword ptr [0x011C3270]
0x00F35299  fld1 ; fdivrp                 ; 1 / fHk2BSScaleSC
0x00F3529D  fstp  dword ptr [0x011C321C]  ; -> fBS2HkScaleSC = 0.14287498593330383 (0x3E124DD2)
0x00F352B0  fld   dword ptr [0x01012050]  ; = 10.0
0x00F352B9  fmul  qword ptr [0x010120F0]  ; = -9.8100004196167 (0xC0239EB860000000)
0x00F352BF  fstp  dword ptr [0x011C32D0]  ; -> fGravitySC = -98.10000610351562 (0xC2C43334)
```

Both double literals are float expressions widened to double: `0x40517F70C0000000` is exactly
`(float)((128.0f/6.0f) * 3.2808399f)` = `0x428BFB86` (verified in `tight_scale_scan.log`; the
X360 compiler evaluated the same expression in double and rounded to `0x428BFB85`, one ULP
lower — hence the 1-ULP platform difference in `fHk2BSScaleSC`, D6 on PC vs D5 on X360), and
`0xC0239EB860000000` is `(double)(-9.81f)`. The two literals sit at 0x010120E8/0x010120F0 in
`.rdata` and are byte-identical in the on-disk Steam exe.

Runtime census of the populated `.data` (`pc_fnv_step2.log`): 488 copies of `0x40DFF8D6`
(6.999125), 435 copies of `0xC2C43334` (-98.1), 477 copies of `0x3E124DD2` (0.142875). On disk
only 43 and 3 of those are folded into the raw `.data` (the rest are BSS), which is why a scan
of the Steam exe alone finds neither 6.999125 nor 0.142875: the value must be taken from the
runtime dump or derived from the literals above. The 1.0 disc exe (fnv_disc10) shows the same
43/3 folded copies.

World creation (`pc_fnv_step3.log`): `bhkWorldCinfo::bhkWorldCinfo` at 0x00C681C0 stores the
default gravity (0, -98.1, 0, 0) into cinfo+0x10 (`movss xmm2,[0x011AFED4]=fGravitySC`,
`unpcklps` shuffles, `movaps [esi+0x10]`). Its callers at 0x00552E4D and 0x00554072
(the interior and exterior `TESObjectCELL::CreateWorld` paths, **inferred** by shape against the
named X360 function) then push (0, 0, `[0x011CA158]` = -98.1, 0) and call a `hkVector4::set` on
the cinfo, i.e. (0, 0, -98.1) Z-down; the exterior path converts the broad-phase size
`147456.0` (36 cells × 4096 units) through `NI2HK` (0x004A3E90) before
`setBroadPhaseWorldSize`, the interior path passes 3250.0 Havok units directly.

Cross-check runtime image vs. on-disk Steam exe (`versions_and_identity.log`): `.text` differs
on every one of its 4 KiB pages (3,037 full pages plus the partial tail; the first revision's
"3,038" counted the tail as a page) — the on-disk code is Steam-DRM-encrypted (the `.bind` stub
decrypts it at load), which is what the runtime dump exists for, so every x86 disassembly above
is from the dump; `.rdata` differs on three full pages (indices 0, 171 and 271: the loader-patched
import table and two others) plus the partial tail (the first revision's "4 of 420"), and the two
double literals plus all 492 `fHkScaleSC` copies are byte-identical on disk; the raw part of
`.data` differs on 43 of 65 pages where the process wrote.
`FalloutNV_PC.exe` and the Steam `FalloutNV.exe` differ in 4 of 4,040 pages, all in the DRM
stub, so they are the same 1.4.0.525 build.

## 5 Oblivion (PC 1.2.0416, no symbols)

Oblivion keeps the conversion constants as x87 double literals (VS2005-era code):

| Constant | Address | Value | References |
|---|---|---|---|
| `fHk2BSScaleSC` | 0x00A372E0 (`.rdata`, f64) | 6.999040126800537 (= `0x40DFF823` widened) | 27 (5 `fld`, 22 `fmul`); the function at 0x0043F3E0 multiplies an `hkVector4`'s x, y, z by it into an `NiPoint3` — the `HK2NI(NiPoint3&, const hkVector4&)` shape |
| `fBS2HkScaleSC` | 0x00A39088 (`.rdata`, f64) and 0x00A56118 (f32 `0x3E124E47`) | 0.1428767293691635 | 88 + 2; the function at 0x004529E0 multiplies x, y, z by it — the `NI2HK` shape; 0x005F2C48 broadcasts the f32 copy with `shufps`/`mulps` |
| `fGravitySC` | 0x00A46B20 (`.rdata`, f32) | -73.57500457763672 (`0xC2932667`; the first revision misquoted the pattern as `0xC2932666`) | 3: the `bhkWorldCinfo` constructor at 0x0088A4F0 (calls Havok's `hkpWorldCinfo::hkpWorldCinfo` at 0x008A9510, whose own defaults are the immediates (0,-9.8,0) and ±500 broad-phase, then overrides cinfo+0x10 with (0, -73.575, 0, 0)), and the two `CreateWorld` paths at 0x004D4A8F / 0x004D5047, which build **(0, 0, -73.575, 0)** on the stack and `movaps` it into the cinfo |
| broad-phase size | 0x00A46B30 (f32) | 21068.03125 | the exterior `CreateWorld` path; = `147456 × 0.1428767` folded (IEEE single, verified: `0x46A49810`); interior path uses 3000.0 |

Arithmetic identity (verified in `fo3_obl_scale_xrefs.log`): `(128.0f/6.0f) * 3.2808f` =
69.99040222 (`0x428BFB16`), divided by 10 = 6.999040127 (`0x40DFF823`), and
`1/6.999040127` = 0.1428767294 (`0x3E124E47`). So Oblivion's chain is the FNV chain with a
four-digit feet-per-meter constant; the `/10` step is why `fHkScaleSC = 10` is **inferred** for
Oblivion (there is no named static and no `-9.81 × 10` initializer to read: the gravity literal is
already folded).

Gravity: -73.575 Havok units/s² = 0.75 × 98.1. With the chain above (1 Havok unit = 6.99904
units = 0.1 m) that is **7.3575 m/s², 0.75 g**. The one ULP of the bit pattern is load-bearing:
`0xC2932667` (-73.57500457763672) is exactly `0.75 × 0xC2C43334` in single precision, where
`0xC2C43334` (-98.10000610351562) is the FO3/FNV constant `10 × (-9.81f)` (and equally
`f32(7.5f × -9.81f)`), whereas a hand-typed decimal `-73.575` would have rounded to `0xC2932666`,
as would `f32(-9.81 × 7.5)` evaluated in double (`verify-re1/v1_constants.log`, `v6_followups.log`,
re-verified here with `struct`: `f32(-98.1)` itself is `0xC2C43333`, so the -98.1 constant is the
`-9.81f × 10` product and Oblivion's literal descends from that product). So the literal was
FOLDED from the -98.1 constant times 0.75, which is the derivation the "gravity tuned to 0.75 × the
98.1 constant" reading predicts and the "52.5 units per meter" reading does not. Which of the two
the designers MEANT is still not stated by the executable, so the choice stays OPEN and **inferred**
(see §10, follow-up 3), but the derivation chain is now read, not guessed. The 7.5 literal in the
exe (0x00A58F98) is the default of the game settings `fArrowBounceRotateSpeed` and
`fWeaponClutterKnockMinClutterMass`, not a Havok scale (`oblivion_step4.log`).

## 6 Fallout 3 (PC 1.7.0.4, VC2019 rebuild, no symbols)

Everything is constant-folded into f32 literals:

| Constant | Address | Value | References |
|---|---|---|---|
| `fBS2HkScaleSC` | 0x00F410F0 (`.rdata`; a second copy at 0x00F52414 is an UNREFERENCED duplicate, 0 code references), plus 3 `mov ..., 0x3E124E47` immediates (0x009909A6, 0x00993096, 0x00998571) | 0.1428767293691635 (`0x3E124E47`) | 232, all to 0x00F410F0 (192 `movss`, 34 `mulss`, 6 `fmul`) |
| `fHk2BSScaleSC` | 0x00F5174C (`.rdata`) | 6.999040126800537 (`0x40DFF823`) | 169 (130 `movss`, 37 `mulss`, 2 `fmul`); e.g. 0x00603BA7 multiplies a motion-state position's components — the same `and eax,0xFFFF0000 / cmp eax,0x10000` guard the Skyrim SE `bhkRigidBody` position getter has (0x1402236D9) |
| `fGravitySC` | immediates 0x009FB32A and 0x009FBD84 (`mov [esi+0x14], 0xC2C43334`) and `.rdata` 0x00FAA7FC; vector literal 0x00F75740 = (0, 0, -98.1000061, 0) | -98.10000610351562 | `bhkWorldCinfo::bhkWorldCinfo` at 0x009FB310 sets the default (0, -98.1, 0, 0) then +0x50 = 0.1, +0x24 = FLT_MAX-ish, sim-type byte from 0x011178E0; the `CreateWorld` callers at 0x007426FE / 0x0074696F load the **(0, 0, -98.1, 0)** vector from 0x00F75740 and store it to cinfo+0x10, then `setBroadPhaseWorldSize(21068.03125)` (exterior, immediate `0x46A49810` = 147456 × 0.1428767 folded) or 3250.0 (interior) |

Same chain as Oblivion (`(128/6) × 3.2808 / 10`, so `fHkScaleSC = 10` **inferred**; gravity
-98.1 = -9.81 × 10 supports it). The `.data` copies of 10.0 (84), 128.0 (15) and 6.0 (29) are
settings defaults, not the scale (`fo3_step3.log`). The 9.81 literal at 0x00FA8960 is used only
as Havok's "if |gravity| == 0 use 9.81" fallback in the character-state code (0x00A15668,
0x00A1614B) and in projectile range math (0x00BD1DB9, 0x00BD28A2).

## 7 Skyrim LE and SE

### 7.1 LE (TESV.exe 311e7173..., named through TESV.map)

`??__EfHkScaleConversionSC@bhkConvert@@YAXXZ`, `??__EfHk2BSScaleSC@bhkConvert@@YAXXZ`,
`??__EfBS2HkScaleSC@bhkConvert@@YAXXZ` and `??__EfGravitySC@HavokWorldProps@@YAXXZ` exist once
per object file (987 `fHk2BSScaleSC` initializers). The AlchemyItem.obj set (`skyrim_le_convert.log`):

```
??__EfHkScaleConversionSC  @0x0118FB00: fld [0x01382FEC]=1.0 ; fdiv [0x01382FE8]=10.0 ; fstp [0x0160E47C]   -> 0.1
??__EfHk2BSScaleSC         @0x0118FB20: fld [0x0138338C]=128.0 ; fdiv [0x01383388]=6.0 ; fmul [0x01383384]=3.2808399
                                          ; fdiv [0x01382FEC]=1.0 ; fstp [0x0160E434]                     -> 69.99125
??__EfBS2HkScaleSC         @0x0118FB50: fld1 ; fdiv [0x0160E434] ; fstp [0x0160E3FC]                      -> 1/69.99125
??__EfGravitySC            @0x0118FB70: fld [0x01383390]=-9.81 ; fmul [0x01382FEC]=1.0 ; fstp [0x0160E470] -> -9.81
```

So Skyrim moved Havok to meters (`fHkScaleSC = 1.0`; the `/10.0` in `fHkScaleConversionSC`
is the ratio to the FO3/FNV scale), and the source formula is visible in full:
`fHk2BSScaleSC = (128 / 6) × 3.2808399 / fHkScaleSC`. The map has no symbol for the per-CU
`fHkScaleSC` const itself (its nearest public is a string), so `1.0` is read as the divisor
in that formula. The runtime values are BSS; by IEEE x87 arithmetic `fHk2BSScaleSC` =
69.99124908 (`0x428BFB85`) and `fBS2HkScaleSC` = 0.0142875006 (`0x3C6A161F`) (**computed**).

`?HK2NI@@YAMM@Z` (0x00453430) is `x * fHk2BSScaleSC(this CU)`, `?NI2HK@@YAMM@Z` (0x0048BD00)
is `x * fBS2HkScaleSC(this CU)`. `??0bhkWorldCinfo@@QAE@XZ` (0x00EC6850) sets gravity through
`hkVector4::set(0, 0, [0x01767F94], 0)` where 0x01767F94 is bhkWorld.obj's `fGravitySC`
(written by `??__EfGravitySC@HavokWorldProps@@` at 0x01313320, `-9.81 × 1.0`), i.e.
**(0, 0, -9.81)** Z-down (`skyrim_le_gravity_global.log`). `??0bhkWorld@@` calls
`?Init@bhkWorld@@`, which constructs `ahkpWorld` from that cinfo. `CombatUtilities::Init`
(0x009252B0) stores `-HK2NI(GetGravity().z)` = 686.6 units/s² into
`?fWorldGravity@CombatUtilities@@1MA` (0x016E6320).

### 7.2 SE (SkyrimSE.exe 846efccf..., x64, no symbols)

All folded to literals (`xrefs_skyrim_se.json`, `skyrim_se_step*.log`):

| Constant | Address | Value | References |
|---|---|---|---|
| `fHk2BSScaleSC` | 0x1417F423C | 69.99124908447266 (`0x428BFB85`) | 118 RIP-relative (e.g. 0x1401A7059 scales x, y, z of an `hkVector4`) |
| `fBS2HkScaleSC` | 0x1417FDE5C (+ vector literal 0x1418E9D60, + immediate at 0x1402ECDCD) | 0.014287499710917473 (`0x3C6A161E`) | 183 + 9 + 1 (e.g. 0x1402CCC72 scales x, y, z; 0x1402CC90C multiplies the exterior broad-phase 147456 by it) |
| gravity vector | 0x141A7D480 | (0, 0, -9.81, 0) | `bhkWorldCinfo` constructor at 0x141048AA0: `movaps xmm0,[0x141A7D480]` then `movups [rbx+0x10]` |

`fHkScaleSC = 1.0` and `fGravitySC = -9.81` for SE are **inferred** from those literals (the
x64 compiler left no initializer to read).

## 8 Player height, eye height, capsule

Searched: every printable string in `.rdata`/`.data` of FNV, FO3, Oblivion, Skyrim LE and SE for
`Height`, `EyeHeight`, `PlayerHeight`, `Capsule`, `CharController`; every `S_LDATA32`/`S_GDATA32`
name in the X360 PDB for the same; the Skyrim map for `Height|Radius|Capsule`.

- No executable holds a player-height or eye-height constant. The height-like names are game
  settings whose values live in the ESM/INI (`fJumpHeightMin`, `fJumpFallHeightMin`,
  `fVATSSmartCameraCheckHeight`, `fMagicLightHeightOffset`, Skyrim
  `fCharControllerCheckHeightOffset:Camera`), FaceGen eye-offset emotion settings, and
  renderer settings (`fEyeAdaptSpeed`, `fEyeEnvMapLOD*`).
- Skyrim `?GetEyeHeight@PlayerCharacter@@QAEMXZ` (0x007C3670) returns the member at +0x59C;
  `?Get3rdPersonCameraEyeHeight@PlayerCamera@@` reads a camera state; both are runtime data.
- `capsuleRadius` / `capsuleHeight` (Skyrim 0x01465F58/68) are Havok behavior-graph
  property names, not constants.
- The X360 PDB's other height-like names, so a later reader does not rediscover them: the
  navmesh cover constants `NAVMESH_COVER_HEIGHT` / `NAVMESH_COVER_MAX_HEIGHT` /
  `NAVMESH_COVER_HEIGHT_GRANULARITY` (147 `S_LDATA32` copies), `fedgeAdditionalHeight` = 2.0
  (0x83247774), and a function-local static `fheight` = 1.3 f32 (`S_LDATA32 [0007:00040FEC]` =
  0x832513EC, scoped inside `Actor::Update`; `verify-re1/cvdump_s_mine.txt` line 129010). None is
  a player or eye height; the rest of the PDB's `*Height*`/`*Capsule*`/`*Radius*` statics are
  FaceGen, renderer and Havok reflection-table names.

Verdict: **not found** in any executable. The character capsule and eye height come from data
(race height, skeleton collision) and are outside RE-1's executable scope.

## 9 Meters per unit: what the code fixes and what the formula adds

Two derivations, kept apart as §4.3 asks.

**(a) The engine's own length chain** (read from the initializers): 128 units = 6 feet.

| Game | units per Havok unit × Havok units per meter | units per meter | meters per unit |
|---|---|---|---|
| FNV PC | 6.9991255 × 10 | 69.991255 (`0x428BFB86` widened) | 0.014287499 (exactly 1.8288 m / 128 = 0.0142875 at the literal level) |
| FNV X360 | 6.999125 × 10 | 69.99125 | 0.0142875 |
| Skyrim LE / SE | 69.99125 × 1 | 69.99125 | 0.0142875 |
| FO3, Oblivion | 6.99904 × 10 | 69.9904 | 0.01428767 (1.8288 m / 128 with 3.2808 ft/m; 0.0012 % above FNV's) |

**(b) The §4.3 gravity formula** `meters per unit = 9.80665 / (|g_havok| × unitsPerHavok)`,
which assumes the designers set Earth gravity (the code uses `9.81`, so the formula is
0.034 % off its own inputs by construction):

| Game | \|g_havok\| (read) | unitsPerHavok (read) | g in units/s² | meters per unit by (b) |
|---|---|---|---|---|
| FNV PC / X360 | 98.1000061 | 6.9991255 / 6.999125 | 686.62 | 0.0142825 (1/70.016) |
| FO3 | 98.1000061 | 6.99904 | 686.61 | 0.0142827 |
| Skyrim LE / SE | 9.81 | 69.99125 | 686.61 | 0.0142826 |
| Oblivion | 73.575 | 6.99904 | 514.95 | 0.0190437 (1/52.51) — disagrees with (a) by exactly 4/3 |

For FNV, FO3 and Skyrim (a) and (b) agree once the code's own `9.81` replaces 9.80665
(`9.81 / (98.1 × 6.999125) = 1/69.99125` exactly), so the "assumed Earth gravity" caveat is
discharged by the literal in the binaries. For Oblivion the two disagree; the exe cannot say
which one the designers intended (§5).

Recommendation for `GameProfiles` (owner's call): `ClassicWorldUnitsPerMeter` = 69.99125 for
FNV and Skyrim (provenance ReverseEngineered, this file), 69.9904 for FO3 and Oblivion, with
`0.0142875` / `0.01428767` as the meters-per-unit literals; the existing comment "1 unit =
1.42875 cm" (GameProfiles.cs:45-50) was right and `70f` is its rounding. Today the profiles keep
1/70 (Assumed) with this file cited in their evidence, because adopting 69.99125 moves
`HumanScaleFactor` off its bit-exact 1 for every Gamebryo game and the camera constants would have
to be re-pinned — the owner's decision, not this session's. The Havok
collision multiplier in `HavokCollisionExtractor` (`7f`, Core/Formats/Nif/Collision/
HavokCollisionExtractor.cs:33-36) is 6.999125 for FNV and 6.99904 for FO3/Oblivion; Skyrim
files need 69.99125.

## 10 What only a Ghidra run (or a data oracle) can settle

1. **FNV X360 VMX128 stores.** capstone cannot decode the Xenon VMX128 opcodes at
   0x823943F8-0x823943FC (`TESObjectCELL::CreateWorld`), 0x82395548-0x8239554C
   (`TESObjectCELL::InitHavok`) and 0x82B137D0-0x82B137D4 (`bhkWorldCinfo` ctor). They decode by
   the public VMX128 encoding as `lvx128 v63,r0,r9` / `stvx128 v63,r0,r8` (and `lvx128 v63,r0,r6`
   / `stvx128 v63,r31,r4` in the ctor), which puts the (0, 0, fGravitySC, 0) stack vector into
   `hkpWorldCinfo::m_gravity` (cinfo+0x10) — §3. The encoding table is recalled (Xenia/free60), so
   Ghidra's `PowerPC:BE:64:Xenon` is now the CONFIRMATION of that xop table rather than the only
   source. Same for `HK2NI` at 0x8245BE40 (its multiply at 0x8245BE64, primary opcode 6, is a
   VMX128 arithmetic form the recalled table does not cover).
2. **The unnamed X360 `bhkWorldCinfo` constructor** at 0x82B13750 (the PDB has no proc there;
   nearest symbol is `hkVector4::setTransformedInversePos+0x2258`). Confirm the class from the
   vtable it stores (0x8210725C, `lis r8,-0x7DF0; addi r5,r8,0x725C; stw r5,0(r31)`).
3. **Oblivion's 0.75 g.** Decide between "gravity tuned to 0.75 g" and "52.5 units per meter"
   with a data oracle: the player race height and the skeleton's character capsule
   (`bhkCharControllerShape`/capsule in `skeleton.nif`), or the jump-height settings against
   `fJumpHeightMin`. Also confirm the unnamed functions 0x004D4A8F / 0x004D5047 are
   `TESObjectCELL::CreateWorld` and 0x008A9460 is `hkpWorldCinfo::setBroadPhaseWorldSize`.
4. **FO3 original vs. VC2019 rebuild.** The 3.2808 literal is measured on the 2026 Steam
   Fallout3.exe (VC2019). `Sample/ReverseEngineering/Fallout 3 (PC)/Fallout3_PC.exe` is the
   same size and was not opened; an original 2008/2009 exe would show whether 3.2808 predates
   the rebuild.
5. **Skyrim LE runtime values.** `fHk2BSScaleSC`/`fBS2HkScaleSC` are BSS; the values quoted are
   IEEE derivations of the read literals, not bytes. A debugger or Ghidra p-code emulation of
   `??__EfHk2BSScaleSC@bhkConvert@@YAXXZ` would pin them; the Steam 1.9.32 exe
   (skyrim_le_steam) was only constant-scanned, not cross-referenced.
6. **Skyrim SE `fHkScaleSC` and `fGravitySC`** are inferred from the folded literals; SE has no
   initializer to read. The function at 0x1403F9C90 that uses the -9.81 literal beside the table
   (0, 0.05715, 0.71, -9.81, -69.99125) at 0x141865B70 was not identified.
7. **Retail X360 `default.xex` and PS3.** Not opened (XEX2 container); the MemDebug PE stands in
   for the X360 code.

## 11 Scripts (scratchpad `cut1a-prep/re1/`)

- `re1_common.py` — PE/memory-dump loader into VA space (pefile), SHA-256, candidate table.
- `scan_constants.py` — exact f32/f64 byte scan of every section for the candidate constants;
  writes `constants_<key>.json`, log `scan_constants.log`.
- `ppc_xref.py` — PowerPC BE helper: cvdump symbol table (S_GPROC32/S_LDATA32 -> VA), `bl`
  caller search, `lis`+D-form absolute-reference sweep, word-by-word capstone dump that
  survives VMX128 opcodes. Logs `ppc_step1..6.log`, `ppc_refs.json`. ⚠ Its `abs_refs` sweep is a
  FORWARD tracker that keeps a pending `lis` alive across later writes to the same register, so
  it over-counts: a D-form whose base register was reloaded in between is summed with the stale
  high half (the `HighProcess::ProcessTravel` false positive in §3). Every reference count taken
  from it is an upper bound; the review's backward sweep with write invalidation
  (`verify-re1/v2_x360.py`, `v2b_window.log`) is the one to reuse.
- `x86_xref.py`, `run_x86_xrefs.py` — capstone `disasm_lite` sweep for absolute (x86) and
  RIP-relative (x64) data references and float immediates; TESV.map symbolizer.
  Outputs `xrefs_oblivion*.json`, `xrefs_skyrim_se.json`.
- `vec_scale_hunt.py`, `bss_scale_census.py` — the two negative probes (three-in-a-row
  multiplies; BSS multiplicands) that showed FO3/Oblivion keep no runtime scale global.
- Inline scripts preserved as logs: `pc_initializers.log`, `pc_fnv_writers.log`,
  `pc_fnv_step2/3.log`, `fnv_data_neighbors.log`, `skyrim_le_funcs.log`,
  `skyrim_le_gravity_global.log`, `skyrim_le_convert.log`, `skyrim_se_step*.log`,
  `oblivion_world.log`, `oblivion_step2..4.log`, `oblivion_bss.log`, `fo3_step*.log`,
  `fo3_bss.log`, `fo3_obl_scale_xrefs.log`, `tight_scale_scan.log`,
  `versions_and_identity.log`, `sha256.txt`, `cvdump_memdebug_s.txt` (74 MB).

Peak memory of any step was well under 1 GB (the largest input held in memory is the 37.9 MB
SkyrimSE.exe; the 74 MB cvdump text is streamed line by line).

## 12 Review corrections (2026-09-23)

An independent re-derivation (scratchpad `cut1a-prep/verify-re1/`: its own pefile loader, byte
scans, backward `lis` sweeps with register-write invalidation, capstone dumps, a fresh cvdump run
of the PDB and a TESV.map parser; all 11 file hashes re-verified, every bit pattern recomputed in
IEEE arithmetic) confirmed every value in §1 and corrected the following in this revision:

| Item | First revision | Corrected | Evidence |
|---|---|---|---|
| Oblivion gravity literal at 0x00A46B20 | `0xC2932666` | `0xC2932667`; it is `0.75 × 0xC2C43334` exactly and cannot be a decimal -73.575 (§5) | `v4_fo3_obl.log`, `v6_followups.log`, re-verified with `struct` |
| `fHkScaleSC` readers | the two initializers and `HighProcess::ProcessTravel` | the two initializers only; ProcessTravel was a forward-tracker false positive (§3) | `v2d_processtravel.log`, `v2e_falsepos.log` |
| `fBS2HkScaleSC` consumer count | 818 references in 306 functions | approximate, sweep-window dependent (105/92 at 16 words, 193/157 at 64, 455/294 at 256); named consumers confirmed (§3) | `v2b_window.log` |
| FO3 `fBS2HkScaleSC` copies | "0x00F410F0 and 0x00F52414, 232 references" | all 232 go to 0x00F410F0; 0x00F52414 is an unreferenced duplicate (§6) | `v4_fo3_obl.log` |
| `bhkConvert::fHkScaleSC` | stated as PDB-named | the PDB names the static bare; the namespace is inferred from the initializers (§1) | `v2_x360.log` named-statics census, `v5_skyrim.log` |
| X360 VMX128 store to `m_gravity` | register analysis (inferred) | decoded by the public VMX128 encoding (`lvx128`/`stvx128`), Ghidra confirms the xop table (§3, §10) | `v2_x360.log` raw-word decodes |
| Height-like PDB names | "none outside FaceGen, the renderer and Havok" | plus `NAVMESH_COVER_*` ×147, `fedgeAdditionalHeight` 2.0, `fheight` 1.3 (Actor::Update local static); verdict unchanged (§8) | `cvdump_s_mine.txt` |
| Page counts, runtime vs disk | ".text all 3,038 pages, .rdata 4 of 420" | 3,037 full `.text` pages plus the tail; `.rdata` pages 0, 171, 271 plus the tail (§4) | `v3_fnv_pc.log` |

Nothing in §9 (meters per unit) changes: the length chain and the gravity formula rows were
reproduced exactly, and the 1/70 probe stands — the f32 1/70 (`0x3C6A0EA1`) occurs zero times in
the `.rdata` of all six executables, and every `70.0` reference is HUD, audio or imagespace code
(`v2_x360.log`, `v3_fnv_pc.log`, `v4_fo3_obl.log`, `v5_skyrim.log`).
