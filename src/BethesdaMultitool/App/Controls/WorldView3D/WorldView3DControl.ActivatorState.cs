using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Scene;

namespace BethesdaMultitool;

public sealed partial class WorldView3DControl
{
    /// <summary>Returns the non-persistent Enabled preview for one placed FormID.</summary>
    internal ReferenceEnabledOverride GetReferenceEnabledOverride(uint formId) =>
        _referenceEnabledOverrides.Get(formId);

    /// <summary>
    ///     Returns the resolved authored initial state used by the renderer: the REFR's own flag OR
    ///     the already-resolved XESP parent chain. This intentionally ignores UI preview overrides.
    /// </summary>
    internal bool IsReferenceAuthoredEnabled(PlacedReference reference) =>
        !reference.IsInitiallyDisabled &&
        (_data?.XespDisabledRefs.Contains(reference.FormId) != true);

    /// <summary>
    ///     Sets a session-only per-instance Enabled preview. <see cref="ReferenceEnabledOverride.Authored" />
    ///     removes the override. The parsed placement and shared ACTI base record remain untouched.
    /// </summary>
    internal void SetReferenceEnabledOverride(uint formId, ReferenceEnabledOverride value)
    {
        _referenceEnabledOverrides.Set(formId, value);
        // The main renderer keys its cull cache on the store version. The collision overlay builds a
        // fresh eligible instance list each frame, so both paths observe this change without rebaking.
    }
}
