using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;

namespace BethesdaMultitool.Core.Formats.Esm.Runtime;

/// <summary>
///     The three payloads a runtime record can carry behind a container or an indirection, read
///     independently of whichever reader produces that record's typed model.
///     <para>
///         They are grouped because they share one property: each lives on an <b>engine base
///         class</b> (<c>TESModel</c>, <c>TESModelTextureSwap</c>, <c>BGSDestructibleObjectForm</c>)
///         rather than on any one record type, so their presence follows C++ inheritance and cuts
///         clean across the reader split. 43, 28 and 26 record types respectively carry them, and
///         roughly half of each set is routed to a hand-written specialized reader.
///     </para>
/// </summary>
public sealed record RuntimeNestedPayloads(
    RuntimeTextureHashList? TextureHashes,
    IReadOnlyList<AlternateTextureEntry>? AlternateTextures,
    DestructionData? Destruction);
