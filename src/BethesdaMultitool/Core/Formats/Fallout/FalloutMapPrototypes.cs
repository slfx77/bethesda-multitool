namespace BethesdaMultitool.Core.Formats.Fallout;

/// <summary>
///     The prototype facts a map walk needs, read through whatever file reader the caller has — a
///     layered install mount, a single <c>MASTER.DAT</c>, or a loose tree. Two things: each item's and
///     scenery's SUBTYPE (which sizes its object record — see <see cref="FalloutMapObject" />) and each
///     prototype's art id (the fallback when a placed object's own art id does not resolve).
///     <para>
///         Resolution goes through the family's <c>.LST</c> line number, never the file name
///         (<see cref="FalloutProList" />): a quarter of the retail prototypes are not named by their id.
///         Only the item and scenery lists are read for subtypes — walls, tiles and misc carry none the
///         loader sizes by, and critters take a fixed extra.
///     </para>
/// </summary>
internal sealed class FalloutMapPrototypes
{
    private readonly Dictionary<uint, int> _subtypes;
    private readonly Dictionary<uint, uint> _frameIds;

    private FalloutMapPrototypes(Dictionary<uint, int> subtypes, Dictionary<uint, uint> frameIds)
    {
        _subtypes = subtypes;
        _frameIds = frameIds;
    }

    /// <summary>How many prototypes were read.</summary>
    public int Count => _frameIds.Count;

    /// <summary>The subtype lookup in the shape <see cref="FalloutMapFile.TryParse" /> takes.</summary>
    public int? SubtypeOf(uint protoId)
    {
        return _subtypes.TryGetValue(protoId, out var subtype) ? subtype : null;
    }

    /// <summary>The prototype's own art id, or null when the id is unknown.</summary>
    public uint? FrameIdOf(uint protoId)
    {
        return _frameIds.TryGetValue(protoId, out var fid) ? fid : null;
    }

    /// <summary>
    ///     Reads the item and scenery families (and, when <paramref name="allFamilies" />, the rest for
    ///     their art ids) through <paramref name="read" />, which takes a data-root-relative path such as
    ///     <c>PROTO/ITEMS/ITEMS.LST</c> and returns null for a file that is not there.
    /// </summary>
    public static FalloutMapPrototypes Load(Func<string, byte[]?> read, bool allFamilies = true)
    {
        ArgumentNullException.ThrowIfNull(read);

        var subtypes = new Dictionary<uint, int>();
        var frameIds = new Dictionary<uint, uint>();
        foreach (var type in Enum.GetValues<FalloutProType>())
        {
            var sized = type is FalloutProType.Item or FalloutProType.Scenery;
            if (!sized && !allFamilies)
            {
                continue;
            }

            var listPath = FalloutProList.PathFor(type);
            if (read(listPath) is not { } listBytes)
            {
                continue;
            }

            var list = FalloutProList.Parse(listBytes, listPath);
            var directory = FalloutProList.DirectoryFor(type);
            for (var index = 1; index <= list.Count; index++)
            {
                var fileName = list.Names[index - 1];
                if (read($"PROTO/{directory}/{fileName}") is not { } bytes
                    || !FalloutProFile.TryParse(bytes, fileName, out var prototype, out _))
                {
                    continue;
                }

                frameIds[prototype.ProtoId] = prototype.FrameId;
                if (sized && prototype.Subtype is { } subtype)
                {
                    subtypes[prototype.ProtoId] = subtype;
                }
            }
        }

        return new FalloutMapPrototypes(subtypes, frameIds);
    }
}
