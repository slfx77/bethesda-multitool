// NIF converter - Schema-driven big-endian to little-endian conversion
// Uses nif.xml definitions for proper field-by-field byte swapping
// Handles BSPackedAdditionalGeometryData expansion for Xbox 360 NIFs

using System.Buffers.Binary;
using static Xbox360MemoryCarver.Core.Formats.Nif.NifEndianUtils;

namespace Xbox360MemoryCarver.Core.Formats.Nif;

/// <summary>
///     Converts Xbox 360 (big-endian) NIF files to PC (little-endian) format.
///     Uses schema-driven conversion based on nif.xml definitions.
///     Handles BSPackedAdditionalGeometryData expansion for geometry blocks.
/// </summary>
internal sealed class NifConverter
{
    // Blocks to strip from output (BSPackedAdditionalGeometryData)
    private readonly HashSet<int> _blocksToStrip = [];

    // Geometry blocks that need expansion, keyed by geometry block index
    private readonly Dictionary<int, GeometryBlockExpansion> _geometryExpansions = [];

    // Maps geometry block index to its associated NiSkinPartition block index
    private readonly Dictionary<int, int> _geometryToSkinPartition = [];

    // Havok collision blocks that need HalfVector3→Vector3 expansion
    private readonly Dictionary<int, HavokBlockExpansion> _havokExpansions = [];

    // Block index → node name mapping from NiDefaultAVObjectPalette
    private readonly Dictionary<int, string> _nodeNamesByBlock = [];

    // New strings to add to the string table (for node names)
    private readonly List<string> _newStrings = [];

    // Maps block index → string table index (for NiNode Name field restoration)
    private readonly Dictionary<int, int> _nodeNameStringIndices = [];

    // Original string count (before adding new strings)
    private int _originalStringCount;

    // Extracted geometry data indexed by packed block index
    private readonly Dictionary<int, PackedGeometryData> _packedGeometryByBlock = [];
    private readonly NifSchema _schema;

    // NiSkinPartition blocks that need bone weights/indices expansion
    private readonly Dictionary<int, SkinPartitionExpansion> _skinPartitionExpansions = [];

    // Maps NiSkinPartition block index to its associated packed geometry data
    private readonly Dictionary<int, PackedGeometryData> _skinPartitionToPackedData = [];

    // Triangles extracted from NiSkinPartition strips, keyed by NiSkinPartition block index
    private readonly Dictionary<int, ushort[]> _skinPartitionTriangles = [];
    private readonly bool _verbose;

    // Vertex maps from NiSkinPartition blocks, keyed by NiSkinPartition block index
    private readonly Dictionary<int, ushort[]> _vertexMaps = [];

    public NifConverter(bool verbose = false)
    {
        _verbose = verbose;
        _schema = NifSchema.LoadEmbedded();
    }

    /// <summary>
    ///     Converts a big-endian NIF file to little-endian.
    /// </summary>
    public ConversionResult Convert(byte[] data)
    {
        try
        {
            // Reset state
            _blocksToStrip.Clear();
            _packedGeometryByBlock.Clear();
            _geometryExpansions.Clear();
            _havokExpansions.Clear();
            _vertexMaps.Clear();
            _skinPartitionTriangles.Clear();
            _geometryToSkinPartition.Clear();
            _skinPartitionExpansions.Clear();
            _skinPartitionToPackedData.Clear();
            _nodeNamesByBlock.Clear();
            _newStrings.Clear();
            _nodeNameStringIndices.Clear();
            _originalStringCount = 0;

            // Parse the NIF header to understand structure
            var info = NifParser.Parse(data);
            if (info == null)
                return new ConversionResult
                {
                    Success = false,
                    ErrorMessage = "Failed to parse NIF header"
                };

            if (!info.IsBigEndian)
                return new ConversionResult
                {
                    Success = true,
                    OutputData = data,
                    SourceInfo = info,
                    ErrorMessage = "File is already little-endian (PC format)"
                };

            if (_verbose)
                Console.WriteLine(
                    $"Converting NIF: {info.BlockCount} blocks, version {info.BinaryVersion:X8}, BS version {info.BsVersion}");

            // Step 0: Parse NiDefaultAVObjectPalette to get node names
            ParseNodeNamesFromPalette(data, info);

            // Step 1: Find BSPackedAdditionalGeometryData blocks and extract geometry
            FindAndExtractPackedGeometry(data, info);

            // Step 1b: Extract vertex maps and triangles from NiSkinPartition blocks (for skinned meshes)
            ExtractVertexMaps(data, info);

            // Step 2: Find geometry blocks that reference packed data and calculate expansions
            FindGeometryExpansions(data, info);

            // Step 2a: Update geometry expansions with triangle sizes (for NiTriShapeData)
            UpdateGeometryExpansionsWithTriangles();

            // Step 2b: Find Havok collision blocks with compressed vertices
            FindHavokExpansions(data, info);

            // Step 2c: Find NiSkinPartition blocks that need bone weights/indices expansion
            FindSkinPartitionExpansions(data, info);

            // Step 3: Calculate block remap (accounting for removed packed blocks)
            var blockRemap = CalculateBlockRemap(info.BlockCount);

            // Step 4: Calculate output size and create buffer
            var outputSize = CalculateOutputSize(data.Length, info);
            var output = new byte[outputSize];

            // Step 5: Convert and write output
            WriteConvertedOutput(data, output, info, blockRemap);

            return new ConversionResult
            {
                Success = true,
                OutputData = output,
                SourceInfo = info
            };
        }
        catch (Exception ex)
        {
            return new ConversionResult
            {
                Success = false,
                ErrorMessage = $"Conversion failed: {ex.Message}"
            };
        }
    }

    /// <summary>
    ///     Parse NiDefaultAVObjectPalette to extract node names.
    ///     Xbox 360 NIFs have NULL names on NiNode/BSFadeNode blocks, but the names
    ///     are preserved in the palette. We restore them by adding the names to the
    ///     string table and updating the Name field on node blocks.
    /// </summary>
    private void ParseNodeNamesFromPalette(byte[] data, NifInfo info)
    {
        var nameMappings = NifPaletteParser.ParseAll(data, info, _verbose);
        if (nameMappings == null)
            return;

        // Store original string count for index calculations
        _originalStringCount = info.Strings.Count;

        // Build a set of existing strings to avoid duplicates
        var existingStrings = new HashSet<string>(info.Strings);

        // For each block→name mapping, determine if we need a new string
        foreach (var (blockIndex, name) in nameMappings.BlockNames)
        {
            // Check if this is a node block (NiNode, BSFadeNode, etc.)
            var typeName = info.GetBlockTypeName(blockIndex);
            if (!IsNodeType(typeName))
                continue;

            _nodeNamesByBlock[blockIndex] = name;

            // Check if the name already exists in the string table
            var existingIndex = info.Strings.IndexOf(name);
            if (existingIndex >= 0)
            {
                _nodeNameStringIndices[blockIndex] = existingIndex;
            }
            else if (existingStrings.Contains(name))
            {
                // Already added as a new string - find its index
                var newIdx = _newStrings.IndexOf(name);
                if (newIdx >= 0)
                    _nodeNameStringIndices[blockIndex] = _originalStringCount + newIdx;
            }
            else
            {
                // Need to add a new string
                _nodeNameStringIndices[blockIndex] = _originalStringCount + _newStrings.Count;
                _newStrings.Add(name);
                existingStrings.Add(name);
            }
        }

        // Handle Accum Root Name for the root BSFadeNode (block 0)
        // The palette doesn't include the root node, so we get the name from NiControllerSequence
        if (nameMappings.AccumRootName != null && !_nodeNamesByBlock.ContainsKey(0))
        {
            var rootTypeName = info.GetBlockTypeName(0);
            if (IsNodeType(rootTypeName))
            {
                var rootName = nameMappings.AccumRootName;
                _nodeNamesByBlock[0] = rootName;

                // Check if the name already exists in the string table
                var existingIndex = info.Strings.IndexOf(rootName);
                if (existingIndex >= 0)
                {
                    _nodeNameStringIndices[0] = existingIndex;
                }
                else if (existingStrings.Contains(rootName))
                {
                    // Already added as a new string - find its index
                    var newIdx = _newStrings.IndexOf(rootName);
                    if (newIdx >= 0)
                        _nodeNameStringIndices[0] = _originalStringCount + newIdx;
                }
                else
                {
                    // Need to add a new string
                    _nodeNameStringIndices[0] = _originalStringCount + _newStrings.Count;
                    _newStrings.Add(rootName);
                    existingStrings.Add(rootName);
                }

                if (_verbose)
                    Console.WriteLine($"  Root node (block 0) name from Accum Root Name: '{rootName}'");
            }
        }

        if (_verbose && _nodeNamesByBlock.Count > 0)
            Console.WriteLine($"  Found {_nodeNamesByBlock.Count} node names from palette/sequence, adding {_newStrings.Count} new strings");
    }

    /// <summary>
    ///     Check if a block type is a node type that has a Name field.
    /// </summary>
    private static bool IsNodeType(string typeName)
    {
        return typeName is "NiNode" or "BSFadeNode" or "BSLeafAnimNode" or "BSTreeNode" or
            "BSOrderedNode" or "BSMultiBoundNode" or "BSMasterParticleSystem" or "NiSwitchNode" or
            "NiBillboardNode" or "NiLODNode" or "BSBlastNode" or "BSDamageStage" or "NiAVObject";
    }

