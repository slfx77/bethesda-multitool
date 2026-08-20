using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Textures;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Particles;

/// <summary>
///     Parses a Bethesda <c>NiParticleSystem</c> block (and its emitter + modifier chain + NiPSysData) into a
///     <see cref="ParticleSystemDefinition" /> the baker can simulate. Layout verified against real data
///     (FXDustWhirlWind01 block 46), Skyrim LE, and Fallout 4 retail particle systems. Bethesda changed the
///     inherited geometry layout twice while retaining the NiPSys modifier model, so each stream family has
///     an explicit cursor rather than attempting to interpret all of them as the FO3/FNV NiGeometry form.
///     The per-particle arrays in NiPSysData are NOT read — the engine fills them by simulation, so the
///     baker re-simulates (see particles_formula_spec.md).
/// </summary>
internal static class NifParticleSystemParser
{
    // NiPSysModifier base (FO3/FNV string-table): Name(int32 index) + Order(uint) + Target(int32) + Active(bool).
    private const int ModifierBaseSize = 4 + 4 + 4 + 1;

    // NiControllerSequence's verified Bethesda 20.2.0.7 controlled-block form: interpolator ref,
    // controller ref, priority byte, then five string-table indices. The fixed sequence tail follows it.
    private const int ControlledBlockStride = 29;
    private const int SequenceTailSize = 32;
    private const int MaxControlledBlocks = 512;

    /// <summary>
    ///     "1" restores the pre-rest-state behavior: bind the first activation-triggered sequence
    ///     as if it were playing. Default OFF = the load-time rest-state resolve (see
    ///     <see cref="EnvironmentVariables.Viewer.TriggeredFx" />).
    /// </summary>
    private static readonly bool TriggeredFxForced =
        EnvironmentVariables.Get(EnvironmentVariables.Viewer.TriggeredFx) == "1";

    /// <summary>
    ///     True for the schema-backed NiPSys particle-system family. <c>BSStripParticleSystem</c> is a
    ///     field-identical Bethesda subclass in nif.xml. The unrelated 20.5+ <c>NiPSParticleSystem</c>
    ///     simulator family is deliberately excluded until an installed binary fixture proves its full base.
    /// </summary>
    internal static bool IsParticleSystem(string typeName)
    {
        return typeName is "NiParticleSystem" or "NiMeshParticleSystem" or "BSStripParticleSystem";
    }

    /// <summary>
    ///     Collect every shape block referenced as an emitter VOLUME mesh by any NiPSysMeshEmitter in the
    ///     file. Those shapes are emission volumes the engine never renders (they otherwise appear as
    ///     untextured white blobs). Used by <c>NifSceneGraphWalker</c> to exclude them from extraction.
    /// </summary>
    internal static HashSet<int> CollectEmitterMeshShapes(byte[] data, NifInfo nif)
    {
        var result = new HashSet<int>();
        var be = nif.IsBigEndian;
        for (var i = 0; i < nif.Blocks.Count; i++)
        {
            if (nif.Blocks[i].TypeName != "NiPSysMeshEmitter")
            {
                continue;
            }

            foreach (var meshRef in ReadEmitterMeshRefs(data, nif.Blocks[i], be))
            {
                if (meshRef >= 0 && meshRef < nif.Blocks.Count)
                {
                    result.Add(meshRef);
                }
            }
        }

        return result;
    }

    /// <summary>Parse one NiParticleSystem block. Returns null if the block is malformed/inconsistent.</summary>
    internal static ParticleSystemDefinition? Parse(
        byte[] data,
        NifInfo nif,
        int blockIndex,
        IReadOnlyDictionary<int, NifMaterialAlphaController>? alphaControllersByProperty = null)
    {
        if (blockIndex < 0 || blockIndex >= nif.Blocks.Count)
        {
            return null;
        }

        var block = nif.Blocks[blockIndex];
        if (!IsParticleSystem(block.TypeName))
        {
            return null;
        }

        var be = nif.IsBigEndian;
        var pos = block.DataOffset;
        var end = block.DataOffset + block.Size;

        if (!NifBinaryCursor.SkipNiObjectNET(
                data, ref pos, end, be, nif.HasInlineStrings, nif.BinaryVersion))
        {
            return null;
        }

        pos += nif.BsVersion > 26 ? 4 : 2; // Flags
        pos += 12 + 36 + 4; // Translation + Rotation + Scale
        var propertyRefs = new List<int>();
        int dataRef;
        bool worldSpace;
        List<int> modifierRefs;
        if (!TryReadSystemLayout(
                data, nif, ref pos, end, propertyRefs,
                out dataRef, out worldSpace, out modifierRefs))
        {
            return null;
        }

        var capacity = ReadDataCapacity(data, nif, dataRef);
        var def = new ParticleSystemDefinition
        {
            BlockIndex = blockIndex,
            WorldSpace = worldSpace,
            Capacity = capacity,
            SourceTypeName = block.TypeName,
            SourceLayout = nif.BsVersion switch
            {
                <= 34 => ParticleSystemSourceLayout.LegacyNiGeometry,
                < 100 => ParticleSystemSourceLayout.SkyrimNiGeometry,
                _ => ParticleSystemSourceLayout.BsGeometry
            }
        };
        ReadParticlePresentation(data, nif, dataRef, def);

        foreach (var modRef in modifierRefs)
        {
            var modifier = ParseModifier(data, nif, modRef);
            if (modifier is null)
            {
                continue;
            }

            modifier.SourceTypeName = nif.Blocks[modRef].TypeName;
            def.Modifiers.Add(modifier);
            if (modifier is ParticleEmitterDefinition emitter && def.Emitter is null)
            {
                def.Emitter = emitter;
            }

            if (modifier.Kind == ParticleModifierKind.Other)
            {
                def.UnsupportedSimulatorSteps.Add(
                    $"block {modRef}: {modifier.SourceTypeName} (retained in order; simulation not implemented)");
            }
        }

        // Density comes from the live BIRTH RATE curve, not the capacity (which is only the buffer maximum).
        // Preserve the two-stage sequence/controller clocks and every rate key so the renderer can advance the
        // rate instead of holding the busiest key forever.
        if (def.Emitter is not null)
        {
            def.Emitter.BirthRateController = ResolveBirthRateController(data, nif, blockIndex);
        }

        ResolveAppearance(data, nif, propertyRefs, def);
        if (alphaControllersByProperty is not null)
        {
            foreach (var propertyRef in propertyRefs)
            {
                if (alphaControllersByProperty.TryGetValue(propertyRef, out var alphaController))
                {
                    def.MaterialAlphaController = alphaController;
                    break;
                }
            }
        }

        return def;
    }

