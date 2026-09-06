using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BethesdaMultitool.Core.Formats.Bsa.Index;
using BethesdaMultitool.Core.Formats.InstallShield;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.InstallShield;

/// <summary>
///     The InstallShield 5 cabinet reader. The format's tables are far too interlinked to forge a
///     meaningful cabinet in a test, so correctness is established the strong way instead — against
///     Redguard's Disc 1 with the Steam install as the ORACLE (the same game data, shipped loose)
///     — and the synthetic cases here cover only the probe, whose job is to refuse everything that
///     is not a cabinet.
/// </summary>
public sealed class InstallShieldCabinetTests : IDisposable
{
    private readonly List<string> _tempFiles = [];

    private string WriteTemp(byte[] bytes, string extension = ".cab")
    {
        var path = Path.Combine(Path.GetTempPath(), $"installshield-{Guid.NewGuid():N}{extension}");
        File.WriteAllBytes(path, bytes);
        _tempFiles.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var path in _tempFiles)
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // Temp cleanup only.
            }
        }
    }

    [Fact]
    public void Probe_RejectsAnythingWithoutTheSignature()
    {
        Assert.False(InstallShieldCabinet.TryProbe(WriteTemp(new byte[4096])));
        Assert.False(InstallShieldCabinet.TryProbe(WriteTemp(Encoding.ASCII.GetBytes(new string('x', 4096)))));
        Assert.False(InstallShieldCabinet.TryProbe(WriteTemp("OARC"u8.ToArray())));

        // Truncated: the signature alone is not a cabinet.
        var signatureOnly = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(signatureOnly, InstallShieldCabinet.Signature);
        Assert.False(InstallShieldCabinet.TryProbe(WriteTemp(signatureOnly)));
    }

    [Fact]
    public void Probe_SignatureIsTheFourCharacterTag()
    {
        // "ISc(" little-endian — the magic every InstallShield cabinet opens with.
        Assert.Equal("ISc("u8.ToArray(), BitConverter.GetBytes(InstallShieldCabinet.Signature));
    }

    [Fact]
    public void Parse_NonCabinet_ThrowsWithTheFileNamed()
    {
        var path = WriteTemp(new byte[4096]);

        var error = Assert.Throws<InvalidDataException>(() => InstallShieldCabinet.Parse(path));

        Assert.Contains(Path.GetFileName(path), error.Message, StringComparison.Ordinal);
    }
}

/// <summary>
///     Opt-in checks (<c>RUN_BUCKET_B=1</c>) of Redguard Disc 1's <c>DATA1.CAB</c>, staged by
///     extracting the CUE/BIN's ISO tree. The Steam install is the oracle: it ships the same game
///     data loose, so an extracted file that differs by a byte is a decoder bug, not a variant.
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", BucketBTestGuard.Category)]
public sealed class RedguardCabinetRetailTests
{
    /// <summary>Files present both inside the cabinet and in the Steam install, spread across the tree.</summary>
    private static readonly string[] OracleFiles =
    [
        "WORLD.INI", "ENGLISH.RTX", "COMBAT.INI", "ITEM.INI",
        "maps/ISLAND.RGM", "maps/ISLAND.WLD", "3dart/BELLTOWR.ROB", "3dart/TEXTURE.352", "sound/MAIN.SFX",
    ];

    private static string RequireCabinet()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var path = Path.Combine(RepositoryRoot(), "Sample", "Full_Builds", "Redguard_Disc1_iso", "DATA1.CAB");
        Assert.SkipWhen(!File.Exists(path), RealAssetPaths.SkipMessage("Redguard Disc 1 DATA1.CAB"));
        return path;
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "BethesdaMultitool.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("BethesdaMultitool.slnx not found above the test binary.");
    }

    [Fact]
    public void CabinetListsTheWholeInstallIncludingTheThreeDfxArt()
    {
        using var cabinet = ArchiveReader.Open(RequireCabinet());

        Assert.Equal("InstallShield CAB", cabinet.FormatName);
        var entries = cabinet.ListFiles();
        Assert.Equal(1666, entries.Count);

        // The 3dfx art the Steam release omits — the reason this cabinet had to be read at all.
        var texbsi = entries.Where(e => e.FullPath.Contains("fxart", StringComparison.OrdinalIgnoreCase) &&
                                        e.Name.StartsWith("TEXBSI.", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Equal(415, texbsi.Count);

        // And the software tree beside it.
        Assert.Contains(entries, e => e.FullPath.Equals("3dart/TEXTURE.352", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(entries, e => e.Name.Equals("WORLD.INI", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ExtractedFilesAreByteIdenticalToTheSteamInstall()
    {
        var cabinetPath = RequireCabinet();
        var steam = RealAssetPaths.Classics.Redguard();
        Assert.SkipWhen(steam is null, RealAssetPaths.SkipMessage("Redguard"));

        using var cabinet = ArchiveReader.Open(cabinetPath);

        // ⚑ The cabinet lists two paths twice (rg3dfx.pif among them), which is why 1,666 entries
        // extract to 1,664 files — the installer would have written each twice. Group rather than
        // key, so the duplication is a recorded fact instead of an exception.
        var entries = cabinet.ListFiles()
            .GroupBy(e => e.FullPath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        Assert.Equal(2, cabinet.ListFiles().Count - entries.Count);

        var compared = 0;
        foreach (var relative in OracleFiles)
        {
            var loose = Path.Combine(steam!, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(loose) || !entries.TryGetValue(relative, out var entry))
            {
                continue;
            }

            var fromCabinet = cabinet.ReadFile(entry.FullPath);
            Assert.NotNull(fromCabinet);
            var fromDisk = File.ReadAllBytes(loose);

            Assert.Equal(fromDisk.Length, fromCabinet!.Length);
            var difference = fromDisk.AsSpan().CommonPrefixLength(fromCabinet);
            Assert.True(difference == fromDisk.Length, $"{relative} first differs at byte {difference}");
            compared++;
        }

        // ENGLISH.RTX alone is 184 MB, so this is a real end-to-end decompression check.
        Assert.True(compared >= 5, $"only {compared} of the {OracleFiles.Length} oracle files were comparable");
    }
}
