#!/usr/bin/env python3
"""nif_feature_probe.py -- independent NIF feature probe (design 7.1, oracle A1).

PURPOSE
    Reads Gamebryo NIF/KF files with a hand-written offset reader and emits, per file, the
    version key, the block-type census and a flat sorted list of feature TAGS ("name=value")
    with the literal numeric values behind them in a details object. The tags are the
    field-level values the cut-1a sample must cover (blend pairs, alpha test, clamp modes,
    UV transforms, texture slots, billboard modes, controller/interpolator chains, ...) and
    the values the reader under test must reproduce (oracle A1).

INDEPENDENCE RULE
    This script imports nothing from BethesdaMultitool and never shells out to it. It does not
    read nif.xml at run time. Every layout below was transcribed by hand from nif.xml
    (src/BethesdaMultitool/Core/Formats/Nif/nif.xml) for the keys in scope, and the header
    field order was confirmed against a hex dump of a retail X360 file. The BSA reader is a
    copy of the project's own independent Python BSA v103/104 reader (ddxm_common.py), not
    of BsaExtractor.

SCOPE
    Full walk: header string "Gamebryo File Format, Version 20.2.0.7", user version 11,
    BS stream versions 14, 21, 26, 32 and 34, little- and big-endian (the header's endian
    byte; X360/PS3 files are big-endian from the block-type table onward, while the user
    version, block count and BS version words stay little-endian). .nif and .kf alike.

    Animation walk (cut 1b, 2026-09-25): 20.2.0.7 / user 11 / BS 24, 25, 27, 28, 30, 31 and 33 are
    walked ONLY when every block in the file is one of ANIMATION_BS_TYPES: the 14 block types of
    the 505 FO3/FNV .kf at those streams (NiControllerSequence, NiTextKeyExtraData, the transform,
    B-spline, bool, float and point3 interpolators and their data, BSAnimNotes) plus BSAnimNote,
    BSRotAccumTransfInterpolator and BSTreadTransfInterpolator. Every field those types reach in
    nif.xml was evaluated at each BS value; the only BS-dependent ones are NiControllerSequence
    Anim Notes (vercond "(#BSVER# #GTE# 24) #AND# (#BSVER# #LTE# 28)"), Num Anim Note Arrays and
    Anim Note Arrays (vercond "#BSVER# #GT# 28") and ControlledBlock Priority (vercond
    "#BSSTREAM#"), and the parsers below honor all four. A file at those streams holding any
    other block type (every .nif: an NiNode at least) is DECLINED: nif.xml gives BS 24 and 25 an
    NiMaterialProperty layout no supported stream has (Ambient/Diffuse "#BSVER# #LT# 26" together
    with Emissive Mult "#BSVER# #GT# 21"), and the corpus holds no .nif at any of the seven.
    Since cut 2 (2026-09-28) BOTH scopes also walk the little-endian 20.0.0.4 .kf animation stream at
    user 10 or 11, BS 11 (LEGACY_KF_*: the Oblivion-era .kf identity, of which FNV ships five files; the
    reader's one pre-20.2.0.5 key), when every block type is one of the eight measured on those five and
    block 0 is the NiControllerSequence root (the C# probe's root rule); a 20.0.0.4 file of any other
    shape (big-endian, another user or BS version, a block type outside the eight, so every 20.0.0.4
    scene graph and the Oblivion .kf driving floats or bools, or another block 0) is declined with a
    reason naming the version. The C# gate applies the same rules (NifModelProbe.IsLegacyKfKey,
    LegacyKfBlockTypes, LegacyKfBlockTypeRejection), so the two instruments decline the same files.
    Every other key (3.3.0.13, 4.x, 10.x, 20.0.0.5, bs 83+, ...) is DECLINED with
    a reason; its block-type table is still listed when the header carries one.

    Scope parameter (2026-09-25). parse_header, walk and probe_bytes take scope='cut1a' (the DEFAULT)
    or 'cut1b'. 'cut1a' reproduces the cut-1a probe (sha256 b959692b...) exactly, so every existing
    caller and the checked-in cut1a-probe-expectations.jsonl are unchanged: BS 24, 25, 27, 28, 30, 31
    and 33 are declined with the cut-1a message, BSAnimNotes, BSAnimNote, BSRotAccumTransfInterpolator
    and BSTreadTransfInterpolator are skipped by size (unparsed), BSAnimNotes and BSAnimNote are of kind
    'unknown', and NiControllerSequence's anim-note refs take the cut-1a kinds. 'cut1b' is the widened
    walk described above (TestOutput/probe-widen-20260925/REPORT.md) and must be asked for explicitly.
    No environment variable selects the scope. Pass the SAME scope to parse_header and walk
    (probe_bytes does). CLI: --scope on every subcommand.

    Every parsed block must end exactly on its header-declared size, every reference must stay
    inside the block table, never point at the block itself and, where the field's type is
    known, point at a block of that kind (a child at an NiAVObject, an interpolator ref at an
    interpolator, ...); key times must be sorted and sampled skin weights must sum to 1. A
    violation is an error in `errors`, never a clean tag set. Zeroing a span that holds only
    payload (vertex floats, strip indices, packed console vertex bytes, an interpolator's
    scalars, a BSXFlags value) is NOT visible to these checks: the probe reads features, not
    payload integrity, and the `control` subcommand reports honestly which spans it caught.

    Block types parsed (all fields consumed): the NiNode
    family (NiNode, BSFadeNode, NiBillboardNode, NiSwitchNode, NiLODNode, BSOrderedNode,
    BSValueNode, BSRangeNode/BSBlastNode/BSDamageStage/BSDebrisNode, BSMultiBoundNode,
    BSTreeNode, BSMasterParticleSystem, ...), NiTriShape/NiTriStrips (+ BSSegmentedTriShape,
    BSLODTriShape) and their data, NiParticleSystem + NiPSysData, NiSkinInstance /
    BSDismemberSkinInstance / NiSkinData / NiSkinPartition, the render properties
    (NiAlphaProperty, NiStencilProperty, NiZBufferProperty, NiMaterialProperty,
    NiVertexColorProperty, NiTexturingProperty, NiSourceTexture, the BSShader*Property family,
    BSShaderTextureSet), lights, NiCamera, NiTextureEffect, extra data (BSXFlags, strings,
    BSBound, BSFurnitureMarker, NiTextKeyExtraData, ...), every controller in the FNV corpus
    (NiTimeController base + the per-type tails), every interpolator, the key data blocks,
    NiControllerManager / NiControllerSequence / NiDefaultAVObjectPalette, BSAnimNotes /
    BSAnimNote, BSRotAccumTransfInterpolator, BSTreadTransfInterpolator, and the
    bhk*CollisionObject wrappers. Havok shapes/bodies/constraints and particle modifiers are
    skipped by their declared size (no tags come from them).

PAYLOADS (opt-in, the cut-1b oracle, 2026-09-25)
    walk(data, h, payloads=True) and probe_bytes(..., payloads=True) (CLI: --payloads on probe and
    census) decode the animation payloads the default walk validates and skips. Each block below then
    carries ONE extra key, 'payload'; every other key of every block dict is exactly what the default
    walk produces. With payloads off (the default) the walk and every block dict are those of the
    cut-1a probe at the default scope, and those of the widened probe
    (TestOutput/probe-widen-20260925/nif_feature_probe.py) at scope='cut1b' (see SCOPE). Every float is given twice:
    as the Python float struct.unpack makes of the float32, and as its bits ('timeBits',
    'valueBits', ...): the IEEE-754 binary32 pattern as an unsigned integer, read in the file's byte
    order, so equal bits mean equal floats on every platform (a NaN payload need not survive a float
    round trip; the bits do). Byte values are integers and carry no bits. Layouts, transcribed from
    nif.xml (src/BethesdaMultitool/Core/Formats/Nif/nif.xml) at 20.2.0.7:
      key group  KeyGroup<T> (line 6424): Num Keys (uint, 6426); Interpolation (KeyType, 6428, present
                 only when Num Keys != 0; enum at 678: 1 LINEAR, 2 QUADRATIC, 3 TBC, 4 XYZ_ROTATION,
                 5 CONST); Keys (Key<T> x Num Keys, arg Interpolation, 6432). A Key<T> (6401) is Time
                 (float, 6406) and Value (#T#, 6409), then Forward and Backward (#T#, 6411 and 6414, cond
                 "#ARG# == 2") or TBC (6418, cond "#ARG# == 3"; struct TBC at 6388: t, b, c floats at
                 6390, 6394, 6395). #T# is byte, float, Vector3 (x y z, 5782) or Color4 (r g b a, 5691).
                 A LINEAR or CONST key is Time + Value. Payload of a group: {numKeys, keyType
                 (0 when Num Keys is 0: no Interpolation field), keyTypeName, valueType, keys: [{time,
                 timeBits, value, valueBits, then forward, forwardBits, backward, backwardBits (type 2)
                 or tbc, tbcBits (type 3: the three TBC floats in FILE order, see TBC ORDER)}]}.
      quat keys  QuatKey<Quaternion> (6437) at 20.2.0.7, type != 4: Time (float, 6444, since 10.1.0.106),
                 Value (6448; Quaternion w x y z in that order, 5883/5887/5891/5893), TBC (6452) when the
                 type is 3. "Never has tangents" (6438): a QUADRATIC quaternion key is Time + Value.
      NiTransformData / NiKeyframeData (12556 / 10805): Num Rotation Keys (10809), Rotation Type (10814),
                 Quaternion Keys (10820, cond "Rotation Type != 4") or XYZ Rotations (10827, three
                 KeyGroup<float>, cond "Rotation Type == 4"; Order, 10825, is until 10.1.0.0 and absent),
                 Translations (KeyGroup<Vector3>, 10832), Scales (KeyGroup<float>, 10836). Payload:
                 {rotation, xyzRotations (type 4 only: X, Y, Z in file order), translations, scales}. For
                 type 4 no quaternion key is stored (10820), so 'rotation' is {numKeys 0,
                 storedNumRotationKeys (the stored Num Rotation Keys, which 10809-10812 say must be 1),
                 keyType 4, keyTypeName, valueType, keys []}: numKeys == len(keys) for EVERY group of
                 every payload, and a loop over numKeys never indexes past the list.
      NiFloatData (10697), NiPosData (11473), NiBoolData (10365), NiColorData (10556): one Data
                 KeyGroup of float, Vector3, byte, Color4; the payload is that group. NiUVData (12724):
                 four KeyGroup<float>, payload {groups}. NiVisData (12771): Num Keys (12774) and Keys
                 (Key<byte>, arg 1, 12776): time + byte value, reported as a group of keyType 1.
      NiBSplineData (10473): Float Control Points (float x Num, 10476-10477) and Compact Control Points
                 (short x Num, 10482-10483), both arrays whole: {floatControlPoints,
                 floatControlPointsBits, compactControlPoints}. NiBSplineBasisData (10378):
                 {numControlPoints} (Num Control Points, 10382).
      B-spline interpolators (NiBSplineInterpolator, 9019): Start Time (9023) and Stop Time (9027) with
                 bits; the static value with bits: NiBSplineFloatInterpolator Value (10391),
                 NiBSplinePoint3Interpolator Value (10413) or NiBSplineTransformInterpolator Transform
                 (10436, an NiQuatTransform, 6834: Translation, Rotation w x y z, Scale; TRS Valid is
                 until 10.1.0.109 and absent); per channel its Handle (10396, 10418; 10438, 10442,
                 10447) and, on the compact forms, its Offset and Half Range with bits
                 (NiBSplineCompFloatInterpolator 10405/10407, NiBSplineCompPoint3Interpolator
                 10427/10429, NiBSplineCompTransformInterpolator 10456/10458, 10459/10461,
                 10463/10465). Channels: {float} | {position} | {translation, rotation, scale}.
    The probe does NOT dequantize a compact control point or resolve a handle: nif.xml says only that
    the shorts are "scaled by SHRT_MAX" (10483) and that a handle is a "Handle into the data" (10438),
    so the arithmetic is the consumer's reading, not a transcription. The ORDER of the interpolator's
    fields (Offset before Half Range, the channels translation, rotation, scale, a static rotation's w x y
    z) is invisible to the size check, since the candidates are equal-size floats; it is checked
    semantically instead (control_d_discrimination.py part 2: every driven channel's Half Range is
    positive, dequantized rotation control points are unit quaternions, handles tile the data block, and
    the static rotation of an undriven channel matches the skeleton's bind pose).

    TBC ORDER. 'tbc' lists the three floats of nif.xml's TBC struct (6388) in FILE order and names none
    of them. nif.xml calls them t, b, c (Tension 6390, Bias 6394, Continuity 6395); the engines read
    them as TENSION, CONTINUITY, BIAS. NiTCBFloatKey / NiTCBPosKey / NiTCBRotKey::LoadBinary store the
    three floats into consecutive members, and those members are named tension, continuity, bias by
    Fallout 4's own getters (NiTCBFloatKey::GetTension +8, GetContinuity +0xC, GetBias +0x10) and by a
    numeric emulation of CalculateDVals against the Kochanek-Bartels tangents, which passes for that
    assignment alone out of all six (Fallout 4 float and pos keys, Skyrim, the FNV runtime image and
    the FNV GECK): TestOutput/probe-payloads-20260925/tbc_engine_oracle.py and .json. So tbc[1] is
    CONTINUITY and tbc[2] is BIAS, and nif.xml's labels for the second and third float are swapped.
    TBC_FILE_ORDER names them. The rotation keys' three members are read the same way (+0x14, +0x18,
    +0x1C, consecutive) but their names rest on the float and pos keys' layout, not on a formula check.
    With payloads on, the key arrays and control-point arrays are read field by field through the
    bounded cursor instead of skipped by the stride table, so the block-size check (every block ends on
    its declared size) tests the decoded layout itself. The stride table still rejects an unknown key
    type and still feeds check_times, exactly as with payloads off.

TAGS (sorted, unique, "name=value")
    blend=SRC/DST            NiAlphaProperty blend enabled, source/destination factors
    alphatest=FUNC/T         alpha test enabled: test function and threshold
    blend+test=1             both enabled on one NiAlphaProperty
    nosorter=1               NiAlphaProperty No Sorter bit
    clamp=FAMILY/MODE        BSShader*LightingProperty texture clamp mode; FAMILY is PP, L30, No, Sky
                             or Tile (the property type, whose reader paths differ)
    texclamp=MODE            NiTexturingProperty map clamp mode (Oblivion-style)
    uvxform=1                a NiTexturingProperty map carries a UV transform
    apply=MODE               NiTexturingProperty apply mode
    texslots=0,1,2           BSShaderTextureSet slots that are non-empty ('none' when all are empty)
    normalalpha=1            Specular flag set with a normal map in slot 1 (PC: alpha = specular)
    speccompanion=1          X360 file whose slot-1 name follows the *_n convention, so a *_s specular
                             companion is expected beside it (the candidate name is in details on every
                             platform). The platform comes from --platform or, on 'auto', from an
                             "x360" / "ps3" token in the source path; a big-endian file whose platform
                             cannot be told carries no tag and details.platform says 'unknown'.
                             Measured 2026-09-23 on the retail texture BSAs: X360 ships 3,005 *_s beside
                             3,423 *_n (same stem); PS3 ships 4 *_s (3 paired) beside 3,430 *_n; PC 0.
                             So the companion convention is X360's alone and the header's endian byte
                             (shared with PS3) cannot carry it.
    shadertype=N             BSShaderType
    sf1=NAME / sf2=NAME      each set BSShaderFlags / BSShaderFlags2 bit
    shaderzbuf=test:N/write:N  ZBuffer_Test (sf1 bit 31) and ZBuffer_Write (sf2 bit 0)
    unlit=1                  BSShaderNoLightingProperty
    falloff=1                NoLighting falloff params differ from the exporter default (1, 0, 1, 0)
                             after rounding to 4 decimals (the default's stop angle is cos(90 deg))
    envscale!=1              environment map scale not 1
    refraction=1             PPLighting refraction strength not 0
    emitmult!=1              NiMaterialProperty Emissive Mult not 1 (bs > 21)
    emissive=1               NiMaterialProperty emissive color non-zero
    alpha!=1                 NiMaterialProperty alpha not 1
    vcolor=src:X/light:Y     NiVertexColorProperty source and lighting modes
    zbuf=test:N/write:N/func:F   NiZBufferProperty flags
    stencil=enable:N/draw:MODE/test:FUNC  NiStencilProperty flags
    hidden=1                 NiAVObject flag bit 0
    rot=nonorthonormal | rot=reflected   NiAVObject rotation checks
    billboard=N              NiBillboardNode mode
    switch=1 / lod=1         NiSwitchNode / NiLODNode present
    vcolors=1 uvsets=N normals=0|1 tangents=1   geometry data streams, emitted ONLY from blocks that
                             store the streams (NiTriShapeData / NiTriStripsData). NiPSysData and
                             BSStripPSysData at this key keep the Has bytes but no arrays (nif.xml
                             NiGeometryData: "Vertices, Normals, Tangents, Colors, and UV arrays do not
                             have length for NiPSysData regardless of Num or booleans"), so their bytes
                             describe no stream and go to details.particleData instead
    normals=nonunit          a sampled normal is not unit length (retail ships 14 such FNV files)
    packedgeom=1             BSPackedAdditionalGeometryData present (console vertex streams)
    additive+nonuniformvcolor=1  additive or mixed blend on a geometry whose stored vertex colors
                             were read and vary; never emitted when uniformity is unmeasurable
                             (particle data, packed console streams)
    additive+psys=1          additive or mixed blend on a particle system (the geometry whose color
                             uniformity cannot be measured, kept reachable for the cover)
    additive+lit=1           additive or mixed blend on a geometry whose shader is lit
    skin=1 dismember=1 partitions=N   skinning
    ctrl=Type->TargetType    every controller block and the type of its target
    ctrlcycle=LOOP|REVERSE|CLAMP, ctrlinactive=1   NiTimeController flags
    interp=Type              every interpolator reached from a controller, a sequence, a
                             morpher, a look-at or a blend item
    seqctrl=Type cycle=MODE  NiControllerSequence controlled-block controller types, cycle
    uvctrl=OP matcolor=NAME flip=N morph=N   controller specifics
    furniture=N              BSFurnitureMarker positions
    texeffect=clamp:MODE/type:N  NiTextureEffect

ANIMATION TAGS (scope 'cut1b' with payloads ONLY; 2026-09-25, the cut-1b cover vocabulary)
    probe_bytes(..., scope='cut1b', payloads=True) (CLI: --scope cut1b --payloads) adds the tags of
    animation_tags() to a walked file whose header lists a controller type or NiControllerSequence
    (is_animated). Every one starts with ANIMATION_TAG_PREFIX ('anim:'), so no other tag can collide with
    them; at any other scope or without payloads no such tag is emitted and every record is unchanged. The
    vocabulary is the plan's slice-0 item list (docs/design/cut1b-nif-animation-reader-plan-20260925.md,
    "Slice 0 detail"), transcribed from the seed cover scripts cut1b_cover_seed.py and
    review2_cover_seed.py so that a cover over these tags covers the seed's items:
    anim:animated                      the marker: every file the vocabulary describes carries it
    anim:key:<block>/<channel>/<KEY_TYPE>  a non-empty key group (channel rotation, xyz, translation,
                                       scale, data or uv); anim:key:negTime when a group's first time < 0
    anim:tbc:<channel>                 a TBC group; anim:tbc:<channel>:cneb when some key's second and third
                                       floats (continuity, bias; TBC ORDER) differ numerically
    anim:quad:<block>/<channel>:fneb   a QUADRATIC group of 2+ keys where some Forward != Backward numerically
    anim:quat:nonUnit                  a rotation key with | |q|^2 - 1 | > 1e-4 (double)
    anim:quat:dotNeg / anim:quat:ge170  adjacent LINEAR rotation keys with dot < 0 / 170 degrees or more
                                       between them; the ':unit' forms only in a group whose every key is unit
    anim:bspline:<type>/<channel>/handle|absent   a B-spline interpolator channel and its handle state
    anim:state:<type>/<channel>/<state>  the plan's section 1.3 channel state of every translation, rotation and
                                       scale channel of a transform interpolator (NiTransformInterpolator,
                                       BSRotAccumTransfInterpolator, NiBSplineTransformInterpolator,
                                       NiBSplineCompTransformInterpolator): keyed+fallback (keys with a valid
                                       static), keyed (keys, static #INV_FLT#), constant (no keys, valid static),
                                       notDriven (no keys, static #INV_FLT#) or mixed (some static components
                                       #INV_FLT#, some not). Keys means a non-empty key group in the NiTransformData
                                       (rotation: the quaternion group or any XYZ axis group) or a B-spline handle
                                       other than 0xFFFF; #INV_FLT# is -FLT_MAX (bits 0xFF7FFFFF, the only float32
                                       of that value, so the value test is the bit test)
    anim:ctrl:<type>|cycle=<c>|active=<0|1>|mgr=<0|1>   every NiTimeController by its flags
    anim:clock:sentinel | negStart | zeroLen | phase!=0   NiTimeController clock classes (sentinel =
                                       start +FLT_MAX with stop -FLT_MAX)
    anim:seq:cycle=<c>, anim:seq:multi, anim:seq:dupTarget   NiControllerSequence cycle, more than one
                                       sequence in the file, a node named twice under NiTransformController
    anim:seq:cb=<controller type>|<interpolator type>|ctrlRef=y|n   controlled-block pairs
    anim:seq:ctrlId=<controller type>|<id class>   controller IDs of controlled blocks with no controller ref
    anim:morph:embedded=<shape>, anim:seq:morph=<shape>   morph-weight shapes (same, differ,
                                       keyedPlusConst, allConst, none, other:<types>), Base excluded
    anim:text:crlf | twoEvents | empty  NiTextKeyExtraData key text shapes

CLI
    probe   <file.nif|.kf|.bsa> [--entry <path-in-bsa>] [--json] [--platform auto|x360|ps3|pc] [--payloads]
                                        [--scope cut1a|cut1b]
            --payloads decodes the animation payloads (see PAYLOADS); with --json they are listed in
            details.payloads as {i, type, payload} per block that has one. --scope: see SCOPE (default:
            cut1a); census and control take it too.
    census  <dir|.bsa> --out <jsonl> [--key-filter <substr> ...] [--ext .nif,.kf]
                                     [--no-details] [--limit N] [--platform auto|x360|ps3|pc]
                                     [--payloads]
            one JSON line per file: path (RELATIVE to source), source, platform, sha256, size, key,
            keyString, blockCount, blockTypes ({name: count}), tags, details, errors, declined, ms.
            --key-filter matches the key string "20.2.0.7/uv11/bs34/LE" (or "declined"); files are
            filtered on the header alone, before the walk.
    control <file> [--entry <path>] [--block N] [--offset K] [--length 64] [--set-bytes HEX]
                   [--expect-tag TAG] [--platform ...]
            mutates a copy in memory: zeroes LENGTH bytes at OFFSET inside block N (default: the
            first block, offset 0), or with --set-bytes writes those bytes there instead; re-probes
            the copy and reports one of three states: CAUGHT (error), CAUGHT (tags changed: +.. -..)
            or NOT CAUGHT (no error, tags unchanged). With --expect-tag the verdict is the oracle
            A1 rule: CAUGHT only when the tag the expectation names is reported on the original and
            no longer reported on the mutant (or an error surfaced). Exit 0 for CAUGHT, 1 otherwise.
            The probe is only trusted while the controls on the sample are CAUGHT.

Two enum spellings are deliberately corrected from nif.xml, which a consumer matching tag names
against the xml must know: SHADER_FLAGS1 bit 28 is written 'Parallax_Occlusion' (nif.xml
'Parallax_Occulsion') and SHADER_FLAGS2 bit 22 'No_Transparency_Multisampling' (nif.xml
'No_Transparecny_Multisampling').

Run time: a few milliseconds per little-endian loose file; files are read whole (they are
small) and BSA entries are read one at a time.
"""
import argparse
import collections
import hashlib
import json
import math
import os
import re
import struct
import sys
import time
import zlib

