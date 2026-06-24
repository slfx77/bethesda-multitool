namespace EsmSchemaGen.Pascal;

/// <summary>
///     A generic, untyped AST of the xEdit builder DSL — the output of <see cref="WbExprParser" />,
///     before any <c>wb*</c>-specific meaning is applied. The <see cref="Ir.IrBuilder" /> lowers these
///     nodes into the typed schema IR. Keeping a generic call tree first means new/unknown <c>wb*</c>
///     builders still parse (as <see cref="WbCall" />) and can be reported rather than crashing.
/// </summary>
public abstract record WbValue;

/// <summary>A <c>wbXxx(args).Modifier(...).Modifier</c> call (args/modifiers possibly empty).</summary>
public sealed record WbCall(string Name, IReadOnlyList<WbValue> Args, IReadOnlyList<WbModifier> Modifiers) : WbValue;

/// <summary>A bare identifier used as a value (e.g. <c>itU32</c>, a signature constant, a symbol ref).</summary>
public sealed record WbIdent(string Name) : WbValue;

public sealed record WbStr(string Value) : WbValue;

public sealed record WbNum(long IntValue, bool IsFloat, double FloatValue) : WbValue;

public sealed record WbBool(bool Value) : WbValue;

public sealed record WbNil : WbValue;

/// <summary>A bracketed list <c>[ … ]</c> (member lists, enum/flag name lists, FormID target lists).</summary>
public sealed record WbList(IReadOnlyList<WbValue> Items) : WbValue;

/// <summary>A fluent modifier in a call chain, e.g. <c>.SetRequired</c> or <c>.SetDefaultNativeValue(1)</c>.</summary>
public sealed record WbModifier(string Name, IReadOnlyList<WbValue> Args);
