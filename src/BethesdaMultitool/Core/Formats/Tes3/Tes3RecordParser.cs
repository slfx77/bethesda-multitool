using System.Buffers;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;
using BethesdaMultitool.Core.Formats.Esm.RecordModel;
using BethesdaMultitool.Core.Formats.Esm.RecordModel.Decoding;
using BethesdaMultitool.Core.Formats.Esm.RecordModel.Schema;
using BethesdaMultitool.Core.Formats.Esm.Subrecords;
using BethesdaMultitool.Core.Formats.Esm;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Tes3;

/// <summary>
///     Builds a <see cref="RecordCollection" /> from a scanned Morrowind (TES3) plugin. Because TES3
///     record/subrecord layouts share nothing with TES4+, the TES4 typed handlers can't run on this
///     data; records are parsed here. Most become a <see cref="GenericEsmRecord" /> whose
///     <see cref="GenericEsmRecord.Fields" /> carry decoded, typed subrecord values
///     (<see cref="Tes3SubrecordDecoder" />); CELLs become typed <see cref="CellRecord" />s with
///     resolved <see cref="PlacedReference" />s, and a synthetic exterior <see cref="WorldspaceRecord" />
///     groups them — so the world browser and 3D viewer get the typed structures they key off.
/// </summary>
internal sealed class Tes3RecordParser(RecordParserContext context)
{
    // Guards against a pathological record (e.g. a CELL with thousands of placed-reference
    // subrecords) ballooning the Fields map; the remainder is summarized.
    private const int MaxFieldsPerRecord = 256;

    private readonly RecordParserContext _context = context;

    // The registered TES3 record-layout schema (RecordModel/Generated/Tes3Schema). Drives the
    // SchemaRecordDecoder so Morrowind Records render the same DecodedTree the TES4 family does; the
    // legacy Tes3SubrecordDecoder Fields are still emitted for the CLI/report/semdiff tooling.
    private readonly IReadOnlyDictionary<string, RecordDef>? _schema =
        EsmSchemas.IndexForGame(BethesdaGame.Morrowind);

    // Typed dialogue, built positionally (an INFO belongs to the most recent DIAL in file order) so the
    // Dialogue tab works for Morrowind. INFO speakers are editor-id strings resolved to synthetic FormIDs
    // after the whole-file id index is built.
    private readonly List<DialogTopicRecord> _topics = [];
    private readonly List<Tes3DialogueExtractor.Tes3InfoDraft> _infoDrafts = [];