# =====================================================================================================
# Enumerations (nif.xml names)
# =====================================================================================================

ALPHA_FUNCTION = ['ONE', 'ZERO', 'SRC_COLOR', 'INV_SRC_COLOR', 'DEST_COLOR', 'INV_DEST_COLOR', 'SRC_ALPHA',
                  'INV_SRC_ALPHA', 'DEST_ALPHA', 'INV_DEST_ALPHA', 'SRC_ALPHA_SATURATE']
TEST_FUNCTION = ['ALWAYS', 'LESS', 'EQUAL', 'LESS_EQUAL', 'GREATER', 'NOT_EQUAL', 'GREATER_EQUAL', 'NEVER']
STENCIL_TEST = ['NEVER', 'LESS', 'EQUAL', 'LESS_EQUAL', 'GREATER', 'NOT_EQUAL', 'GREATER_EQUAL', 'ALWAYS']
STENCIL_DRAW = ['CCW_OR_BOTH', 'CCW', 'CW', 'BOTH']
STENCIL_ACTION = ['KEEP', 'ZERO', 'REPLACE', 'INCREMENT', 'DECREMENT', 'INVERT']
CLAMP_MODE = ['CLAMP_S_CLAMP_T', 'CLAMP_S_WRAP_T', 'WRAP_S_CLAMP_T', 'WRAP_S_WRAP_T']
FILTER_MODE = ['NEAREST', 'BILERP', 'TRILERP', 'NEAREST_MIPNEAREST', 'NEAREST_MIPLERP', 'BILERP_MIPNEAREST',
               'ANISOTROPIC']
APPLY_MODE = ['REPLACE', 'DECAL', 'MODULATE', 'HILIGHT', 'HILIGHT2']
SOURCE_VERTEX_MODE = ['SRC_IGNORE', 'SRC_EMISSIVE', 'SRC_AMB_DIF']
LIGHTING_MODE = ['E', 'E_A_D']
CYCLE_TYPE = ['LOOP', 'REVERSE', 'CLAMP']
# nif.xml enum KeyType (line 678) and the #T# sizes the key parsers are called with (payload value types).
KEY_TYPE = {1: 'LINEAR_KEY', 2: 'QUADRATIC_KEY', 3: 'TBC_KEY', 4: 'XYZ_ROTATION_KEY', 5: 'CONST_KEY'}
KEY_VALUE_TYPE = {1: 'byte', 4: 'float', 12: 'Vector3', 16: 'Color4'}
TRANSFORM_CHANNELS = ('translation', 'rotation', 'scale')
TRANSFORM_MEMBER = ['TRANSLATE_U', 'TRANSLATE_V', 'ROTATE', 'SCALE_U', 'SCALE_V']
MATERIAL_COLOR = ['AMBIENT', 'DIFFUSE', 'SPECULAR', 'SELF_ILLUM']
LIGHT_COLOR = ['DIFFUSE', 'AMBIENT']
TEX_TYPE = ['BASE', 'DARK', 'DETAIL', 'GLOSS', 'GLOW', 'BUMP', 'NORMAL', 'PARALLAX', 'DECAL_0', 'DECAL_1', 'DECAL_2',
            'DECAL_3']
BS_SHADER_TYPE = {0: 'TALL_GRASS', 1: 'DEFAULT', 10: 'SKY', 14: 'SKIN', 15: 'UNKNOWN', 17: 'WATER', 29: 'LIGHTING30',
                  32: 'TILE', 33: 'NOLIGHTING'}
# Bit names as nif.xml spells them, with two deliberate corrections: bit 28 of SHADER_FLAGS1 is
# 'Parallax_Occulsion' in the xml and bit 22 of SHADER_FLAGS2 is 'No_Transparecny_Multisampling';
# the tags use the corrected spellings (see the README block).
SHADER_FLAGS1 = ['Specular', 'Skinned', 'LowDetail', 'Vertex_Alpha', 'Unknown_1', 'Single_Pass', 'Empty',
                 'Environment_Mapping', 'Alpha_Texture', 'Unknown_2', 'FaceGen', 'Parallax_Shader_Index_15',
                 'Unknown_3', 'Non_Projective_Shadows', 'Unknown_4', 'Refraction', 'Fire_Refraction',
                 'Eye_Environment_Mapping', 'Hair', 'Dynamic_Alpha', 'Localmap_Hide_Secret',
                 'Window_Environment_Mapping', 'Tree_Billboard', 'Shadow_Frustum', 'Multiple_Textures',
                 'Remappable_Textures', 'Decal_Single_Pass', 'Dynamic_Decal_Single_Pass', 'Parallax_Occlusion',
                 'External_Emittance', 'Shadow_Map', 'ZBuffer_Test']
SHADER_FLAGS2 = ['ZBuffer_Write', 'LOD_Landscape', 'LOD_Building', 'No_Fade', 'Refraction_Tint', 'Vertex_Colors',
                 'Unknown1', 'First_Light_is_Point_Light', 'Second_Light', 'Third_Light', 'Vertex_Lighting',
                 'Uniform_Scale', 'Fit_Slope', 'Billboard_and_Envmap_Light_Fade', 'No_LOD_Land_Blend',
                 'Envmap_Light_Fade', 'Wireframe', 'VATS_Selection', 'Show_in_Local_Map', 'Premult_Alpha',
                 'Skip_Normal_Maps', 'Alpha_Decal', 'No_Transparency_Multisampling', 'Unknown2', 'Unknown3',
                 'Unknown4', 'Unknown5', 'Unknown6', 'Unknown7', 'Unknown8', 'Unknown9', 'Unknown10']

SHADER_FAMILY = {'BSShaderPPLightingProperty': 'PP', 'Lighting30ShaderProperty': 'L30',
                 'BSShaderNoLightingProperty': 'No', 'SkyShaderProperty': 'Sky', 'TileShaderProperty': 'Tile'}

SUPPORTED_VERSION = 0x14020007  # 20.2.0.7
SUPPORTED_USER_VERSION = 11
SUPPORTED_BS = (14, 21, 26, 32, 34)
# Streams walked for animation-only files (see SCOPE). nif.xml's own version table groups them as
# V20_2_0_7__11_4 (24), __11_5 (25), __11_7 (27 28) and __11_8 (30 31 32 33).
ANIMATION_BS = (24, 25, 27, 28, 30, 31, 33)
ANIMATION_BS_TYPES = frozenset((
    'NiControllerSequence', 'NiTextKeyExtraData', 'NiTransformInterpolator', 'NiTransformData',
    'NiBSplineCompTransformInterpolator', 'NiBSplineData', 'NiBSplineBasisData', 'NiBoolInterpolator', 'NiBoolData',
    'NiFloatInterpolator', 'NiFloatData', 'NiPoint3Interpolator', 'NiPosData', 'BSAnimNotes', 'BSAnimNote',
    'BSRotAccumTransfInterpolator', 'BSTreadTransfInterpolator'))
ANIM_NOTE_TYPE = ['ANT_INVALID', 'ANT_GRABIK', 'ANT_LOOKIK']
# Cut 2 (2026-09-28): the one pre-20.2.0.5 key walked, as a .kf animation stream only, at BOTH scopes (see SCOPE):
# little-endian 20.0.0.4 at user 10 or 11, BS 11. That identity is the Oblivion-era .kf identity, NOT the five
# FNV-shipped files' alone (the only five among the 4,316 .kf of the FNV PC Meshes BSA; byte-identical on FNV
# PC/PS3/X360 and FO3 PC): measured 2026-09-28 on the first 600 .kf of Oblivion's Meshes BSA, 598 carry it (550 at
# user 11, 48 at user 10; the other 2 are 10.2.0.0). The header has no Block Size array (since 20.2.0.5) and no
# string table (since 20.1.0.1), so walk() measures each block by what its parser consumes and every `string` is an
# inline SizedString. Only the eight block types the five files use were checked against nif.xml at this version
# (TestOutput/cut2-prep-20260928/kf2004: exact tiling on all five, with counter-shape controls that desync); a
# 20.0.0.4 file with any other block type is declined, naming the first in sorted order (17 of the 598: NiFloatData
# 15, NiBoolData 1, NiBSplineCompFloatInterpolator 1; the unmeasured types across them are NiFloatInterpolator,
# NiFloatData, NiBoolInterpolator, NiBoolData and NiBSplineCompFloatInterpolator), and so is a file whose block 0
# is not an NiControllerSequence (the C# probe reads the root from block 0, the footer being out of its reach). The
# C# reader applies the same rules (NifModelProbe.LegacyKfBlockTypes, LegacyKfBlockTypeRejection), so the gate's
# expectation rows and the reader's probe agree on every file of the identity.
LEGACY_KF_VERSION = 0x14000004
LEGACY_KF_USER_VERSIONS = (10, 11)
LEGACY_KF_BS = 11
LEGACY_KF_TYPES = frozenset((
    'NiControllerSequence', 'NiTextKeyExtraData', 'NiStringPalette', 'NiTransformInterpolator', 'NiTransformData',
    'NiBSplineCompTransformInterpolator', 'NiBSplineData', 'NiBSplineBasisData'))
# The five ControlledBlock strings a 20.0.0.4 sequence names through its String Palette (nif.xml ControlledBlock at
# 10.2.0.0 to 20.1.0.0), in file order; each is stored as <name>Offset and resolved into <name> after the walk.
LEGACY_OFFSET_FIELDS = ('nodeName', 'propertyType', 'controllerType', 'controllerId', 'interpolatorId')
# Both empty StringOffset sentinels the runtime resolver honors (the five retail files store only 0xFFFFFFFF).
LEGACY_EMPTY_OFFSETS = (0xFFFFFFFF, 0x0000FFFF)
LEGACY_PALETTE_ENTRY_MAX = 512  # the runtime resolver's MaxInlineStringBytes
# Scope (see SCOPE): 'cut1a' (the default) reproduces the cut-1a probe exactly; 'cut1b' is the widened walk.
SCOPES = ('cut1a', 'cut1b')
DEFAULT_SCOPE = 'cut1a'
CUT1B_PARSED_TYPES = frozenset(('BSAnimNotes', 'BSAnimNote', 'BSRotAccumTransfInterpolator',
                                'BSTreadTransfInterpolator'))  # no parser at scope 'cut1a'
CUT1B_KIND_TYPES = frozenset(('BSAnimNotes', 'BSAnimNote'))  # kind 'unknown' at scope 'cut1a'
# The three floats of a TBC key in file order, as the engines read them (see TBC ORDER); nif.xml 6388 says t, b, c.
TBC_FILE_ORDER = ('tension', 'continuity', 'bias')
NULL_REF = 0xFFFFFFFF
UNLIT_SHADERS = {'BSShaderNoLightingProperty', 'SkyShaderProperty', 'TileShaderProperty'}
ROT_TOL = 1e-3
NORMAL_SAMPLE = 64
NORMAL_TOL = 0.02  # |n|^2 tolerance for the sampled unit-length check


def name_of(table, value, prefix=''):
    if isinstance(table, dict):
        return table.get(value, '%s%d' % (prefix, value))
    return table[value] if 0 <= value < len(table) else '%s%d' % (prefix, value)


def vstr(v):
    return '%d.%d.%d.%d' % ((v >> 24) & 255, (v >> 16) & 255, (v >> 8) & 255, v & 255)


def resolve_scope(scope=None):
    """The walk's scope (see SCOPE): `scope`, else DEFAULT_SCOPE ('cut1a'). One of SCOPES."""
    s = scope or DEFAULT_SCOPE
    if s not in SCOPES:
        raise ValueError('scope %r: expected one of %s' % (s, ', '.join(SCOPES)))
    return s


class ProbeError(Exception):
    """A block did not parse as its declared type (overrun, bad ref, size mismatch)."""


class Decline(Exception):
    """The file's key is outside the probe's scope; the reason is the message."""


# =====================================================================================================
# Cursor
# =====================================================================================================

class Cur:
    """Bounded cursor over one block. Every read checks the block end and raises ProbeError."""
    __slots__ = ('d', 'p', 'end', 'ctx', 'S_u16', 'S_i16', 'S_u32', 'S_i32', 'S_f', 'S_v3', 'S_m33', 'S_q', 'S_v4',
                 'S_v2')

    def __init__(self, data, pos, end, ctx):
        self.d = data
        self.p = pos
        self.end = end
        self.ctx = ctx
        self.S_u16, self.S_i16, self.S_u32, self.S_i32 = ctx.S_u16, ctx.S_i16, ctx.S_u32, ctx.S_i32
        self.S_f, self.S_v3, self.S_m33, self.S_q, self.S_v4, self.S_v2 = (
            ctx.S_f, ctx.S_v3, ctx.S_m33, ctx.S_q, ctx.S_v4, ctx.S_v2)

    def need(self, n):
        if self.p + n > self.end:
            raise ProbeError('overrun: need %d bytes at +%d, block ends at +%d' % (
                n, self.p - self.ctx.block_start, self.end - self.ctx.block_start))

    def u8(self):
        self.need(1)
        v = self.d[self.p]
        self.p += 1
        return v

    def i8(self):
        v = self.u8()
        return v - 256 if v > 127 else v

    def u16(self):
        self.need(2)
        v = self.S_u16.unpack_from(self.d, self.p)[0]
        self.p += 2
        return v

    def i16(self):
        self.need(2)
        v = self.S_i16.unpack_from(self.d, self.p)[0]
        self.p += 2
        return v

    def u32(self):
        self.need(4)
        v = self.S_u32.unpack_from(self.d, self.p)[0]
        self.p += 4
        return v

    def i32(self):
        self.need(4)
        v = self.S_i32.unpack_from(self.d, self.p)[0]
        self.p += 4
        return v

    def f32(self):
        self.need(4)
        v = self.S_f.unpack_from(self.d, self.p)[0]
        self.p += 4
        return v

    def f32b(self):
        """Payload read: one float and its bits (the binary32 pattern as a uint32 in the file's byte order)."""
        self.need(4)
        v = self.S_f.unpack_from(self.d, self.p)[0]
        b = self.S_u32.unpack_from(self.d, self.p)[0]
        self.p += 4
        return v, b

    def fnb(self, n):
        """Payload read: n consecutive floats and their bits, as two lists."""
        self.need(4 * n)
        e = '>' if self.ctx.be else '<'
        v = list(struct.unpack_from('%s%df' % (e, n), self.d, self.p))
        b = list(struct.unpack_from('%s%dI' % (e, n), self.d, self.p))
        self.p += 4 * n
        return v, b

    def i16n(self, n):
        """Payload read: n consecutive signed shorts, as a list."""
        self.need(2 * n)
        v = list(struct.unpack_from('%s%dh' % ('>' if self.ctx.be else '<', n), self.d, self.p))
        self.p += 2 * n
        return v

    def v2(self):
        self.need(8)
        v = self.S_v2.unpack_from(self.d, self.p)
        self.p += 8
        return v

    def v3(self):
        self.need(12)
        v = self.S_v3.unpack_from(self.d, self.p)
        self.p += 12
        return v

    def v4(self):
        self.need(16)
        v = self.S_v4.unpack_from(self.d, self.p)
        self.p += 16
        return v

    def m33(self):
        self.need(36)
        v = self.S_m33.unpack_from(self.d, self.p)
        self.p += 36
        return v

    def skip(self, n):
        if n < 0:
            raise ProbeError('negative skip')
        self.need(n)
        self.p += n

    def ref(self, kind=None):
        """Block reference (Ref or Ptr): -1 or an index below the block count. A reference to the
        block being parsed is an error, and so is one whose target type is known to be of another
        kind than `kind` expects (see kind_of)."""
        v = self.u32()
        if v == NULL_REF:
            return -1
        if v >= self.ctx.nb:
            raise ProbeError('ref %d out of range (%d blocks)' % (v, self.ctx.nb))
        if v == self.ctx.block_index:
            raise ProbeError('ref %d points at the block itself' % v)
        if kind is not None:
            k = kind_of(self.ctx.types[v], self.ctx.scope)
            if k != 'unknown' and k != kind and not (isinstance(kind, tuple) and k in kind):
                raise ProbeError('ref %d -> %s is %s, expected %s' % (v, self.ctx.types[v], k, kind))
        return v

    def refs(self, n, kind=None):
        if n > (self.end - self.p) // 4:
            raise ProbeError('ref array of %d does not fit' % n)
        return [self.ref(kind) for _ in range(n)]

    def string(self):
        """NiFixedString: index into the header string table; before 20.1.0.1 (ctx.inline_strings) an inline
        SizedString, nif.xml `string` until 20.0.0.5."""
        if self.ctx.inline_strings:
            return self.sized()
        v = self.u32()
        if v == NULL_REF:
            return None
        if v >= len(self.ctx.strings):
            raise ProbeError('string index %d out of range (%d strings)' % (v, len(self.ctx.strings)))
        return self.ctx.strings[v]

    def sized(self):
        n = self.u32()
        if n > 4096:
            raise ProbeError('sized string length %d' % n)
        self.need(n)
        v = self.d[self.p:self.p + n].decode('latin-1')
        self.p += n
        return v

    def count(self, n, unit):
        """Validate an array count against the remaining bytes before skipping it."""
        if n * unit > self.end - self.p:
            raise ProbeError('array of %d x %d bytes does not fit in %d' % (n, unit, self.end - self.p))
        return n


