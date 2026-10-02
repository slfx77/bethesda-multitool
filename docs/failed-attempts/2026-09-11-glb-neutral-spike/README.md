# Failed attempt: `GlbSceneNeutralAdapter` (2026-09-11)

Kept verbatim as failed evidence. The program records failed attempts separately from successful
evidence rather than deleting them. Both files are stored with a `.txt` suffix so no compile glob
picks them up; rename to `.cs` to read them in an editor with highlighting.

| File | Original location |
| --- | --- |
| `GlbSceneNeutralAdapter.cs.txt` | `src/BethesdaMultitool/Core/Formats/Nif/Rendering/Export/` |
| `GlbSceneNeutralAdapterTests.cs.txt` | `tests/BethesdaMultitool.Tests/Core/Formats/Nif/Rendering/Export/` |

Written 2026-09-11 at 17:13 and 18:37, after the last shared commit of that session (`eb7f496`,
17:04). Both were untracked, and no receipt, document or ledger entry referenced them.

## Why it was not adopted

The code reads well — an exhaustive decline taxonomy, no silent loss of a Bethesda material
semantic — but it is wrong in kind rather than degree. Four defects, each independently
disqualifying.

1. **It adapts the wrong end of the pipeline.** `TryAdapt` takes a `GlbScene`, which is the *input*
   to `GlbWriter`. Every transform the writer applies afterwards is therefore skipped, including
   `GltfCoordinateAdapter`'s Z-up to Y-up rotation, `NormalizeWinding`, material dedup and
   `NpcGlbTangentBuilder`. Wiring it in as written is a silent 90-degree regression on every export.

2. **Its tests certify a document the shared validator rejects.** It emits `Vector3.Zero` normals,
   which `SceneValidation` refuses, and its `Triangle()` fixture supplies no normals — so all 15
   cases assert against a shape that cannot validate. It never calls `SceneValidation.Validate`;
   AweMultitool's equivalent calls it at `AseNeutralSceneAdapter.cs:68`.

3. **It serialises with `ToJsonString()` instead of `SceneJson.Serialize`**, repeating the
   reflection-disabled defect that made an earlier pilot historical.

4. **It has no possible caller.** `SceneGltfBuilder` and `GltfExporter`, the only consumers of a
   `SceneDocument`, live in `Slfx77.Multitool.Media`, and BethesdaMultitool references only `Core`
   and `WinUI`. That missing project reference — not `SceneTangents` — is M2.1's real precondition.

## What is worth salvaging

- The `MaterialReason` census: the enumeration of Bethesda material states that a neutral document
  cannot express is genuine work and should be carried into the replacement's decline policy.
- The shape of the test file: one case per decline reason is the right structure for the rewrite.

## Replacement

A `NifNeutralSceneAdapter` integrated at the `GlbWriter.BuildGltfScene` boundary, taking the
texture resolver so images, samplers and bindings are emitted rather than a path stuffed into
material extras, applying the coordinate adapter and winding normalisation, and calling
`SceneValidation.Validate`.
