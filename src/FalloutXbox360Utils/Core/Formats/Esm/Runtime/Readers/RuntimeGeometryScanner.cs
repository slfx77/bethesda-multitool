using System.Collections.Concurrent;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm;

/// <summary>
///     Scans memory dumps for NiTriShapeData and NiTriStripsData runtime objects.
///     Uses heuristic pattern matching based on PDB-derived struct layouts.
///     These are Gamebryo engine objects (NOT TESForm-derived), so they cannot
///     be found through the pAllForms hash table — heuristic scanning is required.
///
///     NiRefObject (8 bytes — base of all Gamebryo objects):
///       +0   vtable (ptr, 4 bytes)
///       +4   m_uiRefCount (uint32 BE) — reference count, >0 for live objects
///
///     NiGeometryData (64 bytes, extends NiObject):
///       +8   m_usVertices (uint16 BE) — vertex count
///       +10  m_usID (uint16 BE) — unique ID
///       +12  m_usDataFlags (uint16 BE)
///       +14  m_usDirtyFlags (uint16 BE)
///       +16  m_kBound (NiBound: center XYZ float3 + radius float, 16 bytes)
///       +32  m_pkVertex (NiPoint3*) — vertex position array
///       +36  m_pkNormal (NiPoint3*) — vertex normal array
///       +40  m_pkColor (NiColorA*) — vertex color array
///       +44  m_pkTexture (NiPoint2*) — UV coordinate array
///       +48  m_spAdditionalGeomData (smart ptr)
///       +52  m_pkBuffData (ptr) — GPU buffer data
///       +56  m_ucKeepFlags, +57 m_ucCompressFlags, +58-60 bools
///
///     NiTriBasedGeomData (68 bytes, extends NiGeometryData):
///       +64  m_usTriangles (uint16 BE) — triangle count (must be >0)
///
///     NiTriShapeData (88 bytes, extends NiTriBasedGeomData):
///       +68  m_uiTriListLength (uint32 BE) — triangle index count (= triangles * 3)
///       +72  m_pusTriList (uint16*) — triangle index array
///
///     NiTriStripsData (80 bytes, extends NiTriBasedGeomData):
///       +68  m_usStrips (uint16 BE) — number of triangle strips
///       +72  m_pusStripLengths (uint16*) — length of each strip
///       +76  m_pusStripLists (uint16*) — strip index array
/// </summary>
internal sealed class RuntimeGeometryScanner(RuntimeMemoryContext context)
{
    private readonly RuntimeMemoryContext _context = context;
    private readonly RuntimeObjectScanner _scanner = new(context);

    #region NiRefObject Field Offsets (PDB-verified)

    private const int RefCountOffset = 4;          // m_uiRefCount: uint32 BE

    #endregion

    #region NiGeometryData Field Offsets (PDB-verified, no shift)

    private const int VertexCountOffset = 8;       // m_usVertices: uint16 BE
    private const int BoundCenterOffset = 16;      // m_kBound.center: NiPoint3 (12 bytes)
    private const int BoundRadiusOffset = 28;      // m_kBound.radius: float BE
    private const int VertexPtrOffset = 32;        // m_pkVertex: NiPoint3*
    private const int NormalPtrOffset = 36;        // m_pkNormal: NiPoint3*
    private const int ColorPtrOffset = 40;         // m_pkColor: NiColorA*
    private const int UVPtrOffset = 44;            // m_pkTexture: NiPoint2*

    #endregion

    #region NiTriBasedGeomData Field Offsets (PDB-verified)

    private const int TriangleCountOffset = 64;    // m_usTriangles: uint16 BE

    #endregion

    #region NiTriShapeData Field Offsets (PDB-verified)

    private const int TriListLengthOffset = 68;    // m_uiTriListLength: uint32 BE
    private const int TriListPtrOffset = 72;       // m_pusTriList: uint16*
    private const int TriShapeStructSize = 88;

    #endregion

    #region NiTriStripsData Field Offsets (PDB-verified)

