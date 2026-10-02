using System.Globalization;
using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;

namespace BethesdaMultitool.Core.Formats.Esm.Presentation;

internal static class ImageSpaceModifierDetailBuilder
{
    internal static RecordDetailModel Build(ImageSpaceModifierRecord record, FormIdResolver resolver)
    {
        var sections = new List<RecordDetailSection>
        {
            RecordDetailHelpers.Section("Identity",
            [
                RecordDetailHelpers.Scalar("Form ID", $"0x{record.FormId:X8}"),
                RecordDetailHelpers.Scalar("Editor ID", record.EditorId),
                RecordDetailHelpers.Scalar("Offset", $"0x{record.Offset:X}"),
                RecordDetailHelpers.Scalar("Source", record.FromRuntime ? "Captured" : "Stored"),
                RecordDetailHelpers.Scalar("Byte order", record.IsBigEndian ? "big-endian" : "little-endian")
            ]),
            RecordDetailHelpers.Section("Settings",
            [
                RecordDetailHelpers.Scalar("Animatable", record.Data?.IsAnimatable.ToString()),
                RecordDetailHelpers.Scalar("Duration", record.Data is { } data ? Number(data.Duration) : null),
                RecordDetailHelpers.Link("Intro sound", record.IntroSoundFormId, resolver),
                RecordDetailHelpers.Link("Outro sound", record.OutroSoundFormId, resolver)
            ])
        };

        foreach (var parameter in record.Parameters)
        {
            sections.Add(Curve($"{parameter.Parameter} / Multiply", parameter.Multiply));
            sections.Add(Curve($"{parameter.Parameter} / Add", parameter.Add));
        }
        foreach (var (signature, keys) in record.ScalarTimelines)
        {
            sections.Add(Curve(signature, keys));
        }
        sections.Add(ColorCurve("Tint (TNAM)", record.TintColorTimeline));
        sections.Add(ColorCurve("Fade (NAM3)", record.FadeColorTimeline));
        if (record.Data is { } settings)
        {
            sections.Add(RecordDetailHelpers.ListSection("DNAM raw slots", settings.RawPayload.Select((value, index) =>
                new RecordDetailListItem { Label = $"[{index + 2}]", Value = $"0x{value:X8}" }).ToList()));
        }
        sections.Add(RecordDetailHelpers.ListSection("Ordered subrecords", record.OrderedSubrecords.Select((entry, index) =>
            new RecordDetailListItem
            {
                Label = $"[{index}] {EscapeSignature(entry.Signature)}",
                Value = $"{entry.Data.Length.ToString(CultureInfo.InvariantCulture)} bytes: {Convert.ToHexString(entry.Data)}"
            }).ToList()));
        return RecordDetailHelpers.Model("IMAD", record.FormId, record.EditorId, null, sections);
    }

    private static RecordDetailSection Curve(string title, IReadOnlyList<ImageSpaceModifierFloatKey> keys) =>
        RecordDetailHelpers.ListSection(title, keys.Select((key, index) => new RecordDetailListItem
        {
            Label = $"[{index}] t={Number(key.Time)}", Value = Number(key.Value)
        }).ToList());

    private static RecordDetailSection ColorCurve(string title, IReadOnlyList<ImageSpaceModifierColorKey> keys) =>
        RecordDetailHelpers.ListSection(title, keys.Select((key, index) => new RecordDetailListItem
        {
            Label = $"[{index}] t={Number(key.Time)}",
            Value = $"RGBA {Number(key.Red)}, {Number(key.Green)}, {Number(key.Blue)}, {Number(key.Alpha)}"
        }).ToList());

    private static string Number(float value) => value.ToString("R", CultureInfo.InvariantCulture);

    private static string EscapeSignature(string signature) => string.Concat(signature.Select(character =>
        char.IsControl(character) ? $"\\x{(int)character:X2}" : character.ToString()));
}
