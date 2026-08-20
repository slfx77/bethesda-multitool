using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Esm.Terrain;
using BethesdaMultitool.Core.Minidump;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Esm.Runtime.Readers.Specialized.World;

/// <summary>
///     Reader for TESObjectLAND and DIAL runtime structs from Xbox 360 memory dumps.
///     Extracts cell coordinates, loaded land data, and probes dialogue topic layouts.
/// </summary>
internal sealed class RuntimeWorldReader
{
    // Build-specific shift delta vs the PDB baseline (+16). Most builds align with
    // PDB exactly (shift=0); proto Debug builds shift -4 etc. Routed through
    // PdbStructView.WithShift so the per-field offset constants below are PDB-aligned
    // and the shift adjustment happens once per opened view.
    private const int PdbBaselineShift = 16;

    // High-byte histogram of "bad" ppVertices VAs — values that are non-null but don't
    // resolve to a captured memory region. Tells us whether they cluster in a specific
    // VA range (suggesting an uncaptured heap region) or are random noise (suggesting a
    // wrong field offset). Index = upper 8 bits of the VA (0x00..0xFF).
    private readonly int[] _badVertexVaHighBytes = new int[256];
    private readonly RuntimeMemoryContext _context;
    private readonly RuntimeLoadedLandDiagnosticsReader _diagnosticsReader;
    private readonly RuntimePdbFieldAccessor _fields;

    // Parallel histogram of successful inner-pointer VAs (the actual pVertices arrays
    // that were resolved). Comparing this against `_badVertexVaHighBytes` shows whether
    // good vs bad VAs cluster in different regions.
    private readonly int[] _goodVertexVaHighBytes = new int[256];
    private readonly RuntimeLandVisualReader _landVisualReader;
    private readonly int _shift;
    private int _meshStageGridReconstructFail;

    // Stage counters for the terrain-mesh extraction path. Aggregated across a single
    // `ReadAllRuntimeLandData` call so we can pinpoint which step drops a build's
    // LoadedLandData reads before they reach a TerrainMesh. Each counter is mutually
    // exclusive: a LAND record contributes to exactly one of them.
    private int _meshStageQuadrantOk;
    private int _meshStageSingleArrayOk;
    private int _meshStageVertexDataReadFail;
    private int _meshStageVertexFloatValidationFail;
    private int _meshStageVertexInnerPtrNullOrBad;
    private int _meshStageVertexOuterDerefFail;
    private int _meshStageVertexPtrBad;
    private int _meshStageVertexPtrNull;

    /// <summary>Creates the reader bound to the given runtime memory context.</summary>
    public RuntimeWorldReader(RuntimeMemoryContext context)
    {
        _context = context;
        _fields = new RuntimePdbFieldAccessor(context);
        _landVisualReader = new RuntimeLandVisualReader(context);
        _diagnosticsReader = new RuntimeLoadedLandDiagnosticsReader(context);
        _shift = RuntimeBuildOffsets.GetPdbShift(MinidumpAnalyzer.DetectBuildType(context.MinidumpInfo))
                 - PdbBaselineShift;
    }

    private PdbStructView? OpenLandView(RuntimeEditorIdEntry entry)
    {
        // Always look up the TESObjectLAND PDB layout (key 0x44 = TLOD/TESObjectLAND)
        // regardless of entry.FormType. Per-DMP FormType drift means runtime LAND
        // entries may carry a different byte — e.g. Fallout_Release_Beta.xex.dmp has
        // LAND at runtime FormType 0x43 (the byte where the PDB says NavMesh lives).
        // EditorIdLookupTables.landFormType detection identifies the right runtime
        // byte for filtering; this override ensures the field-resolution machinery
        // uses the right PDB layout regardless.
        return _fields.OpenStructView(entry, 0x44)?.WithShift(0, int.MaxValue, _shift);
    }

