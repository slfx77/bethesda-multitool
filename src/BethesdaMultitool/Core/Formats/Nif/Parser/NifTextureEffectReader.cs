using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Nif.Parser;

/// <summary>
///     Parsed identity of a <c>NiTextureEffect</c> block — the scene-graph EFFECT that projects a
///     texture onto affected geometry (projected lights/shadows, environment maps, fog maps).
/// </summary>
/// <param name="SwitchState">
///     NiDynamicEffect Switch State (since 10.1.0.106); false = the effect is authored disabled.
///     Versions without the field (Morrowind 4.0.0.2, Oblivion 10.0.x/10.1.0.101) report true.
/// </param>
/// <param name="AffectedNodes">
///     NiDynamicEffect affected-node block refs (10.1.0.0+). "If a node appears in this list, then
///     its entire subtree will be affected by the effect" (nif.xml). Empty = unscoped. Legacy
///     NetImmerse streams store pointer HASHES here, not refs (nif.xml: "the link will be invalid"),
///     so this is always empty for them — retail Morrowind authors a zero count anyway
///     (byte-verified on <c>meshes\a\a_glass_boots_gnd.nif</c> block 16).
/// </param>
/// <param name="TextureType">TextureType enum: 2 = TEX_ENVIRONMENT_MAP.</param>
/// <param name="CoordGenType">CoordGenType enum: 2 = CG_SPHERE_MAP.</param>
/// <param name="SourceTextureRef">Block ref of the NiSourceTexture, or a negative ref for none.</param>
internal readonly record struct NifTextureEffectInfo(
    bool SwitchState,
    int[] AffectedNodes,
    uint TextureType,
    uint CoordGenType,
    int SourceTextureRef);

/// <summary>
///     Reads <c>NiTextureEffect</c> blocks (nif.xml). Layout after the NiObjectNET + NiAVObject +
///     NiDynamicEffect bases: Model Projection Matrix (36) + Model Projection Translation (12) +
///     Texture Filtering (uint) [+ Max Anisotropy ushort, 20.5.0.4+] + Texture Clamping (uint) +
///     Texture Type (uint) + Coordinate Generation Type (uint) + Source Texture (Ref). The trailing
///     clipping-plane / PS2 / legacy-short fields are never read, so their per-version presence
///     cannot desync this parser.
///     <para>
///         The full 4.0.0.2 stream was byte-verified against retail Morrowind
///         <c>meshes\a\a_glass_boots_gnd.nif</c> block 16 (offset 0x3C68, 181 bytes): TextureType 2
///         (ENVIRONMENT_MAP), CoordGen 2 (SPHERE_MAP), Source Texture ref 17, and the trailing
///         <c>PS2 K = -75</c> default landing exactly at the modeled offset. Retail TES4 authors NO
///         NiTextureEffect blocks at all — a 2026-08-19 sweep of all 8,032 NIFs in
///         "Oblivion - Meshes.bsa" plus 1,580 SI/DLC NIFs found zero — its window reflections are
///         an engine-side runtime attachment; this parser exists for TES3 chrome and for TES4-era
///         (modded / runtime-captured) content that does author the effect.
///     </para>
///     Error tolerance mirrors the other block parsers: a truncated or unmodeled block returns
///     null and the effect is simply not applied (block offsets/sizes come from the authoritative
///     measure pass, so a bail never corrupts neighboring blocks).
/// </summary>
internal static class NifTextureEffectReader
{
    internal const uint TextureTypeEnvironmentMap = 2; // TEX_ENVIRONMENT_MAP
    internal const uint CoordGenTypeSphereMap = 2; // CG_SPHERE_MAP

    private const int MaxAffectedNodes = 512;

    /// <summary>10.1.0.106: NiDynamicEffect gains the Switch State bool (nif.xml since-gate).</summary>
    private const uint Gamebryo101106 = 0x0A01006A;

    /// <summary>20.5.0.4: NiTextureEffect gains the Max Anisotropy ushort (absent for TES4/FO3-era).</summary>
    private const uint Gamebryo20504 = 0x14050004;

