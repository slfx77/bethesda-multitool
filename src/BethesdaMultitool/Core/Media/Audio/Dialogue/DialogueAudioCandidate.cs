using BethesdaMultitool.Core.Vfs;

namespace BethesdaMultitool.Core.Media.Audio.Dialogue;

/// <summary>Retains an audio path, its actual catalog provenance, and available same-stem LIP companions.</summary>
/// <param name="Plugin">The plugin directory identity.</param>
/// <param name="VoiceType">The voice-type directory identity.</param>
/// <param name="OriginalInfoFormId">The INFO identity encoded in the source filename.</param>
/// <param name="ResponseNumber">The response number encoded in the source filename.</param>
/// <param name="Audio">The audio entry; availability does not imply codec support.</param>
/// <param name="LipCompanions">Matching path-stem companions with their own source provenance.</param>
public sealed record DialogueAudioCandidate(string Plugin, string VoiceType, uint OriginalInfoFormId,
    byte ResponseNumber, GameFileEntry Audio, IReadOnlyList<GameFileEntry> LipCompanions);
