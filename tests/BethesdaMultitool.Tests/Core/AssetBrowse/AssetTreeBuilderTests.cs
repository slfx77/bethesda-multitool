using BethesdaMultitool.Core.AssetBrowse;
using BethesdaMultitool.Core.Vfs;
using Xunit;

namespace BethesdaMultitool.Tests.Core.AssetBrowse;

/// <summary>
///     Pins <see cref="AssetTreeBuilder.Build" />: folder-hierarchy synthesis from virtual paths,
///     folders-first ordinal-ignore-case ordering, the extension classification table,
///     first-entry-wins deduplication, and iterative handling of deep/wide trees.
/// </summary>
public sealed class AssetTreeBuilderTests
{
    [Fact]
    public void Build_EmptyFileSystem_YieldsBareRoot()
    {
        using var fs = new FakeGameFileSystem();

        var root = AssetTreeBuilder.Build(fs, "My Source");

        Assert.Equal("My Source", root.Name);
        Assert.Equal(string.Empty, root.VirtualPath);
        Assert.Equal(AssetNodeKind.Folder, root.Kind);
        Assert.Equal(0L, root.Size);
        Assert.Null(root.Parent);
        Assert.Empty(root.Children);
    }

    [Fact]
    public void Build_DeepPath_CreatesFolderChainWithParentLinks()
    {
        using var fs = new FakeGameFileSystem((@"a\b\c\file.dds", 123));

        var root = AssetTreeBuilder.Build(fs, "root");

        var a = Assert.Single(root.Children);
        Assert.Equal("a", a.Name);
        Assert.Equal("a", a.VirtualPath);
        Assert.Equal(AssetNodeKind.Folder, a.Kind);
        Assert.Same(root, a.Parent);

        var b = Assert.Single(a.Children);
        Assert.Equal("b", b.Name);
        Assert.Equal(@"a\b", b.VirtualPath);
        Assert.Equal(AssetNodeKind.Folder, b.Kind);

        var c = Assert.Single(b.Children);
        Assert.Equal("c", c.Name);
        Assert.Equal(@"a\b\c", c.VirtualPath);

        var leaf = Assert.Single(c.Children);
        Assert.Equal("file.dds", leaf.Name);
        Assert.Equal(@"a\b\c\file.dds", leaf.VirtualPath);
        Assert.Equal(AssetNodeKind.Texture, leaf.Kind);
        Assert.Equal(123L, leaf.Size);
        Assert.Same(c, leaf.Parent);
    }

    [Fact]
    public void Build_OrdersFoldersFirstThenNameIgnoreCase()
    {
        // "GAMMA" vs "beta" discriminates ordinal-ignore-case from case-sensitive ordinal:
        // sensitive ordinal would put 'G' (0x47) before 'b' (0x62).
        using var fs = new FakeGameFileSystem(
            ("GAMMA.txt", 1),
            (@"zulu\x.dds", 1),
            ("beta.txt", 1));

        var root = AssetTreeBuilder.Build(fs, "root");

        Assert.Equal(new[] { "zulu", "beta.txt", "GAMMA.txt" },
            root.Children.Select(n => n.Name).ToArray());
        Assert.Equal(
            new[] { AssetNodeKind.Folder, AssetNodeKind.Text, AssetNodeKind.Text },
            root.Children.Select(n => n.Kind).ToArray());
    }

    [Theory]
    [InlineData("f.dds", AssetNodeKind.Texture)]
    [InlineData("f.ddx", AssetNodeKind.Texture)]
    [InlineData("f.png", AssetNodeKind.Texture)]
    [InlineData("f.tga", AssetNodeKind.Texture)]
    [InlineData("f.nif", AssetNodeKind.Model)]
    [InlineData("f.glb", AssetNodeKind.Model)]
    [InlineData("f.gltf", AssetNodeKind.Model)]
    [InlineData("f.wav", AssetNodeKind.Audio)]
    [InlineData("f.mp3", AssetNodeKind.Audio)]
    [InlineData("f.ogg", AssetNodeKind.Audio)]
    [InlineData("f.xma", AssetNodeKind.Audio)]
    [InlineData("f.voc", AssetNodeKind.Audio)]
    [InlineData("f.acm", AssetNodeKind.Audio)]
    [InlineData("f.bik", AssetNodeKind.Video)]
    [InlineData("f.mve", AssetNodeKind.Video)]
    [InlineData("f.flc", AssetNodeKind.Video)]
    [InlineData("f.vid", AssetNodeKind.Video)]
    [InlineData("f.smk", AssetNodeKind.Video)]
    [InlineData("f.frm", AssetNodeKind.Sprite)]
    [InlineData("f.cif", AssetNodeKind.Sprite)]
    [InlineData("f.cfa", AssetNodeKind.Sprite)]
    [InlineData("f.dfa", AssetNodeKind.Sprite)]
    [InlineData("f.zar", AssetNodeKind.Sprite)]
    [InlineData("f.til", AssetNodeKind.Sprite)]
    [InlineData("f.spr", AssetNodeKind.Sprite)]
    [InlineData("f.rci", AssetNodeKind.Sprite)]
    [InlineData("f.esm", AssetNodeKind.Plugin)]
    [InlineData("f.esp", AssetNodeKind.Plugin)]
    [InlineData("f.fos", AssetNodeKind.Save)]
    [InlineData("f.fxs", AssetNodeKind.Save)]
    [InlineData("f.txt", AssetNodeKind.Text)]
    [InlineData("f.msg", AssetNodeKind.Text)]
    [InlineData("f.ini", AssetNodeKind.Text)]
    [InlineData("f.cfg", AssetNodeKind.Text)]
    [InlineData("f.xml", AssetNodeKind.Text)]
    [InlineData("f.json", AssetNodeKind.Text)]
    [InlineData("f.lst", AssetNodeKind.Text)]
    [InlineData("f.gam", AssetNodeKind.Text)]
    [InlineData("F.NIF", AssetNodeKind.Model)]
    [InlineData("f.bin", AssetNodeKind.Raw)]
    [InlineData("noextension", AssetNodeKind.Raw)]
    [InlineData("trailingdot.", AssetNodeKind.Raw)]
    public void Build_ClassifiesLeafByExtension(string fileName, AssetNodeKind expected)
    {
        using var fs = new FakeGameFileSystem((fileName, 1));

        var root = AssetTreeBuilder.Build(fs, "root");

        var leaf = Assert.Single(root.Children);
        Assert.Equal(fileName, leaf.Name);
        Assert.Equal(expected, leaf.Kind);
    }

