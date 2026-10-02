using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using BethesdaMultitool.CLI.Commands.Esm;
using Xunit;

namespace BethesdaMultitool.Tests.Architecture;

/// <summary>
///     IL guard against reflection-only System.Text.Json in the app assembly.
///     <para>
///         The shipped CLI is trimmed and its runtimeconfig sets
///         <c>System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault=false</c>; the test host does not.
///         A <see cref="System.Text.Json.JsonSerializer" /> call that resolves its type metadata by reflection
///         (any overload taking neither a <c>JsonTypeInfo</c> nor a <c>JsonSerializerContext</c>) therefore
///         passes every in-process test and throws <c>InvalidOperationException: Reflection-based
///         serialization has been disabled</c> in the shipped tool — <c>esm packages -f json</c> did exactly
///         that. This scan walks the IL of the net10.0 build of the app assembly (so <c>App/**</c> is excluded
///         by construction) and fails on any such call, and on any <c>new DefaultJsonTypeInfoResolver()</c>,
///         outside a reasoned allowlist.
///     </para>
/// </summary>
public sealed class CliJsonReflectionGuardTests
{
    private const string JsonSerializerType = "System.Text.Json.JsonSerializer";
    private const string DefaultResolverType = "System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver";
    private const string RendererProfilerTraceType = "BethesdaMultitool.Core.Formats.Nif.Rendering.Profiling.RendererProfilerTrace";

    private const string ExplicitDefaultResolverReason =
        "Sets TypeInfoResolver = new DefaultJsonTypeInfoResolver() explicitly, so it does not throw under the " +
        "disabled default; it still depends on TrimMode=partial leaving the app assembly untrimmed. Deferred.";

    private const string GuiOnlyReason =
        "Reached only from App/** and the untrimmed profiler apps (BethesdaRendererProfiler, " +
        "BethesdaMap2DProfiler); the trimmed CLI exe cannot reach it. Deferred.";

    /// <summary>
    ///     Known reflection-only sites, pinned to one member and an exact count. Adding another call to
    ///     an exempt member must fail too: default options and resolver-backed options use the same overload.
    /// </summary>
    private static readonly AllowedSite[] Allowlist =
    [
        new(RendererProfilerTraceType, "Event",
            "Environment-gated (FALLOUT_VIEWER_PROFILE_JSONL) trace of a Dictionary<string, object?> payload; " +
            "moving it to Utf8JsonWriter needs a primitive switch that keeps RendererProfilerTraceTests' payload " +
            "byte-identical. Deferred."),
        new("BethesdaMultitool.Core.Formats.Esm.Plugin.AssetPacking.MeshRenameMapService", "Save", GuiOnlyReason),
        new("BethesdaMultitool.Core.Formats.Esm.Plugin.AssetPacking.MeshRenameMapService", "TryLoad", GuiOnlyReason),
        new("BethesdaMultitool.CLI.Commands.Version.VersionExtractCommand", ".cctor", ExplicitDefaultResolverReason),
        new("BethesdaMultitool.CLI.Commands.Version.VersionExtractCommand", "WriteCacheOutput", ExplicitDefaultResolverReason),
        new("BethesdaMultitool.Core.VersionTracking.Reporting.JsonTimelineWriter", ".cctor", ExplicitDefaultResolverReason),
        new("BethesdaMultitool.Core.VersionTracking.Reporting.JsonTimelineWriter", "WriteTimeline", ExplicitDefaultResolverReason),
        new("BethesdaMultitool.CLI.Commands.Dmp.RttiCommand", ".cctor", ExplicitDefaultResolverReason),
        new("BethesdaMultitool.CLI.Commands.Dmp.RttiCommand", "ExecuteCensusAll", ExplicitDefaultResolverReason),
        new("BethesdaMultitool.CLI.Rendering.Nif.RenderNifHelpers", "WriteIndexAndSummary",
            "mesh cutover owns this file; sprite-index.json still uses reflection JSON - reported, not fixed here. " +
            "(Its options are RenderIndexJsonContext.Default.Options, so the call resolves through a " +
            "source-generated context and does not throw today; the scan flags the JsonSerializerOptions overload " +
            "because it cannot see where the options came from. The JsonTypeInfo overload would clear it.)")
    ];

    private static readonly Lazy<IReadOnlyList<JsonCallSite>> AppAssemblySites =
        new(() => JsonCallScanner.Scan(AppAssemblyPath()));

