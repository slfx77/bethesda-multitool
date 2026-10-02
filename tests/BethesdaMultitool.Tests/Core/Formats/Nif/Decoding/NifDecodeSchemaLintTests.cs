using BethesdaMultitool.Core.Formats.Nif.Conditions;
using BethesdaMultitool.Core.Formats.Nif.Conversion;
using BethesdaMultitool.Core.Formats.Nif.Decoding;
using BethesdaMultitool.Core.Formats.Nif.Schema;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Decoding;

/// <summary>
///     Schema lint for the cut-1a typed block types (the Typed rows of the plan's mapping table): at 20.2.0.7, user 11
///     and every cut-1a BS version, every <c>cond</c>, <c>vercond</c>, <c>length</c>, <c>width</c> and <c>arg</c>
///     reachable from those types compiles strictly and names only fields an enclosing definition declares; every
///     <c>#ARG#</c> has an argument passed to it; every vercond <c>#MACRO#</c> is known to <see cref="NifVersionExpr" />;
///     every type resolves; every Ref/Ptr template is a block type. The walk here is independent of the decoder's: it
///     visits every branch without evaluating conditions.
/// </summary>
public class NifDecodeSchemaLintTests
{
    private static readonly string[] CutOneATypedBlocks =
    [
        // Nodes and layers.
        "NiNode", "BSFadeNode", "BSValueNode", "BSOrderedNode", "BSMultiBoundNode", "BSBlastNode", "BSDamageStage",
        "BSDebrisNode", "BSRangeNode", "BSMasterParticleSystem", "NiBillboardNode", "NiSwitchNode", "NiLODNode",
        "NiRangeLODData", "NiScreenLODData", "NiDefaultAVObjectPalette",

        // Geometry and morphs.
        "NiTriShape", "NiTriStrips", "BSSegmentedTriShape", "NiTriShapeData", "NiTriStripsData",
        "BSPackedAdditionalGeometryData", "NiMorphData",

        // Skin.
        "NiSkinInstance", "BSDismemberSkinInstance", "NiSkinData", "NiSkinPartition",

        // Materials, properties and textures.
        "NiAlphaProperty", "NiStencilProperty", "NiZBufferProperty", "NiVertexColorProperty", "NiMaterialProperty",
        "BSShaderPPLightingProperty", "Lighting30ShaderProperty", "BSShaderNoLightingProperty", "SkyShaderProperty",
        "TileShaderProperty", "TallGrassShaderProperty", "BSShaderTextureSet", "NiTexturingProperty", "NiSourceTexture"
    ];

    /// <summary>The non-macro tokens a vercond may use (variables and operators), written out independently.</summary>
    private static readonly HashSet<string> VersionTokens = new(StringComparer.Ordinal)
    {
        "#VER#", "#BSVER#", "#USER#", "#AND#", "#OR#", "#NOT#", "#GT#", "#GTE#", "#LT#", "#LTE#", "#EQ#", "#NEQ#"
    };