    /// <summary>
    ///     Decode the three observed Bethesda 20.2 inheritance layouts. All cursors are schema-derived and
    ///     terminate at the shared World Space / Modifiers tail:
    ///     FO3/FNV through BS 34, Skyrim LE (BS 83), and SSE/FO4/FO76 from BS 100 onward.
    /// </summary>
    private static bool TryReadSystemLayout(
        byte[] data,
        NifInfo nif,
        ref int pos,
        int end,
        List<int> propertyRefs,
        out int dataRef,
        out bool worldSpace,
        out List<int> modifierRefs)
    {
        dataRef = -1;
        worldSpace = true;
        modifierRefs = [];
        var be = nif.IsBigEndian;

        if (nif.BsVersion <= 34)
        {
            // NiAVObject.Properties[] is present through FO3/FNV.
            if (!TryReadPropertyArray(data, nif, ref pos, end, propertyRefs) || pos + 12 > end)
            {
                return false;
            }

            pos += 4; // Collision Object
            dataRef = BinaryUtils.ReadInt32(data, pos, be);
            pos += 8; // Data + Skin Instance
            if (!SkipMaterialData(data, ref pos, end, be))
            {
                return false;
            }
        }
        else if (nif.BsVersion < 100)
        {
            // Skyrim LE (BS 83) remains NiGeometry-style, but NiAVObject.Properties[] is gone.
            // Appearance moved to dedicated Shader/Alpha refs after MaterialData.
            if (pos + 12 > end)
            {
                return false;
            }

            pos += 4; // Collision Object
            dataRef = BinaryUtils.ReadInt32(data, pos, be);
            pos += 8; // Data + Skin Instance
            if (!SkipMaterialData(data, ref pos, end, be) || pos + 16 > end)
            {
                return false;
            }

            AddPropertyRef(BinaryUtils.ReadInt32(data, pos, be));
            AddPropertyRef(BinaryUtils.ReadInt32(data, pos + 4, be));
            pos += 8;
            pos += 8; // Far Begin/End + Near Begin/End (4 x ushort)
        }
        else
        {
            // SSE/FO4/FO76 NiParticleSystem uses the BSGeometry base: bounds and dedicated refs precede
            // VertexDesc; its NiPSysData ref moved after the distance bands.
            var boundsSize = 16 + (nif.BsVersion >= 155 ? 24 : 0);
            if (pos + 4 + boundsSize + 12 + 8 + 8 + 4 > end)
            {
                return false;
            }

            pos += 4; // Collision Object
            pos += boundsSize; // Bounding sphere, plus FO76 bounding box
            pos += 4; // Skin
            AddPropertyRef(BinaryUtils.ReadInt32(data, pos, be));
            AddPropertyRef(BinaryUtils.ReadInt32(data, pos + 4, be));
            pos += 8; // Shader + Alpha
            pos += 8; // BSVertexDesc
            pos += 8; // Far Begin/End + Near Begin/End
            dataRef = BinaryUtils.ReadInt32(data, pos, be);
            pos += 4;
        }

        if (pos + 5 > end)
        {
            return false;
        }

        worldSpace = data[pos++] != 0;
        var authoredModifierCount = BinaryUtils.ReadUInt32(data, pos, be);
        pos += 4;
        if (authoredModifierCount > 100 || pos + (long)authoredModifierCount * 4 > end)
        {
            return false;
        }

        modifierRefs = new List<int>((int)authoredModifierCount);
        for (var i = 0; i < authoredModifierCount; i++)
        {
            var modifierRef = BinaryUtils.ReadInt32(data, pos, be);
            pos += 4;
            if (modifierRef >= 0 && modifierRef < nif.Blocks.Count)
            {
                modifierRefs.Add(modifierRef);
            }
        }

        return true;

        void AddPropertyRef(int propertyRef)
        {
            if (propertyRef >= 0 && propertyRef < nif.Blocks.Count)
            {
                propertyRefs.Add(propertyRef);
            }
        }
    }

    private static bool TryReadPropertyArray(
        byte[] data, NifInfo nif, ref int pos, int end, List<int> propertyRefs)
    {
        if (pos + 4 > end)
        {
            return false;
        }

        var count = BinaryUtils.ReadUInt32(data, pos, nif.IsBigEndian);
        pos += 4;
        if (count > 100 || pos + (long)count * 4 > end)
        {
            return false;
        }

        for (var i = 0; i < count; i++)
        {
            var propertyRef = BinaryUtils.ReadInt32(data, pos, nif.IsBigEndian);
            pos += 4;
            if (propertyRef >= 0 && propertyRef < nif.Blocks.Count)
            {
                propertyRefs.Add(propertyRef);
            }
        }

        return true;
    }

    /// <summary>
    ///     Resolve the NiPSysEmitterCtlr that targets this system, then bind its BirthRate
    ///     NiFloatInterpolator from the exact ControlledBlock table. The controller's own clock is retained in
    ///     addition to the outer sequence clock: retail FXDust uses distinct controller phases to desynchronise
    ///     its two emitters even though both live in the same 0..12 second looping sequence.
    /// </summary>
    private static ParticleRateControllerDefinition? ResolveBirthRateController(
        byte[] data, NifInfo nif, int systemIndex)
    {
        for (var controllerIndex = 0; controllerIndex < nif.Blocks.Count; controllerIndex++)
        {
            var controllerBlock = nif.Blocks[controllerIndex];
            if (controllerBlock.TypeName is not ("NiPSysEmitterCtlr" or "BSPSysMultiTargetEmitterCtlr") ||
                !NifTimeControllerReader.TryRead(
                    data, controllerBlock, nif.IsBigEndian, out var controllerHeader) ||
                controllerHeader.TargetRef != systemIndex)
            {
                continue;
            }

            var controllerTiming = ReadControllerTiming(controllerHeader);
            ParticleRateControllerDefinition? firstManaged = null;

            // Managed controllers point at a NiBlendFloatInterpolator themselves; the real BirthRate
            // NiFloatInterpolator is supplied by the matching sequence ControlledBlock.
            for (var sequenceIndex = 0; sequenceIndex < nif.Blocks.Count; sequenceIndex++)
            {
                var sequenceBlock = nif.Blocks[sequenceIndex];
                if (sequenceBlock.TypeName != "NiControllerSequence" ||
                    !TryReadSequenceRateBinding(
                        data, nif, sequenceBlock, controllerIndex,
                        out var interpolatorRef, out var emitterActiveRef,
                        out var sequenceTiming, out var isIdle))
                {
                    continue;
                }

                var managed = ReadRateInterpolator(
                    data, nif, interpolatorRef, controllerHeader.IsActive,
                    controllerTiming, sequenceTiming, emitterActiveRef);
                if (managed is null)
                {
                    continue;
                }

                // Passive embedded effects conventionally auto-play Idle. Any other sequence only
                // plays when something ACTIVATES the object (door groups, one-shot quest FX).
                if (isIdle)
                {
                    return managed;
                }

                firstManaged ??= managed;
            }

            if (firstManaged is not null)
            {
                // Load-time rest-state resolve: this emitter is bound ONLY by activation-triggered
                // sequences, so at game start — before any script or door has fired it — the engine
                // has never advanced its rate curve and the emitter is DORMANT. Binding the first
                // triggered sequence as if it were playing rendered explosion/burst FX permanently
                // (and paid their bake/sim/overdraw cost every frame). The dormant definition keeps
                // IsActive so the system still decodes (a per-instance "preview activation" can
                // re-bind the curve later); FALLOUT_VIEWER_TRIGGERED_FX=1 restores the old behavior.
                return TriggeredFxForced
                    ? firstManaged
                    : new ParticleRateControllerDefinition
                    {
                        IsActive = true,
                        ConstantValue = 0f,
                        DormantTriggeredFx = true
                    };
            }

            // Non-manager-controlled legacy files can attach NiFloatInterpolator directly after the shared
            // NiTimeController header. Blend/unknown interpolators are rejected safely by ReadRateInterpolator.
            if (controllerBlock.Size >= NifTimeControllerHeader.HeaderSize + 4)
            {
                var directInterpolator = BinaryUtils.ReadInt32(
                    data, controllerBlock.DataOffset + NifTimeControllerHeader.HeaderSize, nif.IsBigEndian);
                // NiPSysEmitterCtlr carries a SECOND slot after interpolator + modifier-name: the
                // EmitterActive visibility interpolator (base 26 + 4 + 4 = offset 34, size 38).
                // The multi-target variant lays extra fields there instead, so it keeps rate-only.
                var directEmitterActive = -1;
                if (controllerBlock.TypeName == "NiPSysEmitterCtlr" &&
                    controllerBlock.Size >= NifTimeControllerHeader.HeaderSize + 12)
                {
                    directEmitterActive = BinaryUtils.ReadInt32(
                        data, controllerBlock.DataOffset + NifTimeControllerHeader.HeaderSize + 8,
                        nif.IsBigEndian);
                }

                if (ReadRateInterpolator(
                        data, nif, directInterpolator, controllerHeader.IsActive,
                        controllerTiming, null, directEmitterActive) is { } direct)
                {
                    return direct;
                }
            }
        }

        return null;
    }