    [Fact]
    public void NoReflectionOnlyJsonSerializerCalls_OutsideAllowlist()
    {
        var offenders = AppAssemblySites.Value
            .Where(site => site.IsReflectionOnly && !IsAllowed(site))
            .Select(site => site.Describe())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(text => text, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "Reflection-only System.Text.Json calls in the app assembly. The trimmed CLI disables reflection-based " +
            "serialization, so each of these throws in the shipped exe while passing in-process tests. Write the " +
            "document with Utf8JsonWriter (see PackageJsonWriter) or pass a JsonTypeInfo:\n" +
            string.Join("\n", offenders));
    }

    /// <summary>
    ///     Positive control: the scanner must find a deliberately unsafe test-only site. A
    ///     scanner that silently finds nothing (a broken opcode walk, a renamed JsonSerializer overload) would
    ///     otherwise let the guard above pass vacuously.
    /// </summary>
    [Fact]
    public void Scanner_FindsTestOnlyReflectionSite()
    {
        Assert.Contains(JsonCallScanner.Scan(typeof(CliJsonReflectionGuardTests).Assembly.Location), site =>
            site.IsReflectionOnly
            && site.OwnerType == typeof(ReflectionScannerControls).FullName
            && site.Member == nameof(ReflectionScannerControls.SerializeWithDefaultOptions)
            && site.Callee.StartsWith("JsonSerializer.Serialize(", StringComparison.Ordinal));
    }

    /// <summary>
    ///     Positive control for the constructor path: a static field initializer's
    ///     <c>new DefaultJsonTypeInfoResolver()</c> is attributed to the type's <c>.cctor</c> and flagged.
    /// </summary>
    [Fact]
    public void Scanner_FlagsDefaultResolverConstruction()
    {
        Assert.Contains(JsonCallScanner.Scan(typeof(CliJsonReflectionGuardTests).Assembly.Location), site =>
            site.IsReflectionOnly
            && site.OwnerType == typeof(ReflectionScannerControls).FullName
            && site.Member == ".cctor"
            && site.Callee == "new DefaultJsonTypeInfoResolver()");
    }

    /// <summary>
    ///     Discrimination control: CarveManifest serializes through source-generated JsonTypeInfo inside two
    ///     async methods. The scanner must SEE both calls (through the compiler-generated state machines) and
    ///     must NOT flag them — a scanner that flagged every JsonSerializer call, or that missed async bodies,
    ///     fails here.
    /// </summary>
    [Fact]
    public void Scanner_SeesJsonTypeInfoCallsInAsyncMethods_AndDoesNotFlagThem()
    {
        var carveSites = AppAssemblySites.Value
            .Where(site => site.OwnerType == "BethesdaMultitool.Core.Carving.CarveManifest")
            .ToList();

        Assert.Contains(carveSites, site => site.Member == "SaveAsync"
                                            && site.Callee.StartsWith("JsonSerializer.Serialize(", StringComparison.Ordinal));
        Assert.Contains(carveSites, site => site.Member == "LoadAsync"
                                            && site.Callee.StartsWith("JsonSerializer.Deserialize(", StringComparison.Ordinal));
        Assert.All(carveSites, site => Assert.False(site.IsReflectionOnly, site.Describe()));
    }

