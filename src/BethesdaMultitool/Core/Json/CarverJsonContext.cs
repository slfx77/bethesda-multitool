using System.Text.Json.Serialization;
using BethesdaMultitool.Core.Carving;

namespace BethesdaMultitool.Core.Json;

/// <summary>
///     Source-generated JSON serializer context for trim-compatible serialization.
///     This avoids reflection-based serialization that breaks with IL trimming.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(List<CarveEntry>))]
[JsonSerializable(typeof(CarveEntry))]
[JsonSerializable(typeof(JsonAnalysisResult))]
[JsonSerializable(typeof(Dictionary<string, object>))]
internal partial class CarverJsonContext : JsonSerializerContext;
