using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace BethesdaMultitool.Core.Assets;

internal enum AssetSelectionStatus { Selected, Missing, Ambiguous, Unavailable }

internal sealed record AssetCandidate(int MountIndex, string SourcePath, string VirtualPath,
    int Occurrence, long? Offset, long Size, string Role, long SourceLength, long SourceWriteTicks,
    ulong? NameHash = null, uint? RawSize = null);

/// <summary>Declared source identity when no physical entry can describe a read attempt.</summary>
internal sealed record AssetMountSnapshot(string SourcePath, AssetMountKind Kind, string Role, string? Origin,
    long? DeclaredSourceLength, long? DeclaredSourceWriteTicks)
{
    public string IdentityScope => Kind == AssetMountKind.Archive
        ? "plan-creation-file-stat" : "declared-directory-path";
}

internal sealed record AssetReadAttempt(AssetCandidate? Candidate, int MountIndex, string Status)
{
    // Candidate-bearing attempts already retain their physical source. Do not repeat every
    // declared mount on every receipt; this snapshot belongs only to candidate-null attempts.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AssetMountSnapshot? DeclaredMount { get; init; }
}

internal sealed record AssetSelectionReceipt(string Policy, string PlanIdentity, string RequestedPath,
    AssetSelectionStatus Status, ImmutableArray<AssetCandidate> Candidates, AssetCandidate? Selected,
    ImmutableArray<AssetReadAttempt> Attempts, string? PayloadSha256 = null, long Sequence = 0)
{
    public string SourceIdentityScope => "file-length-and-last-write-time";
    public string PayloadHashScope => "extracted-bytes-sha256";
    public bool EnginePriorityVerified => false;
}

internal sealed record SelectedAssetRead<T>(AssetSelectionReceipt Receipt, T? Value) where T : class;
