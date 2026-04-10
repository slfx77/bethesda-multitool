using System.IO.MemoryMappedFiles;
using FalloutXbox360Utils.Core.Formats;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Records;
using FalloutXbox360Utils.Core.Formats.Esm.Runtime;

namespace FalloutXbox360Utils.Core.Minidump;

/// <summary>
///     Extracts ESM metadata, runtime assets, and FormID correlations from
///     memory-mapped dump files during analysis.
/// </summary>
internal static class MinidumpMetadataExtractor
{
    /// <summary>
    ///     Post-process pre-computed scan results: merge ESM records and asset strings,
    ///     extract LAND/REFR records, map FormIDs, walk scene graph, and add carved file entries.
    ///     Called by <see cref="ConcurrentScanCoordinator" /> after all concurrent scans complete.
    /// </summary>
    internal static async Task PostProcessMetadataAsync(
        MemoryMappedViewAccessor accessor,
        AnalysisResult result,
        EsmRecordScanResult esmRecords,
        List<DetectedAssetString> assetStrings,
        List<ExtractedMesh>? meshes,
        List<ExtractedTexture>? textures,
        List<ExtractedTexture>? gpuTextures,
        MinidumpInfo minidumpInfo,
        IProgress<AnalysisProgress>? progress,
        bool verbose,
        CancellationToken cancellationToken)
    {
        var log = Logger.Instance;

        // Merge pre-computed scan results
        result.EsmRecords = esmRecords;
        esmRecords.AssetStrings.AddRange(assetStrings);
        log.Debug("PostProcess: ESM records: {0}, Asset strings: {1}",
            esmRecords.MainRecords.Count, assetStrings.Count);

        // LAND extraction (random access on warm page cache)
        progress?.Report(new AnalysisProgress
            { Phase = "LAND Records", FilesFound = result.CarvedFiles.Count, PercentComplete = 60 });
        log.Debug("PostProcess: Extracting LAND records...");
        EsmWorldExtractor.ExtractLandRecords(accessor, result.FileSize, esmRecords);

        // REFR extraction
        progress?.Report(new AnalysisProgress
            { Phase = "REFR Records", FilesFound = result.CarvedFiles.Count, PercentComplete = 65 });
        log.Debug("PostProcess: Extracting REFR records...");
        EsmWorldExtractor.ExtractRefrRecords(accessor, result.FileSize, esmRecords);
        log.Debug("PostProcess: REFR complete: {0} records", esmRecords.RefrRecords.Count);

        // Group asset strings into contiguous string pool regions for the memory map
        GroupStringPoolRegions(result, esmRecords);

        // Add VHGT heightmap regions to the memory map
        AddHeightmapRegions(result, esmRecords);

        // Extract runtime Editor IDs with FormID associations via pointer following
        progress?.Report(new AnalysisProgress
            { Phase = "Runtime EditorIDs", FilesFound = result.CarvedFiles.Count, PercentComplete = 70 });
        log.Debug("PostProcess: Extracting runtime EditorIDs...");
        EsmEditorIdExtractor.ExtractRuntimeEditorIds(accessor, result.FileSize, minidumpInfo, esmRecords, verbose);
        log.Debug("PostProcess: EditorIDs complete: {0} IDs", esmRecords.RuntimeEditorIds.Count);

        // FormID mapping
        progress?.Report(new AnalysisProgress
            { Phase = "FormIDs", FilesFound = result.CarvedFiles.Count, PercentComplete = 75 });
        await Task.Run(
            () =>
            {
                result.FormIdMap =
                    EsmFormIdCorrelator.CorrelateFormIdsToNamesMemoryMapped(accessor, result.FileSize,
                        result.EsmRecords!);
            },
            cancellationToken);

        // Runtime mesh and texture results
        result.RuntimeMeshes = meshes;
        result.RuntimeTextures = MergeTextureLists(textures, gpuTextures);

        // Walk scene graph for mesh naming (depends on geometry results)
        if (meshes is { Count: > 0 } && minidumpInfo.IsValid)
        {
            progress?.Report(new AnalysisProgress
                { Phase = "Scene Graph", FilesFound = result.CarvedFiles.Count, PercentComplete = 90 });

            var context = new RuntimeMemoryContext(new MmfMemoryAccessor(accessor), result.FileSize, minidumpInfo);
            var walker = new RuntimeSceneGraphWalker(context);
            result.SceneGraphMap = await Task.Run(
                () => walker.WalkSceneGraph(meshes), cancellationToken);
            log.Debug("PostProcess: Scene graph: {0}/{1} meshes resolved",
                result.SceneGraphMap.Count, meshes.Count);
        }

        // Create CarvedFileInfo entries for all recognized runtime data in one place,
        // so every consumer (CLI and GUI) gets complete coverage automatically.
        AddMeshCarvedFiles(result, meshes);
        AddTextureCarvedFiles(result, textures);
        AddSceneGraphNodeCarvedFiles(result);
        AddTesFormStructRegions(result);

        // Identify terrain meshes among geometry-scanned results and create synthetic
        // LAND records with derived cell coordinates. This runs in the core pipeline
        // so both CLI (--extract-esm) and GUI get terrain heightmap data automatically.
        var terrainLandRecords = TerrainMeshIdentifier.IdentifyTerrainMeshes(meshes, esmRecords);
        if (terrainLandRecords.Count > 0)
        {
            esmRecords.LandRecords.AddRange(terrainLandRecords);
            log.Info("PostProcess: Added {0} terrain-derived LAND records (total: {1})",
                terrainLandRecords.Count, esmRecords.LandRecords.Count);
        }

        progress?.Report(new AnalysisProgress
            { Phase = "Post-Processing", FilesFound = result.CarvedFiles.Count, PercentComplete = 98 });
    }

