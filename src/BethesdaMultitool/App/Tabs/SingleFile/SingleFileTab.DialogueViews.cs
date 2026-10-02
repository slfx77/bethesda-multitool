namespace BethesdaMultitool;

/// <summary>Connects optional diagram links to the current record browser.</summary>
public sealed partial class SingleFileTab
{
    /// <summary>Navigates a selected sidecar reference only when it exists in the opened record set.</summary>
    private void DialogueViews_ReferenceRequested(object? sender, uint formId)
    {
        if (_session.EffectiveRecords?.FormIdToDisplayName.ContainsKey(formId) == true ||
            _session.EffectiveRecords?.FormIdToEditorId.ContainsKey(formId) == true ||
            _session.DialogueFormIdIndex?.ContainsKey(formId) == true)
        {
            NavigateToFormId(formId);
        }
        else
        {
            DialogueViewsSidecar.ShowUnresolvedReference(formId);
        }
    }
}
