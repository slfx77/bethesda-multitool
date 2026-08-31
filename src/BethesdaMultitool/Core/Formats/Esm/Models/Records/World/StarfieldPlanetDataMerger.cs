namespace BethesdaMultitool.Core.Formats.Esm.Models.Records.World;

/// <summary>Why an authored PNDT worldspace-list load order did or did not merge.</summary>
internal enum StarfieldPlanetDataMergeStatus
{
    Resolved,
    MissingMaster,
    DeltaWithoutBase,
    MalformedRecord,
    DuplicateMasterAmbiguity,
    DuplicateMasterTupleAmbiguity,
    UnmatchedRemoval,
    ConflictingAddition
}

/// <summary>
///     Fail-closed result of folding authored PNDT EOVR operations over one master CNAM list.
///     This is a plugin load-order operation only and makes no CE2 runtime-selection claim.
/// </summary>
internal sealed record StarfieldPlanetDataMergeResult(
    StarfieldPlanetDataMergeStatus Status,
    IReadOnlyList<StarfieldPlanetWorldspaceEntry>? EffectiveWorldspaces,
    int? FailureRecordIndex = null,
    int? FailureDeltaIndex = null,
    string? FailureDetail = null)
{
    internal bool IsResolved =>
        Status == StarfieldPlanetDataMergeStatus.Resolved && EffectiveWorldspaces is not null;
}

/// <summary>
///     Applies PNDT records in load-order sequence. Exact tuple identity is the raw latitude bits,
///     raw longitude bits, and WRLD FormID represented by <see cref="StarfieldPlanetWorldspaceEntry" />.
/// </summary>
internal static class StarfieldPlanetDataMerger
{
    internal static StarfieldPlanetDataMergeResult Merge(
        IReadOnlyList<StarfieldPlanetDataRecord> loadOrder)
    {
        ArgumentNullException.ThrowIfNull(loadOrder);

        List<StarfieldPlanetWorldspaceEntry>? effective = null;
        HashSet<StarfieldPlanetWorldspaceEntry>? membership = null;

        for (var recordIndex = 0; recordIndex < loadOrder.Count; recordIndex++)
        {
            var record = loadOrder[recordIndex];
            if (!TryValidateRecord(record, out var validationError))
            {
                return Fail(
                    StarfieldPlanetDataMergeStatus.MalformedRecord,
                    recordIndex,
                    null,
                    validationError ?? "PNDT record validation failed.");
            }

            if (record.PayloadKind == StarfieldPlanetDataPayloadKind.Master)
            {
                if (effective is not null)
                {
                    return Fail(
                        StarfieldPlanetDataMergeStatus.DuplicateMasterAmbiguity,
                        recordIndex,
                        null,
                        "A later PNDT master CNAM cannot be distinguished from an ambiguous replacement.");
                }

                effective = new List<StarfieldPlanetWorldspaceEntry>(record.MasterWorldspaces);
                membership = new HashSet<StarfieldPlanetWorldspaceEntry>();
                for (var tupleIndex = 0; tupleIndex < effective.Count; tupleIndex++)
                {
                    if (!membership.Add(effective[tupleIndex]))
                    {
                        return Fail(
                            StarfieldPlanetDataMergeStatus.DuplicateMasterTupleAmbiguity,
                            recordIndex,
                            tupleIndex,
                            $"PNDT master CNAM repeats exact tuple {tupleIndex}.");
                    }
                }

                continue;
            }

            if (effective is null || membership is null)
            {
                return Fail(
                    StarfieldPlanetDataMergeStatus.DeltaWithoutBase,
                    recordIndex,
                    null,
                    "PNDT EOVR cannot be applied before a master CNAM list.");
            }

            for (var deltaIndex = 0; deltaIndex < record.WorldspaceOverrides.Count; deltaIndex++)
            {
                var delta = record.WorldspaceOverrides[deltaIndex];
                switch (delta.Operation)
                {
                    case StarfieldPlanetWorldspaceOperation.Removed:
                        if (!membership.Remove(delta.Entry))
                        {
                            return Fail(
                                StarfieldPlanetDataMergeStatus.UnmatchedRemoval,
                                recordIndex,
                                deltaIndex,
                                "PNDT EOVR removal has no exact tuple in the effective list.");
                        }

                        var removalIndex = effective.IndexOf(delta.Entry);
                        if (removalIndex < 0)
                        {
                            return Fail(
                                StarfieldPlanetDataMergeStatus.MalformedRecord,
                                recordIndex,
                                deltaIndex,
                                "PNDT merge membership and authored order disagree.");
                        }

                        effective.RemoveAt(removalIndex);
                        break;

                    case StarfieldPlanetWorldspaceOperation.Added:
                        if (!membership.Add(delta.Entry))
                        {
                            return Fail(
                                StarfieldPlanetDataMergeStatus.ConflictingAddition,
                                recordIndex,
                                deltaIndex,
                                "PNDT EOVR addition conflicts with an existing exact tuple.");
                        }

                        effective.Add(delta.Entry);
                        break;

                    default:
                        return Fail(
                            StarfieldPlanetDataMergeStatus.MalformedRecord,
                            recordIndex,
                            deltaIndex,
                            $"PNDT EOVR carries unknown operation {(byte)delta.Operation}.");
                }
            }
        }

        if (effective is null)
        {
            return Fail(
                StarfieldPlanetDataMergeStatus.MissingMaster,
                null,
                null,
                "PNDT load order contains no master CNAM list.");
        }

        return new StarfieldPlanetDataMergeResult(
            StarfieldPlanetDataMergeStatus.Resolved,
            Array.AsReadOnly(effective.ToArray()));
    }

    private static bool TryValidateRecord(
        StarfieldPlanetDataRecord? record,
        out string? error)
    {
        error = null;
        if (record is null)
        {
            error = "PNDT load order contains a null record.";
            return false;
        }

        if (record.DecodeFailure is not null)
        {
            error = record.DecodeFailure;
            return false;
        }

        var atmosphere = record.Body?.Atmosphere;
        if (atmosphere is null ||
            !float.IsFinite(atmosphere.UnknownFloat0) ||
            !float.IsFinite(atmosphere.UnknownFloat1) ||
            !float.IsFinite(atmosphere.UnknownFloat2))
        {
            error = "PNDT record has no complete finite body projection.";
            return false;
        }

        if (record.MasterWorldspaces is null || record.WorldspaceOverrides is null)
        {
            error = "PNDT record has a null authored list.";
            return false;
        }

        switch (record.PayloadKind)
        {
            case StarfieldPlanetDataPayloadKind.Master when record.WorldspaceOverrides.Count == 0:
            case StarfieldPlanetDataPayloadKind.Override when record.MasterWorldspaces.Count == 0:
                return true;
            case StarfieldPlanetDataPayloadKind.Master:
                error = "PNDT master record also carries override operations.";
                return false;
            case StarfieldPlanetDataPayloadKind.Override:
                error = "PNDT override record also carries a master list.";
                return false;
            default:
                error = "PNDT record has no established master/override payload kind.";
                return false;
        }
    }

    private static StarfieldPlanetDataMergeResult Fail(
        StarfieldPlanetDataMergeStatus status,
        int? recordIndex,
        int? deltaIndex,
        string detail)
    {
        return new StarfieldPlanetDataMergeResult(
            status,
            null,
            recordIndex,
            deltaIndex,
            detail);
    }
}
