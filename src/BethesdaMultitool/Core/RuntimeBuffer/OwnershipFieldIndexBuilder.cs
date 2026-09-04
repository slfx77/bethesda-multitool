using BethesdaMultitool.Core.Formats.Esm.Runtime;

namespace BethesdaMultitool.Core.RuntimeBuffer;

/// <summary>
///     Builds PDB-based field indices used by BSStringT reverse lookup and vtable-based
///     reverse lookup strategies for second-pass ownership resolution.
/// </summary>
internal static class OwnershipFieldIndexBuilder
{
    /// <summary>
    ///     Build all three PDB-based field indices in a single pass over the layout database.
    ///     Returns: (bsStringTFieldIndex, classNameFieldIndex, charPointerFieldIndex).
    ///     <para>
    ///         The class-name indices are built from BOTH sources the database carries: the 116
    ///         FormType record layouts and the 449 auxiliary struct layouts. Until 2026-09-04 only
    ///         the former were walked, so every Gamebryo, Havok and engine class the PDB fully
    ///         describes was invisible here — and a hand-written table
    ///         (<see cref="BuildNiObjectFieldIndex" />) guessed at a subset of those same offsets,
    ///         sometimes wrongly.
    ///     </para>
    ///     <para>
    ///         The <c>bsStringTFieldIndex</c> is deliberately left exactly as it was: it is keyed by
    ///         FormType and consumed by the TESForm reverse lookup, whose BSStringT length validation
    ///         only makes sense for genuine BSStringT members.
    ///     </para>
    /// </summary>
    internal static (
        Dictionary<(byte FormType, int FieldOffset), (string RecordCode, string FieldLabel)> BsStringT,
        Dictionary<string, (byte FormType, List<(int Offset, string Label)> Fields)> ClassName,
        Dictionary<string, (byte FormType, List<(int Offset, string Label)> Fields)> CharPointer
        ) BuildFieldIndices()
    {
        var bsIndex = new Dictionary<(byte, int), (string, string)>();
        var classIndex = new Dictionary<string, (byte, List<(int, string)>)>(StringComparer.Ordinal);
        var charIndex = new Dictionary<string, (byte, List<(int, string)>)>(StringComparer.Ordinal);

        foreach (var (formType, layout) in PdbStructLayouts.Layouts)
        {
            foreach (var field in PdbStructLayouts.GetBSStringTFields(formType))
            {
                if (field.Name is CFormEditorId)
                {
                    continue;
                }

                bsIndex.TryAdd((formType, field.Offset), (layout.RecordCode, Label(field)));
            }

            AddClass(classIndex, layout.ClassName, formType, CollectInlineStrings(layout.Fields));
            AddClass(charIndex, layout.ClassName, formType, CollectCharPointers(layout.Fields));
        }

        foreach (var (className, aux) in PdbStructLayouts.AuxStructs)
        {
            // FormType 0 means "not a record class"; the vtable resolver already treats that as
            // "report the class name rather than a record code".
            AddClass(classIndex, className, 0, CollectInlineStrings(aux.Fields));
            AddClass(charIndex, className, 0, CollectCharPointers(aux.Fields));
        }

        return (bsIndex, classIndex, charIndex);
    }

    /// <summary>
    ///     The EditorID field is excluded from the offset indices on purpose: it has its own
    ///     dedicated resolution path, and letting it match here would shadow that.
    /// </summary>
    private const string CFormEditorId = "cFormEditorID";

    private static string Label(PdbFieldLayout field)
    {
        return field.Owner != null ? $"{field.Owner}.{field.Name}" : field.Name;
    }

    /// <summary>
    ///     Whether a field's first word is a pointer to string bytes. Covers both wrappers the engine
    ///     uses: <c>BSStringT&lt;char&gt;</c> (pointer + length) and <c>NiFixedString</c>, which is a
    ///     4-byte struct whose sole member is a <c>char*</c> at +0 — so the word at the field's own
    ///     offset is the string pointer either way.
    ///     <para>
    ///         <c>NiFixedString</c> is the whole Gamebryo naming family (<c>NiObjectNET.m_kName</c>
    ///         and friends). It was invisible to every index here, which is why those classes had to
    ///         be hand-listed.
    ///     </para>
    /// </summary>
    private static bool IsInlineStringField(PdbFieldLayout field)
    {
        if (field.TypeDetail is null)
        {
            return false;
        }

        return field.TypeDetail == "NiFixedString"
               || (field.Kind == "struct"
                   && field.TypeDetail.Contains("BSStringT", StringComparison.Ordinal));
    }

