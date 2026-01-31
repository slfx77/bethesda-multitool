namespace FalloutXbox360Utils.Core.Formats.EsmRecord.Models;

/// <summary>
///     Faction relation data from XNAM subrecord.
/// </summary>
public record FactionRelation(uint FactionFormId, int Modifier, uint CombatFlags);