class Ctx:
    """Per-file context: endianness, block count, strings and per-block bookkeeping."""

    def __init__(self, be, nb, strings, types):
        e = '>' if be else '<'
        self.be = be
        self.nb = nb
        self.strings = strings
        self.types = types  # block index -> type name
        self.block_start = 0
        self.block_index = -1
        self.bs = 0
        self.payloads = False  # walk(..., payloads=True): the parsers add a 'payload' key (see PAYLOADS)
        self.scope = DEFAULT_SCOPE  # walk(..., scope=...): see SCOPE
        self.inline_strings = False  # walk(): the file predates the header string table (20.1.0.1)
        self.S_u16 = struct.Struct(e + 'H')
        self.S_i16 = struct.Struct(e + 'h')
        self.S_u32 = struct.Struct(e + 'I')
        self.S_i32 = struct.Struct(e + 'i')
        self.S_f = struct.Struct(e + 'f')
        self.S_v2 = struct.Struct(e + '2f')
        self.S_v3 = struct.Struct(e + '3f')
        self.S_v4 = struct.Struct(e + '4f')
        self.S_q = struct.Struct(e + '4f')
        self.S_m33 = struct.Struct(e + '9f')


# =====================================================================================================
# Header
# =====================================================================================================

def parse_header(data, scope=None):
    """Generic header reader. Returns a dict; raises Decline for keys outside the walk's scope
    (after filling in what the header offers: version key and, where present, block types).
    `scope` selects the gate (see SCOPE; None: 'cut1a')."""
    scope = resolve_scope(scope)
    h = {'headerString': None, 'version': 0, 'userVersion': 0, 'bsVersion': 0, 'bigEndian': False,
         'blockCount': 0, 'typeNames': [], 'typeIndex': [], 'sizes': [], 'strings': [], 'dataStart': 0}
    nl = data.find(b'\n', 0, 80)
    if nl < 0:
        raise Decline('no header string')
    hs = data[:nl].decode('latin-1')
    h['headerString'] = hs
    if not (hs.startswith('Gamebryo File Format') or hs.startswith('NetImmerse File Format')):
        raise Decline('not a NIF header: %r' % hs[:40])
    pos = nl + 1
    if pos + 4 > len(data):
        raise Decline('truncated after header string')
    ver = struct.unpack_from('<I', data, pos)[0]
    pos += 4
    h['version'] = ver
    if ver < 0x05000001:
        # NetImmerse 3.x/4.x: no block-type table (inline type names); out of scope.
        raise Decline('version %s: no block-type table, no block sizes (cut 2 or later)' % vstr(ver))
    be = False
    if ver >= 0x14000003:
        be = data[pos] == 0
        pos += 1
    h['bigEndian'] = be
    uv = 0
    if ver >= 0x0A000108:
        uv = struct.unpack_from('<I', data, pos)[0]  # ulittle32 per nif.xml
        pos += 4
    h['userVersion'] = uv
    nb = struct.unpack_from('<I', data, pos)[0]  # ulittle32
    pos += 4
    h['blockCount'] = nb
    bs = 0
    has_bs_header = ver == 0x0A000102 or ((ver in (0x14020007, 0x14000005) or (0x0A010000 <= ver <= 0x14000004
                                                                                  and uv <= 11)) and uv >= 3)
    if has_bs_header:
        bs = struct.unpack_from('<I', data, pos)[0]  # ulittle32
        pos += 4
        pos += 1 + data[pos]  # author (ExportString: 1-byte length incl. NUL + bytes)
        if bs > 130:
            pos += 4
        if bs < 131:
            pos += 1 + data[pos]  # process script
        pos += 1 + data[pos]  # export script
        if 103 <= bs < 170:
            pos += 1 + data[pos]
        if bs >= 170:
            pos += 1 + data[pos]
    h['bsVersion'] = bs
    e = '>' if be else '<'
    if nb > 200000:
        raise Decline('implausible block count %d' % nb)
    nt = struct.unpack_from(e + 'H', data, pos)[0]
    pos += 2
    names = []
    for _ in range(nt):
        ln = struct.unpack_from(e + 'I', data, pos)[0]
        pos += 4
        if ln > 256:
            raise Decline('block type name length %d' % ln)
        names.append(data[pos:pos + ln].decode('latin-1'))
        pos += ln
    h['typeNames'] = names
    idx = [i & 0x7FFF for i in struct.unpack_from(e + 'H' * nb, data, pos)] if nb else []
    pos += 2 * nb
    for i in idx:
        if i >= nt:
            raise Decline('block type index %d beyond the %d-entry type table' % (i, nt))
    h['typeIndex'] = idx
    # Scope gate: 20.2.0.7 / uv 11 / bs 14,21,26,32,34 is walked, and since cut 2 the little-endian 20.0.0.4 .kf
    # animation stream at user 10 or 11, BS 11 (LEGACY_KF_*), at both scopes.
    if ver == LEGACY_KF_VERSION:
        reason = legacy_kf_decline(uv, bs, be, names, idx)
        if reason:
            raise Decline(reason)
    elif ver != SUPPORTED_VERSION:
        raise Decline('version %s: outside cut 1a (only 20.2.0.7 is walked)' % vstr(ver))
    if ver >= 0x14020005:
        sizes = list(struct.unpack_from(e + 'I' * nb, data, pos)) if nb else []
        pos += 4 * nb
        h['sizes'] = sizes
    if ver >= 0x14010001:
        ns = struct.unpack_from(e + 'I', data, pos)[0]
        pos += 8  # num strings + max string length
        strings = []
        for _ in range(ns):
            ln = struct.unpack_from(e + 'I', data, pos)[0]
            pos += 4
            if pos + ln > len(data):
                raise Decline('string table runs past EOF')
            strings.append(data[pos:pos + ln].decode('latin-1'))
            pos += ln
        h['strings'] = strings
    ng = struct.unpack_from(e + 'I', data, pos)[0]
    pos += 4 + 4 * ng
    h['dataStart'] = pos
    if ver == LEGACY_KF_VERSION:
        return h  # its user and BS versions are the key's own (legacy_kf_decline checked them)
    if uv != SUPPORTED_USER_VERSION:
        raise Decline('user version %d: outside cut 1a (only 11 is walked)' % uv)
    if bs in ANIMATION_BS and scope == 'cut1b':
        other = sorted({names[i] for i in idx} - ANIMATION_BS_TYPES)
        if other:
            raise Decline('BS version %d: walked only for animation files; %s is not an animation block checked '
                          'against nif.xml at this stream' % (bs, other[0]))
    elif bs not in SUPPORTED_BS:
        if scope == 'cut1a':
            raise Decline('BS version %d: outside cut 1a (only 14, 21, 26, 32, 34 are walked)' % bs)
        raise Decline('BS version %d: outside cuts 1a and 1b (14, 21, 26, 32, 34 are walked, and 24, 25, 27, 28, '
                      '30, 31, 33 for animation files)' % bs)
    return h


def legacy_kf_decline(uv, bs, be, names, idx):
    """The reason a 20.0.0.4 file is outside the walk (None when it is the LEGACY_KF_* .kf key: the identity, every
    block's type in LEGACY_KF_TYPES, and block 0 an NiControllerSequence, the three rules NifModelProbe applies):
    every reason names the version, which is how the C# hop A2 maps it to the reader's later-cut(2) category, and
    never the phrase 'BS version', which that hop reserves for the 20.2.0.7 animation-only BS keys. The type rule is
    checked before the root rule so that a scene graph's reason names its first foreign type, as it did before the
    root rule existed (the checked-in collisionboxstatic.nif row)."""
    if be:
        what = 'big-endian'
    elif uv not in LEGACY_KF_USER_VERSIONS:
        what = 'user %d' % uv
    elif bs != LEGACY_KF_BS:
        what = 'BS %d' % bs
    else:
        other = sorted({names[i] for i in idx} - LEGACY_KF_TYPES)
        if other:
            return ('version 20.0.0.4: walked only as a .kf animation stream (cut 2); %s is not an animation block '
                    'checked against nif.xml at this version' % other[0])
        root = names[idx[0]] if idx else None
        if root == 'NiControllerSequence':
            return None
        return ('version 20.0.0.4: walked only as a .kf animation stream (cut 2); block 0 is %s, not the '
                'NiControllerSequence root the stream is read from' % (root or 'absent'))
    return ('version 20.0.0.4: walked only as a little-endian .kf animation stream at user 10 or 11, BS 11 (cut 2); '
            'this file is %s' % what)


def key_string(h):
    if h is None or not h.get('version'):
        return 'unknown'
    return '%s/uv%d/bs%d/%s' % (vstr(h['version']), h['userVersion'], h['bsVersion'], 'BE' if h['bigEndian'] else 'LE')


# =====================================================================================================
# Reference kinds (what a ref may point at), from the nif.xml inheritance tree
# =====================================================================================================

EXTRA_TYPES = {'BSBound', 'BSFurnitureMarker', 'BSFurnitureMarkerNode', 'BSInvMarker', 'BSWArray', 'BSXFlags',
               'BSPositionData', 'BSPackedCombinedGeomDataExtra', 'BSPackedCombinedSharedGeomDataExtra',
               'BSConnectPoint::Parents', 'BSConnectPoint::Children', 'BoneTranslations', 'SkinAttach',
               'bhkRagdollTemplate'}
AV_TYPES = {'BSDamageStage', 'NiCollisionSwitch', 'NiBone', 'NiCamera', 'NiTextureEffect'}
KIND_TYPES = {'BSShaderTextureSet': 'textureset', 'NiSourceTexture': 'texture', 'NiSkinInstance': 'skininst',
              'BSDismemberSkinInstance': 'skininst', 'NiSkinPartition': 'skinpart', 'NiControllerSequence': 'sequence',
              'NiDefaultAVObjectPalette': 'palette', 'NiStringPalette': 'palette', 'NiSequenceStreamHelper': 'av',
              'NiPSysColliderManager': 'psysmod', 'BSMultiBound': 'bound', 'BSAnimNotes': 'animnotes',
              'BSAnimNote': 'animnote'}


def kind_of(t, scope=DEFAULT_SCOPE):
    k = KIND_TYPES.get(t)
    if k and not (scope == 'cut1a' and t in CUT1B_KIND_TYPES):
        return k
    if t in AV_TYPES:
        return 'av'
    if t in EXTRA_TYPES or t.endswith('ExtraData'):
        return 'extra'
    if t == 'NiControllerManager' or t.endswith(('Controller', 'Ctlr')):
        return 'controller'
    if t.endswith('Interpolator'):
        return 'interpolator'
    if t.endswith('Property'):
        return 'property'
    if t.endswith(('Node', 'TriShape', 'TriStrips', 'Particles', 'ParticleSystem', 'ParticleMeshes', 'Light')):
        return 'av'
    if t.endswith('Data'):
        return 'data'
    if t.startswith(('bhk', 'hk')):
        return 'havok'
    if t.endswith(('Modifier', 'Emitter', 'Collider')):
        return 'psysmod'
    return 'unknown'


def check_times(c, pos, n, stride):
    """Key times must be non-decreasing (an authored key array is sorted); a zeroed span breaks this."""
    if n < 2:
        return
    unpack = c.S_f.unpack_from
    d = c.d
    prev = unpack(d, pos)[0]
    for i in range(1, n):
        t = unpack(d, pos + stride * i)[0]
        if t < prev or t != t:
            raise ProbeError('key times not sorted at key %d of %d (%g after %g)' % (i, n, t, prev))
        prev = t


