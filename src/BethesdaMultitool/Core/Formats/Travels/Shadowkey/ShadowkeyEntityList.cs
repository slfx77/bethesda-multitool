namespace BethesdaMultitool.Core.Formats.Travels.Shadowkey;

/// <summary>
///     The placements of one zone, from a <c>.ent</c> file or from the single <c>.sta</c> file.
///     <para>
///         Both files are the same record with the same field order; <c>.sta</c> simply stops after
///         the first 32 bytes, before the two name fields, so <see cref="HasNames" /> says whether
///         <see cref="ShadowkeyEntity.Name" /> and <see cref="ShadowkeyEntity.Script" /> carry
///         anything. Only <c>azra.sta</c> exists (201 records, 6,436 bytes) and it is dated two
///         months before <c>azra.ent</c>: 188 of its 201 records are byte-identical to the first 32
///         bytes of some <c>azra.ent</c> record and the first 14 even coincide positionally, so the
///         reading that fits is an older revision of the same placement list from before the record
///         grew its names — not a save file. One file cannot settle that, so it stays a hypothesis.
///     </para>
/// </summary>
/// <param name="Entities">The placements, in file order.</param>
/// <param name="HasNames">
///     <see langword="true" /> for a <c>.ent</c>; <see langword="false" /> for a <c>.sta</c>, whose
///     records end before the name and script fields.
/// </param>
internal sealed record ShadowkeyEntityList(IReadOnlyList<ShadowkeyEntity> Entities, bool HasNames);
