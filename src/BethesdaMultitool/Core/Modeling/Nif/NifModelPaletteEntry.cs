namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     The name an NiDefaultAVObjectPalette gives one NiAVObject block (nif.xml:10660-10675, AVObject 6165-6175): the
///     palette block, the Latin-1 reading of the stored SizedString and its raw bytes.
/// </summary>
/// <param name="PaletteBlock">The NiDefaultAVObjectPalette block that names the object.</param>
/// <param name="Name">The stored name as Latin-1 text (every byte kept).</param>
/// <param name="RawName">The stored name bytes.</param>
internal readonly record struct NifModelPaletteEntry(int PaletteBlock, string Name, ReadOnlyMemory<byte> RawName);
