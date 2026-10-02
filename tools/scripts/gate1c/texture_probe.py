# SPDX-License-Identifier: 0BSD
"""Gate-1c hop A4x oracle: independent texture slicing and decoding for the XnGine mesh games.

Three containers, each restated from the reference behavior the cut-1c plan cites rather than
from BMT's C# readers:

- Daggerfall (and Redguard 3dart) ``TEXTURE.nnn``: the DaggerfallConnect layout (daggerfall-unity,
  MIT). A 26-byte header (i16 record count + 24-byte set name), 20-byte record headers whose i32
  at +2 is the record position, 28-byte record descriptors (offX i16, offY i16, width i16,
  height i16, compression u16 at +8, dataOffset u32 at +14, frameCount u16 at +20). Three storage
  forms: single-frame uncompressed rows on a fixed 256-byte stride; multi-frame records behind an
  i32 offset table, each frame i16 cx + i16 cy then per row alternating transparent-run and
  literal-run bytes (a skip writes index 0); RLE records (compression 0x1108 or 0x0108) with
  4-byte row headers (i16 row offset FROM THE RECORD POSITION, u16 flag; 0x8000 marks an RLE row:
  u16 row width then signed i16 probes, negative repeats one byte, positive copies that many).
  TEXTURE.000/.001 are generated solid swatches (record N is palette index N or 128 + N);
  TEXTURE.215/.217/.436 are malformed in retail and refused by name.
- Battlespire ``.BSI`` (inside BSI.BSA): the ariscop/battlespire-tools chunk grammar (Unlicense).
  4-byte ASCII tag + BIG-endian u32 length; BSIF's length counts its own header so only 8 bytes
  are skipped; BHDR (26 bytes: xoff i16, yoff i16, width i16 at +4, height i16 at +6, frames i16
  at +14, compression i16 at +24) opens an image and DATA closes it; NAME, CMAP (768-byte 6-bit
  VGA), HICL (256-byte 15-bit), HTBL (light ramp) ride along. Compression 0 is raw; 4 and 6 are a
  line table: one u32 per line (height x frames), top bit = run-length coded line, low 31 bits =
  offset in the data block; a control byte with bit 7 repeats the next byte (count = low 7 bits),
  otherwise it counts literal bytes.
- Texture keys: Daggerfall u16 (archive = bits >> 7, record = bits & 0x7F); Redguard u16 with the
  same split (archive * 128 + record into 3dart TEXTURE.nnn); Battlespire u32, a base-40 encoding
  of the BSI stem (alphabet ``0123456789abcdefghijklmnopqrstuvwxyz~_#%``, first place 40^5, digit
  39 terminates), with keys at or above 0xFFF00000 solid-colour planes, not names.
"""

import struct

from xngine_probe import ProbeError, lzss_expand, parse_xngine_bsa, sha256_hex

DF_HEADER_LENGTH = 26
DF_RECORD_HEADER_LENGTH = 20
DF_RECORD_DESCRIPTOR_LENGTH = 28
DF_UNCOMPRESSED_ROW_STRIDE = 256
DF_ROW_IS_RLE = 0x8000
DF_COMPRESSION_RECORD_RLE = 0x1108
DF_COMPRESSION_IMAGE_RLE = 0x0108
DF_UNSUPPORTED = ("TEXTURE.215", "TEXTURE.217", "TEXTURE.436")
DF_SOLID_SIZE = 32

BS_ALPHABET = "0123456789abcdefghijklmnopqrstuvwxyz~_#%"
BS_RADIX = 40
BS_MAX_LENGTH = 6
BS_FIRST_PLACE = 40 ** 5
BS_SOLID_THRESHOLD = 0xFFF0_0000

BSI_KNOWN_TAGS = ("IFHD", "NAME", "BHDR", "HICL", "HTBL", "CMAP", "DATA")


# --------------------------------------------------------------------------- texture keys

def daggerfall_key_parts(key):
    """Daggerfall's packed u16: TEXTURE archive in the high 9 bits, record in the low 7."""
    return key >> 7, key & 0x7F


def redguard_key_parts(key):
    """Redguard packs archive * 128 + record, the same arithmetic as Daggerfall's split."""
    return key >> 7, key & 0x7F


def battlespire_is_solid(key):
    return key >= BS_SOLID_THRESHOLD


