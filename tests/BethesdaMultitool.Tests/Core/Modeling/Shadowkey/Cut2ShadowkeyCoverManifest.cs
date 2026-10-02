using System.Text.Json.Nodes;
using BethesdaMultitool.Tests.Core.Modeling.RealAsset;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Modeling.Shadowkey;

/// <summary>
///     The checked-in cut-2 Shadowkey cover manifest (plan <c>docs/design/cut2-shadowkey-reader-plan-20260928.md</c>,
///     section 9): the pairwise MILP joint cover of the pack slots and the zones, their edge rows and the decline
///     controls, every row pinned by size and SHA-256 and written by <c>tools/scripts/gate2/shadowkey_cover.py manifest</c>.
///     It is read from the repository like the other cover manifests.
/// </summary>
/// <remarks>
///     <see cref="Parse" /> refuses another schema, a cover that left an item uncovered, an unknown role or kind, a blank
///     or repeated row name, a malformed SHA-256, a non-positive size (an empty slot must be 0 bytes with the empty
///     digest), a slot entry that is not <c>NNN_name</c> for its slot, a repeated slot or stem, a zone without its
///     <c>.zmp</c> pin or whose row digest is not that pin, a missing pack pin, and a decline control that does not
///     expect NotAModel. A generator that stopped pinning any of that fails the load instead of weakening a Bucket-B
///     check. The class is public because xUnit resolves <c>MemberData</c> members reflectively.
/// </remarks>
public static class Cut2ShadowkeyCoverManifest
{
    /// <summary>The manifest's repository-relative path.</summary>
    public const string RelativePath =
        "tests/BethesdaMultitool.Tests/Core/Modeling/Samples/cut2-shadowkey-cover-manifest.json";

    /// <summary>The schema the manifest must declare.</summary>
    public const string Schema = "cut2-shadowkey-cover/1";

    /// <summary>The SHA-256 of no bytes (an empty slot's digest).</summary>
    public const string EmptySha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    /// <summary>The pack files every manifest pins.</summary>
    public static IReadOnlyList<string> PackFiles { get; } = ["models.idx", "models.huge", "models.txt", "entities.txt"];

    private static readonly Lazy<Cut2ShadowkeyCover> LazyCover = new(Load);

    /// <summary>The manifest's absolute path, or null when it cannot be located.</summary>
    public static string? Path =>
        Cut1aCoverManifest.RepoFile(RelativePath) is { } path && File.Exists(path) ? path : null;

    /// <summary>The parsed manifest (empty when the file is absent).</summary>
    internal static Cut2ShadowkeyCover Cover => LazyCover.Value;

    /// <summary>One theory row per slot row: (row name, SHA-256).</summary>
    public static TheoryData<string, string> SlotRows()
    {
        var rows = new TheoryData<string, string>();
        foreach (var row in Cover.Slots)
        {
            rows.Add(row.Name, row.Sha256);
        }

        return rows;
    }

    /// <summary>One theory row per zone row: (row name, <c>.zmp</c> SHA-256).</summary>
    public static TheoryData<string, string> ZoneRows()
    {
        var rows = new TheoryData<string, string>();
        foreach (var row in Cover.Zones)
        {
            rows.Add(row.Name, row.Sha256);
        }

        return rows;
    }

    /// <summary>One theory row per slot row whose cover cell has several skins: (row name, SHA-256).</summary>
    public static TheoryData<string, string> MultiSkinSlotRows()
    {
        var rows = new TheoryData<string, string>();
        foreach (var row in Cover.Slots.Where(static row => IsMultiSkin(row.Cell)))
        {
            rows.Add(row.Name, row.Sha256);
        }

        return rows;
    }

    /// <summary>True when a slot cell's <c>skins</c> class is not <c>1</c> (the probe's classes are 1, 2-4 and 5+).</summary>
    internal static bool IsMultiSkin(string cell)
    {
        ArgumentNullException.ThrowIfNull(cell);
        return cell.Split('|').Any(static part => part.StartsWith("skins=", StringComparison.Ordinal) && part != "skins=1");
    }

    /// <summary>One theory row per decline control: (row name, SHA-256).</summary>
    public static TheoryData<string, string> DeclineRows()
    {
        var rows = new TheoryData<string, string>();
        foreach (var row in Cover.Declines)
        {
            rows.Add(row.Name, row.Sha256);
        }

        return rows;
    }

