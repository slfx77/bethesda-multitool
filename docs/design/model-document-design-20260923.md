# ModelDocument: shared model and scene export, final design

Date: 2026-09-23, revision 3 (revision 2 plus the owner's round-4 answers R21-R24 and the DDX measurement). Status: design only, approved by the owner on 2026-09-23. Nothing has been implemented, and no repository was edited.
Audience: the owner and the foundation session.

Inputs:
- The revised design, its surveys and the first adversarial review: `model-document-design.json`.
- `size-accuracy-research.json`, and `mesh-command-design.json` (superseded).
- The owner's rulings: memory `shared_mesh_command_spec.md` and `feedback_visually_lossless_approximation.md`.
- The rulings review and the code review of revision 1, with their read-only checks (`rg3dc_height_census.py`, `review_3dc_all.py`, `review_bb8.py`).

New measurements are read-only Python, git and PowerShell runs. Scripts are in `scratchpad/model-doc-final/` (new this revision: `kf_errors.py`).

Path prefixes used in citations:

| Prefix | Location | Head |
|---|---|---|
| SH | `Multitool.Shared/.worktrees/media-process-v1/src` | bf2127d (clean), branch work/video-batch-v1; citations checked at 775d1cc, and none of the cited files changed through bf2127d |
| SHT | the same worktree's root (`tools/`, `third_party/`, `tests/`) | bf2127d |
| SHD | `Multitool.Shared/docs` | main |
| MB | `Multitool.Shared/INTER-SESSION.md`, the live mailbox | re-read 2026-09-23 after the bf2127d entry |
| BMT | `bmt-material-preparation-20260912/src/BethesdaMultitool` | 5156aa28; pins Shared ef4f8d6 |
| DDX | `bmt-material-preparation-20260912/src/DDXConv/DDXConv` | 5156aa28 |
| NMT | `NeversoftMultitool/src/NeversoftMultitool` | 83b7ec3d |
| AWE | `Multitool-validation/20260912-integration/AweMultitool/src/AweMultitool`, the foundation's live checkout | 299c2bf, exact Shared pin bf2127d |

Reading guide: §1, §8 (cut 1) and §10 hold the decisions; §2–§7 are the design; the appendices hold the review trail, the animation tables and the later cuts.

Labels: **(inferred)** means not verified by reading code or data here. **(recalled)** means taken from memory of external software and unverified.

---

## 1 Decisions

### 1.1 Owner rulings

All rulings are dated 2026-09-23 unless the table says otherwise.

| # | Ruling (quoted where the words matter) | Where applied |
|---|---|---|
| R1 | One generic `mesh` command in Shared. `mesh info` prints format type, vertices, faces, isAnimated, hasSkeleton, numAnimations, and textures one per line as `name WxH bpp colorFormat compression`. `mesh convert <input> <output_path> --format glb\|blend` (default glb) `--scale` (default 1.0), file or directory (batch). Debug subcommands allowed. "Commands specific to speedtree should only exist for debugging our own code." | §3 |
| R2 | "Read once to a generic ModelDocument object that is capable of holding any mesh's information losslessly, then that data is fed to either the glb writer or the blender writer. There should be no intercommunication between the glb and blender paths." | §2 |
| R3 | Output in meters by default; `--scale` multiplies on top. | §4 |
| R4 | Existing outputs are skipped by default (called "skipped", not "failed"); `--overwrite` replaces them. | §3.5 |
| R5 | "Exact is preferred, conversion only when unsupported." | §5, §6 |
| R6 | Unknown timing gets a best guess now, confirmed later by reverse engineering (Ghidra, Capstone). | §4 |
| R7 | NMT's Blender route moves into Shared: "the best code from each project moves to the shared core". | §2.4 |
| R8 | Sample, do not run the whole corpus: one file per NIF version and, within it, the minimal set covering every block type; for other formats an animated, a static and a billboarded file. Strengthened after review: field-level values too, `.kf` included, every oracle able to fail. | §7 |
| R9 | "ModelDocument is better. It's meant to be shared." Rename the Shared type; NMT's and AWE's own ModelDocument classes retire as they migrate. | §2.6 |
| R10 | "Our own code is moving to 0BSD", including the moved NMT Blender code. Blender is GPL; published bpy scripts must be GPL-compatible (0BSD is); .blend outputs belong to the user. | §2.4 |
| R11 | Never bundle Blender; auto-discover it; "the shared settings pane can have a section for including the filepaths to external tools if they're not found or autodiscovery finds the wrong version." Minimum Blender 5.0 (R24). | §3.9 |
| R12 | "If external viewers need PNG, then only retain the PNG and do not add to the file size with DDS." Recorded as: "GLB carries PNG only ... Exactness lives in the document and the Blender route." | §2.4, §5 |
| R13 | "If the format can't handle the data in the same way, then approximate so that it is still visually lossless." Order: exact carrier, then standard extension, then visually lossless approximation (reported), never a silent drop. "Do not bloat outputs with data no consumer of that target can use." | §6 |
| R14 | "Could scene be used for both 3D and 2D contextually? ... room or map is appropriate otherwise." | §3.8 |
| R15 | Runtime layers: "Default-on, flags for the rest." | §3.3, §6 |
| R16 | Read the Gamebryo/Creation world scale from each game's executable once and hardcode it: "we don't want to rederive the value every time". | §4 |
| R17 | "Animations are in-scope for the export." BMT's actor export (FaceGen plus equipment; FO3/FNV mostly complete, Oblivion partial) and its work-in-progress animation support migrate as they are. | §8, App. B, App. C.2 |
| R18 | Neutral `mt_*` Blender property names replace `neversoft_*`; old scripts get updated. | §2.4, App. C.2 |
| R19 (09-22/23) | After the machine crashed from memory starvation: one heavy process at a time, a 4 GiB admission gate that is never bypassed, a watchdog during long runs. Agents never build, test or launch Blender. | §3.4, §7 |
| R20 (standing, 08-11) | No feature flags just to test a change; switch the code over. | §3.10 |
| R21 | External tool paths live once per machine, shared by every Multitool app (Q1: "Yes"). | §3.9 |
| R22 | GLB blends use KHR_materials_transmission, on the owner's condition that it is retained when the GLB is imported into Blender ("If KHR_materials_transmission is retained during Blender import, then that's fine?"). Measured on the installed Blender 5.1 (add-on io_scene_gltf2 5.1.19): the importer drives Principled BSDF Transmission Weight from `transmissionFactor`/`transmissionTexture` (blender/imp/pbrMetallicRoughness.py:218-263) and IOR from KHR_materials_ior (:119-126), and the exporter writes both back (blender/exp/material/extensions/transmission.py, ior.py). Hop F confirms it at the gate. | §6.2, §7.2 |
| R23 | `mesh info` prints the stored channels (`RG` for BC5); the line must be accurate to the actual texture. | §3.2 |
| R24 | Blender minimum 5.0/5.1, not 4.2; the legacy action API and the quantized-normal branch are dropped. | §2.4, §3.9, §6.4 |

### 1.2 The design in brief

- App-owned **source readers** read each source once into **ModelDocument**, the renamed Shared `SceneDocument`. An app's export model (BMT's GlbScene/RenderableSubmesh) is never a source.
- Two **independent writers** behind `IModelWriter`: GLB (Media) and Blender (new `Media.Blender`, NMT's route, 0BSD). Neither references the other. No route passes through a GLB.
- A **GLB holds what a viewer draws**, plus small metadata; images are PNG only. Exact data lives in the document (`mesh dump`) and the .blend.
- Per target: exact carrier, then standard extension, then an approximation **within a stated per-domain bound**, else **Degraded** (listed, exact state kept). Never a silent drop.
- **Meters by default** with provenance; each writer applies units and basis once, on one root.
- **Skip** existing outputs; `--overwrite` replaces model outputs only. Reports never block a run.
- **Serial batches** behind the 4 GiB gate; Blender runs in a job object with a memory limit and a deadline.
- **Cut 1 is one static vertical slice**: FNV/FO3 NIF keys, both writers, `mesh info` and `mesh convert`. Animation (cut 1b) and XnGine (cut 1c) follow with their own gates.

**FaceGen (R17's question).** A `.tri` is an exportable mesh: FRTRI003 stores vertices, triangles, quads, UVs and labeled morphs (BMT Core/Formats/FaceGen/Tri/TriDocument.cs:103-132). An `.egm` is not: it holds symmetric and asymmetric morph modes over a vertex domain, with no faces (BMT Core/Formats/FaceGen/Egm/EgmDocument.cs:5-36).

---

## 2 Architecture

### 2.1 Shape

```
source ─> source reader (app-owned, registered) ─> ModelDocument (Shared, immutable)
                                                        │
          ┌─────────────────────────────────────────────┴──────────────────────────────────┐
          v                                                                                v
 GLB writer (Slfx77.Multitool.Media)                          Blender writer (Slfx77.Multitool.Media.Blender)
 plan -> lowering -> SceneGltfBuilder -> staged .glb          preflight -> plan -> package v2 -> blender.exe
                                          -> commit             (job object) -> staged .blend -> commit
```

- A reader resolves companions (textures, skeletons, palettes, INF) during its one read. Writers touch the file system only to publish.
- `mesh info` evaluates both writers' pure plans through `IModelWriter.Plan`. It decodes no pixels and never launches Blender (§3.2).

### 2.2 ModelDocument

**Base.** The Shared SceneDocument family at SH bf2127d. It already types indexed scenes with instancing, TRS or matrix nodes, skins with unbounded influences, extra UV sets and tangents, morphs with position/normal/tangent deltas, linear/step/cubic and matrix channels, sky/billboard/flipbook presentation, additive alpha, texture transforms, diagnostics and ExtrasJson on nine element kinds. Materials already carry `IndexOfRefraction` (SH Slfx77.Multitool.Core/Models/SceneMaterial.cs:61, integrated in 0d11981), a specular binding read from alpha as KHR_materials_specular defines (:63) and `EmissiveStrength` (:82).

Only the root is renamed (§2.6). Part types keep their `Scene*` names; new parts follow that prefix.

**Three tiers, one rule.**
1. *Typed state.* Format-neutral names; values in the source domain, never clamped, renormalized or unwelded. Inferred values carry `SceneValueProvenance {Authored, ReverseEngineered, Assumed, Unknown}` plus evidence. Writers act only on this tier.
2. *Native state.* `ModelDocument.NativeStates`: `SceneNativeState(SceneElementRef target, string kind, int version, string payloadJson)`, optionally a `SceneSourceLocation` and raw bytes (raw bytes only with `NativeDetail.Full`). Kinds are app-namespaced (`bmt.nif.block`, `nmt.ps2.gsAlpha`).
3. *Portable extras.* The existing ExtrasJson, verbatim.

**The rule.** A writer never needs a native kind to reproduce appearance or behavior. When typed state cannot express something, Shared adds vocabulary. An architecture test forbids comparisons against kind strings in either writer.

**Portable form versus exact form.** A reader fills the exact form (faces, source image bytes, render state, layers) and the triangulation, which only it knows. Shared derives the portable PBR summary (base color, AlphaMode, factors, IndexOfRefraction, specular) from render state and layers with one function, so the two forms cannot drift for new readers; `ValidateStructure` checks legacy adapters that set both.

**Additions.** Every addition is additive: an optional init member or a new collection, one file per new type (M6.4, SHD program.md:128). Cut 1a unless marked.

| Element | Addition |
|---|---|
| Document | `Units: SceneUnits(MetersPerUnit, provenance, evidence)`; `SourceBasis: SceneSourceBasis(Up, Forward, Handedness, NormalizedByReader)` (not `SceneBasis`, to avoid confusion with the existing SceneTransformBasis); `NativeStates`; `LayerSets`; `Palettes` (1c). |
| Node | `Role {Transform, Joint, Helper, Marker, Collision, NoDraw}`. Local transform rule in §2.3. `Billboard: SceneBillboard(Aim {CameraPlane, CameraPosition}, LockedAxis?, Rigid, Pivot, Anchor, RawMode?)`, axes in the source basis, covering NiBillboardNode 0–5 and 9 (BMT Core/Formats/Nif/Parser/NifBillboardMode.cs:7-15) and keeping any other raw value. The enum is `Aim`, because an internal `SceneBillboardFacing` class exists (SH Core/Models/SceneBillboardFacing.cs:7). The legacy `SceneNodePresentation.Billboard` (`SceneBillboardMode {None, AxialY, CameraPlane}`, normalized +Y-up, SH Core/Models/SceneBillboardMode.cs:3) is derived from it by one function, and `ValidateStructure` checks agreement. |
| Primitive | `Faces: SceneFaceList` (n-gons, used from 1c); `PointIndices`; `Attributes: SceneAttributeStream(Name, Semantic, Domain, ComponentType, Components, Normalized, ColorSpace {Srgb, Linear, Unknown} + provenance, Data)`; `Purpose {Render, Collision, Shadow, Occluder, Helper, NoDraw}`; `NormalProvenance {Authored, DerivedSmooth(ruleId), Flat}`. |
| Skin | `BindMode {InverseBind, JointLocalIdentity}`. Weights as authored. |
| Morph target | New `AbsolutePositions` member; `PositionDeltas` stays empty on such targets. `PositionDeltas` is documented as deltas (SH Core/Models/SceneMorphTarget.cs:10, 25) and read by ScenePoseEvaluator, SceneGltfBuilder and SceneValidation, so the legacy `Validate` and `ScenePoseEvaluator` reject a target with `AbsolutePositions` until they support it. A legacy consumer fails closed instead of doubling geometry; a test pins that. |
| Material | `RenderState`: color and alpha blend equations of `SceneBlendTerm(Constant + Scale × Input)` with op {Add, Subtract, ReverseSubtract, Min, Max} and clamp (the affine term makes PS2 GS `(A−B)×C+D` exact); `AlphaTest(Compare, Reference, RawReference)`, which may coexist with blending; `Depth(Test, Write, Compare, ConstantBias, SlopeBias)`; `Stencil(DrawMode {CounterClockwise, Clockwise, Both}, Test?)`; `Cull`; `DrawOrder`; `VertexColorUse(Source {Ignore, Emissive, AmbientDiffuse}, Lighting, NeutralScale)`; `ViewAngleOpacity` (NIF falloff). `Layers: SceneTextureLayer(Role, Binding, ColorOp, AlphaOp, ResultScale, Constant)`, roles including Normal, Specular, Glow, Parallax, Environment, EnvironmentMask, Detail, Dark, Decal. `NormalGreen {Up, Down}` with provenance (RE-13). `Glossiness`, `SpecularColor`. Emission stays separate from the existing EmissiveStrength; `ValidateStructure` allows emission on unlit materials, while the legacy `Validate` keeps rejecting it (SH Core/Models/SceneValidation.cs:151-155). Tint is a layer constant. |
| Image | `Source: SceneImageSource` (§5); PNG becomes optional. A platform companion (the Xbox specular map) is an ordinary image with provenance `PlatformCompanion`. |
| Animation (1b) | `Timing(FramesPerSecond?, RawRate?, RawRateUnit?, provenance, evidence)`; `Clock(Frequency, Phase, Start, Stop, Cycle {Loop, Reverse, Clamp})`; `Events`; `Curves` in source form, following the `QuadraticGranny2` precedent (new kinds appended to the existing `SceneInterpolation` of `SceneTransformTrack`, with required per-key or per-track parameters): Hermite (Quadratic with tangents), TBC with its parameters, quaternion Squad (Quadratic and TBC rotation), B-spline of degree ≤ 3, per-axis Euler; Path and LookAt as node constraints; `PropertyTracks`: MaterialAlpha, MaterialDiffuse, MaterialSpecular, MaterialAmbient, MaterialEmissive (color and strength), LayerUvOffset, LayerUvScale, LayerUvRotation, NodeVisibility, MorphWeight. |
| Layer set | `SceneLayerSet(Id, Label, Members, DefaultOn, SourceKind, ExclusiveGroup?)`, from NiSwitchNode (active child on), NiLODNode (finest level on) and the hidden flag. The committed `Core/Presentation/LayerSelectionOption` (d8754a9) looks like the natural UI binding (inferred). |

**Added in cut 2:** cameras, lights, markers, terrain, water and sky content, per-node source identity (FormID, placement, cell).

**Validation.** The new `ValidateStructure` checks structure only (index ranges, parallel lengths, acyclic graph, finite values, enum domains, agreement of the two forms). The existing `Validate` is unchanged for every current caller and stays the gate inside `SceneGltfBuilder.Build` (SH Slfx77.Multitool.Media/Models/SceneGltfBuilder.cs:42), which is why the GLB writer lowers the document before building (§2.4). The D3D12 viewer gets `SceneRendererAdmission` in cut 2.

**Outside the document:** live parser handles such as NMT's DdmNativeSource and PsxNativeSource.

### 2.3 Source readers

**Contract** (SH Core/Models/Sources):

```
interface IModelSourceReader {
  string FormatId;                                      // "bmt.nif", "bmt.xngine.3d", "bmt.redguard.3dc", ...
  ModelProbeResult Probe(ModelSourceCandidate c);       // bounded content probe (≤ 64 KiB), never the extension alone
  ModelReadResult Read(ModelSourceItem item, ModelReadContext ctx, CancellationToken ct);
}
record ModelReadResult(ModelDocument Document, ModelSourceCoverage Coverage);   // coverage stays outside the document
```

- **Coverage** counts every source element once, as Typed, NativeOnly (with a reason) or Dropped (with a reason). For NIF the element list is the header's block table, independent of what the reader enumerates.
- **Read context** carries the companion resolver, a per-item cache scope, `NativeDetail` and an app option bag (BMT `--data-root`, `--textures-archive`).
- **Registration** is explicit in each app's composition root. Readers live in namespaces with no local ModelDocument: `BethesdaMultitool.Core.Modeling`, `NeversoftMultitool.Core.Formats.Mesh.Modeling`, `AweMultitool.Core.Formats.Mesh.Modeling`.

A reader reads the source once, decodes native semantics into typed state and keeps the raw form as native state, declares units, basis and timing with provenance, throws on corrupt input, and never emits glTF-shaped data (no clamped colors, pre-normalized weights, resampled curves or baked world positions).

**Cut 1a: NIF reader (BMT)** for 20.2.0.7 / user 11, BS 14, 21, 26, 32 and 34, both endiannesses, `.nif`.
- *Parsing.* `NifParser.Parse`'s header and block table (BMT Core/Formats/Nif/Parser/NifParser.cs:20; `ProbeVersionInfo` at :329 is header-only) plus a NifSchema field walk (BMT Core/Formats/Nif/Schema/NifSchema.cs:262-279). Reused readers are audited for bakes first. Two known bakes are not reused as they stand: NifGeometryDataReader truncates float colors to bytes (BMT Core/Formats/Nif/Rendering/Geometry/NifGeometryDataReader.cs:50-53), and NifRenderPropertyReader pre-multiplies emissive by Emit Mult (BMT Core/Formats/Nif/Parser/NifRenderPropertyReader.cs:258-260).
- *Node transforms.* A NIF node stores a 3×3 rotation matrix and a uniform scale. The reader writes TRS when the matrix is orthonormal within 1e-5 per element: determinant > 0 gives a quaternion; determinant < 0 gives a negative uniform scale with the negated rotation, which is exact. Otherwise it keeps the authored matrix (matrix nodes already exist) with provenance. No world bake and no idle-pose override. NiSkinData bone transforms follow the same rule; inverse binds stay as authored.
- *Geometry.* Raw float colors, every UV set, tangents; Xbox binary16 values become float32 exactly. Strips become triangles; a triangle with a repeated index is a strip-stitching artifact, dropped and counted, as BMT's extractor does (BMT NifSubmeshExtractor.cs:751, 796), while zero-area triangles with distinct indices are kept. Strip lengths are native state.
- *Skin and morphs.* NiSkinInstance, NiSkinData, NiSkinPartition; BSDismember partitions as a face stream; NiGeomMorpherController morph data (its weight animation is 1b).
- *Materials.* NiAlphaProperty (blend pair, test function and threshold, both enabled together), NiZBufferProperty, NiStencilProperty (draw mode and test), NiVertexColorProperty (source and lighting modes), NiMaterialProperty (colors, glossiness, alpha, emissive and Emit Mult separately), BSShaderPPLighting and NoLighting properties (texture-set slots 0–5, clamp, falloff, tint), NiTexturingProperty where present, UV offset and scale.
- *Nodes and layers.* NiBillboardNode (raw mode kept, including mode 8), NiSwitchNode, NiLODNode, hidden flag.
- *Textures.* Original bytes from loose Data or BSAs through ArchiveReader. DDX goes through `DdxParser.ConvertDdxToDds` (DDX DdxParser.cs:42) with a fresh `DecodeDiagnostics` in `ConversionOptions.Diagnostics` (DDX ConversionOptions.cs:18), then through the StandardPayload gate (§5.1).
- *Xbox specular companion.* The NIF never names it. The reader resolves it with `NormalMapMerge.ComputeSpecularPath` (BMT Core/Formats/Ddx/NormalMapMerge.cs:36, as NifTextureLoader.cs:104 does today), stores it as its own image with Original bytes and `PlatformCompanion` provenance, and binds it to the material's Specular layer. The BC5+BC4 merge is not applied.
- *Native-only in 1a (coverage NativeOnly, reason stated):* controllers, sequences and interpolators (reason "cut 1b"), particles, Havok (bhk*), multibounds, other extra data.

**Cut 1b: animation in the NIF reader.** `.kf` for BS 14, 21, 24–28 and 30–34, and embedded controllers: NiControllerSequence clock, text keys and interpolators in source curve form, with every controller type in the census classified (Appendix B). NifKeyGroupReader keeps vector Quadratic tangents but skips scalar tangents and TBC payloads (BMT Core/Formats/Nif/Rendering/Animation/NifKeyGroupReader.cs:14, 18-19); it is extended to keep them before reuse. A `.kf` alone resolves targets against `--skeleton` or a `skeleton.nif` found by walking up from its folder; without one, the reader makes nodes named after the tracks with a first-key rest pose, classed Degraded because bones collapse where only rotations are keyed. The five FNV `.kf` files in the 20.0.0.4 key are Unsupported until cut 2 (§7.1).

**Cut 1c: XnGine `.3D` and Redguard `.3DC` readers.**
- `.3D` (Daggerfall ARCH3D; Redguard loose and ROB; Battlespire 3D.BSA, 3D.BS6, loose): n-gon faces; the raw accumulated UVs in 1/16-texel units, first three corners stored as deltas (BMT Core/Formats/Xngine/Mesh/XnGineMesh.cs:33-37), kept as a face-corner stream `xngine.uv16` with the normalized UV as the portable form; authored plane normals; palettized images with a ScenePalette. The reader flips Y and reverses winding exactly (`NormalizedByReader = true`); today the writer conjugates that flip (BMT Core/Formats/Xngine/Mesh/XnGineMeshGlbExporter.cs:99-125). HeaderTail and PlaneData become native state.
- `.3DC`: the keyframe is the base mesh. Narrow files store int16 deltas (Relative targets); wide files store int32 poses (targets with `AbsolutePositions`) (BMT Core/Formats/Redguard/Redguard3dcFile.cs:23-40). One clip plays the poses in stored order (§4.2). The frame table and each frame's plane-normal and plane-data blocks (4 or 12 and 12 or 24 bytes per plane, Redguard3dcFile.cs:240-244) are NativeOnly raw bytes. `.3DC` normals are `DerivedSmooth("newell")`, since BMT computes them with Newell's method (:394-430), unlike `.3D`'s authored plane normals. The per-action sequences live in RGM RAGR graphs (docs/research/redguard_rgm_format.md:163-169) and belong to `scene` in cut 2.

**Later readers:** NMT per source kind from DdmNativeSource and PsxNativeSource; AWE from the ASE parser and the OMT reader; one Shared Granny reader merging AWE's and BMT's; Shadowkey, SpeedTree, Starfield `.mesh`, Oblivion PSP, Van Buren. None starts from an app's export model.

### 2.4 The two writers

`IModelWriter { Format; Preflight(optionBag) -> ModelWriterEnvironment; Plan(document, environment) -> ModelWritePlan; Write(...) }`. Writer-specific options (`--blender`, `--blender-memory`, `--blender-timeout`) travel in an opaque bag that each writer validates, so Core's `ModelConvertOptions` has no Blender field.

**GLB writer** (`ModelGlbWriter`, format `glb`, in Media):
1. `Preflight`: nothing to resolve.
2. `ModelGltfAdmission.Plan(document)`: pure; assigns every feature instance a class (§6).
3. `ModelGltfLowering.Apply(document, plan) -> ModelDocument`: builds a derived document that the unchanged legacy `Validate` accepts, plus an internal `ModelGltfCarriers` object for the new carriers (transmission, node visibility, property-track pointers). Precedent: `SceneAdditiveInterchange` already prepares one document from another (SH Slfx77.Multitool.Media/Models/SceneAdditiveInterchange.cs:14-27). The lowering performs every GLB conversion in §6: PNG decode, units and basis root, color linearization, weight normalization, curve conversion or resampling, absolute-to-relative morphs, TRS decomposition, render state to portable material, same-UV layer bakes. Decoded PNGs exist only for the write and are charged to the item's memory estimate.
4. `SceneGltfBuilder` under `GltfExportIntent.Interchange` (SH Slfx77.Multitool.Media/Models/GltfExportIntent.cs) with an internal model-writer profile: no MSFT_texture_dds, no DDS mips, new carriers enabled. Existing callers never select the profile and stay byte-identical, as M3.5 requires (SHD program.md:116). The profile is not a user flag, so R20 does not apply.
5. `GltfExporter.Save(overwrite: false)` and the commit rule in §3.5.

**What a GLB contains (R12, R13).** Only what a viewer draws, plus small root extras: `multitoolUnits`, `multitoolBasis`, `multitoolLayers`, `multitoolRenderState` for Degraded materials, `multitoolFidelity`, and the relative source path with its SHA-256. No per-vertex source copies, no face arrays, no native payloads, no images a viewer does not sample, and **no underscore-prefixed custom attributes**. Blender's glTF importer builds custom-attribute names from a hash-randomized set but keeps arrays in discovery order: files with a VEC4 and a VEC3 custom attribute failed in 5 of 12 fresh Blender 5.1 processes, and equal shapes permute silently (NMT docs/backlog/mesh-fidelity.md:27-45).

Extensions used, never required except KHR_texture_transform when used (SH SceneGltfBuilder.cs:77-78): KHR_materials_emissive_strength, KHR_materials_specular, KHR_materials_ior, KHR_materials_unlit, KHR_materials_transmission, KHR_animation_pointer, KHR_node_visibility. The pinned SharpGLTF has generated schemas for the last three (SHT third_party/SharpGLTF/upstream/src/SharpGLTF.Core/Schema2/Generated/ext.Transmission.g.cs, ext.AnimPointer.g.cs, ext.NodeVisibility.g.cs).

**Blender writer** (`ModelBlendWriter`, format `blend`, in the new `Slfx77.Multitool.Media.Blender`): NMT's route moved into Shared and re-keyed to ModelDocument.
- *Moved from NMT:* BlendModelExporter, BlendPackageWriter, BlendMorphPackageWriter, the Blend*Manifest types, BlenderLocator (minus its bundled step, §3.9), and the generic part of `BlenderExporter/import_package.py` (about 1,295 generic lines plus about 544 lines of generic ideas, per the survey's AST classification; a judgment call).
- *Not moved:* about 1,015 lines of platform recipes and pixel heuristics (they become state the NMT readers declare) and the dead `_ps2_modulate_*` helpers.
- *Preflight* resolves Blender (§3.9) and returns path, version and job limits.
- *Package v2:* a private temporary `.zip` passed by path (NMT reads all of stdin today, import_package.py:88-90): a manifest mirroring the document graph; little-endian streams (every UV set, float colors, N influences, face sizes and corners, point indices, attribute streams, morphs in stored form, curves); images stored once, keyed by SHA-256.
- *Script `import_model.py`:* linked mesh datablocks and real parenting; n-gons welded by PointIndices; per-loop data through `foreach_set`; absolute shape keys; F-curves and NLA strips carrying the clip clock (1b), using slotted actions (Blender 4.4+, recalled; the minimum is 5.0, R24); text keys as markers; the render-state node compiler (§6); billboard constraints; originals packed with `images.load` plus `pack()`; a units and basis root empty; custom properties `mt_units`, `mt_basis`, `mt_native`, `mt_name`, `mt_fidelity`, `mt_bind`, `mt_color_space`, `mt_source_*`.
- *Process:* `blender --background --factory-startup --python-exit-code 1 --python import_model.py -- --package <zip> --output <staged .blend>`, through MediaProcessRunner in a job object with a deadline (§3.4). The script checks `bpy.app.version >= (5, 0, 0)` (R24) and `PackageVersion == 2` first. C# accepts only a non-empty file starting with `BLENDER` or the zstd magic `28 B5 2F FD` (inferred), then commits it. One Blender process per file, serially.
- *License (R10):* the moved files take 0BSD, with a scoped review recorded in SHD owned-source-licensing.md. Found so far: all commits by slfx77; `import_package.py:1` is `SPDX-License-Identifier: MIT` by the owner; no 40-character overlap with the staged io_thps_scene copies; a PCSX2 citation only in dead code. No Blender or glTF-importer source is copied. A Media.Blender NOTICE states that .blend output belongs to the user.

### 2.5 Assembly layering

```
Slfx77.Multitool.Core                (no package references)
  ModelDocument + parts; Sources contracts; Export contracts (IModelWriter, registry, plans, fidelity types),
  ModelOutputPlanner, ModelInfo/ModelConvert operations, formatters, memory gate
        ^                              ^
Slfx77.Multitool.Media.Processing  (new; no SharpGLTF)
  Processes/* moved from Media with LICENSE.NMT.md and NOTICE.md (namespaces unchanged),
  job object, memory watchdog, ExternalToolLocator + version probes
        ^                              ^
Slfx77.Multitool.Media               Slfx77.Multitool.Media.Blender (new)
  (+SharpGLTF, ImageSharp)             ModelBlendWriter, BlendAdmission, package v2, Python/import_model.py
  ModelGlbWriter, admission, lowering
        ^
Slfx77.Multitool.WinUI(.Direct3D12)  (settings section in cut 2; renderer admission in cut 2)
Apps reference Media + Media.Blender; each composition root fills ModelSourceRegistry and ModelWriterRegistry.
```

Media references SharpGLTF directly (SH Slfx77.Multitool.Media/Slfx77.Multitool.Media.csproj). The `Processes` folder depends on nothing else in Media, so moving it with unchanged namespaces changes no consumer source.

Architecture tests: (1) Media.Blender's closure contains neither SharpGLTF.Core nor Slfx77.Multitool.Media; (2) Media does not reference Media.Blender; (3) neither writer compares `SceneNativeState.Kind`; (4) Core has no package references; (5) operations reach plans only through `IModelWriter`. Media.Blender encodes no images: lossless re-containerizations it needs (indexed PNG, and in cut 2 OpenEXR, gray PNG, channel-mask DDS) arrive as reader-made StandardPayloads (§5.1).

### 2.6 Rename and coordination with the foundation

**Facts, refreshed** (SH bf2127d; MB after the bf2127d entry). The foundation moved eight commits while this design was written, so R0 refreshes them again:
- Shared is clean at bf2127d. Quadratic Granny 2 matrix animation is integrated (22bc762, bf2127d): it appended `QuadraticGranny2` to `SceneMatrixInterpolation` with a per-track `CurveDurationSeconds`, which is the precedent this design follows for NIF curve kinds. Material IOR is integrated (0d11981, 775d1cc) and "released for coordinated adoption"; layer selection is integrated (d8754a9, a5df5d2, 09be412). The foundation reports no "document rename or Blender change" and asks: "Propose exact ownership for the peer's still-unassigned document/export work" (MB, foundation update). Its note on this proposal: "Propose the exact additive files and ownership before implementation. Preserve existing `SceneDocument` and exporter behavior by default, AWE's existing 2D `scene` CLI ..." (MB, the coordination note on this proposal). The owner's rename ruling (R9) came after that note.
- `git grep -lw SceneDocument` at bf2127d: **src 38** (Core 13, Media 11 including 2 README.md, WinUI.Direct3D12 14), **tests 78**, **tools 27** (26 in tools/Slfx77.Multitool.ControlGallery, 1 in tools/validation/neutral-scene-consumer), **docs 7**. No `nameof` or string literal uses the name, so no serialized identifier changes.
- BMT 5156aa28: 20 code files (7 src, 13 tests) plus 8 docs.
- NMT 83b7ec3d: none committed; the one reference is the untracked working-tree `DdmNeutralSceneAdapter.cs`.
- AWE 299c2bf: 66 files (29 src, 36 tests, 1 other); at bf92854, 52 of 65 imported `Slfx77.Multitool.Core.Models`. AWE also declares a 2D `SceneDocument` (AWE Core/Formats/Scene/SceneDocument.cs) and a local `ModelDocument` (AWE Core/Formats/Mesh/Conversion/ModelDocument.cs). The older checkout at C:/dev/Multitool/AweMultitool (24f5348) shows 6 and is not the live pin.
- NMT and AWE declare `ModelDocument` in their `Mesh.Conversion` namespaces, where C# binds the local type first.

**Steps:**

| Step | Who | What |
|---|---|---|
| R0 | bethesda → mailbox | Re-read MB, refresh the claims, publish this design and the §9 claims. No Shared file is touched before the foundation agrees. |
| R1 | foundation, one commit at a quiet point it chooses | Rename `SceneDocument` to `ModelDocument` (type and file) across Shared src (with READMEs), tests, **tools** and docs. Namespaces and other `Scene*` names stay. The freeze covers only this commit; later additions merge normally under a file-level claim each. |
| R2 | bethesda, at BMT's next pin bump | Mechanical rename in 20 code files and 8 docs. |
| R3 | NMT session, at its pin bump | Rename its local type to `LegacyModelDocument` in the same bump; update its untracked adapter. |
| R4 | foundation, at AWE's pin bump | The same for AWE (about 66 files). AWE's 2D `SceneDocument` then collides with nothing. |
| R5 | each app session | Delete `LegacyModelDocument` after that app's readers pass sampled parity. |

---

## 3 Commands

### 3.1 Layering

- **Shared owns** the operations and formatters (`ModelInfoOperation`, `ModelConvertOperation`; `SceneInfo`/`SceneConvert` in cut 2), following the shared-operation pattern (SH Core/Operations/IExecutableOperation.cs).
- **Each app owns** a thin System.CommandLine shell. Shared takes no System.CommandLine dependency: BMT pins 2.0.8, NMT and AWE 2.0.11 (each Directory.Packages.props). Shells may add app read-context options.
- GUI export buttons later call the same operations (GUI and CLI parity, M3.5).

### 3.2 `mesh info <input> [-e <entry>] [-v] [--json]`

- Reads the full document, so it cannot disagree with `convert` except on rows marked `pending`.
- **Decodes no pixels.** DXT1 transparency is found by a bounded block scan of level 0 (each block's two endpoints and indices; no decode). Fidelity rows that depend on decoded pixels, such as a derived alpha or the per-file vertex-color bound, print `pending decode` and are counted as `pending N`; `mesh fidelity` and `convert` resolve them.
- **Never launches Blender.** Version-dependent rows use the version read from blender.exe's PE version resource on Windows (measured: Blender 5.1's blender.exe reports FileVersion 5.1), elsewhere from the version folder beside the executable (inferred). The script's `bpy.app.version` check at convert time stays authoritative.
- Invariant culture, one `key: value` per line; texture lines are indented two spaces with exactly the owner's five fields.

```
file: meshes/clutter/signs/neonsign01.nif
format: nif | NIF 20.2.0.7, user 11, BS 34, little-endian
units: 0.0142857 m per unit | Assumed | Gamebryo 70 units per meter; executable read pending (RE-1)
vertices: 2114
faces: 3402
isAnimated: true
hasSkeleton: false
numAnimations: 1
textures: 3
  textures/clutter/signs/neonsign01.dds 512x512 4bpp RGB BC1
  textures/clutter/signs/neonsign01_n.dds 512x512 8bpp RGBA BC3
  textures/clutter/signs/neonsign01_g.dds 256x256 4bpp RGB BC1
fidelity: glb exact 14, converted 3, approximated 1, degraded 2, metadata 3, dropped 0, pending 1 | blend exact 20, approximated 1, dropped 0
```

Values are illustrative.

- **vertices / faces:** source points and source faces (an n-gon counts once), summed over primitives.
- **isAnimated:** at least one clip, a multi-frame pose set or a property track. **hasSkeleton:** a node has the Joint role. **numAnimations:** clips; embedded controllers outside a sequence form one clip `(controllers)`.
- **Texture fields:** `bpp` of the stored format; `colorFormat` is the stored channel set (R, RG, RGB, RGBA, L, LA, Indexed); BC5 prints `RG`, the stored channels (R23: accurate to the actual texture); DXT1 prints `RGBA` only when a block uses the 3-color transparent mode; `compression` is BC1–BC7, None or a platform code; a missing texture prints `name missing`, an unknown field `?`.
- **`-v`** adds basis, layer sets, native-state count, source coverage; per texture: container, layout, mip count, declared color space, palette, SHA-256 prefix; every fidelity row; the resolved Blender path and version (read, not run).
- **`--json`**: one object per input, schema `multitool.mesh-info/1`.

### 3.3 `mesh convert <input> <output_path> [options]`

| Option | Default | Meaning |
|---|---|---|
| `--format glb\|blend` | glb | Which writer. |
| `--scale <f>` | 1.0 | Finite and > 0; multiplies meters (§4). |
| `--overwrite` | off | Replace existing model outputs (§3.5). Never applies to reports. |
| `-e, --entry <glob>` / `--all` | — | Archive entries, repeatable. A container input (BSA, BA2, disc image) requires `-e` or `--all`, as `scene` does. A directory is a batch without a flag. |
| `--layers default\|all\|<id,...>` | default | R15, below. |
| `--skeleton <file>` | auto | `.kf` input (1b). |
| `--report <file.json>` | none | Run report (fidelity, coverage, per-item peak memory). An existing file is never overwritten (§3.5). |
| `--json` | off | Machine-readable summary on stdout. |
| `--blender <path>` | discovery | Overrides discovery for this run; NMT keeps `--blender-helper` as an alias. |
| `--blender-memory <GiB>`, `--blender-timeout <min>` | 4, 10 | Job limits (§3.4). |
| app read-context options | — | For example BMT's `--data-root`, `--textures-archive` from `export nif`. |

**`--layers`:** `default` writes default-on layers and lists every omitted one in `multitoolLayers`; `all` writes each alternative as an extra glTF scene (scene 0 is the default state). The .blend always holds every layer as a collection, hidden unless on by default.

**Output paths:** a file input whose `output_path` ends in the format's extension is written to exactly that file (a mismatched extension is a usage error); otherwise `output_path` is a directory and the output is `<stem>.<ext>`. A directory input is a recursive batch mirroring the relative layout; enumeration skips the `output_path` subtree. An archive entry becomes `<archive stem>/<entry path>.<ext>`.

### 3.4 Batch planning and memory safety

**Planning** finishes before anything converts: expand inputs in ordinal order; probe each candidate (≤ 64 KiB, content-based); compute every output and the report path; check collisions and §3.5; call the writer's `Preflight` (with `--format blend`, a missing or too-old Blender is a plan error).

**Execution is strictly serial**, one document in memory at a time, companion caches scoped to the item. NifTextureResolver's cache is a ResourceRegistry-visible lazy cache (BMT Core/Formats/Nif/Rendering/NifTextureResolver.cs:12-20); it is still scoped to one item here.

- **Admission before each item (R19):** available physical memory ≥ max(4 GiB, estimate + 2 GiB). Companions are unknown before the read, so the estimate is a fixed per-format ceiling: 8 × source bytes plus a companion allowance (NIF: 256 MiB, an inferred starting value). The report records real peaks and the ceilings are revised from them, with provenance.
- **Short memory:** poll every 5 s for up to 60 s, then stop the batch; the remaining items become NotAttempted (low memory) and the run exits 1. Rerunning resumes, because existing outputs are skipped and the report never blocks.
- **Watchdog during an item:** every 5 s; below 2 GiB available it cancels the item (Failed: memory watchdog) and stops the batch.
- **Blender admission:** ≥ max(4 GiB, job limit + 2 GiB), so 6 GiB with the default 4 GiB limit. A lower `--blender-memory` (recorded in the report) reduces this down to the 4 GiB floor; nothing is admitted below the floor. That is R19, not a tunable.
- **Job object.** MediaProcessRunner starts processes with `System.Diagnostics.Process` (SH Slfx77.Multitool.Media/Processes/MediaProcessRunner.cs:34), which can neither start suspended nor pass a job attribute. Chosen: assign the process to a job immediately after `Start`, with `JOB_OBJECT_LIMIT_PROCESS_MEMORY`, `JOB_MEMORY`, `KILL_ON_JOB_CLOSE` and `DIE_ON_UNHANDLED_EXCEPTION` (the last keeps a limit hit from hanging on a Windows Error Reporting dialog). The window before assignment is acceptable because background Blender starts no child and commits little memory before running the script (inferred). Fallback if that measures a leak: a CreateProcess launcher with `PROC_THREAD_ATTRIBUTE_JOB_LIST`, as the Python validator tool does (gltf_validator_tool.py:88-91).
- **Deadline:** new, kills the job. The existing bounded drain stays: ProcessDiagnosticBuffer keeps a prefix and suffix within 1 Mi characters (MediaProcessRunner.cs:12; ProcessDiagnosticBuffer.cs:8), and `MaximumStandardOutputBytes` bounds stdout.
- Other platforms: a 5 s RSS watchdog replaces the job object.
- The report records peak private bytes and minimum available memory per item.

### 3.5 Skip and `--overwrite`

| When | Condition | Outcome |
|---|---|---|
| Plan | Output exists, no `--overwrite` | **Skipped (exists)**; the source is never read. |
| Plan | The output path is an existing directory where a file is planned | Failed (item) |
| Plan | Two items map to one output (`OrdinalIgnoreCase` on Windows and macOS, `Ordinal` on Linux) | Plan error; nothing runs |
| Plan | An output equals an input after full-path normalization | Plan error |
| Plan | The `--report` file exists | **Not an error.** The run writes `<name>.<UTC yyyyMMddTHHmmssZ>.json` beside it and prints that path. |
| Plan | An orphaned `.<name>.<guid>.tmp` beside a planned output | Warning; deleted only with `--overwrite` and an exact staging-pattern match |
| Commit | `File.Move(staged, output, overwrite: false)` throws `IOException` and the error is file-exists (Windows `HResult & 0xFFFF` 80 or 183, Unix errno 17) | **Skipped (appeared during run)**; staged file deleted |
| Commit | Any other `IOException` or `UnauthorizedAccessException` | Failed, with the OS error text |
| Commit | `--overwrite` and the destination is locked | Failed |

Both writers stage into the output directory and commit this way. `GltfExporter.Save` already defaults to `overwrite: false` and commits with `File.Move` (SH Slfx77.Multitool.Media/Models/GltfExporter.cs:50, 107). NMT's hard-coded `File.Move(stagedOutputPath, outputPath, true)` (NMT Core/Formats/Mesh/Conversion/BlendModelExporter.cs:46) does not move over.

### 3.6 Outcomes, summary and exit codes

Per item: Converted, Skipped (exists or appeared), NotAModel, Unsupported (recognized but declined, with a fixed reason), Failed (reason), NotAttempted (low memory), Canceled. `OperationJobOutcome` has only Completed, Canceled and Faulted, so this is a new type.

Summary: `Converted 64, skipped 3 (exist), not converted 4 (2 not models, 2 unsupported), failed 0 in 00:04:12`, then fidelity totals per writer.

| Exit | When |
|---|---|
| 0 | Every planned item Converted or Skipped. NotAModel and Unsupported files found while expanding a directory are listed, not errors. |
| 1 | Any item Failed or NotAttempted, or an explicitly named input is NotAModel, Unsupported or unreadable. |
| 2 | Usage or plan error, or Blender missing or too old. Nothing converted. |
| 130 | Canceled. Committed outputs remain; staged files are deleted. |

### 3.7 Debug subcommands

| Command | Purpose |
|---|---|
| `mesh formats` | Registered readers, unit rows, static admission tables. |
| `mesh dump <input> [--native]` | The document as JSON, including every exact value the GLB omits. |
| `mesh fidelity <input> --format glb\|blend` | The admission plan with pending rows resolved; no write. |
| `mesh validate <input>` | `ValidateStructure`. |
| `mesh package <input> <out.zip>` | The Blender package without launching Blender, for inspection. |
| `mesh census <input...>` (cut 2) | Version, block-type and feature census plus the cover selector. Cut 1 uses the Python probe (§7.1). |

SpeedTree gets no command (R1); any SpeedTree switch lives under `mesh dump`/`mesh fidelity` and only debugs our own generator.

### 3.8 `scene`: contextual 2D/3D is coherent, and it is the recommendation

The dimension is a property of what the reader produces: placements resolved against 3D meshes produce a ModelDocument; layered rasters and vectors produce a MapDocument, which Shared already has (SH Core/Maps/MapDocument.cs:6-42; ordered MapLayers with initial visibility, MapLayer.cs:13-44).

- `--format glb|blend` means 3D; `--format png` means 2D (one PNG per layer plus `scene.json` with ids, labels, default visibility, placements and coordinate mapping). Default: `glb` when a 3D reading exists, else `png`. An impossible format is a usage error naming the possible ones.
- `scene info` prints `dimension: 3D` and `(2D also available)` for sources offering both (Arena MIF, Daggerfall RMB automap, Redguard WLD). `--scale` multiplies meters in 3D and is an integer nearest-neighbor factor in 2D.

No invocation needs both dimensions in one output, so the `map` fallback is not needed; "map" stays the name of the 2D document type.

Precedents: AWE `scene` reads 2D rooms (AWE CLI/SceneCommand.cs:51); NMT's ten `*-scene` composers (dhj, ngage, p8, pg, thaw, thps4-j2me, thug-gamemidlet, thug, zodiac-actor, zodiac) mostly write PNG layers plus scene.json, and dhj-scene and ngage-scene also write GLB; BMT has `classic map` (2D, BMT CLI/Commands/Classic/ClassicCommand.cs:2036) and `classic level|dungeon|block export` (3D, :50, :457, :619). Neither NMT nor AWE has a 3D scene verb; their 3D levels go through `mesh`.

Surface: `scene info <input> [-e] [--textures] [--json]`; `scene convert <input> <output_path> [--format glb|blend|png] [--scale] [--overwrite] [-e|--all] [--include lights,markers,collision,editor,terrain] [--layers]`; debug `scene manifest`, `scene render`. Same planner, skip rules, exit codes and writers as `mesh`. `mesh` exports one authored model; `scene` resolves placements or selects runtime state.

### 3.9 External tools: discovery and the settings section

A new `ExternalToolLocator` in Media.Processing follows the Shared glTF-validator locator (SH Slfx77.Multitool.Media/Validation/GltfValidatorLocator.cs): no fall-through on a bad override. Order: explicit CLI path, saved setting, PATH, known install folders. There is **never a bundled helper**; NMT's first probe of `<app>/BlenderExporter/blender.exe` (NMT Core/Formats/Mesh/Conversion/BlenderLocator.cs:43, 86) is removed. A stale explicit or saved path is an error.

- A candidate counts only if the executable exists. Measured here: `C:\Program Files\Blender Foundation\Blender 4.2` holds only a leftover `4.2` data folder and no blender.exe, which a folder-name probe would misreport; `Blender 5.1` holds blender.exe.
- Versions are read without launching Blender (PE version resource; version folder elsewhere). FFmpeg is probed with `ffmpeg -version`. Results are cached by path, size and modification time.

| Tool | Used by | Minimum | Setting |
|---|---|---|---|
| Blender | Blender writer | 5.0 (R24); tested on 5.1, the installed version | path |
| FFmpeg + ffprobe | Shared video poster/probe; NMT video; BMT DependencyChecker and AudioTranscriber | current behavior | directory or path |
| FluidSynth + SoundFont | NMT MIDI (NMT Core/Formats/Audio/FluidSynthMidiRenderer.cs:227) | — | two paths |

Three FFmpeg searches move onto the locator as each app adopts: BMT App/Helpers/Dependencies/DependencyChecker.cs:118-146, NMT Core/Formats/Video/SfdConverter.cs:28-33, NMT Core/ExecutablePathLocator.cs.

**Settings section (cut 2).** A Shared WinUI control, `ExternalToolsSettings`, in the common settings view after theme and language, through `ThemeLanguageSettings.AdditionalContent` (SH Slfx77.Multitool.WinUI/Settings/ThemeLanguageSettings.cs:76): detected path and version, status (found, missing, too old), Browse and Reset, accessible names. The glTF validator and chdman are developer-only and not listed. Cut 1 has the CLI `--blender` option and discovery. Paths are stored once per machine and shared by every Multitool app (R21), in the Shared settings store beside theme and language (`ExternalToolSettings`, §9 row 4); an app never keeps its own copy.

### 3.10 Legacy commands

- BMT has no top-level `mesh` or `scene` today, only `classic mesh` and `classic map` (BMT ClassicCommand.cs:952, 2036), so cut 1 collides with nothing. `export nif` and `classic mesh export` stay until sampled parity.
- NMT's `mesh <input> -o` collides: `-t/--textures` takes a DDX PNG directory (NMT CLI/MeshCommand.cs:48), `--scale` multiplies native coordinates (:52), `--worldzone-time-of-day` defaults to `all` (:57). AWE has `mesh` (AWE CLI/MeshCommand.cs:51) and a 2D `scene` (AWE CLI/SceneCommand.cs:51).
- When NMT and AWE adopt, `info` and `convert` are subcommands (System.CommandLine matches a subcommand token first, so a file named `info` needs `./info`). The legacy positional form stays as a documented alias with native-unit semantics, an M3.5 exception, until sampled parity; **then it retires** (R20, "switch the code over"). AWE's `scene --render`/`--json` map onto `scene convert --format png` and `scene info --json`.

---

## 4 Units, basis and timing

**Rule.**
- Readers never scale or rotate vertex data; the one exception, XnGine's exact reflection, is declared with `NormalizedByReader`.
- A document declares `MetersPerUnit` and `SourceBasis`, each with provenance. **Each writer applies both exactly once, on the same root:**
  - GLB: a root node that is an ancestor of the joints carries the scale `MetersPerUnit × --scale` (float32-rounded, Converted) and the rotation from the source basis to +Y-up. For NIF (+Z-up, right-handed) that is −90° about X, `(x, y, z) ↦ (x, z, −y)`, exact. Skinned-mesh nodes stay at the root, because glTF ignores their transform.
  - Blender: a root empty with the same scale and the rotation to +Z-up (identity for NIF; +90° about X for a Y-up source such as normalized XnGine). The scene stays metric at unit scale 1.0.
  - Billboard axes, `LockedAxis` and presentation offsets are expressed in the source basis. The legacy presentation's "normalized +Y-up/+Z-front convention" (SH Core/Models/SceneNodePresentation.cs:4-8) is reached only through the derivation function in §2.2.
  - A synthetic test per basis (Z-up NIF, Y-up XnGine) has a control that omits the rotation and must fail.
- When one document mixes unit systems, the reader emits a scale node and records it (XnGine placements in world units with meshes in 1/256 units; NMT PSX per-mesh divisors). Such nodes are Converted with float32 rounding stated (1/2.25 and 1/36 are not exact in float32).
- Values print with provenance in `info`, and appear in `multitoolUnits` and `mt_units`.
- **A reader cannot register without a row here.** The Unknown fallback (factor 1.0, printed "Unknown", plus a fidelity row) is only a guard.
- Light range and camera clip planes convert explicitly (cut 2).

### 4.1 Meters per source unit

| Format (owner, cut) | Meters per unit | Provenance | Evidence | RE |
|---|---|---|---|---|
| NIF/KF: FNV (PC, X360, PS3) and Skyrim LE/SE 1/69.99125 = 0.0142875; FO3 and Oblivion 1/69.9904 = 0.0142877; FO4/FO76 1/69.99125; Morrowind 1/70 (bethesda; FNV/FO3 cut 1, rest cut 2) | as listed | ReverseEngineered for FNV, FO3, Oblivion, Skyrim (RE-1 done 2026-09-23, docs/world_scale_units_re1.md: the engine defines 128 units = 6 feet); Assumed for FO4/FO76 (same chain, not read) and Morrowind | Constant 70 units per meter (BMT Core/Games/GameProfiles.cs:45-50); its comment says 1.42875 cm, which is 0.9144/64 and differs by 0.0125%. BMT scales Havok by 7 for Oblivion/FO3/FNV (BMT Core/Formats/Nif/Collision/HavokCollisionExtractor.cs:33-36). Skyrim's Havok scale is 69.99125 = 64/0.9144 (recalled). | RE-1 |
| Starfield NIF + `.mesh` (bethesda, cut 2) | 1.0 | Assumed | `WorldUnitsPerMetre = 1` (BMT GameProfiles.cs:200-224) | RE-1 |
| XnGine `.3D`, Daggerfall ARCH3D (bethesda, 1c) | 0.025/256 per native unit (0.025 m per world unit) | Assumed | Native units are 1/256 world unit (BMT XnGineMesh.cs:30, 109). Objects 55000–55005 measure 48 × 88 × 6 world units, a door leaf (2.2 m); 41000 is 19 × 88, a bed (0.48 × 2.2 m); tables 41109/41110 are 33 high (0.83 m). Identities inferred from shape (`arch3d_bounds.py`). Agrees with Daggerfall Unity's 0.025 (recalled). | RE-2 |
| XnGine `.3D`, Redguard loose and ROB (bethesda, 1c) | 0.0125/256 = 1/20480 per native unit (1/80 m per world unit) | Assumed | Placements are world units × 256 (BMT Core/Formats/Redguard/RedguardRgmFile.cs:56-57). TV_TABLE 60 high (0.75 m); LH_DOOR 186 (2.3 m); MG_CHR01 seat width 44 (0.55 m); TV_BOTTL 27 (0.34 m) (`rob_bounds.py`, `xng3d_bounds.py`). | RE-3 |
| Redguard `.3DC` actors (bethesda, 1c) | 1/20480 per native unit applied | **Assumed (human actors only)** | Keyframes are normalized to the int16 range, not sized: over all 147 unique `.3DC`, the median keyframe Y extent is exactly 32,766 native units; 48 lie within 2% of it and 22 in [32,760, 32,768] (`rg3dc_height_census.py`, rerun here). CYRSA001 spans Y 32,766 and X 32,682. DOGA001 (Y 30,060, Z 40,706) and GOATA001 (Y 33,214) come out near human height. True size comes from a per-actor runtime scale; a candidate is the RGM MPSZ mesh-size rows (RedguardRgmFile.cs:50-51; inferred). Every `.3DC` export carries the Degraded row "actor scale unknown; geometry normalized to the int16 range". The sample adds DOGA001 as the control. | RE-3 |
| XnGine `.3D`, Battlespire (bethesda, 1c) | (1/64)/256 per native unit | Assumed (weak) | World units are mesh native/256 (BMT CLAUDE.md, BS6 POSI). CHAIR 57, TABLE 46, BENCH 36, BAREL 48, ARMOR.3D stand 152: 0.012–0.023 m per world unit, median about 0.016. | RE-4 |
| Arena MIF/RMD voxels (bethesda, cut 2) | 2.0/128 per unit (128 units per voxel, BMT Core/Formats/Arena/ArenaSceneAssembler.cs:172) | Assumed (weak) | Door textures fill a voxel face (recalled). | RE-5 |
| Shadowkey meshes and zones (bethesda, cut 2) | 0.5/256 per mesh unit (0.5 m tile) | Assumed | Mesh unit = 1/256 tile, measured (BMT Core/Formats/Travels/Shadowkey/ShadowkeyZoneSceneBuilder.cs:88-98); a door is 4.047 tiles (2.0 m), a barrel 1.46, a crate 1.21. | RE-6 |
| Granny (BMT Van Buren; AWE) | From the file | Authored | ArtToolInfo UnitsPerMeter and basis (BMT Core/Formats/Granny/Gr2File.cs:205-212) | — |
| Van Buren B3D, Oblivion PSP RenderWare (bethesda, cut 4) | 1.0 | Assumed (weak) | RenderWare/Jefferson convention (recalled) | RE-7 |
| NMT THPS3–PG (NMT session) | 0.0254 (1 inch) | Assumed (strong) | Inferred from an engine constant's name, `SUB_INCH_PRECISION = 16` in THUG render.h, cited at NMT Core/Formats/Mesh/Ps2Scene/Scene/Ps2SceneFile.cs:18; also ThawPs2ReplayVertexDecoder.cs:9, NgcColBspNode.cs:27. A name is not a measurement. | RE-15 |
| NMT THPS PSX (NMT session) | 1/63, plus per-mesh divisors 2.25 and 36 as scale nodes | Assumed (divisors ReverseEngineered) | A bench seat of 28.444 units ≈ 0.45 m (NMT Core/Formats/Mesh/Conversion/MeshLevelPolicy.cs:25); divisors at NMT Core/Formats/Mesh/Psx/PsxMeshHeaderReader.cs:99, 141. The Blender-only 0.02 (NMT BlenderExporter/import_package.py:62, 71) retires. | RE-16 |
| AWE ASE/OMT (foundation, cut 4) | Stated before registration | — | No unit evidence in AWE code (grep) | foundation |
| BOS Xbox and PS2 | Not registered | — | Position scale is an undecoded shader constant (BMT Core/Formats/BrotherhoodOfSteel/BosXboxMesh.cs:5-6) | RE-8 |

### 4.2 Timing

| Source | Rate | Provenance | Evidence and RE |
|---|---|---|---|
| NIF/KF sequences and embedded controllers | Key times in seconds; controller clock | Authored | — |
| Redguard `.3DC` pose clip | 15 fps; Step between poses | Assumed; Assumed | No measurement; the period convention. Integer poses with no blend data suggest pose switching (inferred). RE-3: RG.EXE's pose-advance tick and interpolation; RAGR command kinds 2/3/4 (`value >> 4`), the likely per-frame durations (redguard_rgm_format.md:163-169). |
| Shadowkey sequences | `Rate` as frames per second | Assumed | 1, 3–7, 10, 11, 14, 15, 17, 29 across 402 sequences (BMT Core/Formats/Travels/Shadowkey/ShadowkeyMesh.cs:45-49); fits a 30 Hz cap (inferred). RE-6. |
| XnGine animated textures and flats; Arena flats | 10 fps | Assumed (weak) | No measurement. RE-9. |
| NMT PSX wibble and color pulses | 60 Hz | ReverseEngineered (NMT) | The NMT script's 60 Hz driver |
| Granny | Authored | Authored | — |
| Starfield `.af`, Oblivion PSP HAnim, BOS animation | Not decoded | — | Blocked |

### 4.3 Reverse-engineering backlog

Each item is read once with Ghidra or Capstone and hardcoded with provenance (executable name, SHA-256, address, value); a test pins the literal.

| Item | Target | What to read |
|---|---|---|
| RE-1 | Gamebryo/Creation units, per game, into GameProfiles | Read: game units per Havok unit, the gravity vector passed at world creation (in Havok units), and any player-height, eye-height or collision-capsule constant. Meters per unit = 9.80665 / (\|g_havok\| × unitsPerHavok); that formula **assumes** designers set Earth gravity, so the record keeps what was read separately from the assumed g. Example: with FNV's Havok scale 7, a gravity of 98.0665 Havok units/s² gives exactly 1/70 m. No outcome is presumed. The player constants decide if they disagree with gravity. FNV first: the X360 MemDebug PDB names globals; a DRM-free PC runtime image is under Sample/ReverseEngineering. |
| RE-2 | Daggerfall FALL.EXE | Player height, eye height or collision radius |
| RE-3 | Redguard RG.EXE | Per-actor scale (MPSZ candidate); pose-advance tick; interpolation |
| RE-4 | Battlespire | Player height |
| RE-5 | Arena A.EXE (unpacked, 304,624 bytes) | Voxel and eye constants |
| RE-6 | Shadowkey N-Gage .app | Tile scale; sequence Rate unit |
| RE-7 | Van Buren and Oblivion PSP | Units |
| RE-8 | BOS | Xbox position scale; level asset type |
| RE-9 | XnGine | Texture and flat animation rates |
| RE-10 | Palettes | 6-bit VGA expansion rule (§5.1) |
| RE-11 | Vertex colors | NIF vertex-color color space and the space of the multiply, from the FNV shader disassembly (docs/fnv_basic_sls_shader_disassembly.txt in the BMT worktree) |
| RE-12 | Xbox 360 | DDX format bytes 0x43 and 0x86 (§5.4) |
| RE-13 | Normal maps | Green-channel convention per Bethesda family |
| RE-14 | NiBillboardNode mode 8 | 4 FNV files use it (for example `nvdlc04/dungeons/silo/silo_elevator/nvdlc04_silo-elevator-shaft02.nif`): an undocumented mode or a probe misread |
| RE-15 | NMT THPS3–PG | The unit behind `SUB_INCH_PRECISION` (NMT session) |
| RE-16 | NMT PSX | The 1/63 base unit (NMT session) |
| RE-17 | Gamebryo rotation keys | How Linear, Quadratic, TBC and B-spline quaternion keys are evaluated (slerp, squad, component-wise then normalized; recalled), confirming the Appendix B classes |

---

## 5 Textures

### 5.1 In the document

`SceneImage.Source: SceneImageSource`; the PNG level 0 becomes optional for new readers, and the legacy constructor (SH Slfx77.Multitool.Core/Models/SceneImage.cs:14) is unchanged. A source holds:
- **Container** (DDS, DDX, BA2 DX10, NiPixelData, PNG, TGA, TEXTURE.nnn, BSI, TEXBSI, ...) and **Original**: the exact bytes and their SHA-256, which is also the deduplication key (BMT deduplicates by PNG bytes today, BMT Core/Formats/Nif/Rendering/Export/NifNeutralSceneAdapter.cs:460-479).
- **Location**, and a **Descriptor**: dimensions, depth, array layers, cube flag, mips (each Authored or Missing), pixel-format code (FourCC, DXGI, Xenos or GS), bpp, channels, compression, declared color space, alpha meaning, layout.
- An optional **StandardPayload**: a *lossless* re-containerization made by the reader, with a note (DDX to DDS; palettized to indexed PNG with PLTE and tRNS; in cut 2, SNORM/float to OpenEXR, R8 to gray PNG, B5G6R5 to a channel-mask DDS).
- **DisplayTransforms**, recorded and never baked (BC5 Z reconstruction, palette cycling), and **Derivation** (recipe plus input image ids: BMT's specular and metallicRoughness maps from normal-map alpha, later FaceGen EGT).

**The DDX gate.** Measured 2026-09-23 on every `.ddx` entry of the three distinct Xbox 360 New Vegas `Fallout - Textures.bsa` archives (Final 22,616 entries, byte-identical to the August 2010 and February 2011 prototype archives; July 2010 prototype 26,123; April 2010 partial archive 3,243 live entries), evidence under BMT `TestOutput/ddx-lossless-20260923/run1/` with a report per run:
- **Complete 3XDO entries in block-compressed formats convert by an exact relayout.** DDXConv's per-file loss counters (skipped, unwritten and duplicate destination blocks; padding; truncated reads) are zero on every such entry. An independent oracle (a Python transcription of the decompiled XGraphics tiling, sharing no code with DDXConv) checked a stratified sample of 761 files at every mip level: 0 misplaced blocks, 0 blocks absent from the decoded source, 0 non-zero blocks in the cropped tile padding, and the two-stream storage order coherent on 340 of 340 discriminating files. The same oracle then ran over every entry (51,982 files, finished 17:34): Final 21,688 exact relayouts, 712 3XDR entries dropping stored levels, 154 3XDR with nothing to drop, 52 with fabricated zero levels, 10 strips; July 22,096 / 3,806 / 155 / 52 / 13 plus the zero-byte entry; April 2,977 / 265 / 0 / 0 / 0 plus the cut entry. No entry in any archive was judged a non-relayout, and the route each file took matched the header prediction file for file (Final: 16,419 two-stream, 3,196 tail-base-0, 1,344 exact square, 472 aligned sequential, 259 plain, 50 mip-0-only, 10 short-mip-0 strips).
- **The heuristic routes are not reached by complete entries.** `UnswizzleDxtTextureHeuristic`, the atlas unpack, the double-size and large-linear routes and the full-atlas fallback (DDX DdxChunkProcessor.cs:975-986, dead code for every block-compressed format code) served 0 of the sampled complete entries. They served the memory-dump carves: of 249 carves from two prototype dumps, 164 were incomplete, took a damage or heuristic route, or raised counters; 62 decoded as exact relayouts.
- **Three losses on complete entries that the counters cannot see**, each handled by the gate below: (a) 3XDR entries (866 Final, 3,961 July, 265 April) decode mip 0 only; `Convert3Xdr` drops the stored levels after it and records nothing (DDX DdxParser.cs:97-136). (b) The 23 strips with format byte 0x43 or 0x86 (§5.4) are decoded under DXT1 and are the only complete entries with non-zero counters. (c) About 52 single-level UI textures per build receive all-zero mip levels the header does not declare; no data is lost.
- The counters' own claim is bounded: they cover destination coverage, not decode correctness. Controls that had to fail did: nine truncated copies and the April archive's own cut entry (`bosunderarmor\outfitm.ddx`, 50,454 of 65,536 blocks unwritten) raised counters; five corrupted copies were caught by the oracle against their pristine twins; sixteen zero placeholders were rejected.

DDXConv's output is a StandardPayload only when all of these hold:
- `DecodeDiagnostics.IsLossless` (DDX DecodeDiagnostics.cs:47-48), `TruncatedReads = 0` and `PaddedBytes = 0`;
- the DDX header's format byte is a block-compressed code DDXConv maps exactly (0x52, 0x53, 0x54, 0x71, 0x7B); 0x43 and 0x86 fail the gate and keep their Original bytes (§5.4);
- the output's width and height equal the descriptor's, and its mip count equals the descriptor's declared count. A 3XDR entry declaring mips therefore fails the gate (its dropped levels are stored data), and the reader trims fabricated all-zero levels beyond the declared count before the check, recording the trim.

The last check exists because three paths record nothing today: `Convert3Xdr`'s drop, the full-atlas fallback that changes dimensions (DDX DdxChunkProcessor.cs:975-983) and the mip-tail fill (DDX DdxMipAtlasUnpacker.cs:442-460). BMT adds a dropped-trailing-bytes counter to `Convert3Xdr` and counters to the other two in cut 1a (in the owner's DDXConv fork, MIT under Kran's notice). When the gate fails, the output is stored as a Derivation (`ddx-recovery`, with the counters and the reason), never as a StandardPayload, and both writers report the display Degraded with the counters. A 3XDR entry with a declared mip chain is Degraded until DDXConv decodes its chain (cut 2 candidate: in the sample, 51 3XDR files dropped non-zero data; on the 40 whose dropped slice could be judged, it reads as a mip 1 on 21 and does not on 19, so the layout of the dropped region must be resolved per format before a lossless 3XDR decode exists).

**ScenePalette** (1c) keeps the original bytes, entry encoding (Vga6, Rgb8, x555, ...), expansion rule with provenance, transparency rule and auxiliary tables (HTBL ramps, cycled ranges). 6-bit VGA expands by bit replication `(v << 2) | (v >> 4)`, Assumed (63 maps to 255); BMT's Arena save reader uses `<< 2`; the difference is at most 3/255; RE-10 settles it.

### 5.2 GLB: PNG only (R12)

The GLB writer embeds PNG only, never MSFT_texture_dds or an original. The lowering makes the display PNG:

| Original | GLB image | Class |
|---|---|---|
| PNG | Byte for byte | Exact |
| Uncompressed DDS, TGA, 565/5551/4444, palettized | Lossless PNG; indexed PNG for palettized sources | Converted |
| BC1–BC3 (FNV: DXT1 19,415, DXT5 5,920, DXT3 1,476 of 26,840) | PNG from the pinned Shared S3tcDecoder (SH Media/Images/S3tcDecoder.cs), decoder id recorded | Converted relative to that decoder; checked against an independent decoder (hop B) |
| BC4 (ATI1, the Xbox specular companion) | Its value becomes the A channel of the normal map's PNG, so one image serves `normalTexture` and KHR_materials_specular's `specularTexture`, which reads A (SH SceneMaterial.cs:63) | Converted |
| BC5 (ATI2, the Xbox normal) | PNG with reconstructed Z | Approximated (Z quantized to 8 bits, ≤ 1/255) |
| Normal-map green convention | Flipped to glTF's +Y when the family is −Y (RE-13; Assumed until then) | Converted |
| DDX failing the gate | PNG of the recovery output | Degraded, with counters |
| Authored mip chain | Not stored; viewers generate mips | Approximated |
| Cube maps (FNV PC ships 6 DXT1) | Not embedded; named with SHA-256 in the fidelity row | Degraded in cut 1; cut 2 revisits |
| BC6H, BC7, SNORM, float | Cut 2 | Per row |

A new Shared BC4/BC5 decoder lands in cut 1a. The legacy RGBA8 DDS mip path (SH SceneGltfBuilder.cs:273-294) stays for legacy callers only.

### 5.3 Blender: exact packing

`bpy.data.images.load(original)` then `pack()`; never `images.new` plus `save()`, which re-encodes (NMT import_package.py:1120-1199).
- **Unchanged:** what OpenImageIO 2.4+ reads: DXT1/3/5, BC4/BC5 UNORM, BC7, BC6H (half), uncompressed channel-mask DDS, PNG, TGA. Exact, pending the byte-for-byte check (hop D).
- **DDX:** the StandardPayload DDS when the gate passed (Converted); otherwise the recovery output, Degraded with counters.
- **Xbox specular companion:** its original BC4 DDS, wired to the specular input. Exact.
- **Cube maps:** original packed, not wired in cut 1 (Degraded); cut 2 adds an approximation.
- **Palettized:** the reader's indexed PNG StandardPayload, Converted. Media.Blender never encodes an image.
- **Color space** follows the binding role (sRGB for color, Non-Color for data), not NMT's blanket sRGB (import_package.py:1133). One Blender image per document image.

### 5.4 Known texture gaps

Twenty-three DDX entries in the Xbox New Vegas Textures BSAs use format byte 0x43 or 0x86: 13 in the July prototype (8 and 5) and 10 in the Final (5 and 5), measured by a header census of every entry on 2026-09-23. DDXConv maps 0x86 to DXT1 and has no entry for 0x43 (DDX TextureUtilities.cs:51-60). Masking with 0x3F gives Xenos base formats k_1_5_5_5 and k_8_8_8_8 (recalled), which supports reading both as misclassified uncompressed formats (inferred), tracked as RE-12. These files keep their Original bytes and report their display Degraded.

---

## 6 Fidelity

### 6.1 Classes and bounds

Each writer's plan gives every feature instance one class and a reason code. `mesh info` (with `pending` rows), `mesh fidelity` and the convert report show the same plan.

| Class | Meaning |
|---|---|
| Exact | Bit for bit, or a standard carrier with identical semantics. |
| Converted | A lossless change of representation (triangulation, relayout, unit root, curve form by exact math, invertible color transfer). Float32 rounding of a derived constant is allowed and stated (≤ 0.5 ulp). |
| Approximated | A change within the bound for its domain (table below), computed by admission and stated on the row. R13's "visually lossless". |
| Degraded | Above the bound, or no approximation exists. The exact state is kept (GLB metadata; typed state in the .blend) and always listed. |
| Metadata | Non-visual data carried only as extras or custom properties. |
| Dropped | Not carried. Only with a reason. Gates allow only `later-cut(1b\|1c\|2)` for elements a cut does not yet write. |

**Bounds** (proposed defaults, inferred to be visually lossless at normal viewing; hop F checks the color bound). A row above its bound becomes Degraded.

| Domain | Bound |
|---|---|
| Color, display-referred 8-bit | ≤ 1/255 per channel |
| Position | ≤ 0.5 mm at output scale |
| Rotation and normal direction | ≤ 0.1° |
| Alpha-test edge | ≤ 0.5/255 in alpha |
| Skin weights | every vertex's sum within 1/255 of 1 before normalization |
| Time | keys kept; resampled curves are bounded by the value bounds above |

**Consistency rules:**
- Normalizing non-unit normals: Approximated (direction exact; only shaders that do not renormalize see a difference, inferred).
- Renormalizing weights: Approximated within the bound, else Degraded.
- BC5 Z reconstruction: Approximated.
- Morphs absolute↔relative: Converted for integer sources below 2^24; Approximated for float32 sources (≤ 0.5 ulp per component).
- Units and scale nodes (1/70, 1/2.25, 1/36): Converted, float32-rounded.
- Vertex color: one rule for both writers (§6.3).
- **Lighting model:** one document-level row in both writers, not per material. Gamebryo's per-pixel lighting (Blinn-Phong specular with glossiness, an ambient term) has no exact metallic-roughness form. Diffuse, normal, specular and glow maps are carried exactly; glossiness maps to roughness by `sqrt(2/(g+2))` (recalled mapping). Degraded, named once.

### 6.2 Cut-1a feature table (static)

The 11 blend pairs measured in 20,542 loose FNV NIFs (field_features.json): SRC_ALPHA/INV_SRC_ALPHA 3,325; SRC_ALPHA/ONE 807; ZERO/SRC_COLOR 726; ONE/ONE 227; ONE/ZERO 12; SRC_COLOR/ONE 9; ZERO/INV_SRC_ALPHA 3; SRC_COLOR/SRC_COLOR 3; SRC_ALPHA/SRC_COLOR, SRC_COLOR/ZERO and ZERO/ZERO 1 each.

**Blends in GLB: the transmission route.** Every pair has the form `out = T·Cd + E`. KHR_materials_transmission (ratified) with transmission 1, roughness 0, metallic 0 and IOR 1.0 (zero Fresnel reflectance; IndexOfRefraction is already typed) transmits the background tinted by the base color and adds emission (recalled semantics). So T = baseColor × COLOR_0 and E = emissive texture × factor × strength. glTF's COLOR_0 multiplies base color but not emission, so:
- multiplicative pairs (T from Cs, E = 0) are exact;
- additive and mixed pairs (E from Cs or αCs) are exact when the vertex color and alpha are uniform per primitive (folded into the emissive factor) and the source term is unlit (NoLighting shader);
- otherwise (per-vertex color or alpha in E, or a lit E) the pair is Degraded, and the drawn fallback is the existing brightness-to-alpha bake (SH SceneAdditiveInterchange.cs).

**Blends in Blender: a display-space fit.** Blender composites a Transparent BSDF plus an Emission as `E + T·Cd` in scene-linear light (hop F, 2026-09-25: 330 of 330 cells to 0.000/255 once the texel is modeled) and cannot read the destination; render-state blends are display-encoded framebuffer equations (inferred for the Gamebryo PC source: a D3D9 8-bit UNORM target without sRGB write, the source saturated; not established for the Xbox 360 or PS3 framebuffers) and are drawn by a display-space fit (display-blend implementation, 2026-09-27). The writer classifies the compiled terms (exact, over, additive, multiplicative, the two tabulated "other" pairs, or untabulated: the set-B or set-A candidate fit with the smallest grid-measured bound), carries the fit in `renderState.blend.display`, and reports its display error bound; the constants are set B of `display_blend_constants.json` (Transparent colors above 1 through Value nodes, measured unclamped in Cycles and EEVEE). A lit surface draws a lit curve blind to the lit color: tabulated for over, SRC_ALPHA/ONE and ONE/ONE, exact for replace, and for every other lit-scaled equation the generic linear curve (the equation's own source scale and source-alpha transmission raised to the exponent from 0.5 to 3, in hundredths, with the smallest grid-measured lit bound, 1 winning ties) with that grid-measured lit bound; only an equation whose terms do not factor into a lit route draws the unlit color and reports the lost lighting. Owner decision D1 (the additive trade-off) is taken as set B provisionally; set K is a data edit plus the re-pins `BlendDisplayConstants` lists.

Viewers without transmission draw such materials as opaque base color. Transmission is the default (R22): Blender 5.1's glTF importer keeps it (Transmission Weight and IOR, measured 2026-09-23), and hop F's readback confirms it at the gate; if a gate ever shows it dropped, the default flips to the brightness-to-alpha bake and the question reopens. The field probe counts additive materials with non-uniform vertex color or lit shaders (§7.1).

| Feature | GLB | Blender |
|---|---|---|
| NIF triangles | Exact | Exact |
| n-gons, point welding (1c) | Triangulated, Converted; face structure only in the .blend and `mesh dump` | Welded n-gons, Exact |
| UV sets | TEXCOORD_n, Exact | UV maps, Exact up to 8; more Degraded |
| Vertex colors | §6.3 | §6.3 |
| Vertex-color source: AmbientDiffuse / Ignore / Emissive | COLOR_0 / omitted, Exact / Degraded (no per-vertex emission in glTF) | Exact / Exact / attribute into emission, Exact |
| Authored normals | As stored, Exact; non-unit normalized, Approximated | §6.4 |
| Node transforms | TRS Converted; reflection as negative scale, Converted; shear Degraded, matrix in extras | TRS; reflection Converted; shear through `matrix_parent_inverse`, Exact (inferred) |
| Skin | JOINTS_n/WEIGHTS_n, more than 4 influences, Exact; normalization §6.1 | Vertex groups, Exact; non-rigid bind §6.4 |
| Morph targets (static) | Relative; absolute converted per §6.1 | Absolute shape keys; relative converted per §6.1 |
| SRC_ALPHA/INV_SRC_ALPHA; ONE/ZERO | BLEND; OPAQUE, Exact | Display fit: over Degraded, bound 14.1854/255 (lit route 36.4189/255); ONE/ZERO Exact |
| Multiplicative (ZERO/SRC_COLOR, SRC_COLOR/ZERO, ZERO/ZERO, ZERO/INV_SRC_ALPHA) | Transmission route, Exact; ZERO/INV_SRC_ALPHA with vertex alpha is Approximated or Degraded per bound | Display fit: ZERO/SRC_COLOR and ZERO/INV_SRC_ALPHA Degraded, bound 4.1849/255; SRC_COLOR/ZERO and ZERO/ZERO Exact |
| Additive and mixed (SRC_ALPHA/ONE, ONE/ONE, SRC_COLOR/ONE, SRC_COLOR/SRC_COLOR, SRC_ALPHA/SRC_COLOR) | Transmission route under the conditions above, Exact; else Degraded with the bake | Display fit, Degraded: additive 13.1153/255 (lit SRC_ALPHA/ONE 45.5909, lit ONE/ONE 43.637; D1 provisional), SRC_COLOR/SRC_COLOR 15.9039, SRC_ALPHA/SRC_COLOR 16.1405 |
| T > 1, destination alpha, Subtract, Min, Max (PS2, later) | Degraded | Affine (Subtract included): display fit, Approximated when its bound is within 1/255, else Degraded (untabulated equations grid-measured); destination alpha, Min, Max Degraded |
| Alpha test GREATER T (all 8,132 alpha-tested FNV files use function 4) | MASK cutoff (T+0.5)/255: Converted for unfiltered 8-bit alpha; Approximated when filtered (edge ≤ 0.5/255) | Exact |
| Alpha test LESS, LESSEQUAL | Inverted display alpha plus MASK, Converted | Exact |
| Blend and test both enabled | BLEND with a derived alpha that zeroes texels ≤ T: Converted when alpha comes only from the texture; per bound when vertex alpha contributes | Exact |
| Depth bias, no depth write (decals); draw order | Geometric offset along normals (≤ 1 mm at output scale) plus order, Approximated; depth test off on non-decal geometry Degraded | Object sort plus the same offset, Approximated |
| Stencil draw mode Both / Clockwise | doubleSided / winding reversed, Converted | Backface culling off / reversed, Converted |
| Stencil test functions | Degraded | Degraded |
| Clamp and wrap | Sampler, Exact | Per-axis sampler group, Exact |
| UV offset and scale | KHR_texture_transform, Exact | Mapping node, Exact |
| Slot 0 diffuse; tint | Base color and factor, Exact | Exact |
| Slot 1 normal (green per RE-13) | normalTexture, Converted | Normal Map node with green handled in nodes, Exact |
| Normal-map alpha as specular (PC) | Same PNG as specularTexture (reads A), Exact | Specular from alpha, Exact |
| Xbox specular companion | §5.2, Converted | Original wired, Exact |
| Slot 2 glow; emissive color and strength | emissiveTexture × factor × KHR_materials_emissive_strength, Exact (`EmissiveStrength` exists, SceneMaterial.cs:82) | Emission, Exact |
| Unlit with emission | Emission folded into base color, Approximated; Degraded when the sum clips | Exact |
| Slot 3 parallax | Degraded (no carrier) | Degraded (image packed, unwired) |
| Slots 4–5 environment cube and mask | Degraded (image-based lighting belongs to the viewer) | Degraded in cut 1 (packed, unwired); cut 2 approximates |
| Glossiness, specular color | Lighting-model row; specular color to KHR_materials_specular specularColorFactor | Same |
| View-angle falloff | Degraded | Layer Weight/Facing node, Exact |
| Detail, dark, decal layers on the base UV set | Baked into a derived base image (SceneImageDerivation), Approximated (≤ 1/255) | Mix chains, Exact |
| The same layers on another UV set or transform | Degraded | Exact |
| Billboard modes 0–5, 9 | `multitoolBillboard`; static in generic viewers, Degraded | Constraints (LOCKED_TRACK, TRACK_TO, COPY_ROTATION), Approximated |
| Billboard mode 8 (4 files) | Degraded, raw mode in extras | Degraded, raw mode in `mt_native` (RE-14) |
| Runtime layers | Default-on written; others listed, or extra scenes with `--layers all` (Metadata) | Collections, hidden unless default-on, Exact |
| Units and basis | Root, Converted | Root empty, Converted |
| Source identity | Relative path plus SHA-256 | `mt_native`, `mt_name` |
| Particles, Havok | Not drawn, Degraded; native state kept | Same |
| Lighting model | Document row, Degraded | Same |

Sky and presentation offsets are defined in world space, so a scaled root cannot misplace the sky (the existing rule adds "the camera's full world translation", SH Core/Models/SceneNodePresentation.cs:4). A scaled-root test covers it.

### 6.3 Vertex color: one rule for both writers

Today no layer declares the space: `SceneColorEncoding` says "no color-space conversion is performed" (SH Core/Models/SceneColorEncoding.cs:3), glTF COLOR_0 is linear, and Gamebryo multiplies in gamma space (inferred; RE-11). Each primary color stream now declares `ColorSpace` with provenance; NIF is Srgb (Assumed).

- **Storage.** GLB: COLOR_0 as float = sRGB EOTF(source), Converted (invertible up to float32 rounding) for 8-bit and float sources alike. Blender keeps the source values: `BYTE_COLOR` for 8-bit, `FLOAT_COLOR` holding the raw float values, with `mt_color_space`: Exact.
- **Shading**, a separate row. Under a pure power law, multiplying in linear space equals multiplying in gamma space; the residual is the sRGB curve's difference from a power law. GLB: Approximated with the bound computed per file over its texture and vertex-color values (`pending decode` in info); Degraded above 1/255. Blender: the node graph multiplies in the declared space through an sRGB-curve node group (inferred feasible): Exact, pending hop F.
- **Check:** an asymmetric color ramp; its control omits the gamma-space step and must fail.

### 6.4 Blender limits

- **Custom normals.** Blender 4.5+ stores a float3 `custom_normal` (recalled), so with the 5.0 minimum (R24) custom normals are Exact and the quantized branch is dropped; an exact `FLOAT_VECTOR` corner attribute `mt_source_normal` is still written so hop D compares byte for byte.
- **Non-rigid binds.** A bone rest is head, tail and roll, so a scaled or sheared bind cannot be stored; NMT rejects them today (NMT BlenderExporter/import_package.py:2745-2754). Exact inverse binds go in `mt_bind`; a helper empty carries the compensating scale, Approximated within the position bound, else Degraded.
- **DDS orientation.** Unverified whether a packed DDS loads with a PNG's vertical orientation; the owner-run probe checks an asymmetric DDS, and the writer flips V only for that image class if needed.
- **Names.** Blender truncates long names and adds `.001`; the source name goes in `mt_name` on every datablock.
- **Actions.** Blender 4.4+ uses slotted actions (recalled); with the 5.0 minimum (R24) the script uses the slotted API only.

All of these are rows in `BlendAdmission`, a pure plan in Media.Blender that never launches Blender, covered by synthetic row tests.

---

## 7 Verification

The owner runs everything in this section, or an orchestrator the owner has approved does. Agents never build, test or launch Blender (R19). Runs are serial, one game per stratum, admitted at the 4 GiB gate with the watchdog. Real-asset tests carry the Bucket-B guard and `SequentialIntegrationGroup`.

### 7.1 Sampling

**Rule** (R8, strengthened):
- Per version key (header string, file version, user version, BS version, endianness), the minimal **joint** cover of block types and field-level values: the exact (MILP) cover where it solves, else greedy with reverse-delete pruning (ties: most new items, fewest bytes, SHA-256 order). Plus one file per (game, platform) sharing the key. Checked in as a manifest of source, entry and SHA-256.
- Values come from an **independent** hand-written Python probe that reads offsets directly, never through NifSchema or the reader under test. It tags: blend pairs; alpha-test function and threshold, and blend-plus-test; clamp modes; UV transforms; texture-set slots in use; normal-map alpha use; Xbox specular companions; billboard modes; controller and interpolator types and their targets; Emit Mult ≠ 1; falloff; vertex-color source and lighting modes; z-buffer modes; stencil draw mode and test; skin and partitions; switch and LOD nodes; the hidden flag; non-orthonormal or reflected node rotations; additive materials with non-uniform vertex color or a lit shader.

**Measured so far (read-only):**
- NIF census: 683,036 records (573,127 unique) in 112 archives and trees, 30 keys. Block-type covers: 302 files (MILP), 319 (greedy plus pruning), 322 (greedy). Endianness is the only field that splits a key.
- Loose FNV bs34 LE (20,542 NIFs): 26 distinct values across blend pairs, test functions, billboard modes and clamps. Block-type covers reach only 9–10 of them; greedy adds 13 files; a joint cover of 150 block types plus the 26 values needs 43 files (`feature_recover.py`).
- **Only those 26 values are measured.** The other tagged features are not yet counted, so every size below is a lower bound; the probe is extended and the measured sizes are published before any gate is fixed.
- Billboard mode 8: 4 files (RE-14).
- `.kf`: 4,765 files in the FNV loose tree across 12 keys (BS 14, 21, 24–28, 30–34); block-type cover 20 files; B-spline interpolators dominate (`kf_cover.py`). Five more are key 20.0.0.4, which the probe's 20.2.0.7 walker cannot parse: `characters/_male/idleanims/eatidle.kf`, `pistolvariant01.kf`, `talk_handsatside_moving.kf`, `talk_handsatside_moving2.kf`, `talk_handsatside_still2.kf` (`kf_errors.py`).
- Key 20.0.0.4 / uv11 / bs11 also ships in FNV (2), FNV X360 (5), FO3 (1) and FNV PS3 (1) (cover_full.txt), for example `triggers/collisionboxstatic.nif`. Cut 1 does not read it.

**Cut-1a sample:**

| Key or kind | Files | Basis |
|---|---|---|
| 20.2.0.7/11/34 LE (58,404: FNV 41,232, FO3 17,144, FNV X360 21, PS3 5, Skyrim LE 2) | ≥ 43 | Joint cover (measured on the loose tree), plus the FO3, X360, PS3 and Skyrim LE floor |
| 20.2.0.7/11/34 BE (108,761: FNV X360 93,936, PS3 14,825) | ≥ 33 | Exact block cover 33, plus field values from the BE probe (not yet measured) |
| bs14 (72), bs21 (162), bs26 (9) | ≈ 12 | 2 + 2 + 1, plus the FNV/FO3/X360/PS3 floor |
| bs32 LE (4 Skyrim LE files) | 1 | Exact cover |
| Decline controls | 3 | `triggers/collisionboxstatic.nif` (20.0.0.4/11/bs11; FNV, FO3, X360, PS3): Unsupported, "20.0.0.4 key: cut 2". `marker_radius.nif` (3.3.0.13) and `marker_map.nif` (4.2.1.0), Oblivion files with 3 and 9 block types: Unsupported, "other version" |
| Textures | where missing | Every DDS FourCC (DXT1/3/5, A8R8G8B8, R8G8B8, A4R4G4B4); every DDX format byte (0x52, 0x53, 0x54, 0x71, 0x7B, 0x43, 0x86); one DDX known to pad or truncate (the gate's control); one X360 normal map with a specular companion; one FNV cube map |

Cut 1b adds the `.kf` cover (20 FNV loose, plus FO3 and X360 once censused), `eatidle.kf` as a 20.0.0.4 decline control, and embedded-controller files per the Appendix B table. Cut 1c adds 10 XnGine files (Daggerfall 44005 v2.7, 451 v2.6, 41509 v2.5; Redguard CRAK0001.3D and ISLAND.ROB GR_COMP; Battlespire ARMOR.3D, one 3D.BS6 entry, one v2.6 3D.BSA entry; BMANA001.3DC wide and one narrow `.3DC`) plus DOGA001.3DC as the unit control. Non-NIF formats get an animated, a static and a billboarded file where they exist; billboards that exist only as placed flats go in the cut-2 scene sample.

### 7.2 Oracles, each with a control that must fail

A control perturbs the hop's output or source in a way drawn from the *affected* population; the oracle must report a mismatch, and the receipt records that it did. An oracle whose control passes is invalid.

| Hop | Oracle | Shared premise | Control that must fail |
|---|---|---|---|
| A1 Source → document, fields | Python probe values versus typed fields, per tagged file | — | ONE/ONE rewritten to SRC_ALPHA/INV_SRC_ALPHA in `dlc05spacewindowcover01.nif`, expectation kept |
| A2 Coverage | The header block table: every block in exactly one of Typed, NativeOnly, Dropped; Dropped only with later-cut reasons | — | One block removed from the reader's enumeration |
| A3 Geometry | BMT's RenderableSubmesh extractor run with the idle pose disabled (may need a new internal option; inferred); its per-submesh world matrix inverted with a stated float tolerance; skinned submeshes compared in bind pose or excluded; degenerate strip triangles dropped on both sides | Shares NifParser: catches reader errors, not parser errors | One vertex moved by 10× the tolerance |
| A4 Texture bytes | Original SHA-256 versus bytes from a Python BSA reader | — | One texture byte changed |
| A5 DDX gate | For mip 0, the multiset of endian-swapped decompressed blocks equals the multiset of output blocks | LZX decompression is DDXConv's unless a Python XMemDecompress exists; the untile check is independent | One output block duplicated |
| A6 XnGine (1c) | A Python `.3D` walker (points, planes, raw UV deltas), extending `xng3d_bounds.py` | — | One UV delta byte flipped |
| A7 `.3DC` (1c) | Integer poses rebuilt exactly (`rg3dc_bounds.py` reader) | — | One narrow delta bit flipped |
| B Document → GLB | Khronos validator, 0 errors (Shared pin); an independent Python GLB reader compares accessors bit for bit after the declared conversions; the display PNG against an independent decoder, Pillow 12.1.0's DdsImagePlugin (its source handles DXT1/3/5, BC4, BC5/ATI2, BC6, BC7; decoding not yet run), equal or within 1/255 with differences recorded; every admission row's declared value present | — | A block with swapped endpoints; a MASK cutoff written as T/255; a one-texel UV shift on a two-axis gradient |
| B' Blender imports the GLB (owner-run, regression guard) | Each sampled GLB imports in 12 fresh Blender processes | — | A GLB with a VEC4 and a VEC3 custom attribute must fail in at least one of 12 (NMT measured 5 of 12; zero failures by chance ≈ 0.15%) |
| C Document → package | A Python package reader compares every stream with `mesh dump` | — | One face index flipped |
| D Package → .blend (owner-run) | A readback script dumps loops, n-gon sizes, UV maps, color attributes, absolute shape keys, F-curves, `mt_*` properties, packed image bytes and SHA-256; exact except custom normals (declared tolerance) | — | One shape-key vertex moved in the package; a DDS repacked through `save()` |
| E Writer versus writer | Positions, indices, UVs equal after each writer's declared conversions; packed bytes equal the original or StandardPayload; the GLB PNG equals the pinned decode | Consistency only | One UV swapped in the package only |
| F Render probe (owner-run, Blender 5.1, Cycles and EEVEE on every run) | The 11 FNV blend pairs over a known background, the design's worst-case sources included (every non-exact bound exercised within 2/255): within 1/255 of the display fit the package declares, and within the row's bound + 1/255 of Gamebryo's display-encoded result; the importer's lit route against lit twins (over, SRC_ALPHA/ONE, ONE/ONE and two generic linear curves, at exponents 1 and 2.07): within 1/255 of m(As) L_twin + tau(As) Cd and within the lit bound + 1/255 of Gamebryo on the lit display color; the transmission route in Khronos Sample Viewer or three.js and in Blender's glTF importer, where `tools/blender/readback.py` asserts Transmission Weight 1, IOR 1.0, roughness 0, metallic 0 and the base-color and emission bindings (R22); the asymmetric color ramp; the DDS orientation probe | — | E and T swapped (both blend checks); the display fit's nodes planted at (0, 1) (C2); the lit exponent planted at 1 on the power and generic curves (C4); a copy of the GLB with KHR_materials_transmission stripped must read back opaque (weight 0); the gamma-space step omitted (ramp); a deliberately flipped image (orientation) |
| Basis and units | Synthetic export per basis (Z-up NIF, Y-up XnGine) | — | The basis rotation omitted |

**Default test suite, synthetic only:** one test per admission row in each writer; `ValidateStructure` and the two-form agreement; `AbsolutePositions` rejected by the legacy `Validate` and ScenePoseEvaluator; the planner (skip, `--overwrite`, report naming, collision, race classification, memory gate); architecture tests; basis tests; curve math against closed forms.

An old writer retires only after sampled parity passes and the fidelity report shows no regression against it.

---

## 8 Cuts

### Cut 1 (1a): one static vertical slice, FNV/FO3 NIF to both writers through `mesh info` and `mesh convert`

**Shared** (foundation; Media.Blender drafted by bethesda under a claim):
1. The R1 rename, in one commit.
2. Cut-1a typed additions and `ValidateStructure`; `AbsolutePositions` fail-closed in `Validate` and ScenePoseEvaluator; the portable-summary function (including IOR and specular).
3. Sources and Export contracts (`IModelWriter` with Preflight, Plan, Write); the planner with skip, report naming, collision rules and the memory gate; the operations and formatters.
4. `Media.Processing`: Processes moved with their notices; job object assigned after start; deadline; watchdog; ExternalToolLocator with executable checks and version reads.
5. GLB: admission, lowering, carriers, the builder's model-writer profile, the transmission route, specular and companion binding, the BC4/BC5 decoder.
6. `Media.Blender`: admission, Preflight, package v2, `import_model.py` for static features, `tools/blender/readback.py`.
7. Architecture tests and the synthetic suite.

**BMT** (bethesda):
8. The NIF reader for static features: bs14, 21, 26, 32, 34; LE and BE; `.nif`; the TRS rule, the DDX gate and the companion rule.
9. DDXConv counters: a dropped-trailing-bytes counter in `Convert3Xdr`, and counters on the full-atlas fallback and the mip-tail fill (in the owner's fork; MIT under Kran's notice).
10. Unit rows with provenance (GameProfiles).
11. The `mesh` shell: `info`, `convert`, `dump`, `fidelity`, `validate`, `package`, with BMT read-context options.
12. The Python probe extended to every §7.1 feature; the measured sample and cover manifest.
13. Synthetic and Bucket-B tests.

**Unchanged:** `export nif`, `classic mesh export`, existing SceneGltfBuilder callers, NMT's and AWE's `mesh`.

**Gate 1a:** hops A1–A5, B, B', C, D, E, F and basis pass on the 1a sample; every control fails; Dropped only with later-cut reasons; legacy outputs byte-identical; peak memory recorded per item.

### Cut 1b: animation
NIF controllers, sequences and `.kf` (BS 14, 21, 24–28, 30–34); NifKeyGroupReader keeps scalar tangents and TBC; the animation additions (timing, clock, events, curves, property tracks); curve conversion and resampling within bounds; KHR_animation_pointer and KHR_node_visibility; Blender F-curves, NLA and markers with slotted actions; the Appendix B controller table; the `.kf` sample. Gate 1b has the same shape plus the 170° rotation control.

### Cut 1c: XnGine `.3D` and Redguard `.3DC`
The two readers; ScenePalette; the §4.1 unit rows (numbers set in this design); oracles A6 and A7; the DOGA001 unit control. Gate 1c has the same shape.

### Later cuts (details in Appendix C)

- **Cut 2:** 3D `scene`; cameras, lights and markers; the remaining NIF keys (including 20.0.0.4, which holds five FNV idle `.kf` files and `collisionboxstatic.nif`); Starfield `.mesh`; BC6H/BC7/float textures; Shadowkey; the WinUI external-tools section; `mesh census`; the D3D12 viewer's renderer admission.
- **Cut 3:** BMT actors (R17) with `--anim` as a clip and the FaceGen face as one morph with normal deltas; SpeedTree; the Shared Granny reader; NMT convergence, including the `mt_*` migration of its tests and docs.
- **Cut 4:** 2D `scene` from MapDocument; AWE convergence; ESM cells as 3D scenes; Van Buren, Oblivion PSP, BOS.
- **Later:** a chunked Blender worker, bounded `--jobs`, `--apply-scale`, render-state previews in the viewer, console texture consolidation (DDXConv stays MIT), palette node graphs, the full FaceGen basis option.

---

## 9 File ownership and order of work

Ownership follows the mailbox table: the foundation owns Shared and AWE, bethesda owns BMT, and the NMT session owns NMT (relayed by the owner). Bethesda drafts a Shared file only under an explicit file-level claim the foundation has agreed in the mailbox. The whole mailbox document is published each time, per the publish rule.

| # | Files (new or changed) | Owner | Cut |
|---|---|---|---|
| 1 | Mailbox: re-read, refresh, publish this design and the claims in rows 6, 8 and 11 | bethesda | 1a |
| 2 | Rename: SH Core/Models/SceneDocument.cs → ModelDocument.cs, plus the other 37 src files (Core 12, Media 11 with 2 READMEs, WinUI.Direct3D12 14), 78 tests, 27 tools (26 ControlGallery, 1 validation), 7 docs | foundation | 1a |
| 3 | New in SH Core/Models: SceneUnits, SceneSourceBasis, SceneValueProvenance, SceneNativeState, SceneElementRef, SceneSourceLocation, SceneImageSource, SceneTextureDescriptor, SceneImageDerivation, SceneRenderState, SceneBlendEquation, SceneBlendTerm, SceneAlphaTest, SceneDepthState, SceneStencilState, SceneDrawOrder, SceneVertexColorUse, SceneViewAngleOpacity, SceneTextureLayer, SceneFaceList, SceneAttributeStream, ScenePrimitivePurpose, SceneNormalProvenance, SceneNodeRole, SceneBillboard, SceneBillboardAim, SceneLayerSet.<br>Changed: ModelDocument, ScenePrimitive, SceneImage, SceneMaterial, SceneNode, SceneNodePresentation (derivation), SceneSkin, SceneMorphTarget (AbsolutePositions), SceneValidation (+ValidateStructure; rejects AbsolutePositions), ScenePoseEvaluator (rejects AbsolutePositions), SceneJsonContext | foundation (bethesda may draft under a claim) | 1a |
| 4 | New SH Core/Models/Sources: IModelSourceReader, ModelSourceRegistry, ModelSourceItem, ModelSourceCandidate, ModelProbeResult, ModelReadContext, ModelReadResult, ModelSourceCoverage.<br>New SH Core/Models/Export: IModelWriter, ModelWriterEnvironment, ModelWriterRegistry, ModelWritePlan, ModelFidelityRow, ModelFidelityOutcome, ModelFidelityBounds, ModelFidelityReport, ModelOutputPlanner, ModelOutputPlan, ModelItemOutcome, ModelConvertOptions, ModelInfoOperation, ModelConvertOperation, ModelInfoTextFormatter, ModelInfoJsonFormatter, ModelMemoryGate, ModelCommitClassifier, ModelReportPath.<br>New SH Core/Settings/ExternalToolSettings | foundation | 1a |
| 5 | New project SH Slfx77.Multitool.Media.Processing: Processes/* moved from Media with LICENSE.NMT.md and NOTICE.md (namespaces kept); WindowsJobObject, ProcessMemoryWatchdog, Tools/ExternalToolLocator, ExternalToolVersionProbe. MediaProcessRunner gains a deadline and optional job assignment; its bounded drain is kept. Media.csproj references it. | foundation | 1a |
| 6 | New project SH Slfx77.Multitool.Media.Blender: ModelBlendWriter, BlendAdmission, BlenderPackageWriter, BlenderPackageManifest*, BlenderRun, Python/import_model.py, NOTICE.md; SHT tools/blender/readback.py | bethesda drafts under claim; foundation reviews and integrates | 1a |
| 7 | SH Media/Models: ModelGlbWriter, ModelGltfAdmission, ModelGltfLowering, ModelGltfCarriers (new); SceneGltfBuilder and SceneGltfAnimationBuilder gain the internal model-writer profile. SH Media/Images: Bc4Bc5Decoder (new) | foundation | 1a |
| 8 | Shared tests (architecture, admission rows, planner, classifier, memory gate, basis, fail-closed morphs); SHD model-document.md (new; supersedes the neutral-scenes.md scope), owned-source-licensing.md (Media.Blender 0BSD), dependency-licenses.md (Blender as an unbundled external tool), program.md row | foundation (bethesda drafts the docs under a claim) | 1a |
| 9 | BMT: Shared pin bump plus the rename (20 code files, 8 docs) | bethesda | 1a |
| 10 | BMT new: Core/Modeling/Nif/{NifModelReader, NifModelGeometryReader, NifModelMaterialReader, NifModelSkinReader, NifModelLayerReader, NifModelTextureSource, NifModelCoverage}.cs; Core/Modeling/BethesdaModelRegistration.cs.<br>Changed: Core/Games/GameProfiles.cs (unit provenance); src/DDXConv/DDXConv/{DecodeDiagnostics, DdxParser, DdxChunkProcessor, DdxMipAtlasUnpacker}.cs (three counters; Kran's MIT notice kept) | bethesda | 1a |
| 11 | BMT CLI/Commands/Mesh/MeshCommand.cs; Program.cs registration; tools/scripts/nif_feature_probe.py (oracle only); tests/…/Modeling/* (synthetic) and tests/…/Modeling/RealAsset/* (Bucket B, sequential); the cover manifest; main-repo docs/backlog RE items | bethesda | 1a |
| 12 | Owner runs §7 and gate 1a | owner | 1a |
| 13 | Shared animation additions (SceneAnimationTiming, SceneClipClock, SceneClipEvent, SceneInterpolation kinds and track parameters, SceneNodeConstraint, ScenePropertyTrack) and both writers' animation paths; BMT NifModelAnimationReader and the NifKeyGroupReader extension; gate 1b | foundation; bethesda (Media.Blender under claim; BMT files) | 1b |
| 14 | ScenePalette (Shared); BMT Core/Modeling/Xngine/XnGineModelReader.cs, Core/Modeling/Redguard/Redguard3DcModelReader.cs, Core/Modeling/Units/ClassicModelUnits.cs; gate 1c | foundation; bethesda | 1c |
| 15 | Cut-2 Shared additions (cameras, lights, markers, SceneRendererAdmission), scene operations, SH WinUI/Settings/ExternalToolsSettings; BMT scene readers, remaining NIF keys, Shadowkey, `mesh census` | foundation; bethesda | 2 |
| 16 | BMT actor composer; NMT convergence | bethesda; NMT session | 3 |
| 17 | 2D scene writer; AWE convergence | foundation | 4 |

**Order and parallelism:**
1. Row 1.
2. Row 2 at the foundation's chosen quiet point. The freeze on Core/Models, Media/Models and WinUI.Direct3D12/Scenes lasts only for that commit.
3. Rows 3–4. Row 3 blocks rows 6, 7 and 10.
4. Rows 5, 6 and 7 in parallel; no foundation file is in flight in the new projects.
5. Row 8.
6. Rows 9–11: BMT adopts at a deliberate pin bump, admitted at the 4 GiB gate.
7. Row 12, then row 13 (gate 1b), then row 14 (gate 1c).

Until row 9, BMT's legacy routes stay exactly as they are.

---

## 10 Remaining questions for the owner

All four were answered by the owner on 2026-09-23 (round 4), and the answers are R21-R24 in §1.1:
1. **External tool paths** live once per machine, shared by every Multitool app (Option A).
2. **GLB blends** default to KHR_materials_transmission, on the condition that Blender's importer retains it; measured true on Blender 5.1 (§6.2, R22), and hop F re-checks it at the gate.
3. **BC5 texture line** keeps `RG`, the stored channels; the line must be accurate to the actual texture.
4. **Blender minimum** is 5.0/5.1; no 4.2 LTS install, and the legacy action API goes.

(A fifth question about DDXConv's license was answered by the owner on 2026-09-23: DDXConv is Kran's project under MIT, "Copyright (c) 2026 Kran <kran@kran.gg>"; the owner's commits to their fork confer no right to relicense it. DDXConv stays a submodule pointing at the owner's fork, keeps MIT with Kran's notice, is listed in THIRD_PARTY_LICENSES, and is outside the 0BSD move.)

---

## Appendix A: review resolutions

### A.1 Revision 1 (first adversarial review, items 0–24)

| Item | Resolution | § |
|---|---|---|
| 0 Cut 1 copied BMT's lossy GlbScene | Block-level NIF source reader; RenderableSubmesh only as the A3 cross-check | 2.3, 7.2 |
| 1 Assembly cycle; SharpGLTF reachable from Blender | Contracts in Core; Media.Processing; architecture tests | 2.5 |
| 2–4 Units and `.3DC` timing | Measured best guesses with provenance and RE items | 4 |
| 5–6 PNG forced into the document; MSFT_texture_dds slot | Source bytes in the document; GLB never writes DDS | 5 |
| 7 Vertex-color space | Declared per stream; one rule (revised again in revision 2) | 6.3 |
| 8 Unlisted Blender losses | BlendAdmission rows | 6.4 |
| 9 Morph storage | AbsolutePositions (revised in revision 2) | 2.2 |
| 10–12 Falloff/tint/emissive; alpha test; animation pointer | Typed; (T+0.5)/255; KHR_animation_pointer never required | 2.2, 6 |
| 13 XnGine UVs, normals, basis | Raw UV stream, authored normals, reader flip | 2.3 |
| 14–15 Skip gaps; memory | Classification table; gates and job object (revised in revision 2) | 3.4, 3.5 |
| 16–17 Verification could not fail; sample keys | Independent probes, controls, measured re-cover | 7 |
| 18–19 Bundled Blender; command collisions | No bundled step; subcommands and aliases | 3.9, 3.10 |
| 20–24 Nits | Folded in | — |

### A.2 Revision 2 (rulings review and code review)

| Point | Resolution | § |
|---|---|---|
| Blocker: underscore custom attributes in the GLB | None written; B' regression guard with a failing control | 2.4, 7.2 |
| Blocker: DDX output classed lossless | Measured 2026-09-23 on every entry of the three distinct Xbox NV Textures BSAs: complete block-format 3XDO entries are exact relayouts (owner's claim holds for them); 3XDR mip drops, 23 wrong-format strips and fabricated zero levels are the real losses, all caught by the gate; heuristic routes serve only damaged or carved data (the reviewer described the code, not the complete entries) | 5.1, 7.2 |
| Blocker: in-flight foundation work | Facts refreshed at bf2127d; IOR and Granny quadratic integrated and folded into the summary function; freeze limited to the rename commit | 2.6, 9 |
| Blocker: cut 1 not one slice | Cut 1a static slice; 1b animation; 1c XnGine; settings UI and census to cut 2 | 8 |
| GLB carries exact-source payloads | GLB holds what a viewer draws plus small metadata | 2.4 |
| No lowering step before the strict builder | ModelGltfLowering and ModelGltfCarriers | 2.4 |
| Absolute morphs reused PositionDeltas | AbsolutePositions, fail-closed | 2.2 |
| `.3DC` unit evidence was the int16 ceiling | Separate row, Assumed (human actors only), Degraded row, animal control | 4.1 |
| Xbox specular companion dropped | Reader resolves and binds it | 2.3, 5.2 |
| Missing FNV material rows | Slots 0–5, glossiness, vertex-color source, stencil, blend plus test | 6.2 |
| Controllers unclassified | Controller table with counts | App. B |
| Basis conversion unspecified | One root rule per writer, tests | 4 |
| 3×3 rotation to TRS | Decomposition rule; shear kept as a matrix | 2.3, 6.2 |
| Quaternion interpolation | Rotation rows; 170° control; RE-17 | App. B |
| Q2 reopened R13; transmission ignored | Transmission route; Q2 narrowed to viewer support | 6.2, 10 |
| No threshold for Approximated | Per-domain bounds; decal, same-UV bake and Reverse rows; skeleton-less `.kf` Degraded | 6.1, 6.2, App. B |
| Class contradictions | Consistency rules | 6.1, 6.3 |
| `mesh info` launched Blender and decoded images | PE version read; `pending decode` rows | 3.2 |
| `--report` blocked reruns | Timestamped report name | 3.5 |
| Oracles without controls; self-checked BC4/BC5 | A control per oracle; Pillow as the independent decoder | 7.2 |
| Memory gate below R19 | max(4 GiB, estimate + 2 GiB); 2 GiB watchdog; fixed per-format estimate | 3.4 |
| Job object through Process.Start; drain claim wrong | Assign after start plus DIE_ON_UNHANDLED_EXCEPTION; existing drain kept | 3.4 |
| Rename counts and tools/ | Recounted; tools in R1; AWE from the live checkout (299c2bf) | 2.6, 9 |
| Sample drift (bs32, joint cover, 20.0.0.4, 5 `.kf`, unmeasured values) | Joint cover; bs32 file; collisionboxstatic control; five idles named; sizes stated as lower bounds | 7.1 |
| `--anim` idle-pose override | A clip or a pose clip at t = 0 | App. C.2 |
| Provenance nits (inch, PSX, RE-1) | Relabeled; RE-15, RE-16; RE-1 read versus assumed | 4 |
| `neversoft_*` tests and docs | NMT step 4 | App. C.2 |
| "Tested 4.2 LTS and 5.1" | "To be tested"; Q4; both action APIs (then R24: minimum 5.0, slotted only) | 3.9, 6.4 |
| Q3 reopened R20 | Removed; legacy forms retire after parity | 3.10 |
| Small issues (package wording, container rule, BC5, mode 8, Blender encoders, normals by version) | Fixed in place; BC5 is Q3 | 3, 5, 6 |
| Citations (NifParser, DDXConv mapping, NMT lines, ten `*-scene`, EmissiveStrength, unlit emission, ClassicCommand lines, Xenos base formats) | Corrected | throughout |
| Billboard with two sources of truth; SceneBasis name | Derived legacy mode, agreement check; SceneSourceBasis; enum `Aim` | 2.2 |
| `.3DC` per-frame blocks and normals | NativeOnly raw bytes; DerivedSmooth("newell") | 2.3 |
| Blender preflight in Core | `IModelWriter.Preflight`, opaque option bag | 2.4 |
| Cube map in the cut-1 sample | Cut-1 rows in both writers | 5.2, 5.3 |
| RE-1 expected value | Dropped; constants listed; read versus assumed | 4.3 |
| FaceGen shading | Normal deltas; departure recorded | App. C.2 |
| DDXConv license facts | Settled by the owner 2026-09-23: Kran's MIT, not relicensed; question withdrawn | 10 |

---

## Appendix B: Cut-1b animation fidelity

**Curves** (source forms per RE-17, recalled until read):

| Curve | GLB | Blender |
|---|---|---|
| Position, scale, float: Linear, Step | LINEAR, STEP, Exact | LINEAR, CONSTANT, Exact |
| Hermite (Quadratic with tangents; scalar tangents now read) | CUBICSPLINE, tangents scaled by key spacing, Converted | Bezier, handles at 1/3, Converted |
| TBC (position, scalar) | Hermite tangents by the TBC formula, CUBICSPLINE, Converted pending RE-17 | Bezier, Converted pending RE-17 |
| B-spline of degree ≤ 3 (dominant in FNV `.kf`) | Knot insertion to CUBICSPLINE, Converted | Bezier, Converted |
| Rotation Linear (slerp) | LINEAR is slerp, Exact | Quaternion F-curves interpolate components then normalize (nlerp): keys inserted until the deviation ≤ 0.1°, Approximated |
| Rotation Quadratic, TBC (squad, recalled) | Resampled to LINEAR at a rate keeping ≤ 0.1°, Approximated | Same, Approximated |
| Rotation B-spline (component-wise then normalized, recalled) | CUBICSPLINE, which glTF also normalizes, Converted pending RE-17 | Bezier, Converted pending RE-17 |
| Euler XYZ | Resampled quaternions ≤ 0.1°, Approximated | Euler F-curves, Exact |
| Path (NiPathInterpolator, 19 files) | Resampled TRS within bounds, Approximated | Resampled, Approximated |
| LookAt (NiLookAtInterpolator, 7 files) | Resampled rotation ≤ 0.1°, Approximated | Damped Track constraint, Exact |
| Clock: frequency, phase, start, stop | Times scaled, Converted | NLA strip scale and offset, Exact |
| Cycle Loop / Clamp / Reverse | Metadata / Exact / keys mirrored into the clip, Converted | NLA repeat / hold / mirrored, Exact |
| Text keys | Metadata | Markers, Exact |
| `.kf` without a skeleton | Invented rest pose, Degraded | Same |

A control with rotation keys about 170° apart must show the nlerp deviation before correction.

**Controllers and interpolators in the FNV loose census** (files; field_features.json):

| Type | Document | GLB | Blender |
|---|---|---|---|
| NiTransformController 1,284; NiMultiTargetTransformController 1,153; NiTransformInterpolator 1,838 | Node TRS tracks | Curve table | Curve table |
| NiBlendFloat/Point3/BoolInterpolator 560 / 341 / 258 | Each sequence's own interpolator becomes that clip's track; blend weights Metadata | Clips | NLA strips |
| NiVisController 363 (NiBoolInterpolator 668, NiBoolTimelineInterpolator 120) | NodeVisibility | KHR_node_visibility through KHR_animation_pointer, plus step scale 0/1 keys on an inserted wrapper node so viewers without the extension also hide it, Converted. Skinned meshes (glTF ignores their node transform) get the extension only, Degraded where unsupported | `hide_viewport`/`hide_render` F-curves, Exact |
| NiMaterialColorController 533 | MaterialDiffuse, Specular, Ambient or Emissive, by target | Pointers to baseColorFactor, specularColorFactor, emissiveFactor, Exact; ambient Metadata (lighting-model row) | Socket F-curves, Exact |
| NiAlphaController 325 | MaterialAlpha | baseColorFactor alpha pointer, Exact | Exact |
| BSMaterialEmittanceMultController 252 | MaterialEmissive strength | emissive_strength pointer, Exact | Exact |
| NiTextureTransformController 687 | LayerUvOffset, Scale, Rotation | KHR_texture_transform pointers, Exact | Mapping F-curves, Exact |
| NiGeomMorpherController 55 | MorphWeight | Weights channel, Exact | Shape-key F-curves, Exact |
| NiFloatInterpolator 1,237; NiPoint3Interpolator 550 | Carriers for the rows above | — | — |
| NiPathInterpolator 19; NiLookAtInterpolator 7 | Path, LookAt curves | Curve table | Curve table |
| BSRefractionStrengthController 66; BSRefractionFirePeriodController 17 | NativeOnly: refraction not yet typed (cut 2 vocabulary) | Degraded | Degraded |
| BSFrustumFOVController 56; NiLightColorController 13; NiLightDimmerController 4 | NativeOnly until cameras and lights | Dropped (later-cut 2) | Same |
| NiFloatExtraDataController 40 | NativeOnly: drives game data, no direct visual (inferred) | Metadata | Metadata |
| NiBSBoneLODController 39 | NativeOnly: runtime bone LOD; full detail is the default | Metadata | Metadata |
| bhkBlendController 57 | NativeOnly with Havok | Degraded with Havok | Same |

---

## Appendix C: Later cuts in detail

### C.1 Cut 2: 3D scenes and the rest of the Bethesda NIF family
- `scene info` and `scene convert` for 3D; cameras, lights (KHR_lights_punctual plus `multitoolLight` for Gamebryo radius falloff, negative and default-off lights), markers, Havok collision previews as a hidden Collision layer.
- Scene readers: BS6 (flats become billboards), Redguard RGM and WLD (RAGR clips; actor scale from RE-3), Daggerfall RMB, RDB and dungeons (with the lights, flats and actions dropped today), then Arena.
- The remaining NIF keys, Morrowind through FO76, including 20.0.0.4 (the five FNV idles and `collisionboxstatic.nif`); Starfield `.mesh`; NiPixelData; Oblivion NiTexturingProperty slots; a refraction vocabulary; Blender environment-map approximation.
- BC6H, BC7, SNORM and float textures; Shadowkey meshes and zones.
- The WinUI `ExternalToolsSettings` section; `mesh census`; `SceneRendererAdmission` for the D3D12 viewer.
- RE items begin landing; labels flip to ReverseEngineered.

### C.2 Cut 3: actors and animation (R17), and NMT convergence
**BMT actor composer.** Produces a ModelDocument and keeps the composition decisions (head, parts, equipment, weapon attachment, skeleton). Today it writes through GlbScene (BMT CLI/Rendering/Npc/NpcExportPipeline.cs:111, 168); its parts move onto the NIF reader.
- `--anim` (BMT CLI/Commands/Export/ExportNpcCommand.cs:91-94) becomes a clip bound to the skeleton, or a named pose clip at t = 0. Rest and bind transforms stay as authored; today the selection overrides node transforms through `ParseIdlePoseOverrides` (BMT CLI/Rendering/Npc/NpcSkeletonLoader.cs:85-117), which the reader rule forbids.
- FaceGen: TRI morphs become Relative targets. The EGM modes weighted by the NPC's coefficients become one morph target at weight 1.0 **with normal deltas** computed from BMT's recalculated and welded normals (BMT NpcExportHeadAssembler.cs:195-199, 306), so shading matches today's baked head. Keeping the base head instead of baking is a deliberate structural departure from "as they are" with the same visual result. EGT becomes an image derivation. The full mode basis is a later option.
- `.kf` clips bind to the skeleton. FO3/FNV first, Oblivion partial, as today. `export npc` becomes an alias after parity. A standalone `.tri` converts with shape keys; an `.egm` alone is NotAModel ("morph basis; convert through the actor route").
- Also: SpeedTree through `mesh convert` (generated geometry, parameters and seed as native state); the Shared Granny reader.

**NMT session:**
1. Rename the local type to `LegacyModelDocument`.
2. Remove GltfModelExporter's calls into BlendPackageManifest (NMT Core/Formats/Mesh/Conversion/GltfModelExporter.cs:353, 357, 1284, 1296).
3. Write readers per source kind from native sources.
4. Switch the .blend route first; its 20 Blender-gated regressions gate the shared writer. In the same change, move the 11 test files and 3 docs that match `neversoft_` (for example PspGeBlendAlphaTests.cs, PsxBlendColourPulseTests.cs) to the `mt_*` names (R18); each match is checked, since some may be unrelated to Blender.
5. Switch GLB one source kind at a time.
6. Retire the old writers.

### C.3 Cut 4: 2D scenes and AWE
`scene --format png` from MapDocument (AWE rooms, BMT `classic map`, NMT's 2D composers, their aliases); AWE ASE and OMT and LevelAssembler as a scene reader (foundation); ESM cells and worldspaces as a 3D scene reader, where initially disabled references and enable parents become layer sets; Van Buren, Oblivion PSP and BOS once decoded.

### C.4 Later
A chunked Blender worker; a bounded `--jobs`; `--apply-scale` (not bit-exact); the D3D12 viewer drawing from render state; console texture relayouts consolidated in Shared after a license review (DDXConv stays MIT under Kran's notice; NMT XenosTiling); palette lookup node graphs; the full FaceGen basis option.
