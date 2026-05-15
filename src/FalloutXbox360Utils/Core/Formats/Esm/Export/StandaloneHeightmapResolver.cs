using System.Globalization;
using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

internal static class StandaloneHeightmapResolver
{
    public static List<DetectedVhgtHeightmap> GetUnresolvedHeightmaps(
        IReadOnlyList<DetectedVhgtHeightmap> heightmaps,
        IReadOnlyList<ExtractedLandRecord> landRecords)
    {
        var matches = Resolve(heightmaps, landRecords);
        return matches
            .Where(row => row.Status is not (StandaloneHeightmapStatus.OffsetLandMatch or
                StandaloneHeightmapStatus.ExactLandMatch))
            .Select(row => row.Heightmap)
            .ToList();
    }

    public static List<StandaloneHeightmapMatch> Resolve(
        IReadOnlyList<DetectedVhgtHeightmap> heightmaps,
        IReadOnlyList<ExtractedLandRecord> landRecords)
    {
        var candidates = BuildLandCandidates(landRecords);
        var rows = new List<StandaloneHeightmapMatch>(heightmaps.Count);

        for (var i = 0; i < heightmaps.Count; i++)
        {
            var heightmap = heightmaps[i];
            var offsetMatches = candidates
                .Where(candidate => SameVhgtOffset(heightmap, candidate.Heightmap))
                .ToList();

            if (offsetMatches.Count == 1)
            {
                rows.Add(new StandaloneHeightmapMatch(
                    i,
                    heightmap,
                    StandaloneHeightmapStatus.OffsetLandMatch,
                    offsetMatches[0].Land,
                    1,
                    [offsetMatches[0].Land]));
                continue;
            }

            var fingerprint = VhgtHeightmapFingerprint.From(heightmap);
            var exactMatches = candidates
                .Where(candidate => candidate.Fingerprint.Equals(fingerprint) &&
                                    SameHeightmap(heightmap, candidate.Heightmap))
                .ToList();

            if (exactMatches.Count == 1)
            {
                rows.Add(new StandaloneHeightmapMatch(
                    i,
                    heightmap,
                    StandaloneHeightmapStatus.ExactLandMatch,
                    exactMatches[0].Land,
                    1,
                    [exactMatches[0].Land]));
            }
            else if (exactMatches.Count > 1)
            {
                var nearest = exactMatches
                    .OrderBy(candidate => Math.Abs(candidate.Heightmap.Offset - heightmap.Offset))
                    .First();
                rows.Add(new StandaloneHeightmapMatch(
                    i,
                    heightmap,
                    StandaloneHeightmapStatus.AmbiguousLandMatch,
                    nearest.Land,
                    exactMatches.Count,
                    exactMatches.Select(candidate => candidate.Land).ToList()));
            }
            else
            {
                rows.Add(new StandaloneHeightmapMatch(
                    i,
                    heightmap,
                    StandaloneHeightmapStatus.Unresolved,
                    null,
                    0,
                    []));
            }
        }

        return rows;
    }

    public static void WriteCsv(
        string path,
        IReadOnlyList<StandaloneHeightmapMatch> matches,
        IReadOnlyDictionary<uint, string>? worldspaceNames = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");

        var csv = new StringBuilder();
        csv.AppendLine("Index,Offset,Endian,HeightOffset,Status,MatchedLandCount,MatchedLandFormID," +
                       "WorldspaceFormID,WorldspaceEditorID,ParentCellFormID,CellX,CellY,LandVhgtOffset," +
                       "CandidateLANDs");
        foreach (var match in matches)
        {
            var land = match.Land;
            var parsedHeightmap = land?.ParsedHeightmap ?? land?.Heightmap;
            csv.AppendLine(CultureInfo.InvariantCulture,
                $"{match.Index},0x{match.Heightmap.Offset:X},{(match.Heightmap.IsBigEndian ? "BE" : "LE")}," +
                $"{match.Heightmap.HeightOffset:R},{FormatStatus(match.Status)},{match.MatchedLandCount}," +
                $"{FormatNullableFormId(land?.Header.FormId)}," +
                $"{FormatNullableFormId(land?.WorldspaceFormId)}," +
                $"{Fmt.CsvEscape(GetWorldspaceEditorId(land?.WorldspaceFormId, worldspaceNames))}," +
                $"{FormatNullableFormId(land?.ParentCellFormId)}," +
                $"{FormatNullableInt(land?.BestCellX)}," +
                $"{FormatNullableInt(land?.BestCellY)}," +
                $"{FormatNullableOffset(parsedHeightmap?.Offset)}," +
                $"{Fmt.CsvEscape(FormatCandidateLands(match.CandidateLands, worldspaceNames))}");
        }

        File.WriteAllText(path, csv.ToString());
    }

