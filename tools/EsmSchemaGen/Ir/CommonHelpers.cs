using EsmSchemaGen.Pascal;

namespace EsmSchemaGen.Ir;

/// <summary>
///     Hand-authored IR templates for the <c>wbDefinitionsCommon</c> helper <em>functions</em> — the
///     ones that build structure procedurally and so cannot be resolved by the <c>wb* := …</c> symbol
///     table (<see cref="IrBuilder.DefineSymbol" />). Each entry mirrors the byte layout the Pascal
///     function produces. The set grows as coverage demands; an unmapped helper falls through to
///     <see cref="UnknownMemberDef" /> and is reported, never silently dropped.
/// </summary>
public static class CommonHelpers
{
    /// <summary>Build the IR for a Common helper call, or null if the name is not a known helper.</summary>
    public static MemberDef? TryBuild(string name, IReadOnlyList<WbValue> args) => name.ToLowerInvariant() switch
    {
        // wbByteColors(aSignature?, aName='Color', R, G, B) — a 3-byte RGB struct (Common.pas:516-527).
        "wbbytecolors" => ByteColors(args),
        // wbVec3(aSignature?, aName) — three little-endian floats (X, Y, Z); ubiquitous across all games.
        "wbvec3" => Vec3(args),
        // wbOBND — the OBND Object Bounds subrecord: six int16 (X1,Y1,Z1,X2,Y2,Z2) (Common.pas:5654).
        "wbobnd" => Obnd(),
        // wbGenericModel — the per-game model RStruct (MODL/MODB/MODT/MODS/MODD) (FNV.pas:1080).
        "wbgenericmodel" => GenericModel(),
        // wbVec3PosRot — a position+rotation struct: six floats (X,Y,Z position then X,Y,Z rotation).
        "wbvec3posrot" => Vec3PosRot(args),
        // wbModelInfo(aSignature) — the MODT-style model-info subrecord: opaque texture-hash bytes.
        "wbmodelinfo" => ModelInfo(args),
        _ => null
    };

    private static StructDef Vec3PosRot(IReadOnlyList<WbValue> args)
    {
        return new StructDef([
            new FieldDef(PrimType.Float) { Name = "Position X" },
            new FieldDef(PrimType.Float) { Name = "Position Y" },
            new FieldDef(PrimType.Float) { Name = "Position Z" },
            new FieldDef(PrimType.Float) { Name = "Rotation X" },
            new FieldDef(PrimType.Float) { Name = "Rotation Y" },
            new FieldDef(PrimType.Float) { Name = "Rotation Z" }
        ])
        {
            Signature = SignatureArg(args),
            Name = NameArg(args) ?? "Position/Rotation"
        };
    }

    private static FieldDef ModelInfo(IReadOnlyList<WbValue> args)
        => new(PrimType.ByteArray) { Signature = SignatureArg(args), Name = "Model Info" };

    private static StructDef Obnd()
    {
        return new StructDef([
            new FieldDef(PrimType.S16) { Name = "X1" },
            new FieldDef(PrimType.S16) { Name = "Y1" },
            new FieldDef(PrimType.S16) { Name = "Z1" },
            new FieldDef(PrimType.S16) { Name = "X2" },
            new FieldDef(PrimType.S16) { Name = "Y2" },
            new FieldDef(PrimType.S16) { Name = "Z2" }
        ])
        {
            Signature = "OBND",
            Name = "Object Bounds"
        };
    }

    private static StructDef GenericModel()
    {
        return new StructDef([
            new FieldDef(PrimType.ZString) { Signature = "MODL", Name = "Model FileName" },
            new FieldDef(PrimType.ByteArray) { Signature = "MODB", Name = "Unknown" },
            new FieldDef(PrimType.ByteArray) { Signature = "MODT", Name = "Model Info" },
            new FieldDef(PrimType.ByteArray) { Signature = "MODS", Name = "Alternate Textures" },
            new FieldDef(PrimType.ByteArray) { Signature = "MODD", Name = "FaceGen Model Flags" }
        ])
        {
            Name = "Model"
        };
    }

    private static StructDef Vec3(IReadOnlyList<WbValue> args)
    {
        return new StructDef([
            new FieldDef(PrimType.Float) { Name = "X" },
            new FieldDef(PrimType.Float) { Name = "Y" },
            new FieldDef(PrimType.Float) { Name = "Z" }
        ])
        {
            Signature = SignatureArg(args),
            Name = NameArg(args) ?? "Vector"
        };
    }

    private static StructDef ByteColors(IReadOnlyList<WbValue> args)
    {
        return new StructDef([
            new FieldDef(PrimType.U8) { Name = "Red" },
            new FieldDef(PrimType.U8) { Name = "Green" },
            new FieldDef(PrimType.U8) { Name = "Blue" }
        ])
        {
            Signature = SignatureArg(args),
            Name = NameArg(args) ?? "Color"
        };
    }

    private static string? SignatureArg(IReadOnlyList<WbValue> args)
    {
        if (args.Count > 0 && args[0] is WbIdent id && !id.Name.StartsWith("it", StringComparison.Ordinal))
        {
            return id.Name == "NULL" ? "" : id.Name;
        }

        return null;
    }

    private static string? NameArg(IReadOnlyList<WbValue> args)
        => args.OfType<WbStr>().FirstOrDefault()?.Value;
}
