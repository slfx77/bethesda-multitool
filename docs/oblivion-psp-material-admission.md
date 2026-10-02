# Oblivion PSP material admission prerequisites

This September 20, 2026 inspection records source evidence for the next
application-owned PSP increment. It does not implement a normalized adapter or
establish rendering, export, animation or material acceptance. The preceding
[CLUMP graph increment](oblivion-psp-clump-decoding.md) remains the geometry
prerequisite.

## Bounded source inspection

The private source was read in place at
`Sample/Builds/The Elder Scrolls Travels - Oblivion (2007-4-27, PSP - Prototype)/PSP_GAME/USRDIR/GR.ARC`
under the existing external corpus root. An independent Python chunk walk
inspected this one build: 87 entries, 39,065,901 entry bytes, 598 named resources,
111 CLUMPs and 1,304 raster occurrences. Each entry read was capped at 4 MiB and
the census entry-byte budget was 40 MiB. No private payload was staged or copied
to output files. These counts do not describe the other five builds.

The CLUMPs contain 694 material texture references. Every raw sampler word is
`0x11102`. Of these references, 691 have at least one exact-name raster in their
own archive entry. Three have none there and have exact-name candidates elsewhere
in the pack. This is a candidate census, not proof of texture resolution or runtime
precedence. There are 180 raster names occurring in multiple entries. Names such
as `Hrud`, `sukna`, `cuirass5`, `greaves` and `boots` occur with differing raw raster
body hashes, so a global name dictionary cannot safely collapse their identities.

## Pinned arrow and texture candidates

The `Weapons` entry starts at archive byte 37,540,384. Its CLUMP resource ordinal
39 starts at entry byte 1,292,860 and has a 5,514-byte root chunk, SHA256
`A4CEA62C4591415C9CAC4DFC9EE5CED2838A9EC5666F095D73198D087C405998`.
The existing `FixedEbonyArrowMatchesIndependentGeometryMaterialsAndChunkBoundaries`
case pins this original graph. Its two texture names are `Ebony_Arrow01` and
`Ebony_Arrow02`, both with an empty mask name and sampler word `0x11102`.

The same entry has one texture dictionary: resource ordinal 0, entry byte 236,
root length 760,296 bytes, containing 172 rasters. Its exact-name arrow candidates
are:

| Raster | Struct offset within entry | Body SHA256 | Independently decoded RGBA SHA256 |
| --- | --- | --- | --- |
| `Ebony_Arrow01` | 193,472 | `6CB2E380F3DE83A698CF48132654435C251B244656993E8D7E1F3698571D7B0D` | `5660AF1830471DB634ABCD0A671885E6A5840623A565F5EF3BCCCADE9B0C3A80` |
| `Ebony_Arrow02` | 184,048 | `D0C5B903666F63678D87D78F43934A61D6CEE30015539BBE294AF4711857CC89` | `4E385E6623CA1256C07EF121B1074E39CCC7784B9362F4F792CD2A238532DF19` |

Both raster Struct bodies are 9,388 bytes, 64 by 128 pixels, Indexed8, with exactly
one mip by the encoded byte count. Independent palette lookup finds alpha 255 for
all 8,192 used pixels in each image. This supports a bounded same-entry resolver
fixture. It does not establish the material's lighting behavior. The source also
has HAnim plugin `0x11E`; absence of a Skin plugin is insufficient for admission.

## Cross-entry scope remains unresolved

`CContainerBehaviour.Goodies`, `Goodies2` and `Goodies3` each reference
`Cloth_Sack` from CLUMP resource ordinal 1, at entry offsets 556, 560 and 560.
Their own texture dictionary resource ordinal 0, at entry offset 264, has a
40-byte root and zero rasters (raw dictionary Struct word `0x00090000`). All three
empty dictionary roots have SHA256
`BE3C72F9B44563157785FA9ABE3B873C49AADC060A1A633007F888DE002969A6`.

The only exact-name raster candidate in this build is in
`CContainerBehaviour.ClothSack`. Its dictionary resource ordinal 0 is also at
entry offset 264, with a 2,360-byte root and one raster (word `0x00090001`).
The four diagnostic authoring paths share the basename
`{927ee0c3-dba7-4152-a744-f1c5c74bab2b}.txd`, but their full paths and wrapper bytes
differ. This basename is not an established dependency key.

