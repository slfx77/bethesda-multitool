using System.Collections.ObjectModel;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering;

namespace FalloutXbox360Utils;

/// <summary>
///     View model for NIF file tree entries in the NIF Viewer tab.
/// </summary>
public sealed class NifTreeViewItem
{
    public required string DisplayName { get; init; }
    public required string FullPath { get; init; }
    public required bool IsDirectory { get; init; }
    public bool IsExpanded { get; set; }
    public ObservableCollection<NifTreeViewItem> Children { get; } = [];

    internal static List<NifTreeViewItem> FromTreeEntries(List<NifTreeEntry> entries)
    {
        var items = new List<NifTreeViewItem>();
        foreach (var entry in entries)
        {
            var item = new NifTreeViewItem
            {
                DisplayName = entry.DisplayName,
                FullPath = entry.FullPath,
                IsDirectory = entry.IsDirectory,
                IsExpanded = false
            };

            foreach (var child in entry.Children)
            {
                item.Children.Add(new NifTreeViewItem
                {
                    DisplayName = child.DisplayName,
                    FullPath = child.FullPath,
                    IsDirectory = child.IsDirectory
                });
            }

            items.Add(item);
        }

        return items;
    }
}