    /// <summary>
    ///     Read cell coordinates from a runtime TESObjectLAND struct's LoadedLandData.
    ///     Returns null if the LAND has no loaded data or the pointer is invalid.
    /// </summary>
    public RuntimeLoadedLandData? ReadRuntimeLandData(RuntimeEditorIdEntry entry)
    {
        // Caller is responsible for filtering to LAND entries (FormType varies by build)
        var view = OpenLandView(entry);
        if (view == null)
        {
            return null;
        }

        var offset = view.FileOffset;
        var formId = entry.FormId;

        var parentCellFormId = view.FormIdPointer("pParentCell", "TESObjectLAND", 0x39);

        // Read pLoadedData pointer (PDB +56, TESObjectLAND owner). View applies build shift.
        var pLoadedData = view.UInt32("pLoadedData", "TESObjectLAND");
        if (pLoadedData == 0 || !_context.IsValidPointer(pLoadedData))
        {
            return null;
        }

        // Convert to file offset
        var loadedDataFileOffset = _context.VaToFileOffset(pLoadedData);
        if (loadedDataFileOffset == null || loadedDataFileOffset.Value + LoadedDataSize > _context.FileSize)
        {
            return null;
        }

        // Read LoadedLandData struct
        var loadedDataBuffer = new byte[LoadedDataSize];
        try
        {
            _context.Accessor.ReadArray(loadedDataFileOffset.Value, loadedDataBuffer, 0, LoadedDataSize);
        }
        catch
        {
            return null;
        }

        // Extract cell coordinates and base height
        var cellX = RuntimeMemoryContext.ReadInt32BE(loadedDataBuffer, LoadedDataCellXOffset);
        var cellY = RuntimeMemoryContext.ReadInt32BE(loadedDataBuffer, LoadedDataCellYOffset);
        var baseHeight = BinaryUtils.ReadFloatBE(loadedDataBuffer, LoadedDataBaseHeightOffset);
        float? terrainBaseHeight = baseHeight;

        // Validate cell coordinates are reasonable (-128 to 127 for typical worldspace)
        if (cellX < -1000 || cellX > 1000 || cellY < -1000 || cellY > 1000)
        {
            return null;
        }

        // Validate base height is reasonable
        if (!RuntimeMemoryContext.IsNormalFloat(baseHeight) || baseHeight < -100000 || baseHeight > 100000)
        {
            terrainBaseHeight = null;
            baseHeight = 0;
        }

        // Extract HeightExtents (NiPoint2 at offset +24): min/max terrain heights for this cell
        var (minHeight, maxHeight) = ReadHeightExtents(loadedDataBuffer);

        // Capture all known LoadedLandData pointer fields from the PDB layout for diagnostics.
        var diagnostics = _diagnosticsReader.Build(loadedDataBuffer);
        var visualExtraction = _landVisualReader.Read(loadedDataBuffer);

        // Extract terrain mesh from heap pointers (ppVertices, ppNormals, ppColorsA)
        var terrainMesh = ReadTerrainMesh(loadedDataBuffer);
        if (terrainMesh is not null)
        {
            terrainMesh = terrainMesh with { RuntimeBaseHeight = terrainBaseHeight };
        }

        // Surface runtime VCLR + VNML alongside the texture layers. The terrain mesh's NiColorA
        // and NiPoint3 arrays (LoadedLandData.ppColorsA, ppNormals) carry the engine's live
        // vertex colors and normals; ToLandVertexColorBytes / ToLandVertexNormalBytes project
        // each into the canonical 33×33×3 LAND payload. Without this, cell.LandVisualData
        // stays empty for runtime-sourced cells even when the DMP holds the data — so the
        // World tab's Vertex Colors / Normals layers (and the converter's LAND encoder) lose
        // visibility into what the engine actually rendered.
        var visualData = visualExtraction.VisualData;
        if (terrainMesh is { HasColors: true })
        {
            var runtimeVclr = RuntimeTerrainColorExtractor.ExtractVclr(terrainMesh);
            if (runtimeVclr is { Length: > 0 })
            {
                visualData = visualData is null
                    ? new LandVisualData
                    {
                        VertexColors = runtimeVclr,
                        VertexColorsSource = VisualDataSource.Runtime,
                        Source = VisualDataSource.Runtime
                    }
                    : visualData with
                    {
                        VertexColors = runtimeVclr,
                        VertexColorsSource = VisualDataSource.Runtime
                    };
            }
        }

        if (terrainMesh is { HasNormals: true })
        {
            var runtimeVnml = terrainMesh.ToLandVertexNormalBytes();
            if (runtimeVnml is { Length: > 0 })
            {
                visualData = visualData is null
                    ? new LandVisualData
                    {
                        VertexNormals = runtimeVnml,
                        VertexNormalsSource = VisualDataSource.Runtime,
                        Source = VisualDataSource.Runtime
                    }
                    : visualData with
                    {
                        VertexNormals = runtimeVnml,
                        VertexNormalsSource = VisualDataSource.Runtime
                    };
            }
        }

        return new RuntimeLoadedLandData
        {
            FormId = formId,
            ParentCellFormId = parentCellFormId,
            CellX = cellX,
            CellY = cellY,
            BaseHeight = baseHeight,
            MinHeight = minHeight,
            MaxHeight = maxHeight,
            LandOffset = offset,
            LoadedDataOffset = loadedDataFileOffset.Value,
            TerrainMesh = terrainMesh,
            VisualData = visualData,
            RuntimeLandTextures = visualExtraction.LandTextures,
            RuntimeTextureSets = visualExtraction.TextureSets,
            Diagnostics = diagnostics
        };
    }

