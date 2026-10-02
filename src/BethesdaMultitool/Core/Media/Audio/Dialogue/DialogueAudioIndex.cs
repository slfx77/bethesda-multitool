using System.Globalization;
using BethesdaMultitool.Core.Vfs;
using Slfx77.Multitool.Core.Assets;

namespace BethesdaMultitool.Core.Media.Audio.Dialogue;

/// <summary>Indexes Fallout voice directories by plugin, original INFO, response, voice type, and provenance.</summary>
public sealed class DialogueAudioIndex
{
    private readonly Dictionary<string, List<DialogueAudioCandidate>> _entries;

    /// <summary>Retains a completed immutable catalog index without owning the source filesystem.</summary>
    private DialogueAudioIndex(Dictionary<string, List<DialogueAudioCandidate>> entries) => _entries = entries;

    public IEnumerable<DialogueAudioCandidate> Entries => _entries.Values.SelectMany(values => values)
        .OrderBy(entry => entry.Audio.Path, StringComparer.OrdinalIgnoreCase)
        .ThenBy(entry => entry.Audio.Source, StringComparer.Ordinal);

    /// <summary>Builds a reusable metadata index without opening or decoding any audio payload.</summary>
    /// <param name="entries">The complete source catalog or its sound/voice subtree, with existing precedence applied.</param>
    /// <param name="cancellationToken">Cancels enumeration and index construction.</param>
    /// <returns>An index retaining alternate voices, filename stems, and source provenance.</returns>
    /// <exception cref="InvalidDataException">More than 500,000 voice entries are supplied.</exception>
    public static DialogueAudioIndex Create(IEnumerable<GameFileEntry> entries, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        cancellationToken.ThrowIfCancellationRequested();
        var audio = new List<DialogueAudioCandidate>();
        var lips = new Dictionary<string, List<GameFileEntry>>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<(string Path, string Source)>();
        var count = 0;
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = AssetPath.Normalize(entry.Path);
            var parts = path.Split('/');
            if (parts.Length != 5 || !parts[0].Equals("sound", StringComparison.OrdinalIgnoreCase) ||
                !parts[1].Equals("voice", StringComparison.OrdinalIgnoreCase)) { continue; }
            var extension = Path.GetExtension(parts[4]);
            if (!IsAudioExtension(extension) && !extension.Equals(".lip", StringComparison.OrdinalIgnoreCase)) { continue; }
            var stem = Path.GetFileNameWithoutExtension(parts[4]);
            var responseSeparator = stem.LastIndexOf('_');
            var formSeparator = responseSeparator > 0 ? stem.LastIndexOf('_', responseSeparator - 1) : -1;
            if (formSeparator < 0 || responseSeparator - formSeparator != 9 ||
                !uint.TryParse(stem.AsSpan(formSeparator + 1, 8), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var formId) ||
                !byte.TryParse(stem.AsSpan(responseSeparator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var response)) { continue; }
            if (!seen.Add((path.ToUpperInvariant(), entry.Source))) { continue; }
            if (++count > 500000) { throw new InvalidDataException("Voice catalog exceeds the 500,000-entry limit."); }
            if (extension.Equals(".lip", StringComparison.OrdinalIgnoreCase))
            {
                var key = Path.ChangeExtension(path, null);
                if (!lips.TryGetValue(key, out var companions))
                {
                    companions = [];
                    lips.Add(key, companions);
                }
                companions.Add(entry);
            }
            else { audio.Add(new DialogueAudioCandidate(parts[2], parts[3], formId, response, entry, [])); }
        }
        var index = new Dictionary<string, List<DialogueAudioCandidate>>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in audio)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = Key(candidate.Plugin, candidate.OriginalInfoFormId, candidate.ResponseNumber);
            if (!index.TryGetValue(key, out var candidates))
            {
                candidates = [];
                index.Add(key, candidates);
            }
            var stem = Path.ChangeExtension(AssetPath.Normalize(candidate.Audio.Path), null);
            candidates.Add(candidate with { LipCompanions = lips.TryGetValue(stem, out var companions) ? companions.ToArray() : [] });
        }
        return new DialogueAudioIndex(index);
    }

    /// <summary>Resolves one original response, retaining ambiguity when voice type or provenance is unspecified.</summary>
    /// <param name="identity">The explicit plugin and original response identity.</param>
    /// <returns>A missing, resolved, or ambiguous result; candidates are never ranked by guessed text or stems.</returns>
    public DialogueAudioResolution Resolve(DialogueAudioIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.Plugin);
        if (identity.Plugin.IndexOfAny(['/', '\\']) >= 0)
        {
            throw new ArgumentException("The plugin identity must be a filename, not a filesystem path.", nameof(identity));
        }
        var candidates = _entries.GetValueOrDefault(Key(identity.Plugin, identity.OriginalInfoFormId, identity.ResponseNumber));
        return new DialogueAudioResolution(identity, candidates?.Where(candidate =>
                (string.IsNullOrWhiteSpace(identity.VoiceType) || candidate.VoiceType.Equals(identity.VoiceType, StringComparison.OrdinalIgnoreCase)) &&
                (identity.Provenance is null || candidate.Audio.Source.Equals(identity.Provenance, StringComparison.Ordinal)))
            .OrderBy(candidate => candidate.Audio.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.Audio.Source, StringComparer.Ordinal).ToArray() ?? []);
    }

    /// <summary>Recognizes Fallout voice audio filenames independently of whether their codec can be played.</summary>
    internal static bool IsAudioExtension(string extension)
        => extension.Equals(".wav", StringComparison.OrdinalIgnoreCase) || extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase) ||
           extension.Equals(".ogg", StringComparison.OrdinalIgnoreCase) || extension.Equals(".xma", StringComparison.OrdinalIgnoreCase);

    /// <summary>Uses the plugin-local 24-bit identity, matching the existing engine voice path builder.</summary>
    private static string Key(string plugin, uint formId, byte response)
        => string.Create(CultureInfo.InvariantCulture, $"{plugin}/{formId & 0x00FFFFFF:X8}/{response}");
}
