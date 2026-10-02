using System.Collections.Concurrent;
using System.Security.Cryptography;
using BethesdaMultitool.Core.Compression;
using BethesdaMultitool.Core.Formats.Redguard;
using BethesdaMultitool.Core.Formats.Xngine.Bsa;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Modeling.RealAsset;

/// <summary>
///     Resolves a cut-1c cover row's payload bytes from the containers the manifest names: the primary source first,
///     then each <c>alsoIn</c> candidate. A candidate is addressed by DIRECTORY INDEX, never by name or id lookup,
///     because ARCH3D.BSA repeats ids and the Battlespire archives repeat names (last-wins path lookup reaches only
///     one copy; plan D6); the entry name at that index must still match, so a reshuffled archive is rejected rather
///     than silently resliced (<see cref="Cut1cFixtureResolverTests" /> proves both rejection paths on synthetic
///     archives), and a ROB candidate's pinned segment type must match the parsed segment's. An LZSS entry's stored
///     bytes are verified against the candidate's stored SHA-256 before decompression, and the decompressed payload
///     against the row's payload SHA-256, so a candidate that decompresses to the wrong bytes and one whose stored
///     bytes drifted are told apart in the reason.
/// </summary>
/// <remarks>
///     Sources spelled <c>Sample/...</c> resolve through <see cref="RealAssetPaths.SampleFile" />, so the
///     <c>BETHESDA_TEST_DATA_ROOT</c> override applies; <c>&lt;SteamLibrary&gt;/&lt;game&gt;/&lt;path&gt;</c> through
///     <see cref="RealAssetPaths.SteamGameFile" />. Containers are parsed once per process
///     (<see cref="XnGineBsaParser" /> for both XnGine BSA forms, <see cref="RedguardRobParser" /> for ROB) and their
///     directories kept for the theory rows; entry bytes are read per call.
/// </remarks>
internal static class Cut1cFixtureResolver
{
    /// <summary>The prefix of a reason whose container is not on this machine (an unavailable fixture, not a failure).</summary>
    public const string AbsentPrefix = "absent: ";

    private const string SteamLibraryPrefix = "<SteamLibrary>/";
    private const string SamplePrefix = "Sample/";

    private static readonly ConcurrentDictionary<string, Lazy<XnGineBsaArchive>> BsaDirectories =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly ConcurrentDictionary<string, Lazy<RedguardRobArchive>> RobDirectories =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The payload bytes of a manifest row, or a skip naming every candidate tried when none reproduces it.</summary>
    public static byte[] Require(Cut1cCoverFile file)
    {
        var fixture = TryResolve(file, out var tried);
        Assert.SkipWhen(fixture is null,
            $"{file}: no candidate reproduces SHA-256 {file.Sha256}. Tried: {string.Join("; ", tried)}. " +
            RealAssetPaths.SkipMessage("the cut-1c cover corpus"));
        return fixture!.Bytes;
    }

    /// <summary>
    ///     The first candidate (primary source, then alsoIn) whose bytes reproduce the row's digests, or null with
    ///     every candidate tried and why it was rejected.
    /// </summary>
    public static Cut1cFixture? TryResolve(Cut1cCoverFile file, out IReadOnlyList<string> tried)
    {
        var attempts = new List<string>();
        tried = attempts;
        var step = "source";
        foreach (var candidate in file.Candidates)
        {
            var bytes = TryReadVerified(candidate, file.Sha256, file.Size, out var reason);
            if (bytes is not null)
            {
                return new Cut1cFixture(bytes, reason, step);
            }

            attempts.Add(reason);
            step = "alsoIn";
        }

        return null;
    }

    /// <summary>
    ///     The row read from its primary candidate alone, or null with the reason: <see cref="AbsentPrefix" /> when
    ///     the container is not on this machine, otherwise why the bytes were rejected.
    /// </summary>
    public static Cut1cFixture? TryResolveContainer(Cut1cCoverFile file, out string reason)
    {
        var candidate = file.Candidates.First();
        var bytes = TryReadVerified(candidate, file.Sha256, file.Size, out reason);
        return bytes is null ? null : new Cut1cFixture(bytes, reason, "source");
    }

    /// <summary>
    ///     A named candidate read and checked against a payload digest and size (and, for a compressed candidate, its
    ///     own stored digest and size), or null with the reason. Passing another row's digest is the manifest's
    ///     namesake control: the candidate resolves by name and index and is then rejected by bytes.
    /// </summary>
    public static byte[]? TryReadVerified(Cut1cCoverSource candidate, string sha256, long size, out string reason)
    {
        var (bytes, label) = ReadCandidate(candidate);
        if (bytes is null)
        {
            reason = label;
            return null;
        }

        if (bytes.LongLength != size || !string.Equals(Sha256(bytes), sha256, StringComparison.Ordinal))
        {
            reason = "payload SHA-256 mismatch: " + label;
            return null;
        }

        reason = label;
        return bytes;
    }

    /// <summary>True when the bytes have the row's payload size and SHA-256.</summary>
    public static bool Matches(ReadOnlySpan<byte> bytes, Cut1cCoverFile file)
    {
        return bytes.Length == file.Size && string.Equals(Sha256(bytes), file.Sha256, StringComparison.Ordinal);
    }

