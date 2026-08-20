namespace BethesdaMultitool.Core.Formats.Nif.Parser;

/// <summary>
///     NIF binary-version thresholds and field-presence predicates, derived from the nif.xml field
///     version gates. NIF layout decisions are keyed on the in-file NIF version, NEVER on a game
///     identity: a single game ships meshes across several versions (Oblivion alone uses 10.1.0.101 /
///     10.1.0.106, 10.2.0.0 and 20.0.0.4/5), so gating on "is Morrowind" — or on bsVersion==0 as a
///     Morrowind proxy — is structurally wrong. Use these predicates against <c>NifInfo.BinaryVersion</c>.
///     <para>Versions are the canonical packed form, one byte per octet: 10.1.0.114 → 0x0A010072.</para>
/// </summary>
internal static class NifVersions
{
    public const uint NetImmerse4002 = 0x04000002; // Morrowind / Freedom Force
    public const uint NetImmerse4210 = 0x04020100; // 4.2.1.0: NiSkinData gains the Has Vertex Weights byte

    public const uint
        NetImmerse4220 =
            0x04020200; // last NetImmerse: Velocity, single Extra Data ref, pre-Group-ID geometry, 32-bit bools

    public const uint
        Gamebryo10010 = 0x0A000100; // 10.0.1.0: Collision Object ref + Extra Data List replace the legacy fields here

    public const uint
        Gamebryo10012 = 0x0A000102; // 10.0.1.2: first version carrying a BSStreamHeader (still no User Version)

    public const uint Gamebryo10013 = 0x0A000103; // 10.0.1.3: NiTriStripsData gains the "Has Points" bool
    public const uint Gamebryo10018 = 0x0A000108; // 10.0.1.8: the Header User Version field is added here
    public const uint Gamebryo10100 = 0x0A010000; // 10.1.0.0: lower bound of the Gamebryo BSStreamHeader version range
    public const uint Gamebryo101101 = 0x0A010065; // 10.1.0.101: NiSkinInstance gains the Skin Partition ref
    public const uint Gamebryo101114 = 0x0A010072; // NiGeometryData.Group ID is added here
    public const uint Gamebryo10200 = 0x0A020000; // Oblivion 10.2.0.0 (first without the per-block legacy word)

    public const uint
        Gamebryo20004 = 0x14000004; // Oblivion 20.0.0.4 (upper bound of the Gamebryo BSStreamHeader range)

    public const uint Gamebryo20005 = 0x14000005; // Oblivion 20.0.0.5
    public const uint Gamebryo202007 = 0x14020007; // FO3 / FNV / Skyrim / FO4 / Starfield

    /// <summary>
    ///     Legacy NetImmerse geometry-data layout (≤ 4.2.2.0, i.e. Morrowind 4.0.0.2): no Group ID,
    ///     32-bit booleans, an always-present Bounding Sphere, and the legacy Data Flags + Has-UV form. Also
    ///     selects the Morrowind NiTexturingProperty variant (extra Apply Mode field).
    /// </summary>
    public static bool IsLegacyNetImmerse(uint binaryVersion)
    {
        return binaryVersion <= NetImmerse4220;
    }

    /// <summary>
    ///     TES4-era Gamebryo (post-NetImmerse, up to Oblivion's 20.0.0.5 — spans 10.x and
    ///     20.0.0.4/5). Oblivion's renderer composes the scene ROOT node's authored transform under the
    ///     REFR placement instead of replacing it: ChorrolLODHouse01's root bakes a −90°-about-X
    ///     Y-up→Z-up correction and the RFN dungeon halls bake 90/180° Z yaws. FO3+ (20.2.0.7) replaces
    ///     the root's world transform, so its identity-root treatment must not apply here.
    /// </summary>
    public static bool IsTes4Era(uint binaryVersion)
    {
        return binaryVersion > NetImmerse4220 && binaryVersion <= Gamebryo20005;
    }

    /// <summary>
    ///     NiObjectNET stores Num Extra Data List + refs (since 10.0.1.0); older NIFs have a single
    ///     Extra Data ref (until 4.2.2.0).
    /// </summary>
    public static bool HasExtraDataList(uint binaryVersion)
    {
        return binaryVersion >= Gamebryo10010;
    }

    /// <summary>NiAVObject carries a Velocity vector (until 4.2.2.0).</summary>
    public static bool HasAvObjectVelocity(uint binaryVersion)
    {
        return binaryVersion <= NetImmerse4220;
    }

    /// <summary>
    ///     NiAVObject has a Collision Object ref (since 10.0.1.0); older NIFs have a Has Bounding
    ///     Volume flag + BoundingVolume union instead.
    /// </summary>
    public static bool HasCollisionObjectRef(uint binaryVersion)
    {
        return binaryVersion >= Gamebryo10010;
    }

    /// <summary>
    ///     NiGeometryData begins with a Group ID int (since 10.1.0.114); Morrowind and Oblivion's
    ///     10.1.0.101 / 10.1.0.106 architecture omit it.
    /// </summary>
    public static bool HasGeometryGroupId(uint binaryVersion)
    {
        return binaryVersion >= Gamebryo101114;
    }