def battlespire_stem(key):
    """Decodes a Battlespire plane key into its BSI stem, or None for a solid-colour key or a
    leading digit outside the alphabet."""
    if battlespire_is_solid(key):
        return None
    name = []
    remaining = key
    place = BS_FIRST_PLACE
    for _ in range(BS_MAX_LENGTH):
        digit = remaining // place
        if digit == BS_RADIX - 1:
            break
        if digit > BS_RADIX - 1:
            return None
        name.append(BS_ALPHABET[digit])
        remaining %= place
        place //= BS_RADIX
    return "".join(name) or None


# --------------------------------------------------------------------------- TEXTURE.nnn

def df_texture_parse(b, name, decode_pixels=True, refuse_malformed=True):
    """Parses one TEXTURE.nnn. Returns the set name and per-record dicts: descriptor fields, the
    consumed source byte range, and (when decode_pixels) the decoded index frames, each exactly
    width * height bytes. Solid files (.000/.001) synthesize their swatches. Raises ProbeError on
    a refused name or a malformed record.

    ``refuse_malformed`` mirrors the reference's by-name refusal of TEXTURE.215/.217/.436, which
    are malformed in DAGGERFALL's retail data. Redguard's 3dart sets reuse the numbering, and its
    meshes reference sets 215 and 217, so a Redguard caller passes False and judges the parse on
    its own acceptance checks instead."""
    upper = name.upper()
    if refuse_malformed and upper in DF_UNSUPPORTED:
        raise ProbeError(f"{name} is one of the three malformed retail archives")
    if len(b) < DF_HEADER_LENGTH:
        raise ProbeError(f"{name} is too small for a TEXTURE header")
    record_count = struct.unpack_from("<h", b, 0)[0]
    if record_count <= 0:
        raise ProbeError(f"{name} declares {record_count} records")
    set_name = b[2:DF_HEADER_LENGTH].split(b"\0", 1)[0].decode("latin-1")
    solid_base = {"TEXTURE.000": 0, "TEXTURE.001": 128}.get(upper, -1)
    records = []
    for r in range(record_count):
        header_offset = DF_HEADER_LENGTH + r * DF_RECORD_HEADER_LENGTH
        if header_offset + DF_RECORD_HEADER_LENGTH > len(b):
            raise ProbeError(f"{name} ends inside record header {r}")
        position = struct.unpack_from("<i", b, header_offset + 2)[0]
        if position < 0 or position + DF_RECORD_DESCRIPTOR_LENGTH > len(b):
            raise ProbeError(f"{name} record {r} points outside the file")
        offset_x, offset_y, width, height = struct.unpack_from("<hhhh", b, position)
        compression = struct.unpack_from("<H", b, position + 8)[0]
        data_offset = struct.unpack_from("<I", b, position + 14)[0]
        frame_count = struct.unpack_from("<H", b, position + 20)[0]
        record = {"record": r, "position": position, "offset_x": offset_x, "offset_y": offset_y,
                  "width": width, "height": height, "compression": compression,
                  "data_offset": data_offset, "frame_count": frame_count, "frames": None,
                  "consumed": None, "solid_index": None}
        if solid_base >= 0:
            record["solid_index"] = solid_base + r
            if decode_pixels:
                record["frames"] = [bytes([solid_base + r]) * (DF_SOLID_SIZE * DF_SOLID_SIZE)]
        elif frame_count > 0 and decode_pixels:
            if width <= 0 or height <= 0:
                raise ProbeError(f"{name} record {r} declares frames with {width}x{height}")
            data_start = position + data_offset
            if data_start >= len(b):
                raise ProbeError(f"{name} record {r} data offset outside the file")
            if compression in (DF_COMPRESSION_RECORD_RLE, DF_COMPRESSION_IMAGE_RLE):
                frames, consumed = _df_decode_rle(b, name, r, position, data_start, width, height,
                                                  frame_count)
            else:
                frames, consumed = _df_decode_uncompressed(b, name, r, data_start, width, height,
                                                           frame_count)
            record["frames"] = frames
            record["consumed"] = consumed
        records.append(record)
    return {"name": name, "set_name": set_name, "record_count": record_count, "records": records,
            "solid": solid_base >= 0}


