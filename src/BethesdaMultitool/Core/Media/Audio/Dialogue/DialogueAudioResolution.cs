namespace BethesdaMultitool.Core.Media.Audio.Dialogue;

/// <summary>Reports every remaining match without guessing between voices, files, or source layers.</summary>
/// <param name="Identity">The requested original response identity.</param>
/// <param name="Candidates">The matching entries in deterministic path/provenance order.</param>
public sealed record DialogueAudioResolution(DialogueAudioIdentity Identity, IReadOnlyList<DialogueAudioCandidate> Candidates)
{
    /// <summary>The result state inferred from the complete candidate set.</summary>
    public DialogueAudioMatchState State => Candidates.Count switch
    {
        0 => DialogueAudioMatchState.Missing,
        1 => DialogueAudioMatchState.Resolved,
        _ => DialogueAudioMatchState.Ambiguous
    };
}
