using System.Text.Json;
using BethesdaMultitool.Core.Diagnostics;

namespace BethesdaMultitool.Core.Formats.Esm.Runtime;

/// <summary>
///     Loads and caches PDB-derived struct layouts from the embedded JSON resource.
///     Provides O(1) lookup by FormType byte for the generic runtime reader.
/// </summary>
internal static class PdbStructLayouts
{
    private static readonly Lazy<Dictionary<byte, PdbTypeLayout>> LazyLayouts = new(LoadLayouts);

    private static readonly Lazy<Dictionary<string, PdbAuxStructLayout>> LazyAuxStructs = new(LoadAuxStructs);

    private static readonly Lazy<Dictionary<string, byte>> LazyFormTypeByClassName = new(() =>
    {
        var map = new Dictionary<string, byte>(StringComparer.Ordinal);
        foreach (var layout in LazyLayouts.Value.Values)
        {
            // TryAdd, not indexer: a duplicate class name would otherwise silently pick whichever
            // FormType enumerated last.
            map.TryAdd(layout.ClassName, layout.FormType);
        }

        return map;
    });

    /// <summary>
    ///     Class name → every FormType byte a pointer declared as that class may legitimately hold:
    ///     the class's own, plus every record class that derives from it.
    ///     <para>
    ///         C++ pointer assignment is covariant, so <c>TESObjectREFR* pShooter</c> normally holds
    ///         a <c>Character</c> (ACHR) or <c>Creature</c> (ACRE), never a plain REFR — demanding
    ///         the declared class's own FormType and nothing else rejects the *correct* answer. The
    ///         derivation is read out of the layout data itself: a flattened field carries the
    ///         <c>Owner</c> that declared it, so the owner set of a record class is its ancestry.
    ///     </para>
    /// </summary>
    private static readonly Lazy<Dictionary<string, IReadOnlySet<byte>>> LazyAssignableFormTypes = new(() =>
    {
        var recordClasses = LazyFormTypeByClassName.Value;
        var map = new Dictionary<string, HashSet<byte>>(StringComparer.Ordinal);

        foreach (var layout in LazyLayouts.Value.Values)
        {
            AddAssignable(map, layout.ClassName, layout.FormType);

            foreach (var owner in layout.Fields
                         .Select(field => field.Owner)
                         .Where(owner => owner != null && !string.Equals(owner, layout.ClassName, StringComparison.Ordinal))
                         .Distinct(StringComparer.Ordinal))
            {
                // Only ancestors that are themselves record classes matter — a pointer declared as
                // TESForm or MobileObject never gets narrowed in the first place.
                if (recordClasses.ContainsKey(owner!))
                {
                    AddAssignable(map, owner!, layout.FormType);
                }
            }
        }

        return map.ToDictionary(pair => pair.Key, IReadOnlySet<byte> (pair) => pair.Value, StringComparer.Ordinal);

        static void AddAssignable(Dictionary<string, HashSet<byte>> map, string className, byte formType)
        {
            if (!map.TryGetValue(className, out var set))
            {
                set = [];
                map[className] = set;
            }

            set.Add(formType);
        }
    });

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
        0x21, // SCOL — RuntimeStaticCollectionReader (typed overlay onto StaticCollections;
        //        keeping it out of the generic sweep prevents a redundant GenericRecords
        //        copy that no writer yield exposes)
        0x23, // PWAT — RuntimePlaceableWaterReader (the parent-WATR pointer lives inside an
        //        8-byte embedded struct the generic reader only hex-dumps, so a typed
        //        read is the only way to recover it)
        0x25, // TREE — RuntimeTreeReader (SNAM's NiTPrimitiveArray and CNAM's OBJ_TREE are
        //        both embedded structs >8 bytes, which the generic reader replaces with
        //        a placeholder string instead of walking — see RuntimeTreeReader)
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
        0x42, // LAND_ID — vestigial: the PDB enum maps 0x42 to TESLand, a class the engine never
              // compiled (no layout exists), so no runtime instance carries this byte.
        0x44, // TLOD_ID — the engine registers TESObjectLAND (runtime terrain) under this slot,
              // NOT under LAND_ID; read by RuntimeWorldReader (PDB-verified 2026-08-25, both eras).
        0x45, // DIAL — RuntimeDialogueReader
        0x46, // INFO — RuntimeDialogueReader
        0x47, // QUST — RuntimeDialogueReader
        0x49, // PACK — RuntimePackageReader
        0x54, // IMAD — RuntimeImageSpaceModifierReader
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
    ///     Resolve a C++ class name (e.g. <c>SpellItem</c>) to its FormType byte. Lets a container
    ///     walker turn a <c>BSSimpleList&lt;SpellItem *&gt;</c> element type into the FormType its
    ///     members must carry, without a hand-maintained parallel table that could drift from the
    ///     layout database.
    /// </summary>
    public static bool TryGetFormTypeByClassName(string className, out byte formType)
    {
        return LazyFormTypeByClassName.Value.TryGetValue(className, out formType);
    }