    [Fact]
    public void Build_NormalizesForwardSlashSeparators()
    {
        // The fake yields the path exactly as given, so this exercises the builder's own
        // normalization rather than the filesystem's.
        using var fs = new FakeGameFileSystem(("textures/armor/iron.dds", 7));

        var root = AssetTreeBuilder.Build(fs, "root");

        var textures = Assert.Single(root.Children);
        Assert.Equal("textures", textures.Name);
        var armor = Assert.Single(textures.Children);
        Assert.Equal(@"textures\armor", armor.VirtualPath);
        var leaf = Assert.Single(armor.Children);
        Assert.Equal(@"textures\armor\iron.dds", leaf.VirtualPath);
        Assert.Equal(7L, leaf.Size);
    }

    [Fact]
    public void Build_DuplicatePaths_KeepFirstEntry()
    {
        using var fs = new FakeGameFileSystem(("dup.txt", 10), ("DUP.TXT", 20));

        var root = AssetTreeBuilder.Build(fs, "root");

        var leaf = Assert.Single(root.Children);
        Assert.Equal("dup.txt", leaf.Name);
        Assert.Equal(10L, leaf.Size);
    }

    [Fact]
    public void Build_DeepAndWide_StaysIterative()
    {
        // A 2,048-deep chain (an accidentally recursive builder/flood would burn stack here)
        // plus 20,000 leaves across 500 folders for the breadth path.
        var deep = string.Join('\\', Enumerable.Repeat("d", 2048)) + @"\leaf.nif";
        var files = new List<(string Path, long Size)> { (deep, 1) };
        for (var i = 0; i < 20_000; i++)
        {
            files.Add(($@"wide{i % 500}\f{i:D5}.dds", 1));
        }

        using var fs = new FakeGameFileSystem(files.ToArray());
        var root = AssetTreeBuilder.Build(fs, "root");

        Assert.Equal(501, root.Children.Count); // 500 wide folders + the deep chain

        // Walk the deep chain without recursion.
        var node = root.Children.Single(child => child.Name == "d");
        var depth = 1;
        while (node.Children.Count == 1 && node.Children[0].Kind == AssetNodeKind.Folder)
        {
            node = node.Children[0];
            depth++;
        }

        Assert.Equal(2048, depth);
        var leaf = Assert.Single(node.Children);
        Assert.Equal(AssetNodeKind.Model, leaf.Kind);

        // The tristate flood and CheckedFiles walk are iterative too.
        root.IsChecked = true;
        Assert.Equal(20_001, root.CheckedFiles().Count());
    }
}

/// <summary>
///     Minimal in-memory <see cref="IGameFileSystem" /> for tree/session tests: (path, size)
///     entries enumerated in insertion order with paths yielded exactly as given (duplicates
///     preserved, so first-wins deduplication is observable), and payloads synthesized as zero
///     bytes of the declared size.
/// </summary>
internal sealed class FakeGameFileSystem : IGameFileSystem
{
    private readonly Dictionary<string, GameFileEntry> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<GameFileEntry> _entries = [];

    public FakeGameFileSystem(params (string Path, long Size)[] files)
    {
        foreach (var (path, size) in files)
        {
            var entry = new GameFileEntry(path, size, Label);
            _entries.Add(entry);
            _byPath.TryAdd(Normalize(path), entry);
        }
    }

    public int DisposeCount { get; private set; }

    public string Label => "fake";

    public bool Exists(string path)
    {
        return _byPath.ContainsKey(Normalize(path));
    }

    public GameFileEntry? TryStat(string path)
    {
        return _byPath.GetValueOrDefault(Normalize(path));
    }

    public byte[]? TryReadAllBytes(string path)
    {
        return _byPath.TryGetValue(Normalize(path), out var entry) ? new byte[entry.Size] : null;
    }

    public IEnumerable<GameFileEntry> EnumerateFiles(string? prefix = null)
    {
        var normalizedPrefix = prefix is null ? null : Normalize(prefix);
        return _entries.Where(e => string.IsNullOrEmpty(normalizedPrefix)
                                   || Normalize(e.Path).StartsWith(normalizedPrefix, StringComparison.OrdinalIgnoreCase));
    }

    public void Dispose()
    {
        DisposeCount++;
    }

    private static string Normalize(string path)
    {
        return path.Replace('/', '\\').TrimStart('\\');
    }
}
