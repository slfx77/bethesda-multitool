namespace BethesdaMultitool.Core.Formats.BrotherhoodOfSteel;

/// <summary>
///     The <b>string-key</b> hash of Fallout: Brotherhood of Steel — the function that keys the
///     <c>.SDB</c> string databases, the <c>.DDF</c> record stores and the <c>.NFO</c> debug dumps.
///     Original RE 2026-09-07/08 off the Xbox executable <c>default.xbe</c> at <c>0x00014BE0</c>
///     (49 bytes; the PS2 twin is <c>SLUS_205.39</c> <c>0x00104BB0</c>):
///     <code>
///     h = 0;
///     for each UTF-16 code unit c:  h = (h * 0x80104025) ^ ((int32)h &gt;&gt; 27) ^ c;
///     </code>
///     <para>
///         ⚠⚠ <b>The shift is ARITHMETIC and the input is UTF-16, not bytes.</b> Ghidra renders the
///         instruction as <c>(int)uVar2 &gt;&gt; 0x1b</c> on a signed int — sign-extending. The same
///         formula with a LOGICAL shift reproduces <b>9</b> of the disc's 5,589 name/hash pairs;
///         this one reproduces <b>5,215</b>. Callers widen an ASCII name to UTF-16 before hashing
///         (<c>0x00014C20</c>, <c>0x00030360</c>, <c>0x0003B0C0</c>), so a C# <see cref="char" />
///         loop is exactly what the engine does — no encoding step is needed or wanted.
///     </para>
///     <para>
///         ⚑ <b>Measured against the disc, on both consoles.</b> The <c>.NFO</c> files are the
///         game's own debug dumps of a database: each line prints a slot's stored hash beside the
///         string. Over the Xbox disc's 53 dumps this function reproduces the stored hash on
///         <b>5,215 of 5,589</b> pairs, and over the PS2 disc's 2 on <b>119 of 131</b>. ⚑ The 374 +
///         12 misses are not failures of the hash: they are display VALUES ("Freezer Chest",
///         "Save Game Console", "Footlocker") filed under the key of the INTERNAL name they belong
///         to — 69 distinct labels repeated across levels. ⚑ The control that separates the two
///         kinds is the UNDERSCORE, and it discriminates: not one of the 374 misses contains one,
///         while this game's identifiers are underscore-separated and only 13 of the 5,215 proven
///         names are Title-Case without one. ⛔ "Every miss contains a space" is REFUTED — 81 of
///         the 374 are one-word labels (Footlocker 34, Locker 13, Switch 11, Cabinet 5, Door 4,
///         Dresser 3, Fan 3, Computer 3). That split is what
///         <see cref="BosStringDatabase" /> stores and what
///         <c>BosRecordSource</c> now uses to tell a proven name from a display string.
///     </para>
///     <para>
///         ⛔ <b>Nine rival hashes score ZERO on the same 5,589 pairs</b>, including
///         <see cref="BosAssetHash" /> (the ASSET-key hash, multiplier <c>0x80000025</c> over
///         Latin-1 bytes with a LOGICAL shift), CRC-32, DJB2, FNV-1a, SDBM and the ELF hash. The
///         two BOS hashes are siblings, not the same function: <see cref="BosAssetHash" /> keys
///         <c>.CLP</c> sections by file name, this one keys strings and records.
///     </para>
/// </summary>
internal static class BosNameHash
{
    /// <summary>The multiplier: <c>imul eax, eax, 0x80104025</c> in the executable.</summary>
    public const uint Multiplier = 0x80104025;

    /// <summary>The arithmetic right shift applied to the running hash each round.</summary>
    public const int ShiftBits = 27;

    /// <summary>Hashes a string exactly as the engine does, over its UTF-16 code units.</summary>
    public static uint Compute(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var h = 0u;
        foreach (var c in text)
        {
            // ⚠ (int)h >> 27 — a SIGNED shift. Written as an unsigned shift this reproduces 9 of
            // the disc's 5,589 pairs instead of 5,215.
            h = unchecked((h * Multiplier) ^ (uint)((int)h >> ShiftBits) ^ c);
        }

        return h;
    }

    /// <summary>
    ///     True when <paramref name="text" /> is the string that PRODUCES <paramref name="key" />
    ///     — i.e. the record's or asset's own internal name rather than a display string filed
    ///     under it.
    /// </summary>
    public static bool Names(string text, uint key)
    {
        return text is not null && Compute(text) == key;
    }
}
