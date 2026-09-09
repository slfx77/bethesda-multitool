namespace BethesdaMultitool.Core.Formats.Audio;

/// <summary>
///     Names an audio container from its leading bytes.
///     <para>
///         ⚠⚠ This exists because a preview file's EXTENSION decides whether it plays at all.
///         Windows Media Foundation's source resolver picks a byte-stream handler by file
///         extension before it inspects the content, so bytes written under the wrong extension are
///         handed to the wrong handler and rejected. The asset browser wrote every preview as
///         <c>.wav</c>, which meant Ogg, FLAC, M4A and MP3 pass-throughs could never play — and the
///         failure surfaced as an opaque Media Foundation error, reading like a corrupt asset
///         rather than a wrong file name. Shadowkey ships six OGG files that were affected.
///     </para>
///     <para>
///         Answered from the BYTES rather than from the source file's name, and that distinction is
///         the point: classic formats such as <c>.voc</c> and <c>.acm</c> are converted to RIFF
///         before they reach a player, so naming the output after the input would be wrong in
///         exactly the cases the conversion made right.
///     </para>
/// </summary>
public static class AudioContainerFormat
{
    /// <summary>The extension unrecognised bytes take — what every converted path produces.</summary>
    public const string DefaultExtension = ".wav";

    /// <summary>
    ///     The file extension (including the dot) these bytes should be stored under, or
    ///     <see cref="DefaultExtension" /> when nothing matches.
    /// </summary>
    public static string ExtensionFor(ReadOnlySpan<byte> data)
    {
        if (HasTag(data, 0, "RIFF"))
        {
            return ".wav";
        }

        if (HasTag(data, 0, "OggS"))
        {
            return ".ogg";
        }

        if (HasTag(data, 0, "fLaC"))
        {
            return ".flac";
        }

        // An MP4/M4A box names its brand at +4, after the box length.
        if (HasTag(data, 4, "ftyp"))
        {
            return ".m4a";
        }

        // ID3-tagged first, then a bare MPEG frame sync for an MP3 that carries no tag. The sync is
        // eleven set bits, so the second byte only has to have its top three set.
        if (HasTag(data, 0, "ID3") ||
            (data.Length >= 2 && data[0] == 0xFF && (data[1] & 0xE0) == 0xE0))
        {
            return ".mp3";
        }

        return DefaultExtension;
    }

    /// <summary>Whether <paramref name="data" /> carries the ASCII <paramref name="tag" /> at an offset.</summary>
    private static bool HasTag(ReadOnlySpan<byte> data, int offset, string tag)
    {
        if (data.Length < offset + tag.Length)
        {
            return false;
        }

        for (var i = 0; i < tag.Length; i++)
        {
            if (data[offset + i] != (byte)tag[i])
            {
                return false;
            }
        }

        return true;
    }
}
