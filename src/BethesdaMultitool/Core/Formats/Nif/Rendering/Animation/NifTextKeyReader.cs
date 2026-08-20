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
}
