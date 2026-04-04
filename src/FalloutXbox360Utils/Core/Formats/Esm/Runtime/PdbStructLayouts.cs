using System.Text.Json;

namespace FalloutXbox360Utils.Core.Formats.Esm;

/// <summary>
///     Loads and caches PDB-derived struct layouts from the embedded JSON resource.
///     Provides O(1) lookup by FormType byte for the generic runtime reader.
/// </summary>
internal static class PdbStructLayouts
{
    private static readonly Lazy<Dictionary<byte, PdbTypeLayout>> LazyLayouts = new(LoadLayouts);

    /// <summary>
    ///     FormType bytes that have specialized hand-written readers and should NOT
    ///     use the generic PDB-based reader (to avoid duplicate/conflicting fields).
    /// </summary>
    private static readonly HashSet<byte> SpecializedFormTypes =
    [
        0x08, // FACT — RuntimeActorReader
        0x11, // SCPT — RuntimeScriptReader
        0x15, // ACTI — RuntimeWorldObjectReader
        0x17, // TERM — RuntimeDialogueReader
        0x18, // ARMO — RuntimeItemReader
        0x1B, // CONT — RuntimeContainerReader
        0x1C, // DOOR — RuntimeWorldObjectReader
        0x1E, // LIGH — RuntimeWorldObjectReader
        0x1F, // MISC — RuntimeItemReader
        0x20, // STAT — RuntimeWorldObjectReader
        0x27, // FURN — RuntimeWorldObjectReader
        0x28, // WEAP — RuntimeItemReader
        0x29, // AMMO — RuntimeItemReader
        0x2A, // NPC_ — RuntimeActorReader
        0x2B, // CREA — RuntimeActorReader
        0x2C, // LVLC — RuntimeCollectionReader
        0x2D, // LVLN — RuntimeCollectionReader
        0x2E, // KEYM — RuntimeItemReader
        0x2F, // ALCH — RuntimeItemReader
        0x31, // NOTE — RuntimeDialogueReader
        0x33, // PROJ — RuntimeEffectReader
        0x34, // LVLI — RuntimeCollectionReader
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
        0x55, // FLST — RuntimeCollectionReader
        0x59, // AVIF — RuntimeActorReader
        0x66 // MUSC — RuntimeMusicTypeReader
    ];

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
                        f is not
                        {
                            Owner: "TESForm", Name: "cFormType" or "iFormFlags" or "iFormID" or "cFormEditorID"
                        } &&
                        // Skip BSStringT fields already resolved as top-level Name/Model/EditorID
                        f is not { Name: "cFullName", Owner: "TESFullName" } &&
                        f is not { Name: "cModel", Owner: "TESModel" })
            .ToList();
    }

    /// <summary>
    ///     Returns BSStringT fields for a FormType — used by string claim extractors
    ///     to identify char* pointer fields within runtime TESForm structs.
    /// </summary>
    public static IReadOnlyList<PdbFieldLayout> GetBSStringTFields(byte formType)
    {
        var layout = Get(formType);
        if (layout == null)
        {
            return [];
        }

        return layout.Fields
            .Where(f => f.Kind == "struct" && f.TypeDetail != null &&
                        f.TypeDetail.Contains("BSStringT", StringComparison.Ordinal))
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
                    fieldElem.GetProperty("name").GetString() ?? "",
                    fieldElem.GetProperty("offset").GetInt32(),
                    fieldElem.GetProperty("size").GetInt32(),
                    fieldElem.GetProperty("kind").GetString() ?? "unknown",
                    fieldElem.TryGetProperty("owner", out var ownerProp) ? ownerProp.GetString() : null,
                    fieldElem.TryGetProperty("typeDetail", out var detailProp) ? detailProp.GetString() : null));
            }

            result[formType] = new PdbTypeLayout(formType, recordCode, className, structSize, fields);
        }

        Logger.Instance.Debug($"  [PdbLayouts] Loaded {result.Count} struct layouts from embedded resource");
        return result;
    }
}
