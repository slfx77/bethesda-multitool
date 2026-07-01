namespace BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;

/// <summary>
///     One entry of a base record's <c>MODS</c> ("Alternate Textures") subrecord: it re-skins a
///     single named 3D object (NiTriShape) of the base's <c>MODL</c> mesh by pointing it at a
///     <see cref="TextureSetRecord" /> (TXST) instead of the mesh's baked textures. This is how
///     Fallout 3/NV reuses one shared NIF for many props — e.g. every billboard is
///     <c>clutter\billboards\BillboardTallNV.NIF</c> with its <c>BB04:13</c> shape swapped to a
///     per-vendor ad TXST.
///     <para>
///         xEdit <c>wbAlternateTexture</c> (wbDefinitionsCommon.pas): a length-prefixed
///         "3D Name", a TXST FormID ("New Texture"), and a signed "3D Index".
///     </para>
/// </summary>
public readonly record struct AlternateTextureEntry(string ShapeName, uint TextureSetFormId, int Index);
