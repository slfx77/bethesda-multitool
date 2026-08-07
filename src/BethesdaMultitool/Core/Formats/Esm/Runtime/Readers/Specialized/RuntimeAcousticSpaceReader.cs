using System.Buffers.Binary;
using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Esm.Runtime.Readers.Generic;

namespace BethesdaMultitool.Core.Formats.Esm.Runtime.Readers.Specialized;

/// <summary>
///     Typed runtime reader for <c>BGSAcousticSpace</c> (ASPC, FormType 0x0E).
///     <para>
///     ASPC has no record model of its own — it reaches the writer as a
///     <see cref="GenericEsmRecord" /> — so this reader deliberately returns one too, keyed by the
///     same PDB-style field names <c>AspcEncoder</c> already looks up. That keeps the fix on the
///     read side: no new model, no writer change.
///     </para>
///     <para>
///     What it replaces: the PDB-driven generic reader applied a probed uniform shift, and for
///     0x0E the generic shift probe reported <c>+4</c>. Every emitted record therefore carried the
///     runtime's <c>pSoundRegion</c> in the SNAM Night slot — a REGN where a SOUN belongs, which
///     the engine logged as <c>Could not find pNightSound</c> — while the real Dawn sound was
///     never read at all. See <see cref="RuntimeAcousticSpaceLayout" /> for the three captured
///     layouts and how they were mapped.
///     </para>
///     <para>
///     Every pointer read here is <b>type-validated</b>: a sound slot that does not resolve to a
///     SOUN yields null rather than a wrong FormID, per the standing rule that we decline to emit
///     rather than invent.
///     </para>
/// </summary>
internal sealed class RuntimeAcousticSpaceReader(
    RuntimeMemoryContext context,
    RuntimeLayoutProbeResult<RuntimeAcousticSpaceLayout>? probeResult = null)
{
    private const byte AspcFormType = 0x0E;
    private const byte SounFormType = 0x0D;
    private const byte RegnFormType = 0x37;

    /// <summary>
    ///     The probe must clear this before its answer is trusted. A dump whose acoustic spaces are
    ///     all null cannot discriminate the eras at all, and the engine's tie-break would hand back
    ///     the first-declared candidate as if it had been chosen.
    /// </summary>
    private const int MinimumMargin = 3;

    /// <summary>The five positional SNAM slots in xEdit order, with the PDB names the encoder reads.</summary>
    private static readonly string[] SoundFieldNames =
    [
        "BGSAcousticSpace.pDawnSound",
        "BGSAcousticSpace.pNoonSound",
        "BGSAcousticSpace.pDuskSound",
        "BGSAcousticSpace.pNightSound",
        "BGSAcousticSpace.pWallaSound"
    ];

    private readonly RuntimeMemoryContext _context = context;
    private readonly RuntimePdbFieldAccessor _fields = new(context);
    private readonly RuntimeAcousticSpaceLayout _layout = ResolveLayout(probeResult);

    /// <summary>The layout in use, so <c>dmp probe-shifts</c> and diagnostics can report it.</summary>
    public RuntimeAcousticSpaceLayout Layout => _layout;

    private static RuntimeAcousticSpaceLayout ResolveLayout(
        RuntimeLayoutProbeResult<RuntimeAcousticSpaceLayout>? probeResult)
    {
        if (probeResult is { Margin: >= MinimumMargin } result)
        {
            Logger.Instance.Info(
                $"[ASPC] Using probed layout {result.Winner.Layout.Label} " +
                $"(score {result.WinnerScore}, margin {result.Margin}, {result.SampleCount} samples).");
            return result.Winner.Layout;
        }

        // Not a hard failure: FourSound is the layout every dump in the corpus that has readable
        // acoustic spaces actually uses, and each slot is independently type-validated below, so a
        // wrong guess yields nulls rather than wrong FormIDs.
        var margin = probeResult?.Margin;
        Logger.Instance.Warn(
            "[ASPC] Acoustic-space layout probe was inconclusive " +
            $"(margin {margin?.ToString() ?? "n/a"} < {MinimumMargin}) — falling back to " +
            $"{RuntimeAcousticSpaceLayout.FourSound.Label}. Sound slots that do not resolve to a " +
            "SOUN will be emitted as NULL.");
        return RuntimeAcousticSpaceLayout.FourSound;
    }

    /// <summary>Reads one runtime acoustic space, or null when the struct can't be read.</summary>
    public GenericEsmRecord? ReadRuntimeAcousticSpace(RuntimeEditorIdEntry entry)
    {
        if (entry.FormType != AspcFormType)
        {
            return null;
        }

        var view = _fields.OpenStructView(entry, AspcFormType);
        if (view == null)
        {
            return null;
        }

        var buffer = view.Buffer;
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal);

        // Slots the captured build does not have are left absent rather than zero-filled: the
        // encoder emits schema-required subrecords as 0 either way, but an absent key keeps
        // "not in this build" distinguishable from "captured as null".
        for (var slot = 0; slot < _layout.SoundOffsets.Count && slot < SoundFieldNames.Length; slot++)
        {
            if (_context.FollowPointerToFormId(buffer, _layout.SoundOffsets[slot], SounFormType) is { } sound)
            {
                fields[SoundFieldNames[slot]] = sound;
            }
        }

        if (_context.FollowPointerToFormId(buffer, _layout.RegionOffset, RegnFormType) is { } region)
        {
            fields["BGSAcousticSpace.pSoundRegion"] = region;
        }

        if (TryReadScalar(buffer, _layout.EnvTypeOffset) is { } envType)
        {
            fields["BGSAcousticSpace.eEnvType"] = envType;
        }

        if (_layout.WallaPopOffset is { } wallaPopOffset && TryReadScalar(buffer, wallaPopOffset) is { } wallaPop)
        {
            fields["BGSAcousticSpace.iWallaPop"] = wallaPop;
        }

        // bIsInterior is intentionally not read — see RuntimeAcousticSpaceLayout for the evidence
        // that the captured builds do not populate it. The encoder still emits the schema-required
        // INAM as 0; the difference is that we no longer present that 0 as a captured value.
        return new GenericEsmRecord
        {
            FormId = entry.FormId,
            RecordType = "ASPC",
            EditorId = entry.EditorId,
            Bounds = view.Bounds(),
            Fields = fields,
            Offset = view.FileOffset,
            IsBigEndian = true
        };
    }

    private static uint? TryReadScalar(byte[] buffer, int offset)
    {
        return offset + 4 <= buffer.Length
            ? BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(offset, 4))
            : null;
    }
}
