using BethesdaMultitool.Core.Formats.Travels.OblivionPsp;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Travels;

/// <summary>
///     Real-asset checks for the Oblivion PSP descriptor-to-payload join, over the seven staged
///     <c>GR.ARC</c> builds. Opt-in: set <c>RUN_BUCKET_B=1</c>.
///     <para>
///         ⚑ The rule is that a <c>0x0716</c> payload begins at <c>headerLength + 8</c>. What makes
///         it a rule rather than a heuristic is not that it usually works — it is that it works on
///         EXACTLY the right subset. The three resource types whose payload is a RenderWare stream
///         (<c>rwID_CLUMP</c>, <c>rwID_TEXDICTIONARY</c>, <c>rwID_WORLD</c>) resolve to a
///         well-formed chunk every time, and the several thousand resources of other types
///         (animations, audio, conversations, quests, navmesh) resolve to one never. A rule that
///         fired on 90% of everything would be a guess; one that partitions the population cleanly
///         is a decode.
///     </para>
///     <para>
///         ⚠ An earlier round of this investigation concluded there was no <c>0x050E</c> chunk in
///         the pack "at any depth". That was wrong, and it was wrong because the walk had never
///         reached the depth those chunks live at — this join is what reaches it. The lesson is
///         pinned here rather than only in a commit message: an absence is only as strong as the
///         search that looked for it.
///     </para>
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", BucketBTestGuard.Category)]
public sealed class OblivionPspResourceRetailTests
{
    /// <summary>The resource types whose payload is a RenderWare chunk stream.</summary>
    private static readonly string[] RenderWareTypes = ["rwID_CLUMP", "rwID_TEXDICTIONARY", "rwID_WORLD"];

    private static string[] RequireBuilds()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var builds = RealAssetPaths.Travels.OblivionPspBuilds();
        Assert.SkipWhen(builds.Count == 0, RealAssetPaths.SkipMessage("Oblivion PSP (cancelled betas)"));

