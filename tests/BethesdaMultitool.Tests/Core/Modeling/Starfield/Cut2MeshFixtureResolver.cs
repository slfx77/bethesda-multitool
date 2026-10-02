using System.Collections.Concurrent;
using System.Security.Cryptography;
using BethesdaMultitool.Core.Vfs;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Modeling.Starfield;

/// <summary>
///     Resolves a cut-2 cover row's payload bytes from the Starfield GNRL archives the manifest names: the primary source
///     first, then each <c>alsoIn</c> candidate. Every candidate must reproduce the row's size and SHA-256 or it is
///     rejected (<see cref="Verify" />), so a path read from a different archive, or from a patched install, can never
///     stand in for the pinned bytes.
/// </summary>
/// <remarks>
///     Archives resolve through <see cref="RealAssetPaths.SteamGameFile" /> (<c>Starfield</c>, <c>Data/&lt;archive&gt;</c>),
///     so every fixed drive's Steam library and the <c>BETHESDA_TEST_DATA_ROOT</c> override are probed; they are opened
///     once per process through BMT's archive layer (<see cref="GameFileSystem.OpenArchive" />) and kept for the theory
///     rows. The digest check is what makes the archive layer's entry lookup a non-issue: whatever it returns is compared
///     with the manifest before any test reads it.
/// </remarks>
internal static class Cut2MeshFixtureResolver
{
    /// <summary>The prefix of a reason whose archive is not on this machine (an unavailable fixture, not a failure).</summary>
    public const string AbsentPrefix = "absent: ";

    /// <summary>The prefix of a reason whose archive is present but whose archive layer returns no entry at the path.</summary>
    public const string EntryNotFoundPrefix = "entry not found: ";

    /// <summary>The prefix of a reason whose entry bytes are not the pinned payload.</summary>
    public const string DigestMismatchPrefix = "payload SHA-256 mismatch: ";

    /// <summary>The Steam game folder.</summary>
    public const string SteamGameFolder = "Starfield";

    private static readonly ConcurrentDictionary<string, Lazy<IGameFileSystem>> Archives =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     The payload bytes of a manifest row. Skips only when every candidate's archive is absent from this machine (an
    ///     unavailable fixture, <see cref="IsAbsent" />); any other miss (an entry the archive layer does not find, other
    ///     bytes, another size) fails the test naming every candidate tried, because Starfield is installed and the pin
    ///     cannot be reproduced.
    /// </summary>
    public static byte[] Require(Cut2MeshCoverFile file)
    {
        var bytes = TryResolve(file, out var tried);
        var reasons = string.Join("; ", tried);
        Assert.SkipWhen(bytes is null && IsAbsent(tried),
            $"{file}: {reasons}. " + RealAssetPaths.SkipMessage("the Starfield install (cut-2 .mesh cover)"));
        Assert.True(bytes is not null, $"{file}: no candidate reproduces SHA-256 {file.Sha256}. Tried: {reasons}.");
        return bytes;
    }

    /// <summary>
    ///     Whether a resolution failed only because the archives are not on this machine: every candidate tried carries
    ///     <see cref="AbsentPrefix" />. An empty list is not absent (nothing was tried, so nothing is known).
    /// </summary>
    public static bool IsAbsent(IReadOnlyList<string> tried)
    {
        ArgumentNullException.ThrowIfNull(tried);
        return tried.Count > 0 && tried.All(static reason => reason.StartsWith(AbsentPrefix, StringComparison.Ordinal));
    }

    /// <summary>The first candidate whose bytes reproduce the row's digest, or null with every candidate tried and why.</summary>
    public static byte[]? TryResolve(Cut2MeshCoverFile file, out IReadOnlyList<string> tried)
    {
        ArgumentNullException.ThrowIfNull(file);
        var attempts = new List<string>();
        tried = attempts;
        foreach (var candidate in file.Candidates)
        {
            var label = $"{candidate.Archive} :: {candidate.Entry} #{candidate.Index}";
            var bytes = Read(candidate, out var absent);
            if (bytes is null)
            {
                attempts.Add((absent ? AbsentPrefix : EntryNotFoundPrefix) + label);
                continue;
            }

            if (!Verify(bytes, file.Size, file.Sha256, label, out var reason))
            {
                attempts.Add(reason);
                continue;
            }

            return bytes;
        }

        return null;
    }

    /// <summary>
    ///     Whether bytes read for a candidate are the row's payload (size and SHA-256), with the reason when they are not.
    ///     Pure, so the default suite can prove a candidate holding other bytes is rejected (no retail path repeats with
    ///     different bytes, so that control is synthetic).
    /// </summary>
    public static bool Verify(byte[] bytes, long size, string sha256, string label, out string reason)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.LongLength != size)
        {
            reason = $"size {bytes.LongLength}, not {size}: {label}";
            return false;
        }

        if (!string.Equals(Sha256(bytes), sha256, StringComparison.Ordinal))
        {
            reason = DigestMismatchPrefix + label;
            return false;
        }

        reason = label;
        return true;
    }

    /// <summary>The digest of arbitrary bytes, lowercase, as the manifest spells it.</summary>
    public static string Sha256(ReadOnlySpan<byte> bytes)
    {
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    /// <summary>A candidate's entry bytes, or null (with whether the archive itself is absent).</summary>
    private static byte[]? Read(Cut2MeshCoverSource candidate, out bool absent)
    {
        var location = RealAssetPaths.SteamGameFile(SteamGameFolder, "Data/" + candidate.Archive);
        absent = location is null;
        if (location is null)
        {
            return null;
        }

        var archive = Archives.GetOrAdd(Path.GetFullPath(location),
            path => new Lazy<IGameFileSystem>(() => GameFileSystem.OpenArchive(path))).Value;
        return archive.TryReadAllBytes(candidate.Entry);
    }
}
