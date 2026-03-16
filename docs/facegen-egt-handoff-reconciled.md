# FaceGen EGT Handoff (Reconciled, 2026-03-14)

This note replaces the stale user handoff summary and
`C:\Users\mmc99\.claude\plans\moonlit-noodling-narwhal.md` where they conflict.
Use `docs/facegen-egt-blend-spec.md`, especially the
`Authoritative Reconciliation (2026-03-14)` section, as the primary source of truth.

## Current State

- The EGT mismatch is still open.
- The current repo does not treat the bake/render path as solved.
- Raw-PDB Xenon reverse-engineering plus current repo code is the highest-confidence evidence source.

Current 3-NPC verifier baseline from `artifacts/egt-handoff-reconcile-3npc.csv`:

| NPC | FormID | MAE | RMSE | Max |
| --- | --- | ---: | ---: | ---: |
| CraigBoone | `0x00092BD2` | `1.3711` | `1.7125` | `12` |
| CGPresetAfricanAmericanF01 | `0x0001816A` | `1.6774` | `2.4261` | `24` |
| DoctorBarrows | `0x000156F0` | `1.5762` | `2.0788` | `18` |

The current verifier already includes:

- coefficient dumps
- `merged` vs `npc_only` vs `race_only`
- channel permutation ranking
- region metrics with signed bias
- per-morph ablation

The current verifier does not include:

- a live RMS clamp sweep
- PNAM/UNAM-aware replay

## What Is Settled

- The runtime merge path is backed by raw-PDB Xenon evidence.
- Raw `FGGS/FGGA/FGTS` record tags and `TESNPC::LoadFaceGen` populate the same descriptor-backed FaceGen coord blob.
- `BSFaceGenManager::ScaleFaceCoord` is only a uniform blob-scale helper.
- DDX wrapper overhead is small relative to the full mismatch, but the encode/decode/output boundary is still not closed.

## PNAM / UNAM Status

Representative parsed race values are all:

- `PNAM / FaceGenMainClamp = 5.0`
- `UNAM / FaceGenFaceClamp = 3.0`

Confirmed boundary in current repo:

- parsed into `RaceRecord`: yes
- carried into `RaceScanEntry` / `NpcAppearanceIndex`: no
- used by the render/verify path: no

Current raw-PDB Xenon evidence does not yet show a concrete PNAM/UNAM consumer in the bake/runtime path. That means PNAM/UNAM is not the top lead right now.

## Priority For The Next Session

Default next target:

1. offline facemod bake/output path
2. encode/decode/output boundary (`GetCompressedImage`, `GetUncompressedImage`, shipped facemod bytes vs decoded texture)
3. authored/runtime control-space behavior only where it directly changes saved or baked facemod output

Do not restart from the old "missing raw FGTS projection" theory.
Do not assume the remaining mismatch is fully explained by DXT1.
Do not make renderer behavior changes until the next reverse-engineering step materially tightens the evidence.
