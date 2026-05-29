using System.Reflection;
using FalloutXbox360Utils.Core.Formats.Esm.PlannedWriter;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Planner.Parity;

/// <summary>
///     Reflection-driven factory: per planner-enabled record type, build a minimum-viable
///     model object (FormId + EditorId only) and invoke the matching legacy
///     <c>EncodeNew</c> method. Both sides use the same model instance, so the planner-vs-
///     legacy parity is determined purely by encoder behavior, not fixture differences.
/// </summary>
/// <remarks>
///     <para>
///         Approach trade-off: a hand-written switch with 70+ entries would be more
///         explicit but also 70× more boilerplate to maintain when an encoder lands. The
///         existing per-tier parity tests all use the same minimal pattern
///         (<c>new XxxRecord { FormId = 0x01000800, EditorId = "TestXxx" }</c>) so the
///         reflection path covers them uniformly. Record types that need richer fixtures
///         can be added to <see cref="ExplicitOverrides" /> with an explicit builder.
///     </para>
///     <para>
///         Cell-children types (CELL/REFR/ACHR/ACRE) are skipped here — they're covered by
///         <c>PlanCellSectionBuilderParityTests</c> and
///         <c>PlanCellSectionBuilderNewWorldspaceTests</c>, which exercise the cell-hierarchy
///         writer path. Aggregating them through <c>PlanWriter.BuildGrupForType</c> would
///         not match how they're actually emitted.
///     </para>
/// </remarks>
internal static class SyntheticModelFactory
{
    public const uint TestFormId = 0x01000800u;

    /// <summary>
    ///     Record types deliberately excluded from the aggregate sweep. See the class summary.
    /// </summary>
    public static readonly IReadOnlySet<string> SkippedRecordTypes =
        new HashSet<string>(StringComparer.Ordinal) { "CELL", "REFR", "ACHR", "ACRE", "PGRE" };

    /// <summary>
    ///     Hooks for record types whose minimal fixture needs extra fields beyond the
    ///     standard FormId + EditorId pair. Each entry returns a fully-constructed model.
    /// </summary>
    private static readonly Dictionary<string, Func<object>> ExplicitOverrides =
        new(StringComparer.Ordinal)
        {
            // None yet — every currently-registered encoder accepts the minimal fixture.
        };

    /// <summary>
    ///     Construct the minimal model for the given record type.
    /// </summary>
    public static object CreateModel(string recordType)
    {
        if (ExplicitOverrides.TryGetValue(recordType, out var build))
        {
            return build();
        }

        var modelType = GetPlannerModelType(recordType)
            ?? throw new InvalidOperationException(
                $"No planner encoder registered for record type '{recordType}'.");

        var model = Activator.CreateInstance(modelType)
            ?? throw new InvalidOperationException(
                $"Failed to instantiate model type {modelType.FullName}.");

        SetPropertyIfPresent(model, "FormId", TestFormId);
        SetPropertyIfPresent(model, "EditorId", $"Test{recordType}");
        return model;
    }

    /// <summary>
    ///     Look up the legacy <c>EncodeNew</c> method on the legacy encoder for this record
    ///     type and invoke it with the given model. Trailing optional parameters
    ///     (validFormIds, remapTable, etc.) are passed as null — matches the per-tier
    ///     parity tests' minimal-fixture pattern.
    /// </summary>
    public static EncodedRecord InvokeLegacyEncodeNew(string recordType, object model)
    {
        var encoder = RecordEncoderRegistry.CreateDefault().Get(recordType)
            ?? throw new InvalidOperationException(
                $"Legacy registry has no encoder for record type '{recordType}'.");

        var encoderType = encoder.GetType();
        var modelType = model.GetType();

        var method = FindEncodeNew(encoderType, modelType)
            ?? throw new InvalidOperationException(
                $"Encoder {encoderType.FullName} has no EncodeNew method accepting {modelType.FullName}.");

        var parameters = method.GetParameters();
        var args = new object?[parameters.Length];
        args[0] = model;
        for (var i = 1; i < parameters.Length; i++)
        {
            args[i] = parameters[i].HasDefaultValue ? parameters[i].DefaultValue : null;
        }

        var result = method.Invoke(null, args)
            ?? throw new InvalidOperationException(
                $"{encoderType.FullName}.{method.Name} returned null.");
        return (EncodedRecord)result;
    }

    private static Type? GetPlannerModelType(string recordType)
    {
        foreach (var encoder in PlannedEncoders.BuildAll())
        {
            if (string.Equals(encoder.RecordType, recordType, StringComparison.Ordinal))
            {
                return encoder.ModelType;
            }
        }

        return null;
    }

    private static MethodInfo? FindEncodeNew(Type encoderType, Type modelType)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        foreach (var method in encoderType.GetMethods(flags))
        {
            if (!string.Equals(method.Name, "EncodeNew", StringComparison.Ordinal))
            {
                continue;
            }

            var parameters = method.GetParameters();
            if (parameters.Length == 0)
            {
                continue;
            }

            if (parameters[0].ParameterType.IsAssignableFrom(modelType))
            {
                return method;
            }
        }

        return null;
    }

    private static void SetPropertyIfPresent(object instance, string propertyName, object value)
    {
        var prop = instance.GetType().GetProperty(propertyName);
        if (prop is null || !prop.CanWrite)
        {
            return;
        }

        if (!prop.PropertyType.IsInstanceOfType(value))
        {
            return;
        }

        prop.SetValue(instance, value);
    }
}
