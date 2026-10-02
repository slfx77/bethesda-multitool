using System.Buffers.Binary;
using System.Security.Cryptography;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Modeling.Shadowkey;

/// <summary>
///     Resolves the cut-2 Shadowkey cover rows from the retail application directory exactly as
///     <see cref="RealAssetPaths.Travels.ShadowkeyRoot" /> finds it, and verifies every byte against the manifest's size
///     and SHA-256 before a test sees it. Slots are sliced out of <c>models.huge</c> through <c>models.idx</c> with a walk
///     of this class's own (the pack pins are checked first), so the resolver shares no parser with the readers.
/// </summary>
/// <remarks>
///     <see cref="Require" /> skips a row only when the directory is absent from this machine; with the directory present,
///     a missing file or bytes that do not reproduce the pin FAIL the row with the reason.
/// </remarks>
internal static class Cut2ShadowkeyFixtureResolver
{
    /// <summary>The prefix of a digest-mismatch reason.</summary>
    public const string DigestMismatchPrefix = "digest mismatch";

    /// <summary>The application directory, or null when it is absent.</summary>
    public static string? Root => RealAssetPaths.Travels.ShadowkeyRoot();

    /// <summary>The lowercase SHA-256 of bytes.</summary>
    public static string Sha256(ReadOnlySpan<byte> bytes)
    {
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    /// <summary>
    ///     The application directory, or a skip naming it. The Bucket-B guard is the calling test's own first statement
    ///     (<see cref="Cut2ShadowkeyOracleTests" />), so the guard and its category trait share one file.
    /// </summary>
    public static string RequireRoot()
    {
        var root = Root;
        Assert.SkipWhen(root is null, RealAssetPaths.SkipMessage("Shadowkey (system/apps/6R51, cut-2 cover)"));
        return root;
    }

    /// <summary>A file of the directory, verified against its pin, or null with the reason.</summary>
    public static byte[]? TryReadFile(string root, string name, Cut2ShadowkeyPin pin, out string reason)
    {
        var path = Path.Combine(root, name);
        if (!File.Exists(path))
        {
            reason = $"'{name}' is not in {root}";
            return null;
        }

        var bytes = File.ReadAllBytes(path);
        return Verify(bytes, pin, name, out reason);
    }

    /// <summary>A file of the directory, verified against its pin; fails the test otherwise.</summary>
    public static byte[] RequireFile(string root, string name, Cut2ShadowkeyPin pin)
    {
        var bytes = TryReadFile(root, name, pin, out var reason);
        Assert.True(bytes is not null, reason);
        return bytes;
    }

    /// <summary>One pack slot's bytes, verified against the row (the pack pins are checked first).</summary>
    public static byte[]? TryReadSlot(string root, int slot, long offset, Cut2ShadowkeyPin pin, out string reason)
    {
        var pack = Cut2ShadowkeyCoverManifest.Cover.Pack;
        var index = TryReadFile(root, "models.idx", pack["models.idx"], out reason);
        var huge = index is null ? null : TryReadFile(root, "models.huge", pack["models.huge"], out reason);
        if (index is null || huge is null)
        {
            return null;
        }

        var count = BinaryPrimitives.ReadUInt32LittleEndian(index);
        if (slot < 0 || slot >= count)
        {
            reason = $"slot {slot} is not in the {count}-slot index";
            return null;
        }

        var at = 4 + slot * 8;
        var start = BinaryPrimitives.ReadUInt32LittleEndian(index.AsSpan(at));
        var size = BinaryPrimitives.ReadUInt32LittleEndian(index.AsSpan(at + 4));
        if (start != offset)
        {
            reason = $"slot {slot} starts at {start}, the manifest says {offset}";
            return null;
        }

        return Verify(huge.AsSpan((int)start, (int)size).ToArray(), pin, $"slot {slot}", out reason);
    }

    /// <summary>A slot row's bytes, verified; fails the test otherwise.</summary>
    public static byte[] Require(string root, Cut2ShadowkeySlotRow row)
    {
        var bytes = TryReadSlot(root, row.Slot, row.Offset, new Cut2ShadowkeyPin(row.Size, row.Sha256), out var reason);
        Assert.True(bytes is not null, $"{row.Name}: {reason}");
        return bytes;
    }

    /// <summary>A decline row's bytes, verified; fails the test otherwise.</summary>
    public static byte[] Require(string root, Cut2ShadowkeyDeclineRow row)
    {
        if (row.Kind == Cut2ShadowkeyDeclineRow.EmptySlotKind)
        {
            var pack = Cut2ShadowkeyCoverManifest.Cover.Pack;
            var index = RequireFile(root, "models.idx", pack["models.idx"]);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(index.AsSpan(4 + row.Slot!.Value * 8 + 4));
            Assert.Equal(0u, size);
            return [];
        }

        return RequireFile(root, row.Path!, new Cut2ShadowkeyPin(row.Size, row.Sha256));
    }

    private static byte[]? Verify(byte[] bytes, Cut2ShadowkeyPin pin, string label, out string reason)
    {
        if (bytes.LongLength != pin.Size || Sha256(bytes) != pin.Sha256)
        {
            reason = $"{DigestMismatchPrefix}: {label} is {bytes.LongLength} bytes with SHA-256 {Sha256(bytes)}, " +
                     $"the manifest pins {pin.Size} bytes and {pin.Sha256}";
            return null;
        }

        reason = string.Empty;
        return bytes;
    }
}
