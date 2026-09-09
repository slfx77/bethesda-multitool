using System.Text;

namespace BethesdaMultitool.Core.Formats.Battlespire;

/// <summary>
///     The 32-bit texture key a Battlespire mesh plane carries at header +2, and its meaning:
///     a <b>base-40 encoding of the BSI file's stem</b>, up to six characters, most significant
///     first. Read off <c>GAME.EXE</c> (DOS/4GW flat image, addresses as in
///     <c>tools/GhidraProject/ClassicRE/GAME.EXE.decompiled.txt</c>):
///     <list type="bullet">
///         <item>
///             <c>FUN_00075154</c> — the ENCODER. Walks the name until <c>.</c> or NUL (at most six
///             characters); each character's index in the alphabet is multiplied by a place value
///             that starts at <c>0x61A8000 = 40^5</c> and divides by <c>0x28 = 40</c> per position;
///             positions after the name's end contribute index <c>0x27 = 39</c>. A character
///             outside the 39 legal ones reports "TEXTURE FILE ERROR - The texture file %s has
///             an illegal character" and yields -2.
///         </item>
///         <item>
///             <c>FUN_00073fc8</c> / <c>FUN_00075324</c> — the DECODER. Divides by the same place
///             values, stops at digit 39, indexes the alphabet at flat <c>0x000F4E18</c>, appends
///             <c>.bsi</c> and fetches the file from BSI.BSA.
///         </item>
///         <item>
///             <c>FUN_0008a139</c> — the texture cache: a binary tree keyed by this 32-bit value
///             (root <c>DAT_00421FA8</c>), so the mesh stores the KEY and the name is only
///             reconstructed on a cache miss.
///         </item>
///         <item>
///             <c>FUN_00087E40</c> — the mesh loader reads the plane field as ONE dword
///             (<c>*(uint*)(plane + 2)</c>) and skips planes at or above <c>0xFFF00000</c>;
///             <c>FUN_00073C6C</c> gives those a SOLID COLOUR instead of a texture (high word
///             <c>0xFFFF</c>, or <c>0xFFF0</c>), taking the colour from the low word.
///         </item>
///     </list>
///     <para>
///         The alphabet at <c>0x000F4E18</c> is <c>0123456789abcdefghijklmnopqrstuvwxyz~_#%</c> —
///         40 symbols, of which the last (<c>%</c>, index 39) is the terminator the encoder pads
///         with and never a legal name character. Names are case-folded: the alphabet is lower
///         case while the archive stores <c>WALL35.BSI</c>.
///     </para>
///     <para>
///         ⚑ Measured on the full retail population (2026-09-08): of the 7,945 loose planes 7,807
///         decode to a stem BSI.BSA holds and 138 are colour planes; 3D.BSA's 134,754 planes give
///         133,477 + 933 colour + 344 that decode to twelve legal names the shipped archive lacks
///         (<c>strut0</c>, <c>hand00..09</c>, <c>presto</c>, <c>keyhol</c>, <c>wwheel</c>); 3D.BS6's
///         123,235 give 122,323 + 568 + the same 344. Re-encoding every decoded name reproduces
///         its key exactly. A random 32-bit value decodes to a name the archive holds with
///         probability about 2,592 / 40^6 ≈ 6e-7, so 689 distinct hits out of 701 distinct
///         non-colour keys is not chance — that is what would have falsified this reading.
///     </para>
///     <para>
///         ⛔ This CORRECTS the five readings the backlog board refuted (direct BSI index, the
///         Daggerfall archive/record split, entry index + frame, a (file, record) split at every
///         shift, and nine hash families): the field is a 32-bit dword, not a u16, and it names
///         the texture rather than indexing anything. "+2 determines +4" held because the high
///         word is the name's first two-and-a-bit characters and the low word the rest.
///     </para>
/// </summary>
internal static class BattlespireTextureName
{
    /// <summary>The 40-symbol alphabet at flat <c>0x000F4E18</c> in GAME.EXE; index 39 terminates.</summary>
    public const string Alphabet = "0123456789abcdefghijklmnopqrstuvwxyz~_#%";

    /// <summary>Characters a name may use: the alphabet without its terminator.</summary>
    public const int Radix = 40;

    /// <summary>Longest name the six base-40 places can hold.</summary>
    public const int MaxLength = 6;

    /// <summary>Digit that ends a name shorter than six characters.</summary>
    public const int Terminator = Radix - 1;

