namespace FalloutXbox360Utils.Core.Formats.SaveGame;

/// <summary>
///     File format module for Fallout 3/NV save files (FO3SAVEGAME / .fxs / .fos).
/// </summary>
public sealed class SaveGameFormat : FileFormatBase
{
    public override string FormatId => "savegame";
    public override string DisplayName => "Save Game";
    public override string Extension => ".fxs";
    public override FileCategory Category => FileCategory.Xbox;
    public override string OutputFolder => "saves";
    public override int MinSize => 1024;
    public override int MaxSize => 50 * 1024 * 1024;
    public override bool EnableSignatureScanning => false; // STFS wrapping makes magic scanning unreliable

    public override IReadOnlyList<FormatSignature> Signatures { get; } =
    [
        new()
        {
            Id = "fo3savegame",
            MagicBytes = "FO3SAVEGAME"u8.ToArray(),
            Description = "Fallout 3/NV Save Game"
        }
    ];

    public override ParseResult? Parse(ReadOnlySpan<byte> data, int offset = 0)
    {
        // Try to find the FO3SAVEGAME magic
        var payloadOffset = SaveFileParser.FindPayloadOffset(data[offset..]);
        if (payloadOffset < 0)
        {
            return null;
        }

        var magicOffset = offset + payloadOffset;
        if (magicOffset + 15 >= data.Length)
        {
            return null;
        }

        // Header size field at offset +11 not needed for detection

        return new ParseResult
        {
            Format = "FO3SAVEGAME",
            EstimatedSize = data.Length - offset,
            FileName = "Savegame.dat"
        };
    }
}