    private const int StripCountOffset = 68;       // m_usStrips: uint16 BE
    private const int StripLengthsPtrOffset = 72;  // m_pusStripLengths: uint16*
    private const int StripListsPtrOffset = 76;    // m_pusStripLists: uint16*
    private const int TriStripsStructSize = 80;

    #endregion

    #region Validation Thresholds

    private const int MinVertices = 3;
    private const int MaxVertices = 65535;
    private const int MaxRefCount = 10_000;
    private const float MaxCoordinate = 500_000f;
    private const float MinSpatialExtent = 0.1f;
    private const float MaxSpatialExtent = 200_000f;
    private const float ValidFloatThreshold = 0.5f;

    #endregion

    /// <summary>
    ///     Scan the entire dump for NiTriShapeData and NiTriStripsData objects.
    ///     Returns a deduplicated list of extracted meshes.
    ///     Thread-safe: processCandidate may be called concurrently from multiple threads.
    /// </summary>
    /// <summary>
    ///     Shared counter incremented each time a unique mesh is found.
    ///     Safe to read from progress callbacks on any thread.
    /// </summary>
    public int MeshesFound => _meshesFound;

    private int _meshesFound;

    public List<ExtractedMesh> ScanForMeshes(IProgress<(long Scanned, long Total)>? progress = null)
    {
        var meshes = new ConcurrentBag<ExtractedMesh>();
        var vertexHashes = new ConcurrentDictionary<long, byte>();
        _meshesFound = 0;
        var log = Logger.Instance;

        log.Info("Geometry scanner: starting parallel dump scan ({0:N0} bytes)", _context.FileSize);

        _scanner.ScanAligned(
            candidateTest: FastFilter,
            processCandidate: (chunk, offset, fileOffset) =>
            {
                var mesh = ValidateAndExtract(chunk, offset, fileOffset);
                if (mesh != null && vertexHashes.TryAdd(mesh.VertexHash, 0))
                {
                    meshes.Add(mesh);
                    Interlocked.Increment(ref _meshesFound);
                    log.Debug(
                        "  Found {0} at 0x{1:X}: {2} vertices, {3} triangles, bound radius {4:F1}",
                        mesh.Type, fileOffset, mesh.VertexCount, mesh.TriangleCount, mesh.BoundRadius);
                }
            },
            minStructSize: TriShapeStructSize,
            progress: progress);

        var result = meshes.OrderBy(m => m.SourceOffset).ToList();

        log.Info("Geometry scanner: found {0} unique meshes ({1} duplicates filtered)",
            result.Count, vertexHashes.Count - result.Count);

        return result;
    }

