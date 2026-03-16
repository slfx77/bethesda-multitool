namespace FalloutXbox360Utils.Core.Formats.SaveGame;

/// <summary>
///     Decodes ExtraDataList_v2 entries (~40 type codes).
///     Simple types are decoded inline; complex/list types delegate to ExtraDataTypeHandlers.
/// </summary>
internal static class ExtraDataDecoder
{
    /// <summary>
    ///     Decodes an ExtraDataList_v2 block: vsval count + (type byte + type-specific payload) per entry.
    ///     Type categories from Ghidra decompilation of ExtraDataList::SaveGame_v2.
    /// </summary>
    internal static void DecodeExtraDataList(ref FormDataReader r, DecodedFormData result, string name)
    {
        var startPos = r.Position;
        if (!r.HasData(1))
        {
            return;
        }

        var count = r.ReadVsval();
        r.TrySkipPipe();
        var entries = new List<DecodedField>();
        var aborted = false;

        for (var i = 0; i < count && r.HasData(1); i++)
        {
            var entryStart = r.Position;
            var type = r.ReadByte();
            r.TrySkipPipe();
            var displayValue = "";

            switch (type)
            {
                // No-data types (just the type byte)
                case 0x16 or 0x1F or 0x3E or 0x90 or 0x91:
                    displayValue = ExtraDataTypeHandlers.ExtraTypeName(type);
                    break;

                // Single RefID types
                case 0x1C or 0x21 or 0x22 or 0x39 or 0x3C or 0x3F or 0x46 or 0x49
                    or 0x55 or 0x6C or 0x74 or 0x89:
                {
                    if (!r.HasData(3))
                    {
                        aborted = true;
                        break;
                    }

                    var refId = r.ReadRefId();
                    r.TrySkipPipe();
                    displayValue = $"{ExtraDataTypeHandlers.ExtraTypeName(type)}: {refId}";
                    break;
                }

                // Single uint32 types (LE via SaveDataEndian)
                case 0x1E or 0x23 or 0x25 or 0x27 or 0x28 or 0x30
                    or 0x56 or 0x5C or 0x5D:
                {
                    if (!r.HasData(4))
                    {
                        aborted = true;
                        break;
                    }

                    var val = r.ReadUInt32();
                    r.TrySkipPipe();
                    displayValue = $"{ExtraDataTypeHandlers.ExtraTypeName(type)}: 0x{val:X8}";
                    break;
                }

                // Single byte types
                case 0x26 or 0x4A or 0x4E or 0x8D:
                {
                    if (!r.HasData(1))
                    {
                        aborted = true;
                        break;
                    }

                    var val = r.ReadByte();
                    r.TrySkipPipe();
                    displayValue = $"{ExtraDataTypeHandlers.ExtraTypeName(type)}: 0x{val:X2}";
                    break;
                }

                // Single uint16 type
                case 0x24:
                {
                    if (!r.HasData(2))
                    {
                        aborted = true;
                        break;
                    }

                    var val = r.ReadUInt16();
                    r.TrySkipPipe();
                    displayValue = $"{ExtraDataTypeHandlers.ExtraTypeName(type)}: {val}";
                    break;
                }

                // Two uint32 values
                case 0x92:
                {
                    if (!r.HasData(8))
                    {
                        aborted = true;
                        break;
                    }

                    var val1 = r.ReadUInt32();
                    r.TrySkipPipe();
                    var val2 = r.ReadUInt32();
                    r.TrySkipPipe();
                    displayValue = $"{ExtraDataTypeHandlers.ExtraTypeName(type)}: 0x{val1:X8}, 0x{val2:X8}";
                    break;
                }

                // Single uint32 (standalone cases)
                case 0x54:
                {
                    if (!r.HasData(4))
                    {
                        aborted = true;
                        break;
                    }

                    var val = r.ReadUInt32();
                    r.TrySkipPipe();
                    displayValue = $"Extra0x54: 0x{val:X8}";
                    break;
                }

                case 0x60:
                {
                    if (!r.HasData(4))
                    {
                        aborted = true;
                        break;
                    }

                    var val = r.ReadUInt32();
                    r.TrySkipPipe();
                    displayValue = $"EditorRef: 0x{val:X8}";
                    break;
                }

                // Single byte (activate ref children)
                case 0x2C:
                {
                    if (!r.HasData(1))
                    {
                        aborted = true;
                        break;
                    }

                    var val = r.ReadByte();
                    r.TrySkipPipe();
                    displayValue = $"ActivateRefChildren: {val}";
                    break;
                }

                // ── Multi-field structured types (delegated) ──

                case 0x4D:
                    displayValue = ExtraDataTypeHandlers.DecodeLockData(ref r) ?? "";
                    aborted = displayValue == "";
                    break;

                case 0x18:
                    displayValue = ExtraDataTypeHandlers.DecodeOwnership(ref r) ?? "";
                    aborted = displayValue == "";
                    break;

                case 0x19:
                    displayValue = ExtraDataTypeHandlers.DecodeRank(ref r) ?? "";
                    aborted = displayValue == "";
                    break;

                case 0x2A:
                    displayValue = ExtraDataTypeHandlers.DecodeRefPointerList(ref r) ?? "";
                    aborted = displayValue == "";
                    break;

                case 0x2F:
                    displayValue = ExtraDataTypeHandlers.DecodeUInt32Byte(ref r) ?? "";
                    aborted = displayValue == "";
                    break;

                case 0x50:
                    displayValue = ExtraDataTypeHandlers.DecodeTwoBytes(ref r) ?? "";
                    aborted = displayValue == "";
                    break;

                case 0x6E:
                    displayValue = ExtraDataTypeHandlers.DecodeRefIdUInt32(ref r, "Extra0x6E") ?? "";
                    aborted = displayValue == "";
                    break;

                case 0x75:
                    displayValue = ExtraDataTypeHandlers.DecodeRefIdRefIdByte(ref r) ?? "";
                    aborted = displayValue == "";
                    break;

                case 0x8F:
                    displayValue = ExtraDataTypeHandlers.DecodeTwoStrings(ref r) ?? "";
                    aborted = displayValue == "";
                    break;

                case 0x32:
                    displayValue = ExtraDataTypeHandlers.DecodeThreeRefIds(ref r) ?? "";
                    aborted = displayValue == "";
                    break;

                case 0x5B:
                    displayValue = ExtraDataTypeHandlers.DecodeRefIdUInt32(ref r, "Extra0x5B") ?? "";
                    aborted = displayValue == "";
                    break;

                // ── List types (delegated) ──

                case 0x1B:
                    displayValue = ExtraDataTypeHandlers.DecodeContainerChanges(ref r) ?? "";
                    aborted = displayValue == "";
                    break;

                case 0x1D:
                    displayValue = ExtraDataTypeHandlers.DecodeLevCreature(ref r) ?? "";
                    aborted = displayValue == "";
                    break;

                case 0x5E:
                    displayValue = ExtraDataTypeHandlers.DecodeRefIdBytePairList(ref r) ?? "";
                    aborted = displayValue == "";
                    break;

                case 0x35:
                    displayValue = ExtraDataTypeHandlers.DecodeUInt32PairList(ref r) ?? "";
                    aborted = displayValue == "";
                    break;

                case 0x73:
                    displayValue = ExtraDataTypeHandlers.DecodeRefIdUInt32UInt32List(ref r) ?? "";
                    aborted = displayValue == "";
                    break;

                case 0x7C:
                    displayValue = ExtraDataTypeHandlers.DecodeOwnerFormIds(ref r) ?? "";
                    aborted = displayValue == "";
                    break;

                // ── Complex structured types (delegated) ──

                case 0x5F:
                    displayValue = ExtraDataTypeHandlers.DecodeAnimNotes(ref r) ?? "";
                    aborted = displayValue == "";
                    break;

                case 0x0D:
                    displayValue = ExtraDataTypeHandlers.DecodeActivateRef(ref r) ?? "";
                    aborted = displayValue == "";
                    break;

                case 0x2E:
                {
                    var (display, shouldAbort) = ExtraDataTypeHandlers.DecodeMagicCaster(ref r);
                    displayValue = display;
                    aborted = shouldAbort;
                    break;
                }

                // ── Partial-decode types (always abort after prefix) ──

                case 0x33:
                    displayValue = ExtraDataTypeHandlers.DecodePackagePartial(ref r) ?? "";
                    aborted = true;
                    break;

                case 0x1A:
                    displayValue = ExtraDataTypeHandlers.DecodeActionPartial(ref r) ?? "";
                    aborted = true;
                    break;

                case 0x70:
                    displayValue = ExtraDataTypeHandlers.DecodeBoundBodyPartial(ref r) ?? "";
                    aborted = true;
                    break;

                // Known-complex types: start with opaque sub-function calls, no decodable prefix
                case 0x2B or 0x45 or 0x8B:
                    displayValue = $"Known complex type: {ExtraDataTypeHandlers.ExtraTypeName(type)} — aborting";
                    aborted = true;
                    break;

                default:
                    // Unknown type — can't determine size, abort per-entry parsing
                    displayValue = $"Unknown type 0x{type:X2} — aborting ExtraDataList decode";
                    aborted = true;
                    break;
            }

            if (aborted)
            {
                // Rewind to before this entry's type byte and stop
                r.Seek(entryStart);
                break;
            }

            entries.Add(new DecodedField
            {
                Name = $"Extra[{i}]",
                DisplayValue = displayValue,
                DataOffset = entryStart,
                DataLength = r.Position - entryStart
            });
        }

        // If we aborted mid-list, consume remaining bytes as a partial blob
        var blobStart = r.Position;
        if (aborted && r.Remaining > 0)
        {
            // Don't consume ALL remaining — this blob is just the rest of THIS ExtraDataList.
            // We can't know the exact boundary, so consume remaining as a blob.
            // This is still better than the old AddRawBlobField which ate EVERYTHING.
            var blobSize = r.Remaining;
            var blobData = r.ReadBytes(blobSize);
            entries.Add(new DecodedField
            {
                Name = "ExtraDataList_Remainder",
                Value = blobData,
                DisplayValue = $"Unparsed remainder ({blobSize} bytes)",
                DataOffset = blobStart,
                DataLength = blobSize
            });
        }

        result.Fields.Add(new DecodedField
        {
            Name = name,
            DisplayValue = $"{count} extra(s){(aborted ? " (partial)" : "")}",
            DataOffset = startPos,
            DataLength = r.Position - startPos,
            Children = entries.Count > 0 ? entries : null
        });
    }
}
