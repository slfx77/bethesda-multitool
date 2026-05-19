using System.Buffers.Binary;
using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Output;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Pipeline;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Nav;

/// <summary>
///     Per-NAVM entry for building NavMeshInfoMap (NAVI) override subrecords.
/// </summary>
/// <param name="NavmFormId">Allocated PC plugin FormID of the newly-emitted NAVM.</param>
/// <param name="LocationFormId">
///     NVMI "Location" field — for exterior NAVMs this is the parent worldspace FormID;
///     for interior NAVMs it should be the cell FormID. Verified against master's first
///     NVMI: NAVM 0x00136567 in cell 0x000DDCAB has LocationFormId = 0x000DA726 (the
///     parent WastelandNV worldspace). Setting this to 0 unconditionally caused the
///     engine to null-deref in NavMeshInfoMap setup (crash @ FalloutNV+0x0069DFDC).
/// </param>
/// <param name="IsInterior">True if the NAVM's parent cell is an interior cell.</param>
/// <param name="GridX">Cell grid X; 0 for interior.</param>
/// <param name="GridY">Cell grid Y; 0 for interior.</param>
/// <param name="NvvxBytes">
///     Raw NVVX subrecord payload (12 bytes per Vec3 LE float vertex). Used to compute the
///     approximate centroid (NVMI ApproxX/Y/Z). Null when the source NAVM had no NVVX.
/// </param>
internal readonly record struct NewNavmEntry(
    uint NavmFormId,
    uint LocationFormId,
    bool IsInterior,
    short GridX,
    short GridY,
    byte[]? NvvxBytes);

/// <summary>
///     Builds a NAVI (NavMeshInfoMap, FormID 0x00014B92) override record that extends master's
///     NAVI with NVMI + NVCI subrecord pairs for newly-emitted NAVMs. Required so the engine's
///     NavMeshInfoMap setup can resolve our new NAVM FormIDs during plugin load — without this
///     the engine null-derefs at NavMeshInfoMap iteration (verified on xex4 crash @ FalloutNV+0x0069E09A).
///
///     NVMI byte layout (32 bytes, non-island only — flags bit 5 unset) per
///     <see cref="Conversion.Processing.EsmSubrecordConverter.ConvertNvmi" />:
///         0..3   uint32  Flags (0)
///         4..7   uint32  NavmFormId
///         8..11  uint32  LocationFormId (0 — synthesized NAVMs have no named location)
///         12..13 int16   GridY
///         14..15 int16   GridX
///         16..27 Vec3    ApproxX/Y/Z (centroid from NVVX or grid-center fallback)
///         28..31 float   Preferred% (0)
///
///     NVCI byte layout (16 bytes, empty links) per
///     <see cref="Conversion.Processing.EsmSubrecordConverter.ConvertNvci" />:
///         0..3   uint32  NavmFormId
///         4..7   int32   StandardCount = 0
///         8..11  int32   PreferredCount = 0
///         12..15 int32   DoorLinksCount = 0
/// </summary>
internal static class NavInfoMapBuilder
{
    public const uint MasterNaviFormId = 0x00014B92u;

    private const int NvmiSize = 32;
    private const int NvciSize = 16;

