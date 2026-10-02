using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using BethesdaMultitool.Core.Formats.Travels.Shadowkey;
using Slfx77.Multitool.Core.Models.Sources;

namespace BethesdaMultitool.Core.Modeling.Shadowkey;

/// <summary>
///     The bounded content probe behind <see cref="ShadowkeyZoneModelReader.Probe" /> (cut-2 plan section 5.2, decision
///     D11): a zone is read from its <c>.zmp</c>, recognized by the compressed envelope and the inflated grid header.
/// </summary>
/// <remarks>
///     <para>
///         The walk: the <see cref="ShadowkeyCompressedFile" /> envelope (a u32 inflated length, then a zlib header at
///         byte 4 whose CMF is 0x78 and whose CMF/FLG word divides by 31); the first 132 inflated bytes; a NUL-terminated
///         printable-ASCII zone name inside the first 32 bytes; a grid of 1 to 4,096 cells a side; and
///         <c>132 + 6 x width x height</c> equal to the declared inflated length. A complete candidate must then inflate to
///         exactly that length with the stream ending at the file's last byte (checked by the trailing Adler-32, because
///         .NET's inflater silently ignores bytes after a stream): Supported and Confirmed. An incomplete prefix is
///         Supported and Tentative. The name equalling the file stem only enriches the evidence.
///     </para>
///     <para>
///         The evidence text is exactly the independent probe's (<c>probe_zone</c> in
///         <c>tools/scripts/gate2/shadowkey_probe.py</c>). Measured on the retail tree: exactly the 21 <c>.zmp</c> files
///         are claimed, Confirmed; the other 105 compressed zone files are refused (name field 101, grid size 2, grid
///         arithmetic 2), and no pack slot, NIF, XnGine <c>.3D</c> or Starfield <c>.mesh</c> passes the envelope and the
///         grid arithmetic together.
///     </para>
/// </remarks>
internal static class ShadowkeyZoneModelProbe
{
    /// <summary>The largest grid side the probe admits (retail grids are 64 and 128).</summary>
    public const int MaximumGridSide = 4096;

    /// <summary>The note a Tentative evidence carries.</summary>
    public const string IncompleteNote = " (prefix is not the whole file)";

    /// <summary>The note an evidence carries when the zone name equals the file stem (case-insensitively).</summary>
    public const string StemNote = " (name equals the file stem)";

    /// <summary>Probes one bounded candidate (see the type remarks).</summary>
    public static ModelProbeResult Probe(ModelSourceCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return Probe(candidate.Content, candidate.IsComplete, candidate.Reference.Path);
    }

