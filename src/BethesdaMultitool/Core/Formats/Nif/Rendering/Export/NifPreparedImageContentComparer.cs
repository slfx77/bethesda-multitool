namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Export;

/// <summary>Treats two prepared images as one image exactly when their encoded PNG bytes are identical.</summary>
/// <remarks>
///     The display name is deliberately ignored in both directions: equal names never alias different bytes, and
///     identical bytes share one stored image whatever their names. Identical bytes arise when a material cache miss
///     re-prepares textures another material already holds, for example a part that differs only by its glow map; the
///     native writer's glTF library stores such images once as well.
/// </remarks>
internal sealed class NifPreparedImageContentComparer : IEqualityComparer<NifPreparedImage>
{
    /// <summary>The shared stateless instance.</summary>
    public static NifPreparedImageContentComparer Instance { get; } = new();

    private NifPreparedImageContentComparer()
    {
    }

    /// <inheritdoc />
    public bool Equals(NifPreparedImage? x, NifPreparedImage? y)
    {
        if (ReferenceEquals(x, y)) return true;
        if (x is null || y is null) return false;
        return x.Png.AsSpan().SequenceEqual(y.Png);
    }

    /// <inheritdoc />
    public int GetHashCode(NifPreparedImage obj)
    {
        ArgumentNullException.ThrowIfNull(obj);
        var hash = new HashCode();
        hash.AddBytes(obj.Png);
        return hash.ToHashCode();
    }
}
