using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Reference;
using BethesdaMultitool.Core.Formats.Esm.Subrecords;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Esm.Analysis.ScriptDiagnostics;

/// <summary>
///     Decides what the leading value of a diagnostics subrecord IS before anything prints or links
///     it as a FormID. The interesting-subrecord summary, the related-record scan and the provenance
///     state trace all read through here, so the three cannot drift apart again.
///     <para>
///         Two families are handled. PLDT/PLD2/PTDT/PTD2 are package unions: a one-byte type plus
///         three pad bytes (unswapped on Xbox 360, so the type is <c>data[0]</c> in both byte orders),
///         then a union the type selects and a radius or count, read in the subrecord's byte order by
///         <see cref="AiRecordHandler" />. Which arms hold a FormID comes from
///         <see cref="PackageReferenceIntegrity" />; an object-type arm is an enum and prints as a
///         number, never through the label index (18 is not HorseMarker). They decode for Oblivion,
///         Fallout 3 and New Vegas only; anything else, short, non-zero-padded or of an unknown type
///         prints raw.
///     </para>
///     <para>
///         The rest follow a per-(record type, signature) value-kind policy taken from the generated
///         FNV schema (<c>FalloutNvSchema.g.cs</c>) and xEdit: SCRV is a local-variable index, DIAL PNAM
///         a priority float, CREA TNAM a float, MESG TNAM a display time, and NOTE TNAM a topic only on
///         a voice note (DATA == 3). ANAM, PNAM, SNAM, TNAM, CNAM and INAM are FormIDs only on the
///         record types listed below; elsewhere they fail closed to raw, unlabelled output.
///     </para>
/// </summary>
internal static class EsmScriptSubrecordSummaryFormatter
{
    /// <summary>What the leading value of a subrecord is, under the per-(record, signature) policy.</summary>
    internal enum ValueKind
    {
        /// <summary>Not covered by the policy; callers keep their previous behaviour.</summary>
        Unclassified,

        /// <summary>A FormID at offset 0.</summary>
        FormId,

        /// <summary>A script local-variable index (SCRV).</summary>
        LocalVariable,

        /// <summary>A topic priority float (DIAL PNAM).</summary>
        Priority,

        /// <summary>A float scalar.</summary>
        Float,

        /// <summary>An unsigned 32-bit scalar.</summary>
        UInt32,

        /// <summary>Text.</summary>
        Text,

        /// <summary>Known not to be a FormID here, with no decoder: printed as bytes, never labelled.</summary>
        Raw,

        /// <summary>A PLDT/PLD2 package location union.</summary>
        PackageLocation,

        /// <summary>A PTDT/PTD2 package target union.</summary>
        PackageTarget
    }

    /// <summary>Which union arm a package location/target type selects.</summary>
    internal enum PackageUnionArm
    {
        /// <summary>The type carries no value (the four bytes are unused).</summary>
        None,

        /// <summary>The union holds a FormID.</summary>
        FormId,

        /// <summary>The union holds an object-type enum value.</summary>
        ObjectType
    }

    private const int TextPreviewLength = 40;
    private const int RawPreviewBytes = 16;

    private static readonly HashSet<string> SummarySignatures = new(StringComparer.Ordinal)
    {
        "NAME", "ANAM", "SNAM", "TPIC", "QSTI", "PNAM", "TCLT", "TCLF", "TCFU", "PKID", "SCRI", "SCRO",
        "SCRV", "TNAM", "PLDT", "PTDT", "PLD2", "PTD2"
    };

    private static readonly HashSet<string> AnamFormIdRecordTypes = new(StringComparer.Ordinal)
    {
        "INFO", "DOOR", "IDLE", "CPTH"
    };

    private static readonly HashSet<string> PnamFormIdRecordTypes = new(StringComparer.Ordinal)
    {
        "INFO", "NPC_", "CREA", "TERM"
    };

    private static readonly HashSet<string> TnamFormIdRecordTypes = new(StringComparer.Ordinal)
    {
        "PACK", "TERM", "ARMO", "LTEX", "RGDL", "WEAP", "DIAL"
    };

