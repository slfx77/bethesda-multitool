using Xbox360MemoryCarver.Core;
using Xunit;

namespace Xbox360MemoryCarver.Tests.Core;

/// <summary>
///     Tests for SignatureMatcher (Aho-Corasick multi-pattern search).
/// </summary>
public class SignatureMatcherTests
{
    #region Pattern Data Preservation Tests

    [Fact]
    public void Search_ReturnsCorrectPatternBytes()
    {
        // Arrange
        var pattern = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        var matcher = new SignatureMatcher();
        matcher.AddPattern("test", pattern);
        matcher.Build();

        var data = new byte[] { 0x00, 0xDE, 0xAD, 0xBE, 0xEF, 0x00 };

        // Act
        var results = matcher.Search(data);

        // Assert
        Assert.Single(results);
        Assert.Equal(pattern, results[0].Pattern);
    }

    #endregion

    #region Construction and Properties

    [Fact]
    public void Constructor_InitializesWithZeroPatterns()
    {
        // Arrange & Act
        var matcher = new SignatureMatcher();

        // Assert
        Assert.Equal(0, matcher.PatternCount);
        Assert.Equal(0, matcher.MaxPatternLength);
    }

    [Fact]
    public void AddPattern_IncrementsPatternCount()
    {
        // Arrange
        var matcher = new SignatureMatcher();

        // Act
        matcher.AddPattern("test", [0x01, 0x02, 0x03]);

        // Assert
        Assert.Equal(1, matcher.PatternCount);
    }

    [Fact]
    public void AddPattern_UpdatesMaxPatternLength()
    {
        // Arrange
        var matcher = new SignatureMatcher();

        // Act
        matcher.AddPattern("short", [0x01, 0x02]);
        matcher.AddPattern("long", [0x01, 0x02, 0x03, 0x04, 0x05]);
        matcher.AddPattern("medium", [0x01, 0x02, 0x03]);

        // Assert
        Assert.Equal(5, matcher.MaxPatternLength);
    }

