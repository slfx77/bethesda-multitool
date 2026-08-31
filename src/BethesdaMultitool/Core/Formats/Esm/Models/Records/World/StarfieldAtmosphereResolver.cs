namespace BethesdaMultitool.Core.Formats.Esm.Models.Records.World;

/// <summary>Why a merged Starfield ATMO record did or did not resolve.</summary>
internal enum StarfieldAtmosphereResolutionStatus
{
    Resolved,
    TargetNotFound,
    DecodeFailure,
    UnknownPayloadKind,
    MissingPatch,
    MissingParent,
    ParentContractViolation,
    InheritanceCycle,
    DepthLimitExceeded
}

/// <summary>
///     Pure, fail-closed result of applying an ATMO inheritance chain. The chain is ordered from
///     the rootward-most record reached to the requested record. <see cref="EffectivePatch" /> is
///     populated only when every record in the chain resolves successfully.
/// </summary>
internal sealed record StarfieldAtmosphereResolution(
    StarfieldAtmosphereResolutionStatus Status,
    uint TargetFormId,
    StarfieldAtmospherePatch? EffectivePatch,
    IReadOnlyList<uint> InheritanceChain,
    uint? FailureFormId = null,
    string? FailureDetail = null)
{
    internal bool IsResolved =>
        Status == StarfieldAtmosphereResolutionStatus.Resolved && EffectivePatch is not null;
}

/// <summary>
///     Resolves already load-order-merged Starfield ATMO records. Nullable leaves are overlaid so
///     an absent DIFF member inherits while an authored zero replaces its parent value. RFDP is
///     the traversal edge; a reflected pParent, when present in a DIFF, must agree with that edge.
///     Retail reaches at most two parent edges and has no missing parents or cycles, but the
///     explicit cycle/depth guards also make malformed plugins deterministic.
/// </summary>
internal static class StarfieldAtmosphereResolver
{
    internal const int DefaultMaxDepth = 64;

    internal static StarfieldAtmosphereResolution Resolve(
        uint targetFormId,
        IReadOnlyDictionary<uint, StarfieldAtmosphereRecord> mergedRecords,
        int maxDepth = DefaultMaxDepth)
    {
        ArgumentNullException.ThrowIfNull(mergedRecords);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDepth, 1);

        var traversal = new List<uint>();
        var patches = new List<StarfieldAtmospherePatch>();
        var visited = new HashSet<uint>();
        var currentFormId = targetFormId;

        while (true)
        {
            if (visited.Contains(currentFormId))
            {
                return Fail(
                    StarfieldAtmosphereResolutionStatus.InheritanceCycle,
                    currentFormId,
                    $"ATMO inheritance revisits {currentFormId:X8}.");
            }

            if (traversal.Count >= maxDepth)
            {
                return Fail(
                    StarfieldAtmosphereResolutionStatus.DepthLimitExceeded,
                    currentFormId,
                    $"ATMO inheritance exceeds the {maxDepth}-record depth cap.");
            }

            if (!mergedRecords.TryGetValue(currentFormId, out var record))
            {
                var status = traversal.Count == 0
                    ? StarfieldAtmosphereResolutionStatus.TargetNotFound
                    : StarfieldAtmosphereResolutionStatus.MissingParent;
                return Fail(status, currentFormId, $"ATMO {currentFormId:X8} is absent from the merged index.");
            }

            visited.Add(currentFormId);
            traversal.Add(currentFormId);

            if (record.DecodeFailure is not null)
            {
                return Fail(
                    StarfieldAtmosphereResolutionStatus.DecodeFailure,
                    currentFormId,
                    record.DecodeFailure);
            }

            if (record.PayloadKind is not StarfieldAtmospherePayloadKind.FullObject and
                not StarfieldAtmospherePayloadKind.Diff)
            {
                return Fail(
                    StarfieldAtmosphereResolutionStatus.UnknownPayloadKind,
                    currentFormId,
                    $"ATMO {currentFormId:X8} has no established reflection payload kind.");
            }

            if (record.Patch is null)
            {
                return Fail(
                    StarfieldAtmosphereResolutionStatus.MissingPatch,
                    currentFormId,
                    $"ATMO {currentFormId:X8} has no decoded patch.");
            }

            patches.Add(record.Patch);

            if (record.IsFullDefinition)
            {
                if (record.ParentFormId is not null || record.Patch.ParentFormId != 0)
                {
                    return Fail(
                        StarfieldAtmosphereResolutionStatus.ParentContractViolation,
                        currentFormId,
                        $"Full ATMO {currentFormId:X8} must omit RFDP and author reflected pParent=0.");
                }

                // A valid full projection carries all three nullable leaves. Guard this invariant
                // here as well so manually constructed or future parser records cannot seed a
                // seemingly resolved chain with absent full-object data.
                if (!record.Patch.SunPresetOverrideFormId.HasValue ||
                    !record.Patch.ClimateOverrideFormId.HasValue)
                {
                    return Fail(
                        StarfieldAtmosphereResolutionStatus.MissingPatch,
                        currentFormId,
                        $"Full ATMO {currentFormId:X8} is missing required structural references.");
                }

                var effective = patches[^1];
                for (var index = patches.Count - 2; index >= 0; index--)
                {
                    effective = Merge(effective, patches[index]);
                }

                return new StarfieldAtmosphereResolution(
                    StarfieldAtmosphereResolutionStatus.Resolved,
                    targetFormId,
                    effective,
                    RootwardChain(traversal));
            }

            if (record.ParentFormId is not { } parentFormId || parentFormId == 0)
            {
                return Fail(
                    StarfieldAtmosphereResolutionStatus.MissingParent,
                    currentFormId,
                    $"Diff ATMO {currentFormId:X8} has no nonzero RFDP parent.");
            }

            if (record.Patch.ParentFormId is { } reflectedParent && reflectedParent != parentFormId)
            {
                return Fail(
                    StarfieldAtmosphereResolutionStatus.ParentContractViolation,
                    currentFormId,
                    $"Diff ATMO {currentFormId:X8} RFDP/reflected pParent values do not match.");
            }

            currentFormId = parentFormId;
        }

        StarfieldAtmosphereResolution Fail(
            StarfieldAtmosphereResolutionStatus status,
            uint failureFormId,
            string detail)
        {
            return new StarfieldAtmosphereResolution(
                status,
                targetFormId,
                null,
                RootwardChain(traversal),
                failureFormId,
                detail);
        }
    }

    private static IReadOnlyList<uint> RootwardChain(IReadOnlyList<uint> targetFirstTraversal)
    {
        var chain = targetFirstTraversal.Reverse().ToArray();
        return Array.AsReadOnly(chain);
    }

    private static StarfieldAtmospherePatch Merge(
        StarfieldAtmospherePatch inherited,
        StarfieldAtmospherePatch overlay)
    {
        return new StarfieldAtmospherePatch
        {
            ParentFormId = overlay.ParentFormId ?? inherited.ParentFormId,
            SunPresetOverrideFormId =
                overlay.SunPresetOverrideFormId ?? inherited.SunPresetOverrideFormId,
            ClimateOverrideFormId =
                overlay.ClimateOverrideFormId ?? inherited.ClimateOverrideFormId
        };
    }
}
