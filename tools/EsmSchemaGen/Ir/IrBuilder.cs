using EsmSchemaGen.Pascal;

namespace EsmSchemaGen.Ir;

/// <summary>
///     Lowers the generic <see cref="WbValue" /> call AST into the typed schema <see cref="SchemaNode" />
///     IR. Unmapped <c>wb*</c> builders/symbols become <see cref="UnknownMemberDef" /> and are tallied
///     in <see cref="UnknownCalls" /> so coverage is visible rather than silently lost — the generator
///     can then fail (or report) on records that still contain unknown members.
/// </summary>
public sealed class IrBuilder
{
    /// <summary>Count of each unmapped <c>wb*</c> call/symbol name encountered, for coverage reporting.</summary>
    public Dictionary<string, int> UnknownCalls { get; } = new(StringComparer.Ordinal);

    /// <summary>
    ///     True when emitting a Fallout 4 / 76 (or later) game, so Common helpers whose layout branches on
    ///     <c>IsFO4Plus(...)</c> (e.g. <c>wbLeveledListEntry</c>) pick the post-FO4 variant. Set per file by
    ///     <see cref="DefinitionsFileParser.LoadWithCommon" /> from the definitions filename.
    /// </summary>
    public bool IsFo4Plus { get; set; }

    // Pascal identifiers are case-insensitive, so all symbol/builder name matching is OrdinalIgnoreCase
    // (xEdit's sources mix casings, e.g. wbFormIDCk / wbFormIDCK / wbFormIDck for the same function).
    private readonly Dictionary<string, MemberDef> _symbols = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, EnumDef> _enumSymbols = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FlagsDef> _flagsSymbols = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Resolved <c>wb* := …</c> builder-symbol assignments (the per-file / Common symbol table).</summary>
    public IReadOnlyDictionary<string, MemberDef> Symbols => _symbols;

    /// <summary>Named enum symbols (<c>wbXEnum := wbEnum([...])</c>), referenced by integer fields.</summary>
    public IReadOnlyDictionary<string, EnumDef> EnumSymbols => _enumSymbols;

    /// <summary>Named flags symbols (<c>wbXFlags := wbFlags([...])</c>), referenced by integer fields.</summary>
    public IReadOnlyDictionary<string, FlagsDef> FlagsSymbols => _flagsSymbols;

    /// <summary>
    ///     Record a <c>wbName := wbExpr</c> assignment so later references resolve. Enum/flags
    ///     assignments go to their own tables (they are field metadata, not members), so referencing
    ///     integer fields can resolve them by name and they never show up as unknown members.
    /// </summary>
    public void DefineSymbol(string name, WbValue rhs)
    {
        if (rhs is WbCall call)
        {
            if (IsBuilder(call.Name, "wbEnum"))
            {
                _enumSymbols[name] = BuildEnum(call.Args);
                return;
            }

            if (IsBuilder(call.Name, "wbFlags"))
            {
                _flagsSymbols[name] = BuildFlags(call.Args);
                return;
            }
        }

        _symbols[name] = BuildMember(rhs);
    }

