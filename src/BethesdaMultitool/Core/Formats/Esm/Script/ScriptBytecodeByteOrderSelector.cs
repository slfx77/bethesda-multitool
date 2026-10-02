using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Esm.Script;

/// <summary>
///     The byte order chosen for one SCDA payload, the rule that chose it, and a one-line account
///     of what each order's walk found (for logs and diagnostics).
/// </summary>
internal readonly record struct ScriptBytecodeByteOrderDecision(
    bool IsBigEndian,
    ScriptBytecodeByteOrderEvidence Evidence,
    string Detail);

/// <summary>
///     Decides the byte order of compiled script bytecode (SCDA) from the payload itself, never from
///     the record container alone.
///     <para>
///         The two are independent. Serialized SCDA is little-endian inside an Xbox 360 ESM — the
///         engine swaps it when it loads the script — while the SCHR/SLSD/SCRO/SCRV integers around
///         it follow the container. Measured 2026-09-28: all 2,487 SCPT in the July 2010 X360
///         prototype and all 2,546 in the X360 final open with <c>1D 00 00 00</c>, and 1,741 of the
///         2,441 July scripts that share a FormID with PC retail carry byte-identical SCDA. A
///         runtime Script object read from an Xbox 360 dump, by contrast, is already big-endian.
///     </para>
///     <para>
///         Rules, in order: (a) fewer than 4 bytes, the caller's default (NotApplicable); (b) the
///         ScriptName anchor; (c) the only order that walks cleanly; (d) of two clean walks, the one
///         with strictly fewer unknown statement opcodes; (e) of two unclean walks, the only one that
///         reaches the end; (f) the caller's default, labelled with the caller's evidence — little-
///         endian for on-disk data (<see cref="ScriptBytecodeByteOrderEvidence.AmbiguousSerializedDefault" />),
///         the container order for dump-derived data
///         (<see cref="ScriptBytecodeByteOrderEvidence.AmbiguousContainerFallback" />).
///     </para>
/// </summary>
internal static class ScriptBytecodeByteOrderSelector
{
    // ScriptName (opcode 0x001D) with a zero-length payload, as the first statement.
    private const byte ScriptNameOpcodeLow = (byte)ScriptOpcodes.ScriptName;

