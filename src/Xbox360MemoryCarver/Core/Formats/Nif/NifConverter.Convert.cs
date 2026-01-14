// NIF converter - Main conversion entry point

namespace Xbox360MemoryCarver.Core.Formats.Nif;

internal sealed partial class NifConverter
{
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
            _geometryStripTriangles.Clear();
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

            // Check if this is a Bethesda version we can fully convert
            if (!NifParser.IsBethesdaVersion(info.BinaryVersion, info.UserVersion))
                return new ConversionResult
                {
                    Success = false,
                    ErrorMessage = $"Unsupported NIF version {info.BinaryVersion:X8} (only Bethesda versions supported)"
                };

            Log.Debug(
                $"Converting NIF: {info.BlockCount} blocks, version {info.BinaryVersion:X8}, BS version {info.BsVersion}");

            // Step 0: Parse NiDefaultAVObjectPalette to get node names
            ParseNodeNamesFromPalette(data, info);

            // Step 1: Find BSPackedAdditionalGeometryData blocks and extract geometry
            FindAndExtractPackedGeometry(data, info);

            // Step 1b: Extract vertex maps and triangles from NiSkinPartition blocks (for skinned meshes)
            ExtractVertexMaps(data, info);

            // Step 2: Find geometry blocks that reference packed data and calculate expansions
            FindGeometryExpansions(data, info);

            // Step 2a: Extract triangles from NiTriStripsData blocks (for non-skinned meshes)
            ExtractNiTriStripsDataTriangles(data, info);

            // Step 2b: Update geometry expansions with triangle sizes (for NiTriShapeData)
            UpdateGeometryExpansionsWithTriangles();

            // Step 2c: Find Havok collision blocks with compressed vertices
            FindHavokExpansions(data, info);

            // Step 2d: Find NiSkinPartition blocks that need bone weights/indices expansion
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
            Log.Debug($"  Stack trace: {ex.StackTrace}");

            return new ConversionResult
            {
                Success = false,
                ErrorMessage = $"Conversion failed: {ex.Message}"
            };
        }
    }
}