def _df_decode_uncompressed(b, name, r, data_start, width, height, frame_count):
    frames = []
    low = data_start
    high = data_start
    if frame_count == 1:
        pixels = bytearray(width * height)
        source = data_start
        for y in range(height):
            if source + width > len(b):
                raise ProbeError(f"{name} record {r} row {y} runs past the end")
            pixels[y * width:(y + 1) * width] = b[source:source + width]
            high = max(high, source + width)
            source += DF_UNCOMPRESSED_ROW_STRIDE
        return [bytes(pixels)], (low, high)
    for frame in range(frame_count):
        table = data_start + frame * 4
        if table + 4 > len(b):
            raise ProbeError(f"{name} record {r} frame table is truncated")
        frame_start = data_start + struct.unpack_from("<i", b, table)[0]
        if frame_start < 0 or frame_start + 4 > len(b):
            raise ProbeError(f"{name} record {r} frame {frame} points outside the file")
        cx, cy = struct.unpack_from("<hh", b, frame_start)
        if cx <= 0 or cy <= 0 or cx > width or cy > height:
            raise ProbeError(f"{name} record {r} frame {frame} declares {cx}x{cy} in {width}x{height}")
        pixels = bytearray(width * height)
        source = frame_start + 4
        destination = 0
        for _ in range(cy):
            x = 0
            while x < cx:
                if source + 2 > len(b):
                    raise ProbeError(f"{name} record {r} frame {frame} ends mid-row")
                skip = b[source]
                literal = b[source + 1]
                source += 2
                for _ in range(min(skip, cx - x)):
                    pixels[destination] = 0
                    destination += 1
                    x += 1
                if source + literal > len(b):
                    raise ProbeError(f"{name} record {r} literal run is truncated")
                take = min(literal, cx - x)
                pixels[destination:destination + take] = b[source:source + take]
                destination += take
                x += take
                source += literal
        high = max(high, source)
        frames.append(bytes(pixels))
    return frames, (low, high)


def _df_decode_rle(b, name, r, position, data_start, width, height, frame_count):
    frames = []
    low = data_start
    high = data_start
    for frame in range(frame_count):
        headers = data_start + height * frame * 4
        if headers + height * 4 > len(b):
            raise ProbeError(f"{name} record {r} frame {frame} row headers are truncated")
        high = max(high, headers + height * 4)
        pixels = bytearray(width * height)
        destination = 0
        for y in range(height):
            row_offset = struct.unpack_from("<h", b, headers + y * 4)[0]
            encoding = struct.unpack_from("<H", b, headers + y * 4 + 2)[0]
            source = position + row_offset
            if source < 0 or source >= len(b):
                raise ProbeError(f"{name} record {r} row {y} points outside the file")
            if encoding == DF_ROW_IS_RLE:
                row_width = struct.unpack_from("<H", b, source)[0]
                source += 2
                written = 0
                while written < row_width:
                    if source + 2 > len(b):
                        raise ProbeError(f"{name} record {r} row {y} RLE stream is truncated")
                    probe = struct.unpack_from("<h", b, source)[0]
                    source += 2
                    if probe < 0:
                        value = b[source]
                        source += 1
                        pixels[destination:destination - probe] = bytes([value]) * -probe
                        destination += -probe
                        written += -probe
                    elif probe > 0:
                        if source + probe > len(b):
                            raise ProbeError(f"{name} record {r} row {y} literal run is truncated")
                        pixels[destination:destination + probe] = b[source:source + probe]
                        source += probe
                        destination += probe
                        written += probe
                    else:
                        raise ProbeError(f"{name} record {r} row {y} has a zero-length probe")
            else:
                if source + width > len(b):
                    raise ProbeError(f"{name} record {r} raw row {y} runs past the file")
                pixels[destination:destination + width] = b[source:source + width]
                destination += width
            high = max(high, source if encoding == DF_ROW_IS_RLE else source + width)
        frames.append(bytes(pixels))
    return frames, (low, high)


# --------------------------------------------------------------------------- BSI

