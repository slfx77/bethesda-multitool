using BethesdaMultitool.CLI;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Plugin.AssetPacking;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc;
using BethesdaMultitool.Core.WorldData;
using Microsoft.UI.Xaml;

namespace BethesdaMultitool;

public sealed partial class WorldView3DControl
{
    /// <summary>
    ///     Why the placed-object (reference) pipeline is unavailable, or null when it initialized.
    ///     Surfaced persistently by <c>UpdateHud</c> so a silent loss of every REFR is impossible.
    /// </summary>
    private string? _referencePipelineInitError;

    /// <summary>
    ///     Collects texture-BSA paths from the primary data file plus every Load Order entry.
    ///     Each unique parent directory is globbed once (so a load order with 5 ESMs in the same
    ///     Data folder doesn't issue 5 identical filesystem scans). The result preserves Load
    ///     Order ordering: primary file first, then load-order entries in order, so a later DLC
    ///     ESM's BSAs win lookups for textures shared with the base game — matching the engine's
    ///     "later file overrides earlier" semantics that <see cref="NifTextureResolver" /> already
    ///     implements via source iteration order.
    /// </summary>
    private static string[] DiscoverTextureBsaPaths(WorldViewData data)
        => WorldDataBsaPathResolver.DiscoverTextureBsaPaths(data);

    /// <summary>
    ///     Mesh-BSA parallel of <see cref="DiscoverTextureBsaPaths" />. Globs the primary file's
    ///     directory + every Load Order entry's directory + every asset-only donor directory for
    ///     the BSA(s) that <c>BsaDiscovery</c> classifies as meshes archives (the primary + each
    ///     entry's extras). Dedupes by full path so identical Load Order entries don't open the
    ///     same BSA twice. Result order is priority order — <c>MeshArchiveSet</c> layers the
    ///     archives first-write-wins, so an earlier donor build outranks a later one.
    /// </summary>
    private static string[] DiscoverMeshBsaPaths(WorldViewData data)
    {
        var seenDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenBsas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();

        AddFrom(data.SourceFilePath);
        if (data.AdditionalDataPaths is not null)
        {
            foreach (var path in data.AdditionalDataPaths) AddFrom(path);
        }

        // Asset-only donor builds, in declared priority order, after the load order — see
        // WorldViewData.AssetDataDirectories.
        if (data.AssetDataDirectories is not null)
        {
            foreach (var dir in data.AssetDataDirectories) AddFromDirectory(dir);
        }

        return result.ToArray();

        void AddFrom(string? candidatePath)
        {
            if (string.IsNullOrEmpty(candidatePath)) return;
            AddFromDirectory(Path.GetDirectoryName(Path.GetFullPath(candidatePath)));
        }

        void AddFromDirectory(string? candidateDir)
        {
            if (string.IsNullOrEmpty(candidateDir)) return;
            var dir = Path.GetFullPath(candidateDir);
            if (!seenDirs.Add(dir)) return;
            var discovery = BsaDiscovery.DiscoverInDirectory(dir);
            foreach (var bsa in discovery.MeshesBsaPaths)
            {
                if (seenBsas.Add(bsa)) result.Add(bsa);
            }
        }
    }

