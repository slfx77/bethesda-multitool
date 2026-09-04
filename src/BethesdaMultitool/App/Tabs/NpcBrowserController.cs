using BethesdaMultitool.CLI.Rendering.Nif;
using BethesdaMultitool.CLI;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc;
using BethesdaMultitool.Core.Ui;

namespace BethesdaMultitool;

/// <summary>Backing state and filtering/selection logic for the NPC browser's list and detail panel.</summary>
internal sealed class NpcBrowserController
{
    private List<NpcListItem> _filteredList = [];
    private List<NpcListItem> _fullList = [];

    public IReadOnlyList<NpcListItem> FilteredList => _filteredList;
    public IReadOnlyList<NpcListItem> FullList => _fullList;
    public NpcActorKind ActorKind { get; private set; }
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
        _filteredList = NpcActorListPolicy.Filter(_fullList, ActorKind, namedOnly, searchText?.Trim());
        var restored = SelectedFormId.HasValue
            ? _filteredList.FirstOrDefault(n => n.FormId == SelectedFormId.Value)
            : null;

        return new NpcListState(
            _filteredList,
            restored,
            NpcActorListPolicy.BuildSelectionCountText(_filteredList, _fullList, ActorKind));
    }

    /// <summary>Switches between NPC and creature rows, clearing the incompatible prior selection.</summary>
    public NpcListState SetActorKind(
        NpcActorKind actorKind,
        bool namedOnly,
        string? searchText,
        bool showEditorId)
    {
        if (ActorKind != actorKind)
        {
            ActorKind = actorKind;
            SelectedFormId = null;
        }

        return Refresh(namedOnly, searchText, showEditorId);
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
        return NpcActorListPolicy.BuildSelectionCountText(_filteredList, _fullList, ActorKind);
    }

    /// <summary>Clears the loaded NPC lists and selection.</summary>
    public void Reset()
    {
        _filteredList = [];
        _fullList = [];
        ActorKind = NpcActorKind.Npc;
        SelectedFormId = null;
    }

    /// <summary>
    ///     Builds NPC render options from the UI controls (the display flags are inverted to the
    ///     renderer's "head/no-X" options). An empty NumberBox produces an explicit no-context null.
    /// </summary>
    public static NpcRenderOptions BuildRenderOptions(
        bool fullBody,
        bool armor,
        bool weapon,
        bool idlePose,
        double previewPlayerLevel = double.NaN)
    {
        return new NpcRenderOptions(
            !fullBody,
            !armor,
            !weapon,
            !idlePose,
            NormalizePreviewPlayerLevel(previewPlayerLevel));
    }

    internal static ushort? NormalizePreviewPlayerLevel(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return null;
        }

        return (ushort)Math.Round(
            Math.Clamp(value, 1d, ushort.MaxValue),
            MidpointRounding.AwayFromZero);
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
