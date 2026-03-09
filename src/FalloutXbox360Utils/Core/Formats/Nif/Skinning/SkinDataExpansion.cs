namespace FalloutXbox360Utils.Core.Formats.Nif.Skinning;

/// <summary>
///     Information about how a NiSkinData block needs vertex weight expansion.
/// </summary>
internal sealed class SkinDataExpansion
{
    public int BlockIndex { get; set; }
    public int OriginalSize { get; set; }
    public int NewSize { get; set; }
    public int SizeIncrease => NewSize - OriginalSize;

    /// <summary>Parsed skin data for writing expanded block.</summary>
    public required NifSkinDataExpander.ParsedSkinData ParsedData { get; set; }
}