def bsi_parse(b, name, decode_pixels=True):
    """Walks one .BSI chunk stream. Returns the images (header fields, chunk presence, decoded
    frames of exactly width * height bytes each) or raises ProbeError for a non-BSI payload."""
    images = []
    pending_name = None
    header = None
    palette_chunks = {"CMAP": False, "HICL": False, "HTBL": False}
    saw_chunk = False
    position = 0
    while position + 8 <= len(b):
        tag = b[position:position + 4]
        length = struct.unpack_from(">I", b, position + 4)[0]
        try:
            tag_text = tag.decode("ascii")
        except UnicodeDecodeError:
            tag_text = None
        if tag_text == "BSIF":
            position += 8
            saw_chunk = True
            continue
        if tag_text == "END ":
            saw_chunk = True
            break
        if tag_text not in BSI_KNOWN_TAGS:
            if not saw_chunk:
                raise ProbeError(f"{name} does not open with a BSI chunk")
            raise ProbeError(f"{name}: unknown chunk at {position}")
        if position + 8 + length > len(b):
            raise ProbeError(f"{name}: chunk {tag_text} overruns the file")
        payload = b[position + 8: position + 8 + length]
        position += 8 + length
        saw_chunk = True
        if tag_text == "NAME":
            pending_name = payload.split(b"\0", 1)[0].decode("latin-1")
        elif tag_text == "BHDR":
            if len(payload) < 26:
                raise ProbeError(f"{name}: BHDR is {len(payload)} bytes")
            header = {"x_offset": struct.unpack_from("<h", payload, 0)[0],
                      "y_offset": struct.unpack_from("<h", payload, 2)[0],
                      "width": struct.unpack_from("<h", payload, 4)[0],
                      "height": struct.unpack_from("<h", payload, 6)[0],
                      "frame_count": struct.unpack_from("<h", payload, 14)[0],
                      "compression": struct.unpack_from("<h", payload, 24)[0]}
            if header["width"] <= 0 or header["height"] <= 0 or header["frame_count"] <= 0:
                raise ProbeError(f"{name}: implausible BHDR geometry")
        elif tag_text in palette_chunks:
            palette_chunks[tag_text] = True
        elif tag_text == "DATA":
            if header is None:
                raise ProbeError(f"{name}: DATA before any BHDR")
            image = dict(header)
            image["name"] = pending_name
            image["chunks"] = dict(palette_chunks)
            if decode_pixels:
                pixels = payload if header["compression"] == 0 else _bsi_decompress(payload, header, name)
                expected = header["width"] * header["height"] * header["frame_count"]
                if len(pixels) != expected:
                    raise ProbeError(f"{name}: {len(pixels)} decoded bytes, {expected} declared")
                frame_length = header["width"] * header["height"]
                image["frames"] = [bytes(pixels[i * frame_length:(i + 1) * frame_length])
                                   for i in range(header["frame_count"])]
            images.append(image)
            header = None
            pending_name = None
    if not saw_chunk:
        raise ProbeError(f"{name} holds no BSI chunks")
    return {"name": name, "images": images}


def _bsi_decompress(data, header, name):
    lines = header["height"] * header["frame_count"]
    width = header["width"]
    if lines * 4 > len(data):
        raise ProbeError(f"{name}: the {lines}-line offset table does not fit")
    out = bytearray(width * lines)
    written = 0
    for line in range(lines):
        entry = struct.unpack_from("<I", data, line * 4)[0]
        compressed = bool(entry & 0x8000_0000)
        offset = entry & 0x7FFF_FFFF
        if offset > len(data):
            raise ProbeError(f"{name}: line {line} starts outside the data block")
        if not compressed:
            if offset + width > len(data):
                raise ProbeError(f"{name}: raw line {line} runs past the data block")
            out[written:written + width] = data[offset:offset + width]
            written += width
            continue
        read = offset
        produced = 0
        while produced < width:
            if read >= len(data):
                raise ProbeError(f"{name}: line {line} ran out of input")
            control = data[read]
            read += 1
            count = control & 0x7F
            if control & 0x80:
                if read >= len(data) or produced + count > width:
                    raise ProbeError(f"{name}: line {line} run overruns its width")
                out[written + produced:written + produced + count] = bytes([data[read]]) * count
                read += 1
            else:
                if read + count > len(data) or produced + count > width:
                    raise ProbeError(f"{name}: line {line} literal overruns its width")
                out[written + produced:written + produced + count] = data[read:read + count]
                read += count
            produced += count
        written += width
    return bytes(out)


# --------------------------------------------------------------------------- BSI.BSA access

def bsi_archive_entries(archive_bytes):
    """Lists BSI.BSA (a name-record XnGine BSA) and returns {upper stem: entry dict}."""
    rtype, entries = parse_xngine_bsa(archive_bytes)
    if rtype != 0x0100:
        raise ProbeError("BSI.BSA is not a name-record archive")
    by_stem = {}
    for entry in entries:
        stem = entry["name"].upper()
        if stem.endswith(".BSI"):
            stem = stem[:-4]
        by_stem[stem] = entry
    return entries, by_stem


def bsi_entry_bytes(archive_bytes, entry):
    raw = archive_bytes[entry["offset"]: entry["offset"] + entry["size"]]
    return lzss_expand(raw) if entry["compressed"] else raw


def record_slice_sha(record_bytes_range, source_bytes):
    """SHA-256 of a consumed source range, the A4x Original comparison hook."""
    low, high = record_bytes_range
    return sha256_hex(source_bytes[low:high])
