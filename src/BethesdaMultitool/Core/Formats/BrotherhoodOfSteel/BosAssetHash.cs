namespace BethesdaMultitool.Core.Formats.BrotherhoodOfSteel;

/// <summary>
///     The asset-key hash of Fallout: Brotherhood of Steel (2004, PS2). Read off <c>SLUS_205.39</c>
///     2026-09-07 — the routine at <c>0x0013FDD8</c>, inlined again at <c>0x001978C0</c> and in every
///     caller that hashes a literal (<c>0x00199370</c>, <c>0x0019B2F8</c>, <c>0x0014F9B8</c>):
///     <code>
///     h = 0; m = 0;
///     for each byte c:  if (c == '\\') c = '/';   h = ((h &gt;&gt; 27) ^ m) ^ c;   m = h * 0x80000025;
///     </code>
///     <para>
///         ⚑ <b>Verified against the disc, not just transcribed.</b> Every <c>_S.CLP</c> sound bank
///         keys its hash table with this routine over <c>/Final_Assets/sound/&lt;name&gt;.vag</c>,
///         and the <c>VAGp</c> header inside each entry carries the name: of the 539 entries whose
///         name is short enough to be complete (14 characters or fewer — 435 sit at exactly 15 and
///         2,456 fill all 16 bytes, so those are truncated and cannot be tested),
///         <b>
///             537 reproduce
///             their slot key
///         </b>
///         — 448 with the name's own case, 81 all-lowercase, 8 with a lowercase
///         name behind the capitalised prefix — and 2 do not (re-measured 2026-09-07; an earlier
///         "957 of 957" here counted 15-character names as complete). The texture clumps' three sections
///         are keyed <c>&lt;stem&gt;.tex</c> / <c>.hsh</c> / <c>.vat</c> on 134 of 134 slots over the
///         54 shipped files, and loose textures are keyed by bare file name (<c>halo1.tex</c>).
///     </para>
///     <para>
///         ⚠ The formula is CASE-SENSITIVE and the engine tries more than one spelling — which is
///         why an earlier transcription of the same instructions "reproduced nothing": it was fed
///         <c>.SDB</c> display strings and clump paths, which are not what the engine hashes.
///         ⛔ The <c>.SDB</c> string database uses a DIFFERENT hash: 0 of 188 dialogue-line names
///         in four spellings match any BAR.SDB key. Do not apply this routine there.
///     </para>
/// </summary>
internal static class BosAssetHash
{
    /// <summary>The multiplier: <c>lui 0x8000; ori 0x25</c> in the executable.</summary>
    public const uint Multiplier = 0x80000025;

    /// <summary>Hashes a key exactly as the engine does (Latin-1 bytes, backslash folded to slash).</summary>
    public static uint Compute(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        uint h = 0;
        uint m = 0;
        foreach (var ch in key)
        {
            var c = ch == '\\' ? '/' : ch;
            h = (h >> 27) ^ m ^ (byte)c;
            m = unchecked(h * Multiplier);
        }

        return h;
    }

    /// <summary>
    ///     The engine's probe step when a hash-table slot is occupied by another key
    ///     (<c>0x00140978</c>: <c>h = (~h &gt;&gt; 27) ^ (h &lt;&lt; 5)</c>, at most 60 times).
    /// </summary>
    public static uint Rehash(uint h)
    {
        return (~h >> 27) ^ (h << 5);
    }

    /// <summary>
    ///     The slot count the loaders allocate for <paramref name="entryCount" /> entries
    ///     (<c>0x0013F4A0</c> and <c>0x0013F750</c>): <c>1 &lt;&lt; (1 + floor(log2(n + n/2)))</c>,
    ///     a power of two strictly above three halves of the count.
    /// </summary>
    public static int SlotCountFor(int entryCount)
    {
        var v = entryCount + (entryCount >> 1);
        var bits = 1;
        while ((v >>= 1) != 0)
        {
            bits++;
        }

        return 1 << bits;
    }
}
