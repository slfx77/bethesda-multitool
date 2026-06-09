using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Item;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.AssetPacking;
using FalloutXbox360Utils.Core.Formats.Esm.Reporting;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Plugin.AssetPacking;

/// <summary>
///     v22: <see cref="AssetPathRewriter" /> rewrites record path fields when the v21
///     fuzzy resolver matches a differently-named asset in an indexed Data folder. Tests
///     cover both the path-style preservation (<see cref="AssetPathRewriter.DenormalizeForField" />)
///     and the end-to-end rewrite against a synthetic <see cref="RecordCollection" />.
/// </summary>
public class AssetPathRewriterTests : IDisposable
{
    private readonly string _scratchRoot = Path.Combine(
        Path.GetTempPath(),
        $"assetrewriter-{Guid.NewGuid():N}");

    public AssetPathRewriterTests()
    {
        Directory.CreateDirectory(_scratchRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_scratchRoot))
            {
                Directory.Delete(_scratchRoot, true);
            }
        }
        catch
        {
            // best-effort
        }
    }

    private string MakeDataFolder(string label)
    {
        return Path.Combine(_scratchRoot, label);
    }

    private static void WriteLooseFile(string dataFolder, string relativePath, ReadOnlySpan<byte> bytes)
    {
        var abs = Path.Combine(dataFolder, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        File.WriteAllBytes(abs, bytes.ToArray());
    }

    // ---- DenormalizeForField ---------------------------------------------------

    [Fact]
    public void DenormalizeForField_OriginalHadTypePrefix_KeepsPrefix()
    {
        // Original ESP field stored a fully-qualified path. New path keeps the prefix.
        var result = AssetPathRewriter.DenormalizeForField(
            "meshes\\armor\\legion\\renamed.nif",
            "meshes\\armor\\legion\\original.nif");

        Assert.Equal("meshes\\armor\\legion\\renamed.nif", result);
    }

    [Fact]
    public void DenormalizeForField_OriginalRelative_StripsPrefix()
    {
        // Original ESP field stored a relative path (no meshes\ prefix). New path strips
        // the prefix so the runtime concatenates correctly.
        var result = AssetPathRewriter.DenormalizeForField(
            "meshes\\armor\\legion\\renamed.nif",
            "armor\\legion\\original.nif");

        Assert.Equal("armor\\legion\\renamed.nif", result);
    }

    [Fact]
    public void DenormalizeForField_ExtensionSwap_DdxToDds_PreservesPrefixStyle()
    {
        // 360 conversion swaps .ddx → .dds. Rename should carry the new extension.
        // Original was relative; new should also be relative.
        var resultRelative = AssetPathRewriter.DenormalizeForField(
            "textures\\armor\\foo.dds",
            "armor\\foo.ddx");
        Assert.Equal("armor\\foo.dds", resultRelative);

        // Original was fully-qualified; new should preserve the type prefix.
        var resultFull = AssetPathRewriter.DenormalizeForField(
            "textures\\armor\\foo.dds",
            "textures\\armor\\foo.ddx");
        Assert.Equal("textures\\armor\\foo.dds", resultFull);
    }

    // ---- ApplyRewrites end-to-end ---------------------------------------------

    [Fact]
    public void ApplyRewrites_FuzzyDirectoryShuffleInSecondary_RewritesArmorModelPath()
    {
        // The v21 fuzzy resolver matches by basename across all indexed folders. When an
        // asset was MOVED between directories (same filename, new directory), it appears
        // as a fuzzy hit with a different resolved path — exactly the case the v22 rewriter
        // targets. Record: ArmorRecord.ModelPath = "armor\\legion\\wolffhead.nif" (relative).
        // Secondary has the same basename but in a different directory:
        // "meshes\\armor\\centurion\\wolffhead.nif". After rewrite, the record's ModelPath
        // should point at the new directory, preserving the field's original relative-style.
        var baselineDir = MakeDataFolder("baseline");
        Directory.CreateDirectory(baselineDir);

        var secondaryDir = MakeDataFolder("fo3");
        WriteLooseFile(secondaryDir, "meshes\\armor\\centurion\\wolffhead.nif", [1, 2, 3]);

        using var baseline = new DataFolderIndex(baselineDir, false);
        baseline.Build();
        using var secondary = new DataFolderIndex(secondaryDir, false);
        secondary.Build();

        var resolver = new DataFolderResolver(baseline, [secondary]);

        var armor = new ArmorRecord
        {
            FormId = 0x100,
            EditorId = "LegionWolfHead",
            ModelPath = "armor\\legion\\wolffhead.nif"
        };
        var records = new RecordCollection { Armor = [armor] };

        var result = AssetPathRewriter.ApplyRewrites(records, resolver, NullConversionProgressSink.Instance);

        Assert.Equal(1, result.Rewritten);
        // ApplyRewrites mutates records in-place via reflection SetValue. The List<ArmorRecord>
        // stores the same record instance, so its ModelPath now reflects the rewrite.
        Assert.Equal("armor\\centurion\\wolffhead.nif", records.Armor[0].ModelPath);
    }

    [Fact]
    public void ApplyRewrites_ExactBaselineMatch_DoesNotRewrite()
    {
        // Baseline has the exact asset. No rewrite happens; resolver returns AlreadyInBaseline
        // (ResolvedPath is null), so the rewriter skips it.
        var baselineDir = MakeDataFolder("baseline");
        WriteLooseFile(baselineDir, "meshes\\armor\\legion\\original.nif", [9, 9, 9]);

        using var baseline = new DataFolderIndex(baselineDir, false);
        baseline.Build();
        var resolver = new DataFolderResolver(baseline, []);

        var armor = new ArmorRecord
        {
            FormId = 0x100,
            EditorId = "Original",
            ModelPath = "armor\\legion\\original.nif"
        };
        var records = new RecordCollection { Armor = [armor] };

        var result = AssetPathRewriter.ApplyRewrites(records, resolver, NullConversionProgressSink.Instance);

        Assert.Equal(0, result.Rewritten);
        Assert.Equal("armor\\legion\\original.nif", records.Armor[0].ModelPath);
    }

    [Fact]
    public void ApplyRewrites_MissingPath_DoesNotRewrite()
    {
        // No folder has the asset. Resolver returns Missing; rewriter takes no action.
        var baselineDir = MakeDataFolder("baseline");
        Directory.CreateDirectory(baselineDir);
        using var baseline = new DataFolderIndex(baselineDir, false);
        baseline.Build();
        var resolver = new DataFolderResolver(baseline, []);

        var armor = new ArmorRecord
        {
            FormId = 0x100,
            EditorId = "Vanished",
            ModelPath = "armor\\never\\found.nif"
        };
        var records = new RecordCollection { Armor = [armor] };

        var result = AssetPathRewriter.ApplyRewrites(records, resolver, NullConversionProgressSink.Instance);

        Assert.Equal(0, result.Rewritten);
        Assert.True(result.SkippedMissing >= 1);
        Assert.Equal("armor\\never\\found.nif", records.Armor[0].ModelPath);
    }
}