    private static (float? Min, float? Max) ReadHeightExtents(byte[] loadedDataBuffer)
    {
        var rawMin = BinaryUtils.ReadFloatBE(loadedDataBuffer, LoadedDataHeightExtentsOffset);
        var rawMax = BinaryUtils.ReadFloatBE(loadedDataBuffer, LoadedDataHeightExtentsOffset + 4);

        float? min = RuntimeMemoryContext.IsNormalFloat(rawMin) && rawMin is > -100000 and < 100000
            ? rawMin
            : null;
        float? max = RuntimeMemoryContext.IsNormalFloat(rawMax) && rawMax is > -100000 and < 100000
            ? rawMax
            : null;

        return (min, max);
    }

    /// <summary>
    ///     Extract terrain mesh data from LoadedLandData heap pointers.
    ///     Follows double-indirected pointers (NiPoint3** ppVertices, ppNormals; NiColorA** ppColorsA).
    ///     Returns null if vertex data cannot be extracted.
    /// </summary>
    private RuntimeTerrainMesh? ReadTerrainMesh(byte[] loadedDataBuffer)
    {
        var quadrantMesh = ReadQuadrantTerrainMesh(loadedDataBuffer);
        if (quadrantMesh != null)
        {
            _meshStageQuadrantOk++;
            return quadrantMesh;
        }

        // Single-array fallback. Inline the stages of ReadDoubleIndirectedFloatArray here
        // so we can pinpoint where the read drops out across the LAND set, which is the
        // bottleneck on builds where many LANDs have a valid LoadedLandData but the
        // vertex array can't be resolved (e.g. xex22/xex43/xex44).
        if (LoadedDataVerticesPtrOffset + 4 > loadedDataBuffer.Length)
        {
            _meshStageVertexPtrNull++;
            return null;
        }

        var ppVertices = BinaryUtils.ReadUInt32BE(loadedDataBuffer, LoadedDataVerticesPtrOffset);
        if (ppVertices == 0)
        {
            _meshStageVertexPtrNull++;
            return null;
        }

        if (!_context.IsValidPointer(ppVertices))
        {
            _meshStageVertexPtrBad++;
            _badVertexVaHighBytes[(ppVertices >> 24) & 0xFF]++;
            return null;
        }

        var outerFileOffset = _context.VaToFileOffset(ppVertices);
        if (outerFileOffset == null)
        {
            _meshStageVertexOuterDerefFail++;
            return null;
        }

        var innerPtrBytes = _context.ReadBytes(outerFileOffset.Value, 4);
        if (innerPtrBytes == null)
        {
            _meshStageVertexOuterDerefFail++;
            return null;
        }

        var pVertices = BinaryUtils.ReadUInt32BE(innerPtrBytes);
        if (pVertices == 0 || !_context.IsValidPointer(pVertices))
        {
            _meshStageVertexInnerPtrNullOrBad++;
            return null;
        }

        var vertexFileOffset = _context.VaToFileOffset(pVertices);
        if (vertexFileOffset == null)
        {
            _meshStageVertexInnerPtrNullOrBad++;
            return null;
        }

        const int totalFloats = RuntimeTerrainMesh.VertexCount * 3;
        var rawData = _context.ReadBytes(vertexFileOffset.Value, totalFloats * 4);
        if (rawData == null)
        {
            _meshStageVertexDataReadFail++;
            return null;
        }

        var vertices = new float[totalFloats];
        var validCount = 0;
        for (var i = 0; i < totalFloats; i++)
        {
            vertices[i] = BinaryUtils.ReadFloatBE(rawData, i * 4);
            if (RuntimeMemoryContext.IsNormalFloat(vertices[i]) && Math.Abs(vertices[i]) <= 200_000f)
            {
                validCount++;
            }
        }

        if (validCount < totalFloats * 0.01)
        {
            _meshStageVertexFloatValidationFail++;
            return null;
        }

        var terrainMesh = new RuntimeTerrainMesh
        {
            Vertices = vertices,
            VertexDataOffset = vertexFileOffset.Value
        };

        var reconstruction = RuntimeTerrainGridReconstructionService.Reconstruct(terrainMesh);
        if (reconstruction == null)
        {
            _meshStageGridReconstructFail++;
            return null;
        }

        _meshStageSingleArrayOk++;

        var companionValidFraction = Math.Max(0.01,
            reconstruction.SourceSampleCount / (double)RuntimeTerrainMesh.VertexCount * 0.5);

        // Try normals (NiPoint3, components should be in [-1, 1] but allow some tolerance)
        var (normals, _) = ReadDoubleIndirectedFloatArray(
            loadedDataBuffer, LoadedDataNormalsPtrOffset,
            3, RuntimeTerrainMesh.VertexCount, 2.0f, companionValidFraction);

        // Try vertex colors (NiColorA = RGBA, components in [0, 1])
        var (colors, _) = ReadDoubleIndirectedFloatArray(
            loadedDataBuffer, LoadedDataColorsPtrOffset,
            4, RuntimeTerrainMesh.VertexCount, 2.0f, companionValidFraction);

        return terrainMesh with
        {
            Normals = normals,
            Colors = colors
        };
    }

