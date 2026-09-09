namespace BethesdaMultitool.Core.Formats.Tactics;

/// <summary>
///     One Fallout Tactics <c>&lt;tile&gt;</c> record — the 23-byte body <c>FUN_006f04c0</c> reads,
///     which is BOTH the header of a <c>.til</c> file and the record a mission's inflated world
///     repeats 99,596 times at a 33-byte stride. See <see cref="TacticsTileFile" /> for the field
///     provenance; every name here comes from the Tile Editor's own dialog or config importer.
/// </summary>
/// <param name="Version">6..10; the flag word's shape depends on it.</param>
/// <param name="BoundingBoxX">Editor "Bounding Box" x, config <c>bounding_box_size</c> component 0.</param>
/// <param name="BoundingBoxY">Component 1 — the vertical one on the retail census (walls read 12, floors 1).</param>
/// <param name="BoundingBoxZ">Component 2.</param>
/// <param name="FootPositionX">Config <c>foot_position</c> x, editor "Texture Coords" x. Signed.</param>
/// <param name="FootPositionY">Config <c>foot_position</c> y.</param>
/// <param name="ImageWidth">Field at <c>this+0x1d</c>; the first embedded image's width on 29,922/29,957.</param>
/// <param name="ImageHeight">Field at <c>this+0x21</c>.</param>
/// <param name="Type">Wall/Floor/Object/Stair/Roof.</param>
/// <param name="Material">Stone/Gravel/Metal/Wood/Water/Snow.</param>
/// <param name="Flags">The u16 the editor's eight checkboxes bind to.</param>
/// <param name="LegacyByteA">
///     The byte versions below 9 carry and the reader THROWS AWAY — surfaced so a caller can see it
///     rather than wonder. 100 on all 7,814 retail tiles below v9.
/// </param>
/// <param name="LegacyByteB">The second discarded byte, version 6 only. 0 on all 1,485 retail v6 tiles.</param>
/// <param name="RecordLength">Bytes of the whole record including its framing — 33 for v10.</param>
internal readonly record struct TacticsTileHeader(
    int Version,
    byte BoundingBoxX,
    byte BoundingBoxY,
    byte BoundingBoxZ,
    int FootPositionX,
    int FootPositionY,
    int ImageWidth,
    int ImageHeight,
    TacticsTileType Type,
    TacticsTileMaterial Material,
    TacticsTileFlags Flags,
    byte? LegacyByteA,
    byte? LegacyByteB,
    int RecordLength)
{
    /// <summary>
    ///     The value <c>FUN_006f02d0</c> derives from the three bounding-box bytes and the reader
    ///     caches at <c>this+0x0a</c> — a shape class in 0..5. Transcribed branch for branch.
    ///     <para>
    ///         ⚠ This is a DERIVED field, not stored, so no file can confirm it: the only check
    ///         available is that it is a pure function of a field that IS stored. What the retail
    ///         census shows is a symmetry consistent with an axis pair — over the 29,957 tiles the
    ///         classes come out 0 x1,280, 1 x6,531, 2 x11,999, 3 x6,533, 4 x2,212, 5 x1,402, and
    ///         classes 1 and 3 (the two branches that differ only in which of x/z is larger) are
    ///         within two of each other. The game's NAME for each class is not in the binary and is
    ///         not guessed here.
    ///     </para>
    /// </summary>
    public int BoundingBoxClass => Classify(BoundingBoxX, BoundingBoxY, BoundingBoxZ);

    /// <summary><c>FUN_006f02d0</c>, transcribed.</summary>
    public static int Classify(byte x, byte y, byte z)
    {
        if (x == y && x == z)
        {
            return 5;
        }

        if (y < 2)
        {
            return 2;
        }

        if (z < x)
        {
            return 3;
        }

        if (x < z)
        {
            return 1;
        }

        // x == z from here, and y >= 2, and y != x.
        if (y * 2 < x && y * 2 < z)
        {
            return 2;
        }

        if (x * 2 < y && x * 2 < z)
        {
            return 1;
        }

        if (z * 2 < y && z * 2 < x)
        {
            return 3;
        }

        if (x * 2 < y && z * 2 < y)
        {
            return 4;
        }

        return 0;
    }
}
