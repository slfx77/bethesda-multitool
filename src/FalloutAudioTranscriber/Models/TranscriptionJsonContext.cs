using System.Text.Json.Serialization;

namespace FalloutAudioTranscriber.Models;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(TranscriptionProject))]
internal sealed partial class TranscriptionJsonContext : JsonSerializerContext;