    private static bool TryReadSequenceRateBinding(
        byte[] data,
        NifInfo nif,
        BlockInfo sequence,
        int controllerIndex,
        out int interpolatorRef,
        out int emitterActiveRef,
        out ParticleControllerTiming sequenceTiming,
        out bool isIdle)
    {
        interpolatorRef = -1;
        emitterActiveRef = -1;
        sequenceTiming = ParticleControllerTiming.Identity;
        isIdle = false;

        // The supported FO3/FNV particle family uses Bethesda's 20.2.0.7 string-table form. Do not scan
        // arbitrary four-byte words: the 29-byte stride intentionally de-aligns every later ControlledBlock.
        if (nif.BinaryVersion != NifVersions.Gamebryo202007 || nif.BsVersion == 0 || sequence.Size < 12)
        {
            return false;
        }

        var be = nif.IsBigEndian;
        var end = sequence.DataOffset + sequence.Size;
        var nameIndex = BinaryUtils.ReadInt32(data, sequence.DataOffset, be);
        isIdle = nameIndex >= 0 && nameIndex < nif.Strings.Count &&
                 nif.Strings[nameIndex].Contains("idle", StringComparison.OrdinalIgnoreCase);

        var count = BinaryUtils.ReadUInt32(data, sequence.DataOffset + 4, be);
        if (count == 0 || count > MaxControlledBlocks)
        {
            return false;
        }

        var controlledCount = (int)count;
        var controlledStart = sequence.DataOffset + 12; // Name + count + Array Grow By
        var tailLong = controlledStart + (long)controlledCount * ControlledBlockStride;
        if (tailLong > int.MaxValue)
        {
            return false;
        }

        var tail = (int)tailLong;
        if (tail + SequenceTailSize > end)
        {
            return false;
        }

        var cycle = ReadCycle(BinaryUtils.ReadInt32(data, tail + 8, be));
        sequenceTiming = new ParticleControllerTiming(
            BinaryUtils.ReadFloat(data, tail + 12, be),
            0f,
            BinaryUtils.ReadFloat(data, tail + 16, be),
            BinaryUtils.ReadFloat(data, tail + 20, be),
            cycle);

        // One NiPSysEmitterCtlr owns TWO ControlledBlocks in a sequence: the float BirthRate
        // binding and the bool EmitterActive binding. Sequences commonly author the rate as a
        // constant pose and do all the gating through the bool (NVNellisArtillery Idle: rate 2250,
        // EmitterActive false), so both must be captured together.
        for (var i = 0; i < controlledCount; i++)
        {
            var blockStart = controlledStart + i * ControlledBlockStride;
            if (BinaryUtils.ReadInt32(data, blockStart + 4, be) != controllerIndex)
            {
                continue;
            }

            var candidate = BinaryUtils.ReadInt32(data, blockStart, be);
            if (candidate < 0 || candidate >= nif.Blocks.Count)
            {
                continue;
            }

            switch (nif.Blocks[candidate].TypeName)
            {
                case "NiFloatInterpolator" when interpolatorRef < 0:
                    interpolatorRef = candidate;
                    break;
                case "NiBoolInterpolator" or "NiBoolTimelineInterpolator" when emitterActiveRef < 0:
                    emitterActiveRef = candidate;
                    break;
            }

            if (interpolatorRef >= 0 && emitterActiveRef >= 0)
            {
                break;
            }
        }

        return interpolatorRef >= 0;
    }

    private static ParticleRateControllerDefinition? ReadRateInterpolator(
        byte[] data,
        NifInfo nif,
        int interpolatorRef,
        bool isActive,
        ParticleControllerTiming controllerTiming,
        ParticleControllerTiming? sequenceTiming,
        int emitterActiveRef = -1)
    {
        if (interpolatorRef < 0 || interpolatorRef >= nif.Blocks.Count)
        {
            return null;
        }

        var interpolator = nif.Blocks[interpolatorRef];
        if (interpolator.TypeName != "NiFloatInterpolator" || interpolator.Size < 8)
        {
            return null;
        }

        var be = nif.IsBigEndian;
        ReadEmitterActiveInterpolator(
            data, nif, emitterActiveRef, out var emitterActiveConstant, out var emitterActiveKeys);
        var dataRef = BinaryUtils.ReadInt32(data, interpolator.DataOffset + 4, be);
        if (TryReadRateKeysFromBlock(data, nif, dataRef, out var interpolation, out var keys))
        {
            return new ParticleRateControllerDefinition
            {
                IsActive = isActive,
                SequenceTiming = sequenceTiming,
                ControllerTiming = controllerTiming,
                Interpolation = interpolation,
                Keys = keys,
                EmitterActiveConstant = emitterActiveConstant,
                EmitterActiveKeys = emitterActiveKeys
            };
        }

        if (dataRef >= 0)
        {
            // An authored NiFloatData is the whole truth for this track, and the engine ignores the
            // pose slot whenever one is attached — so under an authored curve the pose holds stale
            // scratch, not a rate. Failing to decode the curve means we know nothing; reading the
            // scratch anyway is where megatongatehouse01 #518's "authored rate" of 2 995 932 came
            // from (explosiongrenadefrag: 12 000 000). Emit no rate instead. Zero, not the
            // capacity/lifespan density estimate: an undecodable curve must not fabricate FX, and
            // returning null here would also discard the EmitterActive gate decoded above — which
            // is the only thing keeping these gore/explosion emitters silent at rest.
            return new ParticleRateControllerDefinition
            {
                IsActive = isActive,
                SequenceTiming = sequenceTiming,
                ControllerTiming = controllerTiming,
                EmitterActiveConstant = emitterActiveConstant,
                EmitterActiveKeys = emitterActiveKeys
            };
        }

        // NiFloatInterpolator.Value is authoritative only when no NiFloatData is attached at all. Zero is a
        // valid authored rate and must NOT fall through to the old capacity/lifespan density estimate.
        var poseValue = BinaryUtils.ReadFloat(data, interpolator.DataOffset, be);
        if (!float.IsFinite(poseValue) || MathF.Abs(poseValue) >= 1e30f)
        {
            return null;
        }

        return new ParticleRateControllerDefinition
        {
            IsActive = isActive,
            SequenceTiming = sequenceTiming,
            ControllerTiming = controllerTiming,
            ConstantValue = poseValue,
            EmitterActiveConstant = emitterActiveConstant,
            EmitterActiveKeys = emitterActiveKeys
        };
    }

