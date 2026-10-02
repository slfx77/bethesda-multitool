# Shared terrain ownership in Bethesda

September 29, 2026. Runtime: `1e4e745c`; test cleanup and Windows source: `40a7464e`; exact Shared pin: `cb597c13`.

Terrain uploads now retain the exact cell allocation before recording native copies. The existing Shared residency ledger accounts for prepared, resident and retiring ranges; failed releases remain charged and retryable. BMT retains its terrain layout, shaders, world interpretation, distance/LRU policy and fence/deletion-queue timing.

The production renderer uses separate lifetimes for preparation and eviction. Successful submission permanently disarms the preparation release: an older upload fence cannot free a range still used by a later draw. Uncertain or rejected submissions cannot publish drawable residency. World replacement retains each previous cache's exact entries independently of reused grid coordinates.

Admission includes pending release charges. Eviction plans only the additional space needed after those releases finish; repeated blocked requests do not evict further survivors unnecessarily. The distance sweep uses drawable bytes separately from charged ownership. Completed CPU cells survive deferred GPU admission without being marked corrupt or repeatedly rebuilt.

Native terrain blocks and the bounded mapped staging ring now use Shared NativeBufferBlocks. Ring reservations acquire their release handle before admission, failed native cleanup remains owned, and range/staging teardown can be retried. The terrain arena retains its existing session high-water allocation policy; this change does not impose a new physical GPU budget.

## Verification

- Locked portable Release build passes: 699 warnings, zero errors, 7m50s. Two new test array-style warnings are corrected in `40a7464e`; the test-only rebuild passes with 611 existing warnings, zero errors, 2m20s, reusing the byte-identical application assembly.
- All 116 affected tests pass with no skips, including actual WARP upload, arena/staging lifetime and terrain pipeline/layout checks. All eight cache cases pass again after the assertion-only correction.
- Locked Windows Release GUI build passes: 88 existing warnings, zero errors, 5m56s. No changed-file diagnostics or analyzer exceptions remain in these checks.
- Checks use the maintained coordinator, four GiB free-memory admission, BMT isolated compilation, serial compilation and an invocation-only eight GiB compiler heap cap/two-processor bound. BMT Development plus its existing application analyzers is not Full release acceptance.

Receipts are under canonical Shared `TestOutput/archive-waveform-20260914/BmtTerrainOwnership{Portable01,Portable02,Tests01,Tests02,Gui01}`. Test and GUI `payload.json` files identify actual assemblies and the source. The test-only correction preserves runtime SHA-256 `395c5ef10cab7b0ae7d240d3dfdd441978f29353238d5cb8ba96a394ae8226b3`.

There was no new interactive GUI/retail-world, cross-platform, native COM-release-failure-injection or performance measurement. Managed failure-injection tests cover queue admission and exact-range release retries. Physical high-water policy, unpublished-constructor cleanup and outer-owner retry limits remain explicit; this is not whole-program or release acceptance.

## Review checkout

Thirteen runtime/test paths from the component are byte-mirrored into `C:/dev/Multitool/BethesdaMultitool`. The canonical index and staged diff remain unchanged. Its complete dirty checkout is not the same payload as the isolated implementation build. No new checkout, payload copy, full backup or public push was created.