    private static List<LandHeightmapCandidate> BuildLandCandidates(IReadOnlyList<ExtractedLandRecord> landRecords)
    {
        var candidates = new List<LandHeightmapCandidate>();
        foreach (var land in landRecords)
        {
            var parsedHeightmap = land.ParsedHeightmap ?? land.Heightmap;
            if (parsedHeightmap == null)
            {
                continue;
            }

            candidates.Add(new LandHeightmapCandidate(
                land,
                parsedHeightmap,
                VhgtHeightmapFingerprint.From(parsedHeightmap)));
        }

        return candidates;
    }

    private static bool SameHeightmap(DetectedVhgtHeightmap standalone, LandHeightmap land)
    {
        return BitConverter.SingleToUInt32Bits(standalone.HeightOffset) ==
               BitConverter.SingleToUInt32Bits(land.HeightOffset) &&
               standalone.HeightDeltas.SequenceEqual(land.HeightDeltas);
    }

    private static bool SameVhgtOffset(DetectedVhgtHeightmap standalone, LandHeightmap land)
    {
        return land.Offset == standalone.Offset ||
               land.Offset == standalone.Offset + EsmSubrecordUtils.SubrecordHeaderSize;
    }

    private static string FormatStatus(StandaloneHeightmapStatus status)
    {
        return status switch
        {
            StandaloneHeightmapStatus.OffsetLandMatch => "OffsetLAND",
            StandaloneHeightmapStatus.ExactLandMatch => "ExactLAND",
            StandaloneHeightmapStatus.AmbiguousLandMatch => "AmbiguousLAND",
            _ => "Unresolved"
        };
    }

    private static string FormatNullableFormId(uint? formId)
    {
        return formId.HasValue ? $"0x{formId.Value:X8}" : "";
    }

    private static string FormatNullableInt(int? value)
    {
        return value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : "";
    }

    private static string FormatNullableOffset(long? offset)
    {
        return offset.HasValue ? $"0x{offset.Value:X}" : "";
    }

    private static string FormatCandidateLands(
        IReadOnlyList<ExtractedLandRecord> lands,
        IReadOnlyDictionary<uint, string>? worldspaceNames)
    {
        if (lands.Count == 0)
        {
            return "";
        }

        return string.Join("; ", lands.Select(land =>
        {
            var resolvedWorldspace = GetWorldspaceEditorId(land.WorldspaceFormId, worldspaceNames);
            var worldspaceFormId = FormatNullableFormId(land.WorldspaceFormId);
            var worldspace = !string.IsNullOrWhiteSpace(resolvedWorldspace)
                ? resolvedWorldspace
                : !string.IsNullOrWhiteSpace(worldspaceFormId)
                    ? worldspaceFormId
                    : "ws_unknown";
            var cell = land.BestCellX.HasValue && land.BestCellY.HasValue
                ? $"({land.BestCellX.Value},{land.BestCellY.Value})"
                : "(?,?)";
            return $"0x{land.Header.FormId:X8}@{worldspace}{cell}";
        }));
    }

    private static string? GetWorldspaceEditorId(
        uint? worldspaceFormId,
        IReadOnlyDictionary<uint, string>? worldspaceNames)
    {
        if (worldspaceFormId is not uint id || worldspaceNames == null)
        {
            return null;
        }

        return worldspaceNames.TryGetValue(id, out var editorId) && !string.IsNullOrWhiteSpace(editorId)
            ? editorId
            : null;
    }

    private sealed record LandHeightmapCandidate(
        ExtractedLandRecord Land,
        LandHeightmap Heightmap,
        VhgtHeightmapFingerprint Fingerprint);
}

internal enum StandaloneHeightmapStatus
{
    Unresolved,
    OffsetLandMatch,
    ExactLandMatch,
    AmbiguousLandMatch
}

internal sealed record StandaloneHeightmapMatch(
    int Index,
    DetectedVhgtHeightmap Heightmap,
    StandaloneHeightmapStatus Status,
    ExtractedLandRecord? Land,
    int MatchedLandCount,
    IReadOnlyList<ExtractedLandRecord> CandidateLands);
