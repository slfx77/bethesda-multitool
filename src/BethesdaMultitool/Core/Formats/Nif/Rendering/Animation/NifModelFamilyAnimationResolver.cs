using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Inspection;
using BethesdaMultitool.Core.Vfs;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;

/// <summary>The outcome of resolving the external animation family for one NIF model.</summary>
internal enum NifModelFamilyAnimationResolutionStatus
{
    Resolved,
    InvalidModelPath,
    InvalidModel,
    NoSkinBinding,
    NoCompatibleCanonicalSkeleton
}

/// <summary>One canonical external skeleton selected for a skinned NIF.</summary>
internal sealed record NifModelFamilySkeletonAsset(
    string VirtualPath,
    long Size,
    string Source);

/// <summary>One external KF in the selected skeleton's bounded model family.</summary>
internal sealed record NifModelFamilyAnimationAsset(
    string VirtualPath,
    string RelativePath,
    long Size,
    string Source);

/// <summary>
///     Deterministic external-animation catalog for a selected model. The catalog contains paths and
///     provenance only; consumers re-read payloads through the same <see cref="IGameFileSystem" />
///     when they assemble a scene.
/// </summary>
internal sealed record NifModelFamilyAnimationCatalog(
    string ModelPath,
    NifModelFamilyAnimationResolutionStatus Status,
    string? FamilyRoot,
    NifModelFamilySkeletonAsset? Skeleton,
    IReadOnlyList<NifModelFamilyAnimationAsset> Animations,
    bool IsEnumerationTruncated,
    string? Diagnostic);

/// <summary>The skin-node names read from the selected model.</summary>
internal sealed record NifModelFamilyModelRig(IReadOnlyList<string> SkinBoneNames);

/// <summary>All named node occurrences read from a candidate skeleton.</summary>
internal sealed record NifModelFamilySkeletonRig(IReadOnlyList<string> NodeNames);

/// <summary>
///     Narrow NIF-inspection boundary. The production implementation reads real NIF structures; the
///     boundary also lets path/family policy be tested without manufacturing fragile binary NIFs.
/// </summary>
internal interface INifModelFamilyRigInspector
{
    NifModelFamilyModelRig? InspectModel(byte[] data);

    NifModelFamilySkeletonRig? InspectSkeleton(byte[] data);
}

/// <summary>
///     Resolves classic external-skeleton/KF families. Resolution is deliberately conservative:
///     only an ancestor's exact <c>skeleton.nif</c> is eligible, the nearest candidate must contain
///     every model skin-bone name, and a descendant canonical skeleton cuts off its whole
///     subtree from the parent's KF catalog.
/// </summary>
internal static class NifModelFamilyAnimationResolver
{
    // Both limits are correctness boundaries as well as allocation guards. If the entry scan cannot
    // finish, no partial KF list is returned because an unseen descendant skeleton could invalidate
    // an already-seen animation. A completed scan may safely return the first deterministic KF page.
    internal const int MaximumEnumeratedFamilyEntries = 50_000;
    internal const int MaximumAnimationAssets = 10_000;

    private const int MaximumVirtualPathLength = 4096;
    private const int MaximumVirtualPathSegments = 256;
    private const string CanonicalSkeletonFileName = "skeleton.nif";
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;
    private static readonly INifModelFamilyRigInspector DefaultRigInspector = new NifModelFamilyRigInspector();

    internal static NifModelFamilyAnimationCatalog Resolve(
        IGameFileSystem files,
        string modelPath,
        byte[] modelData)
    {
        return Resolve(files, modelPath, modelData, DefaultRigInspector);
    }

    internal static NifModelFamilyAnimationCatalog Resolve(
        IGameFileSystem files,
        string modelPath,
        byte[] modelData,
        INifModelFamilyRigInspector rigInspector)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(modelData);
        ArgumentNullException.ThrowIfNull(rigInspector);