    private static void AddMeshCarvedFiles(AnalysisResult result, List<ExtractedMesh>? meshes)
    {
        if (meshes == null)
        {
            return;
        }

        foreach (var mesh in meshes)
        {
            var structSize = mesh.Type == MeshType.TriShape ? 88 : 80;
            SceneGraphInfo? sceneInfo = null;
            result.SceneGraphMap?.TryGetValue(mesh.SourceOffset, out sceneInfo);

            result.CarvedFiles.Add(new CarvedFileInfo
            {
                Offset = mesh.SourceOffset,
                Length = structSize,
                FileType = mesh.Type == MeshType.TriShape ? "NiTriShapeData" : "NiTriStripsData",
                FileName = sceneInfo?.ModelName,
                SignatureId = "runtime_mesh",
                Category = FileCategory.Model
            });

            // Add vertex position buffer
            if (mesh.VertexDataFileOffset > 0 && mesh.VertexDataSize > 0)
            {
                result.CarvedFiles.Add(new CarvedFileInfo
                {
                    Offset = mesh.VertexDataFileOffset,
                    Length = mesh.VertexDataSize,
                    FileType = "Vertex Data",
                    FileName = sceneInfo?.ModelName,
                    SignatureId = "runtime_mesh_vertices",
                    Category = FileCategory.Model
                });
            }

            // Add normal buffer
            if (mesh.NormalDataFileOffset > 0 && mesh.Normals != null)
            {
                result.CarvedFiles.Add(new CarvedFileInfo
                {
                    Offset = mesh.NormalDataFileOffset,
                    Length = mesh.Normals.Length * 4,
                    FileType = "Normal Data",
                    FileName = sceneInfo?.ModelName,
                    SignatureId = "runtime_mesh_normals",
                    Category = FileCategory.Model
                });
            }

            // Add UV buffer
            if (mesh.UVDataFileOffset > 0 && mesh.UVs != null)
            {
                result.CarvedFiles.Add(new CarvedFileInfo
                {
                    Offset = mesh.UVDataFileOffset,
                    Length = mesh.UVs.Length * 4,
                    FileType = "UV Data",
                    FileName = sceneInfo?.ModelName,
                    SignatureId = "runtime_mesh_uvs",
                    Category = FileCategory.Model
                });
            }

            // Add index buffer
            if (mesh.IndexDataFileOffset > 0 && mesh.IndexDataSize > 0)
            {
                result.CarvedFiles.Add(new CarvedFileInfo
                {
                    Offset = mesh.IndexDataFileOffset,
                    Length = mesh.IndexDataSize,
                    FileType = "Index Data",
                    FileName = sceneInfo?.ModelName,
                    SignatureId = "runtime_mesh_indices",
                    Category = FileCategory.Model
                });
            }
        }
    }

