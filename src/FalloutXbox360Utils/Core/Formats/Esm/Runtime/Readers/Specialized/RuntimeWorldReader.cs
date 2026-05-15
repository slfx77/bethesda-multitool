using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using FalloutXbox360Utils.Core.Formats.Esm.Terrain;
using FalloutXbox360Utils.Core.Minidump;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;

/// <summary>
///     Reader for TESObjectLAND and DIAL runtime structs from Xbox 360 memory dumps.
///     Extracts cell coordinates, loaded land data, and probes dialogue topic layouts.
/// </summary>
internal sealed class RuntimeWorldReader(RuntimeMemoryContext context)
{
    private readonly RuntimeMemoryContext _context = context;
    private readonly RuntimeLandVisualReader _landVisualReader = new(context);

    // Build-specific offset shift: Proto Debug PDB + _s = actual dump offset.
    private readonly int _s = RuntimeBuildOffsets.GetPdbShift(
        MinidumpAnalyzer.DetectBuildType(context.MinidumpInfo));

    /// <summary>
    ///     Read cell coordinates from a runtime TESObjectLAND struct's LoadedLandData.
    ///     Returns null if the LAND has no loaded data or the pointer is invalid.
    /// </summary>
    public RuntimeLoadedLandData? ReadRuntimeLandData(RuntimeEditorIdEntry entry)
    {
        // Caller is responsible for filtering to LAND entries (FormType varies by build)
        if (entry.TesFormOffset == null)
        {
            return null;
        }

        var offset = entry.TesFormOffset.Value;
        if (offset + LandStructSize > _context.FileSize)
        {
            return null;
        }

        var buffer = new byte[LandStructSize];
        try
        {
            _context.Accessor.ReadArray(offset, buffer, 0, LandStructSize);
        }
        catch
        {
            return null;
        }

        // Validate FormID at offset 12 (TESForm: vfptr(4) + cFormType(1) + pad(3) + flags(4) + iFormID(4))
        var formId = BinaryUtils.ReadUInt32BE(buffer, 12);
        if (formId != entry.FormId)
        {
            return null;
        }

        var parentCellFormId = _context.FollowPointerToFormId(buffer, LandParentCellPtrOffset, 0x39);

        // Read pLoadedData pointer at offset 56
        var pLoadedData = BinaryUtils.ReadUInt32BE(buffer, LandLoadedDataPtrOffset);
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

        // Validate cell coordinates are reasonable (-128 to 127 for typical worldspace)
        if (cellX < -1000 || cellX > 1000 || cellY < -1000 || cellY > 1000)
        {
            return null;
        }

        // Validate base height is reasonable
        if (!RuntimeMemoryContext.IsNormalFloat(baseHeight) || baseHeight < -100000 || baseHeight > 100000)
        {
            baseHeight = 0;
        }

        // Extract HeightExtents (NiPoint2 at offset +24): min/max terrain heights for this cell
        var (minHeight, maxHeight) = ReadHeightExtents(loadedDataBuffer);

        // Capture all known LoadedLandData pointer fields from the PDB layout for diagnostics.
        var diagnostics = BuildLoadedLandDiagnostics(loadedDataBuffer);
        var visualExtraction = _landVisualReader.Read(loadedDataBuffer);

        // Extract terrain mesh from heap pointers (ppVertices, ppNormals, ppColorsA)
        var terrainMesh = ReadTerrainMesh(loadedDataBuffer);

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
            VisualData = visualExtraction.VisualData,
            RuntimeLandTextures = visualExtraction.LandTextures,
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

    private RuntimeLoadedLandDiagnostics BuildLoadedLandDiagnostics(byte[] loadedDataBuffer)
    {
        return new RuntimeLoadedLandDiagnostics
        {
            Mesh = ReadDoublePointerDiagnostic(loadedDataBuffer, LoadedDataMeshPtrOffset),
            Vertices = ReadDoublePointerDiagnostic(loadedDataBuffer, LoadedDataVerticesPtrOffset),
            VertexArrays =
                ReadDoublePointerArrayDiagnostics(loadedDataBuffer, LoadedDataVerticesPtrOffset, TerrainQuadrantCount),
            Normals = ReadDoublePointerDiagnostic(loadedDataBuffer, LoadedDataNormalsPtrOffset),
            NormalArrays =
                ReadDoublePointerArrayDiagnostics(loadedDataBuffer, LoadedDataNormalsPtrOffset, TerrainQuadrantCount),
            Colors = ReadDoublePointerDiagnostic(loadedDataBuffer, LoadedDataColorsPtrOffset),
            ColorArrays =
                ReadDoublePointerArrayDiagnostics(loadedDataBuffer, LoadedDataColorsPtrOffset, TerrainQuadrantCount),
            NormalsSet = ReadDoublePointerDiagnostic(loadedDataBuffer, LoadedDataNormalsSetPtrOffset),
            Border = ReadPointerDiagnostic(loadedDataBuffer, LoadedDataBorderPtrOffset),
            MoppCode = ReadPointerDiagnostic(loadedDataBuffer, LoadedDataMoppCodePtrOffset),
            LandRigidBody = ReadPointerDiagnostic(loadedDataBuffer, LoadedDataLandRigidBodyPtrOffset),
            DefaultQuadTextures = ReadDefaultQuadTextureDiagnostics(loadedDataBuffer),
            QuadTextureArrays = ReadQuadTextureArrayDiagnostics(loadedDataBuffer),
            PercentArrays = ReadPercentArrayDiagnostics(loadedDataBuffer),
            GrassMapWords = ReadGrassMapWords(loadedDataBuffer)
        };
    }

    private IReadOnlyList<RuntimePointerDiagnostic> ReadDoublePointerArrayDiagnostics(
        byte[] buffer,
        int ptrOffset,
        int slotCount)
    {
        var outer = ReadPointerDiagnostic(buffer, ptrOffset);
        var results = new List<RuntimePointerDiagnostic>(slotCount);
        if (outer.FileOffset is not long outerFileOffset)
        {
            return results;
        }

        var pointerBytes = _context.ReadBytes(outerFileOffset, slotCount * 4);
        if (pointerBytes == null)
        {
            return results;
        }

        for (var slot = 0; slot < slotCount; slot++)
        {
            var innerPointer = BinaryUtils.ReadUInt32BE(pointerBytes, slot * 4);
            results.Add(new RuntimePointerDiagnostic
            {
                Pointer = outer.Pointer,
                FileOffset = outer.FileOffset,
                DereferencedPointer = innerPointer,
                DereferencedFileOffset = _context.VaToFileOffset(innerPointer)
            });
        }

        return results;
    }

    private RuntimePointerDiagnostic ReadPointerDiagnostic(byte[] buffer, int ptrOffset)
    {
        if (ptrOffset < 0 || ptrOffset + 4 > buffer.Length)
        {
            return RuntimePointerDiagnostic.Empty;
        }

        var pointer = BinaryUtils.ReadUInt32BE(buffer, ptrOffset);
        return new RuntimePointerDiagnostic
        {
            Pointer = pointer,
            FileOffset = _context.VaToFileOffset(pointer)
        };
    }

    private RuntimePointerDiagnostic ReadDoublePointerDiagnostic(byte[] buffer, int ptrOffset)
    {
        var trace = ReadPointerDiagnostic(buffer, ptrOffset);
        if (trace.FileOffset is not long outerFileOffset)
        {
            return trace;
        }

        var innerBytes = _context.ReadBytes(outerFileOffset, 4);
        if (innerBytes == null)
        {
            return trace;
        }

        var innerPointer = BinaryUtils.ReadUInt32BE(innerBytes);
        return trace with
        {
            DereferencedPointer = innerPointer,
            DereferencedFileOffset = _context.VaToFileOffset(innerPointer)
        };
    }

    private IReadOnlyList<RuntimeLandTexturePointerDiagnostic> ReadDefaultQuadTextureDiagnostics(byte[] buffer)
    {
        var results = new List<RuntimeLandTexturePointerDiagnostic>(LoadedDataQuadCount);
        for (var quadrant = 0; quadrant < LoadedDataQuadCount; quadrant++)
        {
            var pointer = ReadPointerDiagnostic(buffer, LoadedDataDefaultQuadTextureOffset + quadrant * 4);
            results.Add(new RuntimeLandTexturePointerDiagnostic
            {
                Quadrant = quadrant,
                Pointer = pointer,
                TextureFormId = pointer.FileOffset.HasValue
                    ? ReadFormIdAtFileOffset(pointer.FileOffset.Value, LandTextureFormType)
                    : null
            });
        }

        return results;
    }

    private IReadOnlyList<RuntimeLandTextureArrayDiagnostic> ReadQuadTextureArrayDiagnostics(byte[] buffer)
    {
        var results = new List<RuntimeLandTextureArrayDiagnostic>(LoadedDataQuadCount);
        for (var quadrant = 0; quadrant < LoadedDataQuadCount; quadrant++)
        {
            var pointer = ReadPointerDiagnostic(buffer, LoadedDataQuadTextureArrayOffset + quadrant * 4);
            var sampledPointerCount = 0;
            var textureFormIds = new List<uint>();

            if (pointer.FileOffset is long arrayFileOffset)
            {
                var bytes = _context.ReadBytes(arrayFileOffset, MaxTextureArrayPointersToSample * 4);
                if (bytes != null)
                {
                    for (var i = 0; i < MaxTextureArrayPointersToSample; i++)
                    {
                        var texturePointer = BinaryUtils.ReadUInt32BE(bytes, i * 4);
                        if (texturePointer == 0)
                        {
                            continue;
                        }

                        sampledPointerCount++;
                        var formId = _context.FollowPointerVaToFormId(texturePointer, LandTextureFormType);
                        if (formId.HasValue)
                        {
                            textureFormIds.Add(formId.Value);
                        }
                    }
                }
            }

            results.Add(new RuntimeLandTextureArrayDiagnostic
            {
                Quadrant = quadrant,
                Pointer = pointer,
                SampledPointerCount = sampledPointerCount,
                ResolvedTextureCount = textureFormIds.Count,
                TextureFormIds = textureFormIds
            });
        }

        return results;
    }

    private IReadOnlyList<RuntimePercentArrayDiagnostic> ReadPercentArrayDiagnostics(byte[] buffer)
    {
        var results = new List<RuntimePercentArrayDiagnostic>(LoadedDataQuadCount);
        for (var quadrant = 0; quadrant < LoadedDataQuadCount; quadrant++)
        {
            var pointer = ReadDoublePointerDiagnostic(buffer, LoadedDataPercentArraysOffset + quadrant * 4);
            var sampledCount = 0;
            var normalCount = 0;
            var unitCount = 0;
            var nonZeroUnitCount = 0;
            float? minValue = null;
            float? maxValue = null;

            if (pointer.DereferencedFileOffset is long dataFileOffset)
            {
                var bytes = _context.ReadBytes(dataFileOffset, PercentArraySamplesToRead * 4);
                if (bytes != null)
                {
                    sampledCount = PercentArraySamplesToRead;
                    for (var i = 0; i < PercentArraySamplesToRead; i++)
                    {
                        var value = BinaryUtils.ReadFloatBE(bytes, i * 4);
                        if (!RuntimeMemoryContext.IsNormalFloat(value))
                        {
                            continue;
                        }

                        normalCount++;
                        minValue = minValue.HasValue ? Math.Min(minValue.Value, value) : value;
                        maxValue = maxValue.HasValue ? Math.Max(maxValue.Value, value) : value;

                        if (value is >= 0f and <= 1f)
                        {
                            unitCount++;
                            if (value > 0.001f)
                            {
                                nonZeroUnitCount++;
                            }
                        }
                    }
                }
            }

            results.Add(new RuntimePercentArrayDiagnostic
            {
                Quadrant = quadrant,
                Pointer = pointer,
                SampledCount = sampledCount,
                NormalFloatCount = normalCount,
                UnitRangeCount = unitCount,
                NonZeroUnitRangeCount = nonZeroUnitCount,
                MinValue = minValue,
                MaxValue = maxValue
            });
        }

        return results;
    }

    private static List<uint> ReadGrassMapWords(byte[] buffer)
    {
        var words = new List<uint>(LoadedDataGrassMapSize / 4);
        for (var offset = LoadedDataGrassMapOffset;
             offset + 4 <= LoadedDataGrassMapOffset + LoadedDataGrassMapSize;
             offset += 4)
        {
            if (offset + 4 > buffer.Length)
            {
                break;
            }

            words.Add(BinaryUtils.ReadUInt32BE(buffer, offset));
        }

        return words;
    }

    private uint? ReadFormIdAtFileOffset(long fileOffset, byte expectedFormType)
    {
        var buffer = _context.ReadBytes(fileOffset, TesFormHeaderReadSize);
        if (buffer == null)
        {
            return null;
        }

        var formType = buffer[4];
        if (formType != expectedFormType)
        {
            return null;
        }

        var formId = BinaryUtils.ReadUInt32BE(buffer, TesFormFormIdOffset);
        return formId is 0 or 0xFFFFFFFF ? null : formId;
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
            return quadrantMesh;
        }

        // Vertices are required — normals and colors are optional.
        // Read the full 1089-slot maximum buffer, but use a low valid-float threshold here:
        // lower LOD captures can contain only 4x4/5x5/8x8/etc. useful vertices followed by garbage.
        // The RuntimeTerrainMesh grid detector does the real validation from XY coverage and bounds.
        var (vertices, vertexOffset) = ReadDoubleIndirectedFloatArray(
            loadedDataBuffer, LoadedDataVerticesPtrOffset,
            3, RuntimeTerrainMesh.VertexCount, 200_000, 0.01);

        if (vertices == null)
        {
            return null;
        }

        var terrainMesh = new RuntimeTerrainMesh
        {
            Vertices = vertices,
            VertexDataOffset = vertexOffset
        };

        var reconstruction = RuntimeTerrainGridReconstructionService.Reconstruct(terrainMesh);
        if (reconstruction == null)
        {
            return null;
        }

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

        return result;
    }

