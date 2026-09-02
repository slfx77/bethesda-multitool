using BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;
using BethesdaMultitool.Core.Vfs;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Animation;

public sealed class NifModelFamilyAnimationResolverTests
{
    [Fact]
    public void Resolve_SelectsNearestCompatibleCanonicalSkeleton_AndBoundsItsFamily()
    {
        using var files = new ResolverFileSystem { HonorRequestedPrefix = false };
        files.AddFile(@"meshes\characters\_male\armor\skeleton.nif", [2], 20, "armor.bsa");
        files.AddFile(@"meshes\characters\_male\armor\skeletonbeast.nif", [3], 30, "armor.bsa");
        files.AddFile(@"meshes\characters\_male\skeleton.nif", [3], 300, "actors.bsa");
        files.AddFile(@"meshes\characters\skeleton.nif", [4], 400, "fallback.bsa");

        files.AddMetadata(@"meshes\characters\_male\z_idle.kf", 90, "actors.bsa");
        files.AddMetadata(@"meshes\characters\_male\A_walk.KF", 10, "loose-data");
        files.AddMetadata(@"meshes\characters\_male\a_WALK.kf", 999, "shadowed.bsa");
        files.AddMetadata(@"meshes\characters\_male\locomotion\run.kf", 30, "actors.bsa");
        files.AddMetadata(@"meshes\characters\_male\variant\skeleton.nif", 1, "actors.bsa");
        files.AddMetadata(@"meshes\characters\_male\variant\idle.kf", 1, "actors.bsa");
        files.AddMetadata(@"meshes\characters\_male\variant\deep\attack.kf", 1, "actors.bsa");
        files.AddMetadata(@"meshes\characters\_male\beastly\skeletonbeast.nif", 1, "actors.bsa");
        files.AddMetadata(@"meshes\characters\_male\beastly\idle.kf", 40, "actors.bsa");
        files.AddMetadata(@"meshes\characters\_maleExtra\leak.kf", 1, "actors.bsa");

        var inspector = new ResolverRigInspector()
            .AddModel(1, "Bip01", "Bip01 Spine", "bip01")
            // The nearest skeleton is genuinely incomplete, so resolution continues rootward.
            .AddSkeleton(2, "Bip01")
            .AddSkeleton(3, "Bip01", "Bip01 Spine", "Unused")
            .AddSkeleton(4, "Bip01", "Bip01 Spine");

        var catalog = NifModelFamilyAnimationResolver.Resolve(
            files,
            @"/meshes/characters/_male/armor/body.nif",
            [1],
            inspector);

        Assert.Equal(NifModelFamilyAnimationResolutionStatus.Resolved, catalog.Status);
        Assert.Equal(@"meshes\characters\_male", catalog.FamilyRoot);
        Assert.NotNull(catalog.Skeleton);
        Assert.Equal(@"meshes\characters\_male\skeleton.nif", catalog.Skeleton.VirtualPath);
        Assert.Equal(300, catalog.Skeleton.Size);
        Assert.Equal("actors.bsa", catalog.Skeleton.Source);
        Assert.False(catalog.IsEnumerationTruncated);
        Assert.Null(catalog.Diagnostic);

        Assert.Equal(
            [
                @"meshes\characters\_male\A_walk.KF",
                @"meshes\characters\_male\z_idle.kf",
                @"meshes\characters\_male\beastly\idle.kf",
                @"meshes\characters\_male\locomotion\run.kf"
            ],
            catalog.Animations.Select(asset => asset.VirtualPath));
        Assert.Equal(
            [@"A_walk.KF", @"z_idle.kf", @"beastly\idle.kf", @"locomotion\run.kf"],
            catalog.Animations.Select(asset => asset.RelativePath));

        var walk = catalog.Animations[0];
        Assert.Equal(10, walk.Size);
        Assert.Equal("loose-data", walk.Source);
        Assert.Equal(
            [
                @"meshes\characters\_male\armor\skeleton.nif",
                @"meshes\characters\_male\skeleton.nif"
            ],
            files.ReadPaths);
        Assert.DoesNotContain(
            catalog.Animations,
            animation => animation.VirtualPath.Contains(@"\variant\", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            catalog.Animations,
            animation => animation.VirtualPath.Contains("_maleExtra", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Resolve_TreatsSkeletonNodeNamesAsCoverageSet()
    {
        using var files = new ResolverFileSystem();
        files.AddFile(@"meshes\characters\_male\skeleton.nif", [2], 1, "actors.bsa");
        var inspector = new ResolverRigInspector()
            .AddModel(1, "Bip01", "Bip01 Spine")
            .AddSkeleton(2, "Bip01", "Bip01 Spine", "BIP01 SPINE");

        var catalog = NifModelFamilyAnimationResolver.Resolve(
            files,
            @"meshes\characters\_male\upperbody.nif",
            [1],
            inspector);

        Assert.Equal(NifModelFamilyAnimationResolutionStatus.Resolved, catalog.Status);
        Assert.Equal(@"meshes\characters\_male\skeleton.nif", catalog.Skeleton?.VirtualPath);
    }

    [Fact]
    public void Resolve_DoesNotUseNamedSkeletonVariants()
    {
        using var files = new ResolverFileSystem();
        files.AddFile(@"meshes\characters\_male\skeletonbeast.nif", [2], 1, "actors.bsa");
        var inspector = new ResolverRigInspector()
            .AddModel(1, "Bip01")
            .AddSkeleton(2, "Bip01");

        var catalog = NifModelFamilyAnimationResolver.Resolve(
            files,
            @"meshes\characters\_male\upperbody.nif",
            [1],
            inspector);

        Assert.Equal(NifModelFamilyAnimationResolutionStatus.NoCompatibleCanonicalSkeleton, catalog.Status);
        Assert.Null(catalog.Skeleton);
        Assert.Empty(files.ReadPaths);
        Assert.Equal(0, files.EnumerationCount);
    }

    [Fact]
    public void Resolve_UnskinnedModel_DoesNotSearchOrEnumerateTheVfs()
    {
        using var files = new ResolverFileSystem();
        files.AddFile(@"meshes\creatures\bear\skeleton.nif", [2], 1, "actors.bsa");
        var inspector = new ResolverRigInspector().AddModel(1);

        var catalog = NifModelFamilyAnimationResolver.Resolve(
            files,
            @"meshes\creatures\bear\claw.nif",
            [1],
            inspector);

        Assert.Equal(NifModelFamilyAnimationResolutionStatus.NoSkinBinding, catalog.Status);
        Assert.Null(catalog.FamilyRoot);
        Assert.Empty(catalog.Animations);
        Assert.Empty(files.ReadPaths);
        Assert.Equal(0, files.EnumerationCount);
    }

    [Fact]
    public void Resolve_InvalidModel_DoesNotSearchOrEnumerateTheVfs()
    {
        using var files = new ResolverFileSystem();
        files.AddFile(@"meshes\creatures\bear\skeleton.nif", [2], 1, "actors.bsa");
        var inspector = new ResolverRigInspector();

        var catalog = NifModelFamilyAnimationResolver.Resolve(
            files,
            @"meshes\creatures\bear\body.nif",
            [1],
            inspector);

        Assert.Equal(NifModelFamilyAnimationResolutionStatus.InvalidModel, catalog.Status);
        Assert.Null(catalog.Skeleton);
        Assert.Empty(files.ReadPaths);
        Assert.Equal(0, files.EnumerationCount);
    }

    [Theory]
    [InlineData(@"..\meshes\body.nif")]
    [InlineData(@"C:\Games\body.nif")]
    [InlineData(@"meshes\.\body.nif")]
    [InlineData("")]
    public void Resolve_RejectsNonVirtualModelPaths(string path)
    {
        using var files = new ResolverFileSystem();
        var inspector = new ResolverRigInspector().AddModel(1, "Bip01");

        var catalog = NifModelFamilyAnimationResolver.Resolve(files, path, [1], inspector);

        Assert.Equal(NifModelFamilyAnimationResolutionStatus.InvalidModelPath, catalog.Status);
        Assert.Empty(files.ReadPaths);
        Assert.Equal(0, files.EnumerationCount);
    }

    [Fact]
    public void Resolve_WithholdsKfsWhenEntryLimitPreventsNestedFamilyProof()
    {
        using var files = new ResolverFileSystem();
        files.AddFile(@"meshes\creatures\bear\skeleton.nif", [2], 1, "actors.bsa");
        files.EnumerationOverride = _ => Enumerable.Repeat(
            new GameFileEntry(@"meshes\creatures\bear\idle.kf", 1, "actors.bsa"),
            NifModelFamilyAnimationResolver.MaximumEnumeratedFamilyEntries + 1);
        var inspector = new ResolverRigInspector()
            .AddModel(1, "Bip01")
            .AddSkeleton(2, "Bip01");

        var catalog = NifModelFamilyAnimationResolver.Resolve(
            files,
            @"meshes\creatures\bear\body.nif",
            [1],
            inspector);

        Assert.Equal(NifModelFamilyAnimationResolutionStatus.Resolved, catalog.Status);
        Assert.True(catalog.IsEnumerationTruncated);
        Assert.Empty(catalog.Animations);
        Assert.Contains("withheld", catalog.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class ResolverRigInspector : INifModelFamilyRigInspector
    {
        private readonly Dictionary<byte, NifModelFamilyModelRig> _models = [];
        private readonly Dictionary<byte, NifModelFamilySkeletonRig> _skeletons = [];

        internal ResolverRigInspector AddModel(byte marker, params string[] boneNames)
        {
            _models[marker] = new NifModelFamilyModelRig(boneNames);
            return this;
        }

        internal ResolverRigInspector AddSkeleton(byte marker, params string[] nodeNames)
        {
            _skeletons[marker] = new NifModelFamilySkeletonRig(nodeNames);
            return this;
        }

        public NifModelFamilyModelRig? InspectModel(byte[] data)
        {
            return data.Length > 0 && _models.TryGetValue(data[0], out var rig) ? rig : null;
        }

        public NifModelFamilySkeletonRig? InspectSkeleton(byte[] data)
        {
            return data.Length > 0 && _skeletons.TryGetValue(data[0], out var rig) ? rig : null;
        }
    }

    private sealed class ResolverFileSystem : IGameFileSystem
    {
        private readonly List<(GameFileEntry Entry, byte[]? Data)> _files = [];

        internal bool HonorRequestedPrefix { get; init; } = true;

        internal Func<string?, IEnumerable<GameFileEntry>>? EnumerationOverride { get; set; }

        internal List<string> ReadPaths { get; } = [];

        internal int EnumerationCount { get; private set; }

        public string Label => "resolver-test-vfs";

        internal void AddFile(string path, byte[] data, long size, string source)
        {
            _files.Add((new GameFileEntry(Normalize(path), size, source), data));
        }

        internal void AddMetadata(string path, long size, string source)
        {
            _files.Add((new GameFileEntry(Normalize(path), size, source), null));
        }

        public bool Exists(string path)
        {
            return TryStat(path) is not null;
        }

        public GameFileEntry? TryStat(string path)
        {
            var normalized = Normalize(path);
            return _files.FirstOrDefault(file =>
                string.Equals(file.Entry.Path, normalized, StringComparison.OrdinalIgnoreCase)).Entry;
        }

        public byte[]? TryReadAllBytes(string path)
        {
            var normalized = Normalize(path);
            ReadPaths.Add(normalized);
            return _files.FirstOrDefault(file =>
                    string.Equals(file.Entry.Path, normalized, StringComparison.OrdinalIgnoreCase))
                .Data;
        }

        public IEnumerable<GameFileEntry> EnumerateFiles(string? prefix = null)
        {
            EnumerationCount++;
            if (EnumerationOverride is not null)
            {
                return EnumerationOverride(prefix);
            }

            var normalizedPrefix = prefix is null ? null : Normalize(prefix);
            return _files
                .Select(file => file.Entry)
                .Where(entry => !HonorRequestedPrefix ||
                                string.IsNullOrEmpty(normalizedPrefix) ||
                                entry.Path.StartsWith(normalizedPrefix, StringComparison.OrdinalIgnoreCase));
        }

        public void Dispose()
        {
        }

        private static string Normalize(string path)
        {
            return path.Replace('/', '\\').TrimStart('\\');
        }
    }
}