    /// <summary>Parses and validates a manifest (see the remarks).</summary>
    /// <exception cref="InvalidDataException">The manifest breaks one of the rules.</exception>
    internal static Cut2ShadowkeyCover Parse(string json)
    {
        var manifest = JsonNode.Parse(json)?.AsObject() ?? throw new InvalidDataException("The manifest is empty.");
        if (!string.Equals(manifest["schema"]?.GetValue<string>(), Schema, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"The manifest is not schema {Schema}.");
        }

        if (manifest["uncoveredItems"]?.AsArray() is not { Count: 0 })
        {
            throw new InvalidDataException("The manifest's cover left items uncovered (or does not say).");
        }

        var pack = new Dictionary<string, Cut2ShadowkeyPin>(StringComparer.Ordinal);
        foreach (var name in PackFiles)
        {
            pack[name] = Pin(manifest["pack"]?[name]?.AsObject()
                             ?? throw new InvalidDataException($"The manifest does not pin {name}."), name);
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        var slots = manifest["slots"]!.AsArray().Select(node => SlotRow(node!.AsObject(), names)).ToList();
        var zones = manifest["zones"]!.AsArray().Select(node => ZoneRow(node!.AsObject(), names)).ToList();
        var declines = manifest["declineControls"]!.AsArray().Select(node => DeclineRow(node!.AsObject(), names)).ToList();
        RequireUnique(slots.Select(static s => s.Slot.ToString(System.Globalization.CultureInfo.InvariantCulture)), "slot");
        RequireUnique(slots.Select(static s => s.Sha256), "slot digest");
        RequireUnique(zones.Select(static z => z.Stem), "zone stem");
        return new Cut2ShadowkeyCover(pack, slots.AsReadOnly(), zones.AsReadOnly(), declines.AsReadOnly());
    }

    /// <summary>The slot row with the given name.</summary>
    internal static Cut2ShadowkeySlotRow RequireSlot(string name)
    {
        return Cover.Slots.SingleOrDefault(row => row.Name == name) ??
               throw new InvalidOperationException($"The cut-2 Shadowkey manifest has no slot row '{name}'.");
    }

    /// <summary>The zone row with the given name.</summary>
    internal static Cut2ShadowkeyZoneRow RequireZone(string name)
    {
        return Cover.Zones.SingleOrDefault(row => row.Name == name) ??
               throw new InvalidOperationException($"The cut-2 Shadowkey manifest has no zone row '{name}'.");
    }

    /// <summary>The decline row with the given name.</summary>
    internal static Cut2ShadowkeyDeclineRow RequireDecline(string name)
    {
        return Cover.Declines.SingleOrDefault(row => row.Name == name) ??
               throw new InvalidOperationException($"The cut-2 Shadowkey manifest has no decline row '{name}'.");
    }

    private static Cut2ShadowkeySlotRow SlotRow(JsonObject row, HashSet<string> names)
    {
        var name = Name(row, names);
        var role = row["role"]!.GetValue<string>();
        if (role is not ("cover" or "edge"))
        {
            throw new InvalidDataException($"{name}: role '{role}' is not cover or edge.");
        }

        var slot = row["slot"]!.GetValue<int>();
        var entry = row["entry"]!.GetValue<string>();
        if (!entry.StartsWith(slot.ToString("000", System.Globalization.CultureInfo.InvariantCulture) + "_",
                StringComparison.Ordinal))
        {
            throw new InvalidDataException($"{name}: entry '{entry}' is not named for slot {slot}.");
        }

        var pin = Pin(row, name);
        return new Cut2ShadowkeySlotRow(role, name, slot, entry, row["offset"]!.GetValue<long>(), pin.Size, pin.Sha256,
            [.. row["alsoInSlots"]!.AsArray().Select(static s => s!.GetValue<int>())], row["cell"]!.GetValue<string>(),
            [.. row["tags"]!.AsArray().Select(static t => t!.GetValue<string>())],
            [.. row["edges"]!.AsArray().Select(static e => e!.GetValue<string>())]);
    }

    private static Cut2ShadowkeyZoneRow ZoneRow(JsonObject row, HashSet<string> names)
    {
        var name = Name(row, names);
        var role = row["role"]!.GetValue<string>();
        if (role is not ("cover" or "edge"))
        {
            throw new InvalidDataException($"{name}: role '{role}' is not cover or edge.");
        }

        var stem = row["stem"]!.GetValue<string>();
        var files = new Dictionary<string, Cut2ShadowkeyPin>(StringComparer.Ordinal);
        foreach (var (file, node) in row["files"]!.AsObject())
        {
            files[file] = Pin(node!.AsObject(), $"{name} {file}");
        }

        if (!files.TryGetValue(stem + ".zmp", out var zmp) || zmp.Sha256 != row["sha256"]!.GetValue<string>() ||
            zmp.Size != row["size"]!.GetValue<long>())
        {
            throw new InvalidDataException($"{name}: the row digest is not its .zmp pin.");
        }

        return new Cut2ShadowkeyZoneRow(role, name, stem, zmp.Sha256, zmp.Size, row["cell"]!.GetValue<string>(),
            [.. row["tags"]!.AsArray().Select(static t => t!.GetValue<string>())],
            [.. row["edges"]!.AsArray().Select(static e => e!.GetValue<string>())], files);
    }

    private static Cut2ShadowkeyDeclineRow DeclineRow(JsonObject row, HashSet<string> names)
    {
        var name = Name(row, names);
        if (!string.Equals(row["expect"]?.GetValue<string>(), "NotAModel", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Decline control '{name}' does not expect NotAModel.");
        }

        var kind = row["kind"]!.GetValue<string>();
        var sha = row["sha256"]!.GetValue<string>();
        var size = row["size"]!.GetValue<long>();
        switch (kind)
        {
            case Cut2ShadowkeyDeclineRow.EmptySlotKind:
                if (size != 0 || sha != EmptySha256)
                {
                    throw new InvalidDataException($"{name}: an empty slot is 0 bytes with the empty digest.");
                }

                return new Cut2ShadowkeyDeclineRow(name, kind, row["slot"]!.GetValue<int>(), null, 0, sha, null, null);
            case Cut2ShadowkeyDeclineRow.FileKind:
                var pin = Pin(row, name);
                return new Cut2ShadowkeyDeclineRow(name, kind, null, row["path"]!.GetValue<string>(), pin.Size, pin.Sha256,
                    row["meshReason"]?.GetValue<string>(), row["zoneReason"]?.GetValue<string>());
            default:
                throw new InvalidDataException($"{name}: kind '{kind}' is not empty-slot or file.");
        }
    }

    private static string Name(JsonObject row, HashSet<string> names)
    {
        var name = row["name"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(name) || !names.Add(name))
        {
            throw new InvalidDataException($"A row name is blank or appears twice ('{name}').");
        }

        return name;
    }

    private static Cut2ShadowkeyPin Pin(JsonObject row, string label)
    {
        var sha = row["sha256"]?.GetValue<string>() ?? string.Empty;
        var size = row["size"]?.GetValue<long>() ?? 0;
        if (sha.Length != 64 || !sha.All(char.IsAsciiHexDigitLower))
        {
            throw new InvalidDataException($"{label}: malformed SHA-256 '{sha}'.");
        }

        if (size <= 0)
        {
            throw new InvalidDataException($"{label}: size {size} is not positive.");
        }

        return new Cut2ShadowkeyPin(size, sha);
    }

    private static void RequireUnique(IEnumerable<string> values, string what)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (!seen.Add(value))
            {
                throw new InvalidDataException($"The manifest repeats the {what} '{value}'.");
            }
        }
    }

    private static Cut2ShadowkeyCover Load()
    {
        return Path is { } path
            ? Parse(File.ReadAllText(path))
            : new Cut2ShadowkeyCover(new Dictionary<string, Cut2ShadowkeyPin>(), [], [], []);
    }
}

/// <summary>The parsed cut-2 Shadowkey cover manifest.</summary>
/// <param name="Pack">The pack pins (<c>models.idx</c>, <c>models.huge</c>, <c>models.txt</c>, <c>entities.txt</c>).</param>
/// <param name="Slots">The slot rows, cover then edge.</param>
/// <param name="Zones">The zone rows, cover then edge.</param>
/// <param name="Declines">The decline controls, empty slots then tree files.</param>
internal sealed record Cut2ShadowkeyCover(
    IReadOnlyDictionary<string, Cut2ShadowkeyPin> Pack,
    IReadOnlyList<Cut2ShadowkeySlotRow> Slots,
    IReadOnlyList<Cut2ShadowkeyZoneRow> Zones,
    IReadOnlyList<Cut2ShadowkeyDeclineRow> Declines);
