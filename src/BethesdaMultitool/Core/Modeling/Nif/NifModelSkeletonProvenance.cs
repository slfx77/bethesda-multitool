using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     Cut-1b slice 3: the provenance of a <c>.kf</c>'s resolved skeleton, for the document native-state row
///     <c>bmt.nif.animation.skeleton</c> (plan sections 1.7 and 2.2): the chosen path and occurrence, the SHA-256 of the
///     skeleton bytes, the rule name, every candidate examined, and the target names that bound and did not bind (with
///     their typed reasons).
/// </summary>
/// <remarks>
///     Names are listed once each, by exact bytes, in the order the caller's matches first give them; the same name
///     always binds the same way in one map, so nothing is lost. Since slice 10 the <c>.kf</c> path
///     (<see cref="NifModelAnimationStreamReader" />) emits the row with every controlled block's target name.
/// </remarks>
internal sealed class NifModelSkeletonProvenance
{
    /// <summary>The native-state row kind.</summary>
    public const string Kind = "bmt.nif.animation.skeleton";

    /// <summary>The payload schema version.</summary>
    public const int PayloadVersion = 1;

    private NifModelSkeletonProvenance(
        NifModelSkeletonResolution resolution,
        string sha256,
        IReadOnlyList<ReadOnlyMemory<byte>> matchedNames,
        IReadOnlyList<NifModelTargetMatch> unmatched)
    {
        Resolution = resolution;
        Sha256 = sha256;
        MatchedNames = matchedNames;
        Unmatched = unmatched;
    }

    /// <summary>The resolution that chose the skeleton.</summary>
    public NifModelSkeletonResolution Resolution { get; }

    /// <summary>The candidate path that chose the skeleton.</summary>
    public string Path => Resolution.SkeletonPath!;

    /// <summary>The lowercase SHA-256 of the skeleton bytes.</summary>
    public string Sha256 { get; }

    /// <summary>The recorded rule name (<see cref="NifModelSkeletonResolver.RuleName" />).</summary>
    public string RuleName => Resolution.RuleName;

    /// <summary>Every candidate examined, in order.</summary>
    public IReadOnlyList<NifModelSkeletonCandidate> Candidates => Resolution.Candidates;

    /// <summary>The distinct target names that bound, as stored bytes, in first-seen order.</summary>
    public IReadOnlyList<ReadOnlyMemory<byte>> MatchedNames { get; }

    /// <summary>The distinct target names that did not bind, each with its typed reason, in first-seen order.</summary>
    public IReadOnlyList<NifModelTargetMatch> Unmatched { get; }

    /// <summary>Records a resolved skeleton, hashing its bytes.</summary>
    /// <param name="resolution">A resolution with a chosen skeleton.</param>
    /// <param name="skeletonBytes">The skeleton's bytes exactly as read.</param>
    /// <param name="matches">The target matches made against the skeleton (duplicates allowed).</param>
    /// <returns>The provenance.</returns>
    /// <exception cref="ArgumentException">The resolution chose no skeleton.</exception>
    public static NifModelSkeletonProvenance FromBytes(
        NifModelSkeletonResolution resolution,
        ReadOnlySpan<byte> skeletonBytes,
        IEnumerable<NifModelTargetMatch> matches)
    {
        return FromDigest(resolution, Convert.ToHexStringLower(SHA256.HashData(skeletonBytes)), matches);
    }

    /// <summary>
    ///     Records a resolved skeleton whose digest the caller already has (the read cache hashes every companion it reads,
    ///     <c>NifCompanionRead.Sha256</c>).
    /// </summary>
    /// <param name="resolution">A resolution with a chosen skeleton.</param>
    /// <param name="sha256">The skeleton's lowercase hexadecimal SHA-256.</param>
    /// <param name="matches">The target matches made against the skeleton (duplicates allowed).</param>
    /// <returns>The provenance.</returns>
    /// <exception cref="ArgumentException">The resolution chose no skeleton, or the digest is not 64 lowercase hex digits.</exception>
    public static NifModelSkeletonProvenance FromDigest(
        NifModelSkeletonResolution resolution,
        string sha256,
        IEnumerable<NifModelTargetMatch> matches)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        ArgumentNullException.ThrowIfNull(sha256);
        ArgumentNullException.ThrowIfNull(matches);
        if (!resolution.IsResolved)
        {
            throw new ArgumentException(
                $"Only a resolved skeleton has provenance; this resolution is {resolution.Status}.", nameof(resolution));
        }