    /// <summary>Place value of the first character: <c>40^5 = 0x61A8000</c>, as GAME.EXE initialises it.</summary>
    public const uint FirstPlace = 102_400_000;

    /// <summary>
    ///     Keys at or above this are not names but SOLID-COLOUR planes: the mesh loader
    ///     (<c>FUN_00087E40</c>) skips their texture set-up and <c>FUN_00073C6C</c> assigns a colour
    ///     from the low word. Retail uses only the <c>0xFFFF</c> high word (1,639 planes over
    ///     twelve distinct keys); <c>0xFFF0</c> is accepted by the game and appears in no file.
    /// </summary>
    public const uint SolidColorThreshold = 0xFFF0_0000;

    /// <summary>Whether a plane key is a colour plane rather than a texture name.</summary>
    public static bool IsSolidColor(uint key)
    {
        return key >= SolidColorThreshold;
    }

    /// <summary>
    ///     Decodes a plane key into its BSI stem (lower case, no extension), or null when the key
    ///     is a colour plane or its leading digit lies outside the alphabet (keys from
    ///     <c>40^6 = 0xF4240000</c> up to the colour threshold, which no name can produce).
    /// </summary>
    public static string? Decode(uint key)
    {
        if (IsSolidColor(key))
        {
            return null;
        }

        var name = new StringBuilder(MaxLength);
        var remaining = key;
        var place = FirstPlace;
        for (var i = 0; i < MaxLength; i++)
        {
            var digit = remaining / place;
            if (digit == Terminator)
            {
                break;
            }

            if (digit > Terminator)
            {
                return null;
            }

            name.Append(Alphabet[(int)digit]);
            remaining %= place;
            place /= Radix;
        }

        // Six terminator digits (0xF423FFFF) is a legal encoding of nothing; the game would ask
        // BSI.BSA for ".bsi", so it resolves to no texture rather than to an empty name.
        return name.Length == 0 ? null : name.ToString();
    }

    /// <summary>
    ///     Encodes a stem the way <c>FUN_00075154</c> does: at most six characters, stopping at the
    ///     first <c>.</c>, case-folded, padded with the terminator digit. The extension is ignored
    ///     so <c>WALL35.BSI</c> and <c>wall35</c> encode alike.
    /// </summary>
    /// <exception cref="ArgumentException">A character is not one of the 39 legal ones.</exception>
    public static uint Encode(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        uint key = 0;
        var place = FirstPlace;
        var length = name.IndexOf('.');
        if (length < 0)
        {
            length = name.Length;
        }

        // The game reads six characters at most and never complains about a seventh; a longer stem
        // simply truncates, as WALL35 does not but a hypothetical KEYHOLE4 would (to "keyhol").
        length = Math.Min(length, MaxLength);
        for (var i = 0; i < MaxLength; i++)
        {
            uint digit;
            if (i < length)
            {
                var c = char.ToLowerInvariant(name[i]);
                var index = Alphabet.IndexOf(c);
                if (index < 0 || index == Terminator)
                {
                    throw new ArgumentException(
                        $"Texture name '{name}' has an illegal character '{name[i]}' (GAME.EXE would refuse it too).",
                        nameof(name));
                }

                digit = (uint)index;
            }
            else
            {
                digit = Terminator;
            }

            key += digit * place;
            place /= Radix;
        }

        return key;
    }

    /// <summary>
    ///     The colour of a solid-colour plane as 8-bit RGB. The low word is a 15-bit colour shifted
    ///     left one bit — the 8-bit path in <c>FUN_00073C6C</c> looks it up as
    ///     <c>DAT_00419E60[low &gt;&gt; 1]</c>, and bit 0 is clear on 1,639 of 1,639 retail colour
    ///     planes. ⚠ The channel ORDER (red in bits 10-14) is a stated assumption carried over from
    ///     the same engine's IMAGE.RAW, which was measured x555; ten of the twelve retail colours
    ///     are greys or near-greys, on which the order cannot be told apart.
    /// </summary>
    public static (byte R, byte G, byte B) SolidColor(uint key)
    {
        var color15 = (key & 0xFFFF) >> 1;
        var r = (int)(color15 >> 10) & 0x1F;
        var g = (int)(color15 >> 5) & 0x1F;
        var b = (int)color15 & 0x1F;
        return (Expand5(r), Expand5(g), Expand5(b));
    }

    /// <summary>Widens a 5-bit channel to 8 bits by replicating its top bits (31 → 255).</summary>
    private static byte Expand5(int value)
    {
        return (byte)((value << 3) | (value >> 2));
    }
}
