using System.Reflection;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.AssetPacking;

/// <summary>
///     One asset path discovered on a parsed record, tracked back to its source object and
///     property. Used by <see cref="AssetPathRewriter" /> to mutate the record field when
///     the v21 fuzzy resolver decides the path should be remapped to a differently-named
///     asset that exists in an indexed Data folder.
/// </summary>
internal sealed record AssetPathReference
{
    /// <summary>
    ///     The record object that owns the path field — could be a top-level record
    ///     (e.g., <c>WeaponRecord</c>) or a nested sub-object discovered via reflection
    ///     (e.g., a <c>WeaponModelVariant</c> inside a weapon).
    /// </summary>
    public required object Owner { get; init; }

    /// <summary>
    ///     The <see cref="PropertyInfo" /> that returned this path value. Used to write
    ///     a remapped path back via <see cref="PropertyInfo.SetValue(object, object)" />.
    /// </summary>
    public required PropertyInfo Property { get; init; }

    /// <summary>
    ///     The raw path string as stored on the record, before normalization. Preserves
    ///     the field's original prefix-style (e.g., relative <c>armor\test.nif</c> vs.
    ///     fully-qualified <c>meshes\armor\test.nif</c>) so the rewriter can write the
    ///     new path back in the same style.
    /// </summary>
    public required string OriginalRawPath { get; init; }

    /// <summary>
    ///     The canonical normalized path (lowercase, backslash, full <c>meshes\</c> /
    ///     <c>textures\</c> / <c>sound\</c> prefix). Matches the lookup key used by
    ///     <see cref="DataFolderResolver" />.
    /// </summary>
    public required string NormalizedPath { get; init; }
}