    public RecordCollection ParseAll()
    {
        var generic = new List<GenericEsmRecord>(_context.ScanResult.MainRecords.Count);
        var cellDrafts = new List<Tes3CellDraft>();
        var landDrafts = new List<Tes3LandDraft>();
        var landTextures = new List<LandscapeTextureRecord>();
        var textureSets = new List<TextureSetRecord>();
        var ltexIndexToFormId = new Dictionary<int, uint>();
        var formIdToEditorId = new Dictionary<uint, string>();
        var formIdToDisplayName = new Dictionary<uint, string>();

        // editor-id → base-record info, for resolving the string-keyed cell references. Morrowind
        // ids are case-insensitive.
        var idToFormId = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        var idToModel = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var idToType = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var maxFormId = 0u;

        // Positional DIAL→INFO linkage: an INFO belongs to the most recent DIAL in file order.
        uint? currentTopicFormId = null;
        ushort currentInfoIndex = 0;

        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            // Pass 1: decode every record; CELLs/LAND are drafted (references + terrain resolved in
            // pass 2 once the whole-file editor-id + land-texture indexes exist).
            foreach (var record in _context.ScanResult.MainRecords)
            {
                maxFormId = Math.Max(maxFormId, record.FormId);

                if (record.RecordType == "CELL")
                {
                    var read = _context.ReadRecordData(record, buffer);
                    if (read != null)
                    {
                        cellDrafts.Add(Tes3CellParser.Parse(read.Value.Data, read.Value.Size, record.FormId,
                            record.Offset));
                    }

                    continue;
                }

                if (record.RecordType == "LAND")
                {
                    var read = _context.ReadRecordData(record, buffer);
                    if (read != null && Tes3LandParser.Parse(read.Value.Data, read.Value.Size) is { } land)
                    {
                        landDrafts.Add(land);
                    }

                    continue;
                }

                if (record.RecordType == "LTEX")
                {
                    var read = _context.ReadRecordData(record, buffer);
                    if (read != null)
                    {
                        AddLandTexture(read.Value.Data, read.Value.Size, landTextures, textureSets,
                            ltexIndexToFormId);
                    }

                    continue;
                }

                var (parsed, subs) = ParseRecord(record, buffer);
                generic.Add(parsed);

                if (!string.IsNullOrEmpty(parsed.EditorId))
                {
                    formIdToEditorId[parsed.FormId] = parsed.EditorId;
                    idToFormId[parsed.EditorId] = parsed.FormId;
                    idToType[parsed.EditorId] = parsed.RecordType;
                    if (!string.IsNullOrEmpty(parsed.ModelPath))
                    {
                        idToModel[parsed.EditorId] = parsed.ModelPath!;
                    }
                }

                if (!string.IsNullOrEmpty(parsed.FullName))
                {
                    formIdToDisplayName[parsed.FormId] = parsed.FullName!;
                }

                // Dialogue: a DIAL opens a topic; the INFOs that follow it (until the next DIAL) are its
                // responses. Speakers (ONAM/FNAM/RNAM strings) are resolved after the loop, once every
                // record's editor-id → synthetic-FormID mapping exists.
                switch (record.RecordType)
                {
                    case "DIAL":
                        currentTopicFormId = record.FormId;
                        currentInfoIndex = 0;
                        _topics.Add(Tes3DialogueExtractor.BuildTopic(record.FormId, parsed.EditorId, subs));
                        break;
                    case "INFO":
                        _infoDrafts.Add(Tes3DialogueExtractor.BuildInfoDraft(
                            record.FormId, currentTopicFormId, currentInfoIndex++, subs));
                        break;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        // Resolve INFO speaker editor-id strings to the synthetic FormIDs now that the whole-file id
        // index is complete, then assemble the topic→response tree the Dialogue tab consumes.
        var dialogues = _infoDrafts.Select(d => Tes3DialogueExtractor.ToRecord(d, idToFormId)).ToList();
        var dialogueTree = new DialogueTreeBuilder(_context).BuildDialogueTrees(dialogues, _topics, []);

        // Pass 2: build typed cells (with terrain) + a synthetic exterior worldspace. The worldspace
        // uses a fixed cross-plugin FormID (not maxFormId+1) so every Morrowind plugin's exterior folds
        // into one worldspace at merge time; per-record synthetic IDs are namespaced by load order later
        // (Tes3FormIdScheme.Namespace) so they don't collide across plugins.
        var nextFormId = maxFormId + 1;
        var worldspaceFormId = WorldspaceRecord.Tes3SyntheticExteriorFormId;
        var modelPathIndex = new Dictionary<uint, string>();
        var landByGrid = landDrafts
            .GroupBy(l => (l.GridX, l.GridY))
            .ToDictionary(g => g.Key, g => g.First());
        var cells = BuildCells(cellDrafts, idToFormId, idToModel, idToType, worldspaceFormId,
            ref nextFormId, modelPathIndex, landByGrid, ltexIndexToFormId);
        var worldspaces = BuildWorldspaces(cells, worldspaceFormId);
        var mapMarkers = BuildMapMarkers(cells, ref nextFormId);

        return new RecordCollection
        {
            GenericRecords = generic,
            Cells = cells,
            Worldspaces = worldspaces,
            MapMarkers = mapMarkers,
            LandTextures = landTextures,
            TextureSets = textureSets,
            ModelPathIndex = modelPathIndex,
            DialogTopics = _topics,
            Dialogues = dialogues,
            DialogueTree = dialogueTree,
            FormIdToEditorId = formIdToEditorId,
            FormIdToDisplayName = formIdToDisplayName,
            TotalRecordsProcessed = _context.ScanResult.MainRecords.Count,
            IsTes3 = true,
            Game = _context.Game
            // UnparsedTypeCounts intentionally left empty: every TES3 record is parsed (typed cells/
            // worldspaces + decoded GenericRecords), so nothing should display as "not parsed".
        };
    }

    private const float MorrowindCellWorldSize = 8192f;

    // Morrowind LTEX: NAME = editor id, INTV = land-texture index (VTEX references index+1),
    // DATA = the texture file name. We model each as a LandscapeTextureRecord whose TextureSetFormId
    // points at a synthetic TextureSetRecord carrying the diffuse path, so the existing land-texture
    // palette (LTEX → TXST → diffuse) resolves it unchanged.
    private static void AddLandTexture(
        byte[] data, int dataSize,
        List<LandscapeTextureRecord> landTextures,
        List<TextureSetRecord> textureSets,
        Dictionary<int, uint> ltexIndexToFormId)
    {
        string? editorId = null;
        string? texturePath = null;
        var index = -1;

        foreach (var sub in Tes3SubrecordUtils.IterateSubrecords(data, dataSize))
        {
            var span = data.AsSpan(sub.DataOffset, sub.DataLength);
            var c = new Tes3Cursor(span);
            switch (sub.Signature)
            {
                case "NAME":
                    editorId = c.ReadRemainingString();
                    break;
                case "INTV" when span.Length >= 4:
                    index = c.ReadInt32();
                    break;
                case "DATA":
                    texturePath = c.ReadRemainingString();
                    break;
            }
        }

        if (index < 0 || string.IsNullOrEmpty(texturePath))
        {
            return;
        }

        var ltexFormId = Tes3FormIdScheme.LtexFormIdBase + (uint)index;
        var txstFormId = Tes3FormIdScheme.LtexTextureSetFormIdBase + (uint)index;
        ltexIndexToFormId[index] = ltexFormId;

        landTextures.Add(new LandscapeTextureRecord
        {
            FormId = ltexFormId,
            EditorId = editorId,
            TextureSetFormId = txstFormId
        });
        textureSets.Add(new TextureSetRecord
        {
            FormId = txstFormId,
            EditorId = editorId,
            DiffuseTexture = texturePath
        });
    }

    private static List<CellRecord> BuildCells(
        List<Tes3CellDraft> drafts,
        Dictionary<string, uint> idToFormId,
        Dictionary<string, string> idToModel,
        Dictionary<string, string> idToType,
        uint worldspaceFormId,
        ref uint nextFormId,
        Dictionary<uint, string> modelPathIndex,
        Dictionary<(int, int), Tes3LandDraft> landByGrid,
        Dictionary<int, uint> ltexIndexToFormId)
    {
        // Destination lookup for door teleports (the "Links to" line): TES3 doors name interior
        // targets by cell NAME (DNAM) and imply exterior targets by DODT position — both resolve to
        // the synthetic cell FormIDs assigned at parse, so this pre-pass must cover ALL drafts before
        // any reference resolves (a door can point at a cell parsed later in the file).
        var interiorCellsByName = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        var exteriorCellsByGrid = new Dictionary<(int GridX, int GridY), uint>();
        foreach (var draft in drafts)
        {
            if (draft.IsInterior)
            {
                if (!string.IsNullOrEmpty(draft.Name))
                {
                    interiorCellsByName.TryAdd(draft.Name!, draft.FormId);
                }
            }
            else
            {
                exteriorCellsByGrid.TryAdd((draft.GridX, draft.GridY), draft.FormId);
            }
        }

        var cells = new List<CellRecord>(drafts.Count);
        foreach (var draft in drafts)
        {
            var placed = new List<PlacedReference>(draft.References.Count);
            foreach (var r in draft.References)
            {
                if (string.IsNullOrEmpty(r.BaseId))
                {
                    continue;
                }

                idToFormId.TryGetValue(r.BaseId, out var baseFormId);
                idToModel.TryGetValue(r.BaseId, out var model);
                idToType.TryGetValue(r.BaseId, out var baseType);

                if (baseFormId != 0 && !string.IsNullOrEmpty(model))
                {
                    modelPathIndex[baseFormId] = model!;
                }

                placed.Add(new PlacedReference
                {
                    FormId = nextFormId++,
                    BaseFormId = baseFormId,
                    BaseEditorId = r.BaseId,
                    ModelPath = model,
                    RecordType = ReferenceRecordType(baseType),
                    X = r.X,
                    Y = r.Y,
                    Z = r.Z,
                    RotX = r.RotX,
                    RotY = r.RotY,
                    RotZ = r.RotZ,
                    Scale = r.Scale,
                    DestinationCellFormId = ResolveTeleportDestination(
                        r, interiorCellsByName, exteriorCellsByGrid),
                    // DODT arrival pose feeds the viewer's door warp (same shape as TES4 XTEL:
                    // destination-cell coords + radian rotations).
                    TeleportPosRot = r.HasTeleportDestination
                        ? new PositionSubrecord(
                            r.DestX, r.DestY, r.DestZ,
                            r.DestRotX, r.DestRotY, r.DestRotZ,
                            Offset: 0, IsBigEndian: false)
                        : null
                });
            }

            var interior = draft.IsInterior;

            // Attach terrain to exterior cells from the matching LAND record (by grid coords). The
            // heights are kept at Morrowind's native 65×65 resolution (no downsample) so the 3D viewer
            // renders the terrain as-is; the 2D map downsamples internally in WorldRenderCache.
            LandHeightmap? heightmap = null;
            LandVisualData? visual = null;
            if (!interior && landByGrid.TryGetValue((draft.GridX, draft.GridY), out var land))
            {
                if (land.Heights is { } heights)
                {
                    heightmap = new LandHeightmap
                    {
                        HeightDeltas = [], // unused: ExactHeights drives CalculateHeights()
                        ExactHeights = heights
                    };
                }

                visual = BuildLandVisualData(land, ltexIndexToFormId);
            }

            cells.Add(new CellRecord
            {
                FormId = draft.FormId,
                EditorId = CellEditorId(draft),
                FullName = interior ? draft.Name : draft.Region,
                GridX = interior ? null : draft.GridX,
                GridY = interior ? null : draft.GridY,
                Flags = (byte)(draft.Flags & 0xFF),
                WorldspaceFormId = interior ? null : worldspaceFormId,
                CellWorldSize = interior ? 0f : MorrowindCellWorldSize,
                WaterHeight = draft.WaterHeight,
                Heightmap = heightmap,
                LandVisualData = visual,
                PlacedObjects = placed,
                Offset = draft.Offset
            });
        }

        return cells;
    }

    /// <summary>
    ///     Resolves a door reference's teleport destination to the target cell's synthetic FormID:
    ///     DNAM names an interior cell (exact NAME match, case-insensitive like the engine's cell
    ///     lookup); otherwise a DODT position implies an exterior cell via the 8192-unit grid. Null
    ///     when the reference isn't a teleport door or the target cell isn't in this file (a plugin
    ///     can door into a master's cell — resolvable only after merge, not per-file).
    /// </summary>
    internal static uint? ResolveTeleportDestination(
        Tes3RefDraft r,
        Dictionary<string, uint> interiorCellsByName,
        Dictionary<(int GridX, int GridY), uint> exteriorCellsByGrid)
    {
        if (r.DestinationCellName is { Length: > 0 } destName &&
            interiorCellsByName.TryGetValue(destName, out var byName))
        {
            return byName;
        }

        if (r.HasTeleportDestination)
        {
            var gridX = (int)MathF.Floor(r.DestX / MorrowindCellWorldSize);
            var gridY = (int)MathF.Floor(r.DestY / MorrowindCellWorldSize);
            if (exteriorCellsByGrid.TryGetValue((gridX, gridY), out var byGrid))
            {
                return byGrid;
            }
        }

        return null;
    }

    // Morrowind's land texturing is a flat 16×16 grid of land-texture indices (no TES4-style alpha
    // blending). The 3D terrain renderer samples the resolved 16×16 FormId grid (VtexTextureFormIds)
    // per vertex. The four per-quadrant dominant Base layers are still emitted for the 2D map (which
    // consumes BTXT layers); they are a coarse fallback, not the 3D path.
    private static LandVisualData? BuildLandVisualData(Tes3LandDraft land, Dictionary<int, uint> ltexIndexToFormId)
    {
        // Native 65×65×3 vertex colors (no downsample) so they line up 1:1 with the native heightmap
        // grid the 3D viewer builds; the per-vertex VertexColor read indexes j*65+i.
        var colors = land.VertexColors is { Length: Tes3LandDraft.Size * Tes3LandDraft.Size * 3 }
            ? land.VertexColors
            : null;
        var layers = new List<LandTextureLayer>(4);
        uint[]? vtexFormIds = null;

        if (land.TextureIndices is { Length: Tes3LandDraft.VtexSize * Tes3LandDraft.VtexSize } vtex)
        {
            // Resolve the full 16×16 grid to LTEX FormIds (0 = engine-default land texture) for the 3D
            // per-vertex path.
            vtexFormIds = new uint[Tes3LandDraft.VtexSize * Tes3LandDraft.VtexSize];
            for (var k = 0; k < vtex.Length; k++)
            {
                var v = vtex[k];
                vtexFormIds[k] = v != 0 && ltexIndexToFormId.TryGetValue(v - 1, out var ltexFormId)
                    ? ltexFormId
                    : 0u;
            }

            for (byte quadrant = 0; quadrant < 4; quadrant++)
            {
                var rowStart = (quadrant & 2) != 0 ? 8 : 0; // bits: 2 = north half, 1 = east half
                var colStart = (quadrant & 1) != 0 ? 8 : 0;
                var counts = new Dictionary<uint, int>();
                for (var r = rowStart; r < rowStart + 8; r++)
                {
                    for (var c = colStart; c < colStart + 8; c++)
                    {
                        var formId = vtexFormIds[r * Tes3LandDraft.VtexSize + c];
                        if (formId == 0)
                        {
                            continue; // 0 = engine default land texture
                        }

                        counts[formId] = counts.GetValueOrDefault(formId) + 1;
                    }
                }

                if (counts.Count == 0)
                {
                    continue;
                }

                var dominant = counts.OrderByDescending(kv => kv.Value).First().Key;
                layers.Add(new LandTextureLayer
                {
                    Kind = LandTextureLayerKind.Base,
                    TextureFormId = dominant,
                    Quadrant = quadrant
                });
            }
        }

        if (colors == null && layers.Count == 0 && vtexFormIds == null)
        {
            return null;
        }

        return new LandVisualData
        {
            VertexColors = colors,
            TextureLayers = layers,
            TextureIndices = land.TextureIndices?.Select(v => (uint)v).ToArray(),
            VtexTextureFormIds = vtexFormIds
        };
    }

    // Morrowind has no map-marker records; the world map labels named exterior cells (towns/landmarks).
    // Synthesize one marker per unique named exterior location at the centroid of the cells sharing
    // that name, so the map shows Balmora/Vivec/… like later games' XMRK markers.
    private static List<PlacedReference> BuildMapMarkers(List<CellRecord> cells, ref uint nextFormId)
    {
        var groups = new Dictionary<string, (double SumX, double SumY, int Count)>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in cells)
        {
            if (c.IsInterior || c.GridX is not { } gx || c.GridY is not { } gy)
            {
                continue;
            }

            // Named exterior cells have a real NAME; unnamed ones get the synthetic "[x,y]" id.
            var name = c.EditorId;
            if (string.IsNullOrEmpty(name) || name.StartsWith('['))
            {
                continue;
            }

            var centerX = gx * MorrowindCellWorldSize + MorrowindCellWorldSize / 2f;
            var centerY = gy * MorrowindCellWorldSize + MorrowindCellWorldSize / 2f;
            var prev = groups.GetValueOrDefault(name);
            groups[name] = (prev.SumX + centerX, prev.SumY + centerY, prev.Count + 1);
        }

        var markers = new List<PlacedReference>(groups.Count);
        foreach (var (name, agg) in groups)
        {
            markers.Add(new PlacedReference
            {
                FormId = nextFormId++,
                RecordType = "REFR",
                IsMapMarker = true,
                MarkerName = name,
                MarkerType = BethesdaMultitool.Core.Formats.Esm.Enums.MapMarkerType.Settlement,
                X = (float)(agg.SumX / agg.Count),
                Y = (float)(agg.SumY / agg.Count),
                Z = 0f
            });
        }

        return markers;
    }

    internal static List<WorldspaceRecord> BuildWorldspaces(List<CellRecord> cells, uint worldspaceFormId)
    {
        var exterior = cells.Where(c => !c.IsInterior).ToList();
        if (exterior.Count == 0)
        {
            return [];
        }

        return
        [
            new WorldspaceRecord
            {
                FormId = worldspaceFormId,
                EditorId = "Wilderness",
                FullName = "Morrowind (Exterior)",
                // Morrowind (TES3) has no DNAM land/water-height block — that field is a Fallout 3
                // addition (the HasWorldspaceDefaultWaterHeight capability, which TES3 lacks), and TES3
                // exterior cells carry no XCLW: the engine renders the sea at Z 0 by convention and a
                // cell only overrides that for an inland body of water (a WHGT, parsed into WaterHeight).
                // Without a worldspace default every coastal cell falls through to "no water" in
                // WorldRenderCache.ResolveEffectiveWaterHeight and Vvardenfell's ocean renders dry, so seed
                // the sea-level-0 default here — mirroring the synthesized Oblivion default in
                // WorldspaceRecordHandler. A cell's own WaterHeight still takes precedence over this.
                DefaultWaterHeight = 0f,
                Cells = exterior
            }.WithMorrowindExteriorBounds(exterior)
        ];
    }

    // Morrowind references actors (NPC_/CREA) too; map them to ACHR/ACRE so the static-mesh viewer
    // skips them (it only renders REFR), exactly as it does for FNV/Skyrim placed actors.
    private static string ReferenceRecordType(string? baseType) => baseType switch
    {
        "NPC_" => "ACHR",
        "CREA" => "ACRE",
        _ => "REFR"
    };

    private static string CellEditorId(Tes3CellDraft draft)
    {
        if (!string.IsNullOrEmpty(draft.Name))
        {
            return draft.Name!;
        }

        return draft.IsInterior
            ? $"Interior 0x{draft.FormId:X8}"
            : $"[{draft.GridX},{draft.GridY}]";
    }

    private (GenericEsmRecord Record, List<RawSubrecord> Subs) ParseRecord(DetectedMainRecord record, byte[] buffer)
    {
        var read = _context.ReadRecordData(record, buffer);
        if (read == null)
        {
            return (new GenericEsmRecord
            {
                FormId = record.FormId,
                RecordType = record.RecordType,
                EditorId = _context.GetEditorId(record.FormId),
                Offset = record.Offset,
                IsBigEndian = false
            }, []);
        }

        var (data, dataSize) = read.Value;
        var type = record.RecordType;

        string? editorId = null;
        string? fullName = null;
        string? modelPath = null;
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
        var sigCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var rawSubs = new List<RawSubrecord>();
        var decodedCount = 0;
        var truncated = 0;

        foreach (var sub in Tes3SubrecordUtils.IterateSubrecords(data, dataSize))
        {
            var span = data.AsSpan(sub.DataOffset, sub.DataLength);
            var sig = sub.Signature;

            // Captured once for the schema decode below and (for DIAL/INFO) the dialogue extractor.
            rawSubs.Add(new RawSubrecord(sig, span.ToArray()));

            // Header-level fields: surfaced via EditorId / FullName / ModelPath rather than the table.
            if (TryCaptureHeaderField(type, sig, span, ref editorId, ref fullName, ref modelPath))
            {
                continue;
            }

            if (decodedCount >= MaxFieldsPerRecord)
            {
                truncated++;
                continue;
            }

            var owner = NextOwnerKey(sigCounts, sig);
            foreach (var field in Tes3SubrecordDecoder.Decode(type, sig, span))
            {
                fields[$"{owner}.{field.Name}"] = field.Value;
            }

            decodedCount++;
        }

        if (truncated > 0)
        {
            fields["(more).subrecords"] = $"+{truncated} additional subrecords not shown";
        }

        // Schema-driven DecodedTree (the GUI Records tab renders this via EsmBrowserTreeBuilder's
        // early-return, identical to the TES4 family). The legacy Fields above stay for the CLI/report/
        // semdiff surfaces. TES3 is little-endian and refs are strings, so no FormID resolver is needed.
        IReadOnlyList<DecodedNode>? tree = null;
        if (_schema != null && _schema.TryGetValue(type, out var def))
        {
            tree = SchemaRecordDecoder.Decode(def, rawSubs);
        }

        return (new GenericEsmRecord
        {
            FormId = record.FormId,
            RecordType = type,
            EditorId = editorId ?? _context.GetEditorId(record.FormId),
            FullName = fullName,
            ModelPath = modelPath,
            Fields = fields,
            DecodedTree = tree,
            Offset = record.Offset,
            IsBigEndian = false
        }, rawSubs);
    }

    // Pulls the id / display-name / model out into the record header. Returns true when the subrecord
    // was consumed as a header field (and should not also appear in the generic field table).
    private static bool TryCaptureHeaderField(
        string type, string sig, ReadOnlySpan<byte> span,
        ref string? editorId, ref string? fullName, ref string? modelPath)
    {
        switch (sig)
        {
            case "MODL":
                modelPath = ReadString(span);
                return true;

            case "NAME" when type == "INFO":
                // INFO's NAME is the spoken response text, not an id; keep a short preview as the name
                // and let the full text appear in the field table.
                fullName = Preview(ReadString(span));
                return false;

            case "INAM" when type == "INFO":
                editorId = ReadString(span);
                return true;

            case "NAME" when type == "CELL":
                editorId = ReadString(span);
                fullName ??= editorId;
                return true;

            case "NAME":
                editorId = ReadString(span);
                return true;

            case "FNAM" when type != "GLOB":
                fullName = ReadString(span);
                return true;

            default:
                return false;
        }
    }

    private static string NextOwnerKey(Dictionary<string, int> sigCounts, string sig)
    {
        var n = sigCounts.GetValueOrDefault(sig) + 1;
        sigCounts[sig] = n;
        return n == 1 ? sig : $"{sig} {n}";
    }

    private static string ReadString(ReadOnlySpan<byte> span)
    {
        var c = new Tes3Cursor(span);
        return c.ReadRemainingString();
    }

    private static string Preview(string text)
    {
        text = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length <= 60 ? text : text[..57] + "...";
    }
}
