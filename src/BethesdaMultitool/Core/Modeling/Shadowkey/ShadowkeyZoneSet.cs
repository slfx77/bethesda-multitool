using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BethesdaMultitool.Core.Formats.Travels.Shadowkey;
using Slfx77.Multitool.Core.Assets;
using Slfx77.Multitool.Core.Models;
using Slfx77.Multitool.Core.Models.Sources;

namespace BethesdaMultitool.Core.Modeling.Shadowkey;

/// <summary>One <c>.ent</c> placement and where its chain lands (<see cref="ShadowkeyZoneSet.Resolve" />).</summary>
/// <param name="Index">The row's index in the <c>.ent</c>.</param>
/// <param name="Entity">The raw row.</param>
/// <param name="Definition">The <c>entities.txt</c> row, or null when the id is absent.</param>
/// <param name="Slot">The pack slot the chain names (the model index), or null when the id is absent.</param>
/// <param name="IsResolved">
///     True when the slot is resident in the zone's model list and holds a record in the pack (8,258 of 8,258 retail).
/// </param>
internal sealed record ShadowkeyZonePlacement(
    int Index,
    ShadowkeyEntity Entity,
    ShadowkeyEntityDef? Definition,
    int? Slot,
    bool IsResolved);

/// <summary>A companion file's facts for the zone header row.</summary>
/// <param name="Size">The file's byte length.</param>
/// <param name="Sha256">The file's lowercase SHA-256.</param>
internal sealed record ShadowkeyZoneFileFacts(long Size, string Sha256);

/// <summary>
///     One zone's file set, read once through the read context's companion resolver and parsed by the BMT readers the
///     viewer uses (cut-2 plan section 4.1, decision D1(c)): the <c>.zmp</c> the item names, its companions by stem
///     (<c>.zcp .zsk .ztx .zlu .zfg .ent .sur .zon .pth .stn .pal</c>, and <c>.sta</c> when present), the zone's model
///     list <c>&lt;stem&gt;_models.txt</c> and the application directory's globals (<c>models.idx</c>,
///     <c>models.huge</c>, <c>entities.txt</c>, and <c>models.txt</c> for the slot names when present).
/// </summary>
/// <remarks>
///     <para>
///         Every compressed file must inflate to its declared length with the stream ending at the file's last byte (the
///         trailing Adler-32 is checked, because .NET's inflater ignores bytes after a stream). A required companion that
///         does not resolve, or resolves ambiguously, throws <see cref="InvalidDataException" /> naming it. Retail: the
///         21 zones carry every required file; only azra carries a <c>.sta</c>.
///     </para>
///     <para>
///         The <c>.ztx</c> textures are validated by <see cref="ShadowkeyTextureBank.Parse" /> and kept as their stored
///         bytes (bottom row first) for the images' originals; the sky payload is kept inflated for
///         <see cref="ShadowkeyMesh.Parse" /> with the variant <see cref="ShadowkeyMesh.DetectTextureHeader" /> chooses.
///     </para>
/// </remarks>
internal sealed class ShadowkeyZoneSet
{
    /// <summary>The largest companion the set reads (Assumed; <c>models.huge</c> is 4,907,880 bytes).</summary>
    public const int MaximumCompanionBytes = 64 * 1024 * 1024;

    /// <summary>The companion families a zone requires, besides the <c>.zmp</c> itself.</summary>
    public static IReadOnlyList<string> RequiredFamilies { get; } =
        [".zcp", ".zsk", ".ztx", ".zlu", ".zfg", ".ent", ".sur", ".zon", ".pth", ".stn", ".pal"];

