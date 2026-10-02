namespace BethesdaMultitool.Core.Formats.Esm.Plugin.AssetPacking;

/// <summary>Why a path was requested; this does not establish a runtime requirement.</summary>
public sealed record AssetRequestEvidence(
    string Basis,
    uint? OwnerFormId = null,
    string? OwnerType = null,
    string? Field = null,
    string? ParentPath = null,
    string? ParentBasis = null,
    uint? SourceOwnerFormId = null,
    string? SourcePath = null,
    string? SelectedParentPath = null);

public sealed record AssetRequest(string Path, IReadOnlyList<AssetRequestEvidence> Evidence);

/// <summary>A physical alternative retained for review, never selected as a morph donor.</summary>
public sealed record AssetUnverifiedCandidate(string Path, int SourceFolderIndex, string Reason);

/// <summary>Collected sequentially, then read without mutation during parallel packing.</summary>
internal sealed class AssetRequestCatalog
{
    private readonly Dictionary<string, HashSet<AssetRequestEvidence>> _evidence =
        new(StringComparer.OrdinalIgnoreCase);

    public void Ensure(string path) => _evidence.TryAdd(path, []);

    public void Add(string path, AssetRequestEvidence evidence)
    {
        if (!_evidence.TryGetValue(path, out var reasons))
        {
            reasons = [];
            _evidence.Add(path, reasons);
        }

        reasons.Add(evidence);
    }

    public IReadOnlyList<AssetRequestEvidence> Get(string path)
    {
        return _evidence.TryGetValue(path, out var reasons)
            ? Array.AsReadOnly(reasons.OrderBy(r => r.Basis, StringComparer.Ordinal)
                .ThenBy(r => r.OwnerFormId).ThenBy(r => r.OwnerType, StringComparer.Ordinal)
                .ThenBy(r => r.Field, StringComparer.Ordinal).ThenBy(r => r.ParentPath, StringComparer.Ordinal)
                .ThenBy(r => r.ParentBasis, StringComparer.Ordinal).ThenBy(r => r.SourceOwnerFormId)
                .ThenBy(r => r.SourcePath, StringComparer.Ordinal)
                .ThenBy(r => r.SelectedParentPath, StringComparer.Ordinal).ToArray())
            : Array.Empty<AssetRequestEvidence>();
    }

    public IReadOnlyList<AssetRequest> Snapshot() => Array.AsReadOnly(_evidence
        .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
        .Select(pair => new AssetRequest(pair.Key, Get(pair.Key))).ToArray());

    public AssetResolution Annotate(AssetResolution resolution) => resolution with
    {
        RequestEvidence = Get(resolution.RequestedPath)
    };

    public static bool IsDerivedOnly(IReadOnlyList<AssetRequestEvidence> reasons) =>
        reasons.Count > 0 && reasons.All(r => r.Basis is "morph-companion-candidate" or "npc-prebake-candidate");

    public DataFolderResolution RequireExactMorphParent(string path, DataFolderResolution resolution,
        DataFolderResolver resolver)
    {
        var reasons = Get(path);
        // A stronger direct/string reason is preserved. Derived morphs are useful only
        // with their literal parent, not a different NIF chosen by fuzzy resolution.
        if (reasons.Count == 0 || reasons.Any(r => r.Basis != "morph-companion-candidate"))
            return resolution;

        if (reasons.All(r => r.ParentPath is { } parent &&
            resolver.Resolve(parent) is { Kind: AssetResolutionKind.AlreadyInBaseline or AssetResolutionKind.ResolvedExact,
                ResolvedPath: var selected } && string.Equals(parent, selected, StringComparison.OrdinalIgnoreCase)))
            return resolution;

        return resolution with
        {
            Kind = AssetResolutionKind.Missing,
            Source = null,
            UnresolvedReason = "derived-morph-parent-not-exact"
        };
    }
}
