using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Textures;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Particles;

/// <summary>
///     Parses a FO3/FNV <c>NiParticleSystem</c> block (and its emitter + modifier chain + NiPSysData) into a
///     <see cref="ParticleSystemDefinition" /> the baker can simulate. Layout verified against real data
///     (FXDustWhirlWind01 block 46): after the standard NiGeometry header come Data ref + Skin ref +
///     MaterialData, then NiParticleSystem's World Space (bool) + Num Modifiers (uint) + Modifiers[] refs.
///     The per-particle arrays in NiPSysData are NOT read — the engine fills them by simulation, so the
///     baker re-simulates (see particles_formula_spec.md).
/// </summary>
internal static class NifParticleSystemParser
{
    // NiPSysModifier base (FO3/FNV string-table): Name(int32 index) + Order(uint) + Target(int32) + Active(bool).
    private const int ModifierBaseSize = 4 + 4 + 4 + 1;

    /// <summary>True when the block is a NiParticleSystem (FO3/FNV; BSParticleSystem variants aren't FNV).</summary>
    internal static bool IsParticleSystem(string typeName) =>
        typeName is "NiParticleSystem" or "NiMeshParticleSystem";

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
    internal static ParticleSystemDefinition? Parse(byte[] data, NifInfo nif, int blockIndex)
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

        if (!NifBinaryCursor.SkipNiObjectNET(data, ref pos, end, be, nif.HasInlineStrings))
        {
            return null;
        }

        pos += nif.BsVersion > 26 ? 4 : 2; // Flags
        pos += 12 + 36 + 4; // Translation + Rotation + Scale

        // Properties (NiAVObject, BS stream <= 34): NumProperties + refs. Capture for texture/blend.
        var propertyRefs = new List<int>();
        if (pos + 4 > end)
        {
            return null;
        }

        var numProperties = BinaryUtils.ReadUInt32(data, pos, be);
        pos += 4;
        if (numProperties > 100 || pos + (long)numProperties * 4 > end)
        {
            return null;
        }

        for (var i = 0; i < numProperties; i++)
        {
            var propRef = BinaryUtils.ReadInt32(data, pos, be);
            pos += 4;
            if (propRef >= 0 && propRef < nif.Blocks.Count)
            {
                propertyRefs.Add(propRef);
            }
        }

        pos += 4; // Collision Object ref

        if (pos + 4 > end)
        {
            return null;
        }

        var dataRef = BinaryUtils.ReadInt32(data, pos, be);
        pos += 4; // Data ref (NiPSysData)
        pos += 4; // Skin Instance ref

        if (!SkipMaterialData(data, ref pos, end, be) || pos + 1 + 4 > end)
        {
            return null;
        }

        var worldSpace = data[pos] != 0;
        pos += 1;

        var numModifiers = BinaryUtils.ReadUInt32(data, pos, be);
        pos += 4;
        if (numModifiers > 100 || pos + (long)numModifiers * 4 > end)
        {
            return null;
        }

        var modifierRefs = new List<int>((int)numModifiers);
        for (var i = 0; i < numModifiers; i++)
        {
            var modRef = BinaryUtils.ReadInt32(data, pos, be);
            pos += 4;
            if (modRef >= 0 && modRef < nif.Blocks.Count)
            {
                modifierRefs.Add(modRef);
            }
        }

        var capacity = ReadDataCapacity(data, nif, dataRef);
        var def = new ParticleSystemDefinition
        {
            BlockIndex = blockIndex,
            WorldSpace = worldSpace,
            Capacity = capacity,
        };

        foreach (var modRef in modifierRefs)
        {
            var modifier = ParseModifier(data, nif, modRef);
            if (modifier is null)
            {
                continue;
            }

            def.Modifiers.Add(modifier);
            if (modifier is ParticleEmitterDefinition emitter && def.Emitter is null)
            {
                def.Emitter = emitter;
            }
        }

        // Steady-state density comes from the BIRTH RATE, not the capacity (which is just the buffer max). The
        // rate lives on the NiPSysEmitterCtlr's interpolator; without it the bake fills to capacity and dust
        // stacks to opaque. Sets it on the emitter so the baker maintains birthRate·lifespan live particles.
        if (def.Emitter is not null)
        {
            def.Emitter.BirthRate = ResolveBirthRate(data, nif, blockIndex);
        }

