using System.Security.Cryptography;
using System.Text;
using BethesdaMultitool.Core.Assets;
using BethesdaMultitool.Core.Formats.Nif.Materials;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Textures;
using BethesdaMultitool.Tests.Core.Formats.Nif.Materials;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Textures;

public sealed class SelectedAssetMaterialTests
{
    private const string CdbPath = @"materials\materialsbeta.cdb";
    private const string MaterialPath = @"materials\test\orm.mat";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Malformed_first_material_falls_through_with_actual_source_receipt(bool cdb)
    {
        using var files = new Files();
        var path = cdb ? CdbPath : @"materials\test\sample.bgsm";
        files.Write(0, path, [1, 2, 3]);
        var bytes = cdb ? Database("Deferred") : Bgsm();
        var expected = files.Write(1, path, bytes);
        using var source = files.Source();
        INifTextureSource[] sources = [source];

        if (cdb)
            Assert.Equal(StarfieldMaterialShaderRoute.Deferred,
                MaterialTexturePathResolver.ResolveStarfieldShaderRoute(MaterialPath, sources));
        else
            Assert.Equal(@"textures\test\fallback.dds",
                MaterialTexturePathResolver.ResolveMaterial(path, sources)?.Diffuse);

        var receipt = Assert.IsType<AssetSelectionReceipt>(source.Selection.LastReceipt(path));
        Assert.Equal(AssetSelectionStatus.Selected, receipt.Status);
        Assert.Equal(expected, receipt.Selected!.SourcePath);
        Assert.Equal(new[] { "decode-unavailable", "read" }, receipt.Attempts.Select(attempt => attempt.Status));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), receipt.PayloadSha256);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Parsed_cdb_reloads_when_a_higher_source_appears_or_changes(bool existed)
    {
        using var files = new Files();
        if (existed) files.Write(0, CdbPath, Database("Deferred"));
        files.Write(1, CdbPath, Database("Deferred"));
        using var source = files.Source();
        INifTextureSource[] sources = [source];
        Assert.Equal(StarfieldMaterialShaderRoute.Deferred,
            MaterialTexturePathResolver.ResolveStarfieldShaderRoute(MaterialPath, sources));
        var previous = MaterialTexturePathResolver.ResolveStarfieldMaterialDatabaseCacheIdentity(sources);
        var sequence = source.Selection.Sequence;
        using (var reads = source.Selection.CaptureReads())
        {
            Assert.Equal(StarfieldMaterialShaderRoute.Deferred,
                MaterialTexturePathResolver.ResolveStarfieldShaderRoute(MaterialPath, sources));
            Assert.Equal(sequence, source.Selection.Sequence); // Stable parsed data is reused.
            Assert.Equal(source.Selection.LastReceipt(CdbPath), Assert.Single(reads.Receipts));
        }

        var winner = files.Write(0, CdbPath, Database("Water"));
        File.SetLastWriteTimeUtc(winner, DateTime.UtcNow.AddMinutes(1));

        Assert.NotEqual(previous, MaterialTexturePathResolver.ResolveStarfieldMaterialDatabaseCacheIdentity(sources));
        Assert.Equal(StarfieldMaterialShaderRoute.Water,
            MaterialTexturePathResolver.ResolveStarfieldShaderRoute(MaterialPath, sources));
        Assert.Equal(winner, source.Selection.LastReceipt(CdbPath)!.Selected!.SourcePath);
        Assert.True(source.Selection.Sequence > sequence);
    }

    [Fact]
    public void Cdb_dependency_identity_includes_shadowed_candidates()
    {
        using var files = new Files();
        files.Write(0, CdbPath, Database("Deferred"));
        files.Write(1, CdbPath, Database("Deferred"));
        using var source = files.Source();
        INifTextureSource[] sources = [source];
        var before = MaterialTexturePathResolver.ResolveStarfieldMaterialDatabaseCacheIdentity(sources);
        var shadowed = files.Write(1, CdbPath, Database("Water"));
        File.SetLastWriteTimeUtc(shadowed, DateTime.UtcNow.AddMinutes(1));

        Assert.NotEqual(before, MaterialTexturePathResolver.ResolveStarfieldMaterialDatabaseCacheIdentity(sources));
        Assert.Equal(StarfieldMaterialShaderRoute.Deferred,
            MaterialTexturePathResolver.ResolveStarfieldShaderRoute(MaterialPath, sources));
        Assert.Equal(0, source.Selection.LastReceipt(CdbPath)!.Selected!.MountIndex);
    }

    [Fact]
    public void Cdb_retries_a_temporarily_unreadable_higher_candidate_without_stat_change()
    {
        using var files = new Files();
        var first = files.Write(0, CdbPath, Database("Water"));
        files.Write(1, CdbPath, Database("Deferred"));
        using var source = files.Source();
        INifTextureSource[] sources = [source];
        using (var locked = new FileStream(first, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Equal(StarfieldMaterialShaderRoute.Deferred,
                MaterialTexturePathResolver.ResolveStarfieldShaderRoute(MaterialPath, sources));
            Assert.Contains(source.Selection.LastReceipt(CdbPath)!.Attempts,
                attempt => attempt.Status.StartsWith("read-failed:", StringComparison.Ordinal));
        }

        Assert.Equal(StarfieldMaterialShaderRoute.Water,
            MaterialTexturePathResolver.ResolveStarfieldShaderRoute(MaterialPath, sources));
        Assert.Equal(first, source.Selection.LastReceipt(CdbPath)!.Selected!.SourcePath);
    }

    private static byte[] Database(string route) =>
        StarfieldMaterialOrmPolicyTests.BuildDatabase(false, shaderRoute: route);

    private static byte[] Bgsm()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write("BGSM"u8);
        writer.Write(22u);
        writer.Write(new byte[52]);
        var path = Encoding.ASCII.GetBytes(@"textures\test\fallback.dds");
        writer.Write((uint)path.Length + 1);
        writer.Write(path);
        writer.Write((byte)0);
        return stream.ToArray();
    }

    private sealed class Files : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"BmtSelectedMaterials-{Guid.NewGuid():N}");
        private readonly string[] _sources;
        internal Files()
        {
            _sources = [Path.Combine(_root, "first"), Path.Combine(_root, "second")];
            foreach (var path in _sources) Directory.CreateDirectory(path);
        }
        internal string Write(int source, string path, byte[] bytes)
        {
            var full = Path.Combine(_sources[source], path.Replace('\\', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, bytes);
            return full;
        }
        internal SelectedAssetTextureSource Source() => new(AssetSourcePlan.FromPaths(_sources));
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