    /// <summary>Case-insensitive builder-name comparison (Pascal identifier semantics).</summary>
    private static bool IsBuilder(string actual, string expected)
        => string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);

    /// <summary>Build a top-level record from a <c>wbRecord(...)</c> call.</summary>
    public RecordDef BuildRecord(WbCall call)
    {
        if (!call.Name.Equals("wbRecord", StringComparison.Ordinal))
        {
            throw new ArgumentException($"Expected wbRecord, got {call.Name}.", nameof(call));
        }

        var signature = SignatureOf(call.Args) ?? "????";
        var name = FirstString(call.Args);
        var members = BuildMembers(call.Args);
        return new RecordDef(signature, members) { Name = name };
    }

    /// <summary>Convenience: parse a single record from raw Pascal source text.</summary>
    public RecordDef BuildRecord(string source) => BuildRecord((WbCall)WbExprParser.Parse(source));

    private IReadOnlyList<MemberDef> BuildMembers(IReadOnlyList<WbValue> args)
    {
        // The member list is the LAST bracketed list: builders like wbStructSK(SIG, 'Name',
        // [sort-key indices], [members]) carry an earlier index list that must not be mistaken for members.
        var list = LastList(args);
        if (list is null)
        {
            return [];
        }

        var members = new List<MemberDef>(list.Items.Count);
        foreach (var item in list.Items)
        {
            members.Add(BuildMember(item));
        }

        return members;
    }

    private MemberDef BuildMember(WbValue value)
    {
        switch (value)
        {
            case WbIdent ident:
                return MapNamedSymbol(ident.Name);
            case WbCall call:
            {
                var member = MapCall(call);
                return ApplyModifiers(member, call.Modifiers);
            }
            default:
                return Unknown(value.GetType().Name);
        }
    }

    private MemberDef MapCall(WbCall call)
    {
        var args = call.Args;
        // Lowercased dispatch — Pascal identifiers are case-insensitive (see IsBuilder).
        switch (call.Name.ToLowerInvariant())
        {
            case "wbinteger":
                return BuildInteger(args);
            case "wbfloat":
            case "wbfloatangle":
                return Field(PrimType.Float, args);
            case "wbdouble":
                return Field(PrimType.Double, args);
            case "wbhalffloat":
                return Field(PrimType.Half, args);
            case "wbstring":
            case "wbstringforward":
            case "wbstringlc":
                return Field(PrimType.ZString, args);
            case "wbstringkc":
                return Field(PrimType.StringKC, args);
            case "wblstring":
            case "wblstringkc":
                return Field(PrimType.LString, args);
            case "wbbytearray":
            case "wbbytearraykc":
                return Field(PrimType.ByteArray, args);
            case "wbformid":
            case "wbformidck":
            case "wbformidcknoreach":
            case "wbformidnoreach":
                return BuildFormId(args);
            case "wbstruct":
            case "wbstructsk":
            case "wbstructz":
                return BuildStruct(args);
            case "wbrstruct":
            case "wbrstructsk":
            case "wbrstructexsk":
            case "wbrstructs":
                return BuildStruct(args);
            case "wbarray":
            case "wbarrays":
                return BuildArray(args, greedy: false);
            case "wbrarray":
            case "wbrarrays":
                return BuildArray(args, greedy: true);
            case "wbunion":
            case "wbrunion":
                return BuildUnion(args);
            case "wbunused":
                return new UnusedDef(FirstInt(args) is { } sz ? (int)sz : 0);
            case "wbempty":
                return new EmptyDef { Signature = SignatureOf(args), Name = FirstString(args) };
            case "wbtexturedmodel":
                return BuildTexturedModel(args);
            case "wbunknown":
                // xEdit's explicit "unmapped bytes" subrecord — model as an opaque byte array.
                return Field(PrimType.ByteArray, args);
            case "wbfromversion":
                return BuildFromVersion(args);
            default:
                // A reference to a defined symbol (e.g. `wbModel.SetRequired` — no args), or a
                // Common helper function (e.g. `wbByteColors('Sunrise')`), else genuinely unknown.
                if (args.Count == 0 && _symbols.TryGetValue(call.Name, out var symbol))
                {
                    return symbol;
                }

                return CommonHelpers.TryBuild(call.Name, args, IsFo4Plus) ?? Unknown(call.Name);
        }
    }

    private FieldDef BuildInteger(IReadOnlyList<WbValue> args)
    {
        string? sig = null;
        string? name = null;
        PrimType prim;
        WbValue? enumArg;

        if (args.Count > 0 && args[0] is WbIdent id0 && !IsItType(id0.Name))
        {
            // signature-first: wbInteger(SIG, 'Name', itXX, enum?)
            sig = ResolveSignature(id0.Name);
            name = (args.ElementAtOrDefault(1) as WbStr)?.Value;
            prim = MapItType((args.ElementAtOrDefault(2) as WbIdent)?.Name);
            enumArg = args.ElementAtOrDefault(3);
        }
        else if (args.Count > 0 && args[0] is WbIdent itOnly && IsItType(itOnly.Name))
        {
            // type-first inline: wbInteger(itXX, enum?)
            prim = MapItType(itOnly.Name);
            enumArg = args.ElementAtOrDefault(1);
        }
        else
        {
            // name-first: wbInteger('Name', itXX, enum?)
            name = (args.ElementAtOrDefault(0) as WbStr)?.Value;
            prim = MapItType((args.ElementAtOrDefault(1) as WbIdent)?.Name);
            enumArg = args.ElementAtOrDefault(2);
        }

        var field = new FieldDef(prim) { Signature = sig, Name = name };
        return ApplyIntegerEnum(field, enumArg);
    }

    private FieldDef ApplyIntegerEnum(FieldDef field, WbValue? enumArg)
    {
        if (enumArg is WbCall call)
        {
            if (IsBuilder(call.Name, "wbEnum"))
            {
                return field with { InlineEnum = BuildEnum(call.Args) };
            }

            if (IsBuilder(call.Name, "wbFlags"))
            {
                return field with { InlineFlags = BuildFlags(call.Args) };
            }
        }

        if (enumArg is WbIdent r)
        {
            return field with { EnumRefName = r.Name };
        }

        return field;
    }

    private FieldDef Field(PrimType type, IReadOnlyList<WbValue> args)
    {
        return new FieldDef(type)
        {
            Signature = SignatureOf(args),
            Name = FirstString(args),
            FixedSize = (int?)FirstInt(SkipSignature(args))
        };
    }

    private FormIdDef BuildFormId(IReadOnlyList<WbValue> args)
    {
        var targets = FirstList(args)?.Items
            .OfType<WbIdent>()
            .Select(i => ResolveSignature(i.Name))
            .ToList() ?? [];

        return new FormIdDef
        {
            Signature = SignatureOf(args),
            Name = FirstString(args),
            Targets = targets
        };
    }

    private StructDef BuildStruct(IReadOnlyList<WbValue> args)
    {
        return new StructDef(BuildMembers(args))
        {
            Signature = SignatureOf(args),
            Name = FirstString(args)
        };
    }

    /// <summary>
    ///     The Common <c>wbTexturedModel(aName, [signatures], [textureSubRecords])</c> helper: an RStruct
    ///     grouping the model-filename string + model-info data (the signatures) followed by the texture
    ///     subrecords. Lives here (not <see cref="CommonHelpers" />) because it lowers nested member
    ///     arguments. Field types are approximate — the oracle only needs the subrecord signatures
    ///     captured (so the completeness gate knows MODL/MODB/MODT/MODS etc. are modeled).
    /// </summary>
    private MemberDef BuildTexturedModel(IReadOnlyList<WbValue> args)
    {
        var name = args.OfType<WbStr>().FirstOrDefault()?.Value ?? "Model";
        var lists = args.OfType<WbList>().ToList();
        var signatures = lists.Count > 0
            ? lists[0].Items.OfType<WbIdent>().Select(i => ResolveSignature(i.Name)).ToList()
            : [];

        var members = new List<MemberDef>();
        for (var i = 0; i < signatures.Count; i++)
        {
            members.Add(i == 0
                ? new FieldDef(PrimType.ZString) { Signature = signatures[i], Name = "Model Filename" }
                : new FieldDef(PrimType.ByteArray) { Signature = signatures[i], Name = "Model Info" });
        }

        if (lists.Count > 1)
        {
            foreach (var textureMember in lists[1].Items)
            {
                members.Add(BuildMember(textureMember));
            }
        }

        return new StructDef(members) { Name = name };
    }

    /// <summary>
    ///     <c>wbFromVersion(formVersion, member)</c> — a form-version gate around a single member. The
    ///     oracle unwraps to the inner member (the version threshold is a runtime concern modeled later).
    /// </summary>
    private MemberDef BuildFromVersion(IReadOnlyList<WbValue> args)
    {
        var inner = args.FirstOrDefault(a => a is WbCall || (a is WbIdent id && !IsItType(id.Name)));
        return inner is not null ? BuildMember(inner) : Unknown("wbFromVersion");
    }

    private MemberDef BuildArray(IReadOnlyList<WbValue> args, bool greedy)
    {
        // The element is the first member-like argument after any leading signature: a wb* call OR a
        // symbol reference (WbIdent). (Previously only calls were considered, so symbol-ref elements —
        // e.g. wbArray('x', wbSomeSymbol) — fell through to an unknown element.)
        var rest = SkipSignature(args).ToList();
        var elementValue = rest.FirstOrDefault(a => a is WbCall || (a is WbIdent id && !IsItType(id.Name)));
        var element = elementValue is not null ? BuildMember(elementValue) : Unknown("array-element");
        var count = greedy ? 0 : (int?)FirstInt(rest) ?? -1;
        return new ArrayDef(element)
        {
            Signature = SignatureOf(args),
            Name = FirstString(args),
            Count = count
        };
    }

    private UnionDef BuildUnion(IReadOnlyList<WbValue> args)
    {
        var decider = SkipSignature(args).OfType<WbIdent>().FirstOrDefault(i => !IsItType(i.Name))?.Name
                      ?? "<unknown-decider>";
        var variants = FirstList(args)?.Items.Select(BuildMember).ToList() ?? [];
        return new UnionDef(decider, variants)
        {
            Signature = SignatureOf(args),
            Name = FirstString(args)
        };
    }

    private MemberDef MapNamedSymbol(string name)
    {
        // The per-game / Common symbol table (wb* := …) resolves the vast majority of references.
        if (_symbols.TryGetValue(name, out var symbol))
        {
            return symbol;
        }

        // A bare helper reference — a Common function invoked with all-default args and no parens
        // (Pascal allows `wbOBND` / `wbGenericModel` without `()`), so it parses as an identifier.
        if (CommonHelpers.TryBuild(name, [], IsFo4Plus) is { } helper)
        {
            return helper;
        }

        // Fallback for the ubiquitous TES4-family symbols before a symbol table is populated.
        switch (name)
        {
            case "wbEDID":
                return new FieldDef(PrimType.ZString) { Signature = "EDID", Name = "Editor ID" };
            case "wbEDIDReq":
                return new FieldDef(PrimType.ZString) { Signature = "EDID", Name = "Editor ID", Required = true };
            case "wbFULL":
                return new FieldDef(PrimType.StringKC) { Signature = "FULL", Name = "Name" };
            default:
                return Unknown(name);
        }
    }

    private static EnumDef BuildEnum(IReadOnlyList<WbValue> args)
    {
        var list = (args.FirstOrDefault(a => a is WbList) as WbList)?.Items ?? [];
        var members = new List<EnumMember>();
        long value = 0;
        foreach (var item in list)
        {
            switch (item)
            {
                case WbNum num:
                    value = num.IntValue;
                    break;
                case WbStr s:
                    members.Add(new EnumMember(value, s.Value));
                    value++;
                    break;
            }
        }

        return new EnumDef(null, members);
    }

    private static FlagsDef BuildFlags(IReadOnlyList<WbValue> args)
    {
        // wbFlags([...]) or wbFlags(wbFlagsList([...])) — dig for the first bracketed list.
        var list = FindFirstList(args);
        var bits = new List<FlagMember>();
        var bit = 0;
        foreach (var item in list)
        {
            switch (item)
            {
                case WbNum num:
                    bit = (int)num.IntValue;
                    break;
                case WbStr s:
                    bits.Add(new FlagMember(bit, s.Value));
                    bit++;
                    break;
            }
        }

        return new FlagsDef(null, bits);
    }

    private static IReadOnlyList<WbValue> FindFirstList(IReadOnlyList<WbValue> args)
    {
        foreach (var a in args)
        {
            switch (a)
            {
                case WbList list:
                    return list.Items;
                case WbCall call:
                {
                    var nested = FindFirstList(call.Args);
                    if (nested.Count > 0)
                    {
                        return nested;
                    }

                    break;
                }
            }
        }

        return [];
    }

    private MemberDef ApplyModifiers(MemberDef member, IReadOnlyList<WbModifier> modifiers)
    {
        var required = member.Required;
        var def = member.DefaultValue;
        foreach (var mod in modifiers)
        {
            switch (mod.Name)
            {
                case "SetRequired":
                    required = true;
                    break;
                case "SetDefaultNativeValue" when mod.Args.Count > 0 && mod.Args[0] is WbNum num:
                    def = num.IntValue;
                    break;
            }
        }

        return member with { Required = required, DefaultValue = def };
    }

    private MemberDef Unknown(string name)
    {
        UnknownCalls[name] = UnknownCalls.GetValueOrDefault(name) + 1;
        return new UnknownMemberDef(name);
    }

    // --- small extraction helpers ---

    private static string? SignatureOf(IReadOnlyList<WbValue> args)
        => args.Count > 0 && args[0] is WbIdent id && !IsItType(id.Name) ? ResolveSignature(id.Name) : null;

    private static IEnumerable<WbValue> SkipSignature(IReadOnlyList<WbValue> args)
        => args.Count > 0 && args[0] is WbIdent id && !IsItType(id.Name) ? args.Skip(1) : args;

    private static string? FirstString(IEnumerable<WbValue> args)
        => args.OfType<WbStr>().FirstOrDefault()?.Value;

    private static long? FirstInt(IEnumerable<WbValue> args)
        => args.OfType<WbNum>().Select(n => (long?)n.IntValue).FirstOrDefault();

    private static WbList? FirstList(IReadOnlyList<WbValue> args)
        => args.OfType<WbList>().FirstOrDefault();

    private static WbList? LastList(IReadOnlyList<WbValue> args)
        => args.OfType<WbList>().LastOrDefault();

    private static bool IsItType(string name) => name.StartsWith("it", StringComparison.Ordinal);

    /// <summary>
    ///     Resolve a signature constant identifier to its 4-char value. The xEdit signature consts are
    ///     overwhelmingly their own literal value (<c>WEAP = 'WEAP'</c>); a proper lookup against
    ///     <c>wbDefinitionsSignatures.pas</c> is wired in a later stage. <c>NULL</c> maps to empty.
    /// </summary>
    private static string ResolveSignature(string ident) => ident == "NULL" ? "" : ident;

    private static PrimType MapItType(string? itType) => itType switch
    {
        "itU8" => PrimType.U8,
        "itS8" => PrimType.S8,
        "itU16" => PrimType.U16,
        "itS16" => PrimType.S16,
        "itU24" => PrimType.U24,
        "itU32" => PrimType.U32,
        "itS32" => PrimType.S32,
        "itU64" => PrimType.U64,
        "itS64" => PrimType.S64,
        _ => PrimType.U32
    };
}