    private static void AddTextureCarvedFiles(AnalysisResult result, List<ExtractedTexture>? textures)
    {
        if (textures == null)
        {
            return;
        }

        foreach (var texture in textures)
        {
            result.CarvedFiles.Add(new CarvedFileInfo
            {
                Offset = texture.SourceOffset,
                Length = 116, // NiPixelData struct size
                FileType = $"{texture.Width}x{texture.Height} {texture.Format}",
                FileName = texture.Filename,
                SignatureId = "runtime_texture",
                Category = FileCategory.Texture
            });

            // Add the pixel data buffer region (the actual texture content)
            if (texture.PixelDataFileOffset > 0 && texture.DataSize > 0)
            {
                result.CarvedFiles.Add(new CarvedFileInfo
                {
                    Offset = texture.PixelDataFileOffset,
                    Length = texture.DataSize,
                    FileType = "Texture Data",
                    FileName = texture.Filename,
                    SignatureId = "runtime_texture_data",
                    Category = FileCategory.Texture
                });
            }
        }
    }

    private static void AddSceneGraphNodeCarvedFiles(AnalysisResult result)
    {
        if (result.SceneGraphMap == null || result.SceneGraphMap.Count == 0)
        {
            return;
        }

        // Deduplicate NiTriShape offsets (each mesh maps to one NiTriShape)
        // and collect unique parent NiNode offsets from parent chains
        var addedOffsets = new HashSet<long>();

        foreach (var (_, info) in result.SceneGraphMap)
        {
            // Add NiTriShape struct (240 bytes)
            if (info.NiTriShapeFileOffset > 0 && addedOffsets.Add(info.NiTriShapeFileOffset))
            {
                result.CarvedFiles.Add(new CarvedFileInfo
                {
                    Offset = info.NiTriShapeFileOffset,
                    Length = 240, // NiTriShape struct size
                    FileType = "NiTriShape",
                    FileName = info.NodeName,
                    SignatureId = "runtime_scene_node",
                    Category = FileCategory.Struct
                });
            }
        }
    }

    /// <summary>
    ///     Merge CPU-side (NiPixelData) and GPU-side (NiXenonSourceTextureData) texture lists.
    ///     Deduplicates by DataHash to avoid exporting the same texture found by both scanners.
    /// </summary>
    private static List<ExtractedTexture>? MergeTextureLists(
        List<ExtractedTexture>? cpuTextures, List<ExtractedTexture>? gpuTextures)
    {
        if (cpuTextures == null && gpuTextures == null)
        {
            return null;
        }

        if (gpuTextures is not { Count: > 0 })
        {
            return cpuTextures;
        }

        if (cpuTextures is not { Count: > 0 })
        {
            return gpuTextures;
        }

        // Deduplicate: CPU textures take priority (they have more reliable metadata)
        var seenHashes = new HashSet<long>(cpuTextures.Select(t => t.DataHash));
        var merged = new List<ExtractedTexture>(cpuTextures);
        var addedCount = 0;

        foreach (var gpu in gpuTextures)
        {
            if (seenHashes.Add(gpu.DataHash))
            {
                merged.Add(gpu);
                addedCount++;
            }
        }

        if (addedCount > 0)
        {
            Logger.Instance.Info("Texture merge: {0} CPU + {1} GPU ({2} new after dedup) = {3} total",
                cpuTextures.Count, gpuTextures.Count, addedCount, merged.Count);
        }

        return merged.OrderBy(t => t.SourceOffset).ToList();
    }

