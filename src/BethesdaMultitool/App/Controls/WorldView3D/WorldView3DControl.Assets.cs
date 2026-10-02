using BethesdaMultitool.Core.WorldData;

namespace BethesdaMultitool;

public sealed partial class WorldView3DControl
{
    private WorldAssetBinding? _assetBinding;
    private long _boundAssetRevision = -1;

    private void BindAssetSources(WorldViewData data) =>
        _assetBinding = new WorldAssetBinding(data, action => DispatcherQueue.TryEnqueue(() => action()),
            RefreshResidentAssets);

    private void UnbindAssetSources()
    {
        _assetBinding?.Dispose();
        _assetBinding = null;
    }

    private void RefreshResidentAssets()
    {
        if (_data is not { } data) return;
        try
        {
            var generation = BeginSceneSelection();
            RebuildAssetPipelines();
            TryBuildCellGrid();
            RefreshAtmosphereForCurrentWorldspace();
            RequestClearAdaptedLight();
            MarkSceneSelectionReady(generation);
            if (data.UnwatchedAssetRoots.Count > 0)
                ShowStatus("Asset watching unavailable; reload to refresh.");
        }
        catch (Exception ex)
        {
            Log.Warn("World assets refresh failed: {0}", ex);
            ShowStatus("Asset refresh failed; reload to retry.");
        }
    }

    internal void ResetData()
    {
        UnbindAssetSources();
        BeginSceneSelection();
        RetireAssetPipelines();
        _data = null;
        _spatialIndex = null;
        _cellGridLookup = null;
        ClearSelection3D();
    }

    private void RetireAssetPipelines()
    {
        if (_topDownReadbackTask is { IsCompleted: false } readback)
        {
            Core.Orchestration.NonPumpingWait.Wait(readback);
            _ = readback.Exception;
        }
        _commandRecorder12?.WaitForGpuIdle();
        DisposeReferencePipeline();
        _terrain?.Dispose();
        _terrain = null;
        _textureResolver12?.Dispose();
        _textureResolver12 = null;
        _skyGeometry?.Clear();
        _shadowMap?.InvalidateContent();
    }

    private void RebuildAssetPipelines()
    {
        if (_data is null) return;
        var snapshot = _data.AssetSnapshot;
        RetireAssetPipelines();
        // The sky textures (sun/moon/cloud/star) are bindless indices INTO this resolver's heap region;
        // recreating the resolver invalidates them, so force a re-resolve even if the climate key is
        // unchanged, and blank the indices so nothing samples a stale slot until the re-resolve lands.
        // The MODL-NIF harvest is keyed by mesh path; drop it too so a new game can't reuse a stale one.
        _skyTexKey = null;
        _skyNifTextures = null;
        _skyNifModlKey = null;
        _sunDiscTexIndex = _sunGlareTexIndex = _moonTexIndex = _moonSecundaTexIndex = uint.MaxValue;
        Array.Fill(_moonPhaseTexIndices, uint.MaxValue);
        Array.Fill(_moonSecundaPhaseTexIndices, uint.MaxValue);

        if (_gpu12 is not null)
        {
            try
            {
                var bsas = snapshot.Sources.TexturePlan?.Mounts.Select(m => m.Path).ToArray() ?? [];
                if (bsas.Length == 0)
                {
                    Log.Warn(
                        "WorldView3DControl: no *Textures*.bsa from '{0}' or {1} Load Order paths - terrain will render white-tinted.",
                        Path.GetDirectoryName(_data.SourceFilePath ?? "") ?? "(unknown)",
                        _data.AdditionalDataPaths.Count);
                }
                else
                {
                    Log.Info("WorldView3DControl: discovered {0} texture BSA(s) for terrain.", bsas.Length);
                }

                _textureResolver12 = new BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12.TerrainTextureResolver12(
                    _gpu12, _commandRecorder12!, _cbvSrvUavHeap12!, _deletionQueue12!,
                    _data.LandTexturesByFormId, _data.TextureSetsByFormId, bsas, _data.Game,
                    snapshot.Sources.TexturePlan);
                var terrain12 = new BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12.TerrainRenderer12(
                    _gpu12, _commandRecorder12!, _ringBuffer12!, _rootSignature12!,
                    _cbvSrvUavHeap12!, _deletionQueue12!,
                    _textureResolver12);
                terrain12.SetDebugModes(_showTerrainTextures, _showVertexColors);
                terrain12.DetailedProfilingEnabled = _profileLogging;
                _terrain = terrain12;
            }
            catch (Exception ex)
            {
                Log.Warn("WorldView3DControl: terrain pipeline init failed: {0}", ex.Message);
                _terrain = null;
                _textureResolver12?.Dispose();
                _textureResolver12 = null;
            }

            TryInitReferencePipeline(snapshot.Sources);
        }

        _boundAssetRevision = snapshot.Revision;
        Core.Formats.Nif.Rendering.Profiling.RendererProfilerTrace.Event("asset-revision-bound",
            new Dictionary<string, object?>
            {
                ["source"] = _data.SourceFilePath,
                ["revision"] = snapshot.Revision,
                ["meshPlan"] = snapshot.Sources.MeshPlan?.Identity,
                ["texturePlan"] = snapshot.Sources.TexturePlan?.Identity,
                ["memoryDumpSource"] = _data.IsMemoryDump,
                ["recordMode"] = _data.AssetRecords.Mode
            });
        // A watcher can advance while old workers drain. Never leave that newer generation unserved.
        if (_data.AssetRevision != _boundAssetRevision) _data.RefreshAssetSources();
    }
}
