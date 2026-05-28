# Synthetic runtime reader tests

This directory holds synthetic-fixture tests for the runtime DMP readers,
landed in the Tier 7 test discipline migration (2026-05-27).

## Standard

Per [`memory/feedback_test_discipline.md`](../../../../../../../../.claude/projects/c--Users-mmc99-source-repos-Xbox360MemoryCarver/memory/feedback_test_discipline.md):
**tests use minimal synthetic data and focus on code coverage + correctness.**

In practice this means:
- **No captured DMP binaries.** All inputs are byte buffers built in-memory.
- **No rate-floor assertions.** Tests assert exact values, not "≥ N%
  pointer-shape across snippets."
- **No skip-with-log paths.** A passing test means the contract holds;
  it doesn't mean "the snippet didn't exercise this path."
- **No diagnostic-only tests.** No `TestOutputHelper.WriteLine` calls that
  describe what the code did without asserting anything.

## Canonical patterns

Two existing files are the gold standard — mirror their shape when adding
new tests:

- [`../PdbStructViewTests.cs`](../PdbStructViewTests.cs) — synthetic
  `PdbTypeLayout` + in-memory buffer + exact-value asserts. The template
  for any test that exercises `PdbStructView` directly.
- [`../../../Plugin/CreaSmokeIntegrationTests.cs`](../../../Plugin/CreaSmokeIntegrationTests.cs) —
  encoder→parser roundtrip pattern: build domain object → encode → parse
  bytes → assert specific field values survive. The template for any
  cross-layer roundtrip test.

## Building blocks

Two helpers in [`../../../../../Helpers/`](../../../../../Helpers/) compose
into a clean test setup:

- **`SyntheticStructFactory`** — `WriteFormHeader` + `WriteBsString` plus
  per-record builders (`BuildNpc`, `BuildRefr`, `BuildWeap`). Add new
  builders here when a record type recurs across multiple test files.
- **`RuntimeReaderTestFixture`** — fluent setup wrapping
  `SparseMemoryAccessor` + synthetic `MinidumpInfo`:
  ```csharp
  var fixture = RuntimeReaderTestFixture.Default()
      .WithStruct(buffer, va: 0x40100000)
      .WithPointerTarget(targetVa: 0x40200000, targetBytes);
  var reader = new RuntimeBookReader(fixture.BuildContext());
  var entry = fixture.MakeEntry(formId, formType, va);
  ```

For lower-level test data (BE primitives etc.), use
[`../../../../../Helpers/BinaryTestWriter.cs`](../../../../../Helpers/BinaryTestWriter.cs).

## What's in here

- **Offset reader tests** (BOOK / REFR / WEAP / CONT / SCPT / LAND / PACK)
  — pin runtime offsets the production readers look up. Each test writes
  sentinel bytes at the offset, calls the reader, asserts exact-value
  resolution.
- **Regressions/** subdirectory — synthetic guards for specific historical
  bugs (TERM Difficulty offset, BGSPerkEntry rank-at-+4, NPC FaceGen
  morph chase). Each fixture captures the bug-triggering input shape,
  not a snapshot of real DMP bytes.

## Adding a new test

1. Pick the most specific reader API you can call (e.g. the leaf reader
   method, not the full DMP scan).
2. Build a synthetic struct with `SyntheticStructFactory` primitives or
   inline `WriteFormHeader` + `Write*BE`.
3. Set up `RuntimeReaderTestFixture.Default().WithStruct(...).WithPointerTarget(...)`.
4. Invoke the reader; assert exact values.
5. Add the negative cases too (null pointer, out-of-band value, wrong
   FormType) — each catches a different real bug class.

If the test needs more than ~2 KB of synthetic input to express its
contract, the abstraction layer is probably wrong — test a smaller leaf
component instead.

## What's NOT in here

- **Bucket B (NIF / render / real-asset tests)** — out of scope for the
  Tier 7 migration. Lives elsewhere under `Core/Formats/Nif/Rendering/`
  and still depends on `Sample/Full_Builds/...` paths via
  `SampleFileFixture`. Future tier candidate.