    /// <summary>
    ///     A <c>BSSimpleList&lt;char const *&gt;</c> member: its first word is the head element, so
    ///     the word at the member's own offset is the FIRST filename in the list.
    ///     <para>
    ///         Worth indexing — a creature's animation and model paths are stored exactly this way —
    ///         but only the first element is reachable without walking the list, so the label says
    ///         <c>[0]</c> rather than implying a plain field. A hand-written table used to assert
    ///         these two offsets for <c>TESCreature</c> with labels that claimed they were scalar
    ///         paths; taking them from the database instead covers every such class and tells the
    ///         truth about what was read.
    ///     </para>
    /// </summary>
    private static bool IsCharListHeadField(PdbFieldLayout field)
    {
        return field is { Kind: "struct", TypeDetail: not null }
               && field.TypeDetail.StartsWith("BSSimpleList<char", StringComparison.Ordinal);
    }

    private static bool IsCharPointerField(PdbFieldLayout field)
    {
        return field is { Kind: "pointer", TypeDetail: "char" };
    }

    /// <summary>
    ///     String offsets declared by a class, plus those reachable one level into an embedded struct
    ///     member that has its own layout — a weapon's model path lives at
    ///     <c>TESModel.cModel</c> inside the embedded <c>TESModel</c>, not at a top-level offset, so
    ///     without this composition step those offsets simply do not exist in any index.
    ///     <para>
    ///         One level only. Deeper nesting would need cycle protection for a handful more offsets,
    ///         and the measured payoff is concentrated at depth one.
    ///     </para>
    /// </summary>
    private static List<(int Offset, string Label)> CollectInlineStrings(
        IReadOnlyList<PdbFieldLayout> fields)
    {
        var result = new List<(int, string)>();
        var seen = new HashSet<int>();

        foreach (var field in fields)
        {
            if (field.Name is CFormEditorId)
            {
                continue;
            }

            if (IsInlineStringField(field))
            {
                if (seen.Add(field.Offset))
                {
                    result.Add((field.Offset, Label(field)));
                }

                continue;
            }

            if (IsCharListHeadField(field))
            {
                if (seen.Add(field.Offset))
                {
                    result.Add((field.Offset, $"{Label(field)}[0]"));
                }

                continue;
            }

            if (field.Kind != "struct" || field.TypeDetail is null ||
                !PdbStructLayouts.TryGetAuxStruct(field.TypeDetail, out var inner))
            {
                continue;
            }

            foreach (var innerField in inner.Fields)
            {
                if (!IsInlineStringField(innerField) || innerField.Name is CFormEditorId)
                {
                    continue;
                }

                var offset = field.Offset + innerField.Offset;
                if (seen.Add(offset))
                {
                    result.Add((offset, $"{Label(field)}.{innerField.Name}"));
                }
            }
        }

        return result;
    }

    private static List<(int Offset, string Label)> CollectCharPointers(
        IReadOnlyList<PdbFieldLayout> fields)
    {
        var result = new List<(int, string)>();
        var seen = new HashSet<int>();

        foreach (var field in fields)
        {
            if (IsCharPointerField(field) && seen.Add(field.Offset))
            {
                result.Add((field.Offset, Label(field)));
            }
        }

        return result;
    }

    private static void AddClass(
        Dictionary<string, (byte FormType, List<(int Offset, string Label)> Fields)> index,
        string className,
        byte formType,
        List<(int Offset, string Label)> fields)
    {
        if (fields.Count > 0)
        {
            index.TryAdd(className, (formType, fields));
        }
    }