    private RuntimeTerrainMesh? ReadQuadrantTerrainMesh(byte[] loadedDataBuffer)
    {
        var vertexArrays = ReadDoubleIndirectedFloatArraySlots(
            loadedDataBuffer, LoadedDataVerticesPtrOffset,
            TerrainQuadrantCount, 3, RuntimeTerrainQuadrantMeshBuilder.QuadrantVertexCount, 200_000, 0.5);

        if (vertexArrays.Count == 0)
        {
            return null;
        }

        var normalArrays = ReadDoubleIndirectedFloatArraySlots(
            loadedDataBuffer, LoadedDataNormalsPtrOffset,
            TerrainQuadrantCount, 3, RuntimeTerrainQuadrantMeshBuilder.QuadrantVertexCount, 2.0f, 0.25);
        var colorArrays = ReadDoubleIndirectedFloatArraySlots(
            loadedDataBuffer, LoadedDataColorsPtrOffset,
            TerrainQuadrantCount, 4, RuntimeTerrainQuadrantMeshBuilder.QuadrantVertexCount, 2.0f, 0.25);

        return RuntimeTerrainQuadrantMeshBuilder.TryBuild(vertexArrays, normalArrays, colorArrays);
    }

    private List<RuntimeTerrainFloatArraySlot> ReadDoubleIndirectedFloatArraySlots(
        byte[] loadedDataBuffer,
        int ptrOffset,
        int slotCount,
        int floatsPerElement,
        int elementCount,
        float maxAbsValue,
        double minValidFraction)
    {
        var result = new List<RuntimeTerrainFloatArraySlot>(slotCount);
        if (ptrOffset + 4 > loadedDataBuffer.Length)
        {
            return result;
        }

        var outerPtr = BinaryUtils.ReadUInt32BE(loadedDataBuffer, ptrOffset);
        if (outerPtr == 0 || !_context.IsValidPointer(outerPtr))
        {
            return result;
        }

        var outerFileOffset = _context.VaToFileOffset(outerPtr);
        if (outerFileOffset == null)
        {
            return result;
        }

        var pointerBytes = _context.ReadBytes(outerFileOffset.Value, slotCount * 4);
        if (pointerBytes == null)
        {
            return result;
        }

        var totalFloats = elementCount * floatsPerElement;
        var byteCount = totalFloats * 4;
        for (var slot = 0; slot < slotCount; slot++)
        {
            var innerPtr = BinaryUtils.ReadUInt32BE(pointerBytes, slot * 4);
            if (innerPtr == 0 || !_context.IsValidPointer(innerPtr))
            {
                continue;
            }

            var dataFileOffset = _context.VaToFileOffset(innerPtr);
            if (dataFileOffset == null)
            {
                continue;
            }

            var rawData = _context.ReadBytes(dataFileOffset.Value, byteCount);
            if (rawData == null)
            {
                continue;
            }

            var data = new float[totalFloats];
            var validCount = 0;
            for (var i = 0; i < totalFloats; i++)
            {
                data[i] = BinaryUtils.ReadFloatBE(rawData, i * 4);
                if (RuntimeMemoryContext.IsNormalFloat(data[i]) && Math.Abs(data[i]) <= maxAbsValue)
                {
                    validCount++;
                }
            }

            if (validCount < totalFloats * minValidFraction)
            {
                continue;
            }

            result.Add(new RuntimeTerrainFloatArraySlot(slot, data, dataFileOffset.Value));

            // Histogram the inner pointer's high byte so we can compare against the
            // bad-VA histogram when diagnosing why other builds fail to resolve vertices.
            if (ptrOffset == LoadedDataVerticesPtrOffset)
            {
                _goodVertexVaHighBytes[(innerPtr >> 24) & 0xFF]++;
            }
        }

        return result;
    }