    /// <summary>
    ///     Probe a known DIAL runtime struct to determine the correct dump shift.
    ///     Tries +0, +4, +8, +16 shift hypotheses and logs which one produces valid data.
    ///     Returns the best shift value, or -1 if none worked.
    /// </summary>
    public int ProbeDialTopicLayout(RuntimeEditorIdEntry entry)
    {
        if (entry.TesFormOffset == null)
        {
            return -1;
        }

        var offset = entry.TesFormOffset.Value;
        var readSize = 96; // Read extra bytes to accommodate larger shifts
        if (offset + readSize > _context.FileSize)
        {
            return -1;
        }

        var buffer = new byte[readSize];
        try
        {
            _context.Accessor.ReadArray(offset, buffer, 0, readSize);
        }
        catch
        {
            return -1;
        }

        // Validate FormID at +12 (no shift — standard TESForm header)
        var formId = BinaryUtils.ReadUInt32BE(buffer, 12);
        if (formId != entry.FormId)
        {
            return -1;
        }

        var sample = new DialProbeSample(entry, offset, buffer);
        var candidates = new List<RuntimeLayoutProbeCandidate<int>>
        {
            new("Shift +0", 0),
            new("Shift +4", 4),
            new("Shift +8", 8),
            new("Shift +16", 16)
        };

        var result = RuntimeLayoutProbeEngine.Probe(
            [sample],
            candidates,
            (probeSample, candidate) => ScoreDialCandidate(probeSample, candidate.Layout),
            "DIAL Probe",
            Logger.Instance.Info,
            probeSample =>
                $"Entry: {probeSample.Entry.EditorId} (FormID 0x{probeSample.Entry.FormId:X8}), TesFormOffset=0x{probeSample.Offset:X}",
            true);

        return result.WinnerScore > 0 ? result.Winner.Layout : -1;
    }

