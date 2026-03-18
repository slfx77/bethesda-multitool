namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering;

/// <summary>
///     Result of rendering a NIF model to a sprite.
/// </summary>
internal sealed class SpriteResult
{
    /// <summary>RGBA pixel data (length = Width * Height * 4).</summary>
    public required byte[] Pixels { get; init; }

    public required int Width { get; init; }
    public required int Height { get; init; }

    /// <summary>Original model width in game units.</summary>
    public required float BoundsWidth { get; init; }

    /// <summary>Original model height in game units.</summary>
    public required float BoundsHeight { get; init; }

    /// <summary>Whether at least one submesh was texture-mapped.</summary>
    public bool HasTexture { get; init; }
}