    /// <summary>The digest of arbitrary bytes, lowercase, as the manifest spells it.</summary>
    public static string Sha256(ReadOnlySpan<byte> bytes)
    {
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    private static (byte[]? Bytes, string Label) ReadCandidate(Cut1cCoverSource candidate)
    {
        var label = $"{candidate.Source} :: {candidate.Entry}" + (candidate.Index is { } i ? $" #{i}" : "");
        var location = Locate(candidate.Source);
        if (location is null)
        {
            return (null, AbsentPrefix + label);
        }

        return candidate.Container switch
        {
            Cut1cCoverFile.LooseContainer => (File.ReadAllBytes(location), label),
            Cut1cCoverFile.Arch3dContainer or Cut1cCoverFile.XnGineBsaContainer =>
                ReadBsaEntry(location, candidate, label),
            Cut1cCoverFile.RobContainer => ReadRobSegment(location, candidate, label),
            _ => (null, $"unsupported container '{candidate.Container}': {label}")
        };
    }

    /// <summary>
    ///     One XnGine BSA entry addressed by directory index, with the name-at-index and stored-pin defenses.
    ///     Internal (not private) so <see cref="Cut1cFixtureResolverTests" /> can aim it at synthetic archives in
    ///     temp files and prove each rejection path fires; the manifest paths reach it through
    ///     <see cref="TryReadVerified" />.
    /// </summary>
    internal static (byte[]? Bytes, string Label) ReadBsaEntry(string location, Cut1cCoverSource candidate,
        string label)
    {
        var archive = BsaDirectories.GetOrAdd(Path.GetFullPath(location),
            path => new Lazy<XnGineBsaArchive>(() => XnGineBsaParser.Parse(path))).Value;
        if (candidate.Index is not { } index || index < 0 || index >= archive.Entries.Count)
        {
            return (null, $"index out of range: {label}");
        }

        var entry = archive.Entries[index];
        if (!string.Equals(entry.Name, candidate.Entry, StringComparison.Ordinal))
        {
            return (null, $"index {index} names '{entry.Name}', not '{candidate.Entry}': {label}");
        }

        var stored = ReadSlice(location, entry.Offset, entry.Size);
        if (!entry.Compressed)
        {
            return candidate.IsCompressed
                ? (null, "the manifest pins stored bytes but the entry is not compressed: " + label)
                : (stored, label);
        }

        if (candidate.StoredSha256 is null)
        {
            return (null, "the entry is LZSS-compressed but the manifest pins no stored digest: " + label);
        }

        if (stored.LongLength != candidate.StoredSize ||
            !string.Equals(Sha256(stored), candidate.StoredSha256, StringComparison.Ordinal))
        {
            return (null, "stored SHA-256 mismatch: " + label);
        }

        return (LzssCodec.DecompressBattlespire(stored), label);
    }

    /// <summary>
    ///     One ROB segment addressed by index, with the name-at-index and segment-type defenses. Internal for the
    ///     same reason as <see cref="ReadBsaEntry" />.
    /// </summary>
    internal static (byte[]? Bytes, string Label) ReadRobSegment(string location, Cut1cCoverSource candidate,
        string label)
    {
        var archive = RobDirectories.GetOrAdd(Path.GetFullPath(location),
            path => new Lazy<RedguardRobArchive>(() => RedguardRobParser.Parse(path))).Value;
        if (candidate.Index is not { } index || index < 0 || index >= archive.Entries.Count)
        {
            return (null, $"segment index out of range: {label}");
        }

        var entry = archive.Entries[index];
        if (!string.Equals(entry.Name, candidate.Entry, StringComparison.Ordinal))
        {
            return (null, $"segment {index} names '{entry.Name}', not '{candidate.Entry}': {label}");
        }

        if (candidate.SegmentType is { } segmentType && entry.Type != (uint)segmentType)
        {
            return (null, $"segment {index} has type {entry.Type}, not {segmentType}: {label}");
        }

        return (ReadSlice(location, entry.Offset, entry.Size), label);
    }

    private static byte[] ReadSlice(string location, long offset, int size)
    {
        using var stream = new FileStream(location, FileMode.Open, FileAccess.Read, FileShare.Read);
        stream.Position = offset;
        var bytes = new byte[size];
        stream.ReadExactly(bytes);
        return bytes;
    }

    /// <summary>A manifest source string as a file on this machine, or null.</summary>
    private static string? Locate(string source)
    {
        if (source.StartsWith(SteamLibraryPrefix, StringComparison.Ordinal))
        {
            var rest = source[SteamLibraryPrefix.Length..];
            var separator = rest.IndexOf('/', StringComparison.Ordinal);
            return separator <= 0 ? null : RealAssetPaths.SteamGameFile(rest[..separator], rest[(separator + 1)..]);
        }

        return source.StartsWith(SamplePrefix, StringComparison.Ordinal)
            ? RealAssetPaths.SampleFile(source[SamplePrefix.Length..])
            : null;
    }
}
