using System.Text.Json.Serialization;

namespace FalloutXbox360Utils.CLI;

internal sealed class SpriteIndexEntry
{
    public required string File { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required float BoundsWidth { get; init; }
    public required float BoundsHeight { get; init; }
    public bool HasTexture { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, BaseRecordValue>? BaseRecords { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string?>? Refs { get; set; }
}