    /// <summary>
    ///     NiGeometryData uses the "modern" Gamebryo base — a Data Flags ushort, an always-present
    ///     Bounding Sphere, flag-driven UV sets and a trailing Consistency Flags — since 10.0.1.0. Below that
    ///     (Morrowind 4.0.0.2) the geometry data has the older Data-Flags-after-colors + Has-UV layout.
    /// </summary>
    public static bool HasModernGeometryBase(uint binaryVersion)
    {
        return binaryVersion >= Gamebryo10010;
    }

    /// <summary>
    ///     NiGeometryData carries Keep Flags + Compress Flags (two bytes, right after Num Vertices)
    ///     since 10.1.0.0. The oldest Oblivion Gamebryo meshes — 10.0.1.0 / 10.0.1.2 — have the modern base
    ///     (<see cref="HasModernGeometryBase" />) but NOT these two bytes; skipping them anyway desyncs the
    ///     vertex array and yields no geometry.
    /// </summary>
    public static bool HasGeometryKeepFlags(uint binaryVersion)
    {
        return binaryVersion >= Gamebryo10100;
    }

    /// <summary>
    ///     NiTriShapeData writes a "Has Triangles" bool before its triangle list since 10.1.0.0; at or
    ///     below 10.0.1.2 the triangle array follows Num Triangle Points unconditionally. Reading the bool when
    ///     it isn't there consumes the first triangle index and (when zero) looks like "no triangles".
    /// </summary>
    public static bool HasShapeTriangleFlag(uint binaryVersion)
    {
        return binaryVersion >= Gamebryo10100;
    }

    /// <summary>
    ///     NiTriStripsData writes a "Has Points" bool before its strip points since 10.0.1.3; at or
    ///     below 10.0.1.2 (Oblivion's 10.0.1.0 / 10.0.1.2 meshes) the points follow the strip lengths
    ///     unconditionally.
    /// </summary>
    public static bool HasStripPointsFlag(uint binaryVersion)
    {
        return binaryVersion >= Gamebryo10013;
    }

    /// <summary>
    ///     Pre-10.2.0.0 Gamebryo/Bethesda streams prefix every data block with a 4-byte word that
    ///     nif.xml does not model (observed on Oblivion 10.1.0.101 / 10.1.0.106). See
    ///     <c>NifParser.MeasureLegacyBlocks</c>.
    /// </summary>
    public static bool HasPerBlockLegacyWord(uint binaryVersion)
    {
        return binaryVersion < Gamebryo10200;
    }

    /// <summary>
    ///     The Header carries a User Version uint (since 10.0.1.8). The oldest Gamebryo NIFs Oblivion
    ///     ships — 10.0.1.0 / 10.0.1.2 (groundcover, fort tiles like rf1xhousingtiles) — predate it, so Num
    ///     Blocks follows Version (and the optional BSStreamHeader) directly; reading a User Version there
    ///     would shift the whole header by 4 bytes and desync the block table.
    /// </summary>
    public static bool HasUserVersion(uint binaryVersion)
    {
        return binaryVersion >= Gamebryo10018;
    }

    /// <summary>
    ///     NiSkinInstance carries a Skin Partition ref between Data and Skeleton Root (since
    ///     10.1.0.101). Morrowind's 4.0.0.2 layout is Data + Skeleton Root + Num Bones directly — reading
    ///     the ref there consumes Skeleton Root as the partition and bones[0] as Num Bones.
    /// </summary>
    public static bool HasSkinInstancePartitionRef(uint binaryVersion)
    {
        return binaryVersion >= Gamebryo101101;
    }

    /// <summary>
    ///     NiSkinData carries a Skin Partition ref right after Num Bones (4.0.0.2 – 10.1.0.0);
    ///     later versions moved the partition ref onto NiSkinInstance.
    /// </summary>
    public static bool HasSkinDataPartitionRef(uint binaryVersion)
    {
        return binaryVersion >= NetImmerse4002 && binaryVersion <= Gamebryo10100;
    }

    /// <summary>
    ///     NiSkinData writes a Has Vertex Weights byte before the per-bone list (since 4.2.1.0);
    ///     Morrowind 4.0.0.2 has no byte and ALWAYS stores weights.
    /// </summary>
    public static bool HasSkinDataVertexWeightsFlag(uint binaryVersion)
    {
        return binaryVersion >= NetImmerse4210;
    }

    /// <summary>
    ///     Whether a BSStreamHeader (BS Version + Author/Process/Export/Max-Filepath ExportStrings)
    ///     sits between Num Blocks and the block-types table. Implements nif.xml's <c>#BSSTREAMHEADER#</c>
    ///     gate: 10.0.1.2 always has one; 10.1.0.0–20.0.0.4 (User Version ≤ 11), 20.0.0.5 and 20.2.0.7 have
    ///     one when User Version ≥ 3. Notably 10.0.1.0 has NONE — so it is NOT simply "everything ≥ 10.0.1.0".
    ///     Getting this wrong reads block-type-table bytes as ExportStrings and loses all geometry.
    /// </summary>
    public static bool HasBsStreamHeader(uint binaryVersion, uint userVersion)
    {
        if (binaryVersion == Gamebryo10012)
        {
            return true; // 10.0.1.2 carries a stream header regardless of (absent) User Version
        }

        var inGamebryoRange =
            binaryVersion == Gamebryo202007 ||
            binaryVersion == Gamebryo20005 ||
            (binaryVersion >= Gamebryo10100 && binaryVersion <= Gamebryo20004 && userVersion <= 11);

        return inGamebryoRange && userVersion >= 3;
    }
}