    /// <summary>
    ///     Every FormType a pointer declared as <paramref name="className" /> may legitimately hold —
    ///     the class's own plus each record class deriving from it. False when the name is not a
    ///     record class, which callers must treat as "no narrowing available".
    /// </summary>
    public static bool TryGetAssignableFormTypes(string className, out IReadOnlySet<byte> formTypes)
    {
        return LazyAssignableFormTypes.Value.TryGetValue(className, out formTypes!);
    }

    /// <summary>
    ///     Record classes that are an ancestor of at least one other record class, mapped to the
    ///     FormTypes assignable to them. Exposed so a test can pin the derivation rather than
    ///     asserting against a hand-copied list that would drift from the layout database.
    /// </summary>
    internal static IEnumerable<KeyValuePair<string, IReadOnlySet<byte>>> PolymorphicRecordClasses =>
        LazyAssignableFormTypes.Value.Where(pair => pair.Value.Count > 1);

    /// <summary>
    ///     Resolve the member layout of a non-record struct — the payload behind a container or an
    ///     indirection. Returns false when this build's layout database has no entry, which callers
    ///     must treat as "decline to read" rather than falling back to hard-coded offsets: the whole
    ///     point of sourcing these from the PDB is that a different build can move them.
    /// </summary>
    public static bool TryGetAuxStruct(string className, out PdbAuxStructLayout layout)
    {
        return LazyAuxStructs.Value.TryGetValue(className, out layout!);
    }

    /// <summary>
    ///     Returns the offset of the embedded <c>TESForm</c> subobject from the complete-object base.
    ///     PDB field offsets are complete-object-relative, while runtime form maps store <c>TESForm*</c>.
    /// </summary>
    internal static int GetTesFormInteriorOffset(PdbTypeLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        var cFormType = layout.Fields.FirstOrDefault(field => field is { Owner: "TESForm", Name: "cFormType" });
        if (cFormType == null || cFormType.Offset < 4 || cFormType.Offset >= layout.StructSize)
        {
            return 0;
        }

        // cFormType is four bytes into TESForm on Xbox 360. Subtracting that local offset
        // converts its complete-object-relative PDB offset into the TESForm interior offset.
        return cFormType.Offset - 4;
    }

    /// <summary>
    ///     Returns true if the given FormType has a hand-written specialized reader.
    /// </summary>
    public static bool HasSpecializedReader(byte formType)
    {
        return SpecializedFormTypes.Contains(formType);
    }

    /// <summary>
    ///     Members that hold a nested payload — one owning class and field name each. They sit on
    ///     engine base classes rather than on any record type, so the set of record types carrying
    ///     them is decided by C++ inheritance and is read off the layout database rather than
    ///     listed by hand.
    /// </summary>
    private static readonly (string Owner, string Name)[] NestedPayloadMembers =
    [
        ("TESModel", "TextureList"), // MODT texture hashes
        ("TESModelTextureSwap", "TextureSwapList"), // MODS alternate textures
        ("BGSDestructibleObjectForm", "pData") // DEST destruction block
    ];

