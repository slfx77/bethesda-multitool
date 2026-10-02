# SPDX-License-Identifier: 0BSD
"""DDS header parsing and level-zero decoding to RGBA8.

Two decoders are provided and both are independent of the writer:

* ``decode_pillow``: Pillow's ``DdsImagePlugin`` (the pinned independent decoder of design section 7.2, hop B).
* ``decode_numpy``: this file's own block decoders (BC1/DXT1 with the three-color mode, BC2/DXT3, BC3/DXT5,
  BC4/ATI1, BC5/ATI2) and masked uncompressed formats, written from the Direct3D block-compression
  documentation (D3D "Texture Block Compression in Direct3D 11", "BC1", "BC2", "BC3", "BC4", "BC5") and
  the DDS_HEADER / DDS_PIXELFORMAT / DDS_HEADER_DXT10 layouts.

``decode`` returns the Pillow result and records which decoder path decoded the format, plus the maximum
per-channel difference between the two decoders so a decoder disagreement is visible in the receipt.
"""

import io
import struct

import numpy as np

from gate1a_common import OracleError

DDPF_ALPHAPIXELS, DDPF_ALPHA, DDPF_FOURCC, DDPF_RGB, DDPF_LUMINANCE = 0x1, 0x2, 0x4, 0x40, 0x20000
FOURCC_DX10 = b'DX10'
FOURCC_BLOCKS = {
    b'DXT1': ('BC1', 8), b'DXT2': ('BC2', 16), b'DXT3': ('BC2', 16), b'DXT4': ('BC3', 16), b'DXT5': ('BC3', 16),
    b'ATI1': ('BC4', 8), b'BC4U': ('BC4', 8), b'ATI2': ('BC5', 16), b'BC5U': ('BC5', 16),
}
DXGI_BLOCKS = {70: ('BC1', 8), 71: ('BC1', 8), 72: ('BC1', 8), 73: ('BC2', 16), 74: ('BC2', 16), 75: ('BC2', 16),
               76: ('BC3', 16), 77: ('BC3', 16), 78: ('BC3', 16), 79: ('BC4', 8), 80: ('BC4', 8),
               82: ('BC5', 16), 83: ('BC5', 16)}