    private static readonly HashSet<string> CnamFormIdRecordTypes = new(StringComparer.Ordinal)
    {
        "CREA", "NPC_", "PACK", "WRLD"
    };

    private static readonly HashSet<string> InamFormIdRecordTypes = new(StringComparer.Ordinal)
    {
        "ACTI", "CREA", "EXPL", "MESG", "MSET", "NPC_", "PACK", "TACT", "TERM", "WEAP", "WRLD"
    };

    /// <summary>Classifies a subrecord's leading value for its record type.</summary>
    internal static ValueKind Classify(ParsedMainRecord record, ParsedSubrecord sub)
    {
        var recordType = record.Header.Signature;
        return sub.Signature switch
        {
            "PLDT" or "PLD2" => ValueKind.PackageLocation,
            "PTDT" or "PTD2" => ValueKind.PackageTarget,
            "SCRV" => ValueKind.LocalVariable,
            "NAME" or "TPIC" or "QSTI" or "TCLT" or "TCLF" or "TCFU" or "PKID" or "SCRI" or "SCRO" =>
                ValueKind.FormId,
            "EDID" or "FULL" or "SCTX" or "SCVR" => ValueKind.Text,
            "ANAM" => ClassifyAnam(recordType),
            "PNAM" => ClassifyPnam(recordType),
            "SNAM" => ClassifySnam(recordType),
            "TNAM" => ClassifyTnam(record),
            "CNAM" => ClassifyCnam(recordType),
            "INAM" => ClassifyInam(recordType),
            "PKE2" when recordType == "PACK" => ValueKind.UInt32,
            "PKFD" or "IDLT" when recordType == "PACK" => ValueKind.Float,
            _ => ValueKind.Unclassified
        };
    }

    /// <summary>
    ///     Formats one summary token for a subrecord the interesting-subrecord summary covers.
    ///     <paramref name="labelSuffix" /> is applied to true FormIDs only.
    /// </summary>
    /// <returns>False when the signature is not part of the summary.</returns>
    internal static bool TryFormatSummaryToken(
        ParsedMainRecord record,
        ParsedSubrecord sub,
        BethesdaGame game,
        Func<uint, string> labelSuffix,
        out string token)
    {
        if (!SummarySignatures.Contains(sub.Signature))
        {
            token = string.Empty;
            return false;
        }

        token = Classify(record, sub) switch
        {
            ValueKind.PackageLocation or ValueKind.PackageTarget => FormatPackageToken(record, sub, game, labelSuffix),
            ValueKind.FormId => FormatFormIdToken(sub, labelSuffix),
            ValueKind.LocalVariable => $"{sub.Signature}=local#{sub.DataAsFormId.ToString(CultureInfo.InvariantCulture)}",
            ValueKind.Priority => $"{sub.Signature}=priority:{FormatFloat(sub.DataAsFloat)}",
            ValueKind.Float => $"{sub.Signature}=float:{FormatFloat(sub.DataAsFloat)}",
            ValueKind.UInt32 => $"{sub.Signature}=u32:{sub.DataAsFormId.ToString(CultureInfo.InvariantCulture)}",
            ValueKind.Text => $"{sub.Signature}=text:{PreviewText(sub.DataAsString)}",
            _ => $"{sub.Signature}=raw:{PreviewBytes(sub.Data)}"
        };
        return true;
    }

    /// <summary>
    ///     The FormID a subrecord links to, for the provenance state trace: the decoded FormID arm of a
    ///     package union, or the leading value of a subrecord the policy calls a FormID. Object-type and
    ///     unused arms, local-variable indexes, scalars and text never link.
    /// </summary>
    /// <param name="detail">An unlabelled description of the linked slot.</param>
    /// <returns>True only for a non-zero FormID.</returns>
    internal static bool TryGetLinkedFormId(
        ParsedMainRecord record,
        ParsedSubrecord sub,
        BethesdaGame game,
        out uint formId,
        out string detail)
    {
        switch (Classify(record, sub))
        {
            case ValueKind.PackageLocation or ValueKind.PackageTarget
                when TryDecodePackageUnion(record.Header.Signature, sub, game, out var union, out _)
                     && union.Arm == PackageUnionArm.FormId
                     && union.Value != 0:
                formId = union.Value;
                detail = FormatPackageUnion(union, _ => string.Empty);
                return true;
            case ValueKind.FormId when sub.Data.Length >= 4 && sub.DataAsFormId != 0:
                formId = sub.DataAsFormId;
                detail = $"{sub.Signature}=0x{formId:X8}";
                return true;
            default:
                formId = 0;
                detail = string.Empty;
                return false;
        }
    }

