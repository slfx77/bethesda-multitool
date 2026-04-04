namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

/// <summary>
///     Deep structural equality for RecordReport objects. Used by delta encoding
///     to determine if a record has changed between builds, avoiding false positives
///     from JSON serialization differences (field ordering, float precision).
/// </summary>
internal static class RecordReportComparer
{
    private const double FloatEpsilon = 1e-6;

    internal static bool Equals(RecordReport a, RecordReport b)
    {
        if (ReferenceEquals(a, b))
            return true;

        return a.RecordType == b.RecordType
               && a.FormId == b.FormId
               && a.EditorId == b.EditorId
               && a.DisplayName == b.DisplayName
               && SectionsEqual(a.Sections, b.Sections);
    }

    private static bool SectionsEqual(List<ReportSection> a, List<ReportSection> b)
    {
        if (a.Count != b.Count)
            return false;

        for (var i = 0; i < a.Count; i++)
        {
            if (a[i].Name != b[i].Name)
                return false;
            if (!FieldsEqual(a[i].Fields, b[i].Fields))
                return false;
        }

        return true;
    }

    private static bool FieldsEqual(List<ReportField> a, List<ReportField> b)
    {
        if (a.Count != b.Count)
            return false;

        for (var i = 0; i < a.Count; i++)
        {
            if (a[i].Key != b[i].Key)
                return false;
            if (a[i].FormIdRef != b[i].FormIdRef)
                return false;
            if (!ValuesEqual(a[i].Value, b[i].Value))
                return false;
        }

        return true;
    }

    private static bool ValuesEqual(ReportValue a, ReportValue b)
    {
        if (ReferenceEquals(a, b))
            return true;

        return (a, b) switch
        {
            (ReportValue.IntVal ia, ReportValue.IntVal ib) =>
                ia.Raw == ib.Raw,

            (ReportValue.FloatVal fa, ReportValue.FloatVal fb) =>
                Math.Abs(fa.Raw - fb.Raw) < FloatEpsilon,

            (ReportValue.StringVal sa, ReportValue.StringVal sb2) =>
                string.Equals(sa.Raw, sb2.Raw, StringComparison.Ordinal),

            (ReportValue.BoolVal ba, ReportValue.BoolVal bb) =>
                ba.Raw == bb.Raw,

            (ReportValue.FormIdVal fa2, ReportValue.FormIdVal fb2) =>
                fa2.Raw == fb2.Raw,

            (ReportValue.ListVal la, ReportValue.ListVal lb) =>
                ListItemsEqual(la.Items, lb.Items),

            (ReportValue.CompositeVal ca, ReportValue.CompositeVal cb) =>
                FieldsEqual(ca.Fields, cb.Fields),

            _ => false
        };
    }

    private static bool ListItemsEqual(List<ReportValue> a, List<ReportValue> b)
    {
        if (a.Count != b.Count)
            return false;

        for (var i = 0; i < a.Count; i++)
        {
            if (!ValuesEqual(a[i], b[i]))
                return false;
        }

        return true;
    }
}
