"""Disassemble D3D9 shaders from a Bethesda PC SDP file."""
import argparse
import struct
import sys
from pathlib import Path

DEFAULT_SDP = Path(
    "Sample/Full_Builds/Fallout New Vegas (PC Final)/Data/Shaders/shaderpackage003.sdp"
)


def iter_sdp_entries(sdp_path):
    with open(sdp_path, "rb") as f:
        data = f.read()
    count = struct.unpack_from("<I", data, 4)[0]
    offset = 12
    for i in range(count):
        name_bytes = data[offset:offset+256]
        name = name_bytes.split(b'\x00')[0].decode('ascii', errors='replace')
        offset += 256
        size = struct.unpack_from("<I", data, offset)[0]
        shader_offset = offset + 4
        offset += 4 + size
        yield i, name, shader_offset, size, data[shader_offset:shader_offset+size]


def extract_shader(sdp_path, shader_name):
    for _, name, _, _, shader in iter_sdp_entries(sdp_path):
        if name == shader_name:
            return shader
    return None

def parse_ctab_constants(bytecode):
    """Extract constant name -> register mapping from CTAB."""
    # Find CTAB comment
    pos = 4  # skip version
    while pos < len(bytecode) - 8:
        token = struct.unpack_from("<I", bytecode, pos)[0]
        if (token & 0xFFFF) == 0xFFFE:
            comment_dwords = (token >> 16) & 0x7FFF
            comment_start = pos + 4
            comment_data = bytecode[comment_start:comment_start + comment_dwords * 4]
            if comment_data[:4] == b'CTAB':
                return _parse_ctab(comment_data)
            pos = comment_start + comment_dwords * 4
        else:
            pos += 4
    return {}

def _parse_ctab(ctab_data):
    """Parse D3DXSHADER_CONSTANTTABLE."""
    # ctab_data starts with "CTAB" (4 bytes), then the struct
    # All offsets in the struct are relative to start of struct (byte 4 of ctab_data)
    base = 4  # struct starts after "CTAB" tag
    struct_size = struct.unpack_from("<I", ctab_data, base + 0)[0]
    # creator_off = struct.unpack_from("<I", ctab_data, base + 4)[0]
    # version = struct.unpack_from("<I", ctab_data, base + 8)[0]
    num_constants = struct.unpack_from("<I", ctab_data, base + 12)[0]
    const_info_off = struct.unpack_from("<I", ctab_data, base + 16)[0]

    result = {}
    for i in range(num_constants):
        entry_off = base + const_info_off + i * 20
        if entry_off + 20 > len(ctab_data):
            break
        name_off = struct.unpack_from("<I", ctab_data, entry_off)[0]
        reg_set = struct.unpack_from("<H", ctab_data, entry_off + 4)[0]
        reg_idx = struct.unpack_from("<H", ctab_data, entry_off + 6)[0]
        reg_cnt = struct.unpack_from("<H", ctab_data, entry_off + 8)[0]

        # Read name from offset (relative to struct start)
        abs_name_off = base + name_off
        if abs_name_off < len(ctab_data):
            name_end = ctab_data.find(b'\x00', abs_name_off)
            if name_end < 0:
                name_end = len(ctab_data)
            name = ctab_data[abs_name_off:name_end].decode('ascii', errors='replace')
        else:
            name = f"?{i}"

        reg_type = {0: 'b', 1: 'i', 2: 'c', 3: 's'}.get(reg_set, '?')
        result[name] = (reg_type, reg_idx, reg_cnt)
    return result

