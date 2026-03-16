namespace FalloutXbox360Utils.Core.Formats.SaveGame;

/// <summary>
///     Decodes REFR (placed object), projectile, and initial data for references.
/// </summary>
internal static class RefrDecoder
{
    internal static void DecodeRefr(ref FormDataReader r, uint flags, DecodedFormData result, int initialDataType)
    {
        // Phase 1: Initial data (written by save infrastructure BEFORE SaveGame_v2).
        // REFR_MOVE (bit 1), REFR_HAVOK_MOVE (bit 2), REFR_CELL_CHANGED (bit 3)
        // are NOT handled by TESObjectREFR::SaveGame_v2 — they're prepended as
        // "initial data" by the save infrastructure (confirmed via Ghidra decompilation).
        DecodeRefrInitialData(ref r, flags, result, initialDataType);

        // Phase 2: Body data (written by TESObjectREFR::SaveGame_v2 call chain).
        // Order from Ghidra decompilation: FORM_FLAGS → SCALE → ExtraDataList → Inventory → Animation

        if (FormFieldWriter.HasFlag(flags, 0x00000001)) // FORM_FLAGS (TESForm::SaveGame)
        {
            FormFieldWriter.AddUInt32Field(ref r, result, "FORM_FLAGS");
        }

        if (FormFieldWriter.HasFlag(flags, 0x00000010)) // REFR_SCALE
        {
            FormFieldWriter.AddFloatField(ref r, result, "REFR_SCALE");
        }

        // ExtraDataList v2 — ExtraDataList::SaveGame_v2 is called ONCE when ANY
        // bit in the non-actor mask is set. All extra data entries are written as
        // a single ExtraDataList v2 block (vsval count + typed entries).
        // Non-actor mask 0xa4021c40: bits 6(OWNERSHIP), 10(ITEM_DATA), 11(AMMO),
        // 12(LOCK), 17(TELEPORT), 26(ACTIVATING_CHILDREN), 29(ENCOUNTER_ZONE), 31(GAME_ONLY)
        const uint nonActorExtraMask = 0xa4021c40;
        if ((flags & nonActorExtraMask) != 0)
        {
            ExtraDataDecoder.DecodeExtraDataList(ref r, result, "EXTRA_DATA");
        }

        // Inventory — InventoryChanges::SaveGame_v2 called ONCE when bit 5 or bit 27 is set.
        if ((flags & 0x08000020) != 0)
        {
            var name = FormFieldWriter.HasFlag(flags, 0x08000000) && !FormFieldWriter.HasFlag(flags, 0x00000020)
                ? "REFR_LEVELED_INVENTORY"
                : "REFR_INVENTORY";
            SharedFieldDecoder.DecodeInventory(ref r, result, name);
        }

        // Animation (bit 28, non-actor only — decompilation confirms !IsActor() gate)
        if (FormFieldWriter.HasFlag(flags, 0x10000000))
        {
            DecodeRefrAnimation(ref r, result);
        }

        // Zero-size flags — no data in save blob, just the change flag existence
        if (FormFieldWriter.HasFlag(flags, 0x00200000))
        {
            result.Fields.Add(new DecodedField
                { Name = "OBJECT_EMPTY", DisplayValue = "Container emptied", DataOffset = r.Position, DataLength = 0 });
        }

        if (FormFieldWriter.HasFlag(flags, 0x00400000))
        {
            result.Fields.Add(new DecodedField
            {
                Name = "OBJECT_OPEN_DEFAULT_STATE", DisplayValue = "Open by default", DataOffset = r.Position,
                DataLength = 0
            });
        }

        if (FormFieldWriter.HasFlag(flags, 0x00800000))
        {
            result.Fields.Add(new DecodedField
                { Name = "OBJECT_OPEN_STATE", DisplayValue = "Open state", DataOffset = r.Position, DataLength = 0 });
        }

        // CREATED_ONLY (bit 30) — separate ExtraDataList v2 block, NOT in the main mask
        if (FormFieldWriter.HasFlag(flags, 0x40000000))
        {
            ExtraDataDecoder.DecodeExtraDataList(ref r, result, "REFR_EXTRA_CREATED_ONLY");
        }
    }

