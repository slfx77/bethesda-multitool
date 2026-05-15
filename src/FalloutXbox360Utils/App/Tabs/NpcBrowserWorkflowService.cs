using System.IO.MemoryMappedFiles;
using FalloutXbox360Utils.CLI;
using FalloutXbox360Utils.Core.Formats.Esm.Records;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc;
using FalloutXbox360Utils.Core.Minidump;

namespace FalloutXbox360Utils;

internal static class NpcBrowserWorkflowService
{
    internal static Task<BsaDiscoveryResult> DiscoverBsaPathsAsync(string esmPath, string? configuredDataDirectory)
    {
        return Task.Run(() =>
        {
            if (configuredDataDirectory != null)
            {
                var pseudoEsmPath = Path.Combine(configuredDataDirectory, Path.GetFileName(esmPath));
                return BsaDiscovery.Discover(pseudoEsmPath);
            }

            return BsaDiscovery.Discover(esmPath);
        });
    }

    internal static Task<NpcBrowserService?> CreateFromEsmAsync(
        string esmPath,
        bool bigEndian,
        BsaDiscoveryResult bsaPaths)
    {
        return Task.Run(() =>
        {
            var esmData = File.ReadAllBytes(esmPath);
            return NpcBrowserService.TryCreate(esmData, bigEndian, esmPath, bsaPaths);
        });
    }

    internal static Task<NpcBrowserService?> CreateFromDmpAsync(
        string dataDirectory,
        MemoryMappedViewAccessor accessor,
        long fileSize,
        MinidumpInfo minidumpInfo,
        EsmRecordScanResult scanResult,
        BsaDiscoveryResult bsaPaths)
    {
        return Task.Run(() =>
        {
            var esmFile = DiscoverEsmFile(dataDirectory);
            if (esmFile == null)
            {
                return null;
            }

            var esmData = File.ReadAllBytes(esmFile);
            var esmBigEndian = NpcBrowserService.DetectEsmBigEndian(esmData);

            return NpcBrowserService.TryCreateFromDmp(
                accessor,
                fileSize,
                minidumpInfo,
                scanResult,
                esmData,
                esmBigEndian,
                esmFile,
                bsaPaths);
        });
    }

    internal static string? DiscoverEsmFile(string dataDir)
    {
        var preferred = Path.Combine(dataDir, "FalloutNV.esm");
        if (File.Exists(preferred))
        {
            return preferred;
        }

        var esmFiles = Directory.GetFiles(dataDir, "*.esm");
        return esmFiles.Length > 0 ? esmFiles[0] : null;
    }

    internal static List<NpcListItem> FilterNpcList(
        IEnumerable<NpcListItem> npcs,
        bool namedOnly,
        string? searchText)
    {
        return npcs
            .Where(n =>
            {
                if (namedOnly && string.IsNullOrEmpty(n.FullName))
                {
                    return false;
                }

                if (!string.IsNullOrEmpty(searchText))
                {
                    return n.DisplayName.Contains(searchText, StringComparison.OrdinalIgnoreCase)
                           || (n.EditorId?.Contains(searchText, StringComparison.OrdinalIgnoreCase) == true)
                           || $"0x{n.FormId:X8}".Contains(searchText, StringComparison.OrdinalIgnoreCase);
                }

                return true;
            })
            .OrderBy(n => n.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    internal static string BuildDetailText(NpcListItem npc)
    {
        if (npc.IsCreature)
        {
            return $"FormID: 0x{npc.FormId:X8}\n" +
                   $"Editor ID: {npc.EditorId ?? "(none)"}\n" +
                   $"Type: {npc.CreatureTypeName}\n" +
                   $"Model: {npc.ModelPath ?? "(none)"}";
        }

        return $"FormID: 0x{npc.FormId:X8}\n" +
               $"Editor ID: {npc.EditorId ?? "(none)"}\n" +
               $"Gender: {(npc.IsFemale ? "Female" : "Male")}";
    }

    internal static Task<byte[]?> BuildGlbAsync(
        NpcBrowserService service,
        NpcListItem npc,
        NpcRenderOptions options)
    {
        return Task.Run(() => npc.IsCreature
            ? service.BuildCreatureGlb(npc.FormId)
            : service.BuildGlb(npc.FormId, options.HeadOnly, options.NoEquip, options.NoWeapon, options.BindPose));
    }

    internal static async Task ExportGlbAsync(
        NpcBrowserService service,
        NpcListItem npc,
        string outputPath,
        NpcRenderOptions options)
    {
        var glbBytes = await BuildGlbAsync(service, npc, options);
        if (glbBytes != null)
        {
            await File.WriteAllBytesAsync(outputPath, glbBytes);
        }
    }

    internal static async Task<int> RenderPngViewsAsync(
        NpcBrowserService service,
        uint npcFormId,
        string outputPath,
        NpcRenderOptions options,
        int spriteSize,
        CameraConfig camera)
    {
        var views = camera.ResolveViews(defaultAzimuth: 90f);
        foreach (var (suffix, azimuth, elevation) in views)
        {
            var pngBytes = await Task.Run(() =>
                service.RenderPng(
                    npcFormId,
                    options.HeadOnly,
                    options.NoEquip,
                    options.NoWeapon,
                    spriteSize,
                    azimuth,
                    elevation));

            if (pngBytes != null)
            {
                var viewOutputPath = views.Length > 1
                    ? Path.Combine(
                        Path.GetDirectoryName(outputPath) ?? ".",
                        Path.GetFileNameWithoutExtension(outputPath) + suffix + ".png")
                    : outputPath;
                await File.WriteAllBytesAsync(viewOutputPath, pngBytes);
            }
        }

        NifConverterWorkflowService.DeleteMultiViewPlaceholder(outputPath, views.Length);
        return views.Length;
    }

    internal static void SetAllSelected(IEnumerable<NpcListItem> items, bool selected)
    {
        foreach (var item in items)
        {
            item.IsSelected = selected;
        }
    }

    internal static List<uint>? GetSelectedFormIds(IEnumerable<NpcListItem> items)
    {
        var selected = items.Where(n => n.IsSelected).Select(n => n.FormId).ToList();
        return selected.Count > 0 ? selected : null;
    }

    internal static string BuildSelectionCountText(
        IReadOnlyCollection<NpcListItem> filteredList,
        IReadOnlyCollection<NpcListItem> fullList)
    {
        var selectedCount = filteredList.Count(n => n.IsSelected);
        var filterNote = fullList.Count != filteredList.Count
            ? $" (of {fullList.Count})"
            : "";

        return selectedCount > 0
            ? $"{filteredList.Count} actors{filterNote} \u2014 {selectedCount} selected"
            : $"{filteredList.Count} actors{filterNote}";
    }
}

internal sealed record NpcRenderOptions(
    bool HeadOnly,
    bool NoEquip,
    bool NoWeapon,
    bool BindPose);
