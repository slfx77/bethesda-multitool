using System.Text.Json;

namespace FalloutXbox360Utils.Core.Formats.Esm;

/// <summary>
///     A single field in a flattened PDB struct layout.
/// </summary>
internal sealed record PdbFieldLayout(
    string Name, int Offset, int Size, string Kind, string? Owner, string? TypeDetail);

/// <summary>
///     PDB-derived struct layout for a single FormType (e.g., TESFaction, TESObjectWEAP).
/// </summary>
internal sealed record PdbTypeLayout(
    byte FormType, string RecordCode, string ClassName, int StructSize, List<PdbFieldLayout> Fields);

/// <summary>
///     Loads and caches PDB-derived struct layouts from the embedded JSON resource.
///     Provides O(1) lookup by FormType byte for the generic runtime reader.
/// </summary>
internal static class PdbStructLayouts
{
    private static readonly Lazy<Dictionary<byte, PdbTypeLayout>> LazyLayouts = new(LoadLayouts);

    /// <summary>
    ///     All loaded type layouts indexed by FormType byte.
    /// </summary>
    public static IReadOnlyDictionary<byte, PdbTypeLayout> Layouts => LazyLayouts.Value;

    /// <summary>
    ///     Get the layout for a specific FormType, or null if not available.
    /// </summary>
    public static PdbTypeLayout? Get(byte formType)
    {
        return LazyLayouts.Value.GetValueOrDefault(formType);
    }

    /// <summary>
    ///     FormType bytes that have specialized hand-written readers and should NOT
    ///     use the generic PDB-based reader (to avoid duplicate/conflicting fields).
    /// </summary>
    private static readonly HashSet<byte> SpecializedFormTypes =
    [
        0x08, // FACT — RuntimeActorReader
        0x11, // SCPT — RuntimeScriptReader
        0x17, // TERM — RuntimeDialogueReader
        0x18, // ARMO — RuntimeItemReader
        0x1B, // CONT — RuntimeContainerReader
        0x1F, // MISC — RuntimeItemReader
        0x28, // WEAP — RuntimeItemReader
        0x29, // AMMO — RuntimeItemReader
        0x2A, // NPC_ — RuntimeActorReader
        0x2B, // CREA — RuntimeActorReader
        0x2E, // KEYM — RuntimeItemReader
        0x2F, // ALCH — RuntimeItemReader
        0x31, // NOTE — RuntimeDialogueReader
        0x33, // PROJ — RuntimeEffectReader
        0x39, // CELL — RuntimeCellReader
        0x3A, // REFR — RuntimeRefrReader
        0x3B, // ACHR — RuntimeRefrReader (via actor)
        0x3C, // ACRE — RuntimeRefrReader (via creature)
        0x41, // WRLD — RuntimeWorldReader/CellReader
        0x42, // LAND — RuntimeWorldReader
        0x45, // DIAL — RuntimeDialogueReader
        0x46, // INFO — RuntimeDialogueReader
        0x47, // QUST — RuntimeDialogueReader
        0x49, // PACK — RuntimePackageReader
        0x59  // AVIF — RuntimeActorReader
    ];

    /// <summary>
    ///     Returns true if the given FormType has a hand-written specialized reader.
    /// </summary>
    public static bool HasSpecializedReader(byte formType)
    {
        return SpecializedFormTypes.Contains(formType);
    }

    /// <summary>
    ///     Returns readable fields for a FormType — fields that the generic reader can
    ///     meaningfully extract (excludes unknown, zero-size, and TESForm base fields
    ///     that are already handled by the scan pipeline).
    /// </summary>
    public static IReadOnlyList<PdbFieldLayout> GetReadableFields(byte formType)
    {
        var layout = Get(formType);
        if (layout == null)
        {
            return [];
        }

        return layout.Fields
            .Where(f => f.Size > 0 &&
                        f.Kind is not "unknown" &&
                        // Skip TESForm header fields already extracted by scan pipeline
                        f is not { Owner: "TESForm", Name: "cFormType" or "iFormFlags" or "iFormID" or "cFormEditorID" } &&
                        // Skip BSStringT fields already resolved as top-level Name/Model/EditorID
                        f is not { Name: "cFullName", Owner: "TESFullName" } &&
                        f is not { Name: "cModel", Owner: "TESModel" })
            .ToList();
    }

    private static Dictionary<byte, PdbTypeLayout> LoadLayouts()
    {
        const string resourceName = "FalloutXbox360Utils.pdb_layouts.json";
        var assembly = typeof(PdbStructLayouts).Assembly;

        using var stream = assembly.GetManifestResourceStream(resourceName)
                           ?? throw new InvalidOperationException(
                               $"Embedded resource '{resourceName}' not found in assembly.");

        using var doc = JsonDocument.Parse(stream);
        var typesElement = doc.RootElement.GetProperty("types");
        var result = new Dictionary<byte, PdbTypeLayout>();

        foreach (var prop in typesElement.EnumerateObject())
        {
            var typeObj = prop.Value;
            var formType = typeObj.GetProperty("formType").GetByte();
            var recordCode = typeObj.GetProperty("recordCode").GetString() ?? "";
            var className = typeObj.GetProperty("className").GetString() ?? "";
            var structSize = typeObj.GetProperty("structSize").GetInt32();

            var fields = new List<PdbFieldLayout>();
            foreach (var fieldElem in typeObj.GetProperty("fields").EnumerateArray())
            {
                fields.Add(new PdbFieldLayout(
                    Name: fieldElem.GetProperty("name").GetString() ?? "",
                    Offset: fieldElem.GetProperty("offset").GetInt32(),
                    Size: fieldElem.GetProperty("size").GetInt32(),
                    Kind: fieldElem.GetProperty("kind").GetString() ?? "unknown",
                    Owner: fieldElem.TryGetProperty("owner", out var ownerProp) ? ownerProp.GetString() : null,
                    TypeDetail: fieldElem.TryGetProperty("typeDetail", out var detailProp) ? detailProp.GetString() : null));
            }

            result[formType] = new PdbTypeLayout(formType, recordCode, className, structSize, fields);
        }

        Logger.Instance.Debug($"  [PdbLayouts] Loaded {result.Count} struct layouts from embedded resource");
        return result;
    }
}
