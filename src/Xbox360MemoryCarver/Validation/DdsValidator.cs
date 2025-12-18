using System.Text;

namespace Xbox360MemoryCarver.Validation;

/// <summary>
/// Validates DDS files for common corruption patterns after conversion.
/// Helps identify textures that may have been carved or converted incorrectly.
/// </summary>
public static class DdsValidator
{
    private const uint DDS_MAGIC = 0x20534444; // "DDS "
    private const uint DDS_HEADER_SIZE = 124;
    
    /// <summary>
    /// Result of DDS validation analysis
    /// </summary>
    public class ValidationResult
    {
        public bool IsValid { get; set; }
        public bool HasCorruptionIndicators { get; set; }
        public int CorruptionScore { get; set; } // 0-100, higher = more likely corrupted
        public CorruptionLevel Level { get; set; }
        public List<string> Issues { get; set; } = new();
        public DdsInfo? Info { get; set; }
        
        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Valid: {IsValid}, Corruption Level: {Level} (Score: {CorruptionScore})");
            if (Info != null)
                sb.AppendLine($"  Format: {Info.FourCC}, Size: {Info.Width}x{Info.Height}, MipLevels: {Info.MipLevels}");
            foreach (var issue in Issues)
                sb.AppendLine($"  - {issue}");
            return sb.ToString();
        }
    }
    
    /// <summary>
    /// Corruption severity level
    /// </summary>
    public enum CorruptionLevel
    {
        None = 0,       // No corruption detected
        Low = 1,        // Minor issues, texture likely usable
        Medium = 2,     // Some issues, texture may have visual problems
        High = 3,       // Significant issues, texture likely has major visual corruption
        Severe = 4      // Definitely corrupted, texture is unusable
    }
    
    /// <summary>
    /// Basic DDS file information
    /// </summary>
    public class DdsInfo
    {
        public uint Width { get; set; }
        public uint Height { get; set; }
        public uint MipLevels { get; set; }
        public string FourCC { get; set; } = string.Empty;
        public uint DataSize { get; set; }
        public uint ExpectedDataSize { get; set; }
    }
    
    /// <summary>
    /// Validate a DDS file for corruption patterns
    /// </summary>
    public static ValidationResult Validate(string filePath)
    {
        try
        {
            var data = File.ReadAllBytes(filePath);
            return Validate(data, Path.GetFileName(filePath));
        }
        catch (Exception ex)
        {
            return new ValidationResult
            {
                IsValid = false,
                HasCorruptionIndicators = true,
                CorruptionScore = 100,
                Level = CorruptionLevel.Severe,
                Issues = { $"Failed to read file: {ex.Message}" }
            };
        }
    }
    
    /// <summary>
    /// Validate DDS data for corruption patterns
    /// </summary>
    public static ValidationResult Validate(byte[] data, string? filename = null)
    {
        var result = new ValidationResult { IsValid = true };
        var issues = new List<string>();
        int score = 0;
        
        // Check minimum size
        if (data.Length < 128)
        {
            return new ValidationResult
            {
                IsValid = false,
                HasCorruptionIndicators = true,
                CorruptionScore = 100,
                Level = CorruptionLevel.Severe,
                Issues = { "File too small to be a valid DDS" }
            };
        }
        
        // Check magic number
        var magic = BitConverter.ToUInt32(data, 0);
        if (magic != DDS_MAGIC)
        {
            return new ValidationResult
            {
                IsValid = false,
                HasCorruptionIndicators = true,
                CorruptionScore = 100,
                Level = CorruptionLevel.Severe,
                Issues = { $"Invalid DDS magic: 0x{magic:X8} (expected 0x{DDS_MAGIC:X8})" }
            };
        }
        
        // Parse DDS header
        var headerSize = BitConverter.ToUInt32(data, 4);
        if (headerSize != DDS_HEADER_SIZE)
        {
            issues.Add($"Unusual header size: {headerSize} (expected {DDS_HEADER_SIZE})");
            score += 5;
        }
        
        var height = BitConverter.ToUInt32(data, 12);
        var width = BitConverter.ToUInt32(data, 16);
        var pitchOrLinearSize = BitConverter.ToUInt32(data, 20);
        var mipLevels = BitConverter.ToUInt32(data, 28);
        
        // Extract FourCC from pixel format (offset 84)
        var fourCC = Encoding.ASCII.GetString(data, 84, 4);
        
        result.Info = new DdsInfo
        {
            Width = width,
            Height = height,
            MipLevels = mipLevels,
            FourCC = fourCC
        };
        
        // Check for invalid dimensions
        if (width == 0 || height == 0)
        {
            issues.Add("Zero dimensions detected");
            score += 50;
        }
        else if (width > 8192 || height > 8192)
        {
            issues.Add($"Unusually large dimensions: {width}x{height}");
            score += 20;
        }
        
        // Check for power-of-two dimensions (common for game textures)
        if (!IsPowerOfTwo(width) || !IsPowerOfTwo(height))
        {
            issues.Add($"Non-power-of-two dimensions: {width}x{height}");
            score += 5;
        }
        
        // Calculate expected data size based on format
        var expectedSize = CalculateExpectedDataSize(width, height, fourCC, mipLevels);
        var actualDataSize = (uint)(data.Length - 128);
        result.Info.DataSize = actualDataSize;
        result.Info.ExpectedDataSize = expectedSize;
        
        if (expectedSize > 0)
        {
            var sizeRatio = (double)actualDataSize / expectedSize;
            if (actualDataSize == 0)
            {
                issues.Add("No texture data (0 bytes after header)");
                score += 80;
            }
            else if (sizeRatio < 0.1)
            {
                issues.Add($"Data severely undersized: {actualDataSize} bytes (expected ~{expectedSize})");
                score += 60;
            }
            else if (sizeRatio < 0.5)
            {
                issues.Add($"Data undersized: {actualDataSize} bytes (expected ~{expectedSize})");
                score += 30;
            }
            else if (sizeRatio > 2.0)
            {
                issues.Add($"Data oversized: {actualDataSize} bytes (expected ~{expectedSize})");
                score += 10;
            }
        }
        
        // Analyze texture data for corruption patterns
        var textureData = data.AsSpan(128);
        
        // Check for all-zero data (completely black/empty texture)
        var zeroRatio = CalculateZeroRatio(textureData);
        if (zeroRatio > 0.99)
        {
            issues.Add($"Texture data is {zeroRatio * 100:F1}% zeros (completely empty)");
            score += 70;
        }
        else if (zeroRatio > 0.9)
        {
            issues.Add($"Texture data is {zeroRatio * 100:F1}% zeros (mostly empty)");
            score += 40;
        }
        else if (zeroRatio > 0.7)
        {
            issues.Add($"High zero ratio: {zeroRatio * 100:F1}%");
            score += 15;
        }
        
        // Check for uniform color (all same byte value)
        var uniformityScore = CalculateUniformityScore(textureData);
        if (uniformityScore > 0.95)
        {
            issues.Add($"Texture data highly uniform ({uniformityScore * 100:F1}% same value)");
            score += 50;
        }
        else if (uniformityScore > 0.8)
        {
            issues.Add($"Texture data mostly uniform ({uniformityScore * 100:F1}% same value)");
            score += 20;
        }
        
        // Check for repeating patterns (sign of tiling/untiling errors)
        var patternScore = DetectRepeatingPatterns(textureData, width, height, fourCC);
        if (patternScore > 50)
        {
            issues.Add($"Suspicious repeating pattern detected (score: {patternScore})");
            score += patternScore / 2;
        }
        
        // Check DXT block validity for compressed formats
        if (fourCC == "DXT1" || fourCC == "DXT3" || fourCC == "DXT5" || 
            fourCC == "ATI1" || fourCC == "ATI2" || fourCC == "BC4U" || fourCC == "BC5U")
        {
            var blockIssues = ValidateDxtBlocks(textureData, fourCC, width, height);
            if (blockIssues > 50)
            {
                issues.Add($"Many invalid DXT blocks detected ({blockIssues}% problematic)");
                score += blockIssues / 2;
            }
            else if (blockIssues > 20)
            {
                issues.Add($"Some invalid DXT blocks detected ({blockIssues}% problematic)");
                score += blockIssues / 4;
            }
        }
        
        // Check entropy (random/encrypted data has high entropy, valid textures have medium)
        var entropy = CalculateEntropy(textureData);
        if (entropy < 1.0 && textureData.Length > 0)
        {
            issues.Add($"Very low entropy ({entropy:F2}) - texture may be empty or uniform");
            score += 20;
        }
        else if (entropy > 7.5)
        {
            issues.Add($"Very high entropy ({entropy:F2}) - texture may be corrupted or random data");
            score += 15;
        }
        
        // Cap score at 100
        score = Math.Min(100, score);
        
        result.Issues = issues;
        result.CorruptionScore = score;
        result.HasCorruptionIndicators = score > 20;
        result.Level = score switch
        {
            >= 80 => CorruptionLevel.Severe,
            >= 50 => CorruptionLevel.High,
            >= 30 => CorruptionLevel.Medium,
            >= 10 => CorruptionLevel.Low,
            _ => CorruptionLevel.None
        };
        
        return result;
    }
    
    /// <summary>
    /// Batch validate all DDS files in a directory
    /// </summary>
    public static IEnumerable<(string Path, ValidationResult Result)> ValidateDirectory(
        string directory, 
        bool recursive = true)
    {
        var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var files = Directory.GetFiles(directory, "*.dds", searchOption);
        
        foreach (var file in files)
        {
            yield return (file, Validate(file));
        }
    }
    
    /// <summary>
    /// Generate a summary report for a directory of DDS files
    /// </summary>
    public static string GenerateReport(string directory, bool recursive = true)
    {
        var results = ValidateDirectory(directory, recursive).ToList();
        var sb = new StringBuilder();
        
        sb.AppendLine($"DDS Validation Report: {directory}");
        sb.AppendLine($"Total files: {results.Count}");
        sb.AppendLine();
        
        var byLevel = results.GroupBy(r => r.Result.Level).OrderByDescending(g => (int)g.Key);
        foreach (var group in byLevel)
        {
            sb.AppendLine($"{group.Key}: {group.Count()} files");
        }
        sb.AppendLine();
        
        // List problematic files
        var problematic = results.Where(r => r.Result.Level >= CorruptionLevel.Medium)
            .OrderByDescending(r => r.Result.CorruptionScore);
        
        if (problematic.Any())
        {
            sb.AppendLine("=== Problematic Files ===");
            foreach (var (path, result) in problematic)
            {
                sb.AppendLine($"\n{Path.GetFileName(path)} [Score: {result.CorruptionScore}]");
                foreach (var issue in result.Issues)
                    sb.AppendLine($"  - {issue}");
            }
        }
        
        return sb.ToString();
    }
    
    private static bool IsPowerOfTwo(uint value) => value > 0 && (value & (value - 1)) == 0;
    
    private static uint CalculateExpectedDataSize(uint width, uint height, string fourCC, uint mipLevels)
    {
        if (width == 0 || height == 0) return 0;
        
        int blockSize = fourCC switch
        {
            "DXT1" or "ATI1" or "BC4U" or "BC4S" => 8,
            "DXT3" or "DXT5" or "ATI2" or "BC5U" or "BC5S" => 16,
            _ => 0
        };
        
        if (blockSize == 0) return 0; // Unknown format
        
        uint total = 0;
        var w = width;
        var h = height;
        var levels = mipLevels == 0 ? 1 : mipLevels;
        
        for (uint i = 0; i < levels && w >= 1 && h >= 1; i++)
        {
            var blocksWide = Math.Max(1u, (w + 3) / 4);
            var blocksHigh = Math.Max(1u, (h + 3) / 4);
            total += blocksWide * blocksHigh * (uint)blockSize;
            w /= 2;
            h /= 2;
        }
        
        return total;
    }
    
    private static double CalculateZeroRatio(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) return 0;
        
        int zeros = 0;
        foreach (var b in data)
        {
            if (b == 0) zeros++;
        }
        return (double)zeros / data.Length;
    }
    
    private static double CalculateUniformityScore(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) return 0;
        
        // Count byte frequency
        Span<int> counts = stackalloc int[256];
        foreach (var b in data)
        {
            counts[b]++;
        }
        
        // Find most common byte
        int maxCount = 0;
        foreach (var c in counts)
        {
            if (c > maxCount) maxCount = c;
        }
        
        return (double)maxCount / data.Length;
    }
    
    private static int DetectRepeatingPatterns(ReadOnlySpan<byte> data, uint width, uint height, string fourCC)
    {
        if (data.Length < 64) return 0;
        
        // Calculate block row size for the format
        int blockSize = fourCC switch
        {
            "DXT1" or "ATI1" => 8,
            "DXT3" or "DXT5" or "ATI2" => 16,
            _ => 16
        };
        
        var blocksPerRow = Math.Max(1, (int)width / 4);
        var rowBytes = blocksPerRow * blockSize;
        
        if (rowBytes == 0 || data.Length < rowBytes * 2) return 0;
        
        // Check if consecutive rows are identical (strong corruption indicator)
        int identicalRows = 0;
        int totalRows = Math.Min(16, data.Length / rowBytes - 1);
        
        for (int i = 0; i < totalRows; i++)
        {
            var row1 = data.Slice(i * rowBytes, rowBytes);
            var row2 = data.Slice((i + 1) * rowBytes, rowBytes);
            
            if (row1.SequenceEqual(row2))
                identicalRows++;
        }
        
        if (totalRows == 0) return 0;
        return (identicalRows * 100) / totalRows;
    }
    
    private static int ValidateDxtBlocks(ReadOnlySpan<byte> data, string fourCC, uint width, uint height)
    {
        int blockSize = fourCC switch
        {
            "DXT1" or "ATI1" => 8,
            "DXT3" or "DXT5" or "ATI2" => 16,
            _ => 16
        };
        
        var totalBlocks = data.Length / blockSize;
        if (totalBlocks == 0) return 0;
        
        int problematicBlocks = 0;
        var maxBlocks = Math.Min(1000, totalBlocks); // Sample up to 1000 blocks
        
        for (int i = 0; i < maxBlocks; i++)
        {
            var blockOffset = i * blockSize;
            if (blockOffset + blockSize > data.Length) break;
            
            var block = data.Slice(blockOffset, blockSize);
            
            // Check for completely empty blocks
            bool allZero = true;
            foreach (var b in block)
            {
                if (b != 0) { allZero = false; break; }
            }
            if (allZero) problematicBlocks++;
            
            // For DXT1, check color endpoints
            if (fourCC == "DXT1" || fourCC == "DXT3" || fourCC == "DXT5")
            {
                int colorOffset = fourCC == "DXT1" ? 0 : (fourCC == "DXT3" ? 8 : 8);
                if (blockOffset + colorOffset + 4 <= data.Length)
                {
                    var color0 = BitConverter.ToUInt16(data.Slice(blockOffset + colorOffset, 2));
                    var color1 = BitConverter.ToUInt16(data.Slice(blockOffset + colorOffset + 2, 2));
                    
                    // Both colors being 0 is suspicious but not definitive
                    if (color0 == 0 && color1 == 0)
                    {
                        // Check if indices are also all zero - that's more suspicious
                        if (blockOffset + colorOffset + 8 <= data.Length)
                        {
                            var indices = BitConverter.ToUInt32(data.Slice(blockOffset + colorOffset + 4, 4));
                            if (indices == 0) problematicBlocks++;
                        }
                    }
                }
            }
        }
        
        return (problematicBlocks * 100) / Math.Max(1, (int)maxBlocks);
    }
    
    private static double CalculateEntropy(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) return 0;
        
        // Count byte frequency
        Span<int> counts = stackalloc int[256];
        foreach (var b in data)
        {
            counts[b]++;
        }
        
        // Calculate Shannon entropy
        double entropy = 0;
        var len = (double)data.Length;
        
        for (int i = 0; i < 256; i++)
        {
            if (counts[i] == 0) continue;
            var p = counts[i] / len;
            entropy -= p * Math.Log2(p);
        }
        
        return entropy;
    }
}
