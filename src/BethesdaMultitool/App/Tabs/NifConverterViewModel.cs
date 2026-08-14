using BethesdaMultitool.CLI.Rendering.Nif;
using BethesdaMultitool.CLI;
using BethesdaMultitool.Core.Formats.Nif.Rendering;

namespace BethesdaMultitool;

/// <summary>Backing state for the NIF converter/viewer tab: the loaded file tree, current source, and selection.</summary>
internal sealed class NifConverterViewModel
{
    private List<NifTreeViewItem> _allItems = [];

    public string? CurrentPath { get; private set; }
    public bool IsArchive { get; private set; }
    public string? SelectedNifPath { get; private set; }

    /// <summary>Adopts a newly loaded folder/archive source and returns the view state.</summary>
    public NifViewerSourceState ApplySource(
        string path,
        bool isArchive,
        NifViewerSourceLoadResult result)
    {
        CurrentPath = path;
        IsArchive = isArchive;
        SelectedNifPath = null;
        _allItems = result.Items;

        return new NifViewerSourceState(
            _allItems,
            result.TexturePathsDisplay,
            $"{CountFiles(_allItems)} NIF files");
    }

    /// <summary>Filters the loaded NIF tree by a search term.</summary>
    public List<NifTreeViewItem> FilterTree(string? search)
    {
        return NifConverterWorkflowService.FilterTreeItems(_allItems, search?.Trim());
    }

    /// <summary>Marks the given tree item as the selected NIF.</summary>
    public void SelectNif(NifTreeViewItem item)
    {
        SelectedNifPath = item.FullPath;
    }

    /// <summary>Formats a multi-line summary (name, size, format, block count, versions) for a loaded NIF.</summary>
    public static string FormatModelInfo(NifViewerInfo info)
    {
        return $"File: {info.FileName}\n" +
               $"Size: {info.FileSize:N0} bytes\n" +
               $"Format: {info.Format}\n" +
               $"Blocks: {info.BlockCount}\n" +
               $"BS Version: {info.BsVersion}\n" +
               $"User Version: {info.UserVersion}";
    }

    /// <summary>Returns the NIF's block type names as a comma-separated string.</summary>
    public static string FormatBlockTypes(NifViewerInfo info)
    {
        return string.Join(", ", info.BlockTypeNames);
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

    /// <summary>Formats the render status line ("N views" or the file name).</summary>
    public static string FormatRenderStatus(int viewCount, string fileName)
    {
        return $"Rendered: {(viewCount > 1 ? $"{viewCount} views" : fileName)}";
    }

    private static int CountFiles(IEnumerable<NifTreeViewItem> items)
    {
        return items.Sum(i => i.IsDirectory ? i.Children.Count : 1);
    }
}

/// <summary>View state after loading a NIF source: the file tree, texture-path display text, and file count label.</summary>
internal sealed record NifViewerSourceState(
    List<NifTreeViewItem> Items,
    string TexturePathsDisplay,
    string FileCountText);