    private RuntimeLayoutProbeScore ScoreDialCandidate(DialProbeSample sample, int shift)
    {
        var score = 0;
        var details = new StringBuilder();

        // Check BSStringT for FullName at PDB+28+shift
        var bstOff = 28 + shift;
        if (bstOff + 8 <= sample.Buffer.Length)
        {
            var pStr = BinaryUtils.ReadUInt32BE(sample.Buffer, bstOff);
            var sLen = BinaryUtils.ReadUInt16BE(sample.Buffer, bstOff + 4);
            var strValid = pStr != 0 && sLen > 0 && sLen < 256 && _context.IsValidPointer(pStr);
            if (strValid)
            {
                var name = _context.ReadBsStringT(sample.Offset, bstOff);
                if (name != null)
                {
                    details.Append($"FullName=\"{name}\" OK, ");
                    score += 3;
                }
                else
                {
                    details.Append("FullName=<ptr valid but string unreadable>, ");
                    score += 1;
                }
            }
            else
            {
                details.Append($"FullName=<invalid ptr=0x{pStr:X8} len={sLen}>, ");
            }
        }

        // Check m_Data.type at PDB+36+shift (should be 0-7)
        var typeOff = 36 + shift;
        if (typeOff < sample.Buffer.Length)
        {
            var topicType = sample.Buffer[typeOff];
            if (topicType <= 7)
            {
                details.Append($"type={topicType} OK, ");
                score += 2;
            }
            else
            {
                details.Append($"type={topicType} FAIL, ");
            }
        }

        // Check m_Data.cFlags at PDB+37+shift (should be 0-3, only bits 0-1 used)
        var flagsOff = 37 + shift;
        if (flagsOff < sample.Buffer.Length)
        {
            var flags = sample.Buffer[flagsOff];
            if (flags <= 3)
            {
                details.Append($"flags={flags} OK, ");
                score += 1;
            }
            else
            {
                details.Append($"flags=0x{flags:X2} FAIL, ");
            }
        }

        // Check m_fPriority at PDB+40+shift (should be a reasonable float, typically 50.0)
        var priorityOff = 40 + shift;
        if (priorityOff + 4 <= sample.Buffer.Length)
        {
            var priority = BinaryUtils.ReadFloatBE(sample.Buffer, priorityOff);
            if (RuntimeMemoryContext.IsNormalFloat(priority) && priority >= 0 && priority <= 200)
            {
                details.Append($"priority={priority:F1} OK, ");
                score += 2;
            }
            else
            {
                details.Append($"priority={priority:F1} FAIL, ");
            }
        }

        // Check m_uiTopicCount at PDB+68+shift (should be a reasonable count, 0-10000)
        var countOff = 68 + shift;
        if (countOff + 4 <= sample.Buffer.Length)
        {
            var count = BinaryUtils.ReadUInt32BE(sample.Buffer, countOff);
            if (count <= 10000)
            {
                details.Append($"topicCount={count} OK");
                score += 1;
            }
            else
            {
                details.Append($"topicCount={count} FAIL");
            }
        }

        return new RuntimeLayoutProbeScore(score, 9, details.ToString());
    }

