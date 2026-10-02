using System.Collections.Concurrent;
using System.Security.Cryptography;
using BethesdaMultitool.Core.Vfs;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Modeling.RealAsset;

/// <summary>
///     Resolves a cut-1b cover file's bytes from the containers the manifest names: its own source first, then each
///     <c>alsoIn</c> source, every candidate verified against the manifest's SHA-256 and size. Unlike
///     <see cref="Cut1aFixtureResolver" /> there is no Data-folder fallback: every cut-1b source is an archive under
///     <c>Sample/Builds</c> that the census read, and a Data-folder lookup is not a neutral fallback here. It resolves
///     by archive name order, which is not the engine's override order, and it returns a different file for a path that
///     two archives ship with different bytes (measured 2026-09-25: <c>meshes/characters/_1stperson/2hraim.kf</c> is
///     pinned from <c>Update.bsa</c>, and the FNV Data folder answers with the older copy in
///     <c>Fallout - Meshes.bsa</c>).
/// </summary>
/// <remarks>
///     Sources spelled <c>Sample/...</c> resolve through <see cref="RealAssetPaths.SampleFile" />, so the
///     <c>BETHESDA_TEST_DATA_ROOT</c> override applies; <c>&lt;SteamLibrary&gt;/&lt;game&gt;/&lt;path&gt;</c> through
///     <see cref="RealAssetPaths.SteamGameFile" />. Archives are opened once per process and kept open for the theory
///     rows; the test host's exit releases them.
/// </remarks>
internal static class Cut1bFixtureResolver
{
    /// <summary>The prefix of a reason whose container is not on this machine (an unavailable fixture, not a failure).</summary>
    public const string AbsentPrefix = "absent: ";

    private const string SteamLibraryPrefix = "<SteamLibrary>/";
    private const string SamplePrefix = "Sample/";

    private static readonly ConcurrentDictionary<string, Lazy<IGameFileSystem>> Archives =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The bytes of a manifest file, or a skip naming every candidate tried when none reproduces the digest.</summary>
    public static byte[] Require(Cut1bCoverFile file)
    {
        var fixture = TryResolve(file, out var tried);
        Assert.SkipWhen(fixture is null,
            $"{file}: no candidate reproduces SHA-256 {file.Sha256}. Tried: {string.Join("; ", tried)}. " +
            RealAssetPaths.SkipMessage("the cut-1b cover corpus"));
        return fixture!.Bytes;
    }

    /// <summary>
    ///     The first candidate (source, then alsoIn) whose bytes reproduce the manifest SHA-256 and size, or null with
    ///     every candidate tried and why it was rejected.
    /// </summary>
    public static Cut1bFixture? TryResolve(Cut1bCoverFile file, out IReadOnlyList<string> tried)
    {
        var attempts = new List<string>();
        tried = attempts;
        var step = "source";
        foreach (var candidate in file.Candidates)
        {
            var fixture = TryRead(candidate, file, step, out var reason);
            if (fixture is not null)
            {
                return fixture;
            }

            attempts.Add(reason);
            step = "alsoIn";
        }

        return null;
    }

    /// <summary>
    ///     The file read from its own container (the manifest's primary source and entry) alone, or null with the reason:
    ///     <see cref="AbsentPrefix" /> when the container is not on this machine, otherwise why the bytes were rejected.
    /// </summary>
    public static Cut1bFixture? TryResolveContainer(Cut1bCoverFile file, out string reason)
    {
        return TryRead(new Cut1bCoverSource(file.Source, file.Entry), file, "source", out reason);
    }

    /// <summary>
    ///     A named source and entry (for example a <c>.kf</c>'s skeleton companion) read and checked against a digest and
    ///     size, or null with the reason.
    /// </summary>
    public static byte[]? TryReadVerified(Cut1bCoverSource candidate, string sha256, long size, out string reason)
    {
        var (bytes, label) = ReadCandidate(candidate);
        if (bytes is null)
        {
            reason = label;
            return null;
        }

        if (!Matches(bytes, sha256, size))
        {
            reason = "SHA-256 mismatch: " + label;
            return null;
        }

        reason = label;
        return bytes;
    }

    /// <summary>True when the bytes have the manifest file's size and SHA-256.</summary>
    public static bool Matches(ReadOnlySpan<byte> bytes, Cut1bCoverFile file)
    {
        return Matches(bytes, file.Sha256, file.Size);
    }

    /// <summary>The digest of arbitrary bytes, lowercase, as the manifest and expectations spell it.</summary>
    public static string Sha256(ReadOnlySpan<byte> bytes)
    {
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    private static bool Matches(ReadOnlySpan<byte> bytes, string sha256, long size)
    {
        return bytes.Length == size && string.Equals(Sha256(bytes), sha256, StringComparison.Ordinal);
    }

    private static Cut1bFixture? TryRead(Cut1bCoverSource candidate, Cut1bCoverFile file, string step, out string reason)
    {
        var bytes = TryReadVerified(candidate, file.Sha256, file.Size, out reason);
        return bytes is null ? null : new Cut1bFixture(bytes, reason, step);
    }

    private static (byte[]? Bytes, string Label) ReadCandidate(Cut1bCoverSource candidate)
    {
        var label = $"{candidate.Source} :: {candidate.Entry}";
        var location = Locate(candidate.Source);
        if (location is null)
        {
            return (null, AbsentPrefix + label);
        }

        if (!location.EndsWith(".bsa", StringComparison.OrdinalIgnoreCase))
        {
            return (null, "unsupported source kind: " + label);
        }

        var archive = Archives.GetOrAdd(Path.GetFullPath(location),
            path => new Lazy<IGameFileSystem>(() => GameFileSystem.OpenArchive(path))).Value;
        var bytes = archive.TryReadAllBytes(candidate.Entry);
        return bytes is null ? (null, "entry not found: " + label) : (bytes, label);
    }

    /// <summary>A manifest source string as an archive on this machine, or null.</summary>
    private static string? Locate(string source)
    {
        if (source.StartsWith(SteamLibraryPrefix, StringComparison.Ordinal))
        {
            var rest = source[SteamLibraryPrefix.Length..];
            var separator = rest.IndexOf('/', StringComparison.Ordinal);
            return separator <= 0 ? null : RealAssetPaths.SteamGameFile(rest[..separator], rest[(separator + 1)..]);
        }

        if (source.StartsWith(SamplePrefix, StringComparison.Ordinal))
        {
            return RealAssetPaths.SampleFile(source[SamplePrefix.Length..]);
        }

        return File.Exists(source) ? source : null;
    }
}