    /// <summary>
    ///     Adds TESForm struct regions from runtime EditorID data to the carved files list.
    /// </summary>
    internal static void AddTesFormStructRegions(AnalysisResult result)
    {
        if (result.EsmRecords == null)
        {
            return;
        }

        foreach (var entry in result.EsmRecords.RuntimeEditorIds)
        {
            if (entry.TesFormOffset is not > 0)
            {
                continue;
            }

            var typeCode = RuntimeBuildOffsets.GetRecordTypeCode(entry.FormType);
            var structSize = RuntimeBuildOffsets.GetStructSize(entry.FormType);

            result.CarvedFiles.Add(new CarvedFileInfo
            {
                Offset = entry.TesFormOffset.Value,
                Length = structSize,
                FileType = typeCode != null ? $"TESForm: {typeCode}" : $"TESForm: 0x{entry.FormType:X2}",
                FileName = entry.EditorId,
                SignatureId = "tesform_struct",
                Category = FileCategory.Struct
            });
        }
    }

    /// <summary>
    ///     Adds runtime terrain mesh regions from enriched LAND records to the carved files list.
    /// </summary>
    internal static void AddRuntimeTerrainMeshRegions(AnalysisResult result)
    {
        if (result.EsmRecords == null)
        {
            return;
        }

        foreach (var land in result.EsmRecords.LandRecords
                     .Where(l => l.RuntimeTerrainMesh is { VertexDataOffset: > 0 }))
        {
            result.CarvedFiles.Add(new CarvedFileInfo
            {
                Offset = land.RuntimeTerrainMesh!.VertexDataOffset,
                Length = 33 * 33 * 3 * 4, // 33x33 grid, 3 floats (x,y,z) each
                FileType = "Terrain Mesh",
                FileName = land.Header.FormId > 0 ? $"LAND {land.Header.FormId:X8}" : null,
                SignatureId = "terrain_mesh",
                Category = FileCategory.Model
            });
        }
    }

    /// <summary>
    ///     Groups detected asset strings into contiguous string pool regions.
    ///     Strings within 256 bytes of each other are merged into a single region
    ///     to avoid creating thousands of tiny memory map entries.
    /// </summary>
    internal static void GroupStringPoolRegions(AnalysisResult result, EsmRecordScanResult esmRecords)
    {
        if (esmRecords.AssetStrings.Count == 0)
        {
            return;
        }

        const int mergeThreshold = 256;
        var sorted = esmRecords.AssetStrings.OrderBy(s => s.Offset).ToList();

        var regionStart = sorted[0].Offset;
        var regionEnd = sorted[0].Offset + sorted[0].Path.Length + 1; // +1 for null terminator
        var stringCount = 1;

        for (var i = 1; i < sorted.Count; i++)
        {
            var s = sorted[i];
            var sEnd = s.Offset + s.Path.Length + 1;

            if (s.Offset <= regionEnd + mergeThreshold)
            {
                // Extend current region
                regionEnd = Math.Max(regionEnd, sEnd);
                stringCount++;
            }
            else
            {
                // Emit current region, start new one
                result.CarvedFiles.Add(new CarvedFileInfo
                {
                    Offset = regionStart,
                    Length = regionEnd - regionStart,
                    FileType = "String Pool",
                    FileName = $"{stringCount} strings",
                    SignatureId = "string_pool",
                    Category = FileCategory.Strings
                });

                regionStart = s.Offset;
                regionEnd = sEnd;
                stringCount = 1;
            }
        }

        // Emit final region
        result.CarvedFiles.Add(new CarvedFileInfo
        {
            Offset = regionStart,
            Length = regionEnd - regionStart,
            FileType = "String Pool",
            FileName = $"{stringCount} strings",
            SignatureId = "string_pool",
            Category = FileCategory.Strings
        });
    }

    /// <summary>
    ///     Adds VHGT heightmap data regions to the memory map.
    /// </summary>
    internal static void AddHeightmapRegions(AnalysisResult result, EsmRecordScanResult esmRecords)
    {
        foreach (var land in esmRecords.LandRecords.Where(l => l.Heightmap is { Offset: > 0 }))
        {
            result.CarvedFiles.Add(new CarvedFileInfo
            {
                Offset = land.Heightmap!.Offset,
                Length = 1089, // Standard VHGT size: 4-byte offset + 33x33 heights + padding
                FileType = "VHGT Heightmap",
                FileName = land.Header.FormId > 0 ? $"LAND {land.Header.FormId:X8}" : null,
                SignatureId = "vhgt_heightmap",
                Category = FileCategory.Model
            });
        }
    }
}