def check_weights(c, wpos, nv, nw):
    """Sample up to NORMAL_SAMPLE vertices; count those whose weights do not sum to 1 within 2%."""
    if nv == 0 or nw == 0:
        return 0
    step = max(1, nv // NORMAL_SAMPLE)
    unpack = c.S_f.unpack_from
    d = c.d
    bad = 0
    for i in range(0, nv, step):
        base = wpos + 4 * nw * i
        total = 0.0
        for k in range(nw):
            total += unpack(d, base + 4 * k)[0]
        if abs(total - 1.0) > 0.02:
            bad += 1
    return bad


# =====================================================================================================
# Shared field groups
# =====================================================================================================

def objnet(c, out):
    out['name'] = c.string()
    n = c.u32()
    out['extraData'] = c.refs(c.count(n, 4), 'extra')
    out['controller'] = c.ref('controller')


def avobject(c, out):
    objnet(c, out)
    out['flags'] = c.u32() if c.ctx.bs > 26 else c.u16()
    out['translation'] = c.v3()
    out['rotation'] = c.m33()
    out['scale'] = c.f32()
    n = c.u32()
    out['properties'] = c.refs(c.count(n, 4), 'property')
    out['collision'] = c.ref('havok')


def node(c, out):
    avobject(c, out)
    n = c.u32()
    out['children'] = c.refs(c.count(n, 4), 'av')
    n = c.u32()
    out['effects'] = c.refs(c.count(n, 4), 'av')


def geometry(c, out):
    avobject(c, out)
    out['data'] = c.ref('data')
    out['skinInstance'] = c.ref('skininst')
    n = c.u32()
    c.count(n, 8)
    out['materialNames'] = [c.string() for _ in range(n)]
    out['materialExtra'] = [c.i32() for _ in range(n)]
    out['activeMaterial'] = c.i32()
    out['materialNeedsUpdate'] = c.u8()


def bound(c):
    return {'center': c.v3(), 'radius': c.f32()}


def geometry_data(c, out, arrays=True):
    """NiGeometryData. arrays=False for the particle data blocks: nif.xml's own BS202 rule (the
    NiGeometryData description: the vertex, normal, tangent, color and UV arrays have no length for
    NiPSysData regardless of the Num field or the Has booleans), confirmed on retail
    effects/impactexplosiondirt.nif, whose NiPSysData says Has Vertex Colors = 1 for 375 particles
    inside a 301-byte block. `streams` records whether the arrays were read, so a consumer never
    mistakes a Has byte for a stream, and `vertexColorsUniform` is None when it was not measured."""
    out['streams'] = arrays
    out['groupId'] = c.i32()
    nv = c.u16()
    out['numVertices'] = nv
    out['keepFlags'] = c.u8()
    out['compressFlags'] = c.u8()
    out['hasVertices'] = c.u8()
    if out['hasVertices'] and arrays:
        c.skip(c.count(nv, 12) * 12)
    flags = c.u16()
    out['dataFlags'] = flags
    # nif.xml: at 20.2.0.7 with a BS stream the field is BSGeometryDataFlags (bit 0 = has UV,
    # bit 12 = has tangents); the plain NiGeometryDataFlags reading (bits 0-5 = UV set count)
    # is kept alongside for the record.
    uvsets = flags & 1
    out['uvSetsPlainReading'] = flags & 63
    out['hasTangents'] = bool(flags & 4096)
    out['hasNormals'] = c.u8()
    if out['hasNormals'] and arrays:
        npos = c.p
        c.skip(c.count(nv, 12) * 12)
        bad = check_normals(c, npos, nv)
        if bad:
            out['nonUnitNormalsSampled'] = bad
        if out['hasTangents']:
            c.skip(c.count(nv, 24) * 24)
    out['bound'] = bound(c)
    out['hasVertexColors'] = c.u8()
    out['vertexColorsUniform'] = None  # None = not measured (no color array read)
    if out['hasVertexColors'] and arrays:
        cpos = c.p
        c.skip(c.count(nv, 16) * 16)
        first = c.d[cpos:cpos + 16]
        out['vertexColorsUniform'] = c.d[cpos:cpos + 16 * nv] == first * nv
        out['firstVertexColor'] = c.S_v4.unpack_from(c.d, cpos) if nv else None
    out['uvSets'] = uvsets
    if arrays:
        c.skip(c.count(nv * uvsets, 8) * 8)
    out['consistencyFlags'] = c.u16()
    out['additionalData'] = c.ref('data')


def check_normals(c, npos, nv):
    """Sample up to NORMAL_SAMPLE normals; return how many are not unit length."""
    if nv == 0:
        return 0
    step = max(1, nv // NORMAL_SAMPLE)
    bad = 0
    unpack = c.S_v3.unpack_from
    d = c.d
    for i in range(0, nv, step):
        x, y, z = unpack(d, npos + 12 * i)
        l2 = x * x + y * y + z * z
        if l2 != l2 or abs(l2 - 1.0) > NORMAL_TOL:
            bad += 1
    return bad


def tri_based_geom_data(c, out):
    geometry_data(c, out)
    out['numTriangles'] = c.u16()


def tex_desc(c):
    t = {'source': c.ref('texture')}
    f = c.u16()
    t['flags'] = f
    t['textureIndex'] = f & 0xFF
    t['filterMode'] = name_of(FILTER_MODE, (f >> 8) & 15)
    t['clampMode'] = name_of(CLAMP_MODE, (f >> 12) & 3)
    t['hasTransform'] = c.u8()
    if t['hasTransform']:
        t['translation'] = c.v2()
        t['scale'] = c.v2()
        t['rotation'] = c.f32()
        t['transformMethod'] = c.u32()
        t['center'] = c.v2()
    return t


def time_controller(c, out):
    out['nextController'] = c.ref('controller')
    f = c.u16()
    out['flags'] = f
    out['animType'] = f & 1
    out['cycleType'] = name_of(CYCLE_TYPE, (f >> 1) & 3)
    out['active'] = bool(f & 8)
    out['playBackwards'] = bool(f & 16)
    out['managerControlled'] = bool(f & 32)
    out['frequency'] = c.f32()
    out['phase'] = c.f32()
    out['startTime'] = c.f32()
    out['stopTime'] = c.f32()
    out['target'] = c.ref(('av', 'property', 'texture'))


def single_interp_controller(c, out):
    time_controller(c, out)
    out['interpolator'] = c.ref('interpolator')


def quat_transform(c):
    return {'translation': c.v3(), 'rotation': c.v4(), 'scale': c.f32()}


def read_key_value(c, tsize):
    """Payload read of one #T# (nif.xml Key Value, line 6409): byte (a u8, no bits), float, Vector3 (x y z,
    line 5782) or Color4 (r g b a, line 5691). Returns (value, bits); bits is None for a byte."""
    if tsize == 1:
        return c.u8(), None
    if tsize == 4:
        return c.f32b()
    return c.fnb(tsize // 4)


def read_tbc(c):
    """Payload read of nif.xml TBC (line 6388): three floats, (values, bits) in FILE order. nif.xml labels them
    t (6390), b (6394), c (6395); the engines read tension, continuity, bias (TBC ORDER, TBC_FILE_ORDER)."""
    return c.fnb(3)


def decode_keys(c, n, kt, tsize):
    """Payload read of n Key<T> (nif.xml line 6401) of key type kt, field by field through the cursor:
    Time (float, 6406), Value (#T#, 6409), then Forward and Backward (#T#, 6411 and 6414, cond "#ARG# == 2")
    or TBC (6418, cond "#ARG# == 3"). LINEAR (1) and CONST (5) keys are Time + Value."""
    keys = []
    for _ in range(n):
        t, tb = c.f32b()
        v, vb = read_key_value(c, tsize)
        k = {'time': t, 'timeBits': tb, 'value': v}
        if vb is not None:
            k['valueBits'] = vb
        if kt == 2:
            f, fb = read_key_value(c, tsize)
            b, bb = read_key_value(c, tsize)
            k['forward'] = f
            if fb is not None:
                k['forwardBits'] = fb
            k['backward'] = b
            if bb is not None:
                k['backwardBits'] = bb
        elif kt == 3:
            k['tbc'], k['tbcBits'] = read_tbc(c)
        keys.append(k)
    return keys


def decode_quat_keys(c, n, kt):
    """Payload read of n QuatKey<Quaternion> (nif.xml line 6437) at 20.2.0.7 for a type other than 4:
    Time (float, 6444), Value (Quaternion w x y z, 6448; struct at 5881), TBC (6452) when the type is 3.
    A QuatKey "Never has tangents" (6438), so a QUADRATIC quaternion key is Time + Value."""
    keys = []
    for _ in range(n):
        t, tb = c.f32b()
        v, vb = c.fnb(4)
        k = {'time': t, 'timeBits': tb, 'value': v, 'valueBits': vb}
        if kt == 3:
            k['tbc'], k['tbcBits'] = read_tbc(c)
        keys.append(k)
    return keys


def group_payload(n, kt, value_type, keys):
    """One decoded key group (see PAYLOADS). keyTypeName is None when the group is empty."""
    return {'numKeys': n, 'keyType': kt, 'keyTypeName': KEY_TYPE.get(kt) if n else None, 'valueType': value_type,
            'keys': keys}


def key_group(c, tsize, decoded=None):
    """KeyGroup<T>: returns (numKeys, keyType). T occupies tsize bytes. With payloads on the caller passes
    a list as `decoded`: the keys are then read field by field (decode_keys) rather than skipped, and the
    decoded group (group_payload) is appended to the list, an empty group included."""
    n = c.u32()
    kt = 0
    if n:
        kt = c.u32()
        if kt in (1, 5):
            ks = 4 + tsize
        elif kt == 2:
            ks = 4 + 3 * tsize
        elif kt == 3:
            ks = 4 + tsize + 12
        else:
            raise ProbeError('key type %d' % kt)
        kpos = c.p
        if decoded is None:
            c.skip(c.count(n, ks) * ks)
        else:
            c.count(n, ks)
            decoded.append(group_payload(n, kt, KEY_VALUE_TYPE[tsize], decode_keys(c, n, kt, tsize)))
        check_times(c, kpos, n, ks)
    elif decoded is not None:
        decoded.append(group_payload(0, 0, KEY_VALUE_TYPE[tsize], []))
    return n, kt


def keyframe_data(c, out):
    """NiKeyframeData / NiTransformData (nif.xml 10805 / 12556); with payloads on, out['payload'] holds
    rotation, xyzRotations (type 4 only), translations and scales (see PAYLOADS)."""
    pl = {} if c.ctx.payloads else None
    n = c.u32()
    out['numRotationKeys'] = n
    kt = 0
    if n:
        kt = c.u32()
        out['rotationType'] = kt
        if kt == 4:
            dec = [] if pl is not None else None
            out['xyzRotations'] = [key_group(c, 4, dec) for _ in range(3)]
            if pl is not None:
                # No quaternion key is stored for type 4 (10820): numKeys 0 keeps numKeys == len(keys) for every
                # group; the stored Num Rotation Keys (1 by 10809-10812) is kept apart.
                pl['rotation'] = {'numKeys': 0, 'storedNumRotationKeys': n, 'keyType': kt,
                                  'keyTypeName': KEY_TYPE[kt], 'valueType': 'Quaternion', 'keys': []}
                pl['xyzRotations'] = dec
        elif kt in (1, 2, 3, 5):
            ks = 32 if kt == 3 else 20
            kpos = c.p
            if pl is None:
                c.skip(c.count(n, ks) * ks)
            else:
                c.count(n, ks)
                pl['rotation'] = group_payload(n, kt, 'Quaternion', decode_quat_keys(c, n, kt))
            check_times(c, kpos, n, ks)
        else:
            raise ProbeError('rotation key type %d' % kt)
    elif pl is not None:
        pl['rotation'] = group_payload(0, 0, 'Quaternion', [])
    dec = [] if pl is not None else None
    out['translations'] = key_group(c, 12, dec)
    out['scales'] = key_group(c, 4, dec)
    if pl is not None:
        pl['translations'], pl['scales'] = dec
        out['payload'] = pl


def blend_interpolator(c, out):
    f = c.u8()
    out['flags'] = f
    asz = c.u8()
    out['arraySize'] = asz
    out['weightThreshold'] = c.f32()
    if not (f & 1):
        out['interpCount'] = c.u8()
        out['singleIndex'] = c.u8()
        out['highPriority'] = c.i8()
        out['nextHighPriority'] = c.i8()
        out['singleTime'] = c.f32()
        out['highWeightsSum'] = c.f32()
        out['nextHighWeightsSum'] = c.f32()
        out['highEaseSpinner'] = c.f32()
        items = []
        c.count(asz, 17)
        for _ in range(asz):
            items.append({'interpolator': c.ref('interpolator'), 'weight': c.f32(), 'normalizedWeight': c.f32(),
                          'priority': c.u8(), 'easeSpinner': c.f32()})
        out['items'] = items


def bspline_interpolator(c, out):
    out['startTime'] = c.f32()
    out['stopTime'] = c.f32()
    out['splineData'] = c.ref('data')
    out['basisData'] = c.ref('data')


def bspline_payload(c, p0, static, channels, compact):
    """Payload of an NiBSpline*Interpolator: re-reads the bytes its parser just consumed, [p0, c.p), with a
    second bounded cursor, keeping every float's bits. NiBSplineInterpolator (nif.xml 9019): Start Time
    (9023), Stop Time (9027), Spline Data and Basis Data refs (9031, 9032; already in the block dict). Then
    the static value: 'float' (NiBSplineFloatInterpolator Value, 10391), 'Vector3' (NiBSplinePoint3Interpolator
    Value, 10413) or 'transform' (NiBSplineTransformInterpolator Transform, 10436: NiQuatTransform, 6834,
    Translation Vector3, Rotation Quaternion w x y z, Scale float). Then one uint Handle per channel (10396,
    10418; 10438, 10442, 10447), then on the compact forms an Offset and a Half Range per channel in channel
    order (10405/10407, 10427/10429, 10456/10458, 10459/10461, 10463/10465)."""
    r = Cur(c.d, p0, c.p, c.ctx)
    pl = {}
    pl['startTime'], pl['startTimeBits'] = r.f32b()
    pl['stopTime'], pl['stopTimeBits'] = r.f32b()
    r.skip(8)
    if static == 'transform':
        tr = {}
        tr['translation'], tr['translationBits'] = r.fnb(3)
        tr['rotation'], tr['rotationBits'] = r.fnb(4)
        tr['scale'], tr['scaleBits'] = r.f32b()
        pl['transform'] = tr
    elif static == 'Vector3':
        pl['value'], pl['valueBits'] = r.fnb(3)
    else:
        pl['value'], pl['valueBits'] = r.f32b()
    ch = {}
    for name in channels:
        ch[name] = {'handle': r.u32()}
    if compact:
        for name in channels:
            ch[name]['offset'], ch[name]['offsetBits'] = r.f32b()
            ch[name]['halfRange'], ch[name]['halfRangeBits'] = r.f32b()
    pl['channels'] = ch
    if r.p != c.p:
        raise ProbeError('payload re-read ended at +%d, the parser at +%d' % (r.p - p0, c.p - p0))
    return pl


def extra_data(c, out):
    out['name'] = c.string()


def shader_property(c, out):
    out['shadeFlags'] = c.u16()
    out['shaderType'] = c.u32()
    out['shaderFlags1'] = c.u32()
    out['shaderFlags2'] = c.u32()
    out['envMapScale'] = c.f32()


def shader_lighting_property(c, out):
    shader_property(c, out)
    out['textureClampMode'] = c.u32()


def dynamic_effect(c, out):
    avobject(c, out)
    out['switchState'] = c.u8()
    n = c.u32()
    out['affectedNodes'] = c.refs(c.count(n, 4), 'av')


def light(c, out):
    dynamic_effect(c, out)
    out['dimmer'] = c.f32()
    out['ambient'] = c.v3()
    out['diffuse'] = c.v3()
    out['specular'] = c.v3()


def skin_partition_struct(c):
    p = {}
    nv = c.u16()
    nt = c.u16()
    nbn = c.u16()
    ns = c.u16()
    nw = c.u16()
    p['numVertices'], p['numTriangles'], p['numBones'], p['numStrips'], p['weightsPerVertex'] = nv, nt, nbn, ns, nw
    c.skip(c.count(nbn, 2) * 2)
    p['hasVertexMap'] = c.u8()
    if p['hasVertexMap']:
        c.skip(c.count(nv, 2) * 2)
    p['hasVertexWeights'] = c.u8()
    if p['hasVertexWeights']:
        wpos = c.p
        c.skip(c.count(nv * nw, 4) * 4)
        p['badWeightSums'] = check_weights(c, wpos, nv, nw)
        if p['badWeightSums']:
            # 0 of 5,840 retail partitions (677 FNV armor files) fail this, so it is an error.
            raise ProbeError('%d sampled vertices whose skin weights do not sum to 1' % p['badWeightSums'])
    c.need(2 * ns)
    lengths = [c.u16() for _ in range(ns)]
    p['hasFaces'] = c.u8()
    if p['hasFaces']:
        if ns:
            c.skip(c.count(sum(lengths), 2) * 2)
        else:
            c.skip(c.count(nt, 6) * 6)
    p['hasBoneIndices'] = c.u8()
    if p['hasBoneIndices']:
        c.skip(c.count(nv * nw, 1))
    return p


# =====================================================================================================
# Block parsers: type name -> function(cursor, out)
# =====================================================================================================

P = {}


def parser(*names):
    def deco(fn):
        for n in names:
            P[n] = fn
        return fn
    return deco


@parser('NiNode', 'BSFadeNode', 'NiBSAnimationNode', 'NiBSParticleNode', 'AvoidNode', 'RootCollisionNode',
        'BSLeafAnimNode', 'NiBone', 'NiCollisionSwitch', 'CsNiNode')
def p_node(c, o):
    node(c, o)


@parser('NiBillboardNode')
def p_billboard(c, o):
    node(c, o)
    o['billboardMode'] = c.u16()


@parser('NiSwitchNode')
def p_switch(c, o):
    node(c, o)
    o['switchFlags'] = c.u16()
    o['index'] = c.u32()


@parser('NiLODNode')
def p_lod(c, o):
    p_switch(c, o)
    o['lodLevelData'] = c.ref('data')


@parser('BSOrderedNode')
def p_ordered(c, o):
    node(c, o)
    o['alphaSortBound'] = bound(c)
    o['staticBound'] = c.u8()


@parser('BSValueNode')
def p_valuenode(c, o):
    node(c, o)
    o['value'] = c.u32()
    o['valueFlags'] = c.u8()


@parser('BSRangeNode', 'BSBlastNode', 'BSDamageStage', 'BSDebrisNode')
def p_rangenode(c, o):
    node(c, o)
    o['min'] = c.u8()
    o['max'] = c.u8()
    o['current'] = c.u8()


@parser('BSMultiBoundNode')
def p_multibound(c, o):
    node(c, o)
    o['multiBound'] = c.ref('bound')


@parser('BSTreeNode')
def p_treenode(c, o):
    node(c, o)
    n = c.u32()
    o['bones1'] = c.refs(c.count(n, 4), 'av')
    n = c.u32()
    o['bones2'] = c.refs(c.count(n, 4), 'av')


@parser('BSMasterParticleSystem')
def p_masterpsys(c, o):
    node(c, o)
    o['maxEmitterObjects'] = c.u16()
    n = c.u32()
    o['particleSystems'] = c.refs(c.count(n, 4), 'av')


@parser('BSFaceGenNiNode')
def p_facegen_node(c, o):
    node(c, o)
    o['unknownUshort'] = c.u16()


@parser('NiTriShape', 'NiTriStrips', 'NiParticles', 'NiAutoNormalParticles', 'NiRotatingParticles')
def p_geometry(c, o):
    geometry(c, o)


@parser('BSSegmentedTriShape')
def p_segmented(c, o):
    geometry(c, o)
    n = c.u32()
    c.count(n, 9)
    o['segments'] = [{'flags': c.u8(), 'startIndex': c.u32(), 'numPrimitives': c.u32()} for _ in range(n)]


@parser('BSLODTriShape')
def p_lodtrishape(c, o):
    geometry(c, o)
    o['lodSizes'] = [c.u32(), c.u32(), c.u32()]


@parser('NiParticleSystem', 'BSStripParticleSystem', 'NiMeshParticleSystem')
def p_psys(c, o):
    geometry(c, o)
    o['worldSpace'] = c.u8()
    n = c.u32()
    o['modifiers'] = c.refs(c.count(n, 4), 'psysmod')


@parser('NiTriShapeData')
def p_trishapedata(c, o):
    tri_based_geom_data(c, o)
    o['numTrianglePoints'] = c.u32()
    o['hasTriangles'] = c.u8()
    if o['hasTriangles']:
        c.skip(c.count(o['numTriangles'], 6) * 6)
    n = c.u16()
    o['numMatchGroups'] = n
    for _ in range(n):
        m = c.u16()
        c.skip(c.count(m, 2) * 2)


@parser('NiTriStripsData')
def p_tristripsdata(c, o):
    tri_based_geom_data(c, o)
    ns = c.u16()
    o['numStrips'] = ns
    c.need(2 * ns)
    lengths = [c.u16() for _ in range(ns)]
    o['hasPoints'] = c.u8()
    if o['hasPoints']:
        c.skip(c.count(sum(lengths), 2) * 2)
    o['stripPoints'] = sum(lengths)


@parser('NiPSysData', 'BSStripPSysData')
def p_psysdata(c, o):
    # NiParticlesData at 20.2.0.7 with a BS stream (BS202): nif.xml states that the per-particle
    # arrays are absent (vercond !#BS202#) and that the NiGeometryData arrays have no length for
    # NiPSysData either; only the "has" bytes remain. Agrees with nif.xml, confirmed on retail
    # effects/impactexplosiondirt.nif (see geometry_data).
    geometry_data(c, o, arrays=False)
    o['hasRadii'] = c.u8()
    o['numActive'] = c.u16()
    o['hasSizes'] = c.u8()
    o['hasRotations'] = c.u8()
    o['hasRotationAngles'] = c.u8()
    o['hasRotationAxes'] = c.u8()
    o['hasTextureIndices'] = c.u8()
    n = c.u8()
    o['numSubtextureOffsets'] = n
    c.skip(c.count(n, 16) * 16)
    o['hasRotationSpeeds'] = c.u8()
    if o['type'] == 'BSStripPSysData':
        o['maxPointCount'] = c.u16()
        o['startCapSize'] = c.f32()
        o['endCapSize'] = c.f32()
        o['doZPrepass'] = c.u8()


@parser('BSPackedAdditionalGeometryData')
def p_packedagd(c, o):
    # X360/PS3 files move the vertex streams here (the NiGeometryData keeps only counts and flags).
    o['numVertices'] = c.u16()
    n = c.u32()
    c.count(n, 25)
    o['streams'] = [{'type': c.u32(), 'unitSize': c.u32(), 'totalSize': c.u32(), 'stride': c.u32(),
                     'blockIndex': c.u32(), 'blockOffset': c.u32(), 'flags': c.u8()} for _ in range(n)]
    nb = c.u32()
    c.count(nb, 1)
    blocks = []
    for _ in range(nb):
        if c.u8():
            # NiAGDDataBlock (BS variant). Measured on retail X360 fxfallingrocks01.nif / pipboyarmnpc.nif:
            # the Data payload is Block Size bytes (141 x 40 = 5,640; 9 x 40 = 360), NOT Num Data x
            # Block Size as nif.xml's length/width attributes read; Num Data (22) counts the per-component
            # Data Sizes that follow the block offsets.
            bsz = c.u32()
            m = c.u32()
            c.skip(c.count(m, 4) * 4)
            nd = c.u32()
            c.count(nd, 4)
            sizes = [c.u32() for _ in range(nd)]
            c.skip(c.count(bsz, 1))
            blocks.append({'blockSize': bsz, 'numBlocks': m, 'numData': nd, 'dataSizes': sizes,
                           'shaderIndex': c.u32(), 'totalSize': c.u32()})
        else:
            blocks.append(None)
    o['blocks'] = blocks


@parser('NiSkinInstance')
def p_skininstance(c, o):
    o['data'] = c.ref('data')
    o['skinPartition'] = c.ref('skinpart')
    o['skeletonRoot'] = c.ref('av')
    n = c.u32()
    o['bones'] = c.refs(c.count(n, 4), 'av')


@parser('BSDismemberSkinInstance')
def p_dismember(c, o):
    p_skininstance(c, o)
    n = c.u32()
    c.count(n, 4)
    o['partitions'] = [{'partFlag': c.u16(), 'bodyPart': c.u16()} for _ in range(n)]


@parser('NiSkinData')
def p_skindata(c, o):
    o['skinTransform'] = {'rotation': c.m33(), 'translation': c.v3(), 'scale': c.f32()}
    n = c.u32()
    o['numBones'] = n
    hvw = c.u8()
    o['hasVertexWeights'] = hvw
    # BoneData is 66 bytes (NiTransform 52 + NiBound 16 + u16 count) plus 6 per weighted vertex,
    # so 66 is the lower bound a bone can occupy (an all-empty bone list would trip a larger one).
    c.count(n, 66)
    bones = []
    for _ in range(n):
        b = {'rotation': c.m33(), 'translation': c.v3(), 'scale': c.f32(), 'bound': bound(c)}
        nv = c.u16()
        b['numVertices'] = nv
        if hvw:
            c.skip(c.count(nv, 6) * 6)
        bones.append(b)
    o['bones'] = bones


@parser('NiSkinPartition')
def p_skinpartition(c, o):
    n = c.u32()
    c.count(n, 10)
    o['partitions'] = [skin_partition_struct(c) for _ in range(n)]


@parser('NiAlphaProperty')
def p_alpha(c, o):
    objnet(c, o)
    f = c.u16()
    o['flags'] = f
    o['blend'] = bool(f & 1)
    o['srcBlend'] = name_of(ALPHA_FUNCTION, (f >> 1) & 15)
    o['dstBlend'] = name_of(ALPHA_FUNCTION, (f >> 5) & 15)
    o['test'] = bool(f & 0x200)
    o['testFunc'] = name_of(TEST_FUNCTION, (f >> 10) & 7)
    o['noSorter'] = bool(f & 0x2000)
    o['threshold'] = c.u8()


@parser('NiStencilProperty')
def p_stencil(c, o):
    objnet(c, o)
    f = c.u16()
    o['flags'] = f
    o['enable'] = bool(f & 1)
    o['failAction'] = name_of(STENCIL_ACTION, (f >> 1) & 7)
    o['zFailAction'] = name_of(STENCIL_ACTION, (f >> 4) & 7)
    o['passAction'] = name_of(STENCIL_ACTION, (f >> 7) & 7)
    o['drawMode'] = name_of(STENCIL_DRAW, (f >> 10) & 3)
    o['testFunc'] = name_of(STENCIL_TEST, (f >> 12) & 7)
    o['stencilRef'] = c.u32()
    o['stencilMask'] = c.u32()


@parser('NiZBufferProperty')
def p_zbuffer(c, o):
    objnet(c, o)
    f = c.u16()
    o['flags'] = f
    o['zTest'] = bool(f & 1)
    o['zWrite'] = bool(f & 2)
    o['testFunc'] = name_of(TEST_FUNCTION, (f >> 2) & 7)


@parser('NiMaterialProperty')
def p_material(c, o):
    objnet(c, o)
    if c.ctx.bs < 26:
        o['ambient'] = c.v3()
        o['diffuse'] = c.v3()
    o['specular'] = c.v3()
    o['emissive'] = c.v3()
    o['glossiness'] = c.f32()
    o['alpha'] = c.f32()
    if c.ctx.bs > 21:
        o['emissiveMult'] = c.f32()


@parser('NiVertexColorProperty')
def p_vertexcolor(c, o):
    objnet(c, o)
    f = c.u16()
    o['flags'] = f
    o['colorMode'] = f & 7
    o['lightingMode'] = name_of(LIGHTING_MODE, (f >> 3) & 1)
    o['sourceVertexMode'] = name_of(SOURCE_VERTEX_MODE, (f >> 4) & 3)


@parser('NiShadeProperty', 'NiSpecularProperty', 'NiWireframeProperty', 'NiDitherProperty')
def p_flagprop(c, o):
    objnet(c, o)
    o['flags'] = c.u16()


@parser('NiFogProperty')
def p_fog(c, o):
    objnet(c, o)
    o['flags'] = c.u16()
    o['fogDepth'] = c.f32()
    o['fogColor'] = c.v3()


@parser('NiTexturingProperty')
def p_texturing(c, o):
    objnet(c, o)
    f = c.u16()
    o['flags'] = f
    o['applyMode'] = name_of(APPLY_MODE, (f >> 1) & 7)
    tc = c.u32()
    o['textureCount'] = tc
    maps = {}
    for slot in ('base', 'dark', 'detail', 'gloss', 'glow'):
        if c.u8():
            maps[slot] = tex_desc(c)
    if tc > 5 and c.u8():
        maps['bump'] = tex_desc(c)
        maps['bump']['lumaScale'] = c.f32()
        maps['bump']['lumaOffset'] = c.f32()
        maps['bump']['matrix'] = c.v4()
    if tc > 6 and c.u8():
        maps['normal'] = tex_desc(c)
    if tc > 7 and c.u8():
        maps['parallax'] = tex_desc(c)
        maps['parallax']['offset'] = c.f32()
    for k, slot in enumerate(('decal0', 'decal1', 'decal2', 'decal3')):
        if tc > 8 + k and c.u8():
            maps[slot] = tex_desc(c)
    n = c.u32()
    c.count(n, 1)
    shader_maps = []
    for _ in range(n):
        if c.u8():
            t = tex_desc(c)
            t['mapId'] = c.u32()
            shader_maps.append(t)
        else:
            shader_maps.append(None)
    o['maps'] = maps
    o['shaderMaps'] = shader_maps


@parser('NiSourceTexture')
def p_sourcetexture(c, o):
    objnet(c, o)
    o['useExternal'] = c.u8()
    o['fileName'] = c.string()
    o['pixelData'] = c.ref('data')
    o['pixelLayout'] = c.u32()
    o['useMipmaps'] = c.u32()
    o['alphaFormat'] = c.u32()
    o['isStatic'] = c.u8()
    o['directRender'] = c.u8()
    o['persistRenderData'] = c.u8()


@parser('BSShaderPPLightingProperty', 'Lighting30ShaderProperty')
def p_pplighting(c, o):
    objnet(c, o)
    shader_lighting_property(c, o)
    o['textureSet'] = c.ref('textureset')
    if c.ctx.bs > 14:
        o['refractionStrength'] = c.f32()
        o['refractionFirePeriod'] = c.i32()
    if c.ctx.bs > 24:
        o['parallaxMaxPasses'] = c.f32()
        o['parallaxScale'] = c.f32()


@parser('BSShaderNoLightingProperty')
def p_nolighting(c, o):
    objnet(c, o)
    shader_lighting_property(c, o)
    o['fileName'] = c.sized()
    if c.ctx.bs > 26:
        o['falloff'] = [c.f32(), c.f32(), c.f32(), c.f32()]


@parser('SkyShaderProperty')
def p_sky(c, o):
    objnet(c, o)
    shader_lighting_property(c, o)
    o['fileName'] = c.sized()
    o['skyObjectType'] = c.u32()


@parser('TileShaderProperty')
def p_tile(c, o):
    objnet(c, o)
    shader_lighting_property(c, o)
    o['fileName'] = c.sized()


@parser('TallGrassShaderProperty')
def p_tallgrass(c, o):
    objnet(c, o)
    shader_property(c, o)
    o['fileName'] = c.sized()


@parser('WaterShaderProperty', 'DistantLODShaderProperty', 'HairShaderProperty', 'VolumetricFogShaderProperty',
        'BSDistantTreeShaderProperty')
def p_plainshader(c, o):
    objnet(c, o)
    shader_property(c, o)


@parser('BSShaderTextureSet')
def p_textureset(c, o):
    n = c.u32()
    c.count(n, 4)
    o['textures'] = [c.sized() for _ in range(n)]


# ---- extra data
@parser('NiStringExtraData')
def p_stringextra(c, o):
    extra_data(c, o)
    o['stringData'] = c.string()


@parser('NiIntegerExtraData', 'BSXFlags')
def p_intextra(c, o):
    extra_data(c, o)
    o['integerData'] = c.u32()


@parser('NiFloatExtraData')
def p_floatextra(c, o):
    extra_data(c, o)
    o['floatData'] = c.f32()


@parser('NiBooleanExtraData')
def p_boolextra(c, o):
    extra_data(c, o)
    o['booleanData'] = c.u8()


@parser('NiVectorExtraData')
def p_vecextra(c, o):
    extra_data(c, o)
    o['vectorData'] = c.v4()


@parser('NiBinaryExtraData')
def p_binextra(c, o):
    extra_data(c, o)
    n = c.u32()
    o['numBytes'] = n
    c.skip(c.count(n, 1))


@parser('NiIntegersExtraData')
def p_intsextra(c, o):
    extra_data(c, o)
    n = c.u32()
    o['numIntegers'] = n
    c.skip(c.count(n, 4) * 4)


@parser('NiFloatsExtraData')
def p_floatsextra(c, o):
    extra_data(c, o)
    n = c.u32()
    o['numFloats'] = n
    c.skip(c.count(n, 4) * 4)


@parser('NiStringsExtraData')
def p_stringsextra(c, o):
    extra_data(c, o)
    n = c.u32()
    c.count(n, 4)
    o['data'] = [c.sized() for _ in range(n)]


@parser('BSWArray')
def p_bswarray(c, o):
    extra_data(c, o)
    n = c.u32()
    o['numItems'] = n
    c.skip(c.count(n, 4) * 4)


@parser('BSBound')
def p_bsbound(c, o):
    extra_data(c, o)
    o['center'] = c.v3()
    o['dimensions'] = c.v3()


@parser('BSFurnitureMarker')
def p_furniture(c, o):
    extra_data(c, o)
    n = c.u32()
    c.count(n, 16)
    o['positions'] = [{'offset': c.v3(), 'orientation': c.u16(), 'positionRef1': c.u8(), 'positionRef2': c.u8()}
                      for _ in range(n)]


@parser('NiTextKeyExtraData')
def p_textkeys(c, o):
    extra_data(c, o)
    n = c.u32()
    c.count(n, 8)
    kpos = c.p
    if c.ctx.inline_strings:
        # Key<string> before 20.1.0.1 (cut 2, the 20.0.0.4 .kf): Time + an inline SizedString, so the keys have no
        # fixed stride; the times are checked as read, with check_times' rule (non-decreasing, never NaN).
        keys = [{'time': c.f32(), 'value': c.string()} for _ in range(n)]
        o['textKeys'] = keys
        for i in range(1, n):
            t, prev = keys[i]['time'], keys[i - 1]['time']
            if t < prev or t != t:
                raise ProbeError('key times not sorted at key %d of %d (%g after %g)' % (i, n, t, prev))
        return
    o['textKeys'] = [{'time': c.f32(), 'value': c.string()} for _ in range(n)]
    check_times(c, kpos, n, 8)


@parser('BSBoneLODExtraData')
def p_bonelod(c, o):
    extra_data(c, o)
    n = c.u32()
    c.count(n, 8)
    o['boneLod'] = [{'distance': c.u32(), 'boneName': c.string()} for _ in range(n)]


@parser('BSDecalPlacementVectorExtraData')
def p_decalvectors(c, o):
    extra_data(c, o)
    o['integerData'] = c.u32()
    n = c.u16()
    o['numVectorBlocks'] = n
    for _ in range(n):
        m = c.u16()
        c.skip(c.count(m, 24) * 24)


# ---- collision objects (the Havok body/shape blocks behind them are skipped by size)
@parser('bhkCollisionObject', 'bhkSPCollisionObject', 'bhkPCollisionObject')
def p_collisionobject(c, o):
    o['target'] = c.ref('av')
    o['flags'] = c.u16()
    o['body'] = c.ref('havok')


@parser('bhkBlendCollisionObject')
def p_blendcollisionobject(c, o):
    p_collisionobject(c, o)
    o['heirGain'] = c.f32()
    o['velGain'] = c.f32()


# ---- lights, camera, effects
@parser('NiDirectionalLight', 'NiAmbientLight')
def p_light(c, o):
    light(c, o)


@parser('NiPointLight')
def p_pointlight(c, o):
    light(c, o)
    o['constantAttenuation'] = c.f32()
    o['linearAttenuation'] = c.f32()
    o['quadraticAttenuation'] = c.f32()


@parser('NiSpotLight')
def p_spotlight(c, o):
    p_pointlight(c, o)
    o['outerSpotAngle'] = c.f32()
    o['innerSpotAngle'] = c.f32()
    o['exponent'] = c.f32()


@parser('NiCamera')
def p_camera(c, o):
    avobject(c, o)
    o['cameraFlags'] = c.u16()
    o['frustum'] = [c.f32() for _ in range(6)]
    o['orthographic'] = c.u8()
    o['viewport'] = [c.f32() for _ in range(4)]
    o['lodAdjust'] = c.f32()
    o['scene'] = c.ref('av')
    o['numScreenPolygons'] = c.u32()
    o['numScreenTextures'] = c.u32()


@parser('NiTextureEffect')
def p_textureeffect(c, o):
    dynamic_effect(c, o)
    o['modelProjectionMatrix'] = c.m33()
    o['modelProjectionTranslation'] = c.v3()
    o['textureFiltering'] = c.u32()
    o['textureClamping'] = c.u32()
    o['textureType'] = c.u32()
    o['coordGenType'] = c.u32()
    o['sourceTexture'] = c.ref('texture')
    o['enablePlane'] = c.u8()
    o['plane'] = c.v4()


# ---- controllers
@parser('NiTransformController', 'NiAlphaController', 'NiVisController', 'NiLightDimmerController',
        'BSFrustumFOVController', 'BSRefractionStrengthController', 'BSMaterialEmittanceMultController',
        'NiKeyframeController')
def p_singleinterp(c, o):
    single_interp_controller(c, o)


@parser('NiTextureTransformController')
def p_textransform(c, o):
    single_interp_controller(c, o)
    o['shaderMap'] = c.u8()
    o['textureSlot'] = name_of(TEX_TYPE, c.u32())
    o['operation'] = name_of(TRANSFORM_MEMBER, c.u32())


@parser('NiFlipController')
def p_flip(c, o):
    single_interp_controller(c, o)
    o['textureSlot'] = name_of(TEX_TYPE, c.u32())
    n = c.u32()
    o['sources'] = c.refs(c.count(n, 4), 'texture')


@parser('NiMaterialColorController')
def p_matcolor(c, o):
    single_interp_controller(c, o)
    o['targetColor'] = name_of(MATERIAL_COLOR, c.u16())


@parser('NiLightColorController')
def p_lightcolor(c, o):
    single_interp_controller(c, o)
    o['targetColor'] = name_of(LIGHT_COLOR, c.u16())


@parser('NiFloatExtraDataController')
def p_floatextractl(c, o):
    single_interp_controller(c, o)
    o['extraDataName'] = c.string()


@parser('NiMultiTargetTransformController')
def p_multitarget(c, o):
    time_controller(c, o)
    n = c.u16()
    o['extraTargets'] = c.refs(c.count(n, 4), 'av')


@parser('NiGeomMorpherController')
def p_morpher(c, o):
    time_controller(c, o)
    o['morpherFlags'] = c.u16()
    o['data'] = c.ref('data')
    o['alwaysUpdate'] = c.u8()
    n = c.u32()
    c.count(n, 8)
    o['interpolators'] = [{'interpolator': c.ref('interpolator'), 'weight': c.f32()} for _ in range(n)]


@parser('NiControllerManager')
def p_ctlmanager(c, o):
    time_controller(c, o)
    o['cumulative'] = c.u8()
    n = c.u32()
    o['sequences'] = c.refs(c.count(n, 4), 'sequence')
    o['objectPalette'] = c.ref('palette')


@parser('NiControllerSequence')
def p_sequence(c, o):
    """NiSequence + NiControllerSequence at 20.2.0.7. The BS-dependent fields (nif.xml, evaluated by
    nifxml_bs_conditions.py): ControlledBlock Priority, vercond "#BSSTREAM#" (BS > 0, so present on
    every stream walked here); Anim Notes, a Ref to BSAnimNotes, vercond "(#BSVER# #GTE# 24) #AND#
    (#BSVER# #LTE# 28)"; Num Anim Note Arrays (ushort) and Anim Note Arrays (Ref x Num), vercond
    "#BSVER# #GT# 28". Every other field of NiSequence, NiControllerSequence and ControlledBlock in
    range at 20.2.0.7 carries no vercond.

    At 20.0.0.4 (cut 2, LEGACY_KF_*): NiSequence.Name and Accum Root Name are inline SizedStrings; a ControlledBlock is
    the 33-byte palette form of 10.2.0.0 to 20.1.0.0 (Interpolator, Controller, Priority (vercond #BSSTREAM#), String
    Palette ref, then the five StringOffsets Node Name / Property Type / Controller Type / Controller ID /
    Interpolator ID); the sequence ends with its own String Palette ref (since 10.1.0.113 until 20.1.0.0) and stores
    no anim-note field (since 20.2.0.7) and no Phase (until 10.4.0.1). Every offset is kept as <name>Offset and
    resolved into <name> by resolve_legacy_palettes once the palette blocks have been walked."""
    if c.ctx.inline_strings:
        o['name'] = c.sized()
        n = c.u32()
        o['arrayGrowBy'] = c.u32()
        c.count(n, 33)
        blocks = []
        for _ in range(n):
            b = {'interpolator': c.ref('interpolator'), 'controller': c.ref('controller'), 'priority': c.u8(),
                 'stringPalette': c.ref('palette')}
            for f in LEGACY_OFFSET_FIELDS:
                b[f + 'Offset'] = c.u32()
            for f in LEGACY_OFFSET_FIELDS:
                b[f] = None
            blocks.append(b)
        o['controlledBlocks'] = blocks
        o['weight'] = c.f32()
        o['textKeys'] = c.ref('extra')
        o['cycleType'] = name_of(CYCLE_TYPE, c.u32())
        o['frequency'] = c.f32()
        o['startTime'] = c.f32()
        o['stopTime'] = c.f32()
        o['manager'] = c.ref('controller')
        o['accumRootName'] = c.sized()
        o['stringPalette'] = c.ref('palette')
        return
    o['name'] = c.string()
    n = c.u32()
    o['arrayGrowBy'] = c.u32()
    c.count(n, 28 if c.ctx.bs == 0 else 29)
    blocks = []
    for _ in range(n):
        b = {'interpolator': c.ref('interpolator'), 'controller': c.ref('controller')}
        if c.ctx.bs > 0:  # #BSSTREAM#
            b['priority'] = c.u8()
        b.update(nodeName=c.string(), propertyType=c.string(), controllerType=c.string(), controllerId=c.string(),
                 interpolatorId=c.string())
        blocks.append(b)
    o['controlledBlocks'] = blocks
    o['weight'] = c.f32()
    o['textKeys'] = c.ref('extra')
    o['cycleType'] = name_of(CYCLE_TYPE, c.u32())
    o['frequency'] = c.f32()
    o['startTime'] = c.f32()
    o['stopTime'] = c.f32()
    o['manager'] = c.ref('controller')
    o['accumRootName'] = c.string()
    bs = c.ctx.bs
    cut1a = c.ctx.scope == 'cut1a'  # the cut-1a probe's ref kinds (see SCOPE)
    if 24 <= bs <= 28:
        o['animNotes'] = c.ref(None if cut1a else 'animnotes')
    elif bs > 28:
        n = c.u16()
        o['animNoteArrays'] = c.refs(c.count(n, 4), 'unknown' if cut1a else 'animnotes')


@parser('BSAnimNotes')
def p_animnotes(c, o):
    """nif.xml BSAnimNotes (versions #FO3_AND_LATER#): Num Anim Notes (ushort), Anim Notes (Ref to
    BSAnimNote x Num). No vercond."""
    n = c.u16()
    o['animNotes'] = c.refs(c.count(n, 4), 'animnote')


@parser('BSAnimNote')
def p_animnote(c, o):
    """nif.xml BSAnimNote (versions #FO3_AND_LATER#): Type (AnimNoteType, storage uint), Time (float),
    Arm (uint, cond "Type == 1"), Gain (float) and State (uint), both cond "Type == 2". No vercond."""
    t = c.u32()
    o['noteType'] = t
    o['noteTypeName'] = name_of(ANIM_NOTE_TYPE, t, 'ANT_')
    o['time'] = c.f32()
    if t == 1:
        o['arm'] = c.u32()
    if t == 2:
        o['gain'] = c.f32()
        o['state'] = c.u32()


@parser('bhkBlendController')
def p_blendctl(c, o):
    time_controller(c, o)
    o['keys'] = c.u32()


@parser('BSRefractionFirePeriodController')
def p_fireperiod(c, o):
    time_controller(c, o)
    o['interpolator'] = c.ref('interpolator')


@parser('NiBSBoneLODController', 'NiBoneLODController')
def p_bonelodctl(c, o):
    time_controller(c, o)
    o['lod'] = c.u32()
    n = c.u32()
    o['numNodeGroups'] = c.u32()
    groups = []
    c.count(n, 4)
    for _ in range(n):
        m = c.u32()
        groups.append(c.refs(c.count(m, 4), 'av'))
    o['nodeGroups'] = groups


@parser('NiPSysUpdateCtlr', 'NiPSysResetOnLoopCtlr')
def p_timectl(c, o):
    time_controller(c, o)


@parser('NiPSysModifierActiveCtlr', 'NiPSysEmitterDeclinationCtlr', 'NiPSysEmitterDeclinationVarCtlr',
        'NiPSysEmitterInitialRadiusCtlr', 'NiPSysEmitterLifeSpanCtlr', 'NiPSysEmitterSpeedCtlr',
        'NiPSysGravityStrengthCtlr', 'NiPSysEmitterPlanarAngleCtlr', 'NiPSysEmitterPlanarAngleVarCtlr',
        'NiPSysInitialRotAngleCtlr', 'NiPSysInitialRotAngleVarCtlr', 'NiPSysInitialRotSpeedCtlr',
        'NiPSysInitialRotSpeedVarCtlr', 'NiPSysAirFieldAirFrictionCtlr', 'NiPSysAirFieldInheritVelocityCtlr',
        'NiPSysAirFieldSpreadCtlr', 'NiPSysFieldAttenuationCtlr', 'NiPSysFieldMagnitudeCtlr',
        'NiPSysFieldMaxDistanceCtlr', 'NiPSysRotDampeningCtlr')
def p_psysmodctl(c, o):
    single_interp_controller(c, o)
    o['modifierName'] = c.string()


@parser('NiPSysEmitterCtlr')
def p_emitterctl(c, o):
    p_psysmodctl(c, o)
    o['visibilityInterpolator'] = c.ref('interpolator')


@parser('BSPSysMultiTargetEmitterCtlr')
def p_multiemitterctl(c, o):
    p_emitterctl(c, o)
    o['maxEmitters'] = c.u16()
    o['masterParticleSystem'] = c.ref('av')


@parser('NiLookAtController')
def p_lookatctl(c, o):
    time_controller(c, o)
    o['lookAtFlags'] = c.u16()
    o['lookAt'] = c.ref('av')


@parser('NiPathController')
def p_pathctl(c, o):
    time_controller(c, o)
    o['pathFlags'] = c.u16()
    o['bankDir'] = c.i32()
    o['maxBankAngle'] = c.f32()
    o['smoothing'] = c.f32()
    o['followAxis'] = c.i16()
    o['pathData'] = c.ref('data')
    o['percentData'] = c.ref('data')


@parser('NiUVController')
def p_uvctl(c, o):
    time_controller(c, o)
    o['textureSet'] = c.u16()
    o['data'] = c.ref('data')


# ---- interpolators
@parser('NiTransformInterpolator', 'BSRotAccumTransfInterpolator')
def p_transforminterp(c, o):
    # BSRotAccumTransfInterpolator: nif.xml inherit="NiTransformInterpolator", versions="#BETHESDA#",
    # no fields of its own, so its layout is NiTransformInterpolator's (Transform, Data).
    o['transform'] = quat_transform(c)
    o['data'] = c.ref('data')


@parser('BSTreadTransfInterpolator')
def p_treadtransf(c, o):
    """nif.xml BSTreadTransfInterpolator (inherit NiInterpolator, versions #FO3_AND_LATER#): Num Tread
    Transforms (uint), Tread Transforms (BSTreadTransform x Num), Data (Ref to NiFloatData). A
    BSTreadTransform (size="68") is Name (NiFixedString) + Transform 1 + Transform 2 (NiQuatTransform,
    32 bytes each at 20.2.0.7: TRS Valid is until="10.1.0.109"). No field carries a vercond."""
    n = c.u32()
    c.count(n, 68)
    o['treadTransforms'] = [{'name': c.string(), 'transform1': quat_transform(c), 'transform2': quat_transform(c)}
                            for _ in range(n)]
    o['data'] = c.ref('data')


@parser('NiFloatInterpolator')
def p_floatinterp(c, o):
    o['value'] = c.f32()
    o['data'] = c.ref('data')


@parser('NiBoolInterpolator', 'NiBoolTimelineInterpolator')
def p_boolinterp(c, o):
    o['value'] = c.u8()
    o['data'] = c.ref('data')


@parser('NiPoint3Interpolator')
def p_point3interp(c, o):
    o['value'] = c.v3()
    o['data'] = c.ref('data')


@parser('NiPathInterpolator')
def p_pathinterp(c, o):
    o['pathFlags'] = c.u16()
    o['bankDir'] = c.i32()
    o['maxBankAngle'] = c.f32()
    o['smoothing'] = c.f32()
    o['followAxis'] = c.i16()
    o['pathData'] = c.ref('data')
    o['percentData'] = c.ref('data')


@parser('NiLookAtInterpolator')
def p_lookatinterp(c, o):
    o['lookAtFlags'] = c.u16()
    o['lookAt'] = c.ref('av')
    o['lookAtName'] = c.string()
    o['transform'] = quat_transform(c)
    o['translationInterp'] = c.ref('interpolator')
    o['rollInterp'] = c.ref('interpolator')
    o['scaleInterp'] = c.ref('interpolator')


@parser('NiBlendBoolInterpolator')
def p_blendbool(c, o):
    blend_interpolator(c, o)
    o['value'] = c.u8()


@parser('NiBlendFloatInterpolator')
def p_blendfloat(c, o):
    blend_interpolator(c, o)
    o['value'] = c.f32()


@parser('NiBlendPoint3Interpolator')
def p_blendpoint3(c, o):
    blend_interpolator(c, o)
    o['value'] = c.v3()


@parser('NiBlendTransformInterpolator')
def p_blendtransform(c, o):
    blend_interpolator(c, o)


def bspline_transform_fields(c, o):
    bspline_interpolator(c, o)
    o['transform'] = quat_transform(c)
    o['handles'] = [c.u32(), c.u32(), c.u32()]


@parser('NiBSplineTransformInterpolator')
def p_bsplinetransform(c, o):
    p0 = c.p
    bspline_transform_fields(c, o)
    if c.ctx.payloads:
        o['payload'] = bspline_payload(c, p0, 'transform', TRANSFORM_CHANNELS, False)


@parser('NiBSplineCompTransformInterpolator')
def p_bsplinecomptransform(c, o):
    p0 = c.p
    bspline_transform_fields(c, o)
    o['offsets'] = [c.f32() for _ in range(6)]
    if c.ctx.payloads:
        o['payload'] = bspline_payload(c, p0, 'transform', TRANSFORM_CHANNELS, True)


@parser('NiBSplineCompFloatInterpolator')
def p_bsplinecompfloat(c, o):
    p0 = c.p
    bspline_interpolator(c, o)
    o['value'] = c.f32()
    o['handle'] = c.u32()
    o['offsets'] = [c.f32(), c.f32()]
    if c.ctx.payloads:
        o['payload'] = bspline_payload(c, p0, 'float', ('float',), True)


@parser('NiBSplineCompPoint3Interpolator')
def p_bsplinecomppoint3(c, o):
    p0 = c.p
    bspline_interpolator(c, o)
    o['value'] = c.v3()
    o['handle'] = c.u32()
    o['offsets'] = [c.f32(), c.f32()]
    if c.ctx.payloads:
        o['payload'] = bspline_payload(c, p0, 'Vector3', ('position',), True)


# ---- animation data
@parser('NiTransformData', 'NiKeyframeData')
def p_transformdata(c, o):
    keyframe_data(c, o)


def single_key_group(c, o, tsize):
    """One Data KeyGroup<T> (NiFloatData 10697, NiPosData 11473, NiBoolData 10365, NiColorData 10556);
    with payloads on, o['payload'] is the decoded group."""
    dec = [] if c.ctx.payloads else None
    o['keys'] = key_group(c, tsize, dec)
    if dec is not None:
        o['payload'] = dec[0]


@parser('NiFloatData')
def p_floatdata(c, o):
    single_key_group(c, o, 4)


@parser('NiPosData')
def p_posdata(c, o):
    single_key_group(c, o, 12)


@parser('NiBoolData')
def p_booldata(c, o):
    single_key_group(c, o, 1)


@parser('NiColorData')
def p_colordata(c, o):
    single_key_group(c, o, 16)


@parser('NiVisData')
def p_visdata(c, o):
    """nif.xml NiVisData (12771): Num Keys (12774), Keys (Key<byte>, arg 1, 12776: Time + Value). With
    payloads on, o['payload'] is a group of keyType 1 (0 when empty)."""
    n = c.u32()
    o['numKeys'] = n
    kpos = c.p
    if c.ctx.payloads:
        c.count(n, 5)
        o['payload'] = group_payload(n, 1 if n else 0, 'byte', decode_keys(c, n, 1, 1))
    else:
        c.skip(c.count(n, 5) * 5)
    check_times(c, kpos, n, 5)


@parser('NiUVData')
def p_uvdata(c, o):
    dec = [] if c.ctx.payloads else None
    o['groups'] = [key_group(c, 4, dec) for _ in range(4)]
    if dec is not None:
        o['payload'] = {'groups': dec}


@parser('NiMorphData')
def p_morphdata(c, o):
    n = c.u32()
    nv = c.u32()
    o['numMorphs'], o['numVertices'] = n, nv
    o['relativeTargets'] = c.u8()
    c.count(n, 4)
    names = []
    for _ in range(n):
        names.append(c.string())
        c.skip(c.count(nv, 12) * 12)
    o['frameNames'] = names


@parser('NiBSplineData')
def p_bsplinedata(c, o):
    """nif.xml NiBSplineData (10473): Num Float Control Points (uint, 10476), Float Control Points (float x
    Num, 10477), Num Compact Control Points (uint, 10482), Compact Control Points (short x Num, 10483).
    With payloads on both arrays are read whole into o['payload']."""
    pl = c.ctx.payloads
    n = c.u32()
    o['numFloatControlPoints'] = n
    if pl:
        c.count(n, 4)
        fv, fb = c.fnb(n)
    else:
        c.skip(c.count(n, 4) * 4)
    n = c.u32()
    o['numCompactControlPoints'] = n
    if pl:
        c.count(n, 2)
        o['payload'] = {'floatControlPoints': fv, 'floatControlPointsBits': fb, 'compactControlPoints': c.i16n(n)}
    else:
        c.skip(c.count(n, 2) * 2)


@parser('NiBSplineBasisData')
def p_bsplinebasis(c, o):
    """nif.xml NiBSplineBasisData (10378): Num Control Points (uint, 10382)."""
    o['numControlPoints'] = c.u32()
    if c.ctx.payloads:
        o['payload'] = {'numControlPoints': o['numControlPoints']}


@parser('NiStringPalette')
def p_stringpalette(c, o):
    o['palette'] = c.sized()
    o['length'] = c.u32()


@parser('NiDefaultAVObjectPalette')
def p_avpalette(c, o):
    o['scene'] = c.ref('av')
    n = c.u32()
    c.count(n, 8)
    o['objs'] = [{'name': c.sized(), 'avObject': c.ref('av')} for _ in range(n)]


@parser('NiSequenceStreamHelper')
def p_seqstreamhelper(c, o):
    objnet(c, o)


# =====================================================================================================
# Walk
# =====================================================================================================

def walk(data, h, payloads=False, scope=None):
    """Parse every block by the header's size table. Returns (blocks, errors). payloads=True makes the
    animation parsers decode their payloads into a 'payload' key (see PAYLOADS); off, nothing changes.
    `scope` as for parse_header (see SCOPE); pass the one the header was read with."""
    nb = h['blockCount']
    names = h['typeNames']
    ctx = Ctx(h['bigEndian'], nb, h['strings'], [names[i] for i in h['typeIndex']])
    ctx.bs = h['bsVersion']
    ctx.payloads = bool(payloads)
    ctx.scope = resolve_scope(scope)
    ctx.inline_strings = h['version'] < 0x14010001
    # Measure mode (cut 2): a header before 20.2.0.5 carries no Block Size array, so each block's size is what its
    # parser consumed, the next block starts there, and a block that does not parse (or has no parser) leaves no
    # trustworthy boundary: the walk aborts and every later block is reported unsized.
    measure = h['version'] < 0x14020005
    blocks = []
    errors = []
    pos = h['dataStart']
    n = len(data)
    for i in range(nb):
        tn = names[h['typeIndex'][i]]
        size = None if measure else h['sizes'][i]
        end = n if measure else pos + size
        blk = {'i': i, 'type': tn, 'offset': pos, 'size': size}
        if end > n:
            errors.append('walk truncated: block %d (%s) declares %d bytes at %d, file is %d bytes' % (
                i, tn, size, pos, n))
            blocks.append(blk)
            blocks.extend({'i': j, 'type': names[h['typeIndex'][j]], 'offset': None, 'size': h['sizes'][j],
                           'truncated': True} for j in range(i + 1, nb))
            return blocks, errors
        fn = None if ctx.scope == 'cut1a' and tn in CUT1B_PARSED_TYPES else P.get(tn)
        if fn is None and measure:
            errors.append('walk aborted: block %d (%s) has no parser and the header has no Block Size array' % (i, tn))
            blk['parsed'] = None
            blocks.append(blk)
            blocks.extend({'i': j, 'type': names[h['typeIndex'][j]], 'offset': None, 'size': None, 'truncated': True}
                          for j in range(i + 1, nb))
            return blocks, errors
        if fn is not None:
            ctx.block_start = pos
            ctx.block_index = i
            c = Cur(data, pos, end, ctx)
            try:
                fn(c, blk)
                if measure:
                    size = c.p - pos
                    end = c.p
                    blk['size'] = size
                elif c.p != end:
                    raise ProbeError('size mismatch: parsed %d of %d declared bytes' % (c.p - pos, size))
                blk['parsed'] = True
            except ProbeError as e:
                errors.append('%s#%d: %s' % (tn, i, e))
                blk = {'i': i, 'type': tn, 'offset': pos, 'size': size, 'parsed': False, 'error': str(e)}
                if measure:
                    errors.append('walk aborted: block %d (%s) did not parse and the header has no Block Size array'
                                  % (i, tn))
                    blocks.append(blk)
                    blocks.extend({'i': j, 'type': names[h['typeIndex'][j]], 'offset': None, 'size': None,
                                   'truncated': True} for j in range(i + 1, nb))
                    return blocks, errors
        else:
            blk['parsed'] = None  # no parser: skipped by size
        blocks.append(blk)
        pos = end
    if measure:
        resolve_legacy_palettes(blocks, errors)
    # Footer: num roots + refs, ending exactly at EOF.
    if pos + 4 > n:
        errors.append('footer missing')
    else:
        e = '>' if h['bigEndian'] else '<'
        nr = struct.unpack_from(e + 'I', data, pos)[0]
        if pos + 4 + 4 * nr != n:
            errors.append('footer: %d roots at %d does not end at EOF %d' % (nr, pos, n))
        else:
            for r in struct.unpack_from(e + 'I' * nr, data, pos + 4):
                if r != NULL_REF and r >= nb:
                    errors.append('footer root %d out of range' % r)
    return blocks, errors


def legacy_palette_string(blocks, palette_ref, offset):
    """One 20.0.0.4 StringOffset resolved through its NiStringPalette exactly as the runtime resolver
    (NifControllerSequenceNameTrackReader.TryResolvePaletteString) judges it, or None: an empty sentinel
    (LEGACY_EMPTY_OFFSETS), a ref that is not a parsed NiStringPalette whose repeated Length equals its byte count, an
    offset at or past the palette end, an offset that does not start a NUL-delimited entry, an entry longer than
    LEGACY_PALETTE_ENTRY_MAX bytes or with no terminator within them, and an empty or ASCII-whitespace-only entry.
    The palette text is Latin-1 (one character per byte), so character offsets are byte offsets."""
    if offset in LEGACY_EMPTY_OFFSETS or palette_ref < 0 or palette_ref >= len(blocks):
        return None
    pb = blocks[palette_ref]
    if pb.get('type') != 'NiStringPalette' or not pb.get('parsed'):
        return None
    raw = pb['palette']
    if pb['length'] != len(raw) or offset >= len(raw):
        return None
    if offset > 0 and raw[offset - 1] != '\0':
        return None
    end = raw.find('\0', offset, offset + LEGACY_PALETTE_ENTRY_MAX + 1)
    if end < 0 or end == offset:
        return None
    value = raw[offset:end]
    if not value.strip(' \t\n\x0b\x0c\r'):
        return None
    return value


def resolve_legacy_palettes(blocks, errors):
    """After a measure walk: every parsed 20.0.0.4 NiControllerSequence gets each controlled block's five
    <name>Offset fields resolved into <name> (legacy_palette_string). A non-sentinel offset that does not resolve is
    reported as an error on the sequence and left None; nothing is guessed."""
    for b in blocks:
        if b.get('type') != 'NiControllerSequence' or not b.get('parsed') or 'stringPalette' not in b:
            continue
        for ordinal, cb in enumerate(b['controlledBlocks']):
            for f in LEGACY_OFFSET_FIELDS:
                off = cb[f + 'Offset']
                cb[f] = legacy_palette_string(blocks, cb['stringPalette'], off)
                if cb[f] is None and off not in LEGACY_EMPTY_OFFSETS:
                    errors.append('NiControllerSequence#%d: controlled block %d %s offset %d does not resolve through '
                                  'palette %d' % (b['i'], ordinal, f, off, cb['stringPalette']))


# =====================================================================================================
# Tags
# =====================================================================================================

def rotation_checks(m):
    """Matrix33 as 9 floats in nif.xml storage order (column-major m11 m21 m31 m12 ...).
    Returns (max |R R^T - I|, det)."""
    a, b, cc, d, e, f, g, hh, i = m
    # columns: (a,b,cc), (d,e,f), (g,hh,i)
    cols = ((a, b, cc), (d, e, f), (g, hh, i))
    err = 0.0
    for x in range(3):
        for y in range(3):
            dot = cols[x][0] * cols[y][0] + cols[x][1] * cols[y][1] + cols[x][2] * cols[y][2]
            err = max(err, abs(dot - (1.0 if x == y else 0.0)))
    det = (a * (e * i - f * hh) - d * (b * i - cc * hh) + g * (b * f - cc * e))
    return err, det


def type_of(blocks, r):
    if r is None or r < 0:
        return 'none'
    if r >= len(blocks):
        return 'invalid'
    return blocks[r]['type']


def additive_or_mixed(src, dst):
    return dst == 'ONE' or (dst == 'SRC_COLOR' and src != 'ZERO')


PLATFORMS = ('auto', 'x360', 'ps3', 'pc')


def platform_of(choice, source, big_endian):
    """The platform a file was shipped on: an explicit --platform, else a token in the source path
    ("x360" / "ps3", the Sample/Builds naming), else 'pc' for a little-endian file and 'unknown' for
    a big-endian one (X360 and PS3 share the endian byte, so it cannot decide between them)."""
    if choice and choice != 'auto':
        return choice
    s = (source or '').replace('\\', '/').lower()
    if 'x360' in s or 'xbox 360' in s or '/360_' in s:
        return 'x360'
    if 'ps3' in s:
        return 'ps3'
    return 'unknown' if big_endian else 'pc'


def extract_tags(blocks, h, platform='unknown'):
    tags = set()
    det = {'nodes': [], 'alpha': [], 'shaders': [], 'materials': [], 'controllers': [], 'sequences': [],
           'geometries': [], 'skins': [], 'texturing': [], 'textureSets': [], 'particleData': [],
           'platform': platform}
    add = tags.add

    def interp_tag(r):
        t = type_of(blocks, r)
        if t not in ('none', 'invalid'):
            add('interp=' + t)
        return t

    for b in blocks:
        if not b.get('parsed'):
            continue
        t = b['type']
        f = b
        # ---- NiAVObject family
        if 'rotation' in f and 'flags' in f and 'translation' in f and t != 'NiSkinData':
            err, dv = rotation_checks(f['rotation'])
            nd = {'i': b['i'], 'type': t, 'name': f.get('name'), 'flags': f['flags'], 'rotErr': err, 'rotDet': dv,
                  'scale': f['scale']}
            if f['flags'] & 1:
                add('hidden=1')
                nd['hidden'] = True
            if err > ROT_TOL:
                add('rot=nonorthonormal')
                nd['nonorthonormal'] = True
            if dv < 0:
                add('rot=reflected')
                nd['reflected'] = True
            if t == 'NiBillboardNode':
                add('billboard=%d' % f['billboardMode'])
                nd['billboardMode'] = f['billboardMode']
            elif t == 'NiSwitchNode':
                add('switch=1')
                nd['switchFlags'], nd['index'] = f['switchFlags'], f['index']
            elif t == 'NiLODNode':
                add('lod=1')
                nd['switchFlags'], nd['index'] = f['switchFlags'], f['index']
            if nd.get('hidden') or nd.get('nonorthonormal') or nd.get('reflected') or 'billboardMode' in nd \
                    or 'switchFlags' in nd:
                det['nodes'].append(nd)
        # ---- properties
        if t == 'NiAlphaProperty':
            a = {'i': b['i'], 'flags': f['flags'], 'threshold': f['threshold'], 'blend': f['blend'], 'test': f['test'],
                 'src': f['srcBlend'], 'dst': f['dstBlend'], 'testFunc': f['testFunc'], 'noSorter': f['noSorter']}
            if f['blend']:
                add('blend=%s/%s' % (f['srcBlend'], f['dstBlend']))
            if f['test']:
                add('alphatest=%s/%d' % (f['testFunc'], f['threshold']))
            if f['blend'] and f['test']:
                add('blend+test=1')
            if f['noSorter']:
                add('nosorter=1')
            det['alpha'].append(a)
        elif t == 'NiStencilProperty':
            add('stencil=enable:%d/draw:%s/test:%s' % (f['enable'], f['drawMode'], f['testFunc']))
        elif t == 'NiZBufferProperty':
            add('zbuf=test:%d/write:%d/func:%s' % (f['zTest'], f['zWrite'], f['testFunc']))
        elif t == 'NiMaterialProperty':
            m = {'i': b['i'], 'emissive': f['emissive'], 'alpha': f['alpha'], 'glossiness': f['glossiness'],
                 'specular': f['specular']}
            if 'emissiveMult' in f:
                m['emissiveMult'] = f['emissiveMult']
                if f['emissiveMult'] != 1.0:
                    add('emitmult!=1')
            if any(f['emissive']):
                add('emissive=1')
            if f['alpha'] != 1.0:
                add('alpha!=1')
            det['materials'].append(m)
        elif t == 'NiVertexColorProperty':
            add('vcolor=src:%s/light:%s' % (f['sourceVertexMode'], f['lightingMode']))
        elif 'shaderFlags1' in f:
            s = {'i': b['i'], 'type': t, 'shaderType': f['shaderType'], 'flags1': f['shaderFlags1'],
                 'flags2': f['shaderFlags2'], 'envMapScale': f['envMapScale']}
            add('shadertype=%d' % f['shaderType'])
            f1, f2 = f['shaderFlags1'], f['shaderFlags2']
            for bit in range(32):
                if f1 & (1 << bit):
                    add('sf1=' + SHADER_FLAGS1[bit])
                if f2 & (1 << bit):
                    add('sf2=' + SHADER_FLAGS2[bit])
            add('shaderzbuf=test:%d/write:%d' % (1 if f1 & (1 << 31) else 0, f2 & 1))
            if f['envMapScale'] != 1.0:
                add('envscale!=1')
            if 'textureClampMode' in f:
                s['clamp'] = f['textureClampMode']
                add('clamp=%s/%s' % (SHADER_FAMILY.get(t, t), name_of(CLAMP_MODE, f['textureClampMode'])))
            if t == 'BSShaderNoLightingProperty':
                add('unlit=1')
                s['fileName'] = f['fileName']
                if 'falloff' in f:
                    s['falloff'] = f['falloff']
                    # The exporter's default is (1, cos(90 deg) = -4.37e-08, 1, 0): 7,663 of 9,499
                    # FNV loose NoLighting properties carry it (rounded to 4 decimals), so a
                    # falloff is "authored" when the rounded tuple differs from (1, 0, 1, 0).
                    if tuple(round(x, 4) + 0.0 for x in f['falloff']) != (1.0, 0.0, 1.0, 0.0):
                        add('falloff=1')
            if 'refractionStrength' in f:
                s['refractionStrength'] = f['refractionStrength']
                s['refractionFirePeriod'] = f['refractionFirePeriod']
                if f['refractionStrength'] != 0.0:
                    add('refraction=1')
            if 'textureSet' in f:
                s['textureSet'] = f['textureSet']
                ts = blocks[f['textureSet']] if 0 <= f['textureSet'] < len(blocks) else None
                if ts is not None and ts.get('parsed') and ts['type'] == 'BSShaderTextureSet':
                    used = [k for k, name in enumerate(ts['textures']) if name]
                    add('texslots=' + (','.join(str(k) for k in used) or 'none'))
                    s['slots'] = used
                    normal = ts['textures'][1] if len(ts['textures']) > 1 else ''
                    if normal:
                        s['normalMap'] = normal
                        if f1 & 1:
                            add('normalalpha=1')
                        stem, ext = os.path.splitext(normal)
                        if stem.lower().endswith('_n'):
                            cand = stem[:-2] + '_s' + ext
                            s['specularCompanionCandidate'] = cand
                            s['specularCompanionPlatform'] = platform
                            # The *_s companion ships on X360 only (README: 3,005 of 3,423 *_n on
                            # X360, 3 of 3,430 on PS3, 0 on PC), so the tag follows the platform,
                            # never the endian byte PS3 shares.
                            if platform == 'x360':
                                add('speccompanion=1')
            if 'fileName' in f and t != 'BSShaderNoLightingProperty':
                s['fileName'] = f['fileName']
            if 'skyObjectType' in f:
                s['skyObjectType'] = f['skyObjectType']
            det['shaders'].append(s)
        elif t == 'BSShaderTextureSet':
            det['textureSets'].append({'i': b['i'], 'textures': f['textures']})
        elif t == 'NiTexturingProperty':
            tx = {'i': b['i'], 'applyMode': f['applyMode'], 'textureCount': f['textureCount'], 'maps': {}}
            add('apply=' + f['applyMode'])
            for slot, m in f['maps'].items():
                add('texclamp=' + m['clampMode'])
                src = blocks[m['source']] if 0 <= m['source'] < len(blocks) else None
                tx['maps'][slot] = {'clamp': m['clampMode'], 'filter': m['filterMode'], 'uvSet': m['textureIndex'],
                                    'file': src.get('fileName') if src and src.get('parsed') else None}
                if m['hasTransform']:
                    add('uvxform=1')
                    tx['maps'][slot].update(translation=m['translation'], scale=m['scale'], rotation=m['rotation'],
                                            method=m['transformMethod'], center=m['center'])
            det['texturing'].append(tx)
        elif t == 'NiTextureEffect':
            add('texeffect=clamp:%s/type:%d' % (name_of(CLAMP_MODE, f['textureClamping']), f['textureType']))
        # ---- geometry data: stream tags only where the streams exist (NiTri*Data). Particle data
        # keeps Has bytes with no arrays behind them, so its bytes are details, not features.
        elif 'hasVertexColors' in f:
            if not f.get('streams'):
                det['particleData'].append({'i': b['i'], 'type': t, 'numVertices': f['numVertices'],
                                            'hasVertices': f['hasVertices'], 'hasNormals': f['hasNormals'],
                                            'hasVertexColors': f['hasVertexColors'], 'uvSets': f['uvSets'],
                                            'hasTangents': f['hasTangents']})
                continue
            if f['hasVertexColors']:
                add('vcolors=1')
            add('uvsets=%d' % f['uvSets'])
            add('normals=%d' % (1 if f['hasNormals'] else 0))
            if f.get('nonUnitNormalsSampled'):
                # Retail ships these (14 FNV loose files, e.g. armor/bigbattle/khanbattlearmor.nif),
                # so a non-unit normal is a feature to cover, not a parse error.
                add('normals=nonunit')
            if f['hasTangents']:
                add('tangents=1')
        # ---- skin
        elif t in ('NiSkinInstance', 'BSDismemberSkinInstance'):
            add('skin=1')
            sk = {'i': b['i'], 'type': t, 'bones': len(f['bones']), 'skeletonRoot': f['skeletonRoot']}
            if t == 'BSDismemberSkinInstance':
                add('dismember=1')
                sk['bodyParts'] = [p['bodyPart'] for p in f['partitions']]
            sp = blocks[f['skinPartition']] if 0 <= f['skinPartition'] < len(blocks) else None
            if sp is not None and sp.get('parsed'):
                add('partitions=%d' % len(sp['partitions']))
                sk['partitions'] = len(sp['partitions'])
                sk['weightsPerVertex'] = sorted({p['weightsPerVertex'] for p in sp['partitions']})
                sk['stripped'] = any(p['numStrips'] for p in sp['partitions'])
            det['skins'].append(sk)
        elif t == 'BSFurnitureMarker':
            add('furniture=%d' % len(f['positions']))
        elif t == 'BSPackedAdditionalGeometryData':
            add('packedgeom=1')
            det.setdefault('packed', []).append({'i': b['i'], 'numVertices': f['numVertices'],
                                                 'streamTypes': [s['type'] for s in f['streams']],
                                                 'unitSizes': [s['unitSize'] for s in f['streams']],
                                                 'blocks': f['blocks']})
        # ---- controllers
        if 'startTime' in f and 'target' in f and 'frequency' in f:
            tt = type_of(blocks, f['target'])
            add('ctrl=%s->%s' % (t, tt))
            add('ctrlcycle=' + f['cycleType'])
            if not f['active']:
                add('ctrlinactive=1')
            cd = {'i': b['i'], 'type': t, 'target': f['target'], 'targetType': tt, 'flags': f['flags'],
                  'cycle': f['cycleType'], 'active': f['active'], 'frequency': f['frequency'], 'phase': f['phase'],
                  'start': f['startTime'], 'stop': f['stopTime'], 'next': f['nextController']}
            if 'interpolator' in f:
                cd['interpolator'] = f['interpolator']
                cd['interpolatorType'] = interp_tag(f['interpolator'])
            if 'visibilityInterpolator' in f:
                cd['visibilityInterpolatorType'] = interp_tag(f['visibilityInterpolator'])
            if 'interpolators' in f:
                cd['interpolatorTypes'] = [interp_tag(x['interpolator']) for x in f['interpolators']]
                add('morph=%d' % len(f['interpolators']))
            if t == 'NiTextureTransformController':
                add('uvctrl=' + f['operation'])
                cd.update(textureSlot=f['textureSlot'], operation=f['operation'], shaderMap=f['shaderMap'])
            elif t == 'NiFlipController':
                add('flip=%d' % len(f['sources']))
                cd.update(textureSlot=f['textureSlot'], sources=f['sources'])
            elif t == 'NiMaterialColorController':
                add('matcolor=' + f['targetColor'])
                cd['targetColor'] = f['targetColor']
            elif t == 'NiLightColorController':
                cd['targetColor'] = f['targetColor']
            elif t == 'NiControllerManager':
                cd['sequences'] = f['sequences']
            elif t == 'NiMultiTargetTransformController':
                cd['extraTargets'] = len(f['extraTargets'])
            det['controllers'].append(cd)
        elif t == 'NiControllerSequence':
            sq = {'i': b['i'], 'name': f['name'], 'cycle': f['cycleType'], 'frequency': f['frequency'],
                  'start': f['startTime'], 'stop': f['stopTime'], 'blocks': []}
            add('cycle=' + f['cycleType'])
            for cb in f['controlledBlocks']:
                it = interp_tag(cb['interpolator'])
                ct = type_of(blocks, cb['controller'])
                if ct not in ('none', 'invalid'):
                    add('seqctrl=' + ct)
                sq['blocks'].append({'interpolator': cb['interpolator'], 'interpolatorType': it,
                                     'controller': cb['controller'], 'controllerType': ct, 'node': cb['nodeName'],
                                     'property': cb['propertyType'], 'ctype': cb['controllerType'],
                                     'cid': cb['controllerId'], 'iid': cb['interpolatorId'], 'priority': cb['priority']})
            det['sequences'].append(sq)
        elif t == 'NiLookAtInterpolator':
            for k in ('translationInterp', 'rollInterp', 'scaleInterp'):
                interp_tag(f[k])
        elif 'items' in f and 'weightThreshold' in f:
            for it in f['items']:
                interp_tag(it['interpolator'])
    # ---- geometry linkage: alpha + shader + data per geometry
    for b in blocks:
        if not b.get('parsed') or 'data' not in b or 'properties' not in b:
            continue
        g = {'i': b['i'], 'type': b['type'], 'name': b.get('name'), 'data': b['data'], 'alpha': None, 'shader': None}
        alpha = shader = None
        for r in b['properties']:
            pb = blocks[r] if 0 <= r < len(blocks) else None
            if pb is None or not pb.get('parsed'):
                continue
            if pb['type'] == 'NiAlphaProperty':
                alpha = pb
                g['alpha'] = r
            elif 'shaderFlags1' in pb:
                shader = pb
                g['shader'] = r
        gd = blocks[b['data']] if 0 <= b['data'] < len(blocks) else None
        if gd is not None and gd.get('parsed') and 'hasVertexColors' in gd:
            g['vertexColors'] = bool(gd['hasVertexColors'])
            g['vertexColorsUniform'] = gd.get('vertexColorsUniform')  # None = no color array to measure
            g['vertexStreams'] = bool(gd.get('streams'))
            g['uvSets'] = gd['uvSets']
        if alpha is not None and alpha['blend'] and additive_or_mixed(alpha['srcBlend'], alpha['dstBlend']):
            g['additive'] = True
            # Only a MEASURED non-uniformity counts: particle data stores no color array (its
            # uniformity is None), so an additive particle system is tagged additive+psys instead.
            if gd is not None and gd.get('vertexColorsUniform') is False:
                add('additive+nonuniformvcolor=1')
                g['additiveNonUniformVcolor'] = True
            if gd is not None and not gd.get('streams', True):
                add('additive+psys=1')
                g['additivePsys'] = True
            lit = shader is None or shader['type'] not in UNLIT_SHADERS
            if lit:
                add('additive+lit=1')
                g['additiveLit'] = True
        det['geometries'].append(g)
    return sorted(tags), det


# =====================================================================================================
# Cut-1b animation vocabulary (scope 'cut1b' with payloads only; see ANIMATION TAGS)
# =====================================================================================================

ANIMATION_TAG_PREFIX = 'anim:'
ANIMATION_MARKER_TAG = ANIMATION_TAG_PREFIX + 'animated'
FLT_MAX_F32 = 3.4028234663852886e+38  # 0x7F7FFFFF as a double: the +FLT_MAX / -FLT_MAX clock sentinel
ANGLE_170_DEGREES = 170.0
UNIT_QUATERNION_TOL = 1e-4  # |q|^2 - 1, the Shared unit-rotation bound (the plan's D2), evaluated in double here
INV_FLT = -FLT_MAX_F32  # nif.xml #INV_FLT# (bits 0xFF7FFFFF): a static channel value that is not driven
KEYFRAME_STATE_TYPES = ('NiTransformInterpolator', 'BSRotAccumTransfInterpolator')  # static + NiTransformData keys
BSPLINE_STATE_TYPES = ('NiBSplineTransformInterpolator', 'NiBSplineCompTransformInterpolator')  # static + handles
CHANNEL_STATES = {(True, 'valid'): 'keyed+fallback', (True, 'sentinel'): 'keyed', (False, 'valid'): 'constant',
                  (False, 'sentinel'): 'notDriven'}  # plan section 1.3: (has keys, static class) -> state


def is_animated(h, scope='cut1b'):
    """A file the cut-1b vocabulary describes: its header lists a controller type or NiControllerSequence."""
    return any(kind_of(tn, scope) == 'controller' or tn == 'NiControllerSequence' for tn in h['typeNames'])


def animation_groups(b):
    """(channel, decoded key group) for every key group in a block's payload (see PAYLOADS)."""
    t = b['type']
    pl = b.get('payload')
    if pl is None:
        return
    if t in ('NiTransformData', 'NiKeyframeData'):
        yield 'rotation', pl.get('rotation')
        for ax, g in enumerate(pl.get('xyzRotations') or []):
            yield 'xyz%d' % ax, g
        yield 'translation', pl.get('translations')
        yield 'scale', pl.get('scales')
    elif t in ('NiFloatData', 'NiPosData', 'NiBoolData', 'NiColorData', 'NiVisData'):
        yield 'data', pl
    elif t == 'NiUVData':
        for k, g in enumerate(pl.get('groups') or []):
            yield 'uv%d' % k, g


def _controller_id_class(s):
    """An NiControllerSequence controlled block's Controller ID, classed: the 'N-N-TT_' particle ids keep only
    their tail, long ids fold into 'other'."""
    if s is None:
        return 'null'
    if re.match(r'^\d+-\d+-TT_', s):
        return re.sub(r'^\d+-\d+-', 'N-N-', s)
    return s if len(s) < 24 else 'other'


def _float_signature(by, iref):
    """The weight curve of a morph interpolator: its type, or (key type, key time bits) of its NiFloatData."""
    ib = by.get(iref)
    if ib is None:
        return ('none',)
    if ib['type'] != 'NiFloatInterpolator':
        return (ib['type'],)
    db = by.get(ib.get('data', -1))
    if db is None or 'payload' not in db:
        return ('const',)
    g = db['payload']
    return (g['keyTypeName'], tuple(k['timeBits'] for k in g['keys']))


def _morph_shape(sig):
    """How the non-Base morph weights of one target set share their keys: same, differ, keyedPlusConst,
    allConst, none, or other:<interpolator types>."""
    if not sig:
        return 'none'
    keyed = [x for x in sig if len(x) == 2]
    other = [x for x in sig if len(x) == 1 and x[0] != 'const']
    consts = [x for x in sig if x[0] == 'const']
    if other:
        return 'other:' + ','.join(sorted(set(x[0] for x in other)))
    if not keyed:
        return 'allConst'
    if consts:
        return 'keyedPlusConst'
    return 'same' if len(set(keyed)) == 1 else 'differ'


def _pair_angle_degrees(a, c, dot):
    na = math.sqrt(sum(v * v for v in a)) * math.sqrt(sum(v * v for v in c))
    if not na:
        return None
    return math.degrees(2 * math.acos(max(-1.0, min(1.0, abs(dot) / na))))


def _key_group_items(add, t, ch, g):
    chn = 'xyz' if ch.startswith('xyz') else ('uv' if ch.startswith('uv') else ch)
    kt = g['keyTypeName']
    keys = g['keys']
    add('key:%s/%s/%s' % (t, chn, kt))
    if keys[0]['time'] < 0:
        add('key:negTime')
    if kt == 'TBC_KEY':
        add('tbc:' + chn)
        if any(k['tbc'][1] != k['tbc'][2] for k in keys):
            add('tbc:%s:cneb' % chn)
    if kt == 'QUADRATIC_KEY' and 'forward' in keys[0] and len(keys) >= 2 and \
            any(k['forward'] != k['backward'] for k in keys):
        add('quad:%s/%s:fneb' % (t, chn))
    if ch != 'rotation':
        return
    # Both tests are written so that a NaN component is neither unit nor non-unit, as the seed measured them.
    deviations = [abs(sum(v * v for v in k['value']) - 1) for k in keys]
    if any(d > UNIT_QUATERNION_TOL for d in deviations):
        add('quat:nonUnit')
    unit = all(d <= UNIT_QUATERNION_TOL for d in deviations)
    if kt != 'LINEAR_KEY':
        return
    for i in range(1, len(keys)):
        a, c = keys[i - 1]['value'], keys[i]['value']
        dot = sum(a[j] * c[j] for j in range(4))
        angle = _pair_angle_degrees(a, c, dot)
        if dot < 0:
            add('quat:dotNeg')
            if unit:
                add('quat:dotNeg:unit')
        if angle is not None and angle >= ANGLE_170_DEGREES:
            add('quat:ge170')
            if unit:
                add('quat:ge170:unit')


def _static_class(value):
    """'sentinel' when every component of a static channel value is #INV_FLT#, 'mixed' when some are, else 'valid'
    (a NaN component is not #INV_FLT#)."""
    comps = value if isinstance(value, (list, tuple)) else [value]
    hits = [v == INV_FLT for v in comps]
    return 'sentinel' if all(hits) else ('mixed' if any(hits) else 'valid')


def _channel_state(keyed, value):
    cls = _static_class(value)
    return 'mixed' if cls == 'mixed' else CHANNEL_STATES[(bool(keyed), cls)]


def _channel_state_items(add, b, by):
    """The plan's section 1.3 state of each transform channel (see ANIMATION TAGS, anim:state)."""
    t = b['type']
    if t in KEYFRAME_STATE_TYPES and 'transform' in b:
        db = by.get(b.get('data', -1))
        pl = (db or {}).get('payload') or {}
        rotation = pl.get('rotation') or {}
        keyed = {'translation': (pl.get('translations') or {}).get('numKeys'),
                 'rotation': rotation.get('numKeys') or any((g or {}).get('numKeys')
                                                            for g in (pl.get('xyzRotations') or [])),
                 'scale': (pl.get('scales') or {}).get('numKeys')}
        for ch in TRANSFORM_CHANNELS:
            add('state:%s/%s/%s' % (t, ch, _channel_state(keyed[ch], b['transform'][ch])))
    elif t in BSPLINE_STATE_TYPES and 'payload' in b:
        pl = b['payload']
        for ch in TRANSFORM_CHANNELS:
            keyed = pl['channels'][ch]['handle'] not in (0xFFFF, NULL_REF)
            add('state:%s/%s/%s' % (t, ch, _channel_state(keyed, pl['transform'][ch])))


def _controller_items(add, b, by):
    t = b['type']
    add('ctrl:%s|cycle=%s|active=%d|mgr=%d' % (t, b['cycleType'], b['active'], b['managerControlled']))
    if b['phase'] != 0:
        add('clock:phase!=0')
    if b['startTime'] == FLT_MAX_F32 and b['stopTime'] == -FLT_MAX_F32:
        add('clock:sentinel')
    else:
        if b['startTime'] < 0:
            add('clock:negStart')
        if b['startTime'] == b['stopTime']:
            add('clock:zeroLen')
    if t == 'NiGeomMorpherController':
        sig = [_float_signature(by, x['interpolator']) for x in b['interpolators']][1:]
        add('morph:embedded=' + _morph_shape(sig))


def _sequence_items(add, b, by):
    add('seq:cycle=' + b['cycleType'])
    targets = collections.Counter()
    morph = {}
    for cb in b['controlledBlocks']:
        ib = by.get(cb['interpolator'])
        add('seq:cb=%s|%s|ctrlRef=%s' % (cb['controllerType'], ib['type'] if ib else 'none',
                                         'y' if cb['controller'] >= 0 else 'n'))
        if cb['controller'] < 0 and cb['controllerType'] != 'NiTransformController':
            add('seq:ctrlId=%s|%s' % (cb['controllerType'], _controller_id_class(cb['controllerId'])))
        if cb['controllerType'] == 'NiTransformController':
            targets[cb['nodeName']] += 1
        if cb['controllerType'] == 'NiGeomMorpherController':
            morph.setdefault(cb['nodeName'], []).append(cb)
    if any(v > 1 for v in targets.values()):
        add('seq:dupTarget')
    for lst in morph.values():
        sig = [_float_signature(by, cb['interpolator']) for cb in lst if cb['interpolatorId'] != 'Base']
        add('seq:morph=' + _morph_shape(sig))


def animation_tags(blocks, h):
    """The cut-1b cover vocabulary of one walked file (see ANIMATION TAGS): ANIMATION_MARKER_TAG plus one
    ANIMATION_TAG_PREFIX tag per item, for a file is_animated() admits; nothing for any other file. Needs the
    payload walk (walk(..., payloads=True)): the key-group, B-spline and morph items read 'payload'."""
    if not is_animated(h):
        return []
    items = set()
    add = items.add
    by = {b['i']: b for b in blocks}
    for b in blocks:
        if not b.get('parsed'):
            continue
        t = b['type']
        for ch, g in animation_groups(b):
            if g and g.get('numKeys'):
                _key_group_items(add, t, ch, g)
        if t.startswith('NiBSpline') and t.endswith('Interpolator') and 'payload' in b:
            for chn, ch in b['payload']['channels'].items():
                add('bspline:%s/%s/%s' % (t, chn, 'absent' if ch['handle'] in (0xFFFF, NULL_REF) else 'handle'))
        _channel_state_items(add, b, by)
        if 'cycleType' in b and 'frequency' in b and 'phase' in b:
            _controller_items(add, b, by)
        if t == 'NiTextKeyExtraData':
            for k in b['textKeys']:
                v = k['value'] or ''
                if v == '':
                    add('text:empty')
                if '\r\n' in v.rstrip('\r\n'):
                    add('text:twoEvents')
                elif v.endswith('\r\n'):
                    add('text:crlf')
        if t == 'NiControllerSequence':
            _sequence_items(add, b, by)
    if sum(1 for b in blocks if b['type'] == 'NiControllerSequence') > 1:
        add('seq:multi')
    return [ANIMATION_MARKER_TAG] + sorted(ANIMATION_TAG_PREFIX + i for i in items)


# =====================================================================================================
# Per-file driver
# =====================================================================================================

def probe_bytes(data, want_details=True, platform='auto', source='', payloads=False, scope=None):
    """Probe one NIF/KF held in memory. Returns the record dict (without path/source). `platform`
    is 'auto' (derived from `source` and the endian byte, see platform_of) or an explicit name.
    payloads=True walks with payload decoding; with want_details the decoded payloads are listed in
    details.payloads as {i, type, payload}. Off (the default), the record is the cut-1a probe's at the
    default scope and the widened probe's at scope='cut1b' (see SCOPE; None: 'cut1a')."""
    scope = resolve_scope(scope)
    t0 = time.perf_counter()
    rec = {'sha256': hashlib.sha256(data).hexdigest(), 'size': len(data), 'key': None, 'keyString': None,
           'blockTypes': {}, 'blockCount': 0, 'tags': [], 'details': {}, 'errors': [], 'declined': None,
           'platform': None}
    h = None
    try:
        h = parse_header(data, scope)
    except Decline as e:
        rec['declined'] = str(e)
    except Exception as e:  # noqa: BLE001 - a garbled header is reported, not raised
        rec['declined'] = 'header unreadable: %s: %s' % (type(e).__name__, e)
    if h is None:
        # Decline raised inside parse_header: rebuild what it managed to read for the record.
        h = partial_header(data)
    if h is not None:
        rec['key'] = {'headerString': h['headerString'], 'version': vstr(h['version']),
                      'userVersion': h['userVersion'], 'bsVersion': h['bsVersion'], 'bigEndian': h['bigEndian']}
        rec['keyString'] = key_string(h)
        rec['blockCount'] = h['blockCount']
        counts = {}
        for i in h['typeIndex']:
            if i < len(h['typeNames']):
                counts[h['typeNames'][i]] = counts.get(h['typeNames'][i], 0) + 1
        rec['blockTypes'] = counts
        rec['platform'] = platform_of(platform, source, h['bigEndian'])
    if rec['declined'] is None:
        blocks, errors = walk(data, h, payloads, scope)
        tags, det = extract_tags(blocks, h, rec['platform'])
        if scope == 'cut1b' and payloads:
            # The cut-1b cover vocabulary (see ANIMATION TAGS): only at this scope and only with payloads, so every
            # record of the default scope, and of scope 'cut1b' without payloads, is unchanged.
            tags = sorted(set(tags).union(animation_tags(blocks, h)))
        rec['tags'] = tags
        rec['errors'] = errors
        unparsed = sorted({b['type'] for b in blocks if b.get('parsed') is None})
        if want_details:
            det['unparsedTypes'] = unparsed
            det['blocks'] = [{'i': b['i'], 'type': b['type'], 'name': b.get('name'), 'size': b['size'],
                              'parsed': b.get('parsed')} for b in blocks]
            if payloads:
                det['payloads'] = [{'i': b['i'], 'type': b['type'], 'payload': b['payload']} for b in blocks
                                   if 'payload' in b]
            rec['details'] = det
        else:
            rec['details'] = {'unparsedTypes': unparsed}
    rec['ms'] = round((time.perf_counter() - t0) * 1000, 3)
    return rec


def partial_header(data):
    """Header facts for a declined file (version key and, when present, the block types)."""
    try:
        nl = data.find(b'\n', 0, 80)
        if nl < 0:
            return None
        h = {'headerString': data[:nl].decode('latin-1'), 'version': 0, 'userVersion': 0, 'bsVersion': 0,
             'bigEndian': False, 'blockCount': 0, 'typeNames': [], 'typeIndex': [], 'sizes': [], 'strings': [],
             'dataStart': 0}
        pos = nl + 1
        ver = struct.unpack_from('<I', data, pos)[0]
        pos += 4
        h['version'] = ver
        if ver < 0x05000001:
            return h
        be = False
        if ver >= 0x14000003:
            be = data[pos] == 0
            pos += 1
        h['bigEndian'] = be
        uv = 0
        if ver >= 0x0A000108:
            uv = struct.unpack_from('<I', data, pos)[0]
            pos += 4
        h['userVersion'] = uv
        nb = struct.unpack_from('<I', data, pos)[0]
        pos += 4
        h['blockCount'] = nb
        bs = 0
        has_bs_header = ver == 0x0A000102 or ((ver in (0x14020007, 0x14000005) or (
            0x0A010000 <= ver <= 0x14000004 and uv <= 11)) and uv >= 3)
        if has_bs_header:
            bs = struct.unpack_from('<I', data, pos)[0]
            pos += 4
            pos += 1 + data[pos]
            if bs > 130:
                pos += 4
            if bs < 131:
                pos += 1 + data[pos]
            pos += 1 + data[pos]
            if 103 <= bs < 170:
                pos += 1 + data[pos]
            if bs >= 170:
                pos += 1 + data[pos]
        h['bsVersion'] = bs
        e = '>' if be else '<'
        nt = struct.unpack_from(e + 'H', data, pos)[0]
        pos += 2
        names = []
        for _ in range(nt):
            ln = struct.unpack_from(e + 'I', data, pos)[0]
            pos += 4
            if ln > 256:
                return h
            names.append(data[pos:pos + ln].decode('latin-1'))
            pos += ln
        h['typeNames'] = names
        if nb <= 200000:
            h['typeIndex'] = [i & 0x7FFF for i in struct.unpack_from(e + 'H' * nb, data, pos)]
        return h
    except Exception:  # noqa: BLE001
        return None


# =====================================================================================================
# Sources: loose files and BSA v103/v104 (copied from the project's own ddxm_common.py reader)
# =====================================================================================================

F_DIRNAMES, F_FILENAMES, F_COMPRESSED, F_EMBED, F_XMEM = 0x1, 0x2, 0x4, 0x100, 0x200
SIZE_MASK, TOGGLE_BIT = 0x3FFFFFFF, 0x40000000


def read_bsa_index(path):
    with open(path, 'rb') as f:
        hdr = f.read(36)
        if len(hdr) < 36:
            raise ValueError('file shorter than a BSA header')
        magic, ver, off, aflags, nfold, nfile, tfold, tfile, fflags = struct.unpack('<4sIIIIIIIH', hdr[:34])
        if magic != b'BSA\0' or ver not in (103, 104, 105):
            raise ValueError('not a v103-105 BSA: %r v%d' % (magic, ver))
        if ver == 105:
            raise ValueError('v105 (Skyrim SE, LZ4) BSA: outside cut 1a')
        f.seek(off)
        folders = [struct.unpack('<QII', f.read(16)) for _ in range(nfold)]
        entries = []
        for (_h, count, _o) in folders:
            fname = ''
            if aflags & F_DIRNAMES:
                n = f.read(1)[0]
                fname = f.read(n).rstrip(b'\0').decode('latin-1')
            for _ in range(count):
                _fh, size, foff = struct.unpack('<QII', f.read(16))
                entries.append([fname, None, size, foff])
        if aflags & F_FILENAMES:
            names = f.read(tfile).split(b'\0')
            for i, e in enumerate(entries):
                e[1] = names[i].decode('latin-1')
    # The embed-name (0x100) and XMem (0x200) archive flags exist only from v104 (FO3/FNV/Skyrim LE);
    # an Oblivion v103 archive sets those bits with other meanings (Oblivion - Meshes.bsa: 0x787).
    if ver < 104:
        aflags &= ~(F_EMBED | F_XMEM)
    return {'version': ver, 'flags': aflags, 'entries': entries}


def read_entry_payload(f, flags, entry):
    _folder, _name, raw_size, off = entry
    size = raw_size & SIZE_MASK
    compressed = bool(flags & F_COMPRESSED) != bool(raw_size & TOGGLE_BIT)
    f.seek(off)
    data = f.read(size)
    if len(data) != size:
        return None, 'record range runs past end of archive'
    if flags & F_EMBED:
        n = data[0] if size >= 1 else 0
        if size - (1 + n) < 0:
            return None, 'record smaller than its embedded file name'
        data = data[1 + n:]
    if compressed:
        if flags & F_XMEM:
            return None, 'XMem-coded BSA entry'
        if len(data) < 4:
            return None, 'compressed entry shorter than its size prefix'
        orig = struct.unpack('<I', data[:4])[0]
        try:
            out = zlib.decompress(data[4:])
        except zlib.error:
            out = zlib.decompressobj(-15).decompress(data[4:])
        if len(out) != orig:
            return None, 'zlib size mismatch %d != %d' % (len(out), orig)
        data = out
    return data, None


def entry_path(entry):
    folder, name = entry[0], entry[1] or ''
    return (folder + '/' + name if folder else name).replace('\\', '/')


def iter_source(source, exts):
    """Yield (path, load) pairs; load() returns (bytes|None, error|None)."""
    if os.path.isdir(source):
        for dp, _dn, fns in os.walk(source):
            for fn in sorted(fns):
                if os.path.splitext(fn)[1].lower() in exts:
                    full = os.path.join(dp, fn)
                    rel = os.path.relpath(full, source).replace('\\', '/')

                    def load(full=full):
                        with open(full, 'rb') as fh:
                            return fh.read(), None
                    yield rel, load
        return
    ext = os.path.splitext(source)[1].lower()
    if ext == '.bsa':
        idx = read_bsa_index(source)
        f = open(source, 'rb')
        try:
            for e in sorted(idx['entries'], key=lambda e: e[3]):
                p = entry_path(e)
                if os.path.splitext(p)[1].lower() not in exts:
                    continue

                def load(e=e):
                    return read_entry_payload(f, idx['flags'], e)
                yield p, load
        finally:
            f.close()
        return
    if ext in exts:
        def load():
            with open(source, 'rb') as fh:
                return fh.read(), None
        yield os.path.basename(source), load
        return
    raise ValueError('unsupported source %s' % source)


def load_one(path, entry=None):
    if os.path.splitext(path)[1].lower() == '.bsa':
        if not entry:
            raise SystemExit('--entry is required for a BSA')
        want = entry.replace('\\', '/').lower()
        idx = read_bsa_index(path)
        with open(path, 'rb') as f:
            for e in idx['entries']:
                if entry_path(e).lower() == want:
                    data, err = read_entry_payload(f, idx['flags'], e)
                    if err:
                        raise SystemExit(err)
                    return data
        raise SystemExit('entry not found: %s' % entry)
    with open(path, 'rb') as f:
        return f.read()


# =====================================================================================================
# CLI
# =====================================================================================================

def cmd_probe(args):
    data = load_one(args.file, args.entry)
    rec = probe_bytes(data, want_details=True, platform=args.platform, source=args.file, payloads=args.payloads,
                      scope=args.scope)
    rec['path'] = (args.entry or args.file).replace('\\', '/')
    rec['source'] = args.file.replace('\\', '/')
    if args.json:
        json.dump(rec, sys.stdout, indent=1, default=_json_default)
        print()
        return 0
    print('file      %s' % rec['path'])
    print('sha256    %s  (%d bytes, %.2f ms)' % (rec['sha256'], rec['size'], rec['ms']))
    print('key       %s   %s' % (rec['keyString'], rec['key']['headerString'] if rec['key'] else ''))
    print('platform  %s (%s)' % (rec['platform'], 'given' if args.platform != 'auto' else 'from the source path and endian byte'))
    if rec['declined']:
        print('DECLINED  %s' % rec['declined'])
    print('blocks    %d: %s' % (rec['blockCount'], ', '.join('%s x%d' % kv for kv in sorted(rec['blockTypes'].items()))))
    if rec['details'].get('unparsedTypes'):
        print('skipped   %s' % ', '.join(rec['details']['unparsedTypes']))
    for t in rec['tags']:
        print('tag       %s' % t)
    if args.payloads:
        print('payloads  %d blocks decoded (their values are in --json output, details.payloads)' % len(
            rec['details'].get('payloads', [])))
    for e in rec['errors']:
        print('ERROR     %s' % e)
    return 1 if rec['errors'] else 0


def _json_default(o):
    if isinstance(o, tuple):
        return list(o)
    if isinstance(o, bytes):
        return o.hex()
    raise TypeError(type(o).__name__)


def cmd_census(args):
    exts = tuple(x if x.startswith('.') else '.' + x for x in args.ext.split(','))
    filters = [k.lower() for k in (args.key_filter or [])]
    n = written = declined = errored = 0
    t0 = time.perf_counter()
    source = args.source.replace('\\', '/')
    with open(args.out, 'a', encoding='utf-8') as out:
        for path, load in iter_source(args.source, exts):
            if args.limit and n >= args.limit:
                break
            data, err = load()
            n += 1
            if data is None:
                rec = {'path': path, 'source': source, 'platform': None, 'sha256': None, 'size': None,
                       'key': None, 'keyString': 'unreadable', 'blockTypes': {}, 'tags': [], 'details': {},
                       'errors': ['source: %s' % err], 'declined': 'unreadable entry'}
            else:
                if filters:
                    ph = partial_header(data)
                    ks = key_string(ph) if ph else 'unknown'
                    if not any(k in ks.lower() for k in filters):
                        continue
                rec = probe_bytes(data, want_details=not args.no_details, platform=args.platform, source=source,
                                  payloads=args.payloads, scope=args.scope)
                rec['path'] = path
                rec['source'] = source
            if rec['declined']:
                declined += 1
            if rec['errors']:
                errored += 1
            out.write(json.dumps(rec, default=_json_default) + '\n')
            written += 1
            if args.progress and written % args.progress == 0:
                print('%d files, %d written, %d declined, %d with errors, %.1fs' % (
                    n, written, declined, errored, time.perf_counter() - t0), file=sys.stderr, flush=True)
    print('%s: %d files seen, %d written, %d declined, %d with errors, %.1fs' % (
        args.source, n, written, declined, errored, time.perf_counter() - t0))
    return 0


def control_verdict(base, mut, expect_tag=None):
    """Three-state control result (state, caught). A mutation is caught by an error, by a changed tag
    set, or -- under the oracle A1 rule, when `expect_tag` is given -- only when that tag was reported
    on the original and is no longer reported on the mutant (or an error surfaced)."""
    added = sorted(set(mut['tags']) - set(base['tags']))
    removed = sorted(set(base['tags']) - set(mut['tags']))
    if mut['errors']:
        return 'CAUGHT (error)', True, added, removed
    if expect_tag is not None:
        if expect_tag not in base['tags']:
            return 'INVALID (expected tag %s is not reported on the original)' % expect_tag, False, added, removed
        if expect_tag in mut['tags']:
            return 'NOT CAUGHT (expected tag %s still reported)' % expect_tag, False, added, removed
        return 'CAUGHT (expected tag %s no longer reported)' % expect_tag, True, added, removed
    if added or removed:
        return 'CAUGHT (tags changed: +%d -%d)' % (len(added), len(removed)), True, added, removed
    return 'NOT CAUGHT (no error, tags unchanged)', False, added, removed


def cmd_control(args):
    data = bytearray(load_one(args.file, args.entry))
    base = probe_bytes(bytes(data), want_details=False, platform=args.platform, source=args.file, scope=args.scope)
    if base['declined']:
        print('base file is declined (%s); the control needs a walkable file' % base['declined'])
        return 2
    h = parse_header(bytes(data), args.scope)
    bi = args.block if args.block is not None else 0
    if bi >= h['blockCount']:
        raise SystemExit('block %d beyond %d blocks' % (bi, h['blockCount']))
    start = h['dataStart'] + sum(h['sizes'][:bi])
    off = start + args.offset
    if off >= start + h['sizes'][bi]:
        raise SystemExit('offset beyond block %d (%d bytes)' % (bi, h['sizes'][bi]))
    if args.set_bytes:
        patch = bytes.fromhex(args.set_bytes.replace(' ', ''))
        if not patch:
            raise SystemExit('--set-bytes needs at least one byte')
        end = off + len(patch)
        if end > start + h['sizes'][bi]:
            raise SystemExit('--set-bytes runs past block %d (%d bytes)' % (bi, h['sizes'][bi]))
        data[off:end] = patch
        what = 'wrote %s' % patch.hex()
    else:
        end = min(off + args.length, start + h['sizes'][bi])
        data[off:end] = b'\0' * (end - off)
        what = 'zeroed %d bytes' % (end - off)
    mut = probe_bytes(bytes(data), want_details=False, platform=args.platform, source=args.file, scope=args.scope)
    print('control   %s: %s at +%d of block %d (%s)' % (
        args.entry or args.file, what, args.offset, bi, h['typeNames'][h['typeIndex'][bi]]))
    print('base      errors=%d tags=%d' % (len(base['errors']), len(base['tags'])))
    print('mutated   errors=%d tags=%d' % (len(mut['errors']), len(mut['tags'])))
    for e in mut['errors']:
        print('  ERROR   %s' % e)
    state, caught, added, removed = control_verdict(base, mut, args.expect_tag)
    if added:
        print('  +tags   %s' % ' '.join(added))
    if removed:
        print('  -tags   %s' % ' '.join(removed))
    print('RESULT    %s' % state)
    return 0 if caught else 1


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.split('\n\n')[0])
    sub = ap.add_subparsers(dest='cmd', required=True)
    p = sub.add_parser('probe', help='probe one file (or one BSA entry)')
    p.add_argument('file')
    p.add_argument('--entry')
    p.add_argument('--json', action='store_true')
    p.add_argument('--platform', choices=PLATFORMS, default='auto',
                   help='the platform the file shipped on (auto: x360/ps3 token in the source path, else pc/unknown)')
    p.add_argument('--payloads', action='store_true', help='decode the animation payloads (see PAYLOADS)')
    p.add_argument('--scope', choices=SCOPES, default=None, help='see SCOPE (default: %s)' % DEFAULT_SCOPE)
    p.set_defaults(fn=cmd_probe)
    c = sub.add_parser('census', help='probe every file under a directory or inside a BSA')
    c.add_argument('source')
    c.add_argument('--out', required=True)
    c.add_argument('--key-filter', action='append')
    c.add_argument('--ext', default='.nif,.kf')
    c.add_argument('--no-details', action='store_true')
    c.add_argument('--limit', type=int, default=0)
    c.add_argument('--progress', type=int, default=0, help='print a progress line every N files')
    c.add_argument('--platform', choices=PLATFORMS, default='auto',
                   help='the platform every file of this source shipped on (auto: token in the source path)')
    c.add_argument('--payloads', action='store_true', help='decode the animation payloads (see PAYLOADS)')
    c.add_argument('--scope', choices=SCOPES, default=None, help='see SCOPE (default: %s)' % DEFAULT_SCOPE)
    c.set_defaults(fn=cmd_census)
    k = sub.add_parser('control', help='mutate bytes inside a block and re-probe the copy')
    k.add_argument('file')
    k.add_argument('--entry')
    k.add_argument('--block', type=int)
    k.add_argument('--offset', type=int, default=0)
    k.add_argument('--length', type=int, default=64)
    k.add_argument('--set-bytes', help='hex bytes to write at the offset instead of zeroing (e.g. 8d10 for '
                                       'the A1 rewrite of an NiAlphaProperty flags word)')
    k.add_argument('--expect-tag', help='oracle A1 rule: CAUGHT only when this tag, reported on the original, '
                                        'is no longer reported on the mutant')
    k.add_argument('--platform', choices=PLATFORMS, default='auto')
    k.add_argument('--scope', choices=SCOPES, default=None, help='see SCOPE (default: %s)' % DEFAULT_SCOPE)
    k.set_defaults(fn=cmd_control)
    args = ap.parse_args(argv)
    return args.fn(args)


if __name__ == '__main__':
    sys.exit(main())
