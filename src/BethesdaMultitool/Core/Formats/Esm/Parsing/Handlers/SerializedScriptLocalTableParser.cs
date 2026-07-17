using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;

/// <summary>
///     Parses the serialized local-variable table as strict adjacent SLSD/SCVR pairs.
///     Malformed declarations are never converted into <see cref="ScriptVariableInfo" />
///     entries; callers use <see cref="IsMalformed" /> to reject the executable bundle.
/// </summary>
internal sealed class SerializedScriptLocalTableParser(List<ScriptVariableInfo> variables)
{
    private readonly HashSet<uint> _seenVariableIds = [];
    private uint? _pendingVariableId;
    private byte _pendingVariableType;

    internal bool IsMalformed { get; private set; }

    /// <summary>
    ///     Observes every subrecord in the owning script block so that an SLSD is accepted
    ///     only when the immediately following subrecord is its SCVR name.
    /// </summary>
    internal void ObserveSubrecord(
        string signature,
        ReadOnlySpan<byte> data,
        bool isBigEndian)
    {
        if (_pendingVariableId.HasValue && signature != "SCVR")
        {
            RejectPendingDeclaration();
        }

        switch (signature)
        {
            case "SLSD":
                ReadDeclaration(data, isBigEndian);
                break;
            case "SCVR":
                ReadName(data);
                break;
        }
    }

    /// <summary>Rejects an SLSD left without an immediately adjacent SCVR at block end.</summary>
    internal void Complete()
    {
        if (_pendingVariableId.HasValue)
        {
            RejectPendingDeclaration();
        }
    }

    private void ReadDeclaration(ReadOnlySpan<byte> data, bool isBigEndian)
    {
        if (data.Length < ScriptLocalVariableLayout.SerializedSize)
        {
            IsMalformed = true;
            return;
        }

        var variableId = isBigEndian
            ? BinaryPrimitives.ReadUInt32BigEndian(data)
            : BinaryPrimitives.ReadUInt32LittleEndian(data);
        var rawType = data[ScriptLocalVariableLayout.IsIntegerOffset];
        if (variableId == 0 || rawType > 1 || !_seenVariableIds.Add(variableId))
        {
            IsMalformed = true;
            return;
        }

        _pendingVariableId = variableId;
        _pendingVariableType = rawType;
    }

    private void ReadName(ReadOnlySpan<byte> data)
    {
        if (!_pendingVariableId.HasValue)
        {
            IsMalformed = true;
            return;
        }

        var name = EsmStringUtils.ReadNullTermString(data);
        if (string.IsNullOrWhiteSpace(name))
        {
            RejectPendingDeclaration();
            return;
        }

        variables.Add(new ScriptVariableInfo(
            _pendingVariableId.Value,
            name,
            _pendingVariableType));
        _pendingVariableId = null;
        _pendingVariableType = 0;
    }

    private void RejectPendingDeclaration()
    {
        IsMalformed = true;
        _pendingVariableId = null;
        _pendingVariableType = 0;
    }
}