    /// <summary>
    ///     True when the subrecord's first DWORD is known NOT to be a FormID (a local-variable index,
    ///     a scalar, text or a package-union type). Used to drop false matches from the related-record
    ///     scan without dropping anything the policy does not cover.
    /// </summary>
    internal static bool FirstDwordIsKnownNonFormId(ParsedMainRecord record, ParsedSubrecord sub)
    {
        return Classify(record, sub) is ValueKind.LocalVariable or ValueKind.Priority or ValueKind.Float
            or ValueKind.UInt32 or ValueKind.Text or ValueKind.PackageLocation or ValueKind.PackageTarget;
    }

    /// <summary>
    ///     Decodes a PLDT/PLD2/PTDT/PTD2 union. Fails, with a <paramref name="status" /> naming why, for
    ///     games other than Oblivion/Fallout 3/New Vegas, records other than PACK, data shorter than 12
    ///     bytes or of an unexpected length (PLDT 12, PTDT 12 or 16), non-zero pad bytes, and types
    ///     outside the game's enum (location 0-5 in Oblivion and 0-7 later, target 0-2 and 0-3).
    /// </summary>
    internal static bool TryDecodePackageUnion(
        string recordType,
        ParsedSubrecord sub,
        BethesdaGame game,
        [NotNullWhen(true)] out PackageUnion? union,
        out string status)
    {
        union = null;
        var isLocation = sub.Signature is "PLDT" or "PLD2";
        if (!isLocation && sub.Signature is not ("PTDT" or "PTD2"))
        {
            status = "not_a_package_union";
            return false;
        }

        if (game is not (BethesdaGame.Oblivion or BethesdaGame.Fallout3 or BethesdaGame.FalloutNewVegas))
        {
            status = "unsupported_game";
            return false;
        }

        if (!string.Equals(recordType, "PACK", StringComparison.Ordinal))
        {
            status = "unsupported_record";
            return false;
        }

        var data = sub.Data;
        if (data.Length < 12)
        {
            status = "short";
            return false;
        }

        if (isLocation ? data.Length != 12 : data.Length is not (12 or 16))
        {
            status = "unsupported_length";
            return false;
        }

        if (data[1] != 0 || data[2] != 0 || data[3] != 0)
        {
            status = "nonzero_pad";
            return false;
        }

        union = isLocation
            ? DecodeLocation(sub.Signature, data, sub.BigEndian, game)
            : DecodeTarget(sub.Signature, data, sub.BigEndian, game);
        status = union is null ? "unknown_type" : "decoded";
        return union is not null;
    }

    /// <summary>
    ///     Formats a decoded union, e.g. <c>PLDT(type=1:InCell,cell=0x0009A285 (EDID),radius=0)</c> or
    ///     <c>PTDT(type=2:ObjectType,objectType=18,count=1,float12=0)</c>.
    /// </summary>
    internal static string FormatPackageUnion(PackageUnion union, Func<uint, string> labelSuffix)
    {
        var sb = new StringBuilder();
        sb.Append(union.Signature)
            .Append("(type=")
            .Append(union.Type.ToString(CultureInfo.InvariantCulture))
            .Append(':')
            .Append(union.TypeToken);

        switch (union.Arm)
        {
            case PackageUnionArm.FormId:
                sb.Append(',')
                    .Append(union.ArmName)
                    .Append("=0x")
                    .Append(union.Value.ToString("X8", CultureInfo.InvariantCulture))
                    .Append(labelSuffix(union.Value));
                break;
            case PackageUnionArm.ObjectType:
                sb.Append(",objectType=").Append(union.Value.ToString(CultureInfo.InvariantCulture));
                break;
            default:
                if (union.Value != 0)
                {
                    sb.Append(",unused=0x").Append(union.Value.ToString("X8", CultureInfo.InvariantCulture));
                }

                break;
        }

        sb.Append(',')
            .Append(union.NumberName)
            .Append('=')
            .Append(union.Number.ToString(CultureInfo.InvariantCulture));
        if (union.TrailingFloat is { } trailing)
        {
            sb.Append(",float12=").Append(FormatFloat(trailing));
        }

        return sb.Append(')').ToString();
    }