    /// <summary>
    ///     Decode the EmitterActive bool binding: NiBoolInterpolator / NiBoolTimelineInterpolator
    ///     is a pose byte (0/1; 2 = the "no pose" sentinel, mirroring the float MIN sentinel)
    ///     followed by an optional NiBoolData ref of stepwise {float time, byte value} keys. An
    ///     absent, malformed, or exotic binding yields no gate (null/empty) — prior behavior.
    /// </summary>
    private static void ReadEmitterActiveInterpolator(
        byte[] data,
        NifInfo nif,
        int emitterActiveRef,
        out bool? constant,
        out IReadOnlyList<ParticleBoolKey> keys)
    {
        constant = null;
        keys = [];
        if (emitterActiveRef < 0 || emitterActiveRef >= nif.Blocks.Count)
        {
            return;
        }

        var interpolator = nif.Blocks[emitterActiveRef];
        if (interpolator.TypeName is not ("NiBoolInterpolator" or "NiBoolTimelineInterpolator") ||
            interpolator.Size < 5)
        {
            return;
        }

        var be = nif.IsBigEndian;
        var dataRef = BinaryUtils.ReadInt32(data, interpolator.DataOffset + 1, be);
        if (dataRef >= 0)
        {
            // Same rule as the rate track: an authored NiBoolData overrides the pose byte, so when we
            // cannot decode the curve we know nothing about the gate. Falling through would fabricate
            // a PERMANENT gate value out of stale scratch — and since a fabricated `false` silences an
            // emitter outright, that failure mode is invisible in a render.
            if (dataRef < nif.Blocks.Count &&
                nif.Blocks[dataRef].TypeName == "NiBoolData" &&
                TryReadBoolKeys(data, nif.Blocks[dataRef], be, out var decoded))
            {
                keys = decoded;
            }

            return;
        }

        var pose = data[interpolator.DataOffset];
        if (pose <= 1)
        {
            constant = pose != 0;
        }
    }

    private static bool TryReadBoolKeys(
        byte[] data,
        BlockInfo block,
        bool bigEndian,
        out IReadOnlyList<ParticleBoolKey> keys)
    {
        keys = [];
        if (block.Size < 8)
        {
            return false;
        }

        var count = BinaryUtils.ReadUInt32(data, block.DataOffset, bigEndian);
        var interpolation = BinaryUtils.ReadUInt32(data, block.DataOffset + 4, bigEndian);
        // Bool tracks step; LINEAR(1) and CONST(5) share the 5-byte {time,value} layout. Anything
        // else would misalign the walk, so it fails to "no gate" rather than misread keys.
        if (count == 0 || count > 4096 || interpolation is not (1 or 5) ||
            block.DataOffset + 8 + (long)count * 5 > block.DataOffset + block.Size)
        {
            return false;
        }

        var decoded = new ParticleBoolKey[count];
        var offset = block.DataOffset + 8;
        for (var i = 0; i < count; i++)
        {
            decoded[i] = new ParticleBoolKey(
                BinaryUtils.ReadFloat(data, offset, bigEndian), data[offset + 4] != 0);
            offset += 5;
        }

        keys = decoded;
        return true;
    }

    private static bool TryReadRateKeysFromBlock(
        byte[] data,
        NifInfo nif,
        int dataRef,
        out ParticleRateInterpolation interpolation,
        out IReadOnlyList<ParticleRateKey> keys)
    {
        interpolation = ParticleRateInterpolation.Linear;
        keys = [];
        if (dataRef < 0 || dataRef >= nif.Blocks.Count || nif.Blocks[dataRef].TypeName != "NiFloatData")
        {
            return false;
        }

        var block = nif.Blocks[dataRef];
        return TryReadRateKeys(
            data, block.DataOffset, block.Size, nif.IsBigEndian, out interpolation, out keys);
    }

    /// <summary>
    ///     Decode one raw NiFloatData KeyGroup. Kept independent of NifInfo so little-/big-endian parser
    ///     fixtures can prove byte order and auxiliary-key retention without manufacturing a full NIF graph.
    /// </summary>
    internal static bool TryReadRateKeys(
        byte[] data,
        int offset,
        int size,
        bool isBigEndian,
        out ParticleRateInterpolation interpolation,
        out IReadOnlyList<ParticleRateKey> keys)
    {
        interpolation = ParticleRateInterpolation.Linear;
        keys = [];
        if (offset < 0 || size < 8 || (long)offset + size > data.Length)
        {
            return false;
        }

        var pos = offset;
        var end = offset + size;
        var be = isBigEndian;
        var count = BinaryUtils.ReadUInt32(data, pos, be);
        var rawInterpolation = BinaryUtils.ReadUInt32(data, pos + 4, be);
        if (count == 0 || count > 1024 ||
            rawInterpolation is not (1u or 2u or 3u or 5u))
        {
            return false;
        }

        interpolation = (ParticleRateInterpolation)rawInterpolation;
        var stride = interpolation switch
        {
            ParticleRateInterpolation.Quadratic => 16,
            ParticleRateInterpolation.Tbc => 20,
            _ => 8
        };
        pos += 8;
        if (pos + count * stride > end)
        {
            return false;
        }

        var parsed = new ParticleRateKey[(int)count];
        var previousTime = float.NegativeInfinity;
        for (var i = 0; i < parsed.Length; i++, pos += stride)
        {
            var time = BinaryUtils.ReadFloat(data, pos, be);
            var value = BinaryUtils.ReadFloat(data, pos + 4, be);
            if (!float.IsFinite(time) || !float.IsFinite(value) || time < previousTime)
            {
                return false;
            }

            previousTime = time;
            parsed[i] = interpolation switch
            {
                ParticleRateInterpolation.Quadratic => new ParticleRateKey(
                    time, value,
                    BinaryUtils.ReadFloat(data, pos + 8, be),
                    BinaryUtils.ReadFloat(data, pos + 12, be)),
                ParticleRateInterpolation.Tbc => new ParticleRateKey(
                    time, value,
                    Tension: BinaryUtils.ReadFloat(data, pos + 8, be),
                    Bias: BinaryUtils.ReadFloat(data, pos + 12, be),
                    Continuity: BinaryUtils.ReadFloat(data, pos + 16, be)),
                _ => new ParticleRateKey(time, value)
            };
        }

        keys = parsed;
        return true;
    }

    private static ParticleControllerTiming ReadControllerTiming(NifTimeControllerHeader header)
    {
        return new ParticleControllerTiming(header.Frequency, header.Phase, header.StartTime, header.StopTime,
            ReadCycle((int)header.CycleType));
    }

    private static ParticleControllerCycle ReadCycle(int cycle)
    {
        return cycle switch
        {
            0 => ParticleControllerCycle.Loop,
            1 => ParticleControllerCycle.Reverse,
            _ => ParticleControllerCycle.Clamp // unknown values hold safely rather than wrapping unpredictably
        };
    }