    internal static NifTextureEffectInfo? Parse(
        byte[] data,
        BlockInfo block,
        uint bsVersion,
        uint binaryVersion,
        bool be,
        bool hasInlineStrings)
    {
        var pos = block.DataOffset;
        var end = block.DataOffset + block.Size;
        if (!NifBinaryCursor.SkipNiObjectNET(data, ref pos, end, be, hasInlineStrings, binaryVersion))
        {
            return null;
        }

        // NiAVObject base (same field walk as NifSceneGraphBlockReader.SkipNiGeometryHeader).
        pos += bsVersion > 26 ? 4 : 2; // Flags (uint Bethesda > 26, else ushort)
        pos += 12 + 36 + 4; // Translation (Vector3) + Rotation (Matrix33) + Scale (float)
        if (NifVersions.HasAvObjectVelocity(binaryVersion))
        {
            pos += 12; // Velocity (Vector3, until 4.2.2.0)
        }

        if (bsVersion <= 34)
        {
            if (pos + 4 > end)
            {
                return null;
            }

            var numProperties = BinaryUtils.ReadUInt32(data, pos, be);
            pos += 4;
            pos += (int)Math.Min(numProperties, 100) * 4;
        }

        if (pos + 4 > end)
        {
            return null;
        }

        if (!NifVersions.HasCollisionObjectRef(binaryVersion))
        {
            // Has Bounding Volume (bool32). The union that follows when set is not modeled —
            // bail, matching the shape parsers' behavior for the same field.
            var hasBoundingVolume = BinaryUtils.ReadUInt32(data, pos, be);
            pos += 4;
            if (hasBoundingVolume != 0)
            {
                return null;
            }
        }
        else
        {
            pos += 4; // Collision Object ref
        }

        // NiDynamicEffect base.
        var switchState = true;
        if (binaryVersion >= Gamebryo101106 && bsVersion < 130)
        {
            if (pos + 1 > end)
            {
                return null;
            }

            switchState = data[pos] != 0;
            pos += 1;
        }

        var affectedNodes = Array.Empty<int>();
        if (binaryVersion <= NifVersions.NetImmerse4002)
        {
            // Legacy: Num Affected Nodes + pointer HASHES (not valid refs — skip them).
            if (pos + 4 > end)
            {
                return null;
            }

            var count = BinaryUtils.ReadUInt32(data, pos, be);
            pos += 4;
            if (count > MaxAffectedNodes || pos + (int)count * 4 > end)
            {
                return null;
            }

            pos += (int)count * 4;
        }
        else if (binaryVersion >= NifVersions.Gamebryo10100 && bsVersion < 130)
        {
            if (pos + 4 > end)
            {
                return null;
            }

            var count = BinaryUtils.ReadUInt32(data, pos, be);
            pos += 4;
            if (count > MaxAffectedNodes || pos + (int)count * 4 > end)
            {
                return null;
            }

            if (count > 0)
            {
                var refs = new List<int>((int)count);
                for (var i = 0; i < count; i++)
                {
                    var nodeRef = BinaryUtils.ReadInt32(data, pos, be);
                    pos += 4;
                    if (nodeRef >= 0)
                    {
                        refs.Add(nodeRef);
                    }
                }

                affectedNodes = [.. refs];
            }
        }
        // else: 4.2.x – 10.0.x carry no affected-node list at all (nif.xml version gates).

        // NiTextureEffect fields.
        pos += 36 + 12; // Model Projection Matrix + Model Projection Translation
        if (pos + 4 > end)
        {
            return null;
        }

        pos += 4; // Texture Filtering (TexFilterMode)
        if (binaryVersion >= Gamebryo20504)
        {
            pos += 2; // Max Anisotropy
        }

        pos += 4; // Texture Clamping (TexClampMode)
        if (pos + 12 > end)
        {
            return null;
        }

        var textureType = BinaryUtils.ReadUInt32(data, pos, be);
        var coordGenType = BinaryUtils.ReadUInt32(data, pos + 4, be);
        var sourceTextureRef = BinaryUtils.ReadInt32(data, pos + 8, be);
        return new NifTextureEffectInfo(
            switchState, affectedNodes, textureType, coordGenType, sourceTextureRef);
    }
}
