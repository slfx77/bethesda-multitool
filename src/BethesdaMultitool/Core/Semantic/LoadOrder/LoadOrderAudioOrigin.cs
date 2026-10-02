using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;

namespace BethesdaMultitool.Core.Semantic.LoadOrder;

/// <summary>File-local voice identity, kept separate from the selected display namespace.</summary>
internal sealed record LoadOrderAudioOrigin(uint InfoFormId, string? RecordPath, bool UsePluginHeader)
{
    /// <summary>Preserves captured origin IDs; otherwise resolves the unique physical INFO winner.</summary>
    internal static LoadOrderAudioOrigin Resolve(DialogueRecord info, LoadOrderRecordIndex? index,
        string? primaryPath, bool isPlugin)
    {
        if (info.AudioSourceInfoFormId is { } original)
        {
            return new(original, null, false);
        }
        if (index is null) { return new(info.FormId, primaryPath, isPlugin); }
        if (index.Records.TryGetValue(info.FormId, out var identity) &&
            !identity.DeletedByWinner && !identity.TypeConflict && !identity.HasAmbiguousWinningRecords &&
            identity.Winner.Signature == "INFO")
        {
            return new(identity.Winner.FileLocalFormId, identity.Winner.FilePath, true);
        }
        // No physical source is established. Require explicit origin selection in the audio pane.
        return new(info.FormId, null, false);
    }
}