    [Fact]
    public void Allowlist_EveryEntryNamesATypeAndCarriesAReason()
    {
        Assert.All(Allowlist, entry =>
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.OwnerType));
            Assert.False(string.IsNullOrWhiteSpace(entry.Member));
            Assert.True(entry.Reason.Length >= 40, $"{entry.OwnerType}::{entry.Member}: the reason is too short to explain the exemption.");
        });
    }

    [Fact]
    public void Allowlist_ExactSiteCountsRejectNewCallsAndStaleEntries()
    {
        foreach (var entry in Allowlist)
        {
            var sites = AppAssemblySites.Value.Where(site => site.IsReflectionOnly && Matches(entry, site)).ToList();
            Assert.True(sites.Count == entry.ExpectedCount,
                $"{entry.OwnerType}::{entry.Member}: expected {entry.ExpectedCount} reflection call(s), found " +
                $"{sites.Count}. Remove stale exemptions or review newly introduced sites.\n" +
                string.Join("\n", sites.Select(site => site.Describe())));
        }
    }

    private static bool IsAllowed(JsonCallSite site)
    {
        return Allowlist.Any(entry => Matches(entry, site));
    }

    private static bool Matches(AllowedSite entry, JsonCallSite site) =>
        string.Equals(entry.OwnerType, site.OwnerType, StringComparison.Ordinal) &&
        string.Equals(entry.Member, site.Member, StringComparison.Ordinal);

    private static string AppAssemblyPath()
    {
        var path = typeof(PackagesCommand).Assembly.Location;
        Assert.False(string.IsNullOrEmpty(path), "The app assembly has no file location to scan.");
        return path;
    }

    /// <summary>A tolerated member with an exact number of reflection-only call sites.</summary>
    private sealed record AllowedSite(string OwnerType, string Member, string Reason, int ExpectedCount = 1);

    // Deliberately unsafe metadata-only fixtures: never called, and independent of production exceptions.
    private static class ReflectionScannerControls
    {
        internal static readonly System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver Resolver = new();

        internal static string SerializeWithDefaultOptions(object value) =>
            System.Text.Json.JsonSerializer.Serialize(value);
    }

    /// <summary>
    ///     One call from the app assembly to a scanned System.Text.Json member.
    /// </summary>
    /// <param name="OwnerType">The user-visible declaring type (compiler-generated nesting walked out).</param>
    /// <param name="Member">The source member: a lambda, local function or async/iterator body maps to its host.</param>
    /// <param name="IlMethod">The IL method that actually contains the call (e.g. MoveNext).</param>
    /// <param name="Callee">The called member with its decoded parameter types.</param>
    /// <param name="IsReflectionOnly">True when the call resolves type metadata by reflection.</param>
    private sealed record JsonCallSite(string OwnerType, string Member, string IlMethod, string Callee, bool IsReflectionOnly)
    {
        public string Describe()
        {
            return $"{OwnerType}::{Member} (IL {IlMethod}) -> {Callee}";
        }
    }

    /// <summary>What a scanned member reference is, and whether calling it relies on reflection.</summary>
    private sealed record Callee(string Description, bool IsReflectionOnly);

    /// <summary>
    ///     Walks every method body's IL with System.Reflection.Metadata (operand sizes taken from
    ///     <see cref="OpCodes" />) and reports each call, callvirt, newobj, ldftn or ldvirtftn whose target is a
    ///     <c>JsonSerializer.Serialize*</c>/<c>Deserialize*</c> overload or the DefaultJsonTypeInfoResolver
    ///     constructor.
    /// </summary>
    private static class JsonCallScanner
    {
        private static readonly Dictionary<ushort, OperandType> OperandTypes = typeof(OpCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(OpCode))
            .Select(field => (OpCode)field.GetValue(null)!)
            .GroupBy(opCode => unchecked((ushort)opCode.Value))
            .ToDictionary(group => group.Key, group => group.First().OperandType);

        private static readonly HashSet<ushort> MethodReferencingOpCodes =
        [
            unchecked((ushort)OpCodes.Call.Value),
            unchecked((ushort)OpCodes.Callvirt.Value),
            unchecked((ushort)OpCodes.Newobj.Value),
            unchecked((ushort)OpCodes.Ldftn.Value),
            unchecked((ushort)OpCodes.Ldvirtftn.Value)
        ];

        internal static IReadOnlyList<JsonCallSite> Scan(string assemblyPath)
        {
            using var stream = new FileStream(
                assemblyPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var peReader = new PEReader(stream);
            var metadata = peReader.GetMetadataReader();
            var callees = ClassifyMemberReferences(metadata);
            var sites = new List<JsonCallSite>();

            foreach (var typeHandle in metadata.TypeDefinitions)
            {
                var type = metadata.GetTypeDefinition(typeHandle);
                foreach (var methodHandle in type.GetMethods())
                {
                    var method = metadata.GetMethodDefinition(methodHandle);
                    if (method.RelativeVirtualAddress == 0)
                    {
                        continue;
                    }

                    var ilMethod = metadata.GetString(method.Name);
                    List<int> tokens;
                    try
                    {
                        tokens = ReadMethodTokens(peReader.GetMethodBody(method.RelativeVirtualAddress));
                    }
                    catch (BadImageFormatException ex)
                    {
                        throw new InvalidOperationException(
                            $"Could not walk the IL of {TypeName(metadata, typeHandle)}::{ilMethod}: {ex.Message}", ex);
                    }

                    foreach (var token in tokens)
                    {
                        var target = MetadataTokens.EntityHandle(token);
                        if (target.Kind == HandleKind.MethodSpecification)
                        {
                            target = metadata.GetMethodSpecification((MethodSpecificationHandle)target).Method;
                        }

                        if (target.Kind != HandleKind.MemberReference
                            || !callees.TryGetValue((MemberReferenceHandle)target, out var callee))
                        {
                            continue;
                        }

                        var (owner, member) = DescribeOwner(metadata, typeHandle, ilMethod);
                        sites.Add(new JsonCallSite(owner, member, ilMethod, callee.Description, callee.IsReflectionOnly));
                    }
                }
            }

            return sites;
        }

        /// <summary>
        ///     Classifies every member reference into System.Text.Json once, so the IL walk is a dictionary
        ///     lookup per call. A JsonSerializer overload is safe when any parameter is a JsonTypeInfo,
        ///     JsonTypeInfo&lt;T&gt; or JsonSerializerContext; everything else resolves metadata by reflection.
        /// </summary>
        private static Dictionary<MemberReferenceHandle, Callee> ClassifyMemberReferences(MetadataReader metadata)
        {
            var result = new Dictionary<MemberReferenceHandle, Callee>();
            foreach (var handle in metadata.MemberReferences)
            {
                var reference = metadata.GetMemberReference(handle);
                if (reference.GetKind() != MemberReferenceKind.Method
                    || reference.Parent.Kind != HandleKind.TypeReference)
                {
                    continue;
                }

                var parent = TypeReferenceName(metadata, (TypeReferenceHandle)reference.Parent);
                var name = metadata.GetString(reference.Name);
                if (parent == JsonSerializerType
                    && (name.StartsWith("Serialize", StringComparison.Ordinal)
                        || name.StartsWith("Deserialize", StringComparison.Ordinal)))
                {
                    var signature = reference.DecodeMethodSignature(SignatureTypeNames.Instance, null);
                    var typed = signature.ParameterTypes.Any(IsTypeInfoOrContext);
                    result[handle] = new Callee(
                        $"JsonSerializer.{name}({string.Join(", ", signature.ParameterTypes)})",
                        !typed);
                }
                else if (parent == DefaultResolverType && name == ".ctor")
                {
                    result[handle] = new Callee("new DefaultJsonTypeInfoResolver()", true);
                }
            }

            return result;
        }

        private static bool IsTypeInfoOrContext(string parameterType)
        {
            return parameterType.StartsWith("System.Text.Json.Serialization.Metadata.JsonTypeInfo", StringComparison.Ordinal)
                   || parameterType == "System.Text.Json.Serialization.JsonSerializerContext";
        }

        /// <summary>
        ///     The metadata tokens operated on by call/callvirt/newobj/ldftn/ldvirtftn in one method body.
        ///     Every other instruction is skipped by its operand size; an unknown opcode is a bad image.
        /// </summary>
        private static List<int> ReadMethodTokens(MethodBodyBlock body)
        {
            var tokens = new List<int>();
            var il = body.GetILReader();
            while (il.RemainingBytes > 0)
            {
                var start = il.Offset;
                int code = il.ReadByte();
                if (code == 0xFE)
                {
                    code = 0xFE00 | il.ReadByte();
                }

                if (!OperandTypes.TryGetValue((ushort)code, out var operandType))
                {
                    throw new BadImageFormatException($"Unknown IL opcode 0x{code:X} at IL offset {start}.");
                }

                switch (operandType)
                {
                    case OperandType.InlineNone:
                        break;
                    case OperandType.ShortInlineBrTarget:
                    case OperandType.ShortInlineI:
                    case OperandType.ShortInlineVar:
                        il.Offset += 1;
                        break;
                    case OperandType.InlineVar:
                        il.Offset += 2;
                        break;
                    case OperandType.InlineI8:
                    case OperandType.InlineR:
                        il.Offset += 8;
                        break;
                    case OperandType.InlineSwitch:
                        var targets = il.ReadInt32();
                        il.Offset += checked(targets * 4);
                        break;
                    case OperandType.InlineMethod:
                        var token = il.ReadInt32();
                        if (MethodReferencingOpCodes.Contains((ushort)code))
                        {
                            tokens.Add(token);
                        }

                        break;
                    default:
                        // InlineBrTarget, InlineField, InlineI, InlineSig, InlineString, InlineTok, InlineType,
                        // ShortInlineR: four bytes each.
                        il.Offset += 4;
                        break;
                }
            }

            return tokens;
        }

        /// <summary>
        ///     Maps an IL method to the member a developer wrote: walks out of compiler-generated nested types
        ///     (<c>&lt;&gt;c</c>, <c>&lt;&gt;c__DisplayClass…</c>, <c>&lt;RunAsync&gt;d__3</c>) to the user type, and
        ///     takes the member from a generated method name (<c>&lt;PrintJson&gt;b__5_0</c>) or, for a state
        ///     machine's <c>MoveNext</c>, from the state machine's own name.
        /// </summary>
        private static (string Owner, string Member) DescribeOwner(
            MetadataReader metadata,
            TypeDefinitionHandle typeHandle,
            string ilMethod)
        {
            string? generatedHostMember = null;
            var current = typeHandle;
            while (true)
            {
                var definition = metadata.GetTypeDefinition(current);
                var name = metadata.GetString(definition.Name);
                var declaring = definition.GetDeclaringType();
                if (!name.StartsWith('<') || declaring.IsNil)
                {
                    break;
                }

                generatedHostMember ??= AngleBracketName(name);
                current = declaring;
            }

            var member = AngleBracketName(ilMethod) ?? generatedHostMember ?? ilMethod;
            return (TypeName(metadata, current), member);
        }

        /// <summary><c>&lt;Name&gt;suffix</c> → <c>Name</c>; null for a non-generated or empty (<c>&lt;&gt;c</c>) name.</summary>
        private static string? AngleBracketName(string name)
        {
            if (!name.StartsWith('<'))
            {
                return null;
            }

            var close = name.IndexOf('>', 1);
            return close > 1 ? name[1..close] : null;
        }

        private static string TypeName(MetadataReader metadata, TypeDefinitionHandle handle)
        {
            var definition = metadata.GetTypeDefinition(handle);
            var name = metadata.GetString(definition.Name);
            var declaring = definition.GetDeclaringType();
            if (!declaring.IsNil)
            {
                return TypeName(metadata, declaring) + "+" + name;
            }

            var ns = metadata.GetString(definition.Namespace);
            return ns.Length == 0 ? name : ns + "." + name;
        }

        private static string TypeReferenceName(MetadataReader metadata, TypeReferenceHandle handle)
        {
            var reference = metadata.GetTypeReference(handle);
            var name = metadata.GetString(reference.Name);
            if (reference.ResolutionScope.Kind == HandleKind.TypeReference)
            {
                return TypeReferenceName(metadata, (TypeReferenceHandle)reference.ResolutionScope) + "+" + name;
            }

            var ns = metadata.GetString(reference.Namespace);
            return ns.Length == 0 ? name : ns + "." + name;
        }
    }

    /// <summary>Renders decoded signature types as readable full names (<c>JsonTypeInfo`1&lt;!!0&gt;</c>).</summary>
    private sealed class SignatureTypeNames : ISignatureTypeProvider<string, object?>
    {
        internal static readonly SignatureTypeNames Instance = new();

        public string GetPrimitiveType(PrimitiveTypeCode typeCode)
        {
            return typeCode.ToString();
        }

        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
        {
            var definition = reader.GetTypeDefinition(handle);
            var ns = reader.GetString(definition.Namespace);
            var name = reader.GetString(definition.Name);
            return ns.Length == 0 ? name : ns + "." + name;
        }

        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
        {
            var reference = reader.GetTypeReference(handle);
            var ns = reader.GetString(reference.Namespace);
            var name = reader.GetString(reference.Name);
            return ns.Length == 0 ? name : ns + "." + name;
        }

        public string GetTypeFromSpecification(
            MetadataReader reader,
            object? genericContext,
            TypeSpecificationHandle handle,
            byte rawTypeKind)
        {
            return reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
        }

        public string GetSZArrayType(string elementType)
        {
            return elementType + "[]";
        }

        public string GetArrayType(string elementType, ArrayShape shape)
        {
            return elementType + "[" + new string(',', Math.Max(0, shape.Rank - 1)) + "]";
        }

        public string GetByReferenceType(string elementType)
        {
            return elementType + "&";
        }

        public string GetPointerType(string elementType)
        {
            return elementType + "*";
        }

        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments)
        {
            return genericType + "<" + string.Join(",", typeArguments) + ">";
        }

        public string GetGenericTypeParameter(object? genericContext, int index)
        {
            return "!" + index;
        }

        public string GetGenericMethodParameter(object? genericContext, int index)
        {
            return "!!" + index;
        }

        public string GetFunctionPointerType(MethodSignature<string> signature)
        {
            return "method*";
        }

        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired)
        {
            return unmodifiedType;
        }

        public string GetPinnedType(string elementType)
        {
            return elementType;
        }
    }
}
