namespace FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Misc;

/// <summary>
///     Caravan Deck (CDCK) record. A pre-built deck of caravan cards.
///     PDB struct: TESCaravanDeck (60 bytes, FormType 0x75).
/// </summary>
public record CaravanDeckRecord
{
    public uint FormId { get; init; }
    public string? EditorId { get; init; }

    /// <summary>Number of cards listed in the deck (CNTO subrecord count).</summary>
    public int CardCount { get; init; }

    /// <summary>Number of jokers in the deck (DATA byte at +52 / a count uint32).</summary>
    public uint JokerCount { get; init; }

    public long Offset { get; init; }
    public bool IsBigEndian { get; init; }
}
