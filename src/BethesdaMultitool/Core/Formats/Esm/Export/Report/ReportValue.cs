using BethesdaMultitool.Core.Formats.Esm.Export.Support;

namespace BethesdaMultitool.Core.Formats.Esm.Export.Report;

/// <summary>
///     Tagged union for typed field values. Enables numeric comparison/sorting
///     while preserving pre-formatted display strings for text output.
/// </summary>
internal abstract record ReportValue
{
    /// <summary>Pre-formatted display string (used by text/CSV formatters).</summary>
    public abstract string Display { get; }

    // --- Factory helpers for concise construction ---

    /// <summary>Creates an integer report value.</summary>
    public static IntVal Int(int value)
    {
        return new IntVal(value);
    }

    /// <summary>Creates an integer report value with an explicit display string.</summary>
    public static IntVal Int(int value, string display)
    {
        return new IntVal(value, display);
    }

    /// <summary>Creates a floating-point report value, formatting the display string with the given numeric format.</summary>
    public static FloatVal Float(double value, string format = "F1")
    {
        return new FloatVal(value, value.ToString(format));
    }

    /// <summary>Creates a floating-point report value with an explicit display string.</summary>
    public static FloatVal FloatDisplay(double value, string display)
    {
        return new FloatVal(value, display);
    }

    /// <summary>Creates a plain string report value.</summary>
    public static StringVal String(string value)
    {
        return new StringVal(value);
    }

    /// <summary>Creates a boolean report value (displays as Yes/No).</summary>
    public static BoolVal Bool(bool value)
    {
        return new BoolVal(value);
    }

    /// <summary>Creates a boolean report value with an explicit display string.</summary>
    public static BoolVal Bool(bool value, string display)
    {
        return new BoolVal(value, display);
    }

    /// <summary>Creates a FormID report value with an explicit display string.</summary>
    public static FormIdVal FormId(uint raw, string display)
    {
        return new FormIdVal(raw, display);
    }

    /// <summary>Creates a FormID report value, resolving its display name via the given resolver.</summary>
    public static FormIdVal FormId(uint raw, FormIdResolver resolver)
    {
        return new FormIdVal(raw, resolver.FormatFull(raw));
    }

    /// <summary>Creates a list report value wrapping the given sub-values.</summary>
    public static ListVal List(List<ReportValue> items)
    {
        return new ListVal(items);
    }

    /// <summary>Creates a list report value with an explicit display string.</summary>
    public static ListVal List(List<ReportValue> items, string display)
    {
        return new ListVal(items, display);
    }

    /// <summary>Integer value (damage, clip size, health, etc.).</summary>
    internal sealed record IntVal(int Raw) : ReportValue
    {
        private readonly string? _display;

        internal IntVal(int raw, string display) : this(raw)
        {
            _display = display;
        }

        public override string Display => _display ?? Raw.ToString();
    }

    /// <summary>Floating-point value (DPS, fire rate, weight, etc.).</summary>
    internal sealed record FloatVal(double Raw) : ReportValue
    {
        private readonly string? _display;

        internal FloatVal(double raw, string display) : this(raw)
        {
            _display = display;
        }

        public override string Display => _display ?? Raw.ToString("F1");
    }

    /// <summary>Plain string value (names, paths, descriptions).</summary>
    internal sealed record StringVal(string Raw) : ReportValue
    {
        public override string Display => Raw;
    }

    /// <summary>Boolean flag (is playable, respawns, etc.).</summary>
    internal sealed record BoolVal(bool Raw) : ReportValue
    {
        private readonly string? _display;

        internal BoolVal(bool raw, string display) : this(raw)
        {
            _display = display;
        }

        public override string Display => _display ?? (Raw ? "Yes" : "No");
    }

    /// <summary>Resolved FormID reference (stores raw ID + display name).</summary>
    internal sealed record FormIdVal(uint Raw) : ReportValue
    {
        private readonly string? _display;

        internal FormIdVal(uint raw, string display) : this(raw)
        {
            _display = display;
        }

        public override string Display => _display ?? $"0x{Raw:X8}";
    }

    /// <summary>List of sub-values (inventory items, faction ranks, effect entries, placed objects).</summary>
    internal sealed record ListVal(List<ReportValue> Items) : ReportValue
    {
        private readonly string? _display;

        internal ListVal(List<ReportValue> items, string display) : this(items)
        {
            _display = display;
        }

        public override string Display => _display ?? $"{Items.Count} entries";
    }

    /// <summary>
    ///     A composite sub-record within a list (e.g., an inventory item with name + count,
    ///     a placed object with position + rotation). Fields are displayed inline.
    /// </summary>
    internal sealed record CompositeVal(List<ReportField> Fields) : ReportValue
    {
        private readonly string? _display;

        internal CompositeVal(List<ReportField> fields, string display) : this(fields)
        {
            _display = display;
        }

        public override string Display => _display ?? "(composite)";
    }
}