    private static readonly Lazy<HashSet<byte>> LazyNestedPayloadFormTypes = new(() =>
    {
        var result = new HashSet<byte>();
        foreach (var layout in LazyLayouts.Value.Values)
        {
            foreach (var field in layout.Fields)
            {
                foreach (var (owner, name) in NestedPayloadMembers)
                {
                    if (field.Owner == owner && field.Name == name)
                    {
                        result.Add(layout.FormType);
                    }
                }
            }
        }

        return result;
    });

    /// <summary>
    ///     True when this FormType's layout carries at least one nested payload member. Lets a
    ///     caller skip the struct read for the majority of FormTypes that carry none, so sweeping
    ///     every runtime entry costs a set lookup rather than a read.
    /// </summary>
    public static bool CarriesNestedPayload(byte formType)
    {
        return LazyNestedPayloadFormTypes.Value.Contains(formType);
    }

    /// <summary>Every FormType carrying a nested payload member. Diagnostics and tests.</summary>
    public static IReadOnlySet<byte> NestedPayloadFormTypes => LazyNestedPayloadFormTypes.Value;

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
        using var doc = OpenLayoutDocument();
        var typesElement = doc.RootElement.GetProperty("types");
        var result = new Dictionary<byte, PdbTypeLayout>();

        foreach (var prop in typesElement.EnumerateObject())
        {
            var typeObj = prop.Value;
            var formType = typeObj.GetProperty("formType").GetByte();
            var recordCode = typeObj.GetProperty("recordCode").GetString() ?? "";
            var className = typeObj.GetProperty("className").GetString() ?? "";
            var structSize = typeObj.GetProperty("structSize").GetInt32();

            result[formType] = new PdbTypeLayout(
                formType, recordCode, className, structSize, ReadFields(typeObj));
        }

        Logger.Instance.Debug($"  [PdbLayouts] Loaded {result.Count} struct layouts from embedded resource");
        return result;
    }

    /// <summary>
    ///     Load the auxiliary (non-record) struct layouts. Absent in layout files generated before
    ///     the exporter emitted them, so a missing section yields an empty map rather than throwing —
    ///     every consumer already has to handle "this build does not describe that struct".
    /// </summary>
    private static Dictionary<string, PdbAuxStructLayout> LoadAuxStructs()
    {
        using var doc = OpenLayoutDocument();
        var result = new Dictionary<string, PdbAuxStructLayout>(StringComparer.Ordinal);

        if (!doc.RootElement.TryGetProperty("auxStructs", out var auxElement))
        {
            Logger.Instance.Debug("  [PdbLayouts] Layout file carries no auxStructs section");
            return result;
        }

        foreach (var prop in auxElement.EnumerateObject())
        {
            var obj = prop.Value;
            var className = obj.TryGetProperty("className", out var nameProp)
                ? nameProp.GetString() ?? prop.Name
                : prop.Name;

            result[className] = new PdbAuxStructLayout(
                className, obj.GetProperty("structSize").GetInt32(), ReadFields(obj));
        }

        Logger.Instance.Debug($"  [PdbLayouts] Loaded {result.Count} auxiliary struct layouts");
        return result;
    }

    private static JsonDocument OpenLayoutDocument()
    {
        const string resourceName = "BethesdaMultitool.pdb_layouts.json";
        var assembly = typeof(PdbStructLayouts).Assembly;

        using var stream = assembly.GetManifestResourceStream(resourceName)
                           ?? throw new InvalidOperationException(
                               $"Embedded resource '{resourceName}' not found in assembly.");

        return JsonDocument.Parse(stream);
    }

    private static List<PdbFieldLayout> ReadFields(JsonElement owner)
    {
        var fields = new List<PdbFieldLayout>();
        foreach (var fieldElem in owner.GetProperty("fields").EnumerateArray())
        {
            fields.Add(new PdbFieldLayout(
                fieldElem.GetProperty("name").GetString() ?? "",
                fieldElem.GetProperty("offset").GetInt32(),
                fieldElem.GetProperty("size").GetInt32(),
                fieldElem.GetProperty("kind").GetString() ?? "unknown",
                fieldElem.TryGetProperty("owner", out var ownerProp) ? ownerProp.GetString() : null,
                fieldElem.TryGetProperty("typeDetail", out var detailProp) ? detailProp.GetString() : null));
        }

        return fields;
    }
}
