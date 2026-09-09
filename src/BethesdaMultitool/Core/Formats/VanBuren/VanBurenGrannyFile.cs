using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Granny;

namespace BethesdaMultitool.Core.Formats.VanBuren;

/// <summary>What a Van Buren Granny payload carries, decided by the objects its root actually holds.</summary>
internal enum VanBurenGrannyKind
{
    /// <summary>
    ///     At least one model binds a mesh (25 retail payloads: 18 collision hulls, <c>deathclaw</c>, and
    ///     six door boxes whose triangle list is unwritten — see <see cref="Gr2Mesh.TopologyDefect" />).
    /// </summary>
    Model,

    /// <summary>
    ///     Skeletons and mesh-less models (74 retail payloads; three of them also carry the clip that
    ///     animates the skeleton, so a Skeleton file may have <see cref="Gr2File.Animations" />).
    /// </summary>
    Skeleton,

    /// <summary>Animation clips only — no skeleton, no mesh (448 retail payloads carrying 448 of the 451 clips).</summary>
    Animation,

    /// <summary>A file with none of the above.</summary>
    Empty
}

/// <summary>
///     The Granny 2 payloads inside the cancelled Van Buren (Fallout 3) prototype's <c>.grp</c>
///     archives, decoded through the ported Granny 2 reader (<see cref="Gr2Container" />,
///     <see cref="Gr2TypeTree" />, <see cref="Gr2File" />).
///     <para>
///         ⚑ Measured 2026-09-08 on all 547 payloads (Critters.grp 437, Items.grp 91, Props.grp 18,
///         Engine.grp 1; found by the 16-byte magic): header size <b>352</b>, header format 0,
///         file-format version <b>6</b>, six sections each, relocation/marshalling/data tiling
///         exactly to EOF on 547/547. Sections 0-4 of every file are <b>Oodle0</b> (2,735) and
///         section 5 raw (547); Oodle1 never occurs. Every expanded section equals its declared size
///         and the type tree walks to a <c>granny_file_info</c> root on 547/547. The exporter is
///         "Granny Standard Exporter, SDK version 2.2.0.0" from LightWave 7.5.
///     </para>
///     <para>
///         ⚠⚠ <b>The character RENDER meshes are NOT here.</b> Only 25 payloads carry a mesh at all:
///         18 collision hulls (<c>CR_Bat_Coll</c>, <c>Female_Collision</c> ×5, <c>Male_Collision</c>
///         ×4), the one render mesh <c>deathclaw</c> (1,420 vertices), and six Props.grp door
///         boxes (<c>DS_City1_Doors:B1Doorbox</c>…) whose 52 vertices are real but whose triangle
///         list is unwritten debug-heap memory (<see cref="Gr2Mesh.TopologyDefect" />; they export
///         as skeletons only). The other 522 are 448 animation-only files and 74
///         skeleton files. Granny here is the ANIMATION system (F3.exe: <c>LoadSkeletonFromBuffer</c>,
///         <c>LoadAnimationFileFromBuffer</c>); the visible creature geometry is the <c>B3D</c>
///         family. No payload carries a texture or a material (0 of 547).
///     </para>
/// </summary>
internal static class VanBurenGrannyFile
{
    /// <summary>Bytes of signature.</summary>
    public const int SignatureLength = Gr2Container.MagicLength;

    /// <summary>Header size every shipped payload declares at <c>+16</c>.</summary>
    public const int DeclaredHeaderSize = 352;

    /// <summary>Whether the bytes are a Granny 2 payload.</summary>
    public static bool IsGranny(ReadOnlySpan<byte> bytes)
    {
        return Gr2Container.IsGranny2(bytes);
    }

    /// <summary>
    ///     The header size the payload declares, or <c>-1</c> when it is not Granny. Checked
    ///     separately from <see cref="IsGranny" /> so a future build declaring a different size is
    ///     reported rather than silently accepted as the 352 this one uses.
    /// </summary>
    public static int ReadDeclaredHeaderSize(ReadOnlySpan<byte> bytes)
    {
        return IsGranny(bytes) && bytes.Length >= SignatureLength + 4
            ? (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes[SignatureLength..])
            : -1;
    }

    /// <summary>Parses the container, expands every section and reads the file's contents.</summary>
    public static Gr2File Parse(byte[] payload, string name)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return Gr2File.Read(Gr2TypeTree.Parse(Gr2Container.Parse(payload, name)));
    }

    /// <summary>Classifies a decoded file by what it holds.</summary>
    public static VanBurenGrannyKind Classify(Gr2File file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (file.Models.Any(static model => model.Meshes.Count > 0) || file.Meshes.Count > 0)
        {
            return VanBurenGrannyKind.Model;
        }

        if (file.Skeletons.Count > 0 || file.Models.Count > 0)
        {
            return VanBurenGrannyKind.Skeleton;
        }

        return file.Animations.Count > 0 ? VanBurenGrannyKind.Animation : VanBurenGrannyKind.Empty;
    }
}
