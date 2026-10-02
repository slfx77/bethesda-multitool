namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;

/// <summary>
///     The fields an NiUVController stores after its NiTimeController header (nif.xml: Texture Set ushort, Data ref to
///     NiUVData), exactly as stored. Read by <see cref="NifPropertyControllerReader.TryReadUvControllerView" />.
/// </summary>
/// <param name="TextureSet">The stored Texture Set word (the texture-coordinate set the controller animates).</param>
/// <param name="DataRef">The Data ref as stored (-1 for none).</param>
internal readonly record struct NifUvControllerView(ushort TextureSet, int DataRef);
