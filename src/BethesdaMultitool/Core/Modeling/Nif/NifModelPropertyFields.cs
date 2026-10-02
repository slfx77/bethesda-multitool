using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Decoding;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     Reads named fields out of a decoded property or texture block for the typed views. Views interpret decoded values;
///     they never compute offsets (plan section 1, "Typed views"). A field a typed view needs that did not decode, or
///     decoded with an unexpected shape, is corrupt input for a typed block and throws naming the block and field.
/// </summary>
internal static class NifModelPropertyFields
{
    /// <summary>
    ///     Requires a block the reader is about to type to have decoded exactly (plan section 1, self-check 1: a typed block
    ///     that does not consume exactly its Block Size is corrupt). Property blocks are decoded tolerantly because whether
    ///     they feed typed state is known only after the hierarchy is walked.
    /// </summary>
    /// <exception cref="InvalidDataException">The block's decode failed, met problems, or did not consume its size.</exception>
    public static void RequireComplete(NifDecodedBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);
        if (block.IsComplete)
        {
            return;
        }

        var detail = block.Failure?.Message ??
                     (block.Problems.Count > 0
                         ? block.Problems[0].Message
                         : $"consumed {block.ConsumedBytes} of {block.Size} bytes");
        throw new InvalidDataException(
            $"NIF block {block.Index} ({block.Type}) feeds a typed material but did not decode exactly: {detail}");
    }

    /// <summary>An integer field.</summary>
    public static NifIntegerValue Integer(NifDecodedBlock block, NifStructValue owner, string field)
    {
        return Get<NifIntegerValue>(block, owner, field);
    }

    /// <summary>An optional integer field (absent by version or condition).</summary>
    public static NifIntegerValue? OptionalInteger(NifStructValue owner, string field)
    {
        return owner.TryGet(field, out var value) ? value as NifIntegerValue : null;
    }

    /// <summary>A float field, exactly as stored (it may be non-finite).</summary>
    public static float Float(NifDecodedBlock block, NifStructValue owner, string field)
    {
        return Get<NifFloatValue>(block, owner, field).Value;
    }

    /// <summary>An optional float field (absent by version or condition).</summary>
    public static float? OptionalFloat(NifDecodedBlock block, NifStructValue owner, string field)
    {
        if (!owner.TryGet(field, out var value))
        {
            return null;
        }

        return value is NifFloatValue single
            ? single.Value
            : throw Shape(block, field, value, nameof(NifFloatValue));
    }

    /// <summary>A Color3 field (r, g, b), exactly as stored.</summary>
    public static Vector3 Color3(NifDecodedBlock block, NifStructValue owner, string field)
    {
        return OptionalColor3(block, owner, field) ?? throw Missing(block, field);
    }

    /// <summary>An optional Color3 field.</summary>
    public static Vector3? OptionalColor3(NifDecodedBlock block, NifStructValue owner, string field)
    {
        if (!owner.TryGet(field, out var value))
        {
            return null;
        }

        return value switch
        {
            NifStructValue color => new Vector3(Component(block, color, field, "r"), Component(block, color, field, "g"),
                Component(block, color, field, "b")),
            NifFloatArrayValue { ComponentsPerElement: 3, Count: 1 } bulk => new Vector3(bulk.Get(0), bulk.Get(0, 1),
                bulk.Get(0, 2)),
            _ => throw Shape(block, field, value, "Color3")
        };
    }

    /// <summary>A TexCoord field (u, v), exactly as stored.</summary>
    public static Vector2 TexCoord(NifDecodedBlock block, NifStructValue owner, string field)
    {
        if (!owner.TryGet(field, out var value))
        {
            throw Missing(block, field);
        }

        return value switch
        {
            NifStructValue coordinate => new Vector2(Component(block, coordinate, field, "u"),
                Component(block, coordinate, field, "v")),
            NifFloatArrayValue { ComponentsPerElement: 2, Count: 1 } bulk => new Vector2(bulk.Get(0), bulk.Get(0, 1)),
            _ => throw Shape(block, field, value, "TexCoord")
        };
    }

    /// <summary>A block link (-1 for none).</summary>
    public static int Ref(NifDecodedBlock block, NifStructValue owner, string field)
    {
        return Get<NifRefValue>(block, owner, field).Index;
    }

    /// <summary>A header-string field (string, FilePath).</summary>
    public static NifStringValue String(NifDecodedBlock block, NifStructValue owner, string field)
    {
        return Get<NifStringValue>(block, owner, field);
    }

    /// <summary>An inline SizedString field.</summary>
    public static NifSizedStringValue? OptionalSizedString(NifDecodedBlock block, NifStructValue owner, string field)
    {
        if (!owner.TryGet(field, out var value))
        {
            return null;
        }

        return value as NifSizedStringValue ?? throw Shape(block, field, value, nameof(NifSizedStringValue));
    }

    /// <summary>The block's NiObjectNET Name as display text, or null when it is none or unresolved.</summary>
    public static string? Name(NifDecodedBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);
        return block.Root.TryGet("Name", out var value) && value is NifStringValue { Text: { Length: > 0 } text }
            ? text
            : null;
    }

    private static T Get<T>(NifDecodedBlock block, NifStructValue owner, string field) where T : NifValue
    {
        ArgumentNullException.ThrowIfNull(block);
        ArgumentNullException.ThrowIfNull(owner);
        if (!owner.TryGet(field, out var value))
        {
            throw Missing(block, field);
        }

        return value as T ?? throw Shape(block, field, value, typeof(T).Name);
    }

    private static float Component(NifDecodedBlock block, NifStructValue owner, string field, string component)
    {
        return owner.TryGet(component, out var value) && value is NifFloatValue single
            ? single.Value
            : throw new InvalidDataException(
                $"NIF block {block.Index} ({block.Type}) has no float '{field}.{component}'.");
    }

    private static InvalidDataException Missing(NifDecodedBlock block, string field)
    {
        return new InvalidDataException(
            $"NIF block {block.Index} ({block.Type}) did not decode the field '{field}' a typed view needs.");
    }

    private static InvalidDataException Shape(NifDecodedBlock block, string field, NifValue value, string expected)
    {
        return new InvalidDataException(
            $"NIF block {block.Index} ({block.Type}) decoded '{field}' as {value.Kind}, not {expected}.");
    }
}