    [Fact]
    public void AddPattern_NullPattern_ThrowsArgumentNullException()
    {
        // Arrange
        var matcher = new SignatureMatcher();

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => matcher.AddPattern("test", null!));
    }

    #endregion

    #region Build Tests

    [Fact]
    public void Build_WithNoPatterns_DoesNotThrow()
    {
        // Arrange
        var matcher = new SignatureMatcher();

        // Act & Assert - should not throw
        var exception = Record.Exception(() => matcher.Build());
        Assert.Null(exception);
    }

    [Fact]
    public void Build_WithPatterns_Succeeds()
    {
        // Arrange
        var matcher = new SignatureMatcher();
        matcher.AddPattern("dds", "DDS "u8.ToArray());
        matcher.AddPattern("png", [0x89, 0x50, 0x4E, 0x47]);

        // Act & Assert
        var exception = Record.Exception(() => matcher.Build());
        Assert.Null(exception);
    }

    #endregion

    #region Search - Single Pattern Tests

    [Fact]
    public void Search_SinglePattern_FindsMatch()
    {
        // Arrange
        var matcher = new SignatureMatcher();
        matcher.AddPattern("magic", [0xDE, 0xAD, 0xBE, 0xEF]);
        matcher.Build();

        var data = new byte[] { 0x00, 0x00, 0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0x00 };

        // Act
        var results = matcher.Search(data);

        // Assert
        Assert.Single(results);
        Assert.Equal("magic", results[0].Name);
        Assert.Equal(2, results[0].Position);
    }

    [Fact]
    public void Search_SinglePattern_NoMatch_ReturnsEmpty()
    {
        // Arrange
        var matcher = new SignatureMatcher();
        matcher.AddPattern("magic", [0xDE, 0xAD, 0xBE, 0xEF]);
        matcher.Build();

        var data = new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05 };

        // Act
        var results = matcher.Search(data);

        // Assert
        Assert.Empty(results);
    }

    [Fact]
    public void Search_PatternAtStart_FindsMatch()
    {
        // Arrange
        var matcher = new SignatureMatcher();
        matcher.AddPattern("dds", "DDS "u8.ToArray());
        matcher.Build();

        var data = "DDS header data..."u8.ToArray();

        // Act
        var results = matcher.Search(data);

        // Assert
        Assert.Single(results);
        Assert.Equal(0, results[0].Position);
    }

    [Fact]
    public void Search_PatternAtEnd_FindsMatch()
    {
        // Arrange
        var matcher = new SignatureMatcher();
        matcher.AddPattern("end", [0xFF, 0xFE]);
        matcher.Build();

        var data = new byte[] { 0x00, 0x01, 0x02, 0xFF, 0xFE };

        // Act
        var results = matcher.Search(data);

        // Assert
        Assert.Single(results);
        Assert.Equal(3, results[0].Position);
    }

    [Fact]
    public void Search_MultipleOccurrences_FindsAll()
    {
        // Arrange
        var matcher = new SignatureMatcher();
        matcher.AddPattern("ab", [0xAB]);
        matcher.Build();

        var data = new byte[] { 0xAB, 0x00, 0xAB, 0x00, 0xAB };

        // Act
        var results = matcher.Search(data);

        // Assert
        Assert.Equal(3, results.Count);
        Assert.Equal(0, results[0].Position);
        Assert.Equal(2, results[1].Position);
        Assert.Equal(4, results[2].Position);
    }

    #endregion

    #region Search - Multiple Pattern Tests

    [Fact]
    public void Search_MultiplePatterns_FindsAllTypes()
    {
        // Arrange
        var matcher = new SignatureMatcher();
        matcher.AddPattern("dds", "DDS "u8.ToArray());
        matcher.AddPattern("png", [0x89, 0x50, 0x4E, 0x47]); // PNG magic
        matcher.AddPattern("nif", "NIF\0"u8.ToArray());
        matcher.Build();

        // Data contains all three signatures
        var data = new byte[50];
        "DDS "u8.ToArray().CopyTo(data, 0);
        new byte[] { 0x89, 0x50, 0x4E, 0x47 }.CopyTo(data, 20);
        "NIF\0"u8.ToArray().CopyTo(data, 40);

        // Act
        var results = matcher.Search(data);

        // Assert
        Assert.Equal(3, results.Count);
        Assert.Contains(results, r => r.Name == "dds" && r.Position == 0);
        Assert.Contains(results, r => r.Name == "png" && r.Position == 20);
        Assert.Contains(results, r => r.Name == "nif" && r.Position == 40);
    }

    [Fact]
    public void Search_OverlappingPatterns_FindsBoth()
    {
        // Arrange
        var matcher = new SignatureMatcher();
        matcher.AddPattern("abc", [0x61, 0x62, 0x63]); // "abc"
        matcher.AddPattern("bcd", [0x62, 0x63, 0x64]); // "bcd"
        matcher.Build();

        var data = "abcd"u8.ToArray(); // Contains both patterns overlapping

        // Act
        var results = matcher.Search(data);

        // Assert
        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.Name == "abc" && r.Position == 0);
        Assert.Contains(results, r => r.Name == "bcd" && r.Position == 1);
    }

    [Fact]
    public void Search_SharedPrefixPatterns_FindsCorrect()
    {
        // Arrange - Patterns with shared prefix
        var matcher = new SignatureMatcher();
        matcher.AddPattern("ab", [0xAB, 0xCD]);
        matcher.AddPattern("abc", [0xAB, 0xCD, 0xEF]);
        matcher.Build();

        var data = new byte[] { 0xAB, 0xCD, 0xEF, 0x00 };

        // Act
        var results = matcher.Search(data);

        // Assert - Should find both: "ab" at 0, "abc" at 0
        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.Name == "ab" && r.Position == 0);
        Assert.Contains(results, r => r.Name == "abc" && r.Position == 0);
    }

    #endregion

    #region Search - Base Offset Tests

    [Fact]
    public void Search_WithBaseOffset_AdjustsPositions()
    {
        // Arrange
        var matcher = new SignatureMatcher();
        matcher.AddPattern("test", [0xAA, 0xBB]);
        matcher.Build();

        var data = new byte[] { 0x00, 0xAA, 0xBB, 0x00 };
        const long baseOffset = 1000;

        // Act
        var results = matcher.Search(data, baseOffset);

        // Assert
        Assert.Single(results);
        Assert.Equal(1001, results[0].Position); // 1000 + 1
    }

    [Fact]
    public void Search_WithLargeBaseOffset_HandlesCorrectly()
    {
        // Arrange
        var matcher = new SignatureMatcher();
        matcher.AddPattern("sig", [0xFF]);
        matcher.Build();

        var data = new byte[] { 0xFF };
        const long baseOffset = 0x7FFFFFFF; // Large offset (2GB)

        // Act
        var results = matcher.Search(data, baseOffset);

        // Assert
        Assert.Single(results);
        Assert.Equal(0x7FFFFFFF, results[0].Position);
    }

    #endregion

    #region Search - Edge Cases

    [Fact]
    public void Search_EmptyData_ReturnsEmpty()
    {
        // Arrange
        var matcher = new SignatureMatcher();
        matcher.AddPattern("test", [0x01, 0x02]);
        matcher.Build();

        // Act
        var results = matcher.Search(ReadOnlySpan<byte>.Empty);

        // Assert
        Assert.Empty(results);
    }

    [Fact]
    public void Search_DataShorterThanPattern_ReturnsEmpty()
    {
        // Arrange
        var matcher = new SignatureMatcher();
        matcher.AddPattern("long", [0x01, 0x02, 0x03, 0x04, 0x05]);
        matcher.Build();

        var data = new byte[] { 0x01, 0x02, 0x03 };

        // Act
        var results = matcher.Search(data);

        // Assert
        Assert.Empty(results);
    }

    [Fact]
    public void Search_PatternEqualsDataLength_FindsMatch()
    {
        // Arrange
        var matcher = new SignatureMatcher();
        matcher.AddPattern("exact", [0x01, 0x02, 0x03]);
        matcher.Build();

        var data = new byte[] { 0x01, 0x02, 0x03 };

        // Act
        var results = matcher.Search(data);

        // Assert
        Assert.Single(results);
        Assert.Equal(0, results[0].Position);
    }

    [Fact]
    public void Search_SingleBytePattern_FindsAll()
    {
        // Arrange
        var matcher = new SignatureMatcher();
        matcher.AddPattern("null", [0x00]);
        matcher.Build();

        var data = new byte[] { 0x00, 0x01, 0x00, 0x02, 0x00 };

        // Act
        var results = matcher.Search(data);

        // Assert
        Assert.Equal(3, results.Count);
    }

    [Fact]
    public void Search_ConsecutivePatterns_FindsAll()
    {
        // Arrange
        var matcher = new SignatureMatcher();
        matcher.AddPattern("aa", [0xAA, 0xAA]);
        matcher.Build();

        var data = new byte[] { 0xAA, 0xAA, 0xAA, 0xAA };

        // Act
        var results = matcher.Search(data);

        // Assert - Should find at positions 0, 1, 2
        Assert.Equal(3, results.Count);
        Assert.Equal(0, results[0].Position);
        Assert.Equal(1, results[1].Position);
        Assert.Equal(2, results[2].Position);
    }

    #endregion

    #region Real-World File Signature Tests

    [Fact]
    public void Search_RealFileSignatures_FindsCorrectly()
    {
        // Arrange - Common Xbox 360 file signatures
        var matcher = new SignatureMatcher();
        matcher.AddPattern("ddx_3xdo", "3XDO"u8.ToArray());
        matcher.AddPattern("ddx_3xdr", "3XDR"u8.ToArray());
        matcher.AddPattern("dds", "DDS "u8.ToArray());
        matcher.AddPattern("xex", "XEX2"u8.ToArray());
        matcher.AddPattern("xui", "XUI "u8.ToArray());
        matcher.Build();

        // Simulate a memory dump with multiple signatures
        var data = new byte[200];
        "3XDO"u8.ToArray().CopyTo(data, 0); // DDX texture
        "DDS "u8.ToArray().CopyTo(data, 50); // DDS texture
        "XEX2"u8.ToArray().CopyTo(data, 100); // Xbox executable
        "3XDR"u8.ToArray().CopyTo(data, 150); // DDX variant

        // Act
        var results = matcher.Search(data);

        // Assert
        Assert.Equal(4, results.Count);
        Assert.Contains(results, r => r.Name == "ddx_3xdo" && r.Position == 0);
        Assert.Contains(results, r => r.Name == "dds" && r.Position == 50);
        Assert.Contains(results, r => r.Name == "xex" && r.Position == 100);
        Assert.Contains(results, r => r.Name == "ddx_3xdr" && r.Position == 150);
    }

    [Fact]
    public void Search_PngSignature_FindsCorrectly()
    {
        // Arrange - PNG has an 8-byte signature
        var matcher = new SignatureMatcher();
        matcher.AddPattern("png", [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        matcher.Build();

        var data = new byte[20];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(data, 5);

        // Act
        var results = matcher.Search(data);

        // Assert
        Assert.Single(results);
        Assert.Equal("png", results[0].Name);
        Assert.Equal(5, results[0].Position);
    }

    [Fact]
    public void Search_NifSignatures_FindsBothVariants()
    {
        // Arrange - NIF has two possible signatures
        var matcher = new SignatureMatcher();
        matcher.AddPattern("nif_game", "Gamebryo File Format"u8.ToArray());
        matcher.AddPattern("nif_ni", "NetImmerse File Format"u8.ToArray());
        matcher.Build();

        var data = new byte[100];
        "Gamebryo File Format"u8.ToArray().CopyTo(data, 10);
        "NetImmerse File Format"u8.ToArray().CopyTo(data, 60);

        // Act
        var results = matcher.Search(data);

        // Assert
        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.Name == "nif_game" && r.Position == 10);
        Assert.Contains(results, r => r.Name == "nif_ni" && r.Position == 60);
    }

    #endregion
}