    private readonly Dictionary<string, byte[]> _inflated = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AssetReference> _references = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Name, ShadowkeyZoneFileFacts Facts)> _files = [];

    private ShadowkeyZoneSet(string stem)
    {
        Stem = stem;
    }

    /// <summary>The zone's stem (the <c>.zmp</c> file name without its extension).</summary>
    public string Stem { get; }

    /// <summary>The parsed grid.</summary>
    public ShadowkeyZoneMap Map { get; private set; } = null!;

    /// <summary>The parsed cell prototypes, in table order.</summary>
    public IReadOnlyList<ShadowkeyCellPrototype> Prototypes => PrototypeTable.Records;

    /// <summary>The parsed prototype table.</summary>
    public ShadowkeyCellPrototypes PrototypeTable { get; private set; } = null!;

    /// <summary>The <c>.zmp</c> bytes' SHA-256 (the document's provenance).</summary>
    public string ZmpSha256 { get; private set; } = string.Empty;

    /// <summary>The <c>.ztx</c> textures' stored index bytes (bottom row first), in bank order.</summary>
    public IReadOnlyList<byte[]> TextureBytes { get; private set; } = [];

    /// <summary>The 768 bytes of the <c>.pal</c>.</summary>
    public byte[] PaletteBytes { get; private set; } = [];

    /// <summary>The parsed fog table.</summary>
    public ShadowkeyFogTable Fog { get; private set; } = null!;

    /// <summary>The <c>.sur</c> rows.</summary>
    public IReadOnlyList<ShadowkeySurface> Surfaces { get; private set; } = [];

    /// <summary>The <c>.ent</c> placements.</summary>
    public ShadowkeyEntityList Placements { get; private set; } = null!;

    /// <summary>The <c>.sta</c> records, or null when the zone has no <c>.sta</c>.</summary>
    public ShadowkeyEntityList? Sta { get; private set; }

    /// <summary>The <c>.zon</c> trigger rectangles.</summary>
    public IReadOnlyList<ShadowkeyTriggerZone> Triggers { get; private set; } = [];

    /// <summary>The <c>.pth</c> paths.</summary>
    public IReadOnlyList<ShadowkeyPath> Paths { get; private set; } = [];

    /// <summary>The <c>.stn</c> lock rows.</summary>
    public IReadOnlyList<ShadowkeyLockEntry> Locks { get; private set; } = [];

    /// <summary>The <c>entities.txt</c> table.</summary>
    public ShadowkeyEntityTable Entities { get; private set; } = null!;

    /// <summary>The zone's model list (<c>&lt;stem&gt;_models.txt</c>), the residency mask over the pack.</summary>
    public ShadowkeyModelTable ZoneModels { get; private set; } = null!;

    /// <summary>The parsed pack.</summary>
    public ShadowkeyModelPack Pack { get; private set; } = null!;

    /// <summary>The <c>models.huge</c> occurrence (for the placed records' locations).</summary>
    public AssetReference PackReference { get; private set; } = null!;

    /// <summary>Every file read, in read order, with its size and SHA-256.</summary>
    public IReadOnlyList<(string Name, ShadowkeyZoneFileFacts Facts)> FileFacts => _files;

    /// <summary>The inflated payload of a compressed family (for example <c>.zsk</c>).</summary>
    /// <exception cref="KeyNotFoundException">The family is not a compressed one of this set.</exception>
    public byte[] Inflated(string family)
    {
        return _inflated[family];
    }

    /// <summary>The location of a whole file of the set (its first to last byte), or null when it was not read.</summary>
    public SceneSourceLocation? Location(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        if (!_references.TryGetValue(fileName, out var reference))
        {
            return null;
        }

        var facts = _files.First(file => string.Equals(file.Name, fileName, StringComparison.OrdinalIgnoreCase)).Facts;
        return new SceneSourceLocation(reference.SourceId, fileName, 0, facts.Size, reference);
    }

    /// <summary>The location of a range of a compressed file's inflated payload (element <c>inflated:&lt;file&gt;</c>).</summary>
    public SceneSourceLocation? InflatedLocation(string family, long offset, long length)
    {
        return _references.TryGetValue(Stem + family, out var reference)
            ? new SceneSourceLocation(reference.SourceId, "inflated:" + Stem + family, offset, length, reference)
            : null;
    }

    /// <summary>The reference of a file of the set, or null when it was not read.</summary>
    public AssetReference? Reference(string fileName)
    {
        return _references.GetValueOrDefault(fileName);
    }

    /// <summary>
    ///     Reads and parses a zone set: <paramref name="zmp" /> is the item's bytes; the companions come from the read
    ///     context's resolver.
    /// </summary>
    /// <exception cref="InvalidDataException">A file is missing, ambiguous, corrupt or inconsistent with the grid.</exception>
    public static ShadowkeyZoneSet Read(ModelSourceItem item, ModelReadContext context, byte[] zmp,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(zmp);
        var stem = Path.GetFileNameWithoutExtension(item.Reference.Path);
        var set = new ShadowkeyZoneSet(stem);
        set.Add(stem + ".zmp", item.Reference, zmp);
        set.ZmpSha256 = set._files[0].Facts.Sha256;
        set.Map = ShadowkeyZoneMap.Parse(set.InflateExactly(".zmp", zmp), stem + ".zmp");

        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var family in RequiredFamilies)
        {
            cancellationToken.ThrowIfCancellationRequested();
            files[family] = set.ReadCompanion(item, context, stem + family, required: true, cancellationToken)!;
        }

        var sta = set.ReadCompanion(item, context, stem + ".sta", required: false, cancellationToken);

        set.PrototypeTable = ShadowkeyCellPrototypes.Parse(set.InflateExactly(".zcp", files[".zcp"]), stem + ".zcp");
        set.PrototypeTable.ValidateAgainst(set.Map, stem + ".zcp");
        set.InflateExactly(".zsk", files[".zsk"]);
        var ztx = set.InflateExactly(".ztx", files[".ztx"]);
        var bank = ShadowkeyTextureBank.Parse(ztx, stem + ".ztx");
        var textures = new byte[bank.Count][];
        for (var n = 0; n < textures.Length; n++)
        {
            textures[n] = ztx.AsSpan(ShadowkeyTextureBank.HeaderLength + n * ShadowkeyTextureBank.TextureLength,
                ShadowkeyTextureBank.TextureLength).ToArray();
        }

        set.TextureBytes = textures;
        ShadowkeyLightTable.Parse(set.InflateExactly(".zlu", files[".zlu"]), stem + ".zlu");
        set.Fog = ShadowkeyFogTable.Parse(set.InflateExactly(".zfg", files[".zfg"]), stem + ".zfg");
        ShadowkeyZonePalette.Parse(files[".pal"], stem + ".pal");
        set.PaletteBytes = files[".pal"];
        set.Surfaces = ShadowkeyZoneFiles.ParseSur(files[".sur"], stem + ".sur");
        foreach (var surface in set.Surfaces)
        {
            if (surface.TextureIndex >= bank.Count)
            {
                throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture,
                    $"'{stem}.sur': a surface names texture {surface.TextureIndex}, but the .ztx holds {bank.Count}."));
            }
        }

        set.Placements = ShadowkeyZoneFiles.ParseEnt(files[".ent"], stem + ".ent");
        set.Triggers = ShadowkeyZoneFiles.ParseZon(files[".zon"], stem + ".zon");
        set.Paths = ShadowkeyZoneFiles.ParsePth(files[".pth"], stem + ".pth");
        set.Locks = ShadowkeyZoneFiles.ParseStn(files[".stn"], stem + ".stn");
        set.Sta = sta is null ? null : ShadowkeyZoneFiles.ParseSta(sta, stem + ".sta");

        var zoneModels = set.ReadCompanion(item, context, stem + "_models.txt", required: true, cancellationToken)!;
        set.ZoneModels = ShadowkeyTextTables.ParseModels(zoneModels, stem + "_models.txt");
        var entities = set.ReadCompanion(item, context, "entities.txt", required: true, cancellationToken)!;
        set.Entities = ShadowkeyTextTables.ParseEntities(entities, "entities.txt");
        var index = set.ReadCompanion(item, context, "models.idx", required: true, cancellationToken)!;
        var pack = set.ReadCompanion(item, context, "models.huge", required: true, cancellationToken)!;
        var names = set.ReadCompanion(item, context, "models.txt", required: false, cancellationToken);
        set.Pack = ShadowkeyModelPack.Parse(index, pack, names is null ? null : Encoding.Latin1.GetString(names),
            "models.huge");
        set.PackReference = set._references["models.huge"];
        return set;
    }

    /// <summary>Walks every placement's chain (entity id, <c>entities.txt</c>, the zone's model list, the pack).</summary>
    public IReadOnlyList<ShadowkeyZonePlacement> Resolve()
    {
        var placements = new List<ShadowkeyZonePlacement>(Placements.Entities.Count);
        for (var i = 0; i < Placements.Entities.Count; i++)
        {
            var entity = Placements.Entities[i];
            var resolution = ShadowkeyTextTables.Resolve(entity.EntityId, Entities, ZoneModels);
            int? slot = resolution.Entity?.ModelIndex;
            var resolved = resolution.IsResident && slot is { } s && s >= 0 && s < Pack.Count && !Pack.Entries[s].IsEmpty;
            placements.Add(new ShadowkeyZonePlacement(i, entity, resolution.Entity, slot, resolved));
        }

        return placements.AsReadOnly();
    }

    private void Add(string name, AssetReference reference, byte[] bytes)
    {
        _references[name] = reference;
        _files.Add((name, new ShadowkeyZoneFileFacts(bytes.LongLength, Convert.ToHexStringLower(SHA256.HashData(bytes)))));
    }

    /// <summary>
    ///     Inflates one compressed file, requiring the declared length and the stream to end at the file's last byte (the
    ///     trailing big-endian Adler-32 must be the payload's).
    /// </summary>
    private byte[] InflateExactly(string family, byte[] bytes)
    {
        var name = Stem + family;
        var payload = ShadowkeyCompressedFile.Inflate(bytes, name);
        if (bytes.Length < ShadowkeyCompressedFile.MinimumLength + 4 ||
            BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(bytes.Length - 4)) !=
            ShadowkeyZoneModelProbe.Adler32(payload))
        {
            throw new InvalidDataException(
                $"'{name}': the zlib stream does not end at the file's last byte (its trailing Adler-32 is not the payload's).");
        }

        _inflated[family] = payload;
        return payload;
    }

    private byte[]? ReadCompanion(ModelSourceItem item, ModelReadContext context, string name, bool required,
        CancellationToken cancellationToken)
    {
        var found = context.CompanionResolver(item, name, cancellationToken).AsTask().GetAwaiter().GetResult();
        if (found.Count == 0)
        {
            if (required)
            {
                throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture,
                    $"'{Stem}.zmp': the zone needs '{name}' beside it, and the companion resolver found none."));
            }

            return null;
        }

        if (found.Count > 1)
        {
            throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture,
                $"'{Stem}.zmp': '{name}' resolves to {found.Count} files ({string.Join(", ", found.Select(f => f.Reference.Path))}); the zone needs exactly one."));
        }

        var companion = found[0];
        if (companion.Length > MaximumCompanionBytes)
        {
            throw new NotSupportedException(string.Create(CultureInfo.InvariantCulture,
                $"'{name}' declares {companion.Length} bytes, more than the zone reader's {MaximumCompanionBytes}-byte companion budget."));
        }

        using var stream = companion.OpenReadAsync(cancellationToken).AsTask().GetAwaiter().GetResult();
        var bytes = ShadowkeyMeshModelReader.ReadBytes(stream, MaximumCompanionBytes, cancellationToken);
        Add(name, companion.Reference, bytes);
        return bytes;
    }
}
