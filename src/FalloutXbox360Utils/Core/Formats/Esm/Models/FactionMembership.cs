namespace FalloutXbox360Utils.Core.Formats.Esm.Models;

/// <summary>
///     Faction membership information from SNAM subrecord.
/// </summary>
public record FactionMembership(uint FactionFormId, sbyte Rank);