    /// <summary>Resolve the particle sprite texture + blend mode from the system's property refs.</summary>
    private static void ResolveAppearance(byte[] data, NifInfo nif, List<int> propertyRefs,
        ParticleSystemDefinition def)
    {
        // Particle sprites carry their texture on a shader property (BSShaderNoLighting/Effect/PP) OR a
        // legacy NiTexturingProperty (FO3/FNV particles commonly use the latter). Try the shader reader
        // first, then fall back to the texturing reader so the wisp/sprite isn't lost (→ white particles).
        var shaderMetadata = NifShaderTexturePropertyReader.ReadShaderMetadata(data, nif, propertyRefs);
        def.ShaderPropertyType = shaderMetadata?.PropertyType;
        def.DiffuseTexturePath = shaderMetadata?.DiffusePath
                                 ?? NifTexturingPropertyReader.ResolveBaseTexturePath(data, nif, propertyRefs);

        var alpha = NifRenderPropertyReader.ReadAlphaProperty(data, nif, propertyRefs);
        def.HasAlphaBlend = alpha.HasAlphaBlend;
        def.SrcBlendMode = alpha.SrcBlendMode;
        def.DstBlendMode = alpha.DstBlendMode;
    }

    private static int ReadDataCapacity(byte[] data, NifInfo nif, int dataRef)
    {
        if (dataRef < 0 || dataRef >= nif.Blocks.Count)
        {
            return 0;
        }

        // NiGeometryData: optional GroupID(4, only since NIF 10.1.0.114) then Num Vertices (ushort).
        var capacity = NifSceneGraphBlockReader.ReadVertexCount(data, nif.Blocks[dataRef], nif.IsBigEndian,
            nif.BinaryVersion);
        return capacity < 0 ? 0 : capacity;
    }

    /// <summary>
    ///     Walk the NiGeometryData/NiParticlesData prefix far enough to retain authored atlas and aspect
    ///     presentation. Bethesda 20.2 streams serialize the presence booleans but omit the per-particle
    ///     rest arrays; treating those booleans as array payloads is what previously walked past the real
    ///     Subtexture Offsets block.
    /// </summary>
    private static void ReadParticlePresentation(
        byte[] data, NifInfo nif, int dataRef, ParticleSystemDefinition definition)
    {
        if (dataRef < 0 || dataRef >= nif.Blocks.Count ||
            nif.Blocks[dataRef].TypeName is not ("NiPSysData" or "NiMeshPSysData" or "NiParticlesData"))
        {
            return;
        }

        var block = nif.Blocks[dataRef];
        var be = nif.IsBigEndian;
        var pos = block.DataOffset;
        var end = Math.Min(data.Length, block.DataOffset + block.Size);
        var modernGeom = NifVersions.HasModernGeometryBase(nif.BinaryVersion);
        var bethesda202 = nif.BinaryVersion == 0x14020007 && nif.BsVersion > 0;

        if (NifVersions.HasGeometryGroupId(nif.BinaryVersion)) pos += 4;
        if (pos + 2 > end) return;
        var numVertices = BinaryUtils.ReadUInt16(data, pos, be);
        pos += 2;
        if (NifVersions.HasGeometryKeepFlags(nif.BinaryVersion)) pos += 2;

        // Bethesda 20.2 NiPSysData retains the presence booleans and BS Max Vertices, but none of the
        // NiGeometryData per-particle arrays are serialized. Their storage is allocated at runtime.
        if (!SkipOptionalArray(ref pos, 12, !bethesda202)) return; // vertices

        ushort dataFlags = 0;
        if (modernGeom)
        {
            if (pos + 2 > end) return;
            dataFlags = BinaryUtils.ReadUInt16(data, pos, be);
            pos += 2;
            if (nif.BsVersion > 34) pos += 4; // material CRC
        }

        if (pos + 1 > end) return;
        var hasNormals = data[pos++] != 0;
        if (hasNormals && !bethesda202)
        {
            pos += numVertices * 12;
            if (modernGeom && (dataFlags & 0x1000) != 0) pos += numVertices * 24;
        }

        if (modernGeom) pos += 16; // bounding sphere is present whether normals are present or not

        if (!SkipOptionalArray(ref pos, 16, !bethesda202)) return; // vertex colors
        if (modernGeom)
        {
            if (!bethesda202 && (dataFlags & 0x0001) != 0) pos += numVertices * 8;
        }
        else
        {
            if (pos + 4 > end) return;
            var uvSets = BinaryUtils.ReadUInt16(data, pos, be) & 0x3F;
            pos += 4 + uvSets * numVertices * 8;
        }

        pos += 2; // consistency
        if (nif.BinaryVersion >= 0x14000004) pos += 4; // additional data ref
        if (pos + 1 > end) return;

        // NiParticlesData. In Bethesda 20.2 the bools remain but their large rest-pose arrays are omitted.
        if (nif.BinaryVersion >= 0x0A010000 && !SkipOptionalArray(ref pos, 4, !bethesda202)) return; // radii
        pos += 2; // Num Active
        if (!SkipOptionalArray(ref pos, 4, !bethesda202)) return; // sizes
        if (nif.BinaryVersion >= 0x0A000100 && !SkipOptionalArray(ref pos, 16, !bethesda202)) return; // quaternions
        if (nif.BinaryVersion >= 0x14000004 && !SkipOptionalArray(ref pos, 4, !bethesda202)) return; // angles
        if (nif.BinaryVersion >= 0x14000004 && !SkipOptionalArray(ref pos, 12, !bethesda202)) return; // axes

        if (!bethesda202 || pos + 1 > end) return;
        _ = data[pos++] != 0; // Has Texture Indices: runtime array presence; offsets still follow.

        uint count;
        if (nif.BsVersion <= 34)
        {
            if (pos + 1 > end) return;
            count = data[pos++];
        }
        else
        {
            if (pos + 4 > end) return;
            count = BinaryUtils.ReadUInt32(data, pos, be);
            pos += 4;
        }

        if (count is > 0 and <= 256 && pos + (long)count * 16 <= end)
        {
            var offsets = new Vector4[count];
            for (var i = 0; i < count; i++)
            {
                // NIF orders each atlas entry as (uOffset, uScale, vOffset, vScale).
                // Normalize it to the renderer's (uOffset, vOffset, uScale, vScale) contract;
                // treating the raw Vector4 as RGBA is what produced zero/negative frame widths.
                offsets[i] = ReadAtlasRectangle(data, pos + i * 16, be);
            }

            definition.SubtextureOffsets = offsets;
            pos += checked((int)count * 16);
        }

        if (nif.BsVersion > 34 && pos + 4 <= end)
        {
            var aspect = BinaryUtils.ReadFloat(data, pos, be);
            if (float.IsFinite(aspect) && aspect > 1e-4f)
            {
                definition.AspectRatio = aspect;
            }
        }

        return;

        bool SkipOptionalArray(ref int cursor, int stride, bool includePayload)
        {
            if (cursor + 1 > end) return false;
            var present = data[cursor++] != 0;
            if (present && includePayload) cursor += numVertices * stride;
            return cursor <= end;
        }
    }

