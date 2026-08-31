using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;

namespace BethesdaMultitool.Core.WorldData;

/// <summary>
///     One effective PNDT after its authored master CNAM and ordered EOVR records have been folded.
///     The body comes from the latest complete physical override. PNDT decoding requires every
///     physical record to carry a complete marker-delimited body, so no field-level body synthesis
///     occurs here.
/// </summary>
internal sealed record StarfieldResolvedPlanetData(
    uint FormId,
    string? EditorId,
    IReadOnlyList<StarfieldPlanetWorldspaceEntry> Worldspaces,
    StarfieldPlanetBodyData Body,
    int SourceRecordCount);

/// <summary>
///     A lossless inverse-WRLD candidate. The complete coordinate tuple is retained because one
///     WRLD FormID can occur more than once and one WRLD can be authored by multiple planets.
/// </summary>
internal sealed record StarfieldPlanetWorldspaceCandidate(
    StarfieldResolvedPlanetData Planet,
    StarfieldPlanetWorldspaceEntry Worldspace);

internal enum StarfieldPlanetWorldspaceIndexFailureKind
{
    InvalidPlanetFormId,
    MergeFailed
}

/// <summary>A per-planet fail-closed diagnostic; one malformed planet does not hide valid peers.</summary>
internal sealed record StarfieldPlanetWorldspaceIndexFailure(
    StarfieldPlanetWorldspaceIndexFailureKind Kind,
    uint PlanetFormId,
    StarfieldPlanetDataMergeStatus? MergeStatus,
    string Detail);

/// <summary>
///     Result of building the authentic Starfield PNDT inverse index. Callers may resolve only a
///     single-candidate WRLD automatically; candidate lists deliberately preserve ambiguity.
/// </summary>
internal sealed record StarfieldPlanetWorldspaceIndexResult(
    IReadOnlyDictionary<uint, StarfieldResolvedPlanetData> PlanetsByFormId,
    IReadOnlyDictionary<uint, IReadOnlyList<StarfieldPlanetWorldspaceCandidate>> CandidatesByWorldspaceFormId,
    IReadOnlyList<StarfieldPlanetWorldspaceIndexFailure> Failures)
{
    internal bool TryResolveUnique(
        uint worldspaceFormId,
        out StarfieldPlanetWorldspaceCandidate? candidate)
    {
        if (CandidatesByWorldspaceFormId.TryGetValue(worldspaceFormId, out var candidates) &&
            candidates.Count == 1)
        {
            candidate = candidates[0];
            return true;
        }

        candidate = null;
        return false;
    }
}

/// <summary>
///     Groups physical PNDT records by rebased FormID, folds each group in load order, and constructs
///     the inverse WRLD-to-planet lookup required by the Starfield environment route. It never picks
///     a winner when multiple authored candidates exist.
/// </summary>
internal static class StarfieldPlanetWorldspaceIndex
{
    internal static StarfieldPlanetWorldspaceIndexResult Build(
        IReadOnlyList<StarfieldPlanetDataRecord> loadOrder)
    {
        ArgumentNullException.ThrowIfNull(loadOrder);

        var recordsByPlanet = new Dictionary<uint, List<StarfieldPlanetDataRecord>>();
        var planetOrder = new List<uint>();
        var failures = new List<StarfieldPlanetWorldspaceIndexFailure>();

        foreach (var record in loadOrder)
        {
            if (record is null || record.FormId == 0)
            {
                failures.Add(new StarfieldPlanetWorldspaceIndexFailure(
                    StarfieldPlanetWorldspaceIndexFailureKind.InvalidPlanetFormId,
                    0,
                    null,
                    record is null
                        ? "PNDT inverse index input contains a null record."
                        : "PNDT inverse index input contains FormID zero."));
                continue;
            }

            if (!recordsByPlanet.TryGetValue(record.FormId, out var records))
            {
                records = [];
                recordsByPlanet.Add(record.FormId, records);
                planetOrder.Add(record.FormId);
            }

            records.Add(record);
        }

        var planets = new Dictionary<uint, StarfieldResolvedPlanetData>(recordsByPlanet.Count);
        var candidates = new Dictionary<uint, List<StarfieldPlanetWorldspaceCandidate>>();

        foreach (var planetFormId in planetOrder)
        {
            var physicalRecords = recordsByPlanet[planetFormId];
            var merge = StarfieldPlanetDataMerger.Merge(physicalRecords);
            if (!merge.IsResolved)
            {
                failures.Add(new StarfieldPlanetWorldspaceIndexFailure(
                    StarfieldPlanetWorldspaceIndexFailureKind.MergeFailed,
                    planetFormId,
                    merge.Status,
                    merge.FailureDetail ?? "PNDT worldspace merge failed without a diagnostic."));
                continue;
            }

            var latest = physicalRecords[^1];
            var body = latest.Body;
            if (body is null)
            {
                // The merger validates every body's completeness, but retain a local fail-closed
                // guard so this index cannot become unsafe if that implementation later changes.
                failures.Add(new StarfieldPlanetWorldspaceIndexFailure(
                    StarfieldPlanetWorldspaceIndexFailureKind.MergeFailed,
                    planetFormId,
                    StarfieldPlanetDataMergeStatus.MalformedRecord,
                    "PNDT merge resolved without a complete latest body."));
                continue;
            }

            var editorId = physicalRecords
                .Select(static record => record.EditorId)
                .LastOrDefault(static value => !string.IsNullOrWhiteSpace(value));
            var resolved = new StarfieldResolvedPlanetData(
                planetFormId,
                editorId,
                merge.EffectiveWorldspaces!,
                body,
                physicalRecords.Count);
            planets.Add(planetFormId, resolved);

            foreach (var worldspace in resolved.Worldspaces)
            {
                if (!candidates.TryGetValue(worldspace.WorldspaceFormId, out var worldspaceCandidates))
                {
                    worldspaceCandidates = [];
                    candidates.Add(worldspace.WorldspaceFormId, worldspaceCandidates);
                }

                worldspaceCandidates.Add(new StarfieldPlanetWorldspaceCandidate(resolved, worldspace));
            }
        }

        return new StarfieldPlanetWorldspaceIndexResult(
            planets,
            candidates.ToDictionary(
                static pair => pair.Key,
                static pair => (IReadOnlyList<StarfieldPlanetWorldspaceCandidate>)
                    Array.AsReadOnly(pair.Value.ToArray())),
            Array.AsReadOnly(failures.ToArray()));
    }
}
