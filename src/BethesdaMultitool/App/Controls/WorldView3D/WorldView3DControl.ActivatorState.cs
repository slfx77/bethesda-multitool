using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Lighting;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Scene;

namespace BethesdaMultitool;

public sealed partial class WorldView3DControl
{
    /// <summary>Returns the non-persistent Enabled preview for one placed FormID.</summary>
    internal ReferenceEnabledOverride GetReferenceEnabledOverride(uint formId) =>
        _referenceEnabledOverrides.Get(formId);

    /// <summary>
    ///     Whether the selected placement owns output this viewer can preview: a supported static
    ///     reference mesh (including any embedded water) or a valid placed-LIGH emitter. Actor refs,
    ///     missing-model ordinary refs, and malformed placements do not get a success-looking no-op UI.
    /// </summary>
    internal bool CanPreviewReferenceVisibility(PlacedReference reference) =>
        ReferencePreviewEligibility.CanPreview(reference, _data);

    /// <summary>Raised after the scene-wide recovery action restores every per-reference preview.</summary>
    public event EventHandler? ReferenceEnabledOverridesReset;

    /// <summary>
    ///     Returns the resolved authored placement state used by reference meshes: the REFR's own
    ///     flag or the already-resolved XESP parent chain. A base LIGH's Off By Default bit governs
    ///     its emitter separately and must not be used to claim an attached lantern/model is hidden.
    ///     XSRF Imposter refs are NOT authored-hidden — the
    ///     2026-08-10 census showed FNV's imposter population is the retail-rendered Vegas skyline
    ///     set, not vantage-only content. This intentionally ignores UI preview overrides.
    /// </summary>
    internal bool IsReferenceAuthoredEnabled(PlacedReference reference) =>
        ReferencePreviewEligibility.IsAuthoredEnabled(reference, _data);

    /// <summary>
    ///     Returns the base LIGH emitter's independent authored state, or <c>null</c> when the
    ///     selected placement is not backed by a parsed LIGH record.
    /// </summary>
    internal bool? IsReferenceBaseLightAuthoredEnabled(PlacedReference reference) =>
        ReferencePreviewEligibility.IsBaseLightAuthoredEnabled(reference, _data);

    /// <summary>
    ///     Sets a session-only per-instance Enabled preview. <see cref="ReferenceEnabledOverride.Authored" />
    ///     removes the override. The parsed placement and shared ACTI base record remain untouched.
    /// </summary>
    internal void SetReferenceEnabledOverride(uint formId, ReferenceEnabledOverride value)
    {
        _referenceEnabledOverrides.Set(formId, value);
        RefreshReferenceOverrideRecoveryUi();
        // The main renderer keys its cull cache on the store version. The collision overlay builds a
        // fresh eligible instance list each frame, so both paths observe this change without rebaking.
    }

    /// <summary>
    ///     Restores authored visibility for every per-reference preview in the current scene. This is
    ///     the recovery path for references which cannot be picked after they are explicitly hidden.
    /// </summary>
    private void ClearReferenceEnabledOverrides()
    {
        if (_referenceEnabledOverrides.Count == 0) return;

        _referenceEnabledOverrides.Clear();
        RefreshReferenceOverrideRecoveryUi();
        ReferenceEnabledOverridesReset?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshReferenceOverrideRecoveryUi()
    {
        var count = _referenceEnabledOverrides.Count;
        SettingsPanel.ResetReferenceOverridesButton.IsEnabled = count > 0;
        SettingsPanel.ResetReferenceOverridesButton.Content = count switch
        {
            0 => "Reset per-object previews",
            1 => "Reset 1 per-object preview",
            _ => $"Reset {count} per-object previews"
        };
    }
}