        if (!TryNormalizeVirtualPath(modelPath, out var normalizedModelPath))
        {
            return Empty(
                modelPath ?? string.Empty,
                NifModelFamilyAnimationResolutionStatus.InvalidModelPath,
                "The model path is not a bounded Data-relative virtual path.");
        }

        var modelRig = rigInspector.InspectModel(modelData);
        if (modelRig is null)
        {
            return Empty(
                normalizedModelPath,
                NifModelFamilyAnimationResolutionStatus.InvalidModel,
                "The selected model could not be inspected as a NIF.");
        }

        var requiredBoneNames = modelRig.SkinBoneNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(PathComparer)
            .ToArray();
        if (requiredBoneNames.Length == 0)
        {
            return Empty(
                normalizedModelPath,
                NifModelFamilyAnimationResolutionStatus.NoSkinBinding,
                "The selected model has no named skin bones, so an external rig would be speculative.");
        }

        var modelDirectory = GetDirectory(normalizedModelPath);
        foreach (var candidateDirectory in EnumerateAncestorDirectories(modelDirectory))
        {
            var candidatePath = Combine(candidateDirectory, CanonicalSkeletonFileName);
            var stat = files.TryStat(candidatePath);
            if (stat is null || files.TryReadAllBytes(candidatePath) is not { } candidateData)
            {
                continue;
            }

            var skeletonRig = rigInspector.InspectSkeleton(candidateData);
            if (skeletonRig is null || !IsCompatible(requiredBoneNames, skeletonRig.NodeNames))
            {
                continue;
            }

            var skeleton = new NifModelFamilySkeletonAsset(
                candidatePath,
                stat.Size,
                stat.Source);
            return BuildCatalog(files, normalizedModelPath, candidateDirectory, skeleton);
        }