        if (sha256.Length != 64 || !sha256.All(static c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f')))
        {
            throw new ArgumentException("The SHA-256 must be 64 lowercase hexadecimal digits.", nameof(sha256));
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var matched = new List<ReadOnlyMemory<byte>>();
        var unmatched = new List<NifModelTargetMatch>();
        foreach (var match in matches)
        {
            ArgumentNullException.ThrowIfNull(match, nameof(matches));
            if (!seen.Add(Encoding.Latin1.GetString(match.RawName.Span)))
            {
                continue;
            }

            if (match.IsResolved)
            {
                matched.Add(match.RawName);
            }
            else
            {
                unmatched.Add(match);
            }
        }

        return new NifModelSkeletonProvenance(resolution, sha256, matched.AsReadOnly(), unmatched.AsReadOnly());
    }

    /// <summary>
    ///     The row payload: rule, paths, the chosen occurrence's source identity, digest, candidates, and the matched and
    ///     unmatched names (Latin-1 text plus raw hex for bytes at or above 0x80, <see cref="NifModelNativeValues.Text" />).
    /// </summary>
    /// <returns>A new payload object.</returns>
    public JsonObject ToPayload()
    {
        var skeleton = Resolution.Skeleton!;
        var candidates = new JsonArray();
        foreach (var candidate in Candidates)
        {
            candidates.Add(new JsonObject
            {
                ["path"] = candidate.Path,
                ["outcome"] = OutcomeCode(candidate.Outcome),
                ["occurrences"] = candidate.OccurrenceCount
            });
        }

        var matched = new JsonArray();
        foreach (var name in MatchedNames)
        {
            matched.Add(NifModelNativeValues.Text(name.Span));
        }

        var unmatched = new JsonArray();
        foreach (var match in Unmatched)
        {
            unmatched.Add(new JsonObject
            {
                ["name"] = NifModelNativeValues.Text(match.RawName.Span),
                ["code"] = NifModelTargetNames.Code(match.Block),
                ["reason"] = NifModelTargetNames.Reason(match.Block)
            });
        }

        return new JsonObject
        {
            ["rule"] = RuleName,
            ["animationPath"] = Resolution.AnimationPath,
            ["explicitPath"] = Resolution.ExplicitPath,
            ["path"] = Path,
            ["sourceId"] = skeleton.Reference.SourceId,
            ["referencePath"] = skeleton.Reference.Path,
            ["occurrenceId"] = skeleton.Reference.OccurrenceId,
            ["provenance"] = skeleton.Entry.Provenance,
            ["sha256"] = Sha256,
            ["candidates"] = candidates,
            ["matched"] = matched,
            ["unmatched"] = unmatched
        };
    }

    /// <summary>The document-level native-state row (target Document, kind <see cref="Kind" />).</summary>
    /// <returns>The row.</returns>
    public SceneNativeState ToNativeState()
    {
        return new SceneNativeState(new SceneElementRef(SceneElementKind.Document), Kind, PayloadVersion,
            ToPayload().ToJsonString());
    }

    private static string OutcomeCode(NifModelSkeletonCandidateOutcome outcome)
    {
        return outcome switch
        {
            NifModelSkeletonCandidateOutcome.Found => "found",
            NifModelSkeletonCandidateOutcome.Missing => "missing",
            NifModelSkeletonCandidateOutcome.Ambiguous => "ambiguous",
            _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Unknown candidate outcome.")
        };
    }
}
