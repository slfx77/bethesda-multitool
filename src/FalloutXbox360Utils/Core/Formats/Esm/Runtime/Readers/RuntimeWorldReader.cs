using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Minidump;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm;

/// <summary>
///     Reader for TESObjectLAND and DIAL runtime structs from Xbox 360 memory dumps.
///     Extracts cell coordinates, loaded land data, and probes dialogue topic layouts.
/// </summary>
internal sealed class RuntimeWorldReader(RuntimeMemoryContext context)
{
    private readonly RuntimeMemoryContext _context = context;

    // Build-specific offset shift: Proto Debug PDB + _s = actual dump offset.
    private readonly int _s = RuntimeBuildOffsets.GetPdbShift(
        MinidumpAnalyzer.DetectBuildType(context.MinidumpInfo));

    #region World/Land Struct Layout

    // TESObjectLAND: PDB size 44, Debug dump 48, Release dump 60
    private int LandStructSize => 44 + _s;
    private int LandLoadedDataPtrOffset => 40 + _s;
    // LoadedLandData: 164 bytes — standalone struct, identical across all builds
    private const int LoadedDataSize = 164;
    private const int LoadedDataVerticesPtrOffset = 4;    // NiPoint3** ppVertices
    private const int LoadedDataNormalsPtrOffset = 8;     // NiPoint3** ppNormals
    private const int LoadedDataColorsPtrOffset = 12;     // NiColorA** ppColorsA
    private const int LoadedDataHeightExtentsOffset = 24; // NiPoint2: min/max terrain heights
    private const int LoadedDataCellXOffset = 152;
    private const int LoadedDataCellYOffset = 156;
    private const int LoadedDataBaseHeightOffset = 160;

    #endregion

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

        // Extract terrain mesh from heap pointers (ppVertices, ppNormals, ppColorsA)
        var terrainMesh = ReadTerrainMesh(loadedDataBuffer);

        return new RuntimeLoadedLandData
        {
            FormId = formId,
            CellX = cellX,
            CellY = cellY,
            BaseHeight = baseHeight,
            MinHeight = minHeight,
            MaxHeight = maxHeight,
            LandOffset = offset,
            LoadedDataOffset = loadedDataFileOffset.Value,
            TerrainMesh = terrainMesh
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
        // Vertices are required — normals and colors are optional
        var (vertices, vertexOffset) = ReadDoubleIndirectedFloatArray(
            loadedDataBuffer, LoadedDataVerticesPtrOffset,
            floatsPerElement: 3, RuntimeTerrainMesh.VertexCount, maxAbsValue: 200_000);

        if (vertices == null)
        {
            return null;
        }

        // Reject degenerate meshes by checking vertex coordinate ranges.
        // Real Gamebryo terrain cells span exactly 4096×4096 world units.
        // If the X or Y range is far from ~4096, the data is garbage.
        var minX = float.MaxValue;
        var maxX = float.MinValue;
        var minY = float.MaxValue;
        var maxY = float.MinValue;
        for (var i = 0; i < RuntimeTerrainMesh.VertexCount; i++)
        {
            var x = vertices[i * 3];
            var y = vertices[i * 3 + 1];
            if (RuntimeMemoryContext.IsNormalFloat(x) && Math.Abs(x) <= 200_000)
            {
                minX = Math.Min(minX, x);
                maxX = Math.Max(maxX, x);
            }

            if (RuntimeMemoryContext.IsNormalFloat(y) && Math.Abs(y) <= 200_000)
            {
                minY = Math.Min(minY, y);
                maxY = Math.Max(maxY, y);
            }
        }

        var rangeX = maxX - minX;
        var rangeY = maxY - minY;

        // Expected range is ~4096 (32 quads × 128 units each). Reject if range is
        // too small (<1000) or too large (>10000) — clearly not a real terrain cell.
        if (rangeX < 1000 || rangeX > 10000 || rangeY < 1000 || rangeY > 10000)
        {
            return null;
        }

        // Try normals (NiPoint3, components should be in [-1, 1] but allow some tolerance)
        var (normals, _) = ReadDoubleIndirectedFloatArray(
            loadedDataBuffer, LoadedDataNormalsPtrOffset,
            floatsPerElement: 3, RuntimeTerrainMesh.VertexCount, maxAbsValue: 2.0f);

        // Try vertex colors (NiColorA = RGBA, components in [0, 1])
        var (colors, _) = ReadDoubleIndirectedFloatArray(
            loadedDataBuffer, LoadedDataColorsPtrOffset,
            floatsPerElement: 4, RuntimeTerrainMesh.VertexCount, maxAbsValue: 2.0f);

        return new RuntimeTerrainMesh
        {
            Vertices = vertices,
            Normals = normals,
            Colors = colors,
            VertexDataOffset = vertexOffset
        };
    }

