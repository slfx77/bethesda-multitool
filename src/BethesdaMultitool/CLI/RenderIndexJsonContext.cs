using System.Text.Json.Serialization;

namespace BethesdaMultitool.CLI;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(SortedDictionary<string, SpriteIndexEntry>))]
[JsonSerializable(typeof(Dictionary<string, BaseRecordValue>))]
[JsonSerializable(typeof(Dictionary<string, string?>))]
internal sealed partial class RenderIndexJsonContext : JsonSerializerContext;