    /// <summary>Builds a 32-byte NVMI subrecord payload for one new NAVM.</summary>
    public static byte[] BuildNvmi(in NewNavmEntry entry)
    {
        var bytes = new byte[NvmiSize];
        // Flags = 0 (non-island).
        // NavmFormId at offset 4.
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), entry.NavmFormId);
        // LocationFormId at offset 8 — parent worldspace for exterior, cell for interior.
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8, 4), entry.LocationFormId);
        // iCellKey at offset 12: bytes 12-13 = gridY (low 16 of packed key), 14-15 = gridX.
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(12, 2), entry.IsInterior ? (short)0 : entry.GridY);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(14, 2), entry.IsInterior ? (short)0 : entry.GridX);

        var (approxX, approxY, approxZ) = ComputeApproxCentroid(entry);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(16, 4), approxX);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(20, 4), approxY);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(24, 4), approxZ);
        // Preferred% at offset 28 = 0.0f (already zero-initialized).
        return bytes;
    }

    /// <summary>Builds a 16-byte NVCI subrecord payload for one new NAVM (empty connection arrays).</summary>
    public static byte[] BuildNvci(in NewNavmEntry entry)
    {
        var bytes = new byte[NvciSize];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), entry.NavmFormId);
        // StandardCount, PreferredCount, DoorLinksCount all zero — empty arrays.
        return bytes;
    }

    /// <summary>
    ///     Builds the full NAVI override record bytes (24-byte main-record header + body).
    ///     Master's NAVI is structured as <c>NVER + [all NVMI] + [all NVCI]</c> (verified
    ///     against FalloutNV.esm: 4771 NVMIs grouped before 4771 NVCIs). FNVEdit treats
    ///     any out-of-group occurrence as a structural error and flags the record. So we
    ///     splice new entries INTO the correct groups: new NVMIs append to the master NVMI
    ///     run; new NVCIs append to the master NVCI run. Other subrecord types (just NVER
    ///     in practice) are emitted in their original positions.
    /// </summary>
    public static byte[] BuildNaviOverride(
        ParsedMainRecord masterNavi,
        IReadOnlyList<NewNavmEntry> newEntries,
        PluginBuildOptions options)
    {
        using var bodyStream = new MemoryStream();
        using (var writer = new BinaryWriter(bodyStream, Encoding.Latin1, true))
        {
            // SubrecordEncoder rewrites the 6-byte subrecord header exactly as the parser
            // produced it; payloads are uint16-sized so we don't risk XXXX extension for
            // master entries (master NVMI/NVCI payloads are far under 64KB).
            //
            // Walk master subrecords in order. When the run of NVMIs ends (next sig != NVMI),
            // flush our new NVMIs first. When the run of NVCIs ends, flush our new NVCIs.
            var newNvmisEmitted = false;
            var newNvcisEmitted = false;
            for (var i = 0; i < masterNavi.Subrecords.Count; i++)
            {
                var sub = masterNavi.Subrecords[i];
                var nextSig = i + 1 < masterNavi.Subrecords.Count ? masterNavi.Subrecords[i + 1].Signature : null;

                SubrecordEncoder.WriteSubrecord(writer, sub.Signature, sub.Data);

                // End of the NVMI run → splice new NVMIs in here.
                if (!newNvmisEmitted && sub.Signature == "NVMI" && nextSig != "NVMI")
                {
                    foreach (var entry in newEntries)
                    {
                        SubrecordEncoder.WriteSubrecord(writer, "NVMI", BuildNvmi(entry));
                    }
                    newNvmisEmitted = true;
                }

                // End of the NVCI run → splice new NVCIs in here.
                if (!newNvcisEmitted && sub.Signature == "NVCI" && nextSig != "NVCI")
                {
                    foreach (var entry in newEntries)
                    {
                        SubrecordEncoder.WriteSubrecord(writer, "NVCI", BuildNvci(entry));
                    }
                    newNvcisEmitted = true;
                }
            }

            // Defensive fallbacks: if master had no NVMI/NVCI runs at all (shouldn't happen
            // for FNV's NAVI, but the parsed-subrecord layout could shift in DLCs), emit our
            // new entries at the end so we never silently lose them.
            if (!newNvmisEmitted)
            {
                foreach (var entry in newEntries)
                {
                    SubrecordEncoder.WriteSubrecord(writer, "NVMI", BuildNvmi(entry));
                }
            }
            if (!newNvcisEmitted)
            {
                foreach (var entry in newEntries)
                {
                    SubrecordEncoder.WriteSubrecord(writer, "NVCI", BuildNvci(entry));
                }
            }
        }

        return PluginRecordByteBuilder.BuildOverrideRecordBytes(masterNavi, bodyStream.ToArray(), options);
    }

    /// <summary>
    ///     Computes the approximate centroid Vec3 from NVVX vertex data (12 bytes per LE
    ///     Vec3). Falls back to the cell-grid center for exterior cells, or origin for
    ///     interior cells when NVVX is missing or empty. The engine uses iCellKey (gridY:gridX
    ///     at NVMI offset 12) for the primary cell lookup; the approx Vec3 is a tie-breaker
    ///     for nearest-mesh selection only, so a coarse fallback is safe.
    /// </summary>
    private static (float X, float Y, float Z) ComputeApproxCentroid(in NewNavmEntry entry)
    {
        var nvvx = entry.NvvxBytes;
        if (nvvx is not null && nvvx.Length >= 12)
        {
            var vertexCount = nvvx.Length / 12;
            double sumX = 0, sumY = 0, sumZ = 0;
            for (var i = 0; i < vertexCount; i++)
            {
                var off = i * 12;
                sumX += BinaryPrimitives.ReadSingleLittleEndian(nvvx.AsSpan(off, 4));
                sumY += BinaryPrimitives.ReadSingleLittleEndian(nvvx.AsSpan(off + 4, 4));
                sumZ += BinaryPrimitives.ReadSingleLittleEndian(nvvx.AsSpan(off + 8, 4));
            }

            return ((float)(sumX / vertexCount), (float)(sumY / vertexCount), (float)(sumZ / vertexCount));
        }

        if (entry.IsInterior)
        {
            return (0f, 0f, 0f);
        }

        // Grid-center fallback: each exterior cell is 4096 game units; offset by +2048 to land
        // in the cell interior. Matches the magnitude of master NAVI ApproxX/Y values.
        return (entry.GridX * 4096f + 2048f, entry.GridY * 4096f + 2048f, 0f);
    }
}