    private static PackageUnion? DecodeLocation(string signature, byte[] data, bool bigEndian, BethesdaGame game)
    {
        var location = AiRecordHandler.ParsePackageLocation(data, bigEndian);
        var maxType = game == BethesdaGame.Oblivion ? 5 : 7;
        if (location.Type > maxType)
        {
            return null;
        }

        PackageUnionArm arm;
        string armName;
        if (PackageReferenceIntegrity.LocationTypeIsFormId(location.Type))
        {
            arm = PackageUnionArm.FormId;
            armName = location.Type switch
            {
                0 => "ref",
                1 => "cell",
                _ => "object"
            };
        }
        else if (location.Type == 5)
        {
            arm = PackageUnionArm.ObjectType;
            armName = "objectType";
        }
        else
        {
            arm = PackageUnionArm.None;
            armName = "unused";
        }

        return new PackageUnion(
            signature,
            location.Type,
            ToTypeToken(location.TypeName),
            arm,
            armName,
            location.Union,
            "radius",
            location.Radius,
            null);
    }

    private static PackageUnion? DecodeTarget(string signature, byte[] data, bool bigEndian, BethesdaGame game)
    {
        var target = AiRecordHandler.ParsePackageTarget(data, bigEndian);
        var maxType = game == BethesdaGame.Oblivion ? 2 : 3;
        if (target.Type > maxType)
        {
            return null;
        }

        PackageUnionArm arm;
        string armName;
        if (PackageReferenceIntegrity.TargetTypeIsFormId(target.Type))
        {
            arm = PackageUnionArm.FormId;
            armName = target.Type == 0 ? "ref" : "object";
        }
        else if (target.Type == 2)
        {
            arm = PackageUnionArm.ObjectType;
            armName = "objectType";
        }
        else
        {
            arm = PackageUnionArm.None;
            armName = "unused";
        }

        return new PackageUnion(
            signature,
            target.Type,
            ToTypeToken(target.TypeName),
            arm,
            armName,
            target.FormIdOrType,
            "count",
            target.CountDistance,
            data.Length >= 16 ? target.AcquireRadius : null);
    }

    private static string FormatPackageToken(
        ParsedMainRecord record,
        ParsedSubrecord sub,
        BethesdaGame game,
        Func<uint, string> labelSuffix)
    {
        return TryDecodePackageUnion(record.Header.Signature, sub, game, out var union, out var status)
            ? FormatPackageUnion(union, labelSuffix)
            : FormatRawToken(sub, status);
    }

    private static string FormatFormIdToken(ParsedSubrecord sub, Func<uint, string> labelSuffix)
    {
        var value = sub.DataAsFormId;
        return value == 0
            ? sub.Signature
            : $"{sub.Signature}=0x{value:X8}{labelSuffix(value)}";
    }

    private static string FormatRawToken(ParsedSubrecord sub, string status)
    {
        return $"{sub.Signature}(length={sub.Data.Length.ToString(CultureInfo.InvariantCulture)}," +
               $"status={status},raw={Convert.ToHexString(sub.Data)})";
    }

    private static ValueKind ClassifyAnam(string recordType)
    {
        if (AnamFormIdRecordTypes.Contains(recordType))
        {
            return ValueKind.FormId;
        }

        return recordType switch
        {
            "ASPC" => ValueKind.UInt32,
            "AVIF" or "RGDL" => ValueKind.Text,
            "MSET" or "MUSC" => ValueKind.Float,
            _ => ValueKind.Raw
        };
    }

