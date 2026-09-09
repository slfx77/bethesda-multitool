using System.Buffers.Binary;

namespace BethesdaMultitool.Core.Formats.Battlespire;

/// <summary>
///     The 111-byte body of a SAVETREE Object record (type 6, 176 bytes in all): the level's 3D
///     objects — doors, levers, platforms, NPC meshes. Offsets in the comments are UESP's, from the
///     record's length word; the constants are body-relative.
///     <para>
///         ⚑ <b>Measured on SAVE0 (2026-09-07), 131 records, against the level's own BS6.</b> The
///         header's RecordID equals an <c>OBJD.IDNB</c> of L1.BS6 on 130/131 (the 131st is a
///         dynamic id), the header's FileID equals that object's <c>IDFI</c> — its index into the
///         level's mesh list — on 130/130, the header XYZ equals the RAW <c>POSI</c> integers read
///         as floats on 125/130, and the header pitch/yaw/roll equal the low 16 bits of <c>ANGS</c>
///         on 126/130; the handful that differ are objects that move. ParentID is 1 on 131/131 —
///         "on the map".
///     </para>
///     <para>
///         ⚠
///         <b>
///             Positions are RAW BS6 units, not the /256 world units <see cref="Bs6File" /> callers
///             convert to
///         </b>
///         : -1122.0 here against <c>POSI</c> -1122. Scale before mixing with a
///         level scene.
///     </para>
///     <para>
///         The body's own XYZ (+101) and rotation (+113) mirror the header on 127/131 records; the
///         four that differ are moving objects, so the body copy reads as a base/start pose. The
///         u32 at +65 is a large pointer-like base-type value, not an index into anything found.
///     </para>
/// </summary>
internal sealed class BattlespireSaveObject
{
    /// <summary>Bytes in the body (record total 176 minus the 65-byte header).</summary>
    public const int BodyLength = 111;

    private const int PositionOffset = 36; // +101, 3 x f32
    private const int RotationOffset = 48; // +113, 3 x u32

    private BattlespireSaveObject(uint baseTypeId, (float, float, float) position, (uint, uint, uint) rotation)
    {
        BaseTypeId = baseTypeId;
        Position = position;
        Rotation = rotation;
    }

    /// <summary>+65: a pointer-like base type value.</summary>
    public uint BaseTypeId { get; }

    /// <summary>+101: the body's copy of the position (raw BS6 units).</summary>
    public (float X, float Y, float Z) Position { get; }

    /// <summary>+113: the body's copy of pitch, yaw, roll.</summary>
    public (uint Pitch, uint Yaw, uint Roll) Rotation { get; }

    /// <summary>Parses a body of at least <see cref="BodyLength" /> bytes.</summary>
    public static BattlespireSaveObject Parse(ReadOnlySpan<byte> body, string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (body.Length < BodyLength)
        {
            throw new InvalidDataException($"{name}: an object body needs {BodyLength} bytes, got {body.Length}.");
        }

        return new BattlespireSaveObject(
            BinaryPrimitives.ReadUInt32LittleEndian(body[..]),
            (BinaryPrimitives.ReadSingleLittleEndian(body[PositionOffset..]),
                BinaryPrimitives.ReadSingleLittleEndian(body[(PositionOffset + 4)..]),
                BinaryPrimitives.ReadSingleLittleEndian(body[(PositionOffset + 8)..])),
            (BinaryPrimitives.ReadUInt32LittleEndian(body[RotationOffset..]),
                BinaryPrimitives.ReadUInt32LittleEndian(body[(RotationOffset + 4)..]),
                BinaryPrimitives.ReadUInt32LittleEndian(body[(RotationOffset + 8)..])));
    }
}