    /// <summary>
    ///     Find BSPackedAdditionalGeometryData blocks and extract their geometry data.
    /// </summary>
    private void FindAndExtractPackedGeometry(byte[] data, NifInfo info)
    {
        foreach (var block in info.Blocks)
            if (block.TypeName == "BSPackedAdditionalGeometryData")
            {
                _blocksToStrip.Add(block.Index);

                var packedData = NifPackedDataExtractor.Extract(
                    data, block.DataOffset, block.Size, info.IsBigEndian, _verbose);

                if (packedData != null)
                {
                    _packedGeometryByBlock[block.Index] = packedData;
                    if (_verbose)
                        Console.WriteLine(
                            $"  Block {block.Index}: BSPackedAdditionalGeometryData - extracted {packedData.NumVertices} vertices");
                }
                else if (_verbose)
                {
                    Console.WriteLine($"  Block {block.Index}: BSPackedAdditionalGeometryData - extraction failed");
                }
            }
    }

    /// <summary>
    ///     Extract vertex maps from NiSkinPartition blocks for skinned meshes.
    ///     Maps geometry blocks to their NiSkinPartition via BSDismemberSkinInstance.
    ///     Chain: NiTriShape -> BSDismemberSkinInstance (refs Data + SkinPartition) -> NiSkinPartition (has VertexMap)
    /// </summary>
    private void ExtractVertexMaps(byte[] data, NifInfo info)
    {
        if (_verbose) Console.WriteLine("  Extracting vertex maps from NiSkinPartition blocks...");

        // First, extract VertexMap and Triangles from all NiSkinPartition blocks
        foreach (var block in info.Blocks)
            if (block.TypeName == "NiSkinPartition")
            {
                if (_verbose)
                    Console.WriteLine(
                        $"    Checking block {block.Index}: NiSkinPartition at offset 0x{block.DataOffset:X}, size {block.Size}");

                var vertexMap = NifSkinPartitionParser.ExtractVertexMap(data, block.DataOffset, block.Size,
                    info.IsBigEndian, _verbose);
                if (vertexMap != null && vertexMap.Length > 0)
                {
                    _vertexMaps[block.Index] = vertexMap;
                    if (_verbose)
                        Console.WriteLine(
                            $"    Block {block.Index}: NiSkinPartition - extracted {vertexMap.Length} vertex mappings");
                }
                else if (_verbose)
                {
                    Console.WriteLine($"    Block {block.Index}: NiSkinPartition - no vertex map found");
                }

                // Also extract triangles from strips for NiTriShapeData expansion
                var triangles = NifSkinPartitionParser.ExtractTriangles(data, block.DataOffset, block.Size,
                    info.IsBigEndian, _verbose);
                if (triangles != null && triangles.Length > 0)
                {
                    _skinPartitionTriangles[block.Index] = triangles;
                    if (_verbose)
                        Console.WriteLine(
                            $"    Block {block.Index}: NiSkinPartition - extracted {triangles.Length / 3} triangles from strips");
                }
            }

        // Now build geometry -> skin partition mapping via BSDismemberSkinInstance
        // Structure: NiTriShape/NiTriStrips has a Skin Instance ref that points to BSDismemberSkinInstance
        // BSDismemberSkinInstance structure:
        //   - Offset 0: Data ref (int) -> NiSkinData
        //   - Offset 4: Skin Partition ref (int) -> NiSkinPartition
        //   - Offset 8: Skeleton Root ref (int)
        //   - etc.
        foreach (var block in info.Blocks.Where(b => b.TypeName is "NiTriShape" or "NiTriStrips"))
        {
            // Find the skin instance ref in the NiTriShape/NiTriStrips block
            var skinInstanceRef = FindSkinInstanceRef(data, block, info);
            if (skinInstanceRef < 0) continue;

            var skinInstanceBlock = info.Blocks.FirstOrDefault(b => b.Index == skinInstanceRef);
            if (skinInstanceBlock?.TypeName is not ("BSDismemberSkinInstance" or "NiSkinInstance")) continue;

            // Read the skin partition ref from offset 4 in the skin instance
            var skinPartitionRefPos = skinInstanceBlock.DataOffset + 4;
            if (skinPartitionRefPos + 4 > data.Length) continue;

            var skinPartitionRef = info.IsBigEndian
                ? BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(skinPartitionRefPos, 4))
                : BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(skinPartitionRefPos, 4));

            if (skinPartitionRef < 0) continue;

