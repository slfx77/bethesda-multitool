namespace FalloutXbox360Utils.Core.Formats.Nif.Skinning;

/// <summary>
///     Information about how a NiSkinPartition block needs bone weights/indices expansion.
/// </summary>
internal sealed class SkinPartitionExpansion
{
    public int BlockIndex { get; set; }
    public int OriginalSize { get; set; }
    public int NewSize { get; set; }
    public int SizeIncrease => NewSize - OriginalSize;

    /// <summary>Parsed skin partition data for writing expanded block.</summary>
    public required NifSkinPartitionExpander.SkinPartitionData ParsedData { get; set; }
}