    /// <summary>The walk over a prefix with its completeness and the entry path (for the stem note only).</summary>
    public static ModelProbeResult Probe(ReadOnlySpan<byte> prefix, bool complete, string? path)
    {
        if (!ShadowkeyCompressedFile.LooksLike(prefix))
        {
            return ModelProbeResult.NotAModel;
        }

        var declared = BinaryPrimitives.ReadUInt32LittleEndian(prefix);
        var head = new byte[ShadowkeyZoneMap.HeaderLength];
        if (Inflate(prefix, head) != head.Length)
        {
            return ModelProbeResult.NotAModel;
        }

        var nameField = head.AsSpan(0, ShadowkeyZoneMap.NameFieldLength);
        var end = nameField.IndexOf((byte)0);
        if (end <= 0)
        {
            return ModelProbeResult.NotAModel;
        }

        foreach (var value in nameField[..end])
        {
            if (value is < 0x20 or > 0x7E)
            {
                return ModelProbeResult.NotAModel;
            }
        }

        int width = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(128));
        int height = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(130));
        if (width is 0 or > MaximumGridSide || height is 0 or > MaximumGridSide)
        {
            return ModelProbeResult.NotAModel;
        }

        if (ShadowkeyZoneMap.HeaderLength + (long)ShadowkeyZoneMap.CellLength * width * height != declared)
        {
            return ModelProbeResult.NotAModel;
        }

        var zoneName = Encoding.Latin1.GetString(nameField[..end]);
        var evidence = string.Create(CultureInfo.InvariantCulture, $"Shadowkey zone grid '{zoneName}' {width}x{height}");
        if (path is not null && string.Equals(Path.GetFileNameWithoutExtension(path.Replace('\\', '/').Split('/')[^1]),
                zoneName, StringComparison.OrdinalIgnoreCase))
        {
            evidence += StemNote;
        }

        var examined = Math.Min(prefix.Length, ModelSourceCandidate.MaximumProbeBytes);
        if (!complete)
        {
            return new ModelProbeResult(ModelProbeKind.Supported, ModelProbeConfidence.Tentative,
                new ModelProbeEvidence(0, examined, evidence + IncompleteNote));
        }

        return InflatesExactly(prefix, declared)
            ? new ModelProbeResult(ModelProbeKind.Supported, ModelProbeConfidence.Confirmed,
                new ModelProbeEvidence(0, examined, evidence))
            : ModelProbeResult.NotAModel;
    }

    /// <summary>The fixed buffer <see cref="InflatesExactly" /> streams the payload through.</summary>
    public const int InflateBufferBytes = 16 * 1024;

    /// <summary>
    ///     True when the whole file inflates to exactly <paramref name="declared" /> bytes and its last four bytes are the
    ///     big-endian Adler-32 of that payload (RFC 1950), i.e. the stream ends at the file's last byte.
    /// </summary>
    /// <remarks>
    ///     The payload streams through one <see cref="InflateBufferBytes" /> buffer, counted and folded into the Adler-32
    ///     as it arrives, and the walk stops at the first byte past <paramref name="declared" />. So the probe allocates
    ///     the same whatever the header claims: the grid check alone admits a claim of about 96 MiB (4,096 x 4,096
    ///     cells), which a buffer sized from it would allocate on every probe of such a file (cut-2 review finding 13;
    ///     retail peaks at 98,436 bytes).
    /// </remarks>
    public static bool InflatesExactly(ReadOnlySpan<byte> file, uint declared)
    {
        if (file.Length < ShadowkeyCompressedFile.MinimumLength + 4)
        {
            return false;
        }

        var buffer = new byte[InflateBufferBytes];
        long produced = 0;
        var adler = 1u;
        try
        {
            using var source = new MemoryStream(file[ShadowkeyCompressedFile.HeaderLength..].ToArray(), false);
            using var inflater = new ZLibStream(source, CompressionMode.Decompress);
            while (true)
            {
                var read = inflater.Read(buffer, 0, buffer.Length);
                if (read == 0)
                {
                    break;
                }

                produced += read;
                if (produced > declared)
                {
                    return false;
                }

                adler = Adler32(buffer.AsSpan(0, read), adler);
            }
        }
        catch (InvalidDataException)
        {
            return false;
        }

        return produced == declared && BinaryPrimitives.ReadUInt32BigEndian(file[^4..]) == adler;
    }

    /// <summary>
    ///     The Adler-32 of a buffer (RFC 1950 section 9), continued from <paramref name="seed" /> (1 starts a stream), so a
    ///     payload can be folded in piece by piece.
    /// </summary>
    public static uint Adler32(ReadOnlySpan<byte> data, uint seed = 1)
    {
        const uint modulus = 65521;
        var a = seed & 0xFFFF;
        var b = seed >> 16;
        foreach (var value in data)
        {
            a = (a + value) % modulus;
            b = (b + a) % modulus;
        }

        return (b << 16) | a;
    }

    /// <summary>Inflates the envelope's stream into <paramref name="target" />, returning the bytes produced (0 on corrupt data).</summary>
    private static int Inflate(ReadOnlySpan<byte> file, byte[] target)
    {
        try
        {
            using var source = new MemoryStream(file[ShadowkeyCompressedFile.HeaderLength..].ToArray(), false);
            using var inflater = new ZLibStream(source, CompressionMode.Decompress);
            var produced = 0;
            while (produced < target.Length)
            {
                var read = inflater.Read(target, produced, target.Length - produced);
                if (read == 0)
                {
                    break;
                }

                produced += read;
            }

            return produced;
        }
        catch (InvalidDataException)
        {
            return 0;
        }
    }
}