        ResolveAppearance(data, nif, propertyRefs, def);
        return def;
    }

    /// <summary>
    ///     Resolve the steady-state birth rate (particles/sec) for the system's emitter. The rate is animated
    ///     through the NiControllerManager, so it isn't on the emitter ctlr's (blend) interpolator directly —
    ///     it's on the real NiFloatInterpolator referenced by the matching ControlledBlock in a
    ///     NiControllerSequence. Returns 0 when it can't be found (the baker then falls back to capacity).
    /// </summary>
    private static float ResolveBirthRate(byte[] data, NifInfo nif, int systemIndex)
    {
        var be = nif.IsBigEndian;

        // 1. The NiPSysEmitterCtlr that targets this system (NiTimeController.Target at +22).
        var emitterCtlr = -1;
        for (var i = 0; i < nif.Blocks.Count; i++)
        {
            if (nif.Blocks[i].TypeName != "NiPSysEmitterCtlr")
            {
                continue;
            }

            var b = nif.Blocks[i];
            if (b.DataOffset + 26 <= b.DataOffset + b.Size &&
                BinaryUtils.ReadInt32(data, b.DataOffset + 22, be) == systemIndex)
            {
                emitterCtlr = i;
                break;
            }
        }

        if (emitterCtlr < 0)
        {
            return 0f;
        }

        // 2. Find the ControlledBlock referencing that ctlr inside a NiControllerSequence. Each ControlledBlock
        //    begins [Interpolator(int32)][Controller(int32)], so scan for Controller == emitterCtlr and take the
        //    preceding int32 as the interpolator — validated by requiring it to be a NiFloatInterpolator.
        for (var i = 0; i < nif.Blocks.Count; i++)
        {
            if (nif.Blocks[i].TypeName != "NiControllerSequence")
            {
                continue;
            }

            var b = nif.Blocks[i];
            var end = b.DataOffset + b.Size;
            for (var p = b.DataOffset + 4; p + 4 <= end; p += 4)
            {
                if (BinaryUtils.ReadInt32(data, p, be) != emitterCtlr)
                {
                    continue;
                }

                var interp = BinaryUtils.ReadInt32(data, p - 4, be);
                if (interp < 0 || interp >= nif.Blocks.Count ||
                    nif.Blocks[interp].TypeName != "NiFloatInterpolator")
                {
                    continue;
                }

                var rate = ReadFloatInterpolatorValue(data, nif, interp);
                if (rate > 0f)
                {
                    return rate;
                }
            }
        }

        return 0f;
    }

    /// <summary>Read a NiFloatInterpolator's effective value: the constant Value when set, else the peak key of
    /// its NiFloatData (the busiest moment of an animated rate). Returns 0 when neither is usable.</summary>
    private static float ReadFloatInterpolatorValue(byte[] data, NifInfo nif, int interp)
    {
        var be = nif.IsBigEndian;
        var b = nif.Blocks[interp];
        var value = BinaryUtils.ReadFloat(data, b.DataOffset, be);
        if (float.IsFinite(value) && value is > 0f and < 1e30f)
        {
            return value;
        }

        var dataRef = BinaryUtils.ReadInt32(data, b.DataOffset + 4, be);
        if (dataRef < 0 || dataRef >= nif.Blocks.Count || nif.Blocks[dataRef].TypeName != "NiFloatData")
        {
            return 0f;
        }

        var db = nif.Blocks[dataRef];
        var pos = db.DataOffset;
        var dend = db.DataOffset + db.Size;
        if (pos + 8 > dend)
        {
            return 0f;
        }

        var numKeys = BinaryUtils.ReadUInt32(data, pos, be);
        var interpolation = BinaryUtils.ReadUInt32(data, pos + 4, be);
        pos += 8;
        if (numKeys == 0 || numKeys > 1024)
        {
            return 0f;
        }

        var stride = 4 + 4 + (interpolation == 2 ? 8 : 0); // QUADRATIC: + in/out tangents (2 floats)
        var peak = 0f;
        for (var k = 0; k < numKeys && pos + 8 <= dend; k++, pos += stride)
        {
            peak = MathF.Max(peak, BinaryUtils.ReadFloat(data, pos + 4, be));
        }

        return peak;
    }

    /// <summary>Resolve the particle sprite texture + blend mode from the system's property refs.</summary>
    private static void ResolveAppearance(byte[] data, NifInfo nif, List<int> propertyRefs, ParticleSystemDefinition def)
    {
        // Particle sprites carry their texture on a shader property (BSShaderNoLighting/Effect/PP) OR a
        // legacy NiTexturingProperty (FO3/FNV particles commonly use the latter). Try the shader reader
        // first, then fall back to the texturing reader so the wisp/sprite isn't lost (→ white particles).
        def.DiffuseTexturePath = NifShaderTexturePropertyReader.ResolveDiffusePath(data, nif, propertyRefs)
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
                    BaseScale = BinaryUtils.ReadFloat(data, p + 12, be),
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
                    SymmetryType = (int)BinaryUtils.ReadUInt32(data, p + 28, be),
                };
            }

            case "NiPSysGravityModifier":
            {
                // Gravity Object(Ptr 4) + Gravity Axis(12) + Decay(4) + Strength(4) + Force Type(4) + ...
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
                };
            }

            case "BSPSysSimpleColorModifier":
            {
                // Fade In/Out Percent + Color1 End/Start + Color2 End/Start (note End-before-Start) + Colors[3].
                if (p + 24 + 48 > end) return new ColorModifierDefinition { Kind = ParticleModifierKind.Color, Active = active, BlockIndex = modRef };
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
                    Color2 = ReadColor4(data, p + 56, be),
                };
            }

            case "NiPSysColorModifier":
                // References a NiColorData block of (time → RGBA) keys.
                return new ColorModifierDefinition
                {
                    Kind = ParticleModifierKind.Color, Active = active, BlockIndex = modRef,
                    Keys = p + 4 <= end ? ReadColorDataKeys(data, nif, BinaryUtils.ReadInt32(data, p, be)) : [],
                };

            case "NiPSysDragModifier":
            {
                // Drag Object(Ptr 4) + Drag Axis(12) + Percentage(4) + Range(4) + Range Falloff(4).
                if (p + 4 + 12 + 12 > end)
                {
                    return new ParticleModifierDefinition { Kind = ParticleModifierKind.Drag, Active = active, BlockIndex = modRef };
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
                    RangeFalloff = BinaryUtils.ReadFloat(data, p + 24, be),
                };
            }

            case "NiPSysAgeDeathModifier":
                return new ParticleModifierDefinition { Kind = ParticleModifierKind.AgeDeath, Active = active, BlockIndex = modRef };
            case "NiPSysPositionModifier":
                return new ParticleModifierDefinition { Kind = ParticleModifierKind.Position, Active = active, BlockIndex = modRef };
            case "NiPSysRotationModifier":
                return new ParticleModifierDefinition { Kind = ParticleModifierKind.Rotation, Active = active, BlockIndex = modRef };

            case "NiPSysSpawnModifier":
            {
                // nif.xml: NumSpawnGenerations(ushort 2) + PercentageSpawned(float 4) + MinToSpawn(ushort 2) +
                // MaxToSpawn(ushort 2) + SpawnSpeedVar(float 4) + SpawnDirVar(float 4) + LifeSpan(float 4) +
                // LifeSpanVar(float 4). Packed (NIF has no field alignment padding).
                if (p + 26 > end)
                {
                    return new ParticleModifierDefinition { Kind = ParticleModifierKind.Spawn, Active = active, BlockIndex = modRef };
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
                    LifeSpanVariation = BinaryUtils.ReadFloat(data, p + 22, be),
                };
            }
            case "NiPSysBoundUpdateModifier":
                return new ParticleModifierDefinition { Kind = ParticleModifierKind.BoundUpdate, Active = active, BlockIndex = modRef };

            default:
                return new ParticleModifierDefinition { Kind = ParticleModifierKind.Other, Active = active, BlockIndex = modRef };
        }
    }

    private static ParticleEmitterDefinition ParseEmitter(byte[] data, NifInfo nif, BlockInfo block, int modRef, bool active)
    {
        var be = nif.IsBigEndian;
        var end = block.DataOffset + block.Size;
        var p = block.DataOffset + ModifierBaseSize; // NiPSysEmitter base fields start here

        // NiPSysEmitter base: Speed, SpeedVar, Decl, DeclVar, Planar, PlanarVar, InitialColor(16),
        // InitialRadius, RadiusVar, LifeSpan, LifeSpanVar  = 56 bytes (FO3/FNV; RadiusVar present).
        float speed = Read(p);
        float speedVar = Read(p + 4);
        float decl = Read(p + 8);
        float declVar = Read(p + 12);
        float planar = Read(p + 16);
        float planarVar = Read(p + 20);
        var initialColor = new Vector4(Read(p + 24), Read(p + 28), Read(p + 32), Read(p + 36));
        float initialRadius = Read(p + 40);
        float radiusVar = Read(p + 44);
        float lifeSpan = Read(p + 48);
        float lifeSpanVar = Read(p + 52);
        var afterBase = p + 56;

        var shape = ParticleEmitterShape.Point;
        float width = 0, height = 0, depth = 0, radius = 0;
        var emitterObjectTransform = Matrix4x4.Identity;
        IReadOnlyList<int> meshIndices = [];
        var emissionAxis = Vector3.UnitZ; // +Z = declination reference for volume emitters (mesh emitter overrides)

        switch (block.TypeName)
        {
            case "NiPSysBoxEmitter":
            {
                // Volume emitter: Emitter Object(Ptr 4) then Width, Height, Depth.
                var emitterObj = afterBase + 4 <= end ? BinaryUtils.ReadInt32(data, afterBase, be) : -1;
                emitterObjectTransform = ResolveObjectTransform(data, nif, emitterObj);
                var v = afterBase + 4;
                if (v + 12 <= end)
                {
                    shape = ParticleEmitterShape.Box;
                    width = Read(v); height = Read(v + 4); depth = Read(v + 8);
                }
                break;
            }
            case "NiPSysSphereEmitter":
            {
                var emitterObj = afterBase + 4 <= end ? BinaryUtils.ReadInt32(data, afterBase, be) : -1;
                emitterObjectTransform = ResolveObjectTransform(data, nif, emitterObj);
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
                var v = afterBase + 4;
                if (v + 8 <= end)
                {
                    shape = ParticleEmitterShape.Cylinder;
                    radius = Read(v); height = Read(v + 4);
                }
                break;
            }
            case "NiPSysMeshEmitter":
            {
                shape = ParticleEmitterShape.Mesh;
                meshIndices = ReadEmitterMeshRefs(data, block, be)
                    .Where(r => r >= 0 && r < nif.Blocks.Count).ToList();
                // After the emitter-mesh refs: Initial Velocity Type(4) + Emission Type(4) + Emission Axis(12).
                var refsEnd = afterBase + 4 + meshIndices.Count * 4;
                if (refsEnd + 8 + 12 <= end)
                {
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
            Width = width,
            Height = height,
            Depth = depth,
            Radius = radius,
            EmitterObjectTransform = emitterObjectTransform,
            EmitterMeshIndices = meshIndices,
        };

        float Read(int offset) => offset + 4 <= end ? BinaryUtils.ReadFloat(data, offset, be) : 0f;
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

    private static Vector3 ReadVector3(byte[] data, int offset, bool be) =>
        new(BinaryUtils.ReadFloat(data, offset, be),
            BinaryUtils.ReadFloat(data, offset + 4, be),
            BinaryUtils.ReadFloat(data, offset + 8, be));

    private static Vector4 ReadColor4(byte[] data, int offset, bool be) =>
        new(BinaryUtils.ReadFloat(data, offset, be),
            BinaryUtils.ReadFloat(data, offset + 4, be),
            BinaryUtils.ReadFloat(data, offset + 8, be),
            BinaryUtils.ReadFloat(data, offset + 12, be));

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
        if (pos + (long)numKeys * keyStride > end)
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