    private static ParticleModifierDefinition? ParseModifier(byte[] data, NifInfo nif, int modRef)
    {
        var block = nif.Blocks[modRef];
        var be = nif.IsBigEndian;
        var start = block.DataOffset;
        var end = block.DataOffset + block.Size;
        if (start + ModifierBaseSize > end)
        {
            return null;
        }

        var active = data[start + 12] != 0; // Active bool at base offset 12
        var p = start + ModifierBaseSize; // first type-specific field

        switch (block.TypeName)
        {
            case "NiPSysBoxEmitter":
            case "NiPSysSphereEmitter":
            case "NiPSysCylinderEmitter":
            case "NiPSysMeshEmitter":
            case "NiPSysEmitter":
                return ParseEmitter(data, nif, block, modRef, active);

            case "NiPSysGrowFadeModifier":
                if (p + 16 > end) return null;
                return new GrowFadeModifierDefinition
                {
                    Kind = ParticleModifierKind.GrowFade, Active = active, BlockIndex = modRef,
                    GrowTime = BinaryUtils.ReadFloat(data, p, be),
                    GrowGeneration = BinaryUtils.ReadUInt16(data, p + 4, be),
                    FadeTime = BinaryUtils.ReadFloat(data, p + 6, be),
                    FadeGeneration = BinaryUtils.ReadUInt16(data, p + 10, be),
                    BaseScale = BinaryUtils.ReadFloat(data, p + 12, be)
                };

            case "NiPSysBombModifier":
            {
                // Bomb Object(Ptr 4) + Bomb Axis(12) + Decay(4) + Delta V(4) + Decay Type(4) + Symmetry Type(4).
                if (p + 4 + 12 + 16 > end) return null;
                var bombObj = BinaryUtils.ReadInt32(data, p, be);
                return new BombModifierDefinition
                {
                    Kind = ParticleModifierKind.Bomb, Active = active, BlockIndex = modRef,
                    HasBombObject = bombObj >= 0,
                    BombObjectTransform = ResolveObjectTransform(data, nif, bombObj),
                    BombAxis = ReadVector3(data, p + 4, be),
                    Range = BinaryUtils.ReadFloat(data, p + 16, be),
                    DeltaV = BinaryUtils.ReadFloat(data, p + 20, be),
                    DecayType = (int)BinaryUtils.ReadUInt32(data, p + 24, be),
                    SymmetryType = (int)BinaryUtils.ReadUInt32(data, p + 28, be)
                };
            }

            case "NiPSysGravityModifier":
            {
                // Gravity Object(Ptr 4) + Gravity Axis(12) + Decay(4) + Strength(4) + Force Type(4)
                // + Turbulence(4) + Turbulence Scale(4) + World Aligned(bool, when present).
                if (p + 4 + 12 + 12 > end) return null;
                var gravObj = BinaryUtils.ReadInt32(data, p, be);
                return new GravityModifierDefinition
                {
                    Kind = ParticleModifierKind.Gravity, Active = active, BlockIndex = modRef,
                    HasGravityObject = gravObj >= 0,
                    GravityObjectTransform = ResolveObjectTransform(data, nif, gravObj),
                    GravityAxis = ReadVector3(data, p + 4, be),
                    Decay = BinaryUtils.ReadFloat(data, p + 16, be),
                    Strength = BinaryUtils.ReadFloat(data, p + 20, be),
                    ForceType = (int)BinaryUtils.ReadUInt32(data, p + 24, be),
                    Turbulence = p + 32 <= end ? BinaryUtils.ReadFloat(data, p + 28, be) : 0f,
                    TurbulenceScale = p + 36 <= end ? BinaryUtils.ReadFloat(data, p + 32, be) : 1f,
                    WorldAligned = p + 37 <= end && data[p + 36] != 0
                };
            }

            case "BSPSysSimpleColorModifier":
            {
                // Fade In/Out Percent + Color1 End/Start + Color2 End/Start (note End-before-Start) + Colors[3].
                if (p + 24 + 48 > end)
                    return new ColorModifierDefinition
                        { Kind = ParticleModifierKind.Color, Active = active, BlockIndex = modRef };
                return new ColorModifierDefinition
                {
                    Kind = ParticleModifierKind.Color, Active = active, BlockIndex = modRef, IsSimpleColor = true,
                    FadeInPercent = BinaryUtils.ReadFloat(data, p, be),
                    FadeOutPercent = BinaryUtils.ReadFloat(data, p + 4, be),
                    Color1EndPercent = BinaryUtils.ReadFloat(data, p + 8, be),
                    Color1StartPercent = BinaryUtils.ReadFloat(data, p + 12, be),
                    Color2EndPercent = BinaryUtils.ReadFloat(data, p + 16, be),
                    Color2StartPercent = BinaryUtils.ReadFloat(data, p + 20, be),
                    Color0 = ReadColor4(data, p + 24, be),
                    Color1 = ReadColor4(data, p + 40, be),
                    Color2 = ReadColor4(data, p + 56, be)
                };
            }

            case "NiPSysColorModifier":
                // References a NiColorData block of (time → RGBA) keys.
                return new ColorModifierDefinition
                {
                    Kind = ParticleModifierKind.Color, Active = active, BlockIndex = modRef,
                    Keys = p + 4 <= end ? ReadColorDataKeys(data, nif, BinaryUtils.ReadInt32(data, p, be)) : []
                };

            case "NiPSysDragModifier":
            {
                // Drag Object(Ptr 4) + Drag Axis(12) + Percentage(4) + Range(4) + Range Falloff(4).
                if (p + 4 + 12 + 12 > end)
                {
                    return new ParticleModifierDefinition
                        { Kind = ParticleModifierKind.Drag, Active = active, BlockIndex = modRef };
                }

                var dragObj = BinaryUtils.ReadInt32(data, p, be);
                return new DragModifierDefinition
                {
                    Kind = ParticleModifierKind.Drag, Active = active, BlockIndex = modRef,
                    HasDragObject = dragObj >= 0,
                    DragObjectTransform = ResolveObjectTransform(data, nif, dragObj),
                    DragAxis = ReadVector3(data, p + 4, be),
                    Percentage = BinaryUtils.ReadFloat(data, p + 16, be),
                    Range = BinaryUtils.ReadFloat(data, p + 20, be),
                    RangeFalloff = BinaryUtils.ReadFloat(data, p + 24, be)
                };
            }

            case "NiPSysAgeDeathModifier":
                return new ParticleModifierDefinition
                    { Kind = ParticleModifierKind.AgeDeath, Active = active, BlockIndex = modRef };
            case "NiPSysPositionModifier":
                return new ParticleModifierDefinition
                    { Kind = ParticleModifierKind.Position, Active = active, BlockIndex = modRef };
            case "NiPSysRotationModifier":
            {
                // FO3/FNV 20.2.0.7: speed, variation, angle, variation, random-sign, random-axis, axis.
                if (p + 4 > end)
                {
                    return new ParticleModifierDefinition
                        { Kind = ParticleModifierKind.Rotation, Active = active, BlockIndex = modRef };
                }

                return new RotationModifierDefinition
                {
                    Kind = ParticleModifierKind.Rotation,
                    Active = active,
                    BlockIndex = modRef,
                    RotationSpeed = BinaryUtils.ReadFloat(data, p, be),
                    RotationSpeedVariation = p + 8 <= end ? BinaryUtils.ReadFloat(data, p + 4, be) : 0f,
                    RotationAngle = p + 12 <= end ? BinaryUtils.ReadFloat(data, p + 8, be) : 0f,
                    RotationAngleVariation = p + 16 <= end ? BinaryUtils.ReadFloat(data, p + 12, be) : 0f,
                    RandomSpeedSign = p + 17 <= end && data[p + 16] != 0
                };
            }

            case "BSPSysSubTexModifier":
                if (p + 28 > end)
                {
                    return new ParticleModifierDefinition
                        { Kind = ParticleModifierKind.Subtexture, Active = active, BlockIndex = modRef };
                }

                return new SubtextureModifierDefinition
                {
                    Kind = ParticleModifierKind.Subtexture,
                    Active = active,
                    BlockIndex = modRef,
                    StartFrame = BinaryUtils.ReadFloat(data, p, be),
                    StartFrameFudge = BinaryUtils.ReadFloat(data, p + 4, be),
                    EndFrame = BinaryUtils.ReadFloat(data, p + 8, be),
                    LoopStartFrame = BinaryUtils.ReadFloat(data, p + 12, be),
                    LoopStartFrameFudge = BinaryUtils.ReadFloat(data, p + 16, be),
                    FrameCount = BinaryUtils.ReadFloat(data, p + 20, be),
                    FrameCountFudge = BinaryUtils.ReadFloat(data, p + 24, be)
                };

            case "NiPSysSpawnModifier":
            {
                // nif.xml: NumSpawnGenerations(ushort 2) + PercentageSpawned(float 4) + MinToSpawn(ushort 2) +
                // MaxToSpawn(ushort 2) + SpawnSpeedVar(float 4) + SpawnDirVar(float 4) + LifeSpan(float 4) +
                // LifeSpanVar(float 4). Packed (NIF has no field alignment padding).
                if (p + 26 > end)
                {
                    return new ParticleModifierDefinition
                        { Kind = ParticleModifierKind.Spawn, Active = active, BlockIndex = modRef };
                }

                return new SpawnModifierDefinition
                {
                    Kind = ParticleModifierKind.Spawn, Active = active, BlockIndex = modRef,
                    NumSpawnGenerations = BinaryUtils.ReadUInt16(data, p, be),
                    PercentageSpawned = BinaryUtils.ReadFloat(data, p + 2, be),
                    MinToSpawn = BinaryUtils.ReadUInt16(data, p + 6, be),
                    MaxToSpawn = BinaryUtils.ReadUInt16(data, p + 8, be),
                    SpawnSpeedVariation = BinaryUtils.ReadFloat(data, p + 10, be),
                    SpawnDirVariation = BinaryUtils.ReadFloat(data, p + 14, be),
                    LifeSpan = BinaryUtils.ReadFloat(data, p + 18, be),
                    LifeSpanVariation = BinaryUtils.ReadFloat(data, p + 22, be)
                };
            }
            case "NiPSysBoundUpdateModifier":
                return new ParticleModifierDefinition
                    { Kind = ParticleModifierKind.BoundUpdate, Active = active, BlockIndex = modRef };

            default:
                return new ParticleModifierDefinition
                    { Kind = ParticleModifierKind.Other, Active = active, BlockIndex = modRef };
        }
    }