def disasm_d3d9(bytecode, const_map):
    ops = {
        0x01: 'mov', 0x02: 'add', 0x03: 'sub', 0x04: 'mad',
        0x05: 'mul', 0x06: 'rcp', 0x07: 'rsq', 0x08: 'dp3', 0x09: 'dp4',
        0x0A: 'min', 0x0B: 'max', 0x0C: 'slt', 0x0D: 'sge', 0x0E: 'exp',
        0x0F: 'log', 0x13: 'frc', 0x23: 'abs', 0x24: 'nrm',
        0x20: 'pow', 0x42: 'texld', 0x50: 'cmp', 0x04: 'mad', 0x12: 'lrp',
        0x58: 'dp2add', 0x5E: 'setp',
    }

    # Reverse map: c_register -> name
    c_names = {}
    for name, (rtype, ridx, rcnt) in const_map.items():
        if rtype == 'c':
            for j in range(rcnt):
                c_names[ridx + j] = f"{name}" + (f"[{j}]" if rcnt > 1 else "")
        elif rtype == 's':
            c_names[('s', ridx)] = name

    def decode_reg(token, is_dst=False):
        num = token & 0x7FF
        # Register type is split: low 3 bits at [30:28], high 2 bits at [12:11]
        # Bit 31 is always 1 (parameter token marker) — must mask to 3 bits, not 4
        rtype_lo = (token >> 28) & 0x7
        rtype_hi = (token >> 11) & 0x3
        rtype = rtype_lo | (rtype_hi << 3)

        # D3DSHADER_PARAM_REGISTER_TYPE enum
        type_map = {
            0: 'r',      # D3DSPR_TEMP
            1: 'v',      # D3DSPR_INPUT
            2: 'c',      # D3DSPR_CONST
            3: 'a',      # D3DSPR_ADDR (VS) / D3DSPR_TEXTURE (PS 1.x)
            4: 'rast',   # D3DSPR_RASTOUT
            5: 'aout',   # D3DSPR_ATTROUT
            6: 'o',      # D3DSPR_OUTPUT / D3DSPR_TEXCRDOUT
            7: 'ci',     # D3DSPR_CONSTINT
            8: 'oC',     # D3DSPR_COLOROUT
            9: 'oDepth', # D3DSPR_DEPTHOUT
            10: 's',     # D3DSPR_SAMPLER
            11: 'c',     # D3DSPR_CONST2 (c2048-c4095)
            12: 'c',     # D3DSPR_CONST3 (c4096-c6143)
            13: 'c',     # D3DSPR_CONST4 (c6144-c8191)
            14: 'b',     # D3DSPR_CONSTBOOL
            15: 'aL',    # D3DSPR_LOOP
            17: 'misc',  # D3DSPR_MISCTYPE
            18: 'label', # D3DSPR_LABEL
            19: 'p',     # D3DSPR_PREDICATE
        }
        prefix = type_map.get(rtype, f'?{rtype}_')
        reg_str = f"{prefix}{num}"

        # Annotate named constants
        if rtype == 2 and num in c_names:
            reg_str = f"c{num}/*{c_names[num]}*/"
        elif rtype == 10 and ('s', num) in c_names:
            reg_str = f"s{num}/*{c_names[('s', num)]}*/"

        if is_dst:
            wmask = (token >> 16) & 0xF
            mask_str = ''
            for bit, ch in [(1, 'x'), (2, 'y'), (4, 'z'), (8, 'w')]:
                if wmask & bit:
                    mask_str += ch
            if mask_str and mask_str != 'xyzw':
                reg_str += '.' + mask_str
        else:
            swizzle = (token >> 16) & 0xFF
            comp = 'xyzw'
            s = comp[swizzle & 3] + comp[(swizzle >> 2) & 3] + comp[(swizzle >> 4) & 3] + comp[(swizzle >> 6) & 3]
            if s != 'xyzw':
                reg_str += '.' + s
            negate = (token >> 24) & 0xF
            if negate == 1:
                reg_str = '-' + reg_str

        return reg_str

    lines = []
    pos = 0
    while pos < len(bytecode):
        token = struct.unpack_from("<I", bytecode, pos)[0]

        # Version
        if pos == 0:
            profile_token = (token >> 16) & 0xFFFF
            profile = {0xFFFE: 'vs', 0xFFFF: 'ps'}.get(profile_token, 'shader')
            major = (token >> 8) & 0xFF
            minor = token & 0xFF
            lines.append(f"{profile}_{major}_{minor}")
            pos += 4
            continue

        # End
        if token == 0x0000FFFF:
            lines.append("end")
            break

        # Comment
        if (token & 0xFFFF) == 0xFFFE:
            cdwords = (token >> 16) & 0x7FFF
            pos += 4 + cdwords * 4
            continue

        opcode = token & 0xFFFF
        pos += 4

        # DCL
        if opcode == 0x1F:
            dcl_tok = struct.unpack_from("<I", bytecode, pos)[0]; pos += 4
            dst_tok = struct.unpack_from("<I", bytecode, pos)[0]; pos += 4
            dst = decode_reg(dst_tok, True)
            rtype = ((dst_tok >> 28) & 0x7) | (((dst_tok >> 11) & 0x3) << 3)
            if rtype == 10:  # D3DSPR_SAMPLER
                stype = {2: '2d', 3: 'cube', 4: 'volume'}.get((dcl_tok >> 27) & 0xF, '?')
                lines.append(f"dcl_{stype} {dst}")
            else:
                usage = dcl_tok & 0x1F
                uidx = (dcl_tok >> 16) & 0xF
                unames = {5: 'texcoord', 10: 'color'}
                lines.append(f"dcl_{unames.get(usage, f'u{usage}')}{uidx} {dst}")
            continue

        # DEF (constant definition)
        if opcode == 0x51:
            dst_tok = struct.unpack_from("<I", bytecode, pos)[0]; pos += 4
            f0 = struct.unpack_from("<f", bytecode, pos)[0]; pos += 4
            f1 = struct.unpack_from("<f", bytecode, pos)[0]; pos += 4
            f2 = struct.unpack_from("<f", bytecode, pos)[0]; pos += 4
            f3 = struct.unpack_from("<f", bytecode, pos)[0]; pos += 4
            dst = decode_reg(dst_tok, True)
            lines.append(f"def {dst}, {f0:.6f}, {f1:.6f}, {f2:.6f}, {f3:.6f}")
            continue

        # DEFI
        if opcode == 0x34:
            dst_tok = struct.unpack_from("<I", bytecode, pos)[0]; pos += 4
            i0 = struct.unpack_from("<i", bytecode, pos)[0]; pos += 4
            i1 = struct.unpack_from("<i", bytecode, pos)[0]; pos += 4
            i2 = struct.unpack_from("<i", bytecode, pos)[0]; pos += 4
            i3 = struct.unpack_from("<i", bytecode, pos)[0]; pos += 4
            lines.append(f"defi i{dst_tok & 0x7FF}, {i0}, {i1}, {i2}, {i3}")
            continue

        # DEFB
        if opcode == 0x35:
            dst_tok = struct.unpack_from("<I", bytecode, pos)[0]; pos += 4
            val = struct.unpack_from("<I", bytecode, pos)[0]; pos += 4
            lines.append(f"defb b{dst_tok & 0x7FF}, {val}")
            continue

        # Check for predication (bit 28 of instruction token)
        predicated = (token >> 28) & 1
        pred_token = None
        if predicated:
            # Next token is the predicate source register
            pred_token = struct.unpack_from("<I", bytecode, pos)[0]; pos += 4

        op_name = ops.get(opcode, f'op_{opcode:04x}')
        pred_prefix = f"({decode_reg(pred_token)}) " if predicated and pred_token is not None else ""

        # SETP: setp_comp dst, src0, src1 (D3DSIO_SETP = 0x005E)
        if opcode == 0x5E:
            comp = (token >> 16) & 0x7
            comp_str = {1: 'gt', 2: 'eq', 3: 'ge', 4: 'lt', 5: 'ne', 6: 'le'}.get(comp, f'c{comp}')
            d = struct.unpack_from("<I", bytecode, pos)[0]; pos += 4
            s0 = struct.unpack_from("<I", bytecode, pos)[0]; pos += 4
            s1 = struct.unpack_from("<I", bytecode, pos)[0]; pos += 4
            lines.append(f"setp_{comp_str} {decode_reg(d, True)}, {decode_reg(s0)}, {decode_reg(s1)}")
            continue

        # 1 src ops
        if opcode in (0x01, 0x06, 0x07, 0x0E, 0x0F, 0x13, 0x23, 0x24):
            d = struct.unpack_from("<I", bytecode, pos)[0]; pos += 4
            s = struct.unpack_from("<I", bytecode, pos)[0]; pos += 4
            lines.append(f"{pred_prefix}{op_name} {decode_reg(d, True)}, {decode_reg(s)}")
            continue

        # 2 src ops
        if opcode in (0x02, 0x03, 0x05, 0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x20):
            d = struct.unpack_from("<I", bytecode, pos)[0]; pos += 4
            s0 = struct.unpack_from("<I", bytecode, pos)[0]; pos += 4
            s1 = struct.unpack_from("<I", bytecode, pos)[0]; pos += 4
            lines.append(f"{pred_prefix}{op_name} {decode_reg(d, True)}, {decode_reg(s0)}, {decode_reg(s1)}")
            continue

        # 3 src ops (mad, lrp, cmp, dp2add)
        if opcode in (0x04, 0x12, 0x50, 0x58):
            d = struct.unpack_from("<I", bytecode, pos)[0]; pos += 4
            s0 = struct.unpack_from("<I", bytecode, pos)[0]; pos += 4
            s1 = struct.unpack_from("<I", bytecode, pos)[0]; pos += 4
            s2 = struct.unpack_from("<I", bytecode, pos)[0]; pos += 4
            lines.append(f"{pred_prefix}{op_name} {decode_reg(d, True)}, {decode_reg(s0)}, {decode_reg(s1)}, {decode_reg(s2)}")
            continue

        # texkill (D3DSIO_TEXKILL = 0x41): kill pixel if any component of dst < 0
        if opcode == 0x41:
            d = struct.unpack_from("<I", bytecode, pos)[0]; pos += 4
            lines.append(f"texkill {decode_reg(d, True)}")
            continue

        # tex family (D3DSIO_TEX = 0x42): texld, texldb, texldp, texldd
        # In SM2.x+, instruction length in bits [27:24] of the instruction token
        # distinguishes variants: texld=3 tokens, texldd=5 tokens
        if opcode == 0x42:
            inst_len = (token >> 24) & 0xF  # number of dword tokens after instruction
            d = struct.unpack_from("<I", bytecode, pos)[0]; pos += 4
            s0 = struct.unpack_from("<I", bytecode, pos)[0]; pos += 4
            s1 = struct.unpack_from("<I", bytecode, pos)[0]; pos += 4
            if inst_len >= 5:
                # texldd: dst, texcoord, sampler, ddx, ddy
                s2 = struct.unpack_from("<I", bytecode, pos)[0]; pos += 4
                s3 = struct.unpack_from("<I", bytecode, pos)[0]; pos += 4
                lines.append(f"texldd {decode_reg(d, True)}, {decode_reg(s0)}, {decode_reg(s1)}, {decode_reg(s2)}, {decode_reg(s3)}")
            else:
                lines.append(f"texld {decode_reg(d, True)}, {decode_reg(s0)}, {decode_reg(s1)}")
            continue

        # IF/IFC/ELSE/ENDIF
        if opcode == 0x29:
            s = struct.unpack_from("<I", bytecode, pos)[0]; pos += 4
            lines.append(f"if {decode_reg(s)}")
            continue
        if opcode == 0x2A:
            s0 = struct.unpack_from("<I", bytecode, pos)[0]; pos += 4
            s1 = struct.unpack_from("<I", bytecode, pos)[0]; pos += 4
            comp = (token >> 16) & 0x7
            comp_str = {1: 'gt', 2: 'eq', 3: 'ge', 4: 'lt', 5: 'ne', 6: 'le'}.get(comp, f'c{comp}')
            lines.append(f"if_{comp_str} {decode_reg(s0)}, {decode_reg(s1)}")
            continue
        if opcode in (0x2B, 0x2C):
            lines.append(op_name)
            continue
        if opcode in (0x2E, 0x2F, 0x31, 0x32):
            if opcode in (0x2E, 0x31):
                s = struct.unpack_from("<I", bytecode, pos)[0]; pos += 4
                lines.append(f"{op_name} {decode_reg(s)}")
            else:
                lines.append(op_name)
            continue

        lines.append(f"; UNKNOWN opcode 0x{opcode:04x}")
        break

    return lines

