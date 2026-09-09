using System.Globalization;
using BethesdaMultitool.Core.Formats.Granny;

namespace BethesdaMultitool.Core.Formats.VanBuren;

/// <summary>One Granny payload of a <c>.grp</c> archive, decoded.</summary>
internal sealed record VanBurenGrannyEntry(VanBurenGrpEntry Entry, Gr2File File, VanBurenGrannyKind Kind)
{
    /// <summary>
    ///     The name a browser shows: the first model's name, else the first skeleton's, else the
    ///     first animation's, else the archive entry name.
    /// </summary>
    public string DisplayName =>
        (File.Models.Count > 0 ? File.Models[0]?.Name : null)
        ?? (File.Skeletons.Count > 0 ? File.Skeletons[0]?.Name : null)
        ?? (File.Animations.Count > 0 ? File.Animations[0]?.Name : null)
        ?? Entry.Name;
}

/// <summary>
///     The Granny payloads of one Van Buren <c>.grp</c> archive: every entry that opens with the
///     Granny 2 magic, decoded through <see cref="VanBurenGrannyFile.Parse" />. Also answers the
///     question the exporter needs — which animation payloads in the archive animate a given
///     skeleton (their track group is named after it).
/// </summary>
internal sealed class VanBurenGrannyCatalog
{
    private readonly VanBurenGrpArchive _archive;
    private readonly byte[] _archiveBytes;
    private readonly Dictionary<int, VanBurenGrannyEntry> _decoded = [];
    private readonly List<VanBurenGrpEntry> _grannyEntries;

    private VanBurenGrannyCatalog(byte[] archiveBytes, VanBurenGrpArchive archive)
    {
        _archiveBytes = archiveBytes;
        _archive = archive;
        _grannyEntries = archive.Entries
            .Where(entry => entry.Size > 0 && VanBurenGrannyFile.IsGranny(archiveBytes.AsSpan((int)entry.Offset,
                Math.Min(entry.Size, VanBurenGrannyFile.SignatureLength))))
            .ToList();
    }

    public string Name => _archive.Name;

    /// <summary>The archive entries that are Granny payloads, in archive order.</summary>
    public IReadOnlyList<VanBurenGrpEntry> GrannyEntries => _grannyEntries;

    public static VanBurenGrannyCatalog Open(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var bytes = File.ReadAllBytes(path);
        return new VanBurenGrannyCatalog(bytes, VanBurenGrpArchive.Parse(bytes, Path.GetFileName(path)));
    }

    /// <summary>Decodes (and caches) the Granny payload at that archive entry index.</summary>
    public VanBurenGrannyEntry Decode(int entryIndex)
    {
        if (_decoded.TryGetValue(entryIndex, out var cached))
        {
            return cached;
        }

        var entry = _archive.Entries.FirstOrDefault(candidate => candidate.Index == entryIndex);
        if (entry.Size <= 0 || !_grannyEntries.Contains(entry))
        {
            throw new InvalidOperationException($"Entry {entryIndex} of {Name} is not a Granny payload.");
        }

        var payload = VanBurenGrpArchive.Read(_archiveBytes, entry);
        var file = VanBurenGrannyFile.Parse(payload, $"{Name}/{entry.Name}");
        var decoded = new VanBurenGrannyEntry(entry, file, VanBurenGrannyFile.Classify(file));
        _decoded[entryIndex] = decoded;
        return decoded;
    }

    /// <summary>Decodes every Granny payload, in archive order.</summary>
    public IEnumerable<VanBurenGrannyEntry> DecodeAll()
    {
        foreach (var entry in _grannyEntries)
        {
            yield return Decode(entry.Index);
        }
    }

    /// <summary>
    ///     Resolves <c>-e</c>: an entry index (<c>00038</c>, <c>38</c>, or the listing's
    ///     <c>00038.G</c>), else the first payload whose model, skeleton or animation name matches
    ///     (case-insensitive), else null.
    /// </summary>
    public VanBurenGrannyEntry? Find(string selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        var stem = selector;
        var dot = stem.IndexOf('.', StringComparison.Ordinal);
        if (dot > 0 && stem[..dot].All(char.IsAsciiDigit))
        {
            stem = stem[..dot];
        }

        if (int.TryParse(stem, NumberStyles.None, CultureInfo.InvariantCulture, out var index) &&
            _grannyEntries.Any(entry => entry.Index == index))
        {
            return Decode(index);
        }

        foreach (var decoded in DecodeAll())
        {
            var file = decoded.File;
            if (file.Models.Any(model => Matches(model.Name, selector))
                || file.Skeletons.Any(skeleton => Matches(skeleton.Name, selector))
                || file.Meshes.Any(mesh => Matches(mesh.Name, selector))
                || file.Animations.Any(animation => Matches(animation.Name, selector)))
            {
                return decoded;
            }
        }

        return null;
    }

    /// <summary>The animation clips in this archive whose track group names that skeleton exactly.</summary>
    public IReadOnlyList<Gr2AnimationClip> AnimationsFor(string skeletonName)
    {
        ArgumentNullException.ThrowIfNull(skeletonName);
        var clips = new List<Gr2AnimationClip>();
        foreach (var decoded in DecodeAll())
        {
            if (decoded.Kind != VanBurenGrannyKind.Animation)
            {
                continue;
            }

            clips.AddRange(decoded.File.Animations.Where(clip =>
                clip.TrackGroups.Any(group => string.Equals(group.Name, skeletonName, StringComparison.Ordinal))));
        }

        return clips;
    }

    private static bool Matches(string? name, string selector)
    {
        return name is not null && string.Equals(name, selector, StringComparison.OrdinalIgnoreCase);
    }
}