    private static ParticleEmitterDefinition ParseEmitter(byte[] data, NifInfo nif, BlockInfo block, int modRef,
        bool active)
    {
        var be = nif.IsBigEndian;
        var end = block.DataOffset + block.Size;
        var p = block.DataOffset + ModifierBaseSize; // NiPSysEmitter base fields start here

        // NiPSysEmitter base: Speed, SpeedVar, Decl, DeclVar, Planar, PlanarVar, InitialColor(16),
        // InitialRadius, RadiusVar, LifeSpan, LifeSpanVar  = 56 bytes (FO3/FNV; RadiusVar present).
        var speed = Read(p);
        var speedVar = Read(p + 4);
        var decl = Read(p + 8);
        var declVar = Read(p + 12);
        var planar = Read(p + 16);
        var planarVar = Read(p + 20);
        var initialColor = new Vector4(Read(p + 24), Read(p + 28), Read(p + 32), Read(p + 36));
        var initialRadius = Read(p + 40);
        var radiusVar = Read(p + 44);
        var lifeSpan = Read(p + 48);
        var lifeSpanVar = Read(p + 52);
        var afterBase = p + 56;

        var shape = ParticleEmitterShape.Point;
        float width = 0, height = 0, depth = 0, radius = 0;
        var emitterObjectTransform = Matrix4x4.Identity;
        var emitterObjectIndex = -1;
        List<int> meshIndices = [];
        var emissionAxis = Vector3.UnitZ; // +Z = declination reference for volume emitters (mesh emitter overrides)
        var velocityType = ParticleVelocityType.UseDirection;
        var emitFrom = ParticleEmitFrom.Vertices;

        switch (block.TypeName)
        {
            case "NiPSysBoxEmitter":
            {
                // Volume emitter: Emitter Object(Ptr 4) then Width, Height, Depth.
                var emitterObj = afterBase + 4 <= end ? BinaryUtils.ReadInt32(data, afterBase, be) : -1;
                emitterObjectTransform = ResolveObjectTransform(data, nif, emitterObj);
                emitterObjectIndex = emitterObj;
                var v = afterBase + 4;
                if (v + 12 <= end)
                {
                    shape = ParticleEmitterShape.Box;
                    width = Read(v);
                    height = Read(v + 4);
                    depth = Read(v + 8);
                }

                break;
            }
            case "NiPSysSphereEmitter":
            {
                var emitterObj = afterBase + 4 <= end ? BinaryUtils.ReadInt32(data, afterBase, be) : -1;
                emitterObjectTransform = ResolveObjectTransform(data, nif, emitterObj);
                emitterObjectIndex = emitterObj;
                var v = afterBase + 4;
                if (v + 4 <= end)
                {
                    shape = ParticleEmitterShape.Sphere;
                    radius = Read(v);
                }

                break;
            }
            case "NiPSysCylinderEmitter":
            {
                var emitterObj = afterBase + 4 <= end ? BinaryUtils.ReadInt32(data, afterBase, be) : -1;
                emitterObjectTransform = ResolveObjectTransform(data, nif, emitterObj);
                emitterObjectIndex = emitterObj;
                var v = afterBase + 4;
                if (v + 8 <= end)
                {
                    shape = ParticleEmitterShape.Cylinder;
                    radius = Read(v);
                    height = Read(v + 4);
                }

                break;
            }
            case "NiPSysMeshEmitter":
            {
                shape = ParticleEmitterShape.Mesh;
                meshIndices = ReadEmitterMeshRefs(data, block, be)
                    .Where(r => r >= 0 && r < nif.Blocks.Count).ToList();
                // After the emitter-mesh refs: Initial Velocity Type(4) + Emission Type(4) + Emission Axis(12).
                // Cursor advancement must use the AUTHORED ref count. meshIndices deliberately filters invalid
                // refs, and using its count shifted these fields left whenever a sparse/broken ref was present.
                var authoredMeshCount = afterBase + 4 <= end
                    ? BinaryUtils.ReadUInt32(data, afterBase, be)
                    : 0u;
                var refsEndLong = afterBase + 4L + authoredMeshCount * 4L;
                if (authoredMeshCount <= 64 && refsEndLong + 8 + 12 <= end)
                {
                    var refsEnd = (int)refsEndLong;
                    velocityType = Enum.IsDefined(typeof(ParticleVelocityType),
                        (int)BinaryUtils.ReadUInt32(data, refsEnd, be))
                        ? (ParticleVelocityType)BinaryUtils.ReadUInt32(data, refsEnd, be)
                        : ParticleVelocityType.UseDirection;
                    emitFrom = Enum.IsDefined(typeof(ParticleEmitFrom),
                        (int)BinaryUtils.ReadUInt32(data, refsEnd + 4, be))
                        ? (ParticleEmitFrom)BinaryUtils.ReadUInt32(data, refsEnd + 4, be)
                        : ParticleEmitFrom.Vertices;
                    emissionAxis = ReadVector3(data, refsEnd + 8, be);
                }

                break;
            }
        }

        return new ParticleEmitterDefinition
        {
            Kind = ParticleModifierKind.Emitter,
            Active = active,
            BlockIndex = modRef,
            Shape = shape,
            Speed = speed,
            SpeedVariation = speedVar,
            Declination = decl,
            DeclinationVariation = declVar,
            PlanarAngle = planar,
            PlanarAngleVariation = planarVar,
            InitialColor = initialColor,
            InitialRadius = initialRadius,
            RadiusVariation = radiusVar,
            LifeSpan = lifeSpan,
            LifeSpanVariation = lifeSpanVar,
            EmissionAxis = emissionAxis,
            VelocityType = velocityType,
            EmitFrom = emitFrom,
            Width = width,
            Height = height,
            Depth = depth,
            Radius = radius,
            EmitterObjectTransform = emitterObjectTransform,
            EmitterObjectIndex = emitterObjectIndex,
            EmitterMeshIndices = meshIndices
        };

        float Read(int offset)
        {
            return offset + 4 <= end ? BinaryUtils.ReadFloat(data, offset, be) : 0f;
        }
    }