    /// <summary>
    ///     Hand-written class-to-string-offset index, consulted AFTER the PDB-derived indices.
    ///     <para>
    ///         ⚠⚠ Do NOT prune these by diffing against <c>pdb_layouts.json</c>. The PDBs postdate
    ///         every dump in the corpus: they fit the newest builds and drift on older ones, which
    ///         sit nearer Fallout 3's layout. An offset that looks past the end of a struct there can
    ///         be correct for the dump in hand, and these offsets are empirical.
    ///     </para>
    ///     <para>
    ///         Layering is what makes both sources safe: the PDB index matches first where it can,
    ///         and this table only fires where it cannot.
    ///     </para>
    /// </summary>
    internal static Dictionary<string, List<(int Offset, string Label)>> BuildNiObjectFieldIndex()
    {
        var index = new Dictionary<string, List<(int, string)>>(StringComparer.Ordinal);

        // --- Gamebryo NiObjectNET types: m_kName (NiFixedString) at +8 ---
        var nameField = (Offset: 8, Label: "NiObjectNET.m_kName");
        var niObjectNetClasses = new[]
        {
            "NiNode", "BSFadeNode", "NiTriShape", "NiTriStrips",
            "NiCamera", "NiLight", "NiPointLight", "NiDirectionalLight",
            "NiAmbientLight", "NiProperty", "NiMaterialProperty",
            "BSShaderPPLightingProperty", "NiAlphaProperty",
            "NiTexturingProperty", "NiStencilProperty",
            "NiVertexColorProperty", "NiWireframeProperty",
            "NiZBufferProperty", "NiSourceTexture",
            "BSTreeNode", "NiSwitchNode", "NiBillboardNode",
            "NiGeometry", "NiParticles", "NiParticleSystem",
            "BSShaderNoLightingProperty", "BSShaderLightingProperty",
            // Animation sequence types (NiObjectNET -> NiSequence -> ...)
            "BSAnimGroupSequence"
        };
        foreach (var cls in niObjectNetClasses)
        {
            index[cls] = [nameField];
        }

        index["NiSourceTexture"].Add((48, "NiSourceTexture.m_kFilename"));

        // --- TES embedded component classes (BaseFormComponent subclasses) ---
        // These have their own vtables when embedded in TESForm types via MI.
        // BSStringT at +4 = char* ptr right after the vtable.
        index["TESTexture"] = [(4, "TESTexture.texture")];
        index["TESIcon"] = [(4, "TESIcon.icon")];
        index["TESModel"] = [(4, "TESModel.model")];
        index["TESFullName"] = [(4, "TESFullName.cFullName")];
        index["TESModelTextureSwap"] =
        [
            (4, "TESModelTextureSwap.model"),
            (44, "TESModelTextureSwap.altTextureName")
        ];

        // TESTexture1024: subclass of TESTexture, same layout
        index["TESTexture1024"] = [(4, "TESTexture1024.texture")];

        // BGSTextureModel: another texture model component
        index["BGSTextureModel"] = [(4, "BGSTextureModel.model")];

        // QueuedModel: engine model loading queue entry, path at +40.
        index["QueuedModel"] = [(40, "QueuedModel.modelPath")];

        // BSShaderTextureSet: NiObject base (vtable + refcount), then NiFixedString texture slots.
        index["BSShaderTextureSet"] =
        [
            (8, "BSShaderTextureSet.diffuse"),
            (12, "BSShaderTextureSet.normal"),
            (16, "BSShaderTextureSet.glow"),
            (20, "BSShaderTextureSet.parallax"),
            (24, "BSShaderTextureSet.envMap"),
            (28, "BSShaderTextureSet.slot5"),
            (32, "BSShaderTextureSet.slot6"),
            (48, "BSShaderTextureSet.slot10"),
            (56, "BSShaderTextureSet.slot12")
        ];

        // SettingT<GameSettingCollection>: RTTI demangles to this template form, which can never
        // join a PDB class name.
        index["?$SettingT@VGameSettingCollection"] = [(8, "SettingT.pKey")];

        // --- Offsets the newer PDB contradicts, kept because they are empirical and still resolve.

        // BGSBodyPart: body part definition with mesh/bone paths
        index["BGSBodyPart"] =
        [
            (12, "BGSBodyPart.boneName"),
            (20, "BGSBodyPart.partNode"),
            (36, "BGSBodyPart.targetNode")
        ];

        // BGSQuestObjective: quest objective display text (BSStringT at +8)
        index["BGSQuestObjective"] = [(8, "BGSQuestObjective.displayText")];

        // TESLoadScreen: loading screen tip text
        index["TESLoadScreen"] = [(68, "TESLoadScreen.screenText")];

        // Script: compiled script contains string references
        index["Script"] =
        [
            (144, "Script.varName1"),
            (152, "Script.varName2")
        ];

        // TESCreature: creature model/animation paths
        index["TESCreature"] =
        [
            (216, "TESCreature.animPath"),
            (296, "TESCreature.modelPath")
        ];

        // BGSTerminal: terminal UI text fields
        index["BGSTerminal"] =
        [
            (208, "BGSTerminal.resultText"),
            (216, "BGSTerminal.headerText")
        ];

        return index;
    }
}
