namespace BethesdaMultitool.Core.Utils;

internal readonly record struct BinarySearchPattern(
    byte[] PatternBytes,
    byte[]? PatternBytesLower);