class DdsHeader:
    def __init__(self, data):
        if len(data) < 128 or data[:4] != b'DDS ':
            raise OracleError('not a DDS file (magic %r)' % data[:4])
        (size, flags, height, width, pitch, depth, mips) = struct.unpack_from('<7I', data, 4)
        if size != 124:
            raise OracleError('DDS header size %d, not 124' % size)
        pf_size, pf_flags = struct.unpack_from('<II', data, 76)
        if pf_size != 32:
            raise OracleError('DDS pixel-format size %d, not 32' % pf_size)
        fourcc = data[84:88]
        bit_count, r_mask, g_mask, b_mask, a_mask = struct.unpack_from('<5I', data, 88)
        caps, caps2 = struct.unpack_from('<II', data, 108)
        self.flags, self.height, self.width, self.pitch_or_size, self.depth = flags, height, width, pitch, depth
        self.mip_count = mips if flags & 0x20000 else 1
        self.pf_flags, self.fourcc = pf_flags, fourcc
        self.bit_count, self.masks = bit_count, (r_mask, g_mask, b_mask, a_mask)
        self.caps2 = caps2
        self.is_cube = bool(caps2 & 0x200)
        self.is_volume = bool(caps2 & 0x200000) or bool(flags & 0x800000)
        self.dxgi_format = None
        self.data_offset = 128
        self.compression = None
        self.block_bytes = 0
        if pf_flags & DDPF_FOURCC:
            if fourcc == FOURCC_DX10:
                if len(data) < 148:
                    raise OracleError('DDS DX10 header truncated')
                self.dxgi_format, dimension, misc, array_size, misc2 = struct.unpack_from('<5I', data, 128)
                self.data_offset = 148
                self.array_size = array_size
                if self.dxgi_format in DXGI_BLOCKS:
                    self.compression, self.block_bytes = DXGI_BLOCKS[self.dxgi_format]
                self.format_label = 'DX10:%d' % self.dxgi_format
            elif fourcc in FOURCC_BLOCKS:
                self.compression, self.block_bytes = FOURCC_BLOCKS[fourcc]
                self.format_label = fourcc.decode('latin-1')
            else:
                self.format_label = 'FourCC:%s' % fourcc.decode('latin-1', 'replace')
        else:
            self.format_label = 'masks:%d:%08x/%08x/%08x/%08x' % ((bit_count,) + self.masks)
        if self.compression:
            self.level0_bytes = ((width + 3) // 4) * ((height + 3) // 4) * self.block_bytes
        else:
            self.level0_bytes = width * height * (bit_count // 8)

    def level0(self, data):
        end = self.data_offset + self.level0_bytes
        if end > len(data):
            raise OracleError('DDS level 0 needs %d bytes, file has %d' % (end, len(data)))
        return data[self.data_offset:end]


def expand_565(value):
    r = (value >> 11) & 0x1f
    g = (value >> 5) & 0x3f
    b = value & 0x1f
    return np.stack([(r << 3) | (r >> 2), (g << 2) | (g >> 4), (b << 3) | (b >> 2)], axis=-1).astype(np.int32)


def _bc1_blocks(block_bytes, force_four):
    """RGBA for every 4x4 block: shape (blocks, 16, 4). ``force_four`` ignores the c0<=c1 three-color mode."""
    raw = np.frombuffer(block_bytes, dtype=np.uint8).reshape(-1, 8)
    c0 = raw[:, 0].astype(np.int32) | (raw[:, 1].astype(np.int32) << 8)
    c1 = raw[:, 2].astype(np.int32) | (raw[:, 3].astype(np.int32) << 8)
    p0 = expand_565(c0)
    p1 = expand_565(c1)
    four = force_four | (c0 > c1)
    p2 = np.where(four[:, None], (2 * p0 + p1) // 3, (p0 + p1) // 2)
    p3 = np.where(four[:, None], (p0 + 2 * p1) // 3, 0)
    a3 = np.where(four, 255, 0).astype(np.int32)
    palette = np.zeros((raw.shape[0], 4, 4), dtype=np.int32)
    palette[:, 0, :3] = p0
    palette[:, 1, :3] = p1
    palette[:, 2, :3] = p2
    palette[:, 3, :3] = p3
    palette[:, :3, 3] = 255
    palette[:, 3, 3] = a3
    indices = raw[:, 4:8].astype(np.uint32)
    selectors = indices[:, 0] | (indices[:, 1] << 8) | (indices[:, 2] << 16) | (indices[:, 3] << 24)
    shifts = np.arange(16, dtype=np.uint32) * 2
    sel = (selectors[:, None] >> shifts[None, :]) & 3
    return np.take_along_axis(palette, sel[:, :, None].astype(np.int64), axis=1)


def _bc4_channel(block_bytes):
    """The 16 decoded byte values of every 8-byte BC4-style block: shape (blocks, 16)."""
    raw = np.frombuffer(block_bytes, dtype=np.uint8).reshape(-1, 8)
    a0 = raw[:, 0].astype(np.int32)
    a1 = raw[:, 1].astype(np.int32)
    eight = a0 > a1
    palette = np.zeros((raw.shape[0], 8), dtype=np.int32)
    palette[:, 0] = a0
    palette[:, 1] = a1
    for i in range(1, 7):
        palette[:, i + 1] = np.where(eight, ((7 - i) * a0 + i * a1) // 7, 0)
    for i in range(1, 5):
        palette[:, i + 1] = np.where(eight, palette[:, i + 1], ((5 - i) * a0 + i * a1) // 5)
    palette[:, 6] = np.where(eight, palette[:, 6], 0)
    palette[:, 7] = np.where(eight, palette[:, 7], 255)
    bits = np.zeros(raw.shape[0], dtype=np.uint64)
    for i in range(6):
        bits |= raw[:, 2 + i].astype(np.uint64) << np.uint64(8 * i)
    shifts = (np.arange(16, dtype=np.uint64) * np.uint64(3))
    sel = ((bits[:, None] >> shifts[None, :]) & np.uint64(7)).astype(np.int64)
    return np.take_along_axis(palette, sel, axis=1)


def _assemble(blocks_rgba, width, height):
    bw, bh = (width + 3) // 4, (height + 3) // 4
    image = blocks_rgba.reshape(bh, bw, 4, 4, 4).transpose(0, 2, 1, 3, 4).reshape(bh * 4, bw * 4, 4)
    return image[:height, :width].astype(np.uint8)


def decode_numpy(data):
    """This file's own decode of level zero to RGBA8 (H, W, 4)."""
    header = DdsHeader(data)
    if header.is_cube or header.is_volume:
        raise OracleError('cube or volume DDS: not a single 2D surface')
    level = header.level0(data)
    w, h = header.width, header.height
    c = header.compression
    if c == 'BC1':
        return _assemble(_bc1_blocks(level, False), w, h), header
    if c == 'BC2':
        raw = np.frombuffer(level, dtype=np.uint8).reshape(-1, 16)
        rgba = _bc1_blocks(raw[:, 8:16].tobytes(), True)
        alpha_bytes = raw[:, 0:8].astype(np.int32)
        alpha = np.zeros((raw.shape[0], 16), dtype=np.int32)
        for pixel in range(16):
            nibble = (alpha_bytes[:, pixel >> 1] >> ((pixel & 1) * 4)) & 0xf
            alpha[:, pixel] = nibble * 17
        rgba[:, :, 3] = alpha
        return _assemble(rgba, w, h), header
    if c == 'BC3':
        raw = np.frombuffer(level, dtype=np.uint8).reshape(-1, 16)
        rgba = _bc1_blocks(raw[:, 8:16].tobytes(), True)
        rgba[:, :, 3] = _bc4_channel(raw[:, 0:8].tobytes())
        return _assemble(rgba, w, h), header
    if c == 'BC4':
        red = _bc4_channel(level)
        rgba = np.zeros((red.shape[0], 16, 4), dtype=np.int32)
        rgba[:, :, 0] = red
        rgba[:, :, 3] = 255
        return _assemble(rgba, w, h), header
    if c == 'BC5':
        raw = np.frombuffer(level, dtype=np.uint8).reshape(-1, 16)
        rgba = np.zeros((raw.shape[0], 16, 4), dtype=np.int32)
        rgba[:, :, 0] = _bc4_channel(raw[:, 0:8].tobytes())
        rgba[:, :, 1] = _bc4_channel(raw[:, 8:16].tobytes())
        rgba[:, :, 3] = 255
        return _assemble(rgba, w, h), header
    if header.pf_flags & DDPF_FOURCC:
        raise OracleError('unsupported DDS FourCC %s' % header.format_label)
    # Masked uncompressed formats: expand each field to 8 bits by nearest-integer normalization.
    bpp = header.bit_count
    if bpp not in (8, 16, 24, 32):
        raise OracleError('unsupported DDS bit count %d' % bpp)
    count = w * h
    if bpp == 24:
        raw = np.frombuffer(level, dtype=np.uint8, count=count * 3).reshape(count, 3).astype(np.uint32)
        packed = raw[:, 0] | (raw[:, 1] << 8) | (raw[:, 2] << 16)
    else:
        packed = np.frombuffer(level, dtype={8: '<u1', 16: '<u2', 32: '<u4'}[bpp], count=count).astype(np.uint32)
    out = np.zeros((count, 4), dtype=np.uint8)
    masks = header.masks
    luminance = bool(header.pf_flags & DDPF_LUMINANCE)
    alpha_only = bool(header.pf_flags & DDPF_ALPHA) and not (header.pf_flags & (DDPF_RGB | DDPF_LUMINANCE))
    for channel in range(4):
        mask = masks[channel]
        if channel == 3 and not (header.pf_flags & (DDPF_ALPHAPIXELS | DDPF_ALPHA)):
            mask = 0
        if mask == 0:
            if channel == 3:
                out[:, 3] = 255
            elif luminance and channel in (1, 2):
                out[:, channel] = out[:, 0]
            continue
        shift = (mask & -mask).bit_length() - 1
        width_bits = bin(mask >> shift).count('1')
        field = (packed & mask) >> shift
        if width_bits == 8:
            out[:, channel] = field
        elif width_bits < 8:
            out[:, channel] = np.rint(field.astype(np.float64) * 255.0 / ((1 << width_bits) - 1)).astype(np.uint8)
        else:
            raise OracleError('DDS field wider than 8 bits (%d) needs a higher-precision path' % width_bits)
    if alpha_only and masks[3] == 0 and masks[0]:
        out[:, 3] = out[:, 0]
        out[:, 0:3] = 0
    return out.reshape(h, w, 4), header


def decode_pillow(data):
    """Pillow's decode of level zero to RGBA8, and the mode Pillow reported."""
    from PIL import Image
    image = Image.open(io.BytesIO(data))
    image.load()
    mode = image.mode
    rgba = np.asarray(image.convert('RGBA'), dtype=np.uint8)
    if mode == 'L':
        # Pillow returns BC4 as a single luminance band; RGBA conversion copies it to RGB. Keep only R (BC4 red).
        rgba = rgba.copy()
        rgba[:, :, 1] = 0
        rgba[:, :, 2] = 0
    if mode == 'RGB':
        rgba = rgba.copy()
        rgba[:, :, 3] = 255
    return rgba, mode


def decode(data):
    """Decode with Pillow, cross-check with the numpy decoder, and report both.

    Returns (rgba, info) where info records the format label, Pillow's mode, whether each decoder
    succeeded, and the maximum per-channel absolute difference between them.
    """
    header = DdsHeader(data)
    info = {'format': header.format_label, 'compression': header.compression, 'width': header.width,
            'height': header.height, 'mipCount': header.mip_count, 'pillow': None, 'numpy': None,
            'decoderMaxDifference': None, 'decoder': None}
    pillow_rgba = None
    try:
        pillow_rgba, mode = decode_pillow(data)
        info['pillow'] = 'decoded as mode %s' % mode
    except Exception as failure:  # noqa: BLE001 - the receipt records what Pillow could not do
        info['pillow'] = 'failed: %s' % failure
    numpy_rgba = None
    try:
        numpy_rgba, _ = decode_numpy(data)
        info['numpy'] = 'decoded'
    except Exception as failure:  # noqa: BLE001
        info['numpy'] = 'failed: %s' % failure
    if pillow_rgba is not None and numpy_rgba is not None:
        if pillow_rgba.shape == numpy_rgba.shape:
            diff = np.abs(pillow_rgba.astype(np.int16) - numpy_rgba.astype(np.int16))
            info['decoderMaxDifference'] = [int(diff[:, :, c].max()) for c in range(4)]
        else:
            info['decoderMaxDifference'] = 'shape mismatch %s vs %s' % (pillow_rgba.shape, numpy_rgba.shape)
    if pillow_rgba is not None:
        info['decoder'] = 'pillow'
        return pillow_rgba, info
    if numpy_rgba is not None:
        info['decoder'] = 'numpy'
        return numpy_rgba, info
    raise OracleError('neither decoder handled DDS %s: pillow %s; numpy %s' % (header.format_label, info['pillow'], info['numpy']))


def swap_bc1_endpoints(data, block_index=None, minimum_change=2):
    """A copy of a BC1/BC2/BC3 DDS with one block's two color endpoints exchanged (the hop-B decode control).

    Picks the first block at or after ``block_index`` whose exchange moves at least one of its decoded texels by
    ``minimum_change`` or more in some channel, measured with this file's block decoder on level zero (BC1 keeps its
    three-color mode rule, since the exchange can flip it; BC2/BC3 color blocks are always four-color). The image
    check tolerates a difference of 1/255, so a block whose swap changes nothing that far, because its endpoints
    are equal, differ by less than the tolerance after 565 expansion, or are distinguished only by palette entries
    no selector references, could never be detected and is skipped rather than chosen. Returns (mutated bytes, block
    index), or (None, None) when no block qualifies: the control is then not applicable, never an error."""
    header = DdsHeader(data)
    if header.compression not in ('BC1', 'BC2', 'BC3'):
        raise OracleError('endpoint swap needs a BC1/BC2/BC3 image, got %s' % header.compression)
    color_offset = 0 if header.compression == 'BC1' else 8
    raw = np.frombuffer(header.level0(data), dtype=np.uint8).reshape(-1, header.block_bytes)
    color = np.ascontiguousarray(raw[:, color_offset:color_offset + 8])
    swapped = color.copy()
    swapped[:, 0:2] = color[:, 2:4]
    swapped[:, 2:4] = color[:, 0:2]
    force_four = header.compression != 'BC1'
    before = _bc1_blocks(color.tobytes(), force_four)
    after = _bc1_blocks(swapped.tobytes(), force_four)
    change = np.abs(before - after).reshape(len(raw), -1).max(axis=1) if len(raw) else np.zeros(0, dtype=np.int32)
    start = block_index or 0
    candidates = np.flatnonzero(change[start:] >= minimum_change)
    if len(candidates) == 0:
        return None, None
    block = start + int(candidates[0])
    mutated = bytearray(data)
    base = header.data_offset + block * header.block_bytes + color_offset
    c0, c1 = bytes(data[base:base + 2]), bytes(data[base + 2:base + 4])
    mutated[base:base + 2], mutated[base + 2:base + 4] = c1, c0
    return bytes(mutated), block