    private static ValueKind ClassifyPnam(string recordType)
    {
        if (PnamFormIdRecordTypes.Contains(recordType))
        {
            return ValueKind.FormId;
        }

        return recordType switch
        {
            "DIAL" => ValueKind.Priority,
            "RACE" => ValueKind.Float,
            _ => ValueKind.Raw
        };
    }

    private static ValueKind ClassifySnam(string recordType)
    {
        return recordType switch
        {
            "TES4" => ValueKind.Text,
            "LTEX" or "TREE" => ValueKind.Raw,
            _ => ValueKind.FormId
        };
    }

    private static ValueKind ClassifyTnam(ParsedMainRecord record)
    {
        var recordType = record.Header.Signature;
        if (TnamFormIdRecordTypes.Contains(recordType))
        {
            return ValueKind.FormId;
        }

        return recordType switch
        {
            // xEdit's wbNOTETNAMDecide: a topic only on a voice note, text otherwise.
            "NOTE" => IsVoiceNote(record) ? ValueKind.FormId : ValueKind.Text,
            "CREA" => ValueKind.Float,
            "MESG" => ValueKind.UInt32,
            _ => ValueKind.Raw
        };
    }

    private static ValueKind ClassifyCnam(string recordType)
    {
        if (CnamFormIdRecordTypes.Contains(recordType))
        {
            return ValueKind.FormId;
        }

        return recordType switch
        {
            "QUST" or "TES4" => ValueKind.Text,
            "FACT" or "MSET" => ValueKind.Float,
            _ => ValueKind.Raw
        };
    }

    private static ValueKind ClassifyInam(string recordType)
    {
        if (InamFormIdRecordTypes.Contains(recordType))
        {
            return ValueKind.FormId;
        }

        return recordType switch
        {
            "FACT" => ValueKind.Text,
            "ASPC" => ValueKind.UInt32,
            _ => ValueKind.Raw
        };
    }

    private static bool IsVoiceNote(ParsedMainRecord record)
    {
        var data = record.Subrecords.FirstOrDefault(s => s.Signature == "DATA" && s.Data.Length >= 1)?.Data;
        return data is not null && data[0] == 3;
    }

    private static string ToTypeToken(string typeName)
    {
        return typeName.Replace(" ", string.Empty, StringComparison.Ordinal);
    }

    private static string FormatFloat(float value)
    {
        return value.ToString(CultureInfo.InvariantCulture);
    }

    private static string PreviewText(string text)
    {
        var sb = new StringBuilder();
        foreach (var ch in text)
        {
            if (sb.Length >= TextPreviewLength)
            {
                sb.Append("...");
                break;
            }

            // The summary joins tokens with "; ", so a semicolon or line break inside the text would
            // read as a token boundary.
            sb.Append(char.IsControl(ch) || ch == ';' ? ' ' : ch);
        }

        return sb.ToString().Trim();
    }

    private static string PreviewBytes(byte[] data)
    {
        return data.Length <= RawPreviewBytes
            ? Convert.ToHexString(data)
            : Convert.ToHexString(data, 0, RawPreviewBytes) + "...";
    }

    /// <summary>A decoded PLDT/PLD2/PTDT/PTD2 union.</summary>
    /// <param name="Signature">The subrecord signature.</param>
    /// <param name="Type">The type byte (<c>data[0]</c>).</param>
    /// <param name="TypeToken">The type's name without spaces, e.g. <c>InCell</c>.</param>
    /// <param name="Arm">What the union holds for this type.</param>
    /// <param name="ArmName">The printed name of the union slot (<c>ref</c>, <c>cell</c>, <c>object</c>, ...).</param>
    /// <param name="Value">The union in the subrecord's byte order.</param>
    /// <param name="NumberName"><c>radius</c> for a location, <c>count</c> for a target.</param>
    /// <param name="Number">The radius or count/distance.</param>
    /// <param name="TrailingFloat">The PTDT/PTD2 float at +12, when the subrecord carries it.</param>
    internal sealed record PackageUnion(
        string Signature,
        byte Type,
        string TypeToken,
        PackageUnionArm Arm,
        string ArmName,
        uint Value,
        string NumberName,
        int Number,
        float? TrailingFloat);
}
