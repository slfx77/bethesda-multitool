namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;

/// <summary>
///     Session-only preview state for one placed reference. <see cref="Authored" /> follows the
///     REFR's own Initially Disabled flag plus its resolved XESP enable-parent chain;
///     <see cref="On" /> and <see cref="Off" /> explicitly override that initial state.
/// </summary>
internal enum ReferenceEnabledOverride
{
    Authored = 0,
    On = 1,
    Off = 2,
}

/// <summary>
///     Pure visibility policy shared by the main reference renderer, picking, and editor overlays.
///     The global "show initially disabled" diagnostic affects only the authored state. An explicit
///     per-instance override wins, while independent layer/category filters remain the caller's job.
/// </summary>
internal static class ReferenceEnabledStatePolicy
{
    internal static bool IsVisible(
        bool isAuthoredDisabled,
        bool showInitiallyDisabled,
        ReferenceEnabledOverride enabledOverride) => enabledOverride switch
    {
        ReferenceEnabledOverride.On => true,
        ReferenceEnabledOverride.Off => false,
        _ => !isAuthoredDisabled || showInitiallyDisabled,
    };
}

/// <summary>
///     Scene-local per-FormID override table. Authored is represented by absence, so resetting one
///     instance cannot mutate the parsed <c>PlacedReference</c>, its base ACTI record, or sibling
///     placements that share that base. <see cref="Version" /> lets render caches invalidate without
///     subscribing a UI-owned callback.
/// </summary>
internal sealed class ReferenceEnabledOverrideStore
{
    private readonly Dictionary<uint, ReferenceEnabledOverride> _overrides = [];

    internal int Version { get; private set; }
    internal int Count => _overrides.Count;

    internal ReferenceEnabledOverride Get(uint formId) =>
        _overrides.TryGetValue(formId, out var value) ? value : ReferenceEnabledOverride.Authored;

    internal void Set(uint formId, ReferenceEnabledOverride value)
    {
        if (value == ReferenceEnabledOverride.Authored)
        {
            if (_overrides.Remove(formId)) Version++;
            return;
        }

        if (_overrides.TryGetValue(formId, out var current) && current == value) return;
        _overrides[formId] = value;
        Version++;
    }

    internal bool IsVisible(uint formId, bool isAuthoredDisabled, bool showInitiallyDisabled) =>
        ReferenceEnabledStatePolicy.IsVisible(isAuthoredDisabled, showInitiallyDisabled, Get(formId));

    internal void Clear()
    {
        if (_overrides.Count == 0) return;
        _overrides.Clear();
        Version++;
    }
}