        return Empty(
            normalizedModelPath,
            NifModelFamilyAnimationResolutionStatus.NoCompatibleCanonicalSkeleton,
            "No ancestor contained an exact canonical skeleton.nif covering every skin-bone name.");
    }

    private static NifModelFamilyAnimationCatalog BuildCatalog(
        IGameFileSystem files,
        string modelPath,
        string familyRoot,
        NifModelFamilySkeletonAsset skeleton)
    {
        var entries = new List<GameFileEntry>();
        var seenPaths = new HashSet<string>(PathComparer);
        var scannedEntryCount = 0;
        var prefix = familyRoot.Length == 0 ? null : familyRoot + "\\";

        try
        {
            foreach (var entry in files.EnumerateFiles(prefix))
            {
                if (scannedEntryCount == MaximumEnumeratedFamilyEntries)
                {
                    // We cannot prove that an unseen entry is not a nested skeleton declaration.
                    return new NifModelFamilyAnimationCatalog(
                        modelPath,
                        NifModelFamilyAnimationResolutionStatus.Resolved,
                        familyRoot,
                        skeleton,
                        [],
                        true,
                        $"Family enumeration exceeded the {MaximumEnumeratedFamilyEntries:N0}-entry safety limit; " +
                        "the KF list was withheld to prevent cross-family leakage.");
                }

                scannedEntryCount++;
                if (!TryNormalizeVirtualPath(entry.Path, out var entryPath) ||
                    !IsStrictDescendant(entryPath, familyRoot) ||
                    !seenPaths.Add(entryPath))
                {
                    continue;
                }

                entries.Add(entry with { Path = entryPath });
            }
        }
        catch (Exception exception) when (IsExpectedFileSystemFailure(exception))
        {
            return new NifModelFamilyAnimationCatalog(
                modelPath,
                NifModelFamilyAnimationResolutionStatus.Resolved,
                familyRoot,
                skeleton,
                [],
                true,
                $"Family enumeration failed ({exception.GetType().Name}); the KF list was withheld.");
        }

        var nestedFamilyRoots = entries
            .Where(entry => IsCanonicalSkeleton(entry.Path))
            .Select(entry => GetDirectory(entry.Path))
            .Where(directory => !PathComparer.Equals(directory, familyRoot))
            .ToHashSet(PathComparer);

        var animations = entries
            .Where(entry => HasExtension(entry.Path, ".kf"))
            .Where(entry => !IsInsideNestedFamily(entry.Path, familyRoot, nestedFamilyRoots))
            .Select(entry => ToAnimationAsset(files, entry, familyRoot))
            .OrderBy(asset => RelativeDirectoryDepth(asset.RelativePath))
            .ThenBy(asset => asset.VirtualPath, PathComparer)
            .ThenBy(asset => asset.VirtualPath, StringComparer.Ordinal)
            .ToArray();

        var animationLimitExceeded = animations.Length > MaximumAnimationAssets;
        IReadOnlyList<NifModelFamilyAnimationAsset> boundedAnimations = animationLimitExceeded
            ? animations[..MaximumAnimationAssets]
            : animations;

        return new NifModelFamilyAnimationCatalog(
            modelPath,
            NifModelFamilyAnimationResolutionStatus.Resolved,
            familyRoot,
            skeleton,
            boundedAnimations,
            animationLimitExceeded,
            animationLimitExceeded
                ? $"The family contains more than {MaximumAnimationAssets:N0} KFs; the deterministic first page was returned."
                : null);
    }

    private static NifModelFamilyAnimationAsset ToAnimationAsset(
        IGameFileSystem files,
        GameFileEntry enumeratedEntry,
        string familyRoot)
    {
        // TryStat is authoritative for a layered VFS and therefore preserves the winning source's
        // provenance even if a custom provider's enumeration metadata is stale.
        var winningEntry = files.TryStat(enumeratedEntry.Path) ?? enumeratedEntry;
        return new NifModelFamilyAnimationAsset(
            enumeratedEntry.Path,
            GetRelativePath(enumeratedEntry.Path, familyRoot),
            winningEntry.Size,
            winningEntry.Source);
    }

    private static bool IsCompatible(
        IReadOnlyList<string> requiredBoneNames,
        IReadOnlyList<string> candidateNodeNames)
    {
        var candidateNames = candidateNodeNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(PathComparer);
        return requiredBoneNames.All(candidateNames.Contains);
    }

    private static bool IsInsideNestedFamily(
        string animationPath,
        string familyRoot,
        HashSet<string> nestedFamilyRoots)
    {
        var directory = GetDirectory(animationPath);
        while (directory.Length > familyRoot.Length)
        {
            if (nestedFamilyRoots.Contains(directory))
            {
                return true;
            }

            directory = GetDirectory(directory);
        }

        return false;
    }

    private static IEnumerable<string> EnumerateAncestorDirectories(string directory)
    {
        while (true)
        {
            yield return directory;
            if (directory.Length == 0)
            {
                yield break;
            }

            directory = GetDirectory(directory);
        }
    }

    private static NifModelFamilyAnimationCatalog Empty(
        string modelPath,
        NifModelFamilyAnimationResolutionStatus status,
        string diagnostic)
    {
        return new NifModelFamilyAnimationCatalog(
            modelPath,
            status,
            null,
            null,
            [],
            false,
            diagnostic);
    }

    private static bool TryNormalizeVirtualPath(string? path, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(path) || path.Length > MaximumVirtualPathLength)
        {
            return false;
        }

        var segments = path.Replace('/', '\\')
            .Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length is 0 or > MaximumVirtualPathSegments ||
            segments.Any(segment => segment is "." or ".." || segment.Contains(':') || segment.Contains('\0')))
        {
            return false;
        }

        normalized = string.Join('\\', segments);
        return normalized.Length <= MaximumVirtualPathLength;
    }

    private static string GetDirectory(string path)
    {
        var separator = path.LastIndexOf('\\');
        return separator < 0 ? string.Empty : path[..separator];
    }

    private static string Combine(string directory, string fileName)
    {
        return directory.Length == 0 ? fileName : directory + "\\" + fileName;
    }

    private static string GetRelativePath(string path, string familyRoot)
    {
        return familyRoot.Length == 0 ? path : path[(familyRoot.Length + 1)..];
    }

    private static bool IsStrictDescendant(string path, string directory)
    {
        return directory.Length == 0 ||
               path.Length > directory.Length &&
               path.StartsWith(directory, StringComparison.OrdinalIgnoreCase) &&
               path[directory.Length] == '\\';
    }

    private static bool IsCanonicalSkeleton(string path)
    {
        var fileNameStart = path.LastIndexOf('\\') + 1;
        return path.AsSpan(fileNameStart).Equals(CanonicalSkeletonFileName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasExtension(string path, string extension)
    {
        return path.EndsWith(extension, StringComparison.OrdinalIgnoreCase);
    }

    private static int RelativeDirectoryDepth(string relativePath)
    {
        return relativePath.Count(character => character == '\\');
    }

    private static bool IsExpectedFileSystemFailure(Exception exception)
    {
        return exception is IOException or UnauthorizedAccessException or InvalidDataException;
    }
}

/// <summary>Reads legacy NiSkinInstance bindings and named skeleton nodes from parsed NIF blocks.</summary>
internal sealed class NifModelFamilyRigInspector : INifModelFamilyRigInspector
{
    public NifModelFamilyModelRig? InspectModel(byte[] data)
    {
        try
        {
            var nif = NifParser.Parse(data);
            if (nif is null || nif.Blocks.Count == 0)
            {
                return null;
            }

            var boneNames = new List<string>();
            var visitedSkinInstances = new HashSet<int>();
            foreach (var shape in nif.Blocks)
            {
                if (!NifSceneGraphWalker.ShapeTypes.Contains(shape.TypeName) ||
                    NifSceneGraphWalker.SelfContainedShapeTypes.Contains(shape.TypeName))
                {
                    continue;
                }

                var skinRef = NifBlockParsers.ParseShapeSkinInstanceRef(
                    data,
                    shape,
                    nif.BsVersion,
                    nif.BinaryVersion,
                    nif.IsBigEndian,
                    nif.HasInlineStrings);
                if (skinRef < 0 || skinRef >= nif.Blocks.Count || !visitedSkinInstances.Add(skinRef) ||
                    nif.Blocks[skinRef].TypeName is not ("NiSkinInstance" or "BSDismemberSkinInstance"))
                {
                    continue;
                }

                var skin = NifSkinningExtractor.ParseNiSkinInstance(
                    data,
                    nif.Blocks[skinRef],
                    nif.IsBigEndian,
                    nif.BinaryVersion);
                if (skin is null)
                {
                    continue;
                }

                foreach (var boneRef in skin.BoneRefs)
                {
                    if (boneRef >= 0 && boneRef < nif.Blocks.Count &&
                        NifBlockParsers.ReadBlockName(data, nif.Blocks[boneRef], nif) is { Length: > 0 } boneName)
                    {
                        boneNames.Add(boneName);
                    }
                }
            }

            return new NifModelFamilyModelRig(boneNames);
        }
        catch (Exception exception) when (IsExpectedMalformedNifFailure(exception))
        {
            return null;
        }
    }

    public NifModelFamilySkeletonRig? InspectSkeleton(byte[] data)
    {
        try
        {
            var nif = NifParser.Parse(data);
            if (nif is null || nif.Blocks.Count == 0)
            {
                return null;
            }

            var nodeNames = new List<string>();
            foreach (var node in nif.Blocks.Where(block => NifSceneGraphWalker.NodeTypes.Contains(block.TypeName)))
            {
                if (NifBlockParsers.ReadBlockName(data, node, nif) is { Length: > 0 } nodeName)
                {
                    nodeNames.Add(nodeName);
                }
            }

            return new NifModelFamilySkeletonRig(nodeNames);
        }
        catch (Exception exception) when (IsExpectedMalformedNifFailure(exception))
        {
            return null;
        }
    }

    private static bool IsExpectedMalformedNifFailure(Exception exception)
    {
        return exception is InvalidDataException or ArgumentException or OverflowException or IndexOutOfRangeException;
    }
}
