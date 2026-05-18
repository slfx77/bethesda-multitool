using FalloutXbox360Utils.CLI;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc;

namespace FalloutXbox360Utils;

internal sealed class NpcBrowserController
{
    private List<NpcListItem> _filteredList = [];
    private List<NpcListItem> _fullList = [];

    public IReadOnlyList<NpcListItem> FilteredList => _filteredList;
    public IReadOnlyList<NpcListItem> FullList => _fullList;
    public uint? SelectedFormId { get; private set; }

    public NpcListState LoadList(List<NpcListItem> npcs, bool namedOnly, string? searchText, bool showEditorId)
    {
        _fullList = npcs;
        SelectedFormId = null;
        return Refresh(namedOnly, searchText, showEditorId);
    }

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

    public NpcListItem? FindVisible(uint formId)
    {
        return _filteredList.FirstOrDefault(n => n.FormId == formId);
    }

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

    public void SetAllVisibleSelected(bool selected)
    {
        NpcBrowserWorkflowService.SetAllSelected(_filteredList, selected);
    }

    public List<uint>? GetSelectedVisibleFormIds()
    {
        return NpcBrowserWorkflowService.GetSelectedFormIds(_filteredList);
    }

    public string BuildSelectionCountText()
    {
        return NpcBrowserWorkflowService.BuildSelectionCountText(_filteredList, _fullList);
    }

    public void Reset()
    {
        _filteredList = [];
        _fullList = [];
        SelectedFormId = null;
    }

    public static NpcRenderOptions BuildRenderOptions(bool fullBody, bool armor, bool weapon, bool idlePose)
    {
        return new NpcRenderOptions(!fullBody, !armor, !weapon, !idlePose);
    }

    public static int ClampSpriteSize(double value)
    {
        return Math.Clamp((int)value, 64, 4096);
    }

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

    public static string BuildDefaultFileName(NpcListItem? npc, string extension)
    {
        return npc != null
            ? $"{npc.EditorId ?? $"npc_{npc.FormId:X8}"}{extension}"
            : $"npc{extension}";
    }

    public static string FormatRenderStatus(int viewCount, string fileName)
    {
        return $"Rendered: {(viewCount > 1 ? $"{viewCount} views" : fileName)}";
    }

    public static string FormatBatchProgress(string operationName, int done, int total, string name)
    {
        return $"{operationName}: {done}/{total} \u2014 {name}";
    }

    public static string FormatBatchCompleted(string operationName)
    {
        return $"{operationName} complete.";
    }

    public static string FormatBatchCancelled(string operationName)
    {
        return $"{operationName} cancelled.";
    }

    public static string FormatBatchFailed(string operationName, Exception ex)
    {
        return $"{operationName} failed: {ex.Message}";
    }
}

internal sealed record NpcListState(
    List<NpcListItem> Items,
    NpcListItem? RestoredSelection,
    string CountText);

internal sealed record NpcSelectionState(
    string Name,
    string DetailText,
    bool CanExportGlb,
    bool CanRenderPng,
    bool CanToggleHumanoidOptions)
{
    public static NpcSelectionState Empty { get; } = new("", "", false, false, false);
}
