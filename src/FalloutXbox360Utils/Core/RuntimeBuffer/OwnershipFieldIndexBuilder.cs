using FalloutXbox360Utils.Core.Formats.Esm.Runtime;

namespace FalloutXbox360Utils.Core.RuntimeBuffer;

/// <summary>
///     Builds PDB-based field indices used by BSStringT reverse lookup and vtable-based
///     reverse lookup strategies for second-pass ownership resolution.
/// </summary>
internal static class OwnershipFieldIndexBuilder
{
    /// <summary>
    ///     Build all three PDB-based field indices in a single pass over PdbStructLayouts.Layouts.
    ///     Returns: (bsStringTFieldIndex, classNameFieldIndex, charPointerFieldIndex).
    /// </summary>
    internal static (
        Dictionary<(byte FormType, int FieldOffset), (string RecordCode, string FieldLabel)> BsStringT,
        Dictionary<string, (byte FormType, List<(int Offset, string Label)> Fields)> ClassName,
        Dictionary<string, (byte FormType, List<(int Offset, string Label)> Fields)> CharPointer
        ) BuildFieldIndices()
    {
        var bsIndex = new Dictionary<(byte, int), (string, string)>();
        var classIndex = new Dictionary<string, (byte, List<(int, string)>)>();
        var charIndex = new Dictionary<string, (byte, List<(int, string)>)>();

        foreach (var (formType, layout) in PdbStructLayouts.Layouts)
        {
            // BSStringT fields -> bsIndex and classIndex
            var bsFields = PdbStructLayouts.GetBSStringTFields(formType);
            List<(int, string)>? classFieldList = null;

            foreach (var field in bsFields)
            {
                if (field.Name is "cFormEditorID")
                {
                    continue;
                }

                var fieldLabel = field.Owner != null ? $"{field.Owner}.{field.Name}" : field.Name;
                bsIndex.TryAdd((formType, field.Offset), (layout.RecordCode, fieldLabel));

                classFieldList ??= [];
                classFieldList.Add((field.Offset, fieldLabel));
            }

            if (classFieldList is { Count: > 0 })
            {
                classIndex[layout.ClassName] = (formType, classFieldList);
            }

            // char* pointer fields -> charIndex
            List<(int, string)>? charFieldList = null;

            foreach (var f in layout.Fields)
            {
                if (f.Kind is not "pointer" || f.TypeDetail is not "char")
                {
                    continue;
                }

                var label = f.Owner != null ? $"{f.Owner}.{f.Name}" : f.Name;
                charFieldList ??= [];
                charFieldList.Add((f.Offset, label));
            }

            if (charFieldList is { Count: > 0 })
            {
                charIndex[layout.ClassName] = (formType, charFieldList);
            }
        }

        return (bsIndex, classIndex, charIndex);
    }

    /// <summary>
    ///     Build hardcoded class name to string field offsets index for types not in PDB layouts.
    ///     Covers Gamebryo NiObject types and TES embedded component classes.
    /// </summary>
    internal static Dictionary<string, List<(int Offset, string Label)>> BuildNiObjectFieldIndex()
    {
        var index = new Dictionary<string, List<(int, string)>>();

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
        index["TESModelTextureSwap"] =
        [
            (4, "TESModelTextureSwap.model"),
            (44, "TESModelTextureSwap.altTextureName")
        ];

        // TESTexture1024: subclass of TESTexture, same layout
        index["TESTexture1024"] = [(4, "TESTexture1024.texture")];

        // BGSTextureModel: another texture model component
        index["BGSTextureModel"] = [(4, "BGSTextureModel.model")];

        // QueuedModel: engine model loading queue entry, path at +40
        index["QueuedModel"] = [(40, "QueuedModel.modelPath")];

        // BSShaderTextureSet: inherits NiObject (vtable+4=refcount), then 6+ texture slots.
        // Each slot is a NiFixedString (char*).
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

        // SettingT<GameSettingCollection>: RTTI demangles to this template form.
        // pKey (setting name char*) at +8.
        index["?$SettingT@VGameSettingCollection"] = [(8, "SettingT.pKey")];

        // --- TESForm-derived classes with string fields not in PDB BSStringT index ---

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
