# Shared world root and terrain pipeline adoption

**BMT world root and terrain pipelines adopted — runtime `d75222f8`,
corrected test source `d8cc0f4a`, Shared pin `20a96ce`.** Shared owns the world root signature
and terrain's ten bounded native pipeline states. The existing thirteen root
parameters, register spaces, samplers, game shaders, MSAA/reversed-Z states and
lazy color/mirror variants remain unchanged. Terrain streaming and frame policy
remain application-owned.

Shared dependent families retain the exact root until their pipelines release.
Early parent disposal rejects live children and remains retryable; releasing the
last child does not establish GPU retirement or dispose the parent automatically.
Terrain cleanup drains CPU builds and releases caches/geometry before pipelines,
retaining failed release actions for retry on the creating thread.

All **19 Shared native cases** and **15 focused BMT cases** pass without
skips. The production terrain factory supplies twelve real captures: all four
color/mirror widths, wrong-winding rejection, nearer depth occlusion, single-sample
shadow depth and family recreation against the retained world root. Locked
portable/Windows Release pass with 744/114 warnings.
Portable ILLink analysis still runs out of memory. Receipts: `SharedWorldRoot*` and `BmtWorldRoot*`.
The first run passed 14/15; its new fixture supplied an invalid typeless depth
view. The corrected fixture uses the production D32/MSAA view and passes a
test-only rebuild with 611 existing warnings, also addressing sixteen
new readonly-field diagnostics.

All five source/test paths and the exact pin are synchronized into canonical BMT;
its existing index and staged changes remain intact. AWE stays `7b42197` / `7825bd3`.
There is no new GUI/retail-world or fault-injected COM cleanup claim. Legacy raw-root
families still require external disposal ordering; unpublished constructor cleanup,
outer-owner retries and full streaming-cache retirement remain open.

**Next:** remaining world pipeline families, dynamic replacement, descriptor,
submission and residency-cache production adoption, then remaining AWE fidelity
and lifecycle work. All six milestones and TB1-TB6 remain active. All checks are
terminal and the component claim is released. No new checkout, backup or publication.

## Implementation

`GpuRootSignature12` retains a root-only `ShaderPipelineResources` owner and keeps
the existing application ABI. `CreatePipelineResources` attaches an independent
bounded family to that exact root on the same device object and creating thread.
No game root layout, shader permutation or rendering policy moves into Shared.

`TerrainRenderer12` retains one ten-slot family before shader/native setup: depth
and shadow in slots zero and one, followed by four color/mirror pairs. The factory
publishes each pair only after both handles exist. If the mirror allocation fails,
the first pipeline stays owned and can be reused during the next attempt. The
factory is now available to the portable target so tests execute the production
implementation rather than a reconstructed pipeline description.

Disposal rejects a foreign thread before stopping work. Successful cleanup stages
are not repeated; failures retain their actions. CPU builds drain first, followed
by the cell cache/shared-index transfer, arena, and pipeline family. Callers still
prove queue completion or device removal. Legacy raw-root users keep their current
external lifetime order until their own adoption passes.

## Acceptance and limits

Shared's five added native cases cover invalid/canceled attachment, exact managed
device identity, foreign-thread attachment, root-only ownership, and two independent
compute families. Actual fenced output is `[137, 148, 159, 170]` after an early
parent disposal is rejected; each child releases independently before parent retry.

BMT uses WARP, the real terrain factory, packed vertex/weight layouts, production
world root and existing recorder/offscreen target. Color/mirror frames match,
incorrect winding stays clear, a nearer depth-only draw occludes the color quad,
and reversed-winding shadow geometry writes the expected biased D32 depth. All ten
pipeline slots are exercised. Releasing/recreating the family preserves the live
root and reproduces the original pixels. Existing ABI, root/device and terrain
grid-binding checks run alongside this test.

Windows compiled runtime `d75222f8`; only the two new test files were being prepared
outside its application graph. Portable application compilation is `bdb3846d`;
the corrected test-only build reuses that unchanged runtime. Receipt payload hashes
identify the actual application, test and
Shared assemblies; no payload copies were made. Builds retain isolated compilation,
one node, default four GiB admission and an explicit eight GiB compiler heap cap.

This verifies native pipeline construction and representative raster/depth behavior,
not retail world appearance, GUI lifecycle or full terrain-streaming parity.
No deterministic COM release failure is injected. The existing arena and outer
legacy owners still have cleanup/retry limitations, including failed construction
before an owner can be returned. These are not covered by the new root dependency.