    /// <summary>Read a NiPSysMeshEmitter's Emitter Meshes refs (Num Emitter Meshes uint + N Ptr int32).</summary>
    private static IEnumerable<int> ReadEmitterMeshRefs(byte[] data, BlockInfo block, bool be)
    {
        var end = block.DataOffset + block.Size;
        var p = block.DataOffset + ModifierBaseSize + 56; // after NiPSysEmitter base
        if (p + 4 > end)
        {
            yield break;
        }

        var count = BinaryUtils.ReadUInt32(data, p, be);
        p += 4;
        if (count > 64)
        {
            yield break;
        }

        for (var i = 0; i < count && p + 4 <= end; i++, p += 4)
        {
            yield return BinaryUtils.ReadInt32(data, p, be);
        }
    }

    private static Matrix4x4 ResolveObjectTransform(byte[] data, NifInfo nif, int objRef)
    {
        if (objRef < 0 || objRef >= nif.Blocks.Count)
        {
            return Matrix4x4.Identity;
        }

        return NifObjectBlockReader.ParseNiAVObjectTransform(data, nif.Blocks[objRef], nif.BsVersion,
            nif.BinaryVersion, nif.IsBigEndian, nif.HasInlineStrings);
    }

    /// <summary>
    ///     Skip a NiGeometry MaterialData block (NIF 20.2.0.7): Num Materials(4) + Names(4×N) + Extra(4×N)
    ///     + Active Material(4) + Material Needs Update(bool). Mirrors the same skip in NifSceneGraphBlockReader.
    /// </summary>
    private static bool SkipMaterialData(byte[] data, ref int pos, int end, bool be)
    {
        if (pos + 4 > end)
        {
            return false;
        }

        var numMaterials = BinaryUtils.ReadUInt32(data, pos, be);
        pos += 4;
        if (numMaterials > 1000)
        {
            return false;
        }

        pos += (int)numMaterials * 4; // Material Names
        pos += (int)numMaterials * 4; // Material Extra Data
        pos += 4; // Active Material
        pos += 1; // Material Needs Update
        return pos <= end;
    }

    private static Vector3 ReadVector3(byte[] data, int offset, bool be)
    {
        return new Vector3(BinaryUtils.ReadFloat(data, offset, be),
            BinaryUtils.ReadFloat(data, offset + 4, be),
            BinaryUtils.ReadFloat(data, offset + 8, be));
    }

    private static Vector4 ReadColor4(byte[] data, int offset, bool be)
    {
        return new Vector4(BinaryUtils.ReadFloat(data, offset, be),
            BinaryUtils.ReadFloat(data, offset + 4, be),
            BinaryUtils.ReadFloat(data, offset + 8, be),
            BinaryUtils.ReadFloat(data, offset + 12, be));
    }

    /// <summary>
    ///     Read one NiPSysData Subtexture Offset. NIF serializes
    ///     <c>(uOffset, uScale, vOffset, vScale)</c>; particle geometry consumes
    ///     <c>(uOffset, vOffset, uScale, vScale)</c>. Keeping this conversion in an independently
    ///     testable helper prevents endian/parser changes from silently reintroducing zero-width frames.
    /// </summary>
    internal static Vector4 ReadAtlasRectangle(byte[] data, int offset, bool isBigEndian)
    {
        var authored = ReadColor4(data, offset, isBigEndian);
        return new Vector4(authored.X, authored.Z, authored.Y, authored.W);
    }

    /// <summary>
    ///     Read a NiColorData block's key array (NiPSysColorModifier's gradient): Num Keys(uint) +
    ///     Interpolation(uint) + Keys[]. Each key is Time(float) + Color4; QUADRATIC interpolation appends
    ///     two Color4 tangents per key (skipped). Returns keys sorted by time, or empty when absent.
    /// </summary>
    private static ParticleColorKey[] ReadColorDataKeys(byte[] data, NifInfo nif, int colorDataRef)
    {
        if (colorDataRef < 0 || colorDataRef >= nif.Blocks.Count ||
            nif.Blocks[colorDataRef].TypeName != "NiColorData")
        {
            return [];
        }

        var block = nif.Blocks[colorDataRef];
        var be = nif.IsBigEndian;
        var pos = block.DataOffset;
        var end = block.DataOffset + block.Size;
        if (pos + 8 > end)
        {
            return [];
        }

        var numKeys = BinaryUtils.ReadUInt32(data, pos, be);
        pos += 4;
        if (numKeys == 0 || numKeys > 1024)
        {
            return [];
        }

        var interpolation = BinaryUtils.ReadUInt32(data, pos, be);
        pos += 4;
        var keyStride = 4 + 16 + (interpolation == 2 ? 32 : 0); // QUADRATIC: + forward/backward tangents
        if (pos + numKeys * keyStride > end)
        {
            return [];
        }

        var keys = new ParticleColorKey[numKeys];
        for (var i = 0; i < numKeys; i++)
        {
            var time = BinaryUtils.ReadFloat(data, pos, be);
            var color = ReadColor4(data, pos + 4, be);
            keys[i] = new ParticleColorKey(time, color);
            pos += keyStride;
        }

        // Normalize: keys are in seconds; map to [0,1] by the last key time so Sample(lifeFrac) works.
        var maxTime = keys[^1].Time;
        if (maxTime > 1e-4f && MathF.Abs(maxTime - 1f) > 1e-3f)
        {
            for (var i = 0; i < keys.Length; i++)
            {
                keys[i] = keys[i] with { Time = keys[i].Time / maxTime };
            }
        }

        return keys;
    }
}
