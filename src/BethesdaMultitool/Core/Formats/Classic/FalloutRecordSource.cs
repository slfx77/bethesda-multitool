using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Fallout;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Core.Vfs;

namespace BethesdaMultitool.Core.Formats.Classic;

/// <summary>
///     Synthesizes one <c>FPRO</c> record per Fallout prototype — the definition behind every object
///     the game can place, with its name and description resolved from the per-family
///     <c>PRO_*.MSG</c>.
///     <para>
///         ⚠ Prototypes are addressed through their family's <c>.LST</c>, never by file name: the
///         PID's low 24 bits are a 1-based line number there, and the line names the file. Assuming
///         <c>&lt;pid&gt;.PRO</c> is wrong for 1,151 of the 4,306 retail prototypes. See
///         <see cref="FalloutProList" />.
///     </para>
///     <para>
///         Identity needs no hashing: the PID already carries a family byte and a 24-bit index that
///         is unique within its family, so each family takes its own domain byte and the index goes
///         through unchanged. Fallout's domains start at 0x50, the block reserved for it when the
///         mobile Travels titles took 0x40-0x4F.
///     </para>
/// </summary>
internal static class FalloutRecordSource
{
    /// <summary>Domain byte of the first prototype family; the others follow in enum order.</summary>
    public const byte PrototypeDomainBase = 0x50;

    /// <summary>Record type for a prototype.</summary>
    public const string PrototypeRecordType = "FPRO";

    /// <summary>The domain byte a family's records use.</summary>
    public static byte DomainFor(FalloutProType type)
    {
        return (byte)(PrototypeDomainBase + (int)type);
    }

    /// <summary>
    ///     Reads every prototype under <paramref name="installRoot" /> and appends its record.
    ///     <para>
    ///         Both games work through the same code: their prototype layout is identical bar the
    ///         critter block, and both address prototypes through the same <c>.LST</c> line numbers
    ///         (7,650/7,650 resolve on Fallout 2, 4,306/4,306 on Fallout 1). What differs is the
    ///         PRECEDENCE, and that already lives in each profile's globs — Fallout 2 layers
    ///         <c>f2_res</c> over <c>patch*</c> over <c>critter</c> over <c>master</c>.
    ///     </para>
    /// </summary>
    public static void Populate(
        string installRoot,
        RecordCollection records,
        BethesdaGame game = BethesdaGame.Fallout1,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(installRoot);
        ArgumentNullException.ThrowIfNull(records);

        // The layered mount already gives each game's precedence from its own profile globs, so a
        // patched install resolves the way the game resolves it.
        using var files = GameFileSystem.OpenGameRoot(GameProfiles.For(game), installRoot);

        foreach (var type in Enum.GetValues<FalloutProType>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            Populate(files, type, records, cancellationToken);
        }
    }

    private static void Populate(
        LayeredGameFileSystem files,
        FalloutProType type,
        RecordCollection records,
        CancellationToken cancellationToken)
    {
        var listBytes = files.TryReadAllBytes(FalloutProList.PathFor(type));
        if (listBytes is null)
        {
            return;
        }

        var list = FalloutProList.Parse(listBytes, FalloutProList.PathFor(type));
        var messages = ReadMessages(files, type);
        var directory = FalloutProList.DirectoryFor(type);

        for (var index = 1; index <= list.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileName = list.Names[index - 1];
            var bytes = files.TryReadAllBytes($"PROTO/{directory}/{fileName}");
            if (bytes is null ||
                !FalloutProFile.TryParse(bytes, fileName, out var prototype, out _))
            {
                continue;
            }

            records.GenericRecords.Add(Build(prototype, index, messages));
        }
    }

    /// <summary>The per-family message file, or null when it is not shipped.</summary>
    private static FalloutMessageFile? ReadMessages(LayeredGameFileSystem files, FalloutProType type)
    {
        var name = type switch
        {
            FalloutProType.Item => "PRO_ITEM",
            FalloutProType.Critter => "PRO_CRIT",
            FalloutProType.Scenery => "PRO_SCEN",
            FalloutProType.Wall => "PRO_WALL",
            FalloutProType.Tile => "PRO_TILE",
            FalloutProType.Misc => "PRO_MISC",
            _ => null
        };

        if (name is null)
        {
            return null;
        }

        var path = $"TEXT/ENGLISH/GAME/{name}.MSG";
        var bytes = files.TryReadAllBytes(path);
        return bytes is null ? null : FalloutMessageFile.Parse(bytes, path);
    }

    /// <summary>The record form of one prototype.</summary>
    public static GenericEsmRecord Build(FalloutProFile prototype, int index, FalloutMessageFile? messages)
    {
        ArgumentNullException.ThrowIfNull(prototype);

        var name = messages?.Find((int)prototype.TextId);
        var description = messages?.Find((int)prototype.DescriptionTextId);

        var fields = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Family"] = prototype.Type.ToString(),
            ["ProtoId"] = $"0x{prototype.ProtoId:X8}",
            ["ListIndex"] = prototype.ListIndex,
            ["File"] = prototype.Name,
            ["TextId"] = prototype.TextId,
            ["Description"] = description,
            ["Art"] = $"0x{prototype.FrameId:X8}",
            ["LightDistance"] = prototype.LightDistance,
            ["LightIntensity"] = prototype.LightIntensity,
            ["Flags"] = $"0x{prototype.Flags:X8}"
        };

        if (prototype.ExtendedFlags is { } extended)
        {
            fields["ExtendedFlags"] = $"0x{extended:X8}";
        }

        // A prototype with no script field at all is a different statement from one whose script is
        // -1, so the two are not collapsed: tiles and misc simply have no entry here.
        if (prototype.ScriptId is { } script)
        {
            fields["Script"] = prototype.HasScript ? script : "none";
        }

        if (prototype.Subtype is { } subtype)
        {
            fields["Subtype"] = subtype;
        }

        if (prototype.Material is { } material)
        {
            fields["Material"] = material;
        }

        return new GenericEsmRecord
        {
            FormId = ClassicFormIdScheme.Compose(DomainFor(prototype.Type), (uint)index),
            RecordType = PrototypeRecordType,
            EditorId = ClassicRecordNaming.ToEditorId(
                $"{FalloutProList.DirectoryFor(prototype.Type)}_{index:D5}"),
            FullName = name is null ? null : ClassicRecordNaming.Summarize(name),
            Fields = fields
        };
    }
}
