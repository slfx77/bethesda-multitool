using BethesdaMultitool.Core.Vfs;

namespace BethesdaMultitool.Core.Media.Audio.Dialogue;

public sealed record AudioCatalogResponse(byte Number, string? Text);
public sealed record AudioCatalogInfo(string Plugin, uint FormId, string Source, long Offset,
    IReadOnlyList<AudioCatalogResponse> Responses);
public sealed record AudioCatalogMatch(string Source, long Offset, string? Text);
public sealed record AudioCatalogRow(string Path, string Source, string? Plugin, string? VoiceType,
    uint? OriginalInfoFormId, byte? ResponseNumber, string State, bool AmbiguousAudio,
    IReadOnlyList<AudioCatalogMatch> Matches, IReadOnlyList<string> LipPaths);

/// <summary>Joins archive identities to explicit original plugin records, preserving orphan entries.</summary>
public static class DialogueAudioCatalog
{
    public static IReadOnlyList<AudioCatalogRow> Build(IEnumerable<GameFileEntry> assets,
        IEnumerable<AudioCatalogInfo> records, IEnumerable<string> suppliedPlugins,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entries = assets.ToArray();
        var index = DialogueAudioIndex.Create(entries, cancellationToken);
        var parsed = index.Entries.ToArray();
        var parsedPaths = parsed.Select(entry => (entry.Audio.Path, entry.Audio.Source)).ToHashSet();
        var plugins = suppliedPlugins.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var infos = records.GroupBy(record => Key(record.Plugin, record.FormId))
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var rows = new List<AudioCatalogRow>();
        foreach (var entry in parsed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var hasInfo = infos.TryGetValue(Key(entry.Plugin, entry.OriginalInfoFormId), out var candidates);
            var matches = (candidates ?? []).SelectMany(info => info.Responses
                .Where(response => response.Number == entry.ResponseNumber)
                .Select(response => new AudioCatalogMatch(info.Source, info.Offset, response.Text)))
                .Distinct().OrderBy(match => match.Source, StringComparer.Ordinal).ThenBy(match => match.Offset).ToArray();
            var audioCount = index.Resolve(new DialogueAudioIdentity(entry.Plugin, entry.OriginalInfoFormId,
                entry.ResponseNumber, entry.VoiceType)).Candidates.Count;
            var state = matches.Length > 1 ? "ambiguous" : matches.Length == 1 ? "matched"
                : hasInfo ? "unmatched-response" : plugins.Contains(entry.Plugin) ? "unmatched-info" : "unavailable-plugin";
            rows.Add(new(entry.Audio.Path, entry.Audio.Source, entry.Plugin, entry.VoiceType,
                entry.OriginalInfoFormId, entry.ResponseNumber, state, audioCount > 1, matches,
                entry.LipCompanions.Select(lip => $"{lip.Source}:{lip.Path}").ToArray()));
        }
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (parsedPaths.Contains((entry.Path, entry.Source)) ||
                !entry.Path.Replace('\\', '/').StartsWith("sound/voice/", StringComparison.OrdinalIgnoreCase) ||
                !DialogueAudioIndex.IsAudioExtension(Path.GetExtension(entry.Path))) continue;
            rows.Add(new(entry.Path, entry.Source, null, null, null, null, "unparsed-name", false, [], []));
        }
        return rows.OrderBy(row => row.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Source, StringComparer.Ordinal).ToArray();
    }

    private static string Key(string plugin, uint formId) => $"{plugin}/{formId & 0x00FFFFFF:X6}";
}
