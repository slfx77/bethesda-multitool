namespace BethesdaMultitool.Core.Utils;

/// <summary>A compiled byte-search pattern, with an optional lowercased copy for ASCII case-insensitive matching.</summary>
internal readonly record struct BinarySearchPattern(
    byte[] PatternBytes,
    byte[]? PatternBytesLower);