    /// <summary>
    ///     Decodes initial data prepended by the save infrastructure before SaveGame_v2 runs.
    ///     Handles REFR_MOVE (bit 1), REFR_HAVOK_MOVE (bit 2), and REFR_CELL_CHANGED (bit 3).
    ///     initialDataType: 4=basic (27B), 5=created/mobile (31B), 6=exterior (34B), 0=unknown.
    /// </summary>
    internal static void DecodeRefrInitialData(ref FormDataReader r, uint flags, DecodedFormData result,
        int initialDataType = 0)
    {
        // ── Position flat struct ──
        // BGSSaveLoadInitialData::SaveInitialData writes ONE flat struct (no intermediate pipes)
        // via a single func_0x82689be0 call. Pipe only at the end.
        // Type 4 (MOVE/HAVOK): RefID(3B) + 6 floats(24B) = 27B (0x1B)
        // Type 5 (Created): + flags(1B) + baseFormRefId(3B) = 31B (0x1F)
        // Type 6 (Cell Changed): + newCellRefId(3B) + gridX(2B) + gridY(2B) = 34B (0x22)
        var hasMove = FormFieldWriter.HasFlag(flags, 0x00000002);
        var hasHavok = FormFieldWriter.HasFlag(flags, 0x00000004);
        var hasCellChanged = FormFieldWriter.HasFlag(flags, 0x00000008);
        var needsPositionBlock = hasMove || hasHavok || hasCellChanged;

        if (needsPositionBlock && r.HasData(27))
        {
            var startPos = r.Position;
            var cellRefId = r.ReadRefId();
            var posX = r.ReadFloat();
            var posY = r.ReadFloat();
            var posZ = r.ReadFloat();
            var rotX = r.ReadFloat();
            var rotY = r.ReadFloat();
            var rotZ = r.ReadFloat();

            // Type 5 extras (Created): flags(1B) + baseFormRefId(3B) — still in the flat struct
            var extraInfo = "";
            if (initialDataType == 5 && r.HasData(4))
            {
                var createdFlags = r.ReadByte();
                var baseFormRef = r.ReadRefId();
                extraInfo = $", CreatedFlags=0x{createdFlags:X2}, BaseForm={baseFormRef}";
            }

            // Type 6 extras (Cell Changed): newCell(3B) + gridX(2B) + gridY(2B) — still in the flat struct
            if (initialDataType == 6 && r.HasData(7))
            {
                var newCellRef = r.ReadRefId();
                var gridX = r.ReadInt16();
                var gridY = r.ReadInt16();
                extraInfo = $", NewCell={newCellRef}, Grid=({gridX}, {gridY})";
            }

            r.TrySkipPipe(); // single pipe after the entire flat struct

            var name = hasMove ? "REFR_MOVE" : "REFR_HAVOK_MOVE";
            result.Fields.Add(new DecodedField
            {
                Name = name,
                DisplayValue =
                    $"Cell={cellRefId}, Pos=({posX:F1}, {posY:F1}, {posZ:F1}), Rot=({rotX:F3}, {rotY:F3}, {rotZ:F3}){extraInfo}",
                DataOffset = startPos,
                DataLength = r.Position - startPos
            });
        }

        // ── Havok extra data ──
        // BGSSaveLoadInitialData writes additional Havok state AFTER the flat struct
        // when HAVOK_MOVE (bit 2) is set: vsval byte_count + pipe + raw bytes.
        // From decompilation line 21855: checks bit 2, then func_0x82689948/func_0x82689990 pattern.
        if (hasHavok && r.HasData(1))
        {
            var startPos = r.Position;
            var havokByteCount = r.ReadVsval();
            r.TrySkipPipe();
            if (havokByteCount > 0 && r.HasData((int)havokByteCount))
            {
                r.Seek(r.Position + (int)havokByteCount);
            }

            result.Fields.Add(new DecodedField
            {
                Name = "HAVOK_STATE",
                DisplayValue = $"{havokByteCount} bytes",
                DataOffset = startPos,
                DataLength = r.Position - startPos
            });
        }

        // ── CELL_CHANGED standalone ──
        // For Type 6, the newCell + grid are already embedded in the flat struct above.
        // For other types (Type 4/5), CELL_CHANGED may appear separately.
        if (hasCellChanged && initialDataType != 6 && r.HasData(3))
        {
            var startPos = r.Position;
            var cellRefId = r.ReadRefId();
            r.TrySkipPipe();
            result.Fields.Add(new DecodedField
            {
                Name = "REFR_CELL_CHANGED",
                DisplayValue = $"NewCell={cellRefId}",
                DataOffset = startPos,
                DataLength = r.Position - startPos
            });
        }
    }

