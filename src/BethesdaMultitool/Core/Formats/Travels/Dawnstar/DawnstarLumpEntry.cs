namespace BethesdaMultitool.Core.Formats.Travels.Dawnstar;

/// <summary>
///     One directory record of a Dawnstar <c>.lmp</c> lump: <c>-name-</c> followed by a big-endian
///     u32 offset and u16 length.
/// </summary>
/// <param name="Name">
///     The member name as written between the dashes — printable ASCII, always carrying an
///     extension, and matched CASE-SENSITIVELY by the engine's lookup.
/// </param>
/// <param name="Offset">Absolute byte position of the payload from the start of the lump file.</param>
/// <param name="Length">
///     Payload byte length. It is a u16, so a member is at most 65,535 bytes (largest on retail:
///     10,821).
/// </param>
/// <param name="DirectoryIndex">Position in the directory; payloads follow in this order.</param>
internal sealed record DawnstarLumpEntry(string Name, int Offset, int Length, int DirectoryIndex);