            // Find the data ref (NiTriStripsData or NiTriShapeData) in the NiTriShape
            var dataRef = FindDataRef(data, block, info);
            if (dataRef >= 0 && _vertexMaps.ContainsKey(skinPartitionRef))
            {
                _geometryToSkinPartition[dataRef] = skinPartitionRef;
                if (_verbose)
                    Console.WriteLine($"  Mapped geometry block {dataRef} -> NiSkinPartition {skinPartitionRef}");
            }
        }
    }

    /// <summary>
    ///     Find the Skin Instance ref in a NiTriShape/NiTriStrips block.
    ///     NiAVObject layout varies but skin instance is typically at a known offset after common fields.
    /// </summary>
    private static int FindSkinInstanceRef(byte[] data, BlockInfo block, NifInfo info)
    {
        // NiTriShape structure (simplified):
        // - NiAVObject base (name, extra data, controller, flags, transform, properties, collision)
        // - Data ref (int) - the geometry data
        // - Skin Instance ref (int) - what we want
        // For simplicity, we'll look for the skin instance ref at offset (block.Size - 8) to (block.Size - 4)
        // This is because the last two refs are typically Data and SkinInstance
        // Actually, better to parse more carefully.

        // NiAVObject common structure before refs:
        //   Name (SizedString), ExtraDataCount (uint), ExtraData[] (int[]), Controller (int), Flags (ushort),
        //   Transform (34 bytes), NumProperties (uint), Properties[] (int[]), CollisionObject (int)
        // Then NiGeometry adds: Data (int), SkinInstance (int)

        // Since parsing this fully is complex, let's try reading backwards from block end
        // The last int before block end should be SkinInstance, second to last should be Data
        // But this varies by block type and version...

        // More reliable: scan the block for refs in a pattern
        // Actually, we know the structure - let's parse it properly

        var pos = block.DataOffset;
        var end = block.DataOffset + block.Size;
        var isBE = info.IsBigEndian;

        // Skip name (SizedString = 4-byte len + chars)
        if (pos + 4 > end) return -1;
        var nameLen = isBE
            ? BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(pos, 4))
            : BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(pos, 4));
        pos += 4 + Math.Max(0, nameLen);

        // Skip extra data (uint count + int[] refs)
        if (pos + 4 > end) return -1;
        var extraDataCount = isBE
            ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos, 4))
            : BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos, 4));
        pos += 4 + (int)extraDataCount * 4;

        // Skip controller ref
        pos += 4;

        // Skip flags (ushort)
        pos += 2;

        // Skip transform (3x3 matrix + translation + scale = 9*4 + 3*4 + 4 = 52 bytes... actually it's typically 34)
        // Matrix33 (36 bytes) + Vector3 (12 bytes) + float scale (4 bytes) = 52? Or condensed?
        // For Fallout NV: Transform = Rotation(9 floats=36) + Translation(3 floats=12) + Scale(1 float=4) = 52 bytes? No wait...
        // Actually it's typically NiTransform which is Matrix33(36) + Vector3(12) + float(4) = 52 bytes
        // But some versions use a condensed format. Let's use 52 for now.

        // Actually, from nif.xml for 20.2.0.7:
        // NiAVObject has:
        //   - Flags: ushort if version < 20.2.0.7 USV 11, else uint
        //   - Translation: Vector3 (12 bytes)
        //   - Rotation: Matrix33 (36 bytes)
        //   - Scale: float (4 bytes)

        // For version 20.2.0.7 with user version 11, flags is uint (4 bytes)
        // So we need to re-read flags properly
        pos -= 2; // undo the ushort skip
        pos += 4; // skip uint flags instead

        // Translation (12) + Rotation (36) + Scale (4) = 52 bytes
        pos += 52;

        // Skip properties (uint count + int[] refs)
        if (pos + 4 > end) return -1;
        var numProperties = isBE
            ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos, 4))
            : BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos, 4));
        pos += 4 + (int)numProperties * 4;

        // Skip collision object ref
        pos += 4;

        // Now we're at NiGeometry specific data
        // Data ref (int)
        if (pos + 4 > end) return -1;
        pos += 4; // skip data ref

        // Skin Instance ref (int) - this is what we want
        if (pos + 4 > end) return -1;
        var skinInstanceRef = isBE
            ? BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(pos, 4))
            : BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(pos, 4));

        return skinInstanceRef;
    }

    /// <summary>
    ///     Find the Data ref in a NiTriShape/NiTriStrips block.
    /// </summary>
    private static int FindDataRef(byte[] data, BlockInfo block, NifInfo info)
    {
        // Similar to FindSkinInstanceRef but we want the data ref, not skin instance
        var pos = block.DataOffset;
        var end = block.DataOffset + block.Size;
        var isBE = info.IsBigEndian;

        // Skip name
        if (pos + 4 > end) return -1;
        var nameLen = isBE
            ? BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(pos, 4))
            : BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(pos, 4));
        pos += 4 + Math.Max(0, nameLen);

        // Skip extra data
        if (pos + 4 > end) return -1;
        var extraDataCount = isBE
            ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos, 4))
            : BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos, 4));
        pos += 4 + (int)extraDataCount * 4;

        // Skip controller ref
        pos += 4;

        // Skip uint flags
        pos += 4;

        // Skip transform (52 bytes)
        pos += 52;

        // Skip properties
        if (pos + 4 > end) return -1;
        var numProperties = isBE
            ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos, 4))
            : BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos, 4));
        pos += 4 + (int)numProperties * 4;

        // Skip collision object ref
        pos += 4;

        // Data ref - this is what we want
        if (pos + 4 > end) return -1;
        var dataRef = isBE
            ? BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(pos, 4))
            : BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(pos, 4));

        return dataRef;
    }

    /// <summary>
    ///     Find geometry blocks (NiTriStripsData, NiTriShapeData) that reference packed data.
    /// </summary>
    private void FindGeometryExpansions(byte[] data, NifInfo info)
    {
        foreach (var block in info.Blocks.Where(b => b.TypeName is "NiTriStripsData" or "NiTriShapeData"))
        {
            // Parse the geometry block to find its Additional Data reference
            var additionalDataRef = ParseAdditionalDataRef(data, block);

            if (additionalDataRef >= 0 && _packedGeometryByBlock.TryGetValue(additionalDataRef, out var packedData))
            {
                // Check if this is a skinned mesh - affects vertex color handling
                var isSkinned = _geometryToSkinPartition.ContainsKey(block.Index);

                // Calculate size increase needed for expanded geometry
                var expansion = CalculateGeometryExpansion(data, block, packedData, isSkinned);
                if (expansion != null)
                {
                    expansion.BlockIndex = block.Index;
                    expansion.PackedBlockIndex = additionalDataRef;
                    _geometryExpansions[block.Index] = expansion;

                    if (_verbose)
                        Console.WriteLine(
                            $"  Block {block.Index}: {block.TypeName} -> expand from {expansion.OriginalSize} to {expansion.NewSize} bytes");
                }
            }
        }
    }

    /// <summary>
    ///     Update geometry expansion sizes to account for triangle data from NiSkinPartition.
    ///     Called after ExtractVertexMaps has populated _skinPartitionTriangles.
    /// </summary>
    private void UpdateGeometryExpansionsWithTriangles()
    {
        foreach (var kvp in _geometryExpansions)
        {
            var geomBlockIndex = kvp.Key;
            var expansion = kvp.Value;

            // Check if this geometry block has triangles from its skin partition
            if (_geometryToSkinPartition.TryGetValue(geomBlockIndex, out var skinPartIndex) &&
                _skinPartitionTriangles.TryGetValue(skinPartIndex, out var triangles))
            {
                // Each triangle is 3 ushorts = 6 bytes
                var triangleBytes = triangles.Length * 2; // triangles array contains indices, not triangle count
                expansion.NewSize += triangleBytes;
                expansion.SizeIncrease += triangleBytes;

                if (_verbose)
                    Console.WriteLine(
                        $"    Block {geomBlockIndex}: Adding {triangles.Length / 3} triangles ({triangleBytes} bytes) from skin partition {skinPartIndex}");
            }
        }
    }

    /// <summary>
    ///     Find hkPackedNiTriStripsData blocks with compressed vertices that need expansion.
    /// </summary>
    private void FindHavokExpansions(byte[] data, NifInfo info)
    {
        foreach (var block in info.Blocks)
            if (block.TypeName == "hkPackedNiTriStripsData")
            {
                var expansion = ParseHavokBlock(data, block, info.IsBigEndian);
                if (expansion != null)
                {
                    _havokExpansions[block.Index] = expansion;

                    if (_verbose)
                        Console.WriteLine(
                            $"  Block {block.Index}: hkPackedNiTriStripsData -> expand from {expansion.OriginalSize} to {expansion.NewSize} bytes ({expansion.NumVertices} vertices)");
                }
            }
    }

    /// <summary>
    ///     Parse hkPackedNiTriStripsData to check if it has compressed vertices.
    ///     Structure:
    ///     - NumTriangles (uint)
    ///     - Triangles[NumTriangles] (TriangleData = 8 bytes each)
    ///     - NumVertices (uint)
    ///     - Compressed (bool) - since 20.2.0.7
    ///     - Vertices[NumVertices] (Vector3 if Compressed=0, HalfVector3 if Compressed=1)
    ///     - NumSubShapes (ushort) - since 20.2.0.7
    ///     - SubShapes[NumSubShapes]
    /// </summary>
    private static HavokBlockExpansion? ParseHavokBlock(byte[] data, BlockInfo block, bool isBigEndian)
    {
        var pos = block.DataOffset;
        var end = block.DataOffset + block.Size;

        if (pos + 4 > end) return null;
        var numTriangles = isBigEndian
            ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos, 4))
            : BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos, 4));
        pos += 4;

        // Skip triangles (TriangleData = Triangle + WeldInfo = 6 + 2 = 8 bytes each)
        pos += (int)numTriangles * 8;

        if (pos + 4 > end) return null;
        var numVertices = isBigEndian
            ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos, 4))
            : BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos, 4));
        pos += 4;

        if (pos + 1 > end) return null;
        var compressed = data[pos] != 0;
        pos += 1;

        // Only need expansion if compressed
        if (!compressed) return null;

        // Calculate how much data follows the vertices
        // CompressedVertices: numVertices * 6 bytes (HalfVector3)
        var compressedVertexSize = (int)numVertices * 6;
        var vertexDataOffset = pos;

        // Skip compressed vertex data
        pos += compressedVertexSize;

        // Read NumSubShapes
        if (pos + 2 > end) return null;
        var numSubShapes = isBigEndian
            ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos, 2))
            : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos, 2));

        // Size increase: expand from HalfVector3 (6 bytes) to Vector3 (12 bytes) per vertex
        var sizeIncrease = (int)numVertices * 6; // 12 - 6 = 6 bytes per vertex

        return new HavokBlockExpansion
        {
            BlockIndex = block.Index,
            NumTriangles = (int)numTriangles,
            NumVertices = (int)numVertices,
            NumSubShapes = numSubShapes,
            OriginalSize = block.Size,
            NewSize = block.Size + sizeIncrease,
            VertexDataOffset = vertexDataOffset
        };
    }

    /// <summary>
    ///     Find NiSkinPartition blocks that need bone weights/indices expansion.
    ///     These are associated with skinned meshes where the bone data is in BSPackedAdditionalGeometryData.
    /// </summary>
    private void FindSkinPartitionExpansions(byte[] data, NifInfo info)
    {
        // Build mapping from NiSkinPartition block index -> packed geometry data
        // Chain: geometry block -> skin partition -> packed data

        foreach (var kvp in _geometryExpansions)
        {
            var geomBlockIndex = kvp.Key;
            var packedBlockIndex = kvp.Value.PackedBlockIndex;

            // Check if this geometry block is associated with a skin partition
            if (!_geometryToSkinPartition.TryGetValue(geomBlockIndex, out var skinPartitionIndex)) continue;

            // Get the packed data
            if (!_packedGeometryByBlock.TryGetValue(packedBlockIndex, out var packedData)) continue;

            // Only expand if we have bone data in the packed geometry
            if (packedData.BoneIndices != null && packedData.BoneWeights != null)
                _skinPartitionToPackedData[skinPartitionIndex] = packedData;
        }

        // Now find all NiSkinPartition blocks that need expansion
        foreach (var block in info.Blocks.Where(b => b.TypeName == "NiSkinPartition"))
        {
            // Check if this skin partition has associated packed data with bone info
            if (!_skinPartitionToPackedData.ContainsKey(block.Index)) continue;

            // Parse the skin partition to understand its structure
            var skinData =
                NifSkinPartitionExpander.Parse(data, block.DataOffset, block.Size, info.IsBigEndian, _verbose);
            if (skinData == null) continue;

            var newSize = NifSkinPartitionExpander.CalculateExpandedSize(skinData);
            var sizeIncrease = newSize - block.Size;

            if (sizeIncrease > 0)
            {
                _skinPartitionExpansions[block.Index] = new SkinPartitionExpansion
                {
                    BlockIndex = block.Index,
                    OriginalSize = block.Size,
                    NewSize = newSize,
                    ParsedData = skinData
                };

                if (_verbose)
                    Console.WriteLine(
                        $"  Block {block.Index}: NiSkinPartition -> expand from {block.Size} to {newSize} bytes (+{sizeIncrease} for bone weights/indices)");
            }
        }
    }

    /// <summary>
    ///     Parse a geometry block to find its Additional Data block reference.
    /// </summary>
    private static int ParseAdditionalDataRef(byte[] data, BlockInfo block)
    {
        // NiGeometryData structure (simplified):
        // - GroupId (int) - since 10.1.0.114
        // - NumVertices (ushort)
        // - KeepFlags (byte) - since 10.1.0.0
        // - CompressFlags (byte) - since 10.1.0.0
        // - HasVertices (bool)
        // - Vertices[NumVertices] if HasVertices
        // - BSDataFlags (ushort) for Bethesda
        // - HasNormals (bool)
        // - Normals[NumVertices] if HasNormals
        // - Tangents/Bitangents if flags set
        // - BoundingSphere (16 bytes)
        // - HasVertexColors (bool)
        // - VertexColors[NumVertices] if HasVertexColors
        // - UVSets based on flags
        // - ConsistencyFlags (ushort)
        // - AdditionalData (Ref) <- this is what we want

        var pos = block.DataOffset;
        var end = block.DataOffset + block.Size;

        // GroupId (int)
        pos += 4;

        // NumVertices (ushort)
        if (pos + 2 > end) return -1;
        var numVertices = ReadUInt16BE(data, pos);
        pos += 2;

        // KeepFlags, CompressFlags (bytes)
        pos += 2;

        // HasVertices (bool as byte)
        if (pos + 1 > end) return -1;
        var hasVertices = data[pos++];
        if (hasVertices != 0)
            pos += numVertices * 12; // Vector3 * numVerts

        // BSDataFlags (ushort)
        if (pos + 2 > end) return -1;
        var bsDataFlags = ReadUInt16BE(data, pos);
        pos += 2;

        // HasNormals (bool)
        if (pos + 1 > end) return -1;
        var hasNormals = data[pos++];
        if (hasNormals != 0)
        {
            pos += numVertices * 12; // Normals
            // Tangents/Bitangents if flag 4096 set
            if ((bsDataFlags & 4096) != 0)
                pos += numVertices * 24; // Tangents + Bitangents
        }

        // BoundingSphere (center Vector3 + radius float = 16 bytes)
        pos += 16;

        // HasVertexColors (bool)
        if (pos + 1 > end) return -1;
        var hasVertexColors = data[pos++];
        if (hasVertexColors != 0)
            pos += numVertices * 16; // Color4 * numVerts

        // UV Sets based on BSDataFlags bit 0
        var numUVSets = bsDataFlags & 1;
        if (numUVSets != 0)
            pos += numVertices * 8; // TexCoord * numVerts

        // ConsistencyFlags (ushort)
        pos += 2;

        // AdditionalData (Ref = int)
        if (pos + 4 > end) return -1;
        var additionalDataRef = ReadInt32BE(data, pos);

        return additionalDataRef;
    }

    /// <summary>
    ///     Calculate how much a geometry block needs to expand.
    /// </summary>
    /// <param name="isSkinned">If true, skip vertex colors (ubyte4 is bone indices, not colors)</param>
    private static GeometryBlockExpansion? CalculateGeometryExpansion(byte[] data, BlockInfo block,
        PackedGeometryData packedData, bool isSkinned = false)
    {
        var pos = block.DataOffset;
        var end = block.DataOffset + block.Size;

        // Parse existing geometry to see what needs to be added
        pos += 4; // GroupId

        if (pos + 2 > end) return null;
        var numVertices = ReadUInt16BE(data, pos);
        pos += 2;

        pos += 2; // KeepFlags, CompressFlags

        if (pos + 1 > end) return null;
        var hasVertices = data[pos];
        pos += 1; // hasVertices byte

        if (hasVertices != 0)
            pos += numVertices * 12; // skip vertex data

        if (pos + 2 > end) return null;
        var bsDataFlags = ReadUInt16BE(data, pos);
        pos += 2;

        if (pos + 1 > end) return null;
        var hasNormals = data[pos];
        pos += 1; // hasNormals byte

        // Skip normals and tangent/bitangent data if present
        if (hasNormals != 0)
        {
            pos += numVertices * 12; // normals
            if ((bsDataFlags & 4096) != 0)
                pos += numVertices * 24; // tangents + bitangents
        }

        // Skip center + radius (16 bytes)
        pos += 16;

        // Check hasVertexColors
        if (pos + 1 > end) return null;
        var hasVertexColors = data[pos];

        // Calculate size increase
        var sizeIncrease = 0;

        // Need to add vertices?
        if (hasVertices == 0 && packedData.Positions != null)
            sizeIncrease += numVertices * 12;

        // Need to add normals (and possibly tangents/bitangents)?
        if (hasNormals == 0 && packedData.Normals != null)
        {
            sizeIncrease += numVertices * 12; // Normals
            if (packedData.Tangents != null)
                sizeIncrease += numVertices * 12;
            if (packedData.Bitangents != null)
                sizeIncrease += numVertices * 12;
        }

        // Need to add vertex colors?
        // Vertex colors are Color4 (4 floats = 16 bytes per vertex)
        // NOTE: For skinned meshes, ubyte4 stream is bone indices, NOT vertex colors - skip!
        if (hasVertexColors == 0 && packedData.VertexColors != null && !isSkinned)
            sizeIncrease += numVertices * 16;

        // Need to add UVs?
        var numUVSets = bsDataFlags & 1;
        if (numUVSets == 0 && packedData.UVs != null)
            sizeIncrease += numVertices * 8;

        if (sizeIncrease == 0)
            return null;

        return new GeometryBlockExpansion
        {
            OriginalSize = block.Size,
            NewSize = block.Size + sizeIncrease,
            SizeIncrease = sizeIncrease
        };
    }

    /// <summary>
    ///     Calculate block index remapping after removing packed blocks.
    /// </summary>
    private int[] CalculateBlockRemap(int blockCount)
    {
        var remap = new int[blockCount];
        var newIndex = 0;

        for (var i = 0; i < blockCount; i++)
            if (_blocksToStrip.Contains(i))
                remap[i] = -1; // Will be removed
            else
                remap[i] = newIndex++;

        return remap;
    }

    /// <summary>
    ///     Calculate total output size accounting for removed and expanded blocks.
    /// </summary>
    private int CalculateOutputSize(int originalSize, NifInfo info)
    {
        var size = originalSize;

        if (_verbose) Console.WriteLine($"  Size calculation: starting from {originalSize}");

        // Add new strings for node names
        foreach (var str in _newStrings)
        {
            size += 4 + str.Length; // SizedString: uint length + chars
            if (_verbose) Console.WriteLine($"    + New string '{str}': {4 + str.Length} bytes");
        }

        // Subtract removed blocks
        foreach (var blockIdx in _blocksToStrip)
        {
            var block = info.Blocks[blockIdx];
            size -= block.Size;
            size -= 4; // Block size entry in header
            size -= 2; // Block type index entry in header
            if (_verbose) Console.WriteLine($"    - Remove block {blockIdx}: {block.Size} + 6 header bytes");
        }

        // Add geometry expansion sizes
        foreach (var kvp in _geometryExpansions)
        {
            size += kvp.Value.SizeIncrease;
            if (_verbose) Console.WriteLine($"    + Expand geometry block {kvp.Key}: {kvp.Value.SizeIncrease} bytes");
        }

        // Add Havok expansion sizes
        foreach (var kvp in _havokExpansions)
        {
            size += kvp.Value.SizeIncrease;
            if (_verbose) Console.WriteLine($"    + Expand Havok block {kvp.Key}: {kvp.Value.SizeIncrease} bytes");
        }

        // Add skin partition expansion sizes
        foreach (var kvp in _skinPartitionExpansions)
        {
            size += kvp.Value.SizeIncrease;
            if (_verbose)
                Console.WriteLine($"    + Expand NiSkinPartition block {kvp.Key}: {kvp.Value.SizeIncrease} bytes");
        }

        if (_verbose) Console.WriteLine($"  Final calculated size: {size}");

        return size;
    }

    /// <summary>
    ///     Write the converted output with expanded geometry and removed packed blocks.
    /// </summary>
    private void WriteConvertedOutput(byte[] input, byte[] output, NifInfo info, int[] blockRemap)
    {
        // Simple case: no expansions, no blocks to strip, and no new strings -> do in-place conversion
        if (_blocksToStrip.Count == 0 && _geometryExpansions.Count == 0 && _havokExpansions.Count == 0 &&
            _skinPartitionExpansions.Count == 0 && _newStrings.Count == 0)
        {
            Array.Copy(input, output, input.Length);
            ConvertInPlace(output, info, blockRemap);
            return;
        }

        // Complex case: rebuild the file with new block sizes
        if (_verbose)
            Console.WriteLine(
                $"  Rebuilding file: removing {_blocksToStrip.Count} packed blocks, expanding {_geometryExpansions.Count} geometry, {_skinPartitionExpansions.Count} skin partition blocks, adding {_newStrings.Count} strings");

        var schemaConverter = new NifSchemaConverter(
            _schema,
            info.BinaryVersion,
            (int)info.UserVersion,
            (int)info.BsVersion,
            _verbose);

        // Write header with updated block counts and sizes
        var outPos = WriteConvertedHeader(input, output, info);

        // Write each block
        foreach (var block in info.Blocks)
        {
            if (_blocksToStrip.Contains(block.Index))
                // Skip packed blocks entirely
                continue;

            var blockStartPos = outPos;
            var expectedSize = block.Size;

            if (_geometryExpansions.TryGetValue(block.Index, out var expansion))
            {
                expectedSize = expansion.NewSize;
                // Expand geometry block with unpacked data
                var packedData = _packedGeometryByBlock[expansion.PackedBlockIndex];

                // Check if this geometry block has a vertex map for skinned mesh remapping
                ushort[]? vertexMap = null;
                ushort[]? triangles = null;
                if (_geometryToSkinPartition.TryGetValue(block.Index, out var skinPartitionIndex))
                {
                    _vertexMaps.TryGetValue(skinPartitionIndex, out vertexMap);
                    _skinPartitionTriangles.TryGetValue(skinPartitionIndex, out triangles);
                    if (_verbose && vertexMap != null)
                        Console.WriteLine(
                            $"    Block {block.Index}: Using vertex map from skin partition {skinPartitionIndex}, length={vertexMap.Length}");
                    if (_verbose && triangles != null)
                        Console.WriteLine(
                            $"    Block {block.Index}: Using {triangles.Length / 3} triangles from skin partition {skinPartitionIndex}");
                }

                outPos = WriteExpandedGeometryBlock(input, output, outPos, block, packedData, vertexMap,
                    triangles);
            }
            else if (_havokExpansions.TryGetValue(block.Index, out var havokExpansion))
            {
                expectedSize = havokExpansion.NewSize;
                // Expand Havok collision block with decompressed vertices
                outPos = WriteExpandedHavokBlock(input, output, outPos, block);
            }
            else if (_skinPartitionExpansions.TryGetValue(block.Index, out var skinPartExpansion))
            {
                expectedSize = skinPartExpansion.NewSize;
                // Expand NiSkinPartition block with bone weights/indices
                var packedData = _skinPartitionToPackedData[block.Index];
                outPos = NifSkinPartitionExpander.WriteExpanded(skinPartExpansion.ParsedData, packedData, output,
                    outPos, _verbose);
            }
            else
            {
                // Regular block - copy and convert endianness
                outPos = WriteConvertedBlock(input, output, outPos, block, schemaConverter, blockRemap);
            }

            var actualSize = outPos - blockStartPos;
            if (_verbose && actualSize != expectedSize)
                Console.WriteLine(
                    $"  BLOCK SIZE MISMATCH: Block {block.Index} ({block.TypeName}) wrote {actualSize} bytes, expected {expectedSize}");
        }

        // Write footer with remapped indices
        outPos = WriteConvertedFooter(input, output, outPos, info, blockRemap);

        // Verify output size
        if (_verbose && outPos != output.Length)
            Console.WriteLine($"  WARNING: Output size mismatch: wrote {outPos}, expected {output.Length}");
    }

    /// <summary>
    ///     Write the converted header to output with updated block counts and sizes.
    /// </summary>
    private int WriteConvertedHeader(byte[] input, byte[] output, NifInfo info)
    {
        var srcPos = 0;
        var outPos = 0;

        // Copy header string including newline
        var newlinePos = Array.IndexOf(input, (byte)0x0A, 0, 60);
        Array.Copy(input, 0, output, 0, newlinePos + 1);
        srcPos = newlinePos + 1;
        outPos = newlinePos + 1;

        // Binary version (4 bytes) - already LE in Bethesda files
        Array.Copy(input, srcPos, output, outPos, 4);
        srcPos += 4;
        outPos += 4;

        // Endian byte: change from 0 (BE) to 1 (LE)
        output[outPos] = 1;
        srcPos += 1;
        outPos += 1;

        // User version (4 bytes) - already LE
        Array.Copy(input, srcPos, output, outPos, 4);
        srcPos += 4;
        outPos += 4;

        // Num blocks (4 bytes) - write new count (LE)
        var newBlockCount = info.BlockCount - _blocksToStrip.Count;
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(outPos), (uint)newBlockCount);
        srcPos += 4;
        outPos += 4;

        // BS Header (Bethesda specific)
        // BS Version (4 bytes) - already LE
        Array.Copy(input, srcPos, output, outPos, 4);
        var bsVersion = BinaryPrimitives.ReadUInt32LittleEndian(input.AsSpan(srcPos));
        srcPos += 4;
        outPos += 4;

        // Author string (1 byte length + chars)
        var authorLen = input[srcPos];
        Array.Copy(input, srcPos, output, outPos, 1 + authorLen);
        srcPos += 1 + authorLen;
        outPos += 1 + authorLen;

        // Unknown int if bsVersion > 130
        if (bsVersion > 130)
        {
            Array.Copy(input, srcPos, output, outPos, 4);
            srcPos += 4;
            outPos += 4;
        }

        // Process Script if bsVersion < 131
        if (bsVersion < 131)
        {
            var psLen = input[srcPos];
            Array.Copy(input, srcPos, output, outPos, 1 + psLen);
            srcPos += 1 + psLen;
            outPos += 1 + psLen;
        }

        // Export Script
        var esLen = input[srcPos];
        Array.Copy(input, srcPos, output, outPos, 1 + esLen);
        srcPos += 1 + esLen;
        outPos += 1 + esLen;

        // Max Filepath if bsVersion >= 103
        if (bsVersion >= 103)
        {
            var mfLen = input[srcPos];
            Array.Copy(input, srcPos, output, outPos, 1 + mfLen);
            srcPos += 1 + mfLen;
            outPos += 1 + mfLen;
        }

        // Num Block Types (ushort) - convert BE to LE
        var numBlockTypes = ReadUInt16BE(input, srcPos);
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(outPos), numBlockTypes);
        srcPos += 2;
        outPos += 2;

        // Block type strings (SizedString: uint length BE + chars)
        for (var i = 0; i < numBlockTypes; i++)
        {
            var strLen = BinaryPrimitives.ReadUInt32BigEndian(input.AsSpan(srcPos));
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(outPos), strLen);
            srcPos += 4;
            outPos += 4;

            Array.Copy(input, srcPos, output, outPos, (int)strLen);
            srcPos += (int)strLen;
            outPos += (int)strLen;
        }

        // Block type indices (ushort[numBlocks]) - skip removed blocks, convert BE to LE
        foreach (var block in info.Blocks)
        {
            if (!_blocksToStrip.Contains(block.Index))
            {
                BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(outPos), block.TypeIndex);
                outPos += 2;
            }

            srcPos += 2;
        }

        // Block sizes (uint[numBlocks]) - skip removed, update expanded, convert BE to LE
        foreach (var block in info.Blocks)
        {
            if (!_blocksToStrip.Contains(block.Index))
            {
                var size = (uint)block.Size;
                if (_geometryExpansions.TryGetValue(block.Index, out var expansion))
                    size = (uint)expansion.NewSize;
                else if (_havokExpansions.TryGetValue(block.Index, out var havokExpansion))
                    size = (uint)havokExpansion.NewSize;
                else if (_skinPartitionExpansions.TryGetValue(block.Index, out var skinPartExpansion))
                    size = (uint)skinPartExpansion.NewSize;
                BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(outPos), size);
                outPos += 4;
            }

            srcPos += 4;
        }

        // Num strings (uint) - add new strings count
        var numStrings = BinaryPrimitives.ReadUInt32BigEndian(input.AsSpan(srcPos));
        var newNumStrings = numStrings + (uint)_newStrings.Count;
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(outPos), newNumStrings);
        srcPos += 4;
        outPos += 4;

        // Max string length (uint) - update if we have longer strings
        var maxStrLen = BinaryPrimitives.ReadUInt32BigEndian(input.AsSpan(srcPos));
        foreach (var str in _newStrings)
        {
            if (str.Length > maxStrLen)
                maxStrLen = (uint)str.Length;
        }
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(outPos), maxStrLen);
        srcPos += 4;
        outPos += 4;

        // Strings (SizedString: uint length BE + chars) - copy original strings
        for (var i = 0; i < numStrings; i++)
        {
            var strLen = BinaryPrimitives.ReadUInt32BigEndian(input.AsSpan(srcPos));
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(outPos), strLen);
            srcPos += 4;
            outPos += 4;

            Array.Copy(input, srcPos, output, outPos, (int)strLen);
            srcPos += (int)strLen;
            outPos += (int)strLen;
        }

        // Write new strings for node names
        foreach (var str in _newStrings)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(outPos), (uint)str.Length);
            outPos += 4;
            System.Text.Encoding.ASCII.GetBytes(str, output.AsSpan(outPos));
            outPos += str.Length;
        }

        // Num groups (uint) - convert BE to LE
        var numGroups = BinaryPrimitives.ReadUInt32BigEndian(input.AsSpan(srcPos));
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(outPos), numGroups);
        srcPos += 4;
        outPos += 4;

        // Groups (uint[numGroups]) - convert BE to LE
        for (var i = 0; i < numGroups; i++)
        {
            var groupSize = BinaryPrimitives.ReadUInt32BigEndian(input.AsSpan(srcPos));
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(outPos), groupSize);
            srcPos += 4;
            outPos += 4;
        }

        return outPos;
    }

    /// <summary>
    ///     Write a regular block (copy and convert endianness).
    /// </summary>
    private int WriteConvertedBlock(byte[] input, byte[] output, int outPos, BlockInfo block,
        NifSchemaConverter schemaConverter, int[] blockRemap)
    {
        // Copy block data
        Array.Copy(input, block.DataOffset, output, outPos, block.Size);

        // Convert using schema
        if (!schemaConverter.TryConvert(output, outPos, block.Size, block.TypeName, blockRemap))
            // Fallback: bulk swap
            BulkSwap32(output, outPos, block.Size);

        // Restore node name if we have one from the palette
        if (_nodeNameStringIndices.TryGetValue(block.Index, out var stringIndex))
        {
            // The Name field is at offset 0 for NiNode/BSFadeNode (first field after AVObject inheritance)
            // It's a StringIndex which is a 4-byte int
            BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(outPos), stringIndex);
            if (_verbose)
                Console.WriteLine($"    Block {block.Index} ({block.TypeName}): Set Name to string index {stringIndex} ('{_nodeNamesByBlock[block.Index]}')");
        }

        return outPos + block.Size;
    }

    /// <summary>
    ///     Write a Havok collision block with decompressed vertices (HalfVector3 -> Vector3).
    /// </summary>
    private static int WriteExpandedHavokBlock(byte[] input, byte[] output, int outPos, BlockInfo block)
    {
        var srcPos = block.DataOffset;

        // NumTriangles (uint BE -> LE)
        var numTriangles = BinaryPrimitives.ReadUInt32BigEndian(input.AsSpan(srcPos, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(outPos), numTriangles);
        srcPos += 4;
        outPos += 4;

        // Triangles (TriangleData[NumTriangles]) - each is 8 bytes: 3 ushorts + 1 ushort weldinfo
        for (var i = 0; i < numTriangles; i++)
        {
            // v1 (ushort)
            BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(outPos),
                BinaryPrimitives.ReadUInt16BigEndian(input.AsSpan(srcPos, 2)));
            srcPos += 2;
            outPos += 2;
            // v2 (ushort)
            BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(outPos),
                BinaryPrimitives.ReadUInt16BigEndian(input.AsSpan(srcPos, 2)));
            srcPos += 2;
            outPos += 2;
            // v3 (ushort)
            BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(outPos),
                BinaryPrimitives.ReadUInt16BigEndian(input.AsSpan(srcPos, 2)));
            srcPos += 2;
            outPos += 2;
            // WeldInfo (ushort)
            BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(outPos),
                BinaryPrimitives.ReadUInt16BigEndian(input.AsSpan(srcPos, 2)));
            srcPos += 2;
            outPos += 2;
        }

        // NumVertices (uint BE -> LE)
        var numVertices = BinaryPrimitives.ReadUInt32BigEndian(input.AsSpan(srcPos, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(outPos), numVertices);
        srcPos += 4;
        outPos += 4;

        // Compressed flag - read as 1, write as 0 (we're decompressing)
        srcPos += 1; // Skip the compressed=1 byte
        output[outPos++] = 0; // Write compressed=0

        // Convert HalfVector3 to Vector3 for each vertex
        for (var i = 0; i < numVertices; i++)
        {
            // Read 3 half-floats (6 bytes) as big-endian
            var hx = BinaryPrimitives.ReadUInt16BigEndian(input.AsSpan(srcPos, 2));
            var hy = BinaryPrimitives.ReadUInt16BigEndian(input.AsSpan(srcPos + 2, 2));
            var hz = BinaryPrimitives.ReadUInt16BigEndian(input.AsSpan(srcPos + 4, 2));
            srcPos += 6;

            // Convert to full floats
            var x = HalfToFloat(hx);
            var y = HalfToFloat(hy);
            var z = HalfToFloat(hz);

            // Write as little-endian floats (12 bytes)
            BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos), x);
            outPos += 4;
            BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos), y);
            outPos += 4;
            BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos), z);
            outPos += 4;
        }

        // NumSubShapes (ushort BE -> LE)
        var numSubShapes = BinaryPrimitives.ReadUInt16BigEndian(input.AsSpan(srcPos, 2));
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(outPos), numSubShapes);
        srcPos += 2;
        outPos += 2;

        // SubShapes (hkSubPartData[NumSubShapes])
        // hkSubPartData = HavokFilter (uint) + NumVertices (uint) + HavokMaterial (4 bytes)
        for (var i = 0; i < numSubShapes; i++)
        {
            // HavokFilter (uint)
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(outPos),
                BinaryPrimitives.ReadUInt32BigEndian(input.AsSpan(srcPos, 4)));
            srcPos += 4;
            outPos += 4;
            // NumVertices (uint)
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(outPos),
                BinaryPrimitives.ReadUInt32BigEndian(input.AsSpan(srcPos, 4)));
            srcPos += 4;
            outPos += 4;
            // HavokMaterial - typically 4 bytes (varies by version)
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(outPos),
                BinaryPrimitives.ReadUInt32BigEndian(input.AsSpan(srcPos, 4)));
            srcPos += 4;
            outPos += 4;
        }

        return outPos;
    }

    /// <summary>
    ///     Convert half-precision float (IEEE 754 binary16) to single precision float.
    /// </summary>
    private static float HalfToFloat(ushort h)
    {
        var sign = (h >> 15) & 0x0001;
        var exp = (h >> 10) & 0x001F;
        var mant = h & 0x03FF;

        if (exp == 0)
        {
            // Zero or denormalized
            if (mant == 0)
                return sign != 0 ? -0.0f : 0.0f;

            // Denormalized: convert to normalized
            while ((mant & 0x0400) == 0)
            {
                mant <<= 1;
                exp--;
            }

            exp++;
            mant &= ~0x0400;
        }
        else if (exp == 31)
        {
            // Inf or NaN
            if (mant != 0)
                return float.NaN;
            return sign != 0 ? float.NegativeInfinity : float.PositiveInfinity;
        }

        exp += 127 - 15;
        mant <<= 13;

        var bits = (sign << 31) | (exp << 23) | mant;
        return BitConverter.Int32BitsToSingle(bits);
    }

    /// <summary>
    ///     Write a geometry block with expanded packed data.
    ///     If vertexMap is provided (for skinned meshes), vertices are remapped from partition order to mesh order.
    ///     If triangles is provided, triangles are written for NiTriShapeData (converted from NiSkinPartition strips).
    /// </summary>
    private static int WriteExpandedGeometryBlock(byte[] input, byte[] output, int outPos, BlockInfo block,
        PackedGeometryData packedData, ushort[]? vertexMap, ushort[]? triangles)
    {
        var srcPos = block.DataOffset;

        // groupId (int BE -> LE)
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(outPos),
            BinaryPrimitives.ReadInt32BigEndian(input.AsSpan(srcPos)));
        srcPos += 4;
        outPos += 4;

        // numVertices (ushort BE -> LE)
        var numVertices = ReadUInt16BE(input, srcPos);
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(outPos), numVertices);
        srcPos += 2;
        outPos += 2;

        // keepFlags, compressFlags (bytes - no conversion)
        output[outPos++] = input[srcPos++];
        output[outPos++] = input[srcPos++];

        // hasVertices - set to 1 if we have positions from packed data
        var origHasVertices = input[srcPos++];
        var newHasVertices = (byte)(packedData.Positions != null ? 1 : origHasVertices);
        output[outPos++] = newHasVertices;

        // Write vertices
        // Always prefer packed data when available - Xbox 360 files set hasVertices=1 
        // even when actual vertex data is in BSPackedAdditionalGeometryData
        if (newHasVertices != 0 && packedData.Positions != null)
        {
            // Write unpacked positions (with optional vertex map remapping for skinned meshes)
            if (vertexMap != null && vertexMap.Length > 0)
            {
                // Skinned mesh: remap from partition order to mesh order
                // vertexMap[partition_index] = mesh_index
                // Note: vertexMap.Length may be > numVertices (packed data has all partition vertices,
                // while numVertices is the unique mesh vertex count)
                var vertexDataSize = numVertices * 12; // 3 floats * 4 bytes
                var basePos = outPos;

                // Pre-zero the vertex area (in case mapping is sparse)
                output.AsSpan(basePos, vertexDataSize).Clear();

                // Iterate over ALL partition vertices from packed data
                var packedVertexCount = Math.Min(vertexMap.Length, packedData.Positions.Length / 3);
                for (var partitionIdx = 0; partitionIdx < packedVertexCount; partitionIdx++)
                {
                    var meshIdx = vertexMap[partitionIdx];
                    if (meshIdx >= numVertices) continue; // Skip invalid indices

                    var writePos = basePos + meshIdx * 12;
                    BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(writePos),
                        packedData.Positions[partitionIdx * 3 + 0]);
                    BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(writePos + 4),
                        packedData.Positions[partitionIdx * 3 + 1]);
                    BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(writePos + 8),
                        packedData.Positions[partitionIdx * 3 + 2]);
                }

                outPos += vertexDataSize;
            }
            else
            {
                // Non-skinned mesh: write in sequential order
                var packedVertexCount = Math.Min(numVertices, packedData.Positions.Length / 3);
                for (var v = 0; v < packedVertexCount; v++)
                {
                    BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos), packedData.Positions[v * 3 + 0]);
                    outPos += 4;
                    BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos), packedData.Positions[v * 3 + 1]);
                    outPos += 4;
                    BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos), packedData.Positions[v * 3 + 2]);
                    outPos += 4;
                }

                // Pad remaining vertices with zeros if packed data has fewer vertices
                var remaining = numVertices - packedVertexCount;
                if (remaining > 0)
                {
                    output.AsSpan(outPos, remaining * 12).Clear();
                    outPos += remaining * 12;
                }
            }
        }
        else if (origHasVertices != 0 && packedData.Positions == null)
        {
            // Copy and convert existing vertices (only if no packed data available)
            for (var v = 0; v < numVertices * 3; v++)
            {
                BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos),
                    BinaryPrimitives.ReadSingleBigEndian(input.AsSpan(srcPos)));
                srcPos += 4;
                outPos += 4;
            }
        }

        // bsDataFlags - update flags based on packed data
        var origBsDataFlags = ReadUInt16BE(input, srcPos);
        var newBsDataFlags = origBsDataFlags;
        if (packedData.Tangents != null) newBsDataFlags |= 4096; // Has tangents flag
        if (packedData.UVs != null) newBsDataFlags |= 1; // Has UVs flag
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(outPos), newBsDataFlags);
        srcPos += 2;
        outPos += 2;

        // hasNormals - set to 1 if we have normals from packed data
        var origHasNormals = input[srcPos++];
        var newHasNormals = (byte)(packedData.Normals != null ? 1 : origHasNormals);
        output[outPos++] = newHasNormals;

        // Write normals
        // Always prefer packed data when available - Xbox 360 files set hasNormals=1 
        // even when actual normal data is in BSPackedAdditionalGeometryData
        if (newHasNormals != 0 && packedData.Normals != null)
        {
            // Write unpacked normals (with optional vertex map remapping for skinned meshes)
            if (vertexMap != null && vertexMap.Length > 0)
            {
                var normalDataSize = numVertices * 12;
                var basePos = outPos;
                output.AsSpan(basePos, normalDataSize).Clear();

                var packedNormalCount = Math.Min(vertexMap.Length, packedData.Normals.Length / 3);
                for (var partitionIdx = 0; partitionIdx < packedNormalCount; partitionIdx++)
                {
                    var meshIdx = vertexMap[partitionIdx];
                    if (meshIdx >= numVertices) continue;

                    var writePos = basePos + meshIdx * 12;
                    BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(writePos),
                        packedData.Normals[partitionIdx * 3 + 0]);
                    BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(writePos + 4),
                        packedData.Normals[partitionIdx * 3 + 1]);
                    BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(writePos + 8),
                        packedData.Normals[partitionIdx * 3 + 2]);
                }

                outPos += normalDataSize;
            }
            else
            {
                for (var v = 0; v < numVertices; v++)
                {
                    BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos), packedData.Normals[v * 3 + 0]);
                    outPos += 4;
                    BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos), packedData.Normals[v * 3 + 1]);
                    outPos += 4;
                    BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos), packedData.Normals[v * 3 + 2]);
                    outPos += 4;
                }
            }

            // Write tangents if available (with optional remapping)
            if ((newBsDataFlags & 4096) != 0 && packedData.Tangents != null)
            {
                if (vertexMap != null && vertexMap.Length > 0)
                {
                    var tangentDataSize = numVertices * 12;
                    var basePos = outPos;
                    output.AsSpan(basePos, tangentDataSize).Clear();

                    var packedTangentCount = Math.Min(vertexMap.Length, packedData.Tangents.Length / 3);
                    for (var partitionIdx = 0; partitionIdx < packedTangentCount; partitionIdx++)
                    {
                        var meshIdx = vertexMap[partitionIdx];
                        if (meshIdx >= numVertices) continue;

                        var writePos = basePos + meshIdx * 12;
                        BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(writePos),
                            packedData.Tangents[partitionIdx * 3 + 0]);
                        BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(writePos + 4),
                            packedData.Tangents[partitionIdx * 3 + 1]);
                        BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(writePos + 8),
                            packedData.Tangents[partitionIdx * 3 + 2]);
                    }

                    outPos += tangentDataSize;
                }
                else
                {
                    for (var v = 0; v < numVertices; v++)
                    {
                        BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos), packedData.Tangents[v * 3 + 0]);
                        outPos += 4;
                        BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos), packedData.Tangents[v * 3 + 1]);
                        outPos += 4;
                        BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos), packedData.Tangents[v * 3 + 2]);
                        outPos += 4;
                    }
                }

                // Write bitangents if available (with optional remapping)
                if (packedData.Bitangents != null)
                {
                    if (vertexMap != null && vertexMap.Length > 0)
                    {
                        var bitangentDataSize = numVertices * 12;
                        var basePos = outPos;
                        output.AsSpan(basePos, bitangentDataSize).Clear();

                        var packedBitangentCount = Math.Min(vertexMap.Length, packedData.Bitangents.Length / 3);
                        for (var partitionIdx = 0; partitionIdx < packedBitangentCount; partitionIdx++)
                        {
                            var meshIdx = vertexMap[partitionIdx];
                            if (meshIdx >= numVertices) continue;

                            var writePos = basePos + meshIdx * 12;
                            BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(writePos),
                                packedData.Bitangents[partitionIdx * 3 + 0]);
                            BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(writePos + 4),
                                packedData.Bitangents[partitionIdx * 3 + 1]);
                            BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(writePos + 8),
                                packedData.Bitangents[partitionIdx * 3 + 2]);
                        }

                        outPos += bitangentDataSize;
                    }
                    else
                    {
                        for (var v = 0; v < numVertices; v++)
                        {
                            BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos),
                                packedData.Bitangents[v * 3 + 0]);
                            outPos += 4;
                            BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos),
                                packedData.Bitangents[v * 3 + 1]);
                            outPos += 4;
                            BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos),
                                packedData.Bitangents[v * 3 + 2]);
                            outPos += 4;
                        }
                    }
                }
            }
        }
        else if (origHasNormals != 0 && packedData.Normals == null)
        {
            // Copy and convert existing normals (only if no packed data available)
            for (var v = 0; v < numVertices * 3; v++)
            {
                BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos),
                    BinaryPrimitives.ReadSingleBigEndian(input.AsSpan(srcPos)));
                srcPos += 4;
                outPos += 4;
            }

            // Copy existing tangents/bitangents if present
            if ((origBsDataFlags & 4096) != 0)
                for (var v = 0; v < numVertices * 6; v++) // 3 floats tangent + 3 floats bitangent
                {
                    BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos),
                        BinaryPrimitives.ReadSingleBigEndian(input.AsSpan(srcPos)));
                    srcPos += 4;
                    outPos += 4;
                }
        }

        // center (Vector3) + radius (float) = 16 bytes
        for (var i = 0; i < 4; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos),
                BinaryPrimitives.ReadSingleBigEndian(input.AsSpan(srcPos)));
            srcPos += 4;
            outPos += 4;
        }

        // hasVertexColors (byte) - set to 1 if we have colors from packed data
        // NOTE: For skinned meshes (vertexMap != null), ubyte4 stream is bone indices, NOT vertex colors!
        var origHasVertexColors = input[srcPos++];
        var isSkinned = vertexMap != null;
        var newHasVertexColors = (byte)(packedData.VertexColors != null && !isSkinned ? 1 : origHasVertexColors);
        output[outPos++] = newHasVertexColors;

        // Write vertex colors
        // NIF stores vertex colors as Color4 (4 floats, 16 bytes per vertex) in RGBA order
        // Xbox 360 packed data stores them as ByteColor4 (4 bytes per vertex) in ARGB order
        // NOTE: For skinned meshes, ubyte4 is bone indices - we already set newHasVertexColors=0 above
        if (newHasVertexColors != 0 && packedData.VertexColors != null && !isSkinned)
        {
            // Convert from ByteColor4 ARGB (0-255) to Color4 RGBA (0.0-1.0) (with optional remapping)
            if (vertexMap != null && vertexMap.Length > 0)
            {
                var colorDataSize = numVertices * 16; // 4 floats * 4 bytes
                var basePos = outPos;
                output.AsSpan(basePos, colorDataSize).Clear();

                var packedColorCount = Math.Min(vertexMap.Length, packedData.VertexColors.Length / 4);
                for (var partitionIdx = 0; partitionIdx < packedColorCount; partitionIdx++)
                {
                    var meshIdx = vertexMap[partitionIdx];
                    if (meshIdx >= numVertices) continue;

                    // Xbox packed format: A, R, G, B
                    // NIF Color4 format: R, G, B, A
                    var a = packedData.VertexColors[partitionIdx * 4 + 0] / 255.0f;
                    var r = packedData.VertexColors[partitionIdx * 4 + 1] / 255.0f;
                    var g = packedData.VertexColors[partitionIdx * 4 + 2] / 255.0f;
                    var b = packedData.VertexColors[partitionIdx * 4 + 3] / 255.0f;

                    var writePos = basePos + meshIdx * 16;
                    BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(writePos), r);
                    BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(writePos + 4), g);
                    BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(writePos + 8), b);
                    BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(writePos + 12), a);
                }

                outPos += colorDataSize;
            }
            else
            {
                for (var v = 0; v < numVertices; v++)
                {
                    // Xbox packed format: A, R, G, B
                    // NIF Color4 format: R, G, B, A
                    var a = packedData.VertexColors[v * 4 + 0] / 255.0f;
                    var r = packedData.VertexColors[v * 4 + 1] / 255.0f;
                    var g = packedData.VertexColors[v * 4 + 2] / 255.0f;
                    var b = packedData.VertexColors[v * 4 + 3] / 255.0f;
                    BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos), r);
                    outPos += 4;
                    BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos), g);
                    outPos += 4;
                    BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos), b);
                    outPos += 4;
                    BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos), a);
                    outPos += 4;
                }
            }
        }
        else if (origHasVertexColors != 0 && packedData.VertexColors == null)
        {
            // Copy and convert existing Color4 values (only if no packed data available)
            for (var v = 0; v < numVertices * 4; v++)
            {
                BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos),
                    BinaryPrimitives.ReadSingleBigEndian(input.AsSpan(srcPos)));
                srcPos += 4;
                outPos += 4;
            }
        }

        // UV sets
        // Always prefer packed data when available
        var origNumUVSets = origBsDataFlags & 1;
        var newNumUVSets = newBsDataFlags & 1;

        if (newNumUVSets != 0 && packedData.UVs != null)
        {
            // Write unpacked UVs (with optional remapping)
            if (vertexMap != null && vertexMap.Length > 0)
            {
                var uvDataSize = numVertices * 8; // 2 floats * 4 bytes
                var basePos = outPos;
                output.AsSpan(basePos, uvDataSize).Clear();

                var packedUvCount = Math.Min(vertexMap.Length, packedData.UVs.Length / 2);
                for (var partitionIdx = 0; partitionIdx < packedUvCount; partitionIdx++)
                {
                    var meshIdx = vertexMap[partitionIdx];
                    if (meshIdx >= numVertices) continue;

                    var writePos = basePos + meshIdx * 8;
                    BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(writePos),
                        packedData.UVs[partitionIdx * 2 + 0]);
                    BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(writePos + 4),
                        packedData.UVs[partitionIdx * 2 + 1]);
                }

                outPos += uvDataSize;
            }
            else
            {
                for (var v = 0; v < numVertices; v++)
                {
                    BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos), packedData.UVs[v * 2 + 0]);
                    outPos += 4;
                    BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos), packedData.UVs[v * 2 + 1]);
                    outPos += 4;
                }
            }
        }
        else if (origNumUVSets != 0 && packedData.UVs == null)
        {
            // Copy and convert existing UVs (only if no packed data available)
            for (var v = 0; v < numVertices * 2; v++)
            {
                BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos),
                    BinaryPrimitives.ReadSingleBigEndian(input.AsSpan(srcPos)));
                srcPos += 4;
                outPos += 4;
            }
        }

        // consistency (ushort BE -> LE)
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(outPos),
            ReadUInt16BE(input, srcPos));
        srcPos += 2;
        outPos += 2;

        // additionalData ref - set to -1 since we're removing the packed block
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(outPos), -1);
        srcPos += 4;
        outPos += 4;

        // Copy and convert remaining block data (strips/triangles specific to NiTriStripsData/NiTriShapeData)
        var remainingOriginalBytes = block.Size - (srcPos - block.DataOffset);
        if (remainingOriginalBytes > 0)
            outPos = WriteTriStripSpecificData(input, output, srcPos, outPos, block.TypeName, triangles);

        return outPos;
    }

    /// <summary>
    ///     Write NiTriStripsData or NiTriShapeData specific fields.
    ///     If triangles is provided for NiTriShapeData, writes triangles from NiSkinPartition strips
    ///     instead of copying the source (which has hasTriangles=0 on Xbox).
    /// </summary>
    private static int WriteTriStripSpecificData(byte[] input, byte[] output, int srcPos, int outPos, string blockType,
        ushort[]? triangles)
    {
        if (blockType == "NiTriStripsData")
        {
            // numTriangles (ushort)
            BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(outPos),
                ReadUInt16BE(input, srcPos));
            srcPos += 2;
            outPos += 2;

            // numStrips (ushort)
            var numStrips = ReadUInt16BE(input, srcPos);
            BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(outPos), numStrips);
            srcPos += 2;
            outPos += 2;

            // stripLengths[numStrips]
            var stripLengths = new ushort[numStrips];
            for (var i = 0; i < numStrips; i++)
            {
                stripLengths[i] = ReadUInt16BE(input, srcPos);
                BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(outPos), stripLengths[i]);
                srcPos += 2;
                outPos += 2;
            }

            // hasPoints (byte)
            var hasPoints = input[srcPos++];
            output[outPos++] = hasPoints;

            // points[numStrips][stripLengths[i]]
            if (hasPoints != 0)
                for (var i = 0; i < numStrips; i++)
                for (var j = 0; j < stripLengths[i]; j++)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(outPos),
                        ReadUInt16BE(input, srcPos));
                    srcPos += 2;
                    outPos += 2;
                }
        }
        else if (blockType == "NiTriShapeData")
        {
            // Read source values
            var srcNumTriangles = ReadUInt16BE(input, srcPos);
            srcPos += 2;
            var srcNumTrianglePoints = BinaryPrimitives.ReadUInt32BigEndian(input.AsSpan(srcPos));
            srcPos += 4;
            var srcHasTriangles = input[srcPos++];

            // If we have triangles extracted from NiSkinPartition, use them
            if (triangles != null && triangles.Length >= 3)
            {
                var numTriangles = (ushort)(triangles.Length / 3);
                var numTrianglePoints = (uint)(numTriangles * 3);

                // numTriangles (ushort)
                BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(outPos), numTriangles);
                outPos += 2;

                // numTrianglePoints (uint)
                BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(outPos), numTrianglePoints);
                outPos += 4;

                // hasTriangles (byte) - set to 1 since we're writing triangles
                output[outPos++] = 1;

                // Write triangles
                for (var i = 0; i < triangles.Length; i++)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(outPos), triangles[i]);
                    outPos += 2;
                }

                // Skip source triangles if any (shouldn't be any since srcHasTriangles=0)
                if (srcHasTriangles != 0) srcPos += srcNumTriangles * 6;
            }
            else
            {
                // No extracted triangles - copy source values
                BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(outPos), srcNumTriangles);
                outPos += 2;
                BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(outPos), srcNumTrianglePoints);
                outPos += 4;
                output[outPos++] = srcHasTriangles;

                // Copy source triangles if present
                if (srcHasTriangles != 0)
                    for (var i = 0; i < srcNumTriangles * 3; i++)
                    {
                        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(outPos),
                            ReadUInt16BE(input, srcPos));
                        srcPos += 2;
                        outPos += 2;
                    }
            }

            // numMatchGroups (ushort)
            var numMatchGroups = ReadUInt16BE(input, srcPos);
            BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(outPos), numMatchGroups);
            srcPos += 2;
            outPos += 2;

            // matchGroups[numMatchGroups] - each is a variable-size MatchGroup struct
            for (var i = 0; i < numMatchGroups; i++)
            {
                // numVertices in this match group (ushort)
                var groupNumVertices = ReadUInt16BE(input, srcPos);
                BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(outPos), groupNumVertices);
                srcPos += 2;
                outPos += 2;

                // vertexIndices[groupNumVertices] (ushort array)
                for (var j = 0; j < groupNumVertices; j++)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(outPos),
                        ReadUInt16BE(input, srcPos));
                    srcPos += 2;
                    outPos += 2;
                }
            }
        }

        return outPos;
    }

    /// <summary>
    ///     Write the footer with remapped block indices.
    /// </summary>
    private static int WriteConvertedFooter(byte[] input, byte[] output, int outPos, NifInfo info, int[] blockRemap)
    {
        // Calculate footer position in source
        var lastBlock = info.Blocks[^1];
        var footerPos = lastBlock.DataOffset + lastBlock.Size;

        if (footerPos + 4 > input.Length)
            return outPos;

        // numRoots (uint BE -> LE)
        var numRoots = BinaryPrimitives.ReadUInt32BigEndian(input.AsSpan(footerPos));
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(outPos), numRoots);
        footerPos += 4;
        outPos += 4;

        // root indices (int[numRoots] BE -> LE with remapping)
        for (var i = 0; i < numRoots && footerPos + 4 <= input.Length; i++)
        {
            var rootIdx = BinaryPrimitives.ReadInt32BigEndian(input.AsSpan(footerPos));
            var newRootIdx = rootIdx >= 0 && rootIdx < blockRemap.Length ? blockRemap[rootIdx] : rootIdx;
            BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(outPos), newRootIdx);
            footerPos += 4;
            outPos += 4;
        }

        return outPos;
    }

    /// <summary>
    ///     Convert the NIF in place (no size changes).
    /// </summary>
    private void ConvertInPlace(byte[] buf, NifInfo info, int[] blockRemap)
    {
        // Convert header
        ConvertHeader(buf, info);

        // Create schema converter
        var schemaConverter = new NifSchemaConverter(
            _schema,
            info.BinaryVersion,
            (int)info.UserVersion,
            (int)info.BsVersion,
            _verbose);

        // Convert each block using schema
        foreach (var block in info.Blocks)
        {
            if (_blocksToStrip.Contains(block.Index))
            {
                if (_verbose)
                    Console.WriteLine($"  Block {block.Index}: {block.TypeName} - skipping (will be removed)");
                continue;
            }

            if (_verbose)
                Console.WriteLine(
                    $"  Block {block.Index}: {block.TypeName} at offset {block.DataOffset:X}, size {block.Size}");

            if (!schemaConverter.TryConvert(buf, block.DataOffset, block.Size, block.TypeName, blockRemap))
            {
                // Fallback: bulk swap all 4-byte values (may break some data)
                if (_verbose)
                    Console.WriteLine("    -> Using fallback bulk swap");
                BulkSwap32(buf, block.DataOffset, block.Size);
            }
        }

        // Convert footer
        ConvertFooter(buf, info);
    }

    private static void ConvertHeader(byte[] buf, NifInfo info)
    {
        // The header string and version are always little-endian
        // Only the endian byte needs to change from 0 (BE) to 1 (LE)

        // Find the endian byte position (after header string + binary version)
        var pos = info.HeaderString.Length + 1 + 4; // +1 for newline, +4 for binary version

        // Change endian byte from 0 to 1
        if (buf[pos] == 0)
            buf[pos] = 1;

        // Swap header fields
        SwapHeaderFields(buf, info);
    }

    private static void SwapHeaderFields(byte[] buf, NifInfo info)
    {
        // Position after header string + newline + binary version + endian byte
        var pos = info.HeaderString.Length + 1 + 4 + 1;

        // User version (4 bytes) - already LE in Bethesda
        pos += 4;

        // Num blocks (4 bytes) - already LE in Bethesda
        pos += 4;

        // BS Header (Bethesda specific)
        // BS Version (4 bytes) - already LE
        var bsVersion = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(pos, 4));
        pos += 4;

        // Author string (1 byte length + chars)
        var authorLen = buf[pos];
        pos += 1 + authorLen;

        // Unknown int if bsVersion > 130
        if (bsVersion > 130)
            pos += 4;

        // Process Script if bsVersion < 131
        if (bsVersion < 131)
        {
            var psLen = buf[pos];
            pos += 1 + psLen;
        }

        // Export Script
        var esLen = buf[pos];
        pos += 1 + esLen;

        // Max Filepath if bsVersion >= 103
        if (bsVersion >= 103)
        {
            var mfLen = buf[pos];
            pos += 1 + mfLen;
        }

        // Now we're at Num Block Types (ushort) - needs swap
        SwapUInt16InPlace(buf, pos);
        var numBlockTypes = ReadUInt16LE(buf, pos);
        pos += 2;

        // Block type strings (SizedString: uint length + chars)
        for (var i = 0; i < numBlockTypes; i++)
        {
            SwapUInt32InPlace(buf, pos);
            var strLen = ReadUInt32LE(buf, pos);
            pos += 4 + (int)strLen;
        }

        // Block type indices (ushort[numBlocks])
        for (var i = 0; i < info.BlockCount; i++)
        {
            SwapUInt16InPlace(buf, pos);
            pos += 2;
        }

        // Block sizes (uint[numBlocks])
        for (var i = 0; i < info.BlockCount; i++)
        {
            SwapUInt32InPlace(buf, pos);
            pos += 4;
        }

        // Num strings (uint)
        SwapUInt32InPlace(buf, pos);
        var numStrings = ReadUInt32LE(buf, pos);
        pos += 4;

        // Max string length (uint)
        SwapUInt32InPlace(buf, pos);
        pos += 4;

        // Strings (SizedString: uint length + chars)
        for (var i = 0; i < numStrings; i++)
        {
            SwapUInt32InPlace(buf, pos);
            var strLen = ReadUInt32LE(buf, pos);
            pos += 4 + (int)strLen;
        }

        // Num groups (uint)
        SwapUInt32InPlace(buf, pos);
        var numGroups = ReadUInt32LE(buf, pos);
        pos += 4;

        // Groups (uint[numGroups])
        for (var i = 0; i < numGroups; i++)
        {
            SwapUInt32InPlace(buf, pos);
            pos += 4;
        }
    }

    private static void ConvertFooter(byte[] buf, NifInfo info)
    {
        // Footer is at the end of the file after all blocks
        // Structure: Num Roots (uint) + Root indices (int[Num Roots])

        // Calculate footer position
        var lastBlock = info.Blocks[^1];
        var footerPos = lastBlock.DataOffset + lastBlock.Size;

        if (footerPos + 4 > buf.Length) return;

        // Swap num roots
        SwapUInt32InPlace(buf, footerPos);
        var numRoots = ReadUInt32LE(buf, footerPos);
        footerPos += 4;

        // Swap root indices
        for (var i = 0; i < numRoots && footerPos + 4 <= buf.Length; i++)
        {
            SwapUInt32InPlace(buf, footerPos);
            footerPos += 4;
        }
    }

    private static void BulkSwap32(byte[] buf, int start, int size)
    {
        // Swap all 4-byte aligned values as a fallback
        var end = Math.Min(start + size, buf.Length - 3);
        for (var i = start; i < end; i += 4)
            SwapUInt32InPlace(buf, i);
    }

    // Big-endian read helpers
    private static ushort ReadUInt16BE(byte[] data, int offset)
    {
        return BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, 2));
    }

    private static int ReadInt32BE(byte[] data, int offset)
    {
        return BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset, 4));
    }
}
