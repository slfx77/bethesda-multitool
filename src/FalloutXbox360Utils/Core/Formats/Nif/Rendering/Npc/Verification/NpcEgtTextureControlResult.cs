namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Verification;

internal sealed record NpcEgtTextureControlResult
{
    public required string Source { get; init; }
    public required string Name { get; init; }
    public double Value { get; init; }
}