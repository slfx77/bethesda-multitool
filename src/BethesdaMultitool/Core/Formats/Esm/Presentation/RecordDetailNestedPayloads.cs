using System.Globalization;
using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;

namespace BethesdaMultitool.Core.Formats.Esm.Presentation;

/// <summary>
///     Appends the nested payloads a base record carries — MODS alternate textures, the DEST
///     destruction block, MODT texture hashes — to an already-built
///     <see cref="RecordDetailModel" />.
///     <para>
///         These are held in <see cref="RecordCollection" /> side-indexes rather than on the typed
///         models, because they hang off engine base classes (<c>TESModel</c>,
///         <c>TESModelTextureSwap</c>, <c>BGSDestructibleObjectForm</c>) that 43, 28 and 26 record
///         types respectively inherit. Presenting them from one place means every record type shows
///         them, including the ones whose typed model has no property that could hold them.
///     </para>
/// </summary>
internal static class RecordDetailNestedPayloads
{
    internal static RecordDetailModel Append(
        RecordDetailModel model, RecordCollection records, FormIdResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(records);

        var sections = new List<RecordDetailSection>();

        if (records.AlternateTexturesByFormId.TryGetValue(model.FormId, out var swaps) && swaps.Count > 0)
        {
            sections.Add(BuildAlternateTextures(swaps, resolver));
        }

        if (records.DestructionByFormId.TryGetValue(model.FormId, out var destruction))
        {
            sections.Add(BuildDestruction(destruction, resolver));
        }

        if (records.TextureHashesByFormId.TryGetValue(model.FormId, out var hashes) &&
            hashes.DeclaredCount > 0)
        {
            sections.Add(BuildTextureHashes(hashes));
        }

        if (sections.Count == 0)
        {
            return model;
        }

        return model with { Sections = [.. model.Sections, .. sections] };
    }

    private static RecordDetailSection BuildAlternateTextures(
        IReadOnlyList<AlternateTextureEntry> swaps, FormIdResolver resolver)
    {
        var items = new List<RecordDetailListItem>(swaps.Count);
        foreach (var swap in swaps)
        {
            // An entry whose TXST pointer did not resolve is kept — the shape name and 3D index are
            // still real — but it must not become a navigable link to FormID 0, which would look
            // like a reference the browser simply failed to open.
            var resolved = swap.TextureSetFormId != 0;
            items.Add(new RecordDetailListItem
            {
                Label = swap.ShapeName,
                Value = resolved
                    ? $"{resolver.FormatWithEditorId(swap.TextureSetFormId)} (3D index {swap.Index})"
                    : $"(texture set unresolved) (3D index {swap.Index})",
                LinkedFormId = resolved ? swap.TextureSetFormId : null
            });
        }

        return new RecordDetailSection
        {
            Title = "Alternate Textures",
            Entries =
            [
                new RecordDetailEntry
                {
                    Kind = RecordDetailEntryKind.List,
                    Label = "Alternate Textures",
                    Items = items,
                    ExpandByDefault = true
                }
            ]
        };
    }

    private static RecordDetailSection BuildDestruction(
        DestructionData destruction, FormIdResolver resolver)
    {
        var items = new List<RecordDetailListItem>(destruction.Stages.Count);
        for (var i = 0; i < destruction.Stages.Count; i++)
        {
            var stage = destruction.Stages[i];
            var parts = new List<string>
            {
                $"{stage.HealthPercent}% health",
                $"model stage {stage.DamageStage}"
            };

            if (stage.SelfDamagePerSecond != 0)
            {
                parts.Add($"{stage.SelfDamagePerSecond} dmg/s");
            }

            if (stage.ExplosionFormId != 0)
            {
                parts.Add($"explosion {resolver.FormatWithEditorId(stage.ExplosionFormId)}");
            }

            if (stage.DebrisFormId != 0)
            {
                parts.Add($"debris {resolver.FormatWithEditorId(stage.DebrisFormId)} x{stage.DebrisCount}");
            }

            if (!string.IsNullOrEmpty(stage.ReplacementModel))
            {
                parts.Add($"model {stage.ReplacementModel}");
            }

            items.Add(new RecordDetailListItem
            {
                Label = $"Stage {i}",
                Value = string.Join(", ", parts),
                LinkedFormId = stage.ExplosionFormId != 0 ? stage.ExplosionFormId : null
            });
        }

        var entries = new List<RecordDetailEntry>
        {
            new()
            {
                Kind = RecordDetailEntryKind.Scalar,
                Label = "Health",
                Value = destruction.Health.ToString(CultureInfo.InvariantCulture)
            },
            new()
            {
                Kind = RecordDetailEntryKind.Scalar,
                Label = "Flags",
                Value = $"0x{destruction.Flags:X2}"
            }
        };

        if (items.Count > 0)
        {
            entries.Add(new RecordDetailEntry
            {
                Kind = RecordDetailEntryKind.List,
                Label = "Stages",
                Items = items,
                ExpandByDefault = true
            });
        }

        return new RecordDetailSection { Title = "Destruction", Entries = entries };
    }

    private static RecordDetailSection BuildTextureHashes(RuntimeTextureHashList hashes)
    {
        // Slot order carries the meaning, so an uncaptured slot is rendered in place. Closing the
        // gap would re-attribute every hash after it to the wrong texture slot.
        var rendered = string.Join(" ", hashes.Slots.Select(slot => slot ?? "--"));

        return new RecordDetailSection
        {
            Title = "Texture Hashes",
            Entries =
            [
                new RecordDetailEntry
                {
                    Kind = RecordDetailEntryKind.Scalar,
                    Label = "Resolved textures",
                    // The hashes cover the source build's own texture paths and do not transfer
                    // between the Xbox and PC builds, so the count is the part worth leading with.
                    Value = hashes.IsComplete
                        ? $"{hashes.DeclaredCount} ({rendered})"
                        : $"{hashes.CapturedCount} of {hashes.DeclaredCount} captured ({rendered})"
                }
            ]
        };
    }
}
