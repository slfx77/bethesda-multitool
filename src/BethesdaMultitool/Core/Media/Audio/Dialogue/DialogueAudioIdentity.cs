namespace BethesdaMultitool.Core.Media.Audio.Dialogue;

/// <summary>Identifies an original Fallout 3/New Vegas voice response independently of allocated output records.</summary>
/// <param name="Plugin">The originating plugin filename from the voice directory.</param>
/// <param name="OriginalInfoFormId">The original INFO identity, before conversion allocation.</param>
/// <param name="ResponseNumber">The original TRDT response number.</param>
/// <param name="VoiceType">An exact voice-type EditorID, or null to retain all voice candidates.</param>
/// <param name="Provenance">An exact source-layer identity, or null to retain available source layers.</param>
public sealed record DialogueAudioIdentity(string Plugin, uint OriginalInfoFormId, byte ResponseNumber,
    string? VoiceType = null, string? Provenance = null);
