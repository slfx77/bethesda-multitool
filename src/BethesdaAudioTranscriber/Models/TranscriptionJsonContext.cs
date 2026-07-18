using System.Text.Json.Serialization;

namespace BethesdaAudioTranscriber.Models;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(TranscriptionProject))]
[JsonSerializable(typeof(ReviewFile))]
internal sealed partial class TranscriptionJsonContext : JsonSerializerContext;
