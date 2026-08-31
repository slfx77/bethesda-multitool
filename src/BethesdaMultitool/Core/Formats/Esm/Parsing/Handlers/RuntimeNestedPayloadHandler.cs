using System.Diagnostics;
using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Esm.Runtime;

namespace BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;

/// <summary>
///     Sweeps the runtime form table once and harvests the three payloads that live behind a
///     container or an indirection: MODT texture hashes, MODS alternate textures, and the DEST
///     destruction block.
///     <para>
///         This exists as a standalone sweep rather than as additions to the ~20 typed readers for
///         the same reason <see cref="AlternateTextureHandler" /> does on the ESM side: the members
///         sit on engine base classes (<c>TESModel</c>, <c>TESModelTextureSwap</c>,
///         <c>BGSDestructibleObjectForm</c>), so which record types have them is decided by C++
///         inheritance and cuts straight across the specialized/generic reader split. Half of the
///         43 / 28 / 26 owning types are routed to hand-written readers — WEAP, ARMO, STAT, MISC,
///         DOOR, NPC_, CREA among them — and none of those would ever have asked.
///     </para>
///     <para>
///         Results land in <c>RecordCollection</c> side-indexes rather than on the typed models,
///         which keeps one consumer path for both the ESM and DMP sources and avoids adding three
///         properties to twenty record types that mostly do not want them.
///     </para>
/// </summary>
internal sealed class RuntimeNestedPayloadHandler(RecordParserContext context) : RecordHandlerBase(context)
{
    internal RuntimeNestedPayloadHarvest BuildIndex()
    {
        var textureHashes = new Dictionary<uint, RuntimeTextureHashList>();
        var alternateTextures = new Dictionary<uint, IReadOnlyList<AlternateTextureEntry>>();
        var destruction = new Dictionary<uint, DestructionData>();

        if (Context.RuntimeReader == null)
        {
            return new RuntimeNestedPayloadHarvest(textureHashes, alternateTextures, destruction);
        }

        // The sweep re-resolves a struct that a specialized reader has usually just read, so its
        // cost is worth stating rather than assuming. Timed here rather than inferred from total
        // parse time, which is dominated by machine load on a multi-GB dump.
        var sw = Stopwatch.StartNew();
        var examined = 0;
        var fromSpecializedTypes = 0;
        var partialTextureHashes = 0;
        foreach (var entry in Context.ScanResult.RuntimeEditorIds)
        {
            // A set lookup against the layout database, so the sweep costs nothing on the majority
            // of FormTypes that carry none of the three and only reads a struct where one exists.
            if (!PdbStructLayouts.CarriesNestedPayload(entry.FormType))
            {
                continue;
            }

            examined++;
            if (Context.RuntimeReader.ReadNestedPayloads(entry) is not { } payloads)
            {
                continue;
            }

            if (payloads.TextureHashes is { DeclaredCount: > 0 } hashes)
            {
                textureHashes[entry.FormId] = hashes;
                if (!hashes.IsComplete)
                {
                    partialTextureHashes++;
                }
            }

            if (payloads.AlternateTextures is { Count: > 0 } swaps)
            {
                alternateTextures[entry.FormId] = swaps;
            }

            if (payloads.Destruction is { } destructible)
            {
                destruction[entry.FormId] = destructible;
            }

            if (PdbStructLayouts.HasSpecializedReader(entry.FormType))
            {
                fromSpecializedTypes++;
            }
        }

        sw.Stop();
        Logger.Instance.Debug(
            $"  [Semantic] Runtime nested payloads: examined {examined} record(s) across " +
            $"{PdbStructLayouts.NestedPayloadFormTypes.Count} FormTypes carrying one " +
            $"in {sw.Elapsed.TotalMilliseconds:F0} ms");

        // Reported at Info, like the truncated-record recovery line, because it names content that
        // was recovered rather than a step that ran — but only when something was actually found,
        // so an ESM load or a dump with none of it stays silent.
        var recovered = textureHashes.Count + alternateTextures.Count + destruction.Count;
        if (recovered > 0)
        {
            Logger.Instance.Info(
                $"[Semantic Parse] Recovered nested payloads from {recovered} record(s): " +
                $"{destruction.Count} destruction block(s), " +
                $"{alternateTextures.Count} alternate-texture set(s), " +
                $"{textureHashes.Count} texture-hash list(s)" +
                // Stated rather than hidden: the engine fills a model's BSFileEntry array as its
                // textures load, so a dump routinely catches one part-filled. Those are kept with
                // their holes marked, and saying how many are partial keeps the headline count from
                // reading as "this many complete manifests".
                (partialTextureHashes > 0 ? $" ({partialTextureHashes} partial)" : string.Empty) +
                " " +
                // The sweep re-resolves structs the specialized readers have usually just read, so
                // its cost is reported rather than assumed. Total parse time is a poor proxy on a
                // multi-GB dump — it is dominated by machine load.
                $"in {sw.Elapsed.TotalMilliseconds:F0} ms; " +
                // The number that says whether this sweep is earning its keep: these come from
                // FormTypes routed to hand-written readers, which never call the generic reader
                // and so surface none of this on their own.
                $"{fromSpecializedTypes} from specialized-reader types.");
        }

        return new RuntimeNestedPayloadHarvest(textureHashes, alternateTextures, destruction);
    }
}

/// <summary>
///     One runtime sweep's worth of nested payloads, keyed by base-record FormID.
/// </summary>
internal sealed record RuntimeNestedPayloadHarvest(
    Dictionary<uint, RuntimeTextureHashList> TextureHashes,
    Dictionary<uint, IReadOnlyList<AlternateTextureEntry>> AlternateTextures,
    Dictionary<uint, DestructionData> Destruction);