    private sealed record DialProbeSample(
        RuntimeEditorIdEntry Entry,
        long Offset,
        byte[] Buffer);

    #region World/Land Struct Layout

    // TESObjectLAND: PDB size 44, Debug dump 48, Release dump 60
    private int LandStructSize => 44 + _s;

    private int LandParentCellPtrOffset => 32 + _s;

    private int LandLoadedDataPtrOffset => 40 + _s;

    // LoadedLandData: 164 bytes — standalone struct, identical across all builds
    private const int LoadedDataSize = 164;
    private const int LoadedDataMeshPtrOffset = 0; // NiPointer<NiTriShape>** ppMesh
    private const int LoadedDataVerticesPtrOffset = 4; // NiPoint3** ppVertices
    private const int LoadedDataNormalsPtrOffset = 8; // NiPoint3** ppNormals
    private const int LoadedDataColorsPtrOffset = 12; // NiColorA** ppColorsA
    private const int LoadedDataNormalsSetPtrOffset = 16; // bool** ppNormalsSet
    private const int LoadedDataBorderPtrOffset = 20; // NiPointer<NiLines> spBorder
    private const int LoadedDataHeightExtentsOffset = 24; // NiPoint2: min/max terrain heights
    private const int LoadedDataDefaultQuadTextureOffset = 32; // TESLandTexture* pDefQuadTexture[4]
    private const int LoadedDataQuadTextureArrayOffset = 48; // TESLandTexture** pQuadTextureArray[4]
    private const int LoadedDataPercentArraysOffset = 64; // float** ppPercentArrays[4]
    private const int LoadedDataMoppCodePtrOffset = 80; // hkpMoppCode* pMoppCode
    private const int LoadedDataGrassMapOffset = 84; // NiTPointerMap<unsigned int,TESGrassAreaParam**> pmGrassMap[4]
    private const int LoadedDataGrassMapSize = 64;
    private const int LoadedDataLandRigidBodyPtrOffset = 148; // NiPointer<bhkRigidBody> spLandRB
    private const int LoadedDataCellXOffset = 152;
    private const int LoadedDataCellYOffset = 156;
    private const int LoadedDataBaseHeightOffset = 160;
    private const int LoadedDataQuadCount = 4;
    private const int MaxTextureArrayPointersToSample = 64;
    private const int PercentArraySamplesToRead = 17 * 17;
    private const int TesFormHeaderReadSize = 16;
    private const int TesFormFormIdOffset = 12;
    private const byte LandTextureFormType = 0x12;
    private const int TerrainQuadrantCount = RuntimeTerrainQuadrantMeshBuilder.QuadrantCount;

    #endregion
}