    /// <summary>
    ///     Follow a double-indirected pointer (T**) from the LoadedLandData buffer to read a float array.
    ///     Step 1: Read pointer at ptrOffset → VA of the inner pointer.
    ///     Step 2: Dereference inner pointer → VA of the actual float array.
    ///     Step 3: Read elementCount × floatsPerElement floats from the array.
    /// </summary>
    private (float[]? Data, long FileOffset) ReadDoubleIndirectedFloatArray(
        byte[] loadedDataBuffer, int ptrOffset, int floatsPerElement, int elementCount, float maxAbsValue,
        double minValidFraction = 0.7)
    {
        if (ptrOffset + 4 > loadedDataBuffer.Length)
        {
            return (null, 0);
        }

        // Step 1: Read the outer pointer (T**)
        var outerPtr = BinaryUtils.ReadUInt32BE(loadedDataBuffer, ptrOffset);
        if (outerPtr == 0 || !_context.IsValidPointer(outerPtr))
        {
            return (null, 0);
        }

        var outerFileOffset = _context.VaToFileOffset(outerPtr);
        if (outerFileOffset == null)
        {
            return (null, 0);
        }

        // Step 2: Dereference to get the inner pointer (T*)
        var innerPtrBytes = _context.ReadBytes(outerFileOffset.Value, 4);
        if (innerPtrBytes == null)
        {
            return (null, 0);
        }

        var innerPtr = BinaryUtils.ReadUInt32BE(innerPtrBytes);
        if (innerPtr == 0 || !_context.IsValidPointer(innerPtr))
        {
            return (null, 0);
        }

        var dataFileOffset = _context.VaToFileOffset(innerPtr);
        if (dataFileOffset == null)
        {
            return (null, 0);
        }

        // Step 3: Read the float array
        var totalFloats = elementCount * floatsPerElement;
        var byteCount = totalFloats * 4;
        var rawData = _context.ReadBytes(dataFileOffset.Value, byteCount);
        if (rawData == null)
        {
            return (null, 0);
        }

        // Parse big-endian floats with validation
        var result = new float[totalFloats];
        var validCount = 0;
        for (var i = 0; i < totalFloats; i++)
        {
            result[i] = BinaryUtils.ReadFloatBE(rawData, i * 4);
            if (RuntimeMemoryContext.IsNormalFloat(result[i]) && Math.Abs(result[i]) <= maxAbsValue)
            {
                validCount++;
            }
        }

        // Require a minimum fraction of valid floats to reject garbage data.
        // Default 70% for normals/colors; 90% for terrain vertices (passed by caller).
        if (validCount < totalFloats * minValidFraction)
        {
            return (null, 0);
        }

        return (result, dataFileOffset.Value);
    }