    /// <summary>
    ///     "Resolve Renames" (memory dumps only): runs the DMP→ESM conversion's donor fuzzy pass
    ///     (<see cref="MeshRenameMapService" />) over every mesh path in the loaded dump, persists
    ///     the resulting map as a sidecar next to the dump, and reopens the mesh pipeline so the
    ///     preview immediately uses the same resolutions the converter would apply.
    /// </summary>
    private async void ResolveRenamesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_data is not { IsMemoryDump: true } data || data.SourceFilePath is not { } dumpPath) return;

        ResolveRenamesButton.IsEnabled = false;
        try
        {
            var donorDirs = CollectRenameDonorDataDirs(data);
            var meshPaths = data.ModelPathIndex.Values.ToArray();
            var sidecar = MeshRenameMapService.SidecarPathFor(dumpPath);
            var built = await Task.Run(() =>
            {
                var result = MeshRenameMapService.Build(meshPaths, donorDirs, CancellationToken.None);
                MeshRenameMapService.Save(sidecar, result.Renames, donorDirs);
                return result;
            });

            data.MeshPathRenames = built.Renames;
            Log.Info(
                "ResolveRenames: {0} mesh paths considered, {1} renamed, {2} exact, {3} missing, {4} cross-root declined -> {5}",
                built.Considered, built.Renamed, built.Exact, built.Missing,
                built.CrossRootDeclined, sidecar);

            // Reopen the archive set (and everything downstream of it) so the map takes effect.
            TryInitReferencePipeline();
        }
        catch (Exception ex)
        {
            Log.Warn("ResolveRenames failed: {0}: {1}", ex.GetType().Name, ex.Message);
        }
        finally
        {
            ResolveRenamesButton.IsEnabled = true;
        }
    }

    /// <summary>
    ///     Donor Data folders for the rename pass, in the same priority order the mesh-BSA
    ///     discovery walks: load-order entry directories first, asset-only donors after.
    ///     Build roots descend into their <c>Data\</c> folder, since that is what
    ///     <c>DataFolderIndex</c> indexes.
    /// </summary>
    private static List<string> CollectRenameDonorDataDirs(WorldViewData data)
    {
        var dirs = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in data.AdditionalDataPaths)
        {
            Add(Path.GetDirectoryName(Path.GetFullPath(path)));
        }

        foreach (var dir in data.AssetDataDirectories)
        {
            Add(dir);
        }

        return dirs;

        void Add(string? candidate)
        {
            if (string.IsNullOrEmpty(candidate) || !Directory.Exists(candidate)) return;
            var full = Path.GetFullPath(candidate);
            var dataDir = Path.Combine(full, "Data");
            if (Directory.Exists(dataDir) && !Directory.EnumerateFiles(full, "*.bsa").Any())
            {
                full = dataDir;
            }

            if (seen.Add(full)) dirs.Add(full);
        }
    }

    /// <summary>
    ///     Placed-object pipeline init. Mirrors the terrain pipeline init in
    ///     <see cref="LoadData" /> but lives in its own method since it needs the Meshes BSA
    ///     discovery in addition to the textures BSAs. Soft-fails when no Meshes BSA is found
    ///     (REFRs simply don't render — terrain still does).
    /// </summary>
    private void TryInitReferencePipeline()
    {
        if (_data is null) return;

        try
        {
            // Clear any prior failure — this runs again on every ESM switch.
            _referencePipelineInitError = null;
            var meshBsas = DiscoverMeshBsaPaths(_data);
            if (meshBsas.Length == 0)
            {
                Log.Warn(
                    "WorldView3DControl: no *Meshes*.bsa from '{0}' or {1} Load Order paths — REFRs will be skipped. Add an ESM whose Data folder contains a Meshes BSA to the Load Order.",
                    Path.GetDirectoryName(_data.SourceFilePath ?? "") ?? "(unknown)",
                    _data.AdditionalDataPaths.Count);
                return;
            }

            var textureBsas = DiscoverTextureBsaPaths(_data);
            // Memory dumps reference prototype mesh paths that were renamed before the shipped
            // archives, so enable the fuzzy renamed-asset fallback (+ loose-file overrides) for
            // dumps only; ESM/ESP browsing stays exact-only.
            _meshArchives = MeshArchiveSet.Open(
                meshBsas[0],
                meshBsas.Length > 1 ? meshBsas[1..] : null,
                enableFuzzy: _data.IsMemoryDump,
                includeLooseFiles: _data.IsMemoryDump,
                pathRenames: _data.MeshPathRenames);
            _referenceTextureResolver = new NifTextureResolver(textureBsas);
            _referenceGpuTextureResolver12 =
                new BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12.NifGpuTextureResolver(textureBsas);

            if (_gpu12 is null ||
                _commandRecorder12 is null ||
                _ringBuffer12 is null ||
                _rootSignature12 is null ||
                _cbvSrvUavHeap12 is null ||
                _deletionQueue12 is null)
            {
                return;
            }

            _referenceTextureCache12 = new BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12.GpuTextureCache12(
                    _gpu12, _commandRecorder12, _cbvSrvUavHeap12!, _referenceGpuTextureResolver12, _deletionQueue12)
                .RegisterWith(BethesdaMultitool.Core.Diagnostics.ResourceRegistry.Instance, "reference");
            // Capacity/budget env knobs are diagnostic levers for eviction-pressure stress gates
            // (e.g. capacity 64 + 16 MB makes the LRU eviction cascade fire constantly); defaults
            // preserve the shipped behavior. Read here, not in the cache — same as `capacity` always was.
            _referenceMeshCache12 = new BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12.ReferenceMeshCache12(
                _gpu12, _meshArchives, _referenceTextureResolver, _referenceTextureCache12,
                _deletionQueue12, _commandRecorder12,
                capacity: BethesdaMultitool.Core.EnvironmentVariables.GetClampedInt(
                    BethesdaMultitool.Core.EnvironmentVariables.Viewer.ReferenceMeshCapacity,
                    defaultValue: 2048, min: 8, max: 65_536),
                // Default SCALES WITH SYSTEM RAM (the decoded payloads are CPU memory) — see
                // AdaptiveMemoryDefaults; the env knob still pins it for eviction stress gates.
                decodedCacheByteBudget: BethesdaMultitool.Core.EnvironmentVariables.GetClampedLong(
                    BethesdaMultitool.Core.EnvironmentVariables.Viewer.ReferenceDecodedCacheMegabytes,
                    defaultValue: BethesdaMultitool.Core.Resources.AdaptiveMemoryDefaults.DecodedMeshCacheMegabytes(
                        BethesdaMultitool.Core.Resources.AdaptiveMemoryDefaults.SystemMemoryMb),
                    min: 4, max: 8_192) * 1024L * 1024L,
                // Auto-size the resident-mesh cap to each worldspace's working set UNLESS the capacity
                // knob is explicitly set (then honor the pinned value — used by eviction stress gates).
                autoSizeMeshCapacity: BethesdaMultitool.Core.EnvironmentVariables.Get(
                    BethesdaMultitool.Core.EnvironmentVariables.Viewer.ReferenceMeshCapacity) is null,
                // Authoritative leaf atlas from the TREE record's ICON (the .spt's dev material often never shipped).
                speedTreeLeafTextures: _data?.SpeedTreeLeafTextures,
                // TREE CNAM canopy-depth dimming (leaf + branch scalars) — engine-applied per tree.
                speedTreeDimming: _data?.SpeedTreeDimming);
            _references = new BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12.ReferenceRenderer12(
                _gpu12, _commandRecorder12, _ringBuffer12, _rootSignature12,
                _cbvSrvUavHeap12, _referenceMeshCache12, _deletionQueue12, _data.Game,
                _referenceEnabledOverrides)
            {
                DetailedProfilingEnabled = _profileLogging,
                ShowInitiallyDisabled = _showDisabled, // persist the toggle across ESM reloads
                // Markers/imposters are hidden by default to match the game; markers are also
                // toggleable from the toolbar (_showMarkers persists across reloads).
                ShowMarkers = _showMarkers,
                ShowGrass = _showGrass,
                ShowImposters = BethesdaMultitool.Core.EnvironmentVariables.IsEnabled(
                    BethesdaMultitool.Core.EnvironmentVariables.Viewer.ShowImposters),
                AnimationsEnabled = _animationsEnabled, // persist the toolbar toggle across reloads
                // FormID heatmap: persist the Overlays toggle + range across ESM reloads. The range
                // is whole cells end to end; the renderer resolves its own grid's cell size.
                FormIdHeatmapEnabled = _formIdHeatmap,
                FormIdHeatmapRangeCells = _formIdHeatmapRangeCells,
                // Video-expander toggles: persist across ESM reloads like the visibility toggles.
                GrassShadowsEnabled = _grassShadowsEnabled,
                TreeShadowsEnabled = _canopyShadowsEnabled,
                WindowReflectionsEnabled = _windowReflectionsEnabled,
                // Scripted day/night reference states (street lights, glow FX) follow the game hour.
                DayNightStates = _dayNightStates
            };
            _references.SetHiddenCategories(_hiddenCategories);
            Log.Info("WorldView3DControl: reference pipeline initialized ({0} meshes BSA(s), {1} textures BSA(s)).",
                meshBsas.Length, textureBsas.Length);
        }
        catch (Exception ex)
        {
            // This is the QUIETEST failure in the viewer and the one that per-game shader work makes
            // more likely: the reference pipeline compiles the placed-object shaders, so when it
            // throws, terrain still renders and every REFR silently disappears. Unlike the
            // device-level path (Lifecycle.cs surfaces "3D view unavailable"), nothing told the user
            // anything, and .Message discarded the FXC diagnostics. Log the whole exception at ERROR
            // and latch the reason for the HUD — a persistent ShowStatus is not usable here because
            // TryInitReferencePipeline runs BEFORE the worldspace load, whose own
            // "Loading worldspace…" status would immediately overwrite it.
            Log.Error("WorldView3DControl: reference pipeline init failed: {0}", ex);
            _referencePipelineInitError = ex.Message;
            DisposeReferencePipeline();
        }
    }

    /// <summary>
    ///     Resolves a placed-ref's requested mesh path against the open archive set and returns the
    ///     SUBSTITUTED path when the renamed-asset fuzzy fallback matched a different file. Returns
    ///     null when no substitution occurred (exact hit, miss, fuzzy disabled / non-DMP view, or the
    ///     pipeline isn't built). Lets the inspect panel surface "Fallback Mesh" for prototype refs
    ///     whose mesh was renamed before the shipped archives. Resolves against the same normalized
    ///     lookup the decoder uses, so the answer matches what actually rendered.
    /// </summary>
    internal string? TryResolveFallbackMeshPath(string requestedModelPath)
    {
        if (_meshArchives is null || _data is null || !_data.IsMemoryDump ||
            string.IsNullOrWhiteSpace(requestedModelPath))
        {
            return null;
        }

        var lookup = BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12
            .ReferenceMeshDecoder12.NormalizeModelPath(requestedModelPath);
        return _meshArchives.TryResolvePath(lookup, out _, out var resolved) &&
               !string.Equals(resolved, lookup, StringComparison.OrdinalIgnoreCase)
            ? resolved
            : null;
    }

    /// <summary>Releases every resource owned by the placed-object pipeline in safe order.</summary>
    private void DisposeReferencePipeline()
    {
        if (_referenceMeshCache12 is not null || _referenceTextureCache12 is not null)
        {
            _commandRecorder12?.WaitForGpuIdle();
        }

        // The mesh cache releases every referenced texture during its disposal. Snapshot the opt-in
        // alias telemetry first so the reference-cache summary describes the final live scene rather
        // than the intentionally empty cache left by that release cascade.
        _referenceTextureCache12?.EmitTraceSummary();
        _references?.Dispose();
        _references = null;
        _referenceMeshCache12?.Dispose();
        _referenceMeshCache12 = null;
        _referenceTextureCache12?.Dispose();
        _referenceTextureCache12 = null;
        _referenceGpuTextureResolver12?.Dispose();
        _referenceGpuTextureResolver12 = null;
        _referenceTextureResolver?.Dispose();
        _referenceTextureResolver = null;
        _meshArchives?.Dispose();
        _meshArchives = null;
    }
}
