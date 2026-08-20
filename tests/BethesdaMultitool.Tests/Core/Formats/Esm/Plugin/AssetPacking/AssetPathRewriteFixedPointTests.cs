using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Item;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Esm.Plugin.AssetPacking;
using BethesdaMultitool.Core.Formats.Esm.Reporting;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Plugin.AssetPacking;

/// <summary>
///     The asset-rename pass must write the path the PACKER will produce, not the donor's own
///     name. Writing the donor name is what left 49 SOUN records per build naming <c>.xma</c>
///     files no archive contained (those sounds were silent) and gave 17 MUSC records a
///     <c>music\</c> prefix retail never has. Fixed 2026-08-13.
/// </summary>
public sealed class AssetPathRewriteFixedPointTests : IDisposable
{
    private const string Sep = "\\";

    private readonly string _scratchRoot = Path.Combine(
        Path.GetTempPath(), $"assetfixedpoint-{Guid.NewGuid():N}");

    private bool _disposed;

    public AssetPathRewriteFixedPointTests()
    {
        Directory.CreateDirectory(_scratchRoot);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
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

        GC.SuppressFinalize(this);
    }

    private string MakeDataFolder(string label)
    {
        return Path.Combine(_scratchRoot, label);
    }

    private static void WriteLooseFile(string dataFolder, string relativePath)
    {
        var abs = Path.Combine(dataFolder, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        File.WriteAllBytes(abs, [1, 2, 3]);
    }

    /// <summary>Baseline + optional 360 donor. Loose files inherit the folder's format hint.</summary>
    private (DataFolderResolver Resolver, IDisposable[] Indexes) BuildResolver(
        string? baselineFile, string? donorFile, bool donorIsXbox360 = true)
    {
        var baselineDir = MakeDataFolder("baseline");
        Directory.CreateDirectory(baselineDir);
        if (baselineFile is not null)
        {
            WriteLooseFile(baselineDir, baselineFile);
        }

        var baseline = new DataFolderIndex(baselineDir, false);
        baseline.Build();

        if (donorFile is null)
        {
            return (new DataFolderResolver(baseline, []), [baseline]);
        }

        var donorDir = MakeDataFolder("donor");
        WriteLooseFile(donorDir, donorFile);
        var donor = new DataFolderIndex(donorDir, donorIsXbox360);
        donor.Build();
        return (new DataFolderResolver(baseline, [donor]), [baseline, donor]);
    }

    private static void Dispose(IDisposable[] indexes)
    {
        foreach (var index in indexes)
        {
            index.Dispose();
        }
    }

    [Fact]
    public void WavRequest_Xma360Donor_LeavesFieldAlone()
    {
        // THE regression. The only donor copy is the 360 .xma; the packer converts it and
        // writes the entry at .wav — which is what the field already says.
        var (resolver, indexes) = BuildResolver(null, "sound\\fx\\amb\\wind_lp.xma");
        try
        {
            var records = new RecordCollection
            {
                Sounds = [new SoundRecord { FormId = 0x100, FileName = "fx\\amb\\wind_lp.wav" }]
            };

            var result = AssetPathRewriter.ApplyRewrites(
                records, resolver, NullConversionProgressSink.Instance);

            Assert.Equal(0, result.Rewritten);
            Assert.Equal("fx\\amb\\wind_lp.wav", records.Sounds[0].FileName);
        }
        finally
        {
            Dispose(indexes);
        }
    }

    [Fact]
    public void XmaRequest_Xma360Donor_RewritesToWav()
    {
        // The field itself names a 360 container, so it has to move even though the donor
        // matched exactly — the archive will hold .wav.
        var (resolver, indexes) = BuildResolver(null, "sound\\fx\\amb\\wind_lp.xma");
        try
        {
            var records = new RecordCollection
            {
                Sounds = [new SoundRecord { FormId = 0x100, FileName = "fx\\amb\\wind_lp.xma" }]
            };

            var result = AssetPathRewriter.ApplyRewrites(
                records, resolver, NullConversionProgressSink.Instance);

            Assert.Equal(1, result.Rewritten);
            Assert.Equal("fx\\amb\\wind_lp.wav", records.Sounds[0].FileName);
        }
        finally
        {
            Dispose(indexes);
        }
    }

    [Fact]
    public void DdsRequest_Ddx360Donor_LeavesFieldAlone()
    {
        // The texture twin of the .xma case.
        var (resolver, indexes) = BuildResolver(null, "textures\\armor\\x.ddx");
        try
        {
            var records = new RecordCollection
            {
                Armor = [new ArmorRecord { FormId = 0x100, IconPath = "armor\\x.dds" }]
            };

            var result = AssetPathRewriter.ApplyRewrites(
                records, resolver, NullConversionProgressSink.Instance);

            Assert.Equal(0, result.Rewritten);
            Assert.Equal("armor\\x.dds", records.Armor[0].IconPath);
        }
        finally
        {
            Dispose(indexes);
        }
    }

    [Fact]
    public void MuscMp3ResolvedUnderMusicRoot_DoesNotGainMusicPrefix()
    {
        // MUSC FNAM is resolved relative to Data\Music\. Rooting the request by extension
        // sent it to sound\, the match came back under music\, and the record kept that
        // root — a prefix retail never has, on 17 records per build.
        var (resolver, indexes) = BuildResolver("music\\endgame\\endgame_02.mp3", null);
        try
        {
            var records = new RecordCollection
            {
                MusicTypes =
                [
                    new MusicTypeRecord { FormId = 0x100, FileName = "endgame\\endgame_02.mp3" }
                ]
            };

            var result = AssetPathRewriter.ApplyRewrites(
                records, resolver, NullConversionProgressSink.Instance);

            Assert.Equal(0, result.Rewritten);
            Assert.Equal("endgame\\endgame_02.mp3", records.MusicTypes[0].FileName);
        }
        finally
        {
            Dispose(indexes);
        }
    }

    [Fact]
    public void SounRadioMp3_StaysRootedAtSound()
    {
        // Tripwire for mis-rooting .mp3 wholesale: the songs\radio\* family are SOUN records
        // under Data\Sound\, and 30 per build resolve correctly today.
        var (resolver, indexes) = BuildResolver("sound\\songs\\radio\\enclave\\america.mp3", null);
        try
        {
            var records = new RecordCollection
            {
                Sounds =
                [
                    new SoundRecord { FormId = 0x100, FileName = "songs\\radio\\enclave\\america.mp3" }
                ]
            };

            var result = AssetPathRewriter.ApplyRewrites(
                records, resolver, NullConversionProgressSink.Instance);

            Assert.Equal(0, result.Rewritten);
            Assert.Equal("songs\\radio\\enclave\\america.mp3", records.Sounds[0].FileName);
        }
        finally
        {
            Dispose(indexes);
        }
    }

    [Theory]
    [InlineData("fx\\amb\\wind_loop.wav", "sound\\fx\\amb\\wind_loop.xma", true, false)]
    [InlineData("fx\\amb\\wind_loop.xma", "sound\\fx\\amb\\wind_loop.xma", true, false)]
    [InlineData("fx\\amb\\x.wav", "sound\\fx\\amb\\x.ogg", false, false)]
    // Directory shuffle (fuzzy matches on basename) AND a 360 container: the stem must move
    // to the donor's folder while the extension stays the one the packer will write.
    [InlineData("fx\\amb\\wind_lp.wav", "sound\\fx\\ambient\\wind_lp.xma", true, false)]
    [InlineData("armor\\x.dds", "textures\\armor\\x.ddx", true, true)]
    [InlineData("armor\\x.ddx", "textures\\armor\\x.ddx", true, true)]
    public void RewrittenValueIsAFixedPoint(
        string field, string donorFile, bool donorIsXbox360, bool texture)
    {
        // The invariant welding the rewriter to the packer: whatever the field ends up
        // saying, re-resolving it must predict that same packed name. Any future change to
        // the swap table, the converter, or the packer's naming breaks this — which is the
        // point, because that drift is the whole defect class.
        //
        // One record per case on purpose: AssetPathCollector source-tracks a given path only
        // the first time it sees it, so two records sharing a value would leave the second
        // unrewritten and this would fail for an unrelated reason.
        var (resolver, indexes) = BuildResolver(null, donorFile, donorIsXbox360);
        try
        {
            var records = texture
                ? new RecordCollection { Armor = [new ArmorRecord { FormId = 0x200, IconPath = field }] }
                : new RecordCollection { Sounds = [new SoundRecord { FormId = 0x100, FileName = field }] };

            AssetPathRewriter.ApplyRewrites(records, resolver, NullConversionProgressSink.Instance);

            var value = texture ? records.Armor[0].IconPath : records.Sounds[0].FileName;
            var normalized = AssetPathRules.TryNormalizeRequestPath(value);
            Assert.NotNull(normalized);

            var resolution = resolver.Resolve(normalized!);
            Assert.NotEqual(AssetResolutionKind.Missing, resolution.Kind);

            var predicted = PrototypeAssetConverter.PredictPackedPath(
                normalized!,
                resolution.Source?.NormalizedPath ?? normalized!,
                resolution.Source?.IsXbox360 ?? false);

            Assert.Equal(normalized, predicted);
        }
        finally
        {
            Dispose(indexes);
        }
    }

    [Fact]
    public void TwoRecordsSharingOneAsset_AreBothRewritten()
    {
        // A relative capture and its absolute-dev-path twin normalize to the same request.
        // AssetPathCollector used to source-track a path only the first time it saw it, so
        // the second record was never repointed - 4 endgame tracks per build stayed silent.
        var (resolver, indexes) = BuildResolver(null, "sound" + Sep + "fx" + Sep + "amb" + Sep + "x.xma");
        try
        {
            var records = new RecordCollection
            {
                Sounds =
                [
                    new SoundRecord { FormId = 0x100, FileName = "fx" + Sep + "amb" + Sep + "x.xma" },
                    new SoundRecord { FormId = 0x101, FileName = "fx" + Sep + "amb" + Sep + "x.xma" }
                ]
            };

            var result = AssetPathRewriter.ApplyRewrites(
                records, resolver, NullConversionProgressSink.Instance);

            Assert.Equal(2, result.Rewritten);
            Assert.All(records.Sounds, s => Assert.EndsWith(".wav", s.FileName!, StringComparison.Ordinal));
        }
        finally
        {
            Dispose(indexes);
        }
    }
}