    /// <summary>
    ///     Read all LAND records from runtime data and extract cell coordinates.
    ///     Returns a dictionary mapping LAND FormID to LoadedLandData.
    /// </summary>
    public Dictionary<uint, RuntimeLoadedLandData> ReadAllRuntimeLandData(IEnumerable<RuntimeEditorIdEntry> entries)
    {
        var result = new Dictionary<uint, RuntimeLoadedLandData>();
        var total = 0;
        var noOffset = 0;
        var noMesh = 0;
        var withMesh = 0;

        // Reset mesh-extraction stage counters for this batch so the summary log reflects
        // only this run.
        _meshStageQuadrantOk = 0;
        _meshStageSingleArrayOk = 0;
        _meshStageVertexPtrNull = 0;
        _meshStageVertexPtrBad = 0;
        _meshStageVertexOuterDerefFail = 0;
        _meshStageVertexInnerPtrNullOrBad = 0;
        _meshStageVertexDataReadFail = 0;
        _meshStageVertexFloatValidationFail = 0;
        _meshStageGridReconstructFail = 0;
        Array.Clear(_badVertexVaHighBytes);
        Array.Clear(_goodVertexVaHighBytes);

        // Entries are pre-filtered to LAND by EsmEditorIdExtractor (FormType varies by build)
        foreach (var entry in entries)
        {
            total++;
            var landData = ReadRuntimeLandData(entry);
            if (landData != null)
            {
                result[landData.FormId] = landData;
                if (landData.TerrainMesh != null)
                {
                    withMesh++;
                }
                else
                {
                    noMesh++;
                }
            }
        }

        // Count failure reasons from the entries that didn't produce results
        foreach (var entry in entries)
        {
            if (entry.TesFormOffset == null)
            {
                noOffset++;
            }
        }

        var log = Logger.Instance;
        var failed = total - result.Count;
        log.Info("LAND terrain: {0} entries → {1} with data ({2} with mesh, {3} coords-only), " +
                 "{4} failed (no offset: {5}, no loaded data or bad coords: {6})",
            total, result.Count, withMesh, noMesh, failed, noOffset, failed - noOffset);

        log.Info(
            "LAND mesh stages: quadOk={0}, singleOk={1}, vertexPtrNull={2}, vertexPtrBad={3}, " +
            "outerDerefFail={4}, innerPtrNullOrBad={5}, dataReadFail={6}, floatFail={7}, gridFail={8}",
            _meshStageQuadrantOk, _meshStageSingleArrayOk, _meshStageVertexPtrNull,
            _meshStageVertexPtrBad, _meshStageVertexOuterDerefFail,
            _meshStageVertexInnerPtrNullOrBad, _meshStageVertexDataReadFail,
            _meshStageVertexFloatValidationFail, _meshStageGridReconstructFail);

        if (_meshStageVertexPtrBad > 0)
        {
            log.Info("LAND bad vertex VA high-byte histogram (top 5): {0}",
                FormatVaHistogram(_badVertexVaHighBytes));
        }

        if (_meshStageQuadrantOk > 0)
        {
            log.Info("LAND good vertex VA high-byte histogram (top 5): {0}",
                FormatVaHistogram(_goodVertexVaHighBytes));
        }

        return result;
    }

    private static string FormatVaHistogram(int[] highBytes)
    {
        var pairs = new List<(int Byte, int Count)>();
        for (var b = 0; b < highBytes.Length; b++)
        {
            if (highBytes[b] > 0)
            {
                pairs.Add((b, highBytes[b]));
            }
        }

        pairs.Sort((a, b) => b.Count.CompareTo(a.Count));
        return string.Join(", ", pairs.Take(5).Select(t => $"0x{t.Byte:X2}xxxxxx={t.Count}"));
    }

    /// <summary>
    ///     Probe a known DIAL runtime struct to determine the correct dump shift.
    ///     Tries +0, +4, +8, +16 shift hypotheses and logs which one produces valid data.
    ///     Returns the best shift value, or -1 if none worked.
    /// </summary>
    public int ProbeDialTopicLayout(RuntimeEditorIdEntry entry)
    {
        return RuntimeDialLayoutProbe.Probe(_context, entry);
    }

    #region World/Land Struct Layout

    // TESObjectLAND fields are now resolved via PdbStructView in ReadRuntimeLandData
    // (pParentCell at PDB +48, pLoadedData at PDB +56, structSize=60). Per-build shift
    // routed through WithShift(0, int.MaxValue, _shift).

    // LoadedLandData: 164 bytes — standalone struct, identical across all builds.
    // Pointer/texture/percent-array diagnostic offsets live in
    // RuntimeLoadedLandDiagnosticsReader; only the offsets used by the cell-coordinate
    // read and terrain-mesh extraction below remain here.
    private const int LoadedDataSize = 164;
    private const int LoadedDataVerticesPtrOffset = 4; // NiPoint3** ppVertices
    private const int LoadedDataNormalsPtrOffset = 8; // NiPoint3** ppNormals
    private const int LoadedDataColorsPtrOffset = 12; // NiColorA** ppColorsA
    private const int LoadedDataHeightExtentsOffset = 24; // NiPoint2: min/max terrain heights
    private const int LoadedDataCellXOffset = 152;
    private const int LoadedDataCellYOffset = 156;
    private const int LoadedDataBaseHeightOffset = 160;
    private const int TerrainQuadrantCount = RuntimeTerrainQuadrantMeshBuilder.QuadrantCount;

    #endregion
}