    /// <summary>
    ///     Decodes REFR_ANIMATION (bit 28): vsval byte-count prefix + raw animation bytes.
    /// </summary>
    internal static void DecodeRefrAnimation(ref FormDataReader r, DecodedFormData result)
    {
        var startPos = r.Position;
        if (!r.HasData(1))
        {
            return;
        }

        var byteCount = r.ReadVsval();
        r.TrySkipPipe();
        if (byteCount > 0 && r.HasData((int)byteCount))
        {
            var animData = r.ReadBytes((int)byteCount);
            result.Fields.Add(new DecodedField
            {
                Name = "REFR_ANIMATION",
                Value = animData,
                DisplayValue = $"Animation data ({byteCount} bytes)",
                DataOffset = startPos,
                DataLength = r.Position - startPos
            });
        }
        else
        {
            result.Fields.Add(new DecodedField
            {
                Name = "REFR_ANIMATION",
                DisplayValue = $"Animation (count={byteCount}, 0 bytes)",
                DataOffset = startPos,
                DataLength = r.Position - startPos
            });
        }
    }

    internal static void DecodeProjectile(ref FormDataReader r, uint flags, DecodedFormData result)
    {
        // Phase 1: Initial data (MOVE, HAVOK_MOVE, CELL_CHANGED)
        DecodeRefrInitialData(ref r, flags, result);

        // Phase 2: Body data — projectiles don't write FORM_FLAGS body data.
        if (FormFieldWriter.HasFlag(flags, 0x00000010)) // REFR_SCALE
        {
            FormFieldWriter.AddFloatField(ref r, result, "REFR_SCALE");
        }

        // Bit 29: Projectile state — full MobileObject::SaveGame_v2 chain + Projectile::SaveGame data.
        // Chain: process_level → func_0x823ae3d0(REFR v2) → 14 MobileObject fields → process →
        //        9 floats → 3 RefIDs → 12B vector → 1 float → 1 byte → 1 uint32 →
        //        48B matrix → 12B vector → 1 float → 3 floats → conditional FormID →
        //        linked list → vsval list → 1 byte → [subtype tail: Missile=1 float, Flame=2 floats]
        // func_0x823ae3d0 is NOT YET DECOMPILED — raw blob until we can fully parse.
        if (FormFieldWriter.HasFlag(flags, 0x20000000))
        {
            FormFieldWriter.AddRawBlobField(ref r, result, "PROJECTILE_STATE",
                "Projectile save state (MobileObject + Projectile chain)");
        }

        // Projectiles have REFR_EXTRA_GAME_ONLY directly (no object/actor overlay)
        if (FormFieldWriter.HasFlag(flags, 0x80000000)) // REFR_EXTRA_GAME_ONLY
        {
            FormFieldWriter.AddRawBlobField(ref r, result, "REFR_EXTRA_GAME_ONLY", "Game-only extra data");
        }
    }
}