    /// <param name="scda">The SCDA payload, exactly as stored.</param>
    /// <param name="vars">The script's SLSD/SCVR locals (they only label the walk).</param>
    /// <param name="refs">The script's SCRO/SCRV table (it only labels the walk).</param>
    /// <param name="fallbackBigEndian">The order used when the payload cannot decide (rules a and f).</param>
    /// <param name="fallbackEvidence">The label rule (f) reports for <paramref name="fallbackBigEndian" />.</param>
    /// <param name="game">Selects the command table that names function opcodes.</param>
    internal static ScriptBytecodeByteOrderDecision Select(
        ReadOnlySpan<byte> scda,
        IReadOnlyList<ScriptVariableInfo> vars,
        IReadOnlyList<uint> refs,
        bool fallbackBigEndian,
        ScriptBytecodeByteOrderEvidence fallbackEvidence,
        BethesdaGame game)
    {
        ArgumentNullException.ThrowIfNull(vars);
        ArgumentNullException.ThrowIfNull(refs);

        // (a) Nothing to decide: not even one opcode/length word pair.
        if (scda.Length < 4)
        {
            return new ScriptBytecodeByteOrderDecision(
                fallbackBigEndian,
                ScriptBytecodeByteOrderEvidence.NotApplicable,
                $"SCDA has {scda.Length} byte(s), fewer than one opcode word pair");
        }

        // (b) A standalone script opens with ScriptName and an empty payload. The two spellings are
        // mutually exclusive, and the other order would read opcode 0x1D00, which no table defines.
        if (scda[0] == ScriptNameOpcodeLow && scda[1] == 0 && scda[2] == 0 && scda[3] == 0)
        {
            return new ScriptBytecodeByteOrderDecision(
                false,
                ScriptBytecodeByteOrderEvidence.ScriptNameAnchor,
                "SCDA opens with ScriptName as 1D 00 00 00");
        }

        if (scda[0] == 0 && scda[1] == ScriptNameOpcodeLow && scda[2] == 0 && scda[3] == 0)
        {
            return new ScriptBytecodeByteOrderDecision(
                true,
                ScriptBytecodeByteOrderEvidence.ScriptNameAnchor,
                "SCDA opens with ScriptName as 00 1D 00 00");
        }

        var bytecode = scda.ToArray();
        var functions = ResolveFunctionSet(game);
        var little = ScriptBytecodeAnalyzer.Analyze(bytecode, false, vars, refs, functions: functions);
        var big = ScriptBytecodeAnalyzer.Analyze(bytecode, true, vars, refs, functions: functions);
        var detail = $"LE: {Describe(little)}; BE: {Describe(big)}";

        // "Clean" is the definition the inline paths have always used: the walk reached the end and
        // the decompiler wrote no truncation/error/unknown-opcode line.
        var littleClean = little.WalkedToEnd && !little.HasDiagnostics;
        var bigClean = big.WalkedToEnd && !big.HasDiagnostics;

        // (c) Exactly one order walks cleanly.
        if (littleClean != bigClean)
        {
            return new ScriptBytecodeByteOrderDecision(
                bigClean,
                ScriptBytecodeByteOrderEvidence.SingleCleanWalk,
                detail);
        }

        // (d) Both clean: a misread short call (23 12 00 00 read as UnknownFunc_0x2312) leaves no
        // diagnostic, but it does fail to name its opcode. The comparison is relative, so a payload
        // with a genuinely unlisted opcode in both readings still falls through to the default.
        if (littleClean && little.UnknownOpcodeCount != big.UnknownOpcodeCount)
        {
            return new ScriptBytecodeByteOrderDecision(
                big.UnknownOpcodeCount < little.UnknownOpcodeCount,
                ScriptBytecodeByteOrderEvidence.FewerUnknownOpcodes,
                detail);
        }

        // (e) Neither clean, but only one order reaches the end of the payload.
        if (!littleClean && little.WalkedToEnd != big.WalkedToEnd)
        {
            return new ScriptBytecodeByteOrderDecision(
                big.WalkedToEnd,
                ScriptBytecodeByteOrderEvidence.WalkedToEndInOneOrder,
                detail);
        }

        // (f) The payload cannot decide; the caller's default and label stand.
        return new ScriptBytecodeByteOrderDecision(fallbackBigEndian, fallbackEvidence, detail);
    }

    /// <summary>
    ///     The games with a compiled SCDA script engine walk with their own command table. Any other
    ///     value (Unknown included) walks with the FNV/FO3 table, which is also ScriptDecompiler's
    ///     default: with an empty table every function opcode is unknown in both orders and rule (d)
    ///     could never discriminate. A caller that walks the chosen order again (the script
    ///     diagnostics block rows) resolves its table here, so the decision and that walk agree.
    /// </summary>
    internal static ScriptFunctionSet ResolveFunctionSet(BethesdaGame game)
    {
        return game is BethesdaGame.Oblivion or BethesdaGame.Fallout3 or BethesdaGame.FalloutNewVegas
            ? ScriptFunctionTables.For(game)
            : ScriptFunctionTables.For(BethesdaGame.FalloutNewVegas);
    }

    private static string Describe(ScriptBytecodeAnalysis analysis)
    {
        var reach = analysis.WalkedToEnd ? "walked to end" : "stopped short";
        var diagnostics = analysis.HasDiagnostics ? $", {analysis.Diagnostics}" : string.Empty;
        return $"{reach}, {analysis.UnknownOpcodeCount} unknown opcode(s){diagnostics}";
    }
}
