# Fallout: New Vegas classic specular LOD parity

## Scope and evidence boundary

This correction is deliberately limited to the classic FNV direct-sun specular highlight. It does
not fade diffuse lighting, the classic environment-map pass, emissive output, or modern FO4-family
specular/environment lighting.

The evidence pass verified the shipped setting names/defaults, the executable equation and branch
order, and the retail shader consumer. Raw executable addresses and authoritative symbol identifiers
were not retained, so this document makes no address or inferred call-chain claims.

## Verified retail settings

The shipped FNV configuration uses:

| Setting | Default | Meaning |
| --- | ---: | --- |
| `fSpecularLODDefaultStartFade` | `500` | sphere-surface distance where fading begins |
| `fSpecularLODRange` | `300` | distance from start to the zero endpoint |
| `fLODAdjust` | `1` | global multiplier applied after sphere-surface distance |

The shipped quality presets select start distances `200`, `500`, `1000`, and `2000`; the exposed
start-setting limits are `200` and `2000`. The viewer intentionally uses the default profile
`500/300/1` until it has a settings surface for selecting a preset.

The audited PC-final `Fallout - Meshes.bsa` corpus contains 33,454 specular properties across 10,139
meshes. None of those retail meshes is a `STINGER` case. The bypass remains explicit in the math API,
but the ordinary-reference renderer currently supplies the retail non-stinger route; this is not a
claim that unclassified non-retail stinger content is already routed through the renderer.

## Recovered equation

Let `C` and `R` be the transformed authored geometry-bound center and radius, `E` the eye position,
`S` the configured start, `Q` the range, and `A` the camera's global LOD adjustment:

```text
end = S + Q
d = (length(C - E) - R) * A

fade = 1                                      when disabled, stinger, or end <= 0
fade = 1                                      when d < S
fade = 0                                      when d >= end
fade = 1 - ((d - S) / (end - S))              otherwise
```

The subtraction of `R` is essential: retail measures from the sphere surface, not from the object
origin or bound center. The distance is not clamped before the branches; an eye inside the sphere
therefore produces a negative value and full specular. A zero range is an endpoint step: values below
start are one and values at/above start are zero, without division by zero. The math/profile API keeps
the retail stinger bypass explicit; the audited retail reference corpus takes the non-stinger route.

## Bounds and cache contract

Classic `NiTriShapeData` and `NiTriStripsData` readers now consume the serialized `NiBound` in both
little- and big-endian files. The center is transformed by the same scene-graph matrix used to bake
the vertices into mesh-root-local space, and the radius is multiplied by the largest linear-basis
length. Bethesda REFR scale is uniform; using the largest basis remains conservative for malformed
non-uniform input.

Authored bounds are not reused after skinning or morph deformation, and non-finite, negative-radius,
or invalid zero-radius bounds are rejected. The deterministic fallback is:

1. ignore non-finite vertices;
2. take the finite vertex AABB midpoint;
3. take the maximum finite vertex distance from that midpoint;
4. use `(0,0,0), 0` when no finite vertex exists.

Both center and radius are carried through decoded and GPU-cached submeshes. Persistent decoded-mesh
cache version `56` adds the radius immediately after the existing center; version `55` entries are
invalidated because they cannot reproduce sphere-surface distance.

## Rendering and ABI contract

The instanced opaque path keeps `StructuredBuffer<float4x4>` as its complete per-instance payload:
one 64-byte world matrix. Two append-only `float4` registers were added to the per-batch
`InstanceDraw` cbuffer:

- root-local bound center/radius;
- start/end/LOD-adjust/enabled parameters.

The instanced vertex shader transforms the bound per instance and evaluates the exact fade. The
`InstanceDraw` struct grows from 224 to 256 bytes, but its ring allocation was already rounded to the
256-byte D3D12 CBV alignment, so effective per-draw ring consumption is unchanged.

The blended path already issues one placement per draw. It evaluates the same equation on the CPU in
camera-relative space and stores the scalar in the previously unused `uUvScroll.z`. `PerDraw` remains
exactly 256 bytes.

Both vertex routes forward a non-interpolated `TEXCOORD15`. The pixel shader multiplies only the FNV
normal-alpha-masked direct-sun specular scalar by it. Diffuse and classic environment mapping do not
read it. Black authored `NiMaterialProperty` specular RGB remains valid: eligibility and exponent are
carried by `vSpecular.w`, while the highlight color is the sun-light RGB, matching the recovered retail
consumer. The shadow-card vertex permutation assigns fade `1` and does not evaluate camera distance.

## Focused validation matrix

| Contract | Focused coverage |
| --- | --- |
| modern shape/strip NiBound parsing and transform | `NifGeometryVersionExtractionTests` |
| little- and big-endian bound decoding | `NifGeometryVersionExtractionTests` |
| malformed/deformed deterministic fallback | `NifGeometryVersionExtractionTests` |
| defaults, surface distance, endpoints, zero range, LOD adjust | `ClassicSpecularLodTests` |
| transformed radius and render-origin invariance | `ClassicSpecularLodTests` |
| 64-byte matrix-only instances; 256-byte cbuffer layouts/offsets | `ClassicSpecularLodTests` |
| direct-sun-only `TEXCOORD15` consumer and black-tint behavior | `ClassicSpecularLodTests`, `NifSpecularPolicyTests` |
| cache center/radius round trip and v56 gate | `ReferenceDecodedMeshDiskCache12Tests` |
| normal, instanced, pixel, and shadow-card shader permutations | `RenderingShaderCompilationTests` |

This source/test gate establishes equation, serialization, shader scope, and ABI parity. A fixed-camera
retail/viewer capture remains the appropriate final visual acceptance step for highlight strength.
