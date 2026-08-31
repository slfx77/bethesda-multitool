namespace BethesdaMultitool.Core.Formats.Esm.Models.Reflection;

/// <summary>
///     One decoded object from a version-4 BETH reflection stream. A DIFF object carries only the
///     fields explicitly present in its indexed overlay; an OBJT carries every serialized field.
/// </summary>
internal sealed record BethesdaReflectionObject(
    string TypeName,
    IReadOnlyDictionary<string, BethesdaReflectionValue> Fields);

internal abstract record BethesdaReflectionValue;

internal sealed record BethesdaReflectionNullValue : BethesdaReflectionValue;

internal sealed record BethesdaReflectionStringValue(string Value) : BethesdaReflectionValue;

internal sealed record BethesdaReflectionSignedValue(long Value) : BethesdaReflectionValue;

internal sealed record BethesdaReflectionUnsignedValue(ulong Value) : BethesdaReflectionValue;

internal sealed record BethesdaReflectionFloatValue(double Value) : BethesdaReflectionValue;

internal sealed record BethesdaReflectionBoolValue(bool Value) : BethesdaReflectionValue;

internal sealed record BethesdaReflectionObjectValue(BethesdaReflectionObject Value) : BethesdaReflectionValue;

internal sealed record BethesdaReflectionReferenceValue(
    string ValueType,
    BethesdaReflectionValue Value) : BethesdaReflectionValue;

internal sealed record BethesdaReflectionListValue(
    string ElementType,
    IReadOnlyList<BethesdaReflectionValue> Values) : BethesdaReflectionValue;

/// <summary>
///     A class whose CLAS flags select Bethesda's out-of-line USER/USRD serializer. The generic
///     reader validates the side-chunk framing and declared/serialized types, but deliberately
///     preserves the serializer-specific payload as bytes instead of guessing its conversion.
/// </summary>
internal sealed record BethesdaReflectionUserValue(
    string DeclaredType,
    string SerializedType,
    bool IsDiff,
    byte[] SerializedPayload) : BethesdaReflectionValue;