    /// <summary>
    ///     Follow a double-indirected pointer (T**) from the LoadedLandData buffer to read a float array.
    ///     Step 1: Read pointer at ptrOffset → VA of the inner pointer.
    ///     Step 2: Dereference inner pointer → VA of the actual float array.
    ///     Step 3: Read elementCount × floatsPerElement floats from the array.
    /// </summary>
    private (float[]? Data, long FileOffset) ReadDoubleIndirectedFloatArray(
        byte[] loadedDataBuffer, int ptrOffset, int floatsPerElement, int elementCount, float maxAbsValue)
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

        var innerPtr = BinaryUtils.ReadUInt32BE(innerPtrBytes, 0);
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

        // Require at least 70% valid floats to reject garbage terrain data.
        // FaceGen uses 50% but terrain is more sensitive to corruption.
        if (validCount < totalFloats * 0.7)
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

        // Entries are pre-filtered to LAND by EsmEditorIdExtractor (FormType varies by build)
        foreach (var entry in entries)
        {
            var landData = ReadRuntimeLandData(entry);
            if (landData != null)
            {
                result[landData.FormId] = landData;
            }
        }

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

        var log = Logger.Instance;
        log.Info($"  [DIAL Probe] Entry: {entry.EditorId} (FormID 0x{entry.FormId:X8}), TesFormOffset=0x{offset:X}");

        // Try each shift hypothesis
        int[] shifts = [0, 4, 8, 16];
        var bestShift = -1;
        var bestScore = 0;

        foreach (var shift in shifts)
        {
            var score = 0;
            var details = new StringBuilder();
            details.Append($"    Shift +{shift}: ");

            // Check BSStringT for FullName at PDB+28+shift
            var bstOff = 28 + shift;
            if (bstOff + 8 <= buffer.Length)
            {
                var pStr = BinaryUtils.ReadUInt32BE(buffer, bstOff);
                var sLen = BinaryUtils.ReadUInt16BE(buffer, bstOff + 4);
                var strValid = pStr != 0 && sLen > 0 && sLen < 256 && _context.IsValidPointer(pStr);
                if (strValid)
                {
                    // Try to read the actual string
                    var name = _context.ReadBSStringT(offset, bstOff);
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
            if (typeOff < buffer.Length)
            {
                var topicType = buffer[typeOff];
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
            if (flagsOff < buffer.Length)
            {
                var flags = buffer[flagsOff];
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
            if (priorityOff + 4 <= buffer.Length)
            {
                var priority = BinaryUtils.ReadFloatBE(buffer, priorityOff);
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
            if (countOff + 4 <= buffer.Length)
            {
                var count = BinaryUtils.ReadUInt32BE(buffer, countOff);
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

            log.Info(details.ToString());
            log.Info($"      Score: {score}/9");

            if (score > bestScore)
            {
                bestScore = score;
                bestShift = shift;
            }
        }

        if (bestShift >= 0)
        {
            log.Info($"  [DIAL Probe] Best shift: +{bestShift} (score {bestScore}/9)");
        }

        return bestShift;
    }
}