    [Fact]
    public void EveryReachableExpressionCompilesStrictlyAndNamesOnlyDeclaredFields()
    {
        var problems = Lint(NifSchema.LoadEmbedded(), CutOneATypedBlocks, out var checkedExpressions);

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));

        // The walk must actually reach the expressions that matter, or an empty problem list proves nothing.
        Assert.Contains("NiTriShapeData.UV Sets length", checkedExpressions);
        Assert.Contains("NiTriShapeData.Tangents cond", checkedExpressions);
        Assert.Contains("NiMaterialProperty.Emissive Mult vercond", checkedExpressions);
        Assert.Contains("NiSkinData.Bone List<BoneData>.Vertex Weights cond", checkedExpressions);
        Assert.Contains(
            "BSPackedAdditionalGeometryData.Blocks<NiAGDDataBlocks>.Data Block<NiAGDDataBlock>.Shader Index cond",
            checkedExpressions);
        Assert.Contains("NiTriStripsData.Points width", checkedExpressions);
    }

    /// <summary>Control: the lint reports each kind of defect in a deliberately broken definition.</summary>
    [Fact]
    public void Lint_ReportsEachKindOfDefect()
    {
        const string xml = """
            <niftoolsxml version="0.10.0.0">
              <basic name="uint" integral="true" size="4" />
              <basic name="Ref" integral="true" generic="true" size="4" />
              <niobject name="NiObject" abstract="true" />
              <niobject name="LintBad" inherit="NiObject">
                <field name="Count" type="uint" />
                <field name="Unsupported" type="uint" length="Count #MUL# 2" />
                <field name="Undeclared" type="uint" cond="Missing Field" />
                <field name="Macro" type="uint" vercond="#NOT_A_MACRO#" />
                <field name="Argument" type="uint" length="#ARG#" />
                <field name="Unknown" type="NoSuchType" />
                <field name="Link" type="Ref" template="NoSuchBlock" />
                <field name="Bound" type="uint" since="twenty" />
              </niobject>
            </niftoolsxml>
            """;
        var schema = NifDecodingTestSupport.SchemaFromXml(xml);

        var problems = Lint(schema, ["LintBad"], out _);

        foreach (var field in new[] { "Unsupported", "Undeclared", "Macro", "Argument", "Unknown", "Link", "Bound" })
        {
            Assert.Contains(problems, p => p.Contains($"LintBad.{field}", StringComparison.Ordinal));
        }

        Assert.DoesNotContain(problems, p => p.Contains("LintBad.Count", StringComparison.Ordinal));
    }

    private static List<string> Lint(NifSchema schema, IEnumerable<string> blockTypes, out HashSet<string> checkedAt)
    {
        var problems = new HashSet<string>(StringComparer.Ordinal);
        checkedAt = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in blockTypes)
        {
            var definition = schema.GetObject(type);
            if (definition is null || definition.IsAbstract)
            {
                problems.Add($"{type}: not a concrete block type in nif.xml");
                continue;
            }

            foreach (var bs in NifTestFileBuilder.CutOneABsVersions)
            {
                var walk = new LintWalk(schema, type,
                    new NifVersionContext { Version = 0x14020007, UserVersion = 11, BsVersion = (int)bs },
                    problems, checkedAt);
                walk.Fields(definition.AllFields, [Names(definition.AllFields)], false, null, type);
            }
        }

        return [.. problems.Order(StringComparer.Ordinal)];
    }

    private static HashSet<string> Names(IEnumerable<NifFieldDef> fields)
    {
        return fields.Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>One block type at one version: visits every reachable field and struct.</summary>
    private sealed class LintWalk(
        NifSchema schema,
        string blockType,
        NifVersionContext version,
        HashSet<string> problems,
        HashSet<string> checkedAt)
    {
        private readonly HashSet<string> _visited = new(StringComparer.Ordinal);

        public void Fields(
            IReadOnlyList<NifFieldDef> fields,
            IReadOnlyList<HashSet<string>> declared,
            bool hasArgument,
            string? template,
            string where)
        {
            foreach (var field in fields)
            {
                var at = $"{where}.{field.Name}";
                if (!IsReachable(field, at))
                {
                    continue;
                }

                Expression(field.Condition, NifStrictExpressionKind.Condition, "cond", declared, hasArgument, at);
                Expression(field.Length, NifStrictExpressionKind.Value, "length", declared, hasArgument, at);
                Expression(field.Width, NifStrictExpressionKind.Value, "width", declared, hasArgument, at);
                Expression(field.Arg, NifStrictExpressionKind.Value, "arg", declared, hasArgument, at);

                var fieldTemplate = field.Template == "#T#" ? template : field.Template;
                if (field.Template == "#T#" && template is null)
                {
                    problems.Add($"{at}: forwards #T# but no template reaches it");
                }

                var typeName = field.Type == "#T#" ? template : field.Type;
                if (typeName is null)
                {
                    problems.Add($"{at}: type #T# but no template reaches it");
                    continue;
                }

                Type(typeName, fieldTemplate, field.Arg is not null, declared, at);
            }
        }

        private void Type(
            string typeName,
            string? fieldTemplate,
            bool passesArgument,
            IReadOnlyList<HashSet<string>> declared,
            string at)
        {
            if (schema.BasicTypes.TryGetValue(typeName, out var basic))
            {
                if (basic.Name is "Ref" or "Ptr" && fieldTemplate is not null && schema.GetObject(fieldTemplate) is null)
                {
                    problems.Add($"{at}: {basic.Name} template '{fieldTemplate}' is not a block type");
                }

                return;
            }

            if (schema.Enums.ContainsKey(typeName))
            {
                return;
            }

            if (!schema.Structs.TryGetValue(typeName, out var structDef))
            {
                problems.Add($"{at}: type '{typeName}' is not a basic, enum or struct type");
                return;
            }

            if (_visited.Add($"{typeName}|{fieldTemplate}|{passesArgument}"))
            {
                Fields(structDef.Fields, [.. declared, Names(structDef.Fields)], passesArgument, fieldTemplate,
                    $"{at}<{typeName}>");
            }
        }

        private bool IsReachable(NifFieldDef field, string at)
        {
            if (field.OnlyT is { } onlyT)
            {
                var known = IsBlockType(onlyT, at, "onlyT");
                if (!known || !schema.Inherits(blockType, onlyT))
                {
                    return false;
                }
            }

            if (field.ExcludeT is { } excludeT && IsBlockType(excludeT, at, "excludeT") &&
                schema.Inherits(blockType, excludeT))
            {
                return false;
            }

            if (!InVersionRange(field.Since, at, "since", bound => version.Version >= bound) ||
                !InVersionRange(field.Until, at, "until", bound => version.Version <= bound))
            {
                return false;
            }

            if (field.VersionCond is not { } versionCondition)
            {
                return true;
            }

            foreach (var token in NifVersionExpr.GatherTokens(versionCondition))
            {
                if (!VersionTokens.Contains(token) && !NifVersionExpr.IsKnownMacro(token))
                {
                    problems.Add($"{at}: vercond uses {token}, which NifVersionExpr does not know");
                }
            }

            checkedAt.Add($"{at} vercond");
            if (!NifStrictExpressions.TryGetVersionCondition(versionCondition, out var evaluator, out var error))
            {
                problems.Add($"{at}: vercond '{versionCondition}' does not compile strictly: {error}");
                return false;
            }

            return evaluator(version);
        }

        private bool IsBlockType(string name, string at, string attribute)
        {
            if (schema.GetObject(name) is not null)
            {
                return true;
            }

            problems.Add($"{at}: {attribute} names '{name}', which is not a block type");
            return false;
        }

        private bool InVersionRange(string? text, string at, string attribute, Func<uint, bool> inRange)
        {
            if (text is null)
            {
                return true;
            }

            if (NifStrictExpressions.TryParseVersion(text, out var bound))
            {
                return inRange(bound);
            }

            problems.Add($"{at}: {attribute}=\"{text}\" is not a version");
            return false;
        }

        private void Expression(
            string? text,
            NifStrictExpressionKind kind,
            string attribute,
            IReadOnlyList<HashSet<string>> declared,
            bool hasArgument,
            string at)
        {
            if (text is null)
            {
                return;
            }

            checkedAt.Add($"{at} {attribute}");
            NifStrictExpression? compiled;
            string? error;
            var compiledOk = kind == NifStrictExpressionKind.Condition
                ? NifStrictExpressions.TryGetCondition(text, out compiled, out error)
                : NifStrictExpressions.TryGetValue(text, out compiled, out error);
            if (!compiledOk || compiled is null)
            {
                problems.Add($"{at}: {attribute} '{text}' does not compile strictly: {error}");
                return;
            }

            foreach (var name in compiled.ReferencedNames)
            {
                if (name == NifStrictExpression.ArgumentToken)
                {
                    if (!hasArgument)
                    {
                        problems.Add($"{at}: {attribute} '{text}' uses #ARG# but no argument is passed here");
                    }

                    continue;
                }

                if (!declared.Any(names => names.Contains(name)))
                {
                    problems.Add($"{at}: {attribute} '{text}' names '{name}', which no enclosing definition declares");
                }
            }
        }
    }
}
