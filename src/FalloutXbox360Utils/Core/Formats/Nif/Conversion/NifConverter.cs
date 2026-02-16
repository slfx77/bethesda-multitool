namespace FalloutXbox360Utils.Core.Formats.Nif.Conversion;

/// <summary>
///     Converts Xbox 360 (big-endian) NIF files to PC (little-endian) format.
///     Uses schema-driven conversion based on nif.xml definitions.
///     Handles BSPackedAdditionalGeometryData expansion for geometry blocks.
/// </summary>
internal static class NifConverter
{
    private static readonly Logger Log = Logger.Instance;

    /// <summary>
    ///     Converts a big-endian NIF file to little-endian.
    /// </summary>
    public static NifConversionResult Convert(byte[] data)
    {
        try
        {
            var state = new NifConversionState();
            state.Reset();

            var info = NifParser.Parse(data);
            if (info == null)
            {
                return new NifConversionResult
                {
                    Success = false,
                    ErrorMessage = "Failed to parse NIF header"
                };
            }

            if (!info.IsBigEndian)
            {
                return new NifConversionResult
                {
                    Success = true,
                    OutputData = data,
                    SourceInfo = info,
                    ErrorMessage = "File is already little-endian (PC format)"
                };
            }

            if (!NifParser.IsBethesdaVersion(info.BinaryVersion, info.UserVersion))
            {
                return new NifConversionResult
                {
                    Success = false,
                    ErrorMessage = $"Unsupported NIF version {info.BinaryVersion:X8} (only Bethesda versions supported)"
                };
            }

            Log.Debug(
                $"Converting NIF: {info.BlockCount} blocks, version {info.BinaryVersion:X8}, BS version {info.BsVersion}");

            // Discovery phase: analyze blocks, extract packed geometry, find expansions
            var discovery = new NifDiscovery(state);
            discovery.ParseNodeNamesFromPalette(data, info);
            discovery.FindAndExtractPackedGeometry(data, info);
            discovery.ExtractVertexMaps(data, info);
            discovery.FindGeometryExpansions(data, info);
            discovery.ExtractNiTriStripsDataTriangles(data, info);
            discovery.UpdateGeometryExpansionsWithTriangles();
            discovery.FindHavokExpansions(data, info);
            discovery.FindSkinPartitionExpansions(data, info);

            // Calculate phase: block remapping and output size
            var blockRemap = state.CalculateBlockRemap(info.BlockCount);
            var outputSize = state.CalculateOutputSize(data.Length, info);
            var output = new byte[outputSize];

            // Output phase: convert and write
            var writer = new NifOutputWriter(state);
            writer.WriteConvertedOutput(data, output, info, blockRemap);

            return new NifConversionResult
            {
                Success = true,
                OutputData = output,
                SourceInfo = info
            };
        }
        catch (Exception ex)
        {
            Log.Debug($"  Stack trace: {ex.StackTrace}");

            return new NifConversionResult
            {
                Success = false,
                ErrorMessage = $"Conversion failed: {ex.Message}"
            };
        }
    }

    /// <summary>
    ///     Check if a block type is a node type that has a Name field.
    /// </summary>
    internal static bool IsNodeType(string typeName)
    {
        return typeName is "NiNode" or "BSFadeNode" or "BSLeafAnimNode" or "BSTreeNode" or
            "BSOrderedNode" or "BSMultiBoundNode" or "BSMasterParticleSystem" or "NiSwitchNode" or
            "NiBillboardNode" or "NiLODNode" or "BSBlastNode" or "BSDamageStage" or "NiAVObject";
    }

}
