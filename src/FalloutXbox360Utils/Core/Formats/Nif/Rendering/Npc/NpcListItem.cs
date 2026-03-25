using System.ComponentModel;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc;

/// <summary>
///     Lightweight NPC descriptor for the GUI NPC browser list.
///     Implements INotifyPropertyChanged for two-way binding of IsSelected.
/// </summary>
internal sealed class NpcListItem : INotifyPropertyChanged
{
    private bool _isSelected;

    public NpcListItem(uint formId, string? editorId, string? fullName, bool isFemale, uint? raceFormId)
    {
        FormId = formId;
        EditorId = editorId;
        FullName = fullName;
        IsFemale = isFemale;
        RaceFormId = raceFormId;
    }

    public uint FormId { get; }

    public string? EditorId { get; }

    public string? FullName { get; }

    public bool IsFemale { get; }

    public uint? RaceFormId { get; }

    /// <summary>
    ///     When true, DisplayName shows EditorID (FormID) instead of FullName.
    ///     Toggled by the NPC browser's "Show Editor ID" checkbox.
    /// </summary>
    internal static bool ShowEditorId { get; set; }

    public string DisplayName => ShowEditorId
        ? $"{EditorId ?? "?"} (0x{FormId:X8})"
        : FullName ?? EditorId ?? $"0x{FormId:X8}";

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public override string ToString()
    {
        return DisplayName;
    }
}
