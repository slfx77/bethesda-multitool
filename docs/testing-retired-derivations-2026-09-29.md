# Retired arithmetic test derivations

These calculations were removed from executable tests on 2026-09-29 because both
the operation and its expected result were defined entirely in test code. They
explain rendering choices but do not validate the production implementation.

| Former test | Retained derivation |
| --- | --- |
| `ShadowComparisonPcfTests.FourLinearSamplesReproduceTheLegacySeparableKernelWeights` | For fractional position `f`, the legacy one-dimensional weights are `[1-f, 1, 1, f]`. Pairing weights `2-f` and `1+f` with fractions `1/(2-f)` and `f/(1+f)` gives the same four weights, whose sum is `3`. The Cartesian product gives the normalized 4-by-4 kernel. The retired test sampled 10,001 fractions without invoking a shader. |
| `ShadowComparisonPcfTests.StrictGreaterComparisonMatchesLegacyReversedZVisibility` | `stored < reference` and `reference > stored` are the same comparison. Equality is shadowed under this strict reversed-Z rule. Nine input rows tested only that language-level identity. |
| `DoubleSidedNormalMapBasisSourceContractTests.BackFaceReversesNormalAndBitangentToPreserveHandedness` | With tangent `T` unchanged and `B = cross(N, T)`, negating `N` also negates `B`. For `N = UnitZ` and `T = UnitX`, the back-face bitangent is `-UnitY`. |
| `GpuTonemapSettingsTests.RecoveredAdaptationReference_WeightsCurrentScene` | Temporal adaptation `(1-k)*previous + k*current` gives `0.4` for previous `0.2`, current `1.0`, and `k = 0.25`. |
| `GpuTonemapSettingsTests.RecoveredCinematicReference_AppliesBrightnessInsideContrast` | `contrast*(brightness*color-pivot)+pivot` gives `0.623` for color `0.6`, brightness `0.9`, contrast `1.2`, and pivot `0.125`. |

Production mode selection, settings, shader inventory, and existing source wiring
checks remain. These derivations must be compared with output from the production
renderer before they can serve as numerical rendering regression tests.

The complete former `RecoveredHdrShaderReferenceTests` source is preserved byte
for byte in [RecoveredHdrShaderReference.cs.txt](reference/RecoveredHdrShaderReference.cs.txt).
Its 15 facts and six theory rows exercised private test-only mathematical models,
not production code. The literal FNV reference vectors, recovered kernel tables,
FO3 scope caveat, and explanatory calculations remain available for future
comparisons with renderer output. No equivalence with GPU output is claimed.
