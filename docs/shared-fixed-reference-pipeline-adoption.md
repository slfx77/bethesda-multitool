# Shared fixed reference pipeline adoption

**BMT fixed reference pipelines adopted — `bfd5c059`, unchanged Shared pin
`d455164`.** The actual application factory delegates mandatory opaque/coverage,
shadow/mirror, optional game specializations, grass, eye and skin pipelines to
existing Shared dependent-root families and keyed caches. BMT retains shader
recipes, admission, fallback, aliases and timing. Complete families publish
atomically; grass replacement and runtime retirement retain failed releases.
Rollback attempts independent siblings and reports both creation and cleanup
failures.

All **135 affected cases pass**, without skips, including seven expanded
native cases for the actual one-/four-sample factories, reversed-Z shadow depth,
optional game families, lazy skin/eye reuse and grass-family replacement. Managed
rollback tests cover deduplication, sibling cleanup, retry, commit and forgetting
released owners. Locked portable/Windows Release pass with
697/88 warnings. Receipts `BmtFixedPipeline*` identify exact
source, pin and built payloads. The portable build retains the invocation-only
two-processor bound, analyzers, isolated compiler, eight GiB cap and four GiB
admission.

All ten component source/test files are synchronized into canonical BMT with
its index unchanged. Native checks are synthetic; no GUI/retail-world or injected
COM-release-failure claim. GPU retirement remains caller-owned, and unpublished
constructor/outer-owner retry gaps remain. See BMT
`docs/shared-fixed-reference-pipeline-adoption.md`.

**Next:** world descriptor/submission/residency adoption, followed by remaining
AWE migration gates. Checks terminal; fixed-reference claim released. AWE remains
`7b42197` / `7825bd3`; all six milestones/TB1-TB6 remain active. Other pins,
peer edits, storage and publication remain unchanged.

## Implementation

`ReferencePipelineFactory12` delegates every fixed, optional and lazy native
pipeline to the existing Shared dependent-root families or keyed cache. BMT
continues to select shader permutations and build the render descriptions.
The Shared pin remains `d455164`; no new framework or rendering abstraction is
introduced for this adoption.

- One mandatory family owns the direct/instanced opaque, coverage, shadow and
  mirror variants. Single-sample coverage handles remain aliases, not extra
  native allocations or disposal owners.
- Modern Fallout, Starfield and classic skin specializations publish complete
  optional families. Failed unpublished allocations stay owned until cleanup
  succeeds. The diagnostic skin pair still propagates its original failure.
- Grass profile replacement retires only its independent family. A failed
  release remains retryable; the profile and compile-attempt state change only
  after the old family has been released. Disabled or unsupported profiles
  retain the established fallback.
- Independent skin variants use the existing keyed cache; eye rendering uses
  its own family. Shader inputs, additive blending, mirror routing, sample
  counts, reversed-Z behavior, timing and game admission remain unchanged.
- Factory rollback attempts every acquired owner and retains failed entries.
  Runtime retirement releases owners rather than borrowed handle fields and
  rejects disposal on a foreign thread. Callers still prove GPU retirement.

The actual factory is included in the portable target. Native tests exercise
production pipeline construction, shadow depth output, family replacement and
shared-root lifetime. They supplement the existing shader/admission contracts;
they do not establish retail-world visual parity.

The initial portable build found obsolete borrowed-handle fields. Correction
`bfd5c059` removes them and makes the mandatory-family constructor handle local;
the retained list remains its owner. The rollback exception has a line-scoped
S3877 allowance because reporting successful cleanup would conceal retained
resources. The initial 135-case test run passed before this diagnostic cleanup.
