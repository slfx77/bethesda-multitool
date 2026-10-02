namespace BethesdaMultitool.Core.Formats.Nif.Decoding;

/// <summary>
///     One decoded field of a <see cref="NifStructValue" />. nif.xml repeats field names (the uint and ushort
///     NiAVObject "Flags", the two NiGeometryData "Num Vertices", the version-split NiSourceTexture "File Name"), so a
///     field is identified by its name plus <see cref="Ordinal" />: how many fields of the same name were decoded
///     before it in the same struct (almost always 0, because version and condition filtering usually leave one).
/// </summary>
/// <param name="Name">The nif.xml field name.</param>
/// <param name="Ordinal">The count of earlier decoded fields with the same name in this struct.</param>
/// <param name="DeclarationIndex">
///     The field's position in the definition's field list (inherited fields first for blocks), which tells the
///     duplicate declarations apart.
/// </param>
/// <param name="Value">The decoded value.</param>
internal sealed record NifField(string Name, int Ordinal, int DeclarationIndex, NifValue Value);