def format_shader_disassembly(sdp_path, name):
    shader = extract_shader(sdp_path, name)
    if not shader:
        raise ValueError(f"Shader {name} not found in {sdp_path}")

    const_map = parse_ctab_constants(shader)
    lines = [f"; === {name} ({len(shader)} bytes) ===", f"; Constants: {const_map}", ""]
    lines.extend(disasm_d3d9(shader, const_map))
    return lines


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("shader", nargs="*", help="Shader record name(s), such as SLS1040.pso")
    parser.add_argument("--sdp", default=str(DEFAULT_SDP), help="PC shaderpackage*.sdp path")
    parser.add_argument("--list", action="store_true", help="List shader records instead of disassembling")
    parser.add_argument("--contains", help="Filter --list by a case-insensitive substring")
    parser.add_argument("--out", help="Optional output text file")
    args = parser.parse_args()

    sdp_path = Path(args.sdp)
    if args.list:
        needle = args.contains.lower() if args.contains else None
        lines = []
        for index, name, offset, size, _ in iter_sdp_entries(sdp_path):
            if needle and needle not in name.lower():
                continue
            lines.append(f"[{index:3}] file+0x{offset:06X} size=0x{size:04X} {name}")
    else:
        if not args.shader:
            parser.error("provide at least one shader name, or use --list")

        lines = []
        for name in args.shader:
            if lines:
                lines.append("")
            lines.extend(format_shader_disassembly(sdp_path, name))

    text = "\n".join(lines)
    if args.out:
        Path(args.out).parent.mkdir(parents=True, exist_ok=True)
        Path(args.out).write_text(text + "\n", encoding="utf-8")
    else:
        print(text)


if __name__ == '__main__':
    try:
        main()
    except ValueError as exc:
        print(exc, file=sys.stderr)
        sys.exit(1)
