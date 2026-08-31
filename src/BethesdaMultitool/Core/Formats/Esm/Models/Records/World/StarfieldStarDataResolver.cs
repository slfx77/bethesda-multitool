namespace BethesdaMultitool.Core.Formats.Esm.Models.Records.World;

/// <summary>Why an STDT route selected by a PNDT scalar system ID did or did not resolve.</summary>
internal enum StarfieldStarDataResolutionStatus
{
    Resolved,
    SystemNotFound,
    AmbiguousSystem,
    PrimaryDecodeFailure,
    PrimaryRoutingMissing,
    BinaryStarNotFound,
    AmbiguousBinaryStar,
    BinaryStarDecodeFailure,
    BinaryStarRoutingMissing
}

/// <summary>
///     Fail-closed STDT routing result. A nonzero SNAM is followed exactly once through the FormID
///     index; no orbital, light, or rendering behavior is inferred from that relationship.
/// </summary>
internal sealed record StarfieldStarDataResolution(
    StarfieldStarDataResolutionStatus Status,
    uint SystemId,
    StarfieldStarDataRecord? Primary,
    StarfieldStarDataRecord? BinaryStar,
    IReadOnlyList<uint> ConflictingFormIds,
    uint? FailureFormId = null,
    string? FailureDetail = null)
{
    internal bool IsResolved =>
        Status == StarfieldStarDataResolutionStatus.Resolved && Primary is not null;
}

/// <summary>
///     Resolves a unique STDT from PNDT's scalar system ID and, when authored, follows the SNAM
///     binary-companion FormID. System-ID ambiguity is reported with every candidate instead of
///     choosing by enumeration or load order. Missing PNAM/HNAM remain valid authored omissions.
/// </summary>
internal static class StarfieldStarDataResolver
{
    internal static StarfieldStarDataResolution ResolveSystem(
        uint systemId,
        StarfieldStarDataIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);

        if (!index.RecordsBySystemId.TryGetValue(systemId, out var systemCandidates))
        {
            return Fail(
                StarfieldStarDataResolutionStatus.SystemNotFound,
                [],
                null,
                $"No STDT authors scalar system ID 0x{systemId:X8}.");
        }

        if (systemCandidates.Count != 1)
        {
            var candidates = CandidateFormIds(systemCandidates);
            return Fail(
                StarfieldStarDataResolutionStatus.AmbiguousSystem,
                candidates,
                null,
                $"Scalar system ID 0x{systemId:X8} matches {systemCandidates.Count} STDT records.");
        }

        var primary = systemCandidates[0];
        if (primary.DecodeFailure is not null)
        {
            return Fail(
                StarfieldStarDataResolutionStatus.PrimaryDecodeFailure,
                [primary.FormId],
                primary.FormId,
                primary.DecodeFailure,
                primary);
        }

        if (primary.Routing is null)
        {
            return Fail(
                StarfieldStarDataResolutionStatus.PrimaryRoutingMissing,
                [primary.FormId],
                primary.FormId,
                $"STDT 0x{primary.FormId:X8} has no typed routing projection.",
                primary);
        }

        if (primary.Routing.SystemId != systemId)
        {
            return Fail(
                StarfieldStarDataResolutionStatus.PrimaryRoutingMissing,
                [primary.FormId],
                primary.FormId,
                $"STDT index contract mismatch for scalar system ID 0x{systemId:X8}.",
                primary);
        }

        if (primary.Routing.BinaryStarFormId is not { } binaryStarFormId || binaryStarFormId == 0)
        {
            return new StarfieldStarDataResolution(
                StarfieldStarDataResolutionStatus.Resolved,
                systemId,
                primary,
                null,
                []);
        }

        if (!index.RecordsByFormId.TryGetValue(binaryStarFormId, out var binaryCandidates))
        {
            return Fail(
                StarfieldStarDataResolutionStatus.BinaryStarNotFound,
                [],
                binaryStarFormId,
                $"STDT 0x{primary.FormId:X8} references absent binary companion " +
                $"0x{binaryStarFormId:X8}.",
                primary);
        }

        if (binaryCandidates.Count != 1)
        {
            return Fail(
                StarfieldStarDataResolutionStatus.AmbiguousBinaryStar,
                CandidateFormIds(binaryCandidates),
                binaryStarFormId,
                $"Binary companion FormID 0x{binaryStarFormId:X8} matches " +
                $"{binaryCandidates.Count} STDT records.",
                primary);
        }

        var binaryStar = binaryCandidates[0];
        if (binaryStar.DecodeFailure is not null)
        {
            return Fail(
                StarfieldStarDataResolutionStatus.BinaryStarDecodeFailure,
                [binaryStar.FormId],
                binaryStar.FormId,
                binaryStar.DecodeFailure,
                primary);
        }

        if (binaryStar.Routing is null)
        {
            return Fail(
                StarfieldStarDataResolutionStatus.BinaryStarRoutingMissing,
                [binaryStar.FormId],
                binaryStar.FormId,
                $"Binary companion STDT 0x{binaryStar.FormId:X8} has no typed routing projection.",
                primary);
        }

        return new StarfieldStarDataResolution(
            StarfieldStarDataResolutionStatus.Resolved,
            systemId,
            primary,
            binaryStar,
            []);

        StarfieldStarDataResolution Fail(
            StarfieldStarDataResolutionStatus status,
            IReadOnlyList<uint> conflicts,
            uint? failureFormId,
            string detail,
            StarfieldStarDataRecord? resolvedPrimary = null)
        {
            return new StarfieldStarDataResolution(
                status,
                systemId,
                resolvedPrimary,
                null,
                conflicts,
                failureFormId,
                detail);
        }
    }

    private static IReadOnlyList<uint> CandidateFormIds(
        IEnumerable<StarfieldStarDataRecord> records) =>
        Array.AsReadOnly(records.Select(record => record.FormId).ToArray());
}

/// <summary>
///     Pure FormID rebasing for an STDT envelope. DNAM's scalar system ID is copied verbatim;
///     authored zero and omitted FormID fields are also preserved without invoking the mapper.
/// </summary>
internal static class StarfieldStarDataFormIdRebaser
{
    internal static StarfieldStarDataRecord Rebase(
        StarfieldStarDataRecord record,
        Func<uint, uint> rebaseFormId)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(rebaseFormId);

        return record with
        {
            FormId = RebaseNonzero(record.FormId, rebaseFormId),
            Routing = record.Routing is null
                ? null
                : record.Routing with
                {
                    SystemId = record.Routing.SystemId,
                    BinaryStarFormId = RebaseOptional(
                        record.Routing.BinaryStarFormId, rebaseFormId),
                    SunPresetFormId = RebaseOptional(
                        record.Routing.SunPresetFormId, rebaseFormId),
                    TimeOfDayDataFormId = RebaseOptional(
                        record.Routing.TimeOfDayDataFormId, rebaseFormId)
                }
        };
    }

    private static uint? RebaseOptional(uint? value, Func<uint, uint> rebaseFormId) =>
        value is null ? null : RebaseNonzero(value.Value, rebaseFormId);

    private static uint RebaseNonzero(uint value, Func<uint, uint> rebaseFormId) =>
        value == 0 ? 0 : rebaseFormId(value);
}