    /// <summary>
    ///     Fast filter applied at every 16-byte aligned offset.
    ///     Rejects non-candidates quickly before expensive pointer dereferencing.
    ///     Each check is ordered cheapest-first (local byte reads before pointer validation).
    /// </summary>
    private bool FastFilter(byte[] chunk, int offset)
    {
        if (offset + TriShapeStructSize > chunk.Length)
        {
            return false;
        }

        // Check 1: m_uiRefCount at +4 must be > 0 (live object) and reasonable
        var refCount = BinaryUtils.ReadUInt32BE(chunk, offset + RefCountOffset);
        if (refCount == 0 || refCount > MaxRefCount)
        {
            return false;
        }

        // Check 2: m_usVertices at +8 must be in [3, 65535]
        var vertexCount = BinaryUtils.ReadUInt16BE(chunk, offset + VertexCountOffset);
        if (vertexCount < MinVertices || vertexCount > MaxVertices)
        {
            return false;
        }

        // Check 3: m_usTriangles at +64 (NiTriBasedGeomData) must be > 0.
        // This is the single most discriminating check — rejects any NiGeometryData
        // that isn't a tri-based subclass, and any false positive with zeros here.
        var triangleCount = BinaryUtils.ReadUInt16BE(chunk, offset + TriangleCountOffset);
        if (triangleCount == 0)
        {
            return false;
        }

        // Check 4: Bounding sphere radius at +28 should be a reasonable positive float
        var boundRadius = BinaryUtils.ReadFloatBE(chunk, offset + BoundRadiusOffset);
        if (!RuntimeMemoryContext.IsNormalFloat(boundRadius) || boundRadius <= 0 ||
            boundRadius > MaxSpatialExtent)
        {
            return false;
        }

        // Check 5: m_pkVertex at +32 must be a valid pointer
        var vertexPtr = BinaryUtils.ReadUInt32BE(chunk, offset + VertexPtrOffset);
        if (!_context.IsValidPointer(vertexPtr))
        {
            return false;
        }

        // Check 6: At least one optional pointer (normal, UV) should be non-null and valid.
        // Both being null means this is likely not real geometry data.
        var normalPtr = BinaryUtils.ReadUInt32BE(chunk, offset + NormalPtrOffset);
        var uvPtr = BinaryUtils.ReadUInt32BE(chunk, offset + UVPtrOffset);

        if (normalPtr == 0 && uvPtr == 0)
        {
            return false;
        }

        // If non-null, each must be a valid pointer (reject garbage values)
        if (normalPtr != 0 && !_context.IsValidPointer(normalPtr))
        {
            return false;
        }

        if (uvPtr != 0 && !_context.IsValidPointer(uvPtr))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    ///     Full validation and extraction for a candidate NiGeometryData struct.
    ///     Follows pointers to read vertex data, validates spatial extent,
    ///     then attempts to identify as NiTriShapeData or NiTriStripsData.
    /// </summary>
    private ExtractedMesh? ValidateAndExtract(byte[] chunk, int offset, long fileOffset)
    {
        var vertexCount = BinaryUtils.ReadUInt16BE(chunk, offset + VertexCountOffset);

        // Read bounding sphere
        var boundCx = BinaryUtils.ReadFloatBE(chunk, offset + BoundCenterOffset);
        var boundCy = BinaryUtils.ReadFloatBE(chunk, offset + BoundCenterOffset + 4);
        var boundCz = BinaryUtils.ReadFloatBE(chunk, offset + BoundCenterOffset + 8);
        var boundRadius = BinaryUtils.ReadFloatBE(chunk, offset + BoundRadiusOffset);

        // Read vertex positions
        var vertexPtr = BinaryUtils.ReadUInt32BE(chunk, offset + VertexPtrOffset);
        var vertices = ReadFloatArray(vertexPtr, vertexCount * 3, MaxCoordinate);
        if (vertices == null)
        {
            return null;
        }

        // Validate spatial extent — reject degenerate meshes
        if (!ValidateSpatialExtent(vertices, vertexCount))
        {
            return null;
        }

        // Read optional arrays
        var normalPtr = BinaryUtils.ReadUInt32BE(chunk, offset + NormalPtrOffset);
        var normals = normalPtr != 0 ? ReadFloatArray(normalPtr, vertexCount * 3, 2.0f) : null;

        var uvPtr = BinaryUtils.ReadUInt32BE(chunk, offset + UVPtrOffset);
        var uvs = uvPtr != 0 ? ReadFloatArray(uvPtr, vertexCount * 2, 100.0f) : null;

        var colorPtr = BinaryUtils.ReadUInt32BE(chunk, offset + ColorPtrOffset);
        var colors = colorPtr != 0 ? ReadFloatArray(colorPtr, vertexCount * 4, 2.0f) : null;

        // Read m_usTriangles from NiTriBasedGeomData (+64) — already validated > 0 in FastFilter
        var triangleCountField = BinaryUtils.ReadUInt16BE(chunk, offset + TriangleCountOffset);

        // Try to identify as NiTriShapeData first (more common than strips)
        ushort[]? triangleIndices = null;
        var meshType = MeshType.TriShape;

        if (offset + TriShapeStructSize <= chunk.Length)
        {
            var triListLength = BinaryUtils.ReadUInt32BE(chunk, offset + TriListLengthOffset);
            var triListPtr = BinaryUtils.ReadUInt32BE(chunk, offset + TriListPtrOffset);

            // Cross-validate: triListLength should equal triangleCountField * 3 for NiTriShapeData
            var expectedIndexCount = (uint)triangleCountField * 3;

            // Validate: index count must be divisible by 3, match triangle count,
            // and not exceed reasonable bounds
            if (triListLength > 0 && triListLength % 3 == 0 &&
                triListLength == expectedIndexCount &&
                triListLength <= (uint)vertexCount * 6 &&
                _context.IsValidPointer(triListPtr))
            {
                triangleIndices = ReadIndexArray(triListPtr, (int)triListLength, vertexCount);
            }
        }

        // If not NiTriShapeData, try NiTriStripsData
        if (triangleIndices == null && offset + TriStripsStructSize <= chunk.Length)
        {
            triangleIndices = TryReadTriStrips(chunk, offset, vertexCount);
            if (triangleIndices != null)
            {
                meshType = MeshType.TriStrips;
            }
        }

        // Require triangle indices — vertex-only results are almost always false positives
        if (triangleIndices == null)
        {
            return null;
        }

        // Compute vertex hash for deduplication (first 8 vertices)
        var hashVertexCount = Math.Min((int)vertexCount, 8);
        var hash = ComputeVertexHash(vertices, hashVertexCount);

        return new ExtractedMesh
        {
            Type = meshType,
            VertexCount = vertexCount,
            Vertices = vertices,
            Normals = normals,
            UVs = uvs,
            VertexColors = colors,
            TriangleIndices = triangleIndices,
            SourceOffset = fileOffset,
            VertexHash = hash,
            BoundCenterX = RuntimeMemoryContext.IsNormalFloat(boundCx) ? boundCx : 0,
            BoundCenterY = RuntimeMemoryContext.IsNormalFloat(boundCy) ? boundCy : 0,
            BoundCenterZ = RuntimeMemoryContext.IsNormalFloat(boundCz) ? boundCz : 0,
            BoundRadius = boundRadius
        };
    }

    /// <summary>
    ///     Read a float array by following a single pointer (direct, not double-indirected).
    ///     NiGeometryData stores direct pointers to arrays, unlike terrain's T** pattern.
    /// </summary>
    private float[]? ReadFloatArray(uint pointer, int floatCount, float maxAbsValue)
    {
        var dataOffset = _context.VaToFileOffset(pointer);
        if (dataOffset == null)
        {
            return null;
        }

        var byteCount = floatCount * 4;
        var rawData = _context.ReadBytes(dataOffset.Value, byteCount);
        if (rawData == null)
        {
            return null;
        }

        // Parse big-endian floats with validation
        var result = new float[floatCount];
        var validCount = 0;
        for (var i = 0; i < floatCount; i++)
        {
            result[i] = BinaryUtils.ReadFloatBE(rawData, i * 4);
            if (RuntimeMemoryContext.IsNormalFloat(result[i]) && MathF.Abs(result[i]) <= maxAbsValue)
            {
                validCount++;
            }
        }

        // Require at least 50% valid floats (same quality gate as RuntimeWorldReader)
        if (validCount < floatCount * ValidFloatThreshold)
        {
            return null;
        }

        return result;
    }

    /// <summary>
    ///     Read a uint16 index array and validate all indices are within vertex count.
    /// </summary>
    private ushort[]? ReadIndexArray(uint pointer, int indexCount, int vertexCount)
    {
        var dataOffset = _context.VaToFileOffset(pointer);
        if (dataOffset == null)
        {
            return null;
        }

        var byteCount = indexCount * 2;
        var rawData = _context.ReadBytes(dataOffset.Value, byteCount);
        if (rawData == null)
        {
            return null;
        }

        var result = new ushort[indexCount];
        for (var i = 0; i < indexCount; i++)
        {
            result[i] = BinaryUtils.ReadUInt16BE(rawData, i * 2);
            if (result[i] >= vertexCount)
            {
                return null;
            }
        }

        return result;
    }

    /// <summary>
    ///     Try to read NiTriStripsData triangle strips and convert to a triangle list.
    /// </summary>
    private ushort[]? TryReadTriStrips(byte[] chunk, int offset, int vertexCount)
    {
        var stripCount = BinaryUtils.ReadUInt16BE(chunk, offset + StripCountOffset);
        if (stripCount == 0 || stripCount > 1000)
        {
            return null;
        }

        var stripLengthsPtr = BinaryUtils.ReadUInt32BE(chunk, offset + StripLengthsPtrOffset);
        var stripListsPtr = BinaryUtils.ReadUInt32BE(chunk, offset + StripListsPtrOffset);

        if (!_context.IsValidPointer(stripLengthsPtr) || !_context.IsValidPointer(stripListsPtr))
        {
            return null;
        }

        // Read strip lengths
        var lengthsOffset = _context.VaToFileOffset(stripLengthsPtr);
        if (lengthsOffset == null)
        {
            return null;
        }

        var lengthsData = _context.ReadBytes(lengthsOffset.Value, stripCount * 2);
        if (lengthsData == null)
        {
            return null;
        }

        var stripLengths = new ushort[stripCount];
        var totalIndices = 0;
        for (var i = 0; i < stripCount; i++)
        {
            stripLengths[i] = BinaryUtils.ReadUInt16BE(lengthsData, i * 2);
            if (stripLengths[i] < 3 || stripLengths[i] > 10_000)
            {
                return null;
            }

            totalIndices += stripLengths[i];
        }

        if (totalIndices > vertexCount * 6)
        {
            return null;
        }

        // Read all strip indices
        var allIndices = ReadIndexArray(stripListsPtr, totalIndices, vertexCount);
        if (allIndices == null)
        {
            return null;
        }

        // Convert strips to triangle list
        var triangles = new List<ushort>();
        var indexPos = 0;
        for (var s = 0; s < stripCount; s++)
        {
            var len = stripLengths[s];
            for (var i = 2; i < len; i++)
            {
                var a = allIndices[indexPos + i - 2];
                var b = allIndices[indexPos + i - 1];
                var c = allIndices[indexPos + i];

                // Skip degenerate triangles
                if (a == b || b == c || a == c)
                {
                    continue;
                }

                // Alternate winding for even/odd triangles in strip
                if (i % 2 == 0)
                {
                    triangles.Add(a);
                    triangles.Add(b);
                    triangles.Add(c);
                }
                else
                {
                    triangles.Add(a);
                    triangles.Add(c);
                    triangles.Add(b);
                }
            }

            indexPos += len;
        }

        return triangles.Count >= 3 ? triangles.ToArray() : null;
    }

    /// <summary>
    ///     Validate that vertex positions have a reasonable spatial extent.
    ///     Rejects degenerate meshes where all vertices collapse to a point.
    /// </summary>
    private static bool ValidateSpatialExtent(float[] vertices, int vertexCount)
    {
        var minX = float.MaxValue;
        var maxX = float.MinValue;
        var minY = float.MaxValue;
        var maxY = float.MinValue;
        var minZ = float.MaxValue;
        var maxZ = float.MinValue;

        for (var i = 0; i < vertexCount; i++)
        {
            var x = vertices[i * 3];
            var y = vertices[i * 3 + 1];
            var z = vertices[i * 3 + 2];

            if (!RuntimeMemoryContext.IsNormalFloat(x) || !RuntimeMemoryContext.IsNormalFloat(y) ||
                !RuntimeMemoryContext.IsNormalFloat(z))
            {
                continue;
            }

            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
            if (z < minZ) minZ = z;
            if (z > maxZ) maxZ = z;
        }

        var rangeX = maxX - minX;
        var rangeY = maxY - minY;
        var rangeZ = maxZ - minZ;

        // At least one dimension must have reasonable extent
        var maxRange = MathF.Max(rangeX, MathF.Max(rangeY, rangeZ));
        return maxRange >= MinSpatialExtent && maxRange <= MaxSpatialExtent;
    }

    /// <summary>
    ///     Compute a hash of the first N vertex positions for deduplication.
    ///     Two NiGeometryData structs sharing the same vertex buffer will produce the same hash.
    /// </summary>
    private static long ComputeVertexHash(float[] vertices, int vertexCount)
    {
        var hash = 17L;
        var floatCount = Math.Min(vertexCount * 3, vertices.Length);
        for (var i = 0; i < floatCount; i++)
        {
            hash = hash * 31 + BitConverter.SingleToInt32Bits(vertices[i]);
        }

        return hash;
    }
}
