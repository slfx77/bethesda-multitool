namespace BethesdaAudioTranscriber.Models;

/// <summary>The actual local model file loaded for a transcription, independent of its suggested filename.</summary>
public sealed record WhisperModelIdentity(string Name, string Path, string Sha256, long Bytes);
