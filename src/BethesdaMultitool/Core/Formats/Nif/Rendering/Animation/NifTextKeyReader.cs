using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;

/// <summary>
///     Reads <c>NiTextKeyExtraData</c> — the authored animation markers ("Idle3: Loop Start", …).
///     TES3 groups several markers into ONE key's label separated by newlines (the tavern banner's
///     key 0 carries "Idle: Start\r\nIdle: Stop\r\nIdle2: Start"), so each line becomes its own
///     <see cref="NifAnimTextKey" /> at the key's time. Legacy layout (≤ 4.2.2.0): next-extra ref +
///     record size + count + (time + SizedString) keys; modern (10.x+): name + count + keys.
/// </summary>
internal static class NifTextKeyReader
{
    private const uint MaxTextKeys = 4096;
    private const uint MaxTextKeyLabelBytes = 512;

    /// <summary>
    ///     Finds the first NiTextKeyExtraData block and reads its markers, sorted by time.
    ///     Empty when the NIF has none (statics without animation clips).
    /// </summary>
    internal static NifAnimTextKey[] ReadFirst(byte[] data, NifInfo nif)
    {
        for (var i = 0; i < nif.Blocks.Count; i++)
        {
            if (nif.Blocks[i].TypeName == "NiTextKeyExtraData")
            {
                return Read(data, nif, nif.Blocks[i]);
            }
        }

        return [];
    }

    internal static NifAnimTextKey[] Read(byte[] data, NifInfo nif, BlockInfo block)
    {
        var be = nif.IsBigEndian;
        var pos = block.DataOffset;
        var end = block.DataOffset + block.Size;

        if (NifVersions.IsLegacyNetImmerse(nif.BinaryVersion))
        {
            // Legacy NiExtraData head: Next Extra Data ref + Record Size.
            pos += 8;
        }
        else if (!NifBinaryCursor.SkipName(data, ref pos, end, be, nif.HasInlineStrings))
        {
            return [];
        }

        if (pos + 4 > end)
        {
            return [];
        }

        var numKeys = BinaryUtils.ReadUInt32(data, pos, be);
        pos += 4;
        if (numKeys > MaxTextKeys)
        {
            return [];
        }

        var keys = new List<NifAnimTextKey>((int)numKeys);
        for (var i = 0; i < numKeys; i++)
        {
            if (pos + 4 > end)
            {
                return [];
            }

            var time = BinaryUtils.ReadFloat(data, pos, be);
            pos += 4;

            string? label;
            if (nif.HasInlineStrings)
            {
                label = NifBinaryCursor.ReadSizedString(data, ref pos, end, be);
            }
            else
            {
                // Modern string-table index.
                if (pos + 4 > end)
                {
                    return [];
                }

                var stringIndex = BinaryUtils.ReadInt32(data, pos, be);
                pos += 4;
                label = stringIndex >= 0 && stringIndex < nif.Strings.Count ? nif.Strings[stringIndex] : null;
            }

            if (label is null)
            {
                continue;
            }

            // TES3 packs several markers into one label, one per line.
            foreach (var line in label.Split('\n'))
            {
                var trimmed = line.Trim('\r', ' ');
                if (trimmed.Length > 0)
                {
                    keys.Add(new NifAnimTextKey(time, trimmed));
                }
            }
        }

        keys.Sort(static (a, b) => a.Time.CompareTo(b.Time));
        return keys.ToArray();
    }

    /// <summary>
    ///     Reads a referenced text-key block only when its complete, known layout consumes the
    ///     declared block span. This distinguishes an authored zero-key block from malformed input;
    ///     <see cref="Read" /> deliberately retains its older tolerant empty-array contract for the
    ///     embedded-animation callers that use text keys as optional metadata.
    /// </summary>
    internal static bool TryReadExact(
        byte[] data,
        NifInfo nif,
        BlockInfo block,
        out NifAnimTextKey[] textKeys)
    {
        textKeys = [];
        if (block.DataOffset < 0 || block.Size < 0 ||
            (long)block.DataOffset + block.Size > data.LongLength)
        {
            return false;
        }

        var be = nif.IsBigEndian;
        var pos = block.DataOffset;
        var end = block.DataOffset + block.Size;

        if (NifVersions.IsLegacyNetImmerse(nif.BinaryVersion))
        {
            // Legacy NiExtraData head: Next Extra Data ref + Record Size.
            if ((long)pos + 8L > end)
            {
                return false;
            }

            pos += 8;
        }
        else if (!TrySkipNameExact(data, ref pos, end, be, nif.HasInlineStrings))
        {
            return false;
        }

        if ((long)pos + 4L > end)
        {
            return false;
        }

        var numKeys = BinaryUtils.ReadUInt32(data, pos, be);
        pos += 4;
        if (numKeys > MaxTextKeys)
        {
            return false;
        }

        var keys = new List<NifAnimTextKey>((int)numKeys);
        for (var index = 0u; index < numKeys; index++)
        {
            if ((long)pos + 4L > end)
            {
                return false;
            }

            var time = BinaryUtils.ReadFloat(data, pos, be);
            pos += 4;

            string label;
            if (nif.HasInlineStrings)
            {
                if (!TryReadRequiredSizedString(data, ref pos, end, be, out label))
                {
                    return false;
                }
            }
            else
            {
                if ((long)pos + 4L > end)
                {
                    return false;
                }

                var stringIndex = BinaryUtils.ReadInt32(data, pos, be);
                pos += 4;
                if (stringIndex < 0 || stringIndex >= nif.Strings.Count ||
                    string.IsNullOrWhiteSpace(nif.Strings[stringIndex]))
                {
                    return false;
                }

                label = nif.Strings[stringIndex];
            }

            var keyCountBeforeLabel = keys.Count;
            foreach (var line in label.Split('\n'))
            {
                var trimmed = line.Trim('\r', ' ');
                if (trimmed.Length > 0)
                {
                    keys.Add(new NifAnimTextKey(time, trimmed));
                }
            }

            if (keys.Count == keyCountBeforeLabel)
            {
                return false;
            }
        }

        if (pos != end)
        {
            return false;
        }

        keys.Sort(static (a, b) => a.Time.CompareTo(b.Time));
        textKeys = keys.ToArray();
        return true;
    }

    private static bool TrySkipNameExact(
        byte[] data,
        ref int pos,
        int end,
        bool be,
        bool hasInlineStrings)
    {
        if ((long)pos + 4L > end)
        {
            return false;
        }

        if (!hasInlineStrings)
        {
            pos += 4;
            return true;
        }

        var length = BinaryUtils.ReadUInt32(data, pos, be);
        pos += 4;
        if (length > int.MaxValue || (long)pos + length > end)
        {
            return false;
        }

        pos += (int)length;
        return true;
    }

    private static bool TryReadRequiredSizedString(
        byte[] data,
        ref int pos,
        int end,
        bool be,
        out string value)
    {
        value = string.Empty;
        if ((long)pos + 4L > end)
        {
            return false;
        }

        var length = BinaryUtils.ReadUInt32(data, pos, be);
        pos += 4;
        if (length is 0 or > MaxTextKeyLabelBytes || (long)pos + length > end)
        {
            return false;
        }

        value = System.Text.Encoding.ASCII.GetString(data, pos, (int)length);
        pos += (int)length;
        return !string.IsNullOrWhiteSpace(value);
    }
}
