using BethesdaMultitool.CLI.Rendering.Nif;
using BethesdaMultitool.CLI;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc;

namespace BethesdaMultitool;

/// <summary>Backing state and filtering/selection logic for the NPC browser's list and detail panel.</summary>
internal sealed class NpcBrowserController
{
    private List<NpcListItem> _filteredList = [];
    private List<NpcListItem> _fullList = [];

    public IReadOnlyList<NpcListItem> FilteredList => _filteredList;
    public IReadOnlyList<NpcListItem> FullList => _fullList;
    public uint? SelectedFormId { get; private set; }

    /// <summary>Replaces the full NPC list and returns the filtered view for the current options.</summary>
    public NpcListState LoadList(List<NpcListItem> npcs, bool namedOnly, string? searchText, bool showEditorId)
    {
        _fullList = npcs;
        SelectedFormId = null;
        return Refresh(namedOnly, searchText, showEditorId);
    }

    /// <summary>Re-applies the named-only/search filters and refreshes the list view, restoring any prior selection.</summary>
    public NpcListState Refresh(bool namedOnly, string? searchText, bool showEditorId)
    {
        NpcListItem.ShowEditorId = showEditorId;
        _filteredList = NpcBrowserWorkflowService.FilterNpcList(_fullList, namedOnly, searchText?.Trim());
        var restored = SelectedFormId.HasValue
            ? _filteredList.FirstOrDefault(n => n.FormId == SelectedFormId.Value)
            : null;

        return new NpcListState(
            _filteredList,
            restored,
            NpcBrowserWorkflowService.BuildSelectionCountText(_filteredList, _fullList));
    }

    /// <summary>Finds an NPC by FormID within the currently visible (filtered) list.</summary>
    public NpcListItem? FindVisible(uint formId)
    {
        return _filteredList.FirstOrDefault(n => n.FormId == formId);
    }

    /// <summary>Selects an NPC (or clears the selection) and returns the resulting detail-panel state.</summary>
    public NpcSelectionState Select(NpcListItem? npc)
    {
        if (npc == null)
        {
            SelectedFormId = null;
            return NpcSelectionState.Empty;
        }

        SelectedFormId = npc.FormId;
        return new NpcSelectionState(
            npc.DisplayName,
            NpcBrowserWorkflowService.BuildDetailText(npc),
            true,
            !npc.IsCreature,
            !npc.IsCreature);
    }

    /// <summary>Checks or unchecks the batch-selection box on every visible NPC.</summary>
    public void SetAllVisibleSelected(bool selected)
    {
        NpcBrowserWorkflowService.SetAllSelected(_filteredList, selected);
    }

    /// <summary>Returns the FormIDs of the batch-selected visible NPCs, or null if none are selected.</summary>
    public List<uint>? GetSelectedVisibleFormIds()
    {
        return NpcBrowserWorkflowService.GetSelectedFormIds(_filteredList);
    }

    /// <summary>Builds the "N selected / M shown" count label for the list footer.</summary>
    public string BuildSelectionCountText()
    {
        return NpcBrowserWorkflowService.BuildSelectionCountText(_filteredList, _fullList);
    }

    /// <summary>Clears the loaded NPC lists and selection.</summary>
    public void Reset()
    {
        _filteredList = [];
        _fullList = [];
        SelectedFormId = null;
    }

    /// <summary>Builds NPC render options from the UI toggles (note: each flag is inverted to a "head/no-X" option).</summary>
    public static NpcRenderOptions BuildRenderOptions(bool fullBody, bool armor, bool weapon, bool idlePose)
    {
        return new NpcRenderOptions(!fullBody, !armor, !weapon, !idlePose);
    }

    /// <summary>Clamps a requested sprite render size to the supported 64-4096 px range.</summary>
    public static int ClampSpriteSize(double value)
    {
        return Math.Clamp((int)value, 64, 4096);
    }

    /// <summary>Builds a camera configuration from the chosen perspective preset and elevation angle.</summary>
    public static CameraConfig BuildCameraConfig(string? perspective, double elevationValue)
    {
        var elevation = (float)elevationValue;
        return perspective switch
        {
            "iso" => new CameraConfig
            {
                Isometric = true,
                ElevationDeg = elevation,
                ElevationOverridden = true
            },
            "side" => new CameraConfig { SideProfile = true },
            "trimetric" => new CameraConfig { Trimetric = true },
            _ => new CameraConfig
            {
                ElevationDeg = elevation,
                ElevationOverridden = true
            }
        };
    }

    /// <summary>Builds a default export file name from the NPC's EditorId (or FormID) plus the extension.</summary>
    public static string BuildDefaultFileName(NpcListItem? npc, string extension)
    {
        return npc != null
            ? $"{npc.EditorId ?? $"npc_{npc.FormId:X8}"}{extension}"
            : $"npc{extension}";
    }

    /// <summary>Formats the render status line ("N views" or the file name).</summary>
    public static string FormatRenderStatus(int viewCount, string fileName)
    {
        return $"Rendered: {(viewCount > 1 ? $"{viewCount} views" : fileName)}";
    }

    /// <summary>Formats a batch-operation progress line ("Op: done/total \u2014 name").</summary>
    public static string FormatBatchProgress(string operationName, int done, int total, string name)
    {
        return $"{operationName}: {done}/{total} \u2014 {name}";
    }

    /// <summary>Formats the batch-operation completed message.</summary>
    public static string FormatBatchCompleted(string operationName)
    {
        return $"{operationName} complete.";
    }

    /// <summary>Formats the batch-operation canceled message.</summary>
    public static string FormatBatchCancelled(string operationName)
    {
        return $"{operationName} canceled.";
    }

    /// <summary>Formats the batch-operation failure message including the exception text.</summary>
    public static string FormatBatchFailed(string operationName, Exception ex)
    {
        return $"{operationName} failed: {ex.Message}";
    }
}

/// <summary>The NPC list view state: the items to show, an optional selection to restore, and the count label.</summary>
internal sealed record NpcListState(
    List<NpcListItem> Items,
    NpcListItem? RestoredSelection,
    string CountText);

/// <summary>Detail-panel state for the selected NPC: name, detail text, and which export/render actions are enabled.</summary>
internal sealed record NpcSelectionState(
    string Name,
    string DetailText,
    bool CanExportGlb,
    bool CanRenderPng,
    bool CanToggleHumanoidOptions)
{
    public static NpcSelectionState Empty { get; } = new("", "", false, false, false);
}
