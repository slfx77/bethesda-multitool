using BethesdaMultitool.Core.Formats.Nif.Rendering.Scene;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Water;

/// <summary>
///     Owns placement-transformed NIF water together with the reference that contributed it. The
///     published list is rebuilt in place when reference visibility changes, which keeps its identity
///     stable for renderer consumers while removing hidden owners immediately.
/// </summary>
internal sealed class PlacedNifWaterRegistry
{
    private readonly List<Entry> _entries = [];
    private readonly HashSet<uint> _registeredOwners = [];
    private readonly List<NifWaterGeometry> _published = [];
    private ReferenceVisibilityKey _publishedVisibility;
    private bool _publishedValid;

    internal bool ContainsOwner(uint formId) => _registeredOwners.Contains(formId);

    /// <summary>
    ///     Registers one reference exactly once. Empty geometry is retained as a completed registration
    ///     so malformed transforms are not retried on every frame.
    /// </summary>
    internal bool Register(
        in RenderableReference owner,
        IReadOnlyList<NifWaterGeometry> placedGeometry)
    {
        ArgumentNullException.ThrowIfNull(placedGeometry);
        if (!_registeredOwners.Add(owner.FormId)) return false;

        var geometry = new NifWaterGeometry[placedGeometry.Count];
        for (var i = 0; i < placedGeometry.Count; i++)
        {
            geometry[i] = placedGeometry[i];
        }

        _entries.Add(new Entry(owner, geometry));
        _publishedValid = false;
        return true;
    }

    /// <summary>
    ///     Returns the stable published list filtered by the current reference visibility snapshot.
    ///     The global Meshes parent is intentionally not an input: authored water is a water-layer
    ///     contribution once discovered, matching the host's independent water pass.
    /// </summary>
    internal IReadOnlyList<NifWaterGeometry> GetPublished(
        in ReferenceVisibilityKey visibility,
        ReferenceEnabledOverrideStore enabledOverrides)
    {
        ArgumentNullException.ThrowIfNull(enabledOverrides);
        if (_publishedValid && visibility == _publishedVisibility) return _published;

        _published.Clear();
        foreach (var entry in _entries)
        {
            if (!visibility.IsVisible(entry.Owner, enabledOverrides)) continue;
            _published.AddRange(entry.Geometry);
        }

        _publishedVisibility = visibility;
        _publishedValid = true;
        return _published;
    }

    internal void Clear()
    {
        _entries.Clear();
        _registeredOwners.Clear();
        _published.Clear();
        _publishedValid = false;
    }

    private readonly record struct Entry(
        RenderableReference Owner,
        NifWaterGeometry[] Geometry);
}