        var packs = builds.SelectMany(build => Directory.EnumerateFiles(build, "GR.ARC", SearchOption.AllDirectories))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Assert.SkipWhen(packs.Length == 0, "No GR.ARC packs are staged.");
        return packs;
    }

    /// <summary>Every named resource across every entry of one pack.</summary>
    private static List<OblivionPspResource> Resources(string pack)
    {
        var bytes = File.ReadAllBytes(pack);
        var archive = OblivionPspArchive.Parse(pack);
        var all = new List<OblivionPspResource>();
        foreach (var entry in archive.Entries)
        {
            if (entry.Size <= 0 || entry.Offset + entry.Size > bytes.Length)
            {
                continue;
            }

            all.AddRange(OblivionPspResourceReader.ReadResources(
                bytes.AsSpan((int)entry.Offset, (int)entry.Size)));
        }

        return all;
    }

    /// <summary>
    ///     ⚑ The partition. Every resolved payload is one of the three RenderWare types, and every
    ///     resource of those three types resolves. Neither direction alone would be convincing:
    ///     the first without the second would mean the rule is merely conservative, and the second
    ///     without the first would mean it is merely permissive.
    /// </summary>
    [Fact]
    public void TheJoinResolvesExactlyTheRenderWareTypedResources()
    {
        var packs = RequireBuilds();

        var resolvedTypes = new HashSet<string>(StringComparer.Ordinal);
        var unresolvedTypes = new HashSet<string>(StringComparer.Ordinal);
        var streams = 0;
        var total = 0;

        foreach (var pack in packs)
        {
            foreach (var resource in Resources(pack))
            {
                total++;
                if (resource.IsRenderWareStream)
                {
                    streams++;
                    resolvedTypes.Add(resource.TypeName);
                }
                else if (resource.TypeName.Length > 0)
                {
                    unresolvedTypes.Add(resource.TypeName);
                }
            }
        }

        // 7,707 across the six dated betas (measured 2026-09-09; see above for the old floor).
        Assert.True(total > 7_500, $"Only {total} named resources were found across the builds.");
        // 1,995 across the six dated betas (measured 2026-09-09; the floor was 2,000 while the
        // corpus also held the community repack).
        Assert.True(streams > 1_900, $"Only {streams} payloads resolved to a RenderWare stream.");

        // Forward: nothing outside the three types ever resolves.
        Assert.Equal(RenderWareTypes.OrderBy(t => t, StringComparer.Ordinal),
            resolvedTypes.Order(StringComparer.Ordinal));

        // Reverse: none of the three types ever fails to resolve.
        Assert.Empty(unresolvedTypes.Intersect(RenderWareTypes, StringComparer.Ordinal));
    }

    /// <summary>
    ///     Every resolved payload carries the same RenderWare library id. The plan flagged that the
    ///     sibling tool's version check compares the raw header word against a constant and takes
    ///     the right branch by luck; pinning the id here is what would make that luck visible if a
    ///     build ever shipped a different one.
    /// </summary>
    [Fact]
    public void EveryResolvedPayloadCarriesTheRetailLibraryId()
    {
        var packs = RequireBuilds();

        var ids = new HashSet<uint>();
        foreach (var pack in packs)
        {
            foreach (var resource in Resources(pack).Where(r => r.IsRenderWareStream))
            {
                ids.Add(resource.LibraryId);
            }
        }

        Assert.Equal([OblivionPspResourceReader.RetailLibraryId], ids);
    }

    /// <summary>
    ///     The root chunk of a resolved payload matches what its type name says it is — a CLUMP
    ///     resource opens on chunk 0x10, a WORLD on 0x0B, a texture dictionary on 0x16. This is the
    ///     cross-check that the join lands on the right bytes rather than merely on plausible ones.
    /// </summary>
    [Fact]
    public void TheRootChunkAgreesWithTheDeclaredType()
    {
        var packs = RequireBuilds();

        var expected = new Dictionary<string, uint>(StringComparer.Ordinal)
        {
            ["rwID_CLUMP"] = 0x10,
            ["rwID_WORLD"] = 0x0B,
            ["rwID_TEXDICTIONARY"] = 0x16
        };

        var checkedAny = 0;
        foreach (var pack in packs)
        {
            foreach (var resource in Resources(pack).Where(r => r.IsRenderWareStream))
            {
                Assert.True(
                    expected.TryGetValue(resource.TypeName, out var root),
                    $"{resource.TypeName} resolved to a stream but is not one of the three types.");
                Assert.Equal(root, resource.RootChunkType);
                checkedAny++;
            }
        }

        // 1,995 across the six dated betas (measured 2026-09-09; the floor was 2,000 while the
        // corpus also held the community repack of the February 2007 pack).
        Assert.True(checkedAny > 1_900, $"Only {checkedAny} payloads were checked.");
    }

    /// <summary>
    ///     The non-RenderWare majority is real content, not junk: animations, audio streams,
    ///     conversations and quests all appear. Pinned so that a future change which quietly stopped
    ///     surfacing them would fail rather than look like a cleaner result.
    /// </summary>
    [Fact]
    public void TheNonRenderWareResourcesCarryTheGamesOtherContent()
    {
        var packs = RequireBuilds();

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var pack in packs)
        {
            foreach (var resource in Resources(pack).Where(r => !r.IsRenderWareStream))
            {
                if (resource.TypeName.Length > 0)
                {
                    counts[resource.TypeName] = counts.GetValueOrDefault(resource.TypeName) + 1;
                }
            }
        }

        foreach (var expected in new[]
                 {
                     "rwID_HANIMANIMATION", "rwID_RWS", "rwID_CONVERSATION", "rwID_QUESTS",
                     "rwID_AINAVMESH", "rwID_AUDIOCUES"
                 })
        {
            Assert.True(counts.GetValueOrDefault(expected) > 0, $"{expected} was not surfaced at all.");
        }

        Assert.True(
            counts.GetValueOrDefault("rwID_HANIMANIMATION") > counts.GetValueOrDefault("rwID_QUESTS"),
            "Animations should far outnumber quests; an inverted census means the walk is wrong.");
    }

    /// <summary>
    ///     Authoring paths come back for the resources that carry them — the developers' own
    ///     directory layout, which is what makes a resource identifiable at all given the pack keys
    ///     entries by a name hash.
    /// </summary>
    [Fact]
    public void ResolvedResourcesCarryTheirAuthoringPaths()
    {
        var packs = RequireBuilds();

        var withPaths = 0;
        var oblivionPaths = 0;
        foreach (var pack in packs)
        {
            foreach (var resource in Resources(pack).Where(r => r.IsRenderWareStream))
            {
                if (resource.AuthoringPath.Length == 0)
                {
                    continue;
                }

                withPaths++;
                if (resource.AuthoringPath.Contains("oblivion", StringComparison.OrdinalIgnoreCase))
                {
                    oblivionPaths++;
                }
            }
        }

        Assert.True(withPaths > 1_000, $"Only {withPaths} resolved resources carried an authoring path.");
        Assert.True(
            oblivionPaths > withPaths / 2,
            $"Only {oblivionPaths} of {withPaths} paths mention the project, so the field is misread.");
    }
}