These entries contain class-registry, named-resource and bulk-data chunks. The
bulk data remains uninterpreted; no decoded dependency or load-order relationship
proves when the external candidate is available or which scope wins. Preserve
entry/resource/dictionary occurrence identity. Accept an explicitly selected
source scope and report missing or ambiguous candidates; do not invent local,
global, first-match or last-match precedence from names or authoring paths.

## Existing authorities and remaining interpretation

- [RwClumpReader](../src/BethesdaMultitool/Core/Formats/RenderWare/RwClumpReader.cs)
  retains original row-vector frame matrices, material fields and texture sampler
  words. The pinned arrow and translated-rigid cases in
  [RwClumpRetailTests](../tests/BethesdaMultitool.Tests/Core/Formats/RenderWare/RwClumpRetailTests.cs)
  establish exact source values and parent placement. Neither proves a global
  coordinate basis, handedness or units. No PSP-specific basis conversion has
  been established by this inspection.
- The arrow's raw material is white, with ambient/specular/diffuse factors all
  equal to one. Its UVs extend beyond the unit interval. These values do not prove
  an unlit or PBR policy, an alpha-test threshold, culling, or the full meaning of
  the sampler word. Preserve them until the application has verified their PSP
  interpretation; do not substitute normalized defaults.
- [RwPspTexture](../src/BethesdaMultitool/Core/Formats/RenderWare/RwPspTexture.cs)
  originally validated encoded mip-chain byte counts but exposed only decoded
  base pixels. The authored-mip continuation now decodes every admitted level
  and retains them through `ToDecodedTexture`, without generating replacements.
  Its eight synthetic and one original-raster checks pass on Windows and Ubuntu
  WSL; the [receipt](validation/travels-fidelity-20260920.md) keeps Linux runtime
  evidence distinct from source-build acceptance. A future normalized adapter must also retain these levels through
  the existing Shared image contract before claiming texture export fidelity.
- The sibling NMT [RwGeometryWriter](C:/dev/Multitool/NeversoftMultitool/src/NeversoftMultitool/Core/Formats/Mesh/Conversion/RwGeometryWriter.cs)
  applies a THPS3 DFF Z-up rotation and uses
  PS2-specific GS alpha/color-key policies. Those donor implementations are not
  evidence of Oblivion PSP's coordinate or material rules. The PSP raster decoder
  likewise documents why NMT's PS2 native texture layout cannot be reused.
- [OblivionPspResourceReader](../src/BethesdaMultitool/Core/Formats/Travels/OblivionPsp/OblivionPspResourceReader.cs)
  supplies source payload offsets and diagnostic authoring paths, not runtime
  dependency precedence. Current Shared
  [ModelDocument](../shared/Multitool.Shared/src/Slfx77.Multitool.Core/Models/ModelDocument.cs)
  accepts exact graph and source identity;
  [SceneMaterial](../shared/Multitool.Shared/src/Slfx77.Multitool.Core/Models/SceneMaterial.cs)
  requires already-prepared application policy,
  [SceneSampler](../shared/Multitool.Shared/src/Slfx77.Multitool.Core/Models/SceneSampler.cs)
  requires explicit sampling, and
  [SceneImage](../shared/Multitool.Shared/src/Slfx77.Multitool.Core/Models/SceneImage.cs)
  can retain authored mips. None supplies missing PSP semantics.

The [source-scoped texture-dictionary reader](oblivion-psp-texture-dictionary.md)
and candidate resolver are now applied, with explicit missing, ambiguous and
incomplete-name outcomes, bounded admission and source nonmutation checks.
Their compiled checks and five original-fixture material-binding cases pass on
Windows and Ubuntu WSL in the [152-case receipt](validation/shadowkey-native-preview-20260920.md).
Those cases establish explicit dictionary candidates, raw material slots and
unchanged prelit/UV/pixel data. Cross-entry dependency interpretation,
coordinate proof and material admission remain separate work before a faithful
normalized adapter can be enabled.
