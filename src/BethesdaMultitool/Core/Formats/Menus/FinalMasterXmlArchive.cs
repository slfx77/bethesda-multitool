using System.Buffers.Binary;
using System.Text;

namespace BethesdaMultitool.Core.Formats.Menus;

/// <summary>
///     Reader for <c>Data\final_master_xml.dat</c>, the container the Xbox 360 build ships
///     instead of the loose <c>menus\*.xml</c> tree that lives in the PC <c>Fallout - Misc.bsa</c>.
///     <para>
///         Layout is big-endian throughout:
///         <code>
///         u32 version        (100 in the retail 360 build)
///         u32 entryCount
///         u32 payloadBytes   (file length minus this 12-byte header)
///         entryCount times:
///             char name[128] (NUL-terminated, 0xFD filler)
///             u32  length
///             byte xml[length]
///         </code>
///     </para>
///     <para>
///         The console build pre-flattens every <c>&lt;include&gt;</c>, so these documents carry no
///         prefab references and stand alone — the PC <c>menus\prefabs\</c> tree is not needed to
///         use them. Names are stored flat; <see cref="FinalMasterXmlLayout" /> maps them back to
///         the subfoldered paths the PC engine opens.
///     </para>
/// </summary>
public static class FinalMasterXmlArchive
{
    /// <summary>The container's fixed-width name field.</summary>
    private const int NameFieldLength = 128;

    /// <summary>Bytes preceding the first entry: version, count, payload size.</summary>
    private const int HeaderLength = 12;

    /// <summary>
    ///     True when the bytes look like a final_master_xml container (version 100, and a header
    ///     whose declared payload size matches the buffer).
    /// </summary>
    public static bool IsContainer(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderLength)
        {
            return false;
        }

        var version = BinaryPrimitives.ReadUInt32BigEndian(data);
        var payload = BinaryPrimitives.ReadUInt32BigEndian(data[8..]);
        return version is >= 1 and <= 1000 && payload == (uint)(data.Length - HeaderLength);
    }

    /// <summary>
    ///     Reads every document out of the container.
    /// </summary>
    /// <exception cref="InvalidDataException">The header or an entry runs past the buffer.</exception>
    public static IReadOnlyList<Entry> Read(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (data.Length < HeaderLength)
        {
            throw new InvalidDataException(
                $"final_master_xml.dat is {data.Length} bytes, too short for a {HeaderLength}-byte header.");
        }

        var span = data.AsSpan();
        var count = BinaryPrimitives.ReadUInt32BigEndian(span[4..]);
        if (count > 4096)
        {
            throw new InvalidDataException($"final_master_xml.dat declares {count} entries, which is not plausible.");
        }

        var entries = new List<Entry>((int)count);
        var offset = HeaderLength;

        for (var i = 0; i < count; i++)
        {
            if (offset + NameFieldLength + 4 > data.Length)
            {
                throw new InvalidDataException(
                    $"final_master_xml.dat entry {i} header runs past the end of the file.");
            }

            var nameField = span.Slice(offset, NameFieldLength);
            var terminator = nameField.IndexOf((byte)0);
            var name = Encoding.ASCII.GetString(terminator < 0 ? nameField : nameField[..terminator]);

            var length = BinaryPrimitives.ReadUInt32BigEndian(span[(offset + NameFieldLength)..]);
            var payloadStart = offset + NameFieldLength + 4;
            if (payloadStart + length > data.Length)
            {
                throw new InvalidDataException(
                    $"final_master_xml.dat entry '{name}' declares {length} bytes but only "
                    + $"{data.Length - payloadStart} remain.");
            }

            entries.Add(new Entry(name, span.Slice(payloadStart, (int)length).ToArray()));
            offset = payloadStart + (int)length;
        }

        return entries;
    }

    /// <summary>One document recovered from the container.</summary>
    /// <param name="Name">The flat name as stored, e.g. <c>hud_main_menu.xml</c>.</param>
    /// <param name="Xml">The document bytes.</param>
    public readonly record struct Entry(string Name, byte[] Xml);
}
