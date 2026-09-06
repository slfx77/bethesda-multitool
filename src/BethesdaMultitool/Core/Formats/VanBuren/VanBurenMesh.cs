using System.Buffers.Binary;
using System.Text;

namespace BethesdaMultitool.Core.Formats.VanBuren;

/// <summary>
///     A Van Buren <c>B3D</c> mesh — <b>identification only</b>. Original RE 2026-09-06; the only
///     reference is GPL and none of it is ported.
///     <para>
///         ⚠ <b>This does NOT decode geometry.</b> A B3D is a tagged OPCODE STREAM, not a struct,
///         and the opcode table is not established. What this reader does is name a mesh, which the
///         mesh browser needs before it can offer one — and which is worth having on its own,
///         because the container gives entries no names at all.
///     </para>
///     <para>
///         Every payload opens <c>"B3D 1.1 "</c> (8 bytes, 3,915/3,915). Byte <c>+8</c> is the first
///         opcode: 28 on 3,808 payloads, 3 on 104, 53 on 3.
///         ⚑ The naming rule: the byte pair <c>0A 0C</c> introduces a u16-length-prefixed node name.
///         <b>3,912 of the 3,915 carry exactly TWO such nodes, and in every one of them the first is
///         <c>"Scene Root"</c></b> — so the second is the mesh's own name. 3,589 names are distinct,
///         and they read as authored identifiers (<c>CR_Bat</c>, <c>CR_Cougar</c>,
///         <c>CR_DesertStalker</c>), which is the oracle that the walk is aligned.
///     </para>
///     <para>
///         ⚠ The 3 payloads without that shape all declare opcode <b>53</b> at +8 and all live in
///         <c>Critters.grp</c>. They are reported as unnamed rather than guessed at.
///     </para>
///     <para>
///         Observed opcode forms, for whoever finishes this: <c>03</c> and <c>04</c> take a float,
///         <c>05</c> and <c>07</c> a u16-length string, <c>0x16</c> a u32. A material block follows
///         <c>07</c> carrying bare u16 strings (texture name, colour, <c>OPAQUE</c>/<c>ALPHABLEND</c>,
///         a surface sound such as <c>SILENT</c> or <c>SAND</c>) around 64 bytes of floats.
///     </para>
/// </summary>
internal sealed class VanBurenMesh
{
    /// <summary>The 8-byte tag and version every payload opens with.</summary>
    public const string Signature = "B3D 1.1 ";

    /// <summary>The node-introducing byte pair.</summary>
    public const ushort NodeMarker = 0x0C0A;

    /// <summary>The name the first node always carries.</summary>
    public const string SceneRoot = "Scene Root";

    private VanBurenMesh(string name, byte firstOpcode, IReadOnlyList<string> nodes)
    {
        Name = name;
        FirstOpcode = firstOpcode;
        Nodes = nodes;
    }

    /// <summary>Source label, for messages.</summary>
    public string Name { get; }

    /// <summary>The opcode byte at +8: 28, 3 or 53 on retail.</summary>
    public byte FirstOpcode { get; }

    /// <summary>The node names found, in order. Normally <c>["Scene Root", &lt;mesh name&gt;]</c>.</summary>
    public IReadOnlyList<string> Nodes { get; }

    /// <summary>
    ///     The mesh's own name — the node after <c>Scene Root</c> — or null when the payload does
    ///     not have the two-node shape (3 of the 3,915 retail meshes).
    /// </summary>
    public string? MeshName =>
        Nodes.Count == 2 && string.Equals(Nodes[0], SceneRoot, StringComparison.Ordinal) ? Nodes[1] : null;

    /// <summary>Content probe: the signature.</summary>
    public static bool IsMesh(ReadOnlySpan<byte> bytes)
    {
        return bytes.Length >= 9 && bytes[..8].SequenceEqual(Encoding.ASCII.GetBytes(Signature));
    }

    /// <summary>Reads a mesh's identification, throwing when the payload is not one.</summary>
    public static VanBurenMesh Parse(ReadOnlySpan<byte> bytes, string name)
    {
        if (!TryParse(bytes, name, out var mesh, out var error))
        {
            throw new InvalidDataException(error);
        }

        return mesh;
    }

    /// <summary>Reads a mesh's identification, reporting why rather than throwing.</summary>
    public static bool TryParse(ReadOnlySpan<byte> bytes, string name, out VanBurenMesh mesh, out string error)
    {
        mesh = null!;
        if (!IsMesh(bytes))
        {
            error = $"{name}: does not open with the '{Signature}' signature.";
            return false;
        }

        mesh = new VanBurenMesh(name, bytes[8], ScanNodes(bytes));
        error = string.Empty;
        return true;
    }

    /// <summary>
    ///     Collects node names by scanning for the marker. This is a SCAN, not a walk of a known
    ///     grammar — the opcode table is not established, so it deliberately does not pretend to
    ///     traverse the stream.
    /// </summary>
    private static List<string> ScanNodes(ReadOnlySpan<byte> bytes)
    {
        var nodes = new List<string>(2);
        var i = 8;
        while (i + 4 < bytes.Length)
        {
            if (BinaryPrimitives.ReadUInt16LittleEndian(bytes[i..]) != NodeMarker)
            {
                i++;
                continue;
            }

            var length = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(i + 2)..]);
            if (length is 0 or > 128 || i + 4 + length > bytes.Length)
            {
                i++;
                continue;
            }

            var candidate = bytes.Slice(i + 4, length);
            if (!IsPrintable(candidate))
            {
                i++;
                continue;
            }

            nodes.Add(Encoding.ASCII.GetString(candidate));
            i += 4 + length;
        }

        return nodes;
    }

    private static bool IsPrintable(ReadOnlySpan<byte> bytes)
    {
        foreach (var b in bytes)
        {
            if (b is < 32 or >= 127)
            {
                return false;
            }
        }

        return true;
    }
}
