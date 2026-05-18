using FalloutXbox360Utils.CLI;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering;

namespace FalloutXbox360Utils;

internal sealed class NifConverterViewModel
{
    private List<NifTreeViewItem> _allItems = [];

    public string? CurrentPath { get; private set; }
    public bool IsBsa { get; private set; }
    public string? SelectedNifPath { get; private set; }

    public NifViewerSourceState ApplySource(
        string path,
        bool isBsa,
        NifViewerSourceLoadResult result,
        bool keepTextureOverride)
    {
        CurrentPath = path;
        IsBsa = isBsa;
        SelectedNifPath = null;
        _allItems = result.Items;

        return new NifViewerSourceState(
            _allItems,
            keepTextureOverride ? null : result.TexturePathsDisplay,
            $"{CountFiles(_allItems)} NIF files");
    }

    public List<NifTreeViewItem> FilterTree(string? search)
    {
        return NifConverterWorkflowService.FilterTreeItems(_allItems, search?.Trim());
    }

    public void SelectNif(NifTreeViewItem item)
    {
        SelectedNifPath = item.FullPath;
    }

    public static string FormatModelInfo(NifViewerInfo info)
    {
        return $"File: {info.FileName}\n" +
               $"Size: {info.FileSize:N0} bytes\n" +
               $"Format: {info.Format}\n" +
               $"Blocks: {info.BlockCount}\n" +
               $"BS Version: {info.BsVersion}\n" +
               $"User Version: {info.UserVersion}";
    }

    public static string FormatBlockTypes(NifViewerInfo info)
    {
        return string.Join(", ", info.BlockTypeNames);
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

    public static string FormatRenderStatus(int viewCount, string fileName)
    {
        return $"Rendered: {(viewCount > 1 ? $"{viewCount} views" : fileName)}";
    }

    private static int CountFiles(IEnumerable<NifTreeViewItem> items)
    {
        return items.Sum(i => i.IsDirectory ? i.Children.Count : 1);
    }
}

internal sealed record NifViewerSourceState(
    List<NifTreeViewItem> Items,
    string? TexturePathsDisplay,
    string FileCountText);
