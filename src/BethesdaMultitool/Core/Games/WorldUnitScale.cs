using System.Globalization;

namespace BethesdaMultitool.Core.Games;

/// <summary>
///     The physical size of one world unit together with where that number came from. A unit
///     value never travels without its <see cref="Provenance" /> and <see cref="Evidence" />, so a
///     consumer (the model-document units table, <c>mesh info</c>, a fidelity row) can print the
///     grounds beside the number instead of presenting an assumption as a fact
///     (docs/design/model-document-design-20260923.md §4 and §4.1).
///     <para>
///         Immutable and validated on construction and on <c>with</c>: the scale must be finite and
///         positive, and the evidence must be non-blank for every provenance — an assumption states
///         its grounds, a reverse-engineered value states the executable, hash, address and value,
///         an authored value states the field.
///     </para>
/// </summary>
/// <param name="MetersPerUnit">Meters spanned by one world unit; finite and positive.</param>
/// <param name="Provenance">How the number was established.</param>
/// <param name="Evidence">
///     One line of grounds for the number, including the reverse-engineering item (RE-n) that
///     could replace it where one exists. Never blank.
/// </param>
public sealed record WorldUnitScale(double MetersPerUnit, UnitProvenance Provenance, string Evidence)
{
    private readonly double _metersPerUnit = ValidateMetersPerUnit(MetersPerUnit);
    private readonly string _evidence = ValidateEvidence(Evidence);

    /// <summary>Meters spanned by one world unit. Finite and positive.</summary>
    public double MetersPerUnit
    {
        get => _metersPerUnit;
        init => _metersPerUnit = ValidateMetersPerUnit(value);
    }

    /// <summary>The grounds for <see cref="MetersPerUnit" />, one line, never blank.</summary>
    public string Evidence
    {
        get => _evidence;
        init => _evidence = ValidateEvidence(value);
    }

    /// <summary>
    ///     World units in one meter, the reciprocal of <see cref="MetersPerUnit" />. The Gamebryo
    ///     value reads as 70 here and 0.0142857 there; both name the same unit.
    /// </summary>
    public double UnitsPerMeter => 1.0 / MetersPerUnit;

    /// <summary>
    ///     One culture-invariant line for reports and <c>mesh info</c>:
    ///     <c>0.0142857 m per unit | Assumed | Gamebryo 70 units per meter; executable read pending (RE-1)</c>.
    ///     The scale prints with up to seven decimals and no exponent, so 1/70 reads as
    ///     <c>0.0142857</c> and a metric unit as <c>1</c>.
    /// </summary>
    public string Describe()
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{MetersPerUnit:0.#######} m per unit | {Provenance} | {Evidence}");
    }

    private static double ValidateMetersPerUnit(double metersPerUnit)
    {
        return double.IsFinite(metersPerUnit) && metersPerUnit > 0
            ? metersPerUnit
            : throw new ArgumentOutOfRangeException(
                nameof(metersPerUnit),
                metersPerUnit,
                "A world unit must span a finite, positive number of meters.");
    }

    private static string ValidateEvidence(string evidence)
    {
        return !string.IsNullOrWhiteSpace(evidence)
            ? evidence
            : throw new ArgumentException(
                "A unit value must state its evidence: the grounds for an assumption, or the " +
                "executable, hash, address and value a reverse-engineered number was read from.",
                nameof(evidence));
    }
}
