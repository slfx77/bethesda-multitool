namespace BethesdaMultitool.Core.Media.Audio.Dialogue;

/// <summary>Describes whether an explicit voice identity identifies exactly one available audio entry.</summary>
public enum DialogueAudioMatchState
{
    /// <summary>No matching audio entry is available in the supplied catalog.</summary>
    Missing,
    /// <summary>Exactly one matching entry is available.</summary>
    Resolved,
    /// <summary>Several matching entries remain; the caller must present their identities.</summary>
    Ambiguous
}
