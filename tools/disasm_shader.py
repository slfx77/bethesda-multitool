"""Disassemble D3D9 shaders from a Bethesda PC SDP file."""
import argparse
import datetime
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
    # D3DSHADER_INSTRUCTION_OPCODE_TYPE (d3d9types.h): opcode -> (mnemonic, has_dst).
    # operand[0] is the destination when has_dst; the remaining operands are sources.
    # Source COUNT is taken from the instruction-length field, not hard-coded — that is
    # what keeps the stream aligned through opcodes this table does not name.
    OPCODES = {
        0x00: ('nop', False),
        0x01: ('mov', True), 0x02: ('add', True), 0x03: ('sub', True), 0x04: ('mad', True),
        0x05: ('mul', True), 0x06: ('rcp', True), 0x07: ('rsq', True), 0x08: ('dp3', True),
        0x09: ('dp4', True), 0x0A: ('min', True), 0x0B: ('max', True), 0x0C: ('slt', True),
        0x0D: ('sge', True), 0x0E: ('exp', True), 0x0F: ('log', True), 0x10: ('lit', True),
        0x11: ('dst', True), 0x12: ('lrp', True), 0x13: ('frc', True),
        0x14: ('m4x4', True), 0x15: ('m4x3', True), 0x16: ('m3x4', True),
        0x17: ('m3x3', True), 0x18: ('m3x2', True),
        0x19: ('call', False), 0x1A: ('callnz', False), 0x1B: ('loop', False),
        0x1C: ('ret', False), 0x1D: ('endloop', False), 0x1E: ('label', False),
        0x20: ('pow', True), 0x21: ('crs', True), 0x22: ('sgn', True), 0x23: ('abs', True),
        0x24: ('nrm', True), 0x25: ('sincos', True), 0x26: ('rep', False), 0x27: ('endrep', False),
        0x28: ('if', False), 0x29: ('ifc', False), 0x2A: ('else', False), 0x2B: ('endif', False),
        0x2C: ('break', False), 0x2D: ('breakc', False), 0x2E: ('mova', True),
        0x40: ('texcoord', True), 0x41: ('texkill', True), 0x42: ('texld', True),
        0x4E: ('expp', True), 0x4F: ('logp', True), 0x50: ('cnd', True),
        0x58: ('cmp', True), 0x5A: ('dp2add', True), 0x5B: ('dsx', True), 0x5C: ('dsy', True),
        0x5D: ('texldd', True), 0x5E: ('setp', True), 0x5F: ('texldl', True),
        0x60: ('breakp', False),
    }
    # Opcodes whose instruction token carries a comparison selector in bits [18:16].
    CMP_SUFFIX = {1: 'gt', 2: 'eq', 3: 'ge', 4: 'lt', 5: 'ne', 6: 'le'}
    # Special opcodes whose operands are NOT plain register tokens (immediates / usage).
    OP_DCL, OP_DEFB, OP_DEFI, OP_DEF = 0x1F, 0x2F, 0x30, 0x51

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
    sm_major = 0
    n = len(bytecode)
    while pos + 4 <= n:
        token = struct.unpack_from("<I", bytecode, pos)[0]

        # Version token (first dword).
        if pos == 0:
            profile = {0xFFFE: 'vs', 0xFFFF: 'ps'}.get((token >> 16) & 0xFFFF, 'shader')
            sm_major = (token >> 8) & 0xFF
            lines.append(f"{profile}_{sm_major}_{token & 0xFF}")
            pos += 4
            continue

        # End token.
        if token == 0x0000FFFF:
            lines.append("end")
            break

        # Comment block (CTAB etc.) — skip its payload.
        if (token & 0xFFFF) == 0xFFFE:
            pos += 4 + ((token >> 16) & 0x7FFF) * 4
            continue

        opcode = token & 0xFFFF
        predicated = ((token >> 28) & 1) if sm_major >= 2 else 0
        pos += 4

        # Read the operand tokens and ALWAYS advance past exactly them, so an opcode we do not
        # recognize is skipped at the right length instead of desyncing the stream. Special
        # opcodes have fixed layouts; otherwise SM2.0+ carries an instruction-length field while
        # SM1.x has none, so its operands are walked by the parameter-token marker (bit 31).
        op_toks = []
        fixed = {OP_DCL: 2, OP_DEF: 5, OP_DEFI: 5, OP_DEFB: 2}.get(opcode)
        if fixed is not None:
            for _ in range(fixed):
                if pos + 4 > n:
                    break
                op_toks.append(struct.unpack_from("<I", bytecode, pos)[0])
                pos += 4
        elif sm_major >= 2:
            for _ in range((token >> 24) & 0xF):
                if pos + 4 > n:
                    break
                op_toks.append(struct.unpack_from("<I", bytecode, pos)[0])
                pos += 4
        else:  # SM1.x — consume following parameter tokens (high bit set)
            while pos + 4 <= n and (struct.unpack_from("<I", bytecode, pos)[0] & 0x80000000):
                op_toks.append(struct.unpack_from("<I", bytecode, pos)[0])
                pos += 4
        inst_len = len(op_toks)

        # dcl: [usage/sampler token][destination register]
        if opcode == OP_DCL and len(op_toks) >= 2:
            dcl_tok, dst_tok = op_toks[0], op_toks[1]
            dst = decode_reg(dst_tok, True)
            rtype = ((dst_tok >> 28) & 0x7) | (((dst_tok >> 11) & 0x3) << 3)
            if rtype == 10:  # D3DSPR_SAMPLER
                stype = {2: '2d', 3: 'cube', 4: 'volume'}.get((dcl_tok >> 27) & 0xF, '?')
                lines.append(f"dcl_{stype} {dst}")
            else:
                usage = dcl_tok & 0x1F
                uidx = (dcl_tok >> 16) & 0xF
                unames = {0: 'position', 1: 'blendweight', 2: 'blendindices', 3: 'normal',
                          5: 'texcoord', 6: 'tangent', 7: 'binormal', 10: 'color'}
                lines.append(f"dcl_{unames.get(usage, f'u{usage}')}{uidx} {dst}")
            continue

        # def (float) / defi (int): [dst][4 immediates]
        if opcode == OP_DEF and len(op_toks) >= 5:
            f = struct.unpack("<4f", struct.pack("<4I", *op_toks[1:5]))
            lines.append(f"def {decode_reg(op_toks[0], True)}, {f[0]:.6f}, {f[1]:.6f}, {f[2]:.6f}, {f[3]:.6f}")
            continue
        if opcode == OP_DEFI and len(op_toks) >= 5:
            i = struct.unpack("<4i", struct.pack("<4I", *op_toks[1:5]))
            lines.append(f"defi i{op_toks[0] & 0x7FF}, {i[0]}, {i[1]}, {i[2]}, {i[3]}")
            continue
        if opcode == OP_DEFB and len(op_toks) >= 2:
            lines.append(f"defb b{op_toks[0] & 0x7FF}, {op_toks[1]}")
            continue

        mnemonic, has_dst = OPCODES.get(opcode, (f"op_{opcode:04x}", True))
        # Comparison selector for ifc / breakc / setp (bits [18:16] of the opcode token).
        if opcode in (0x29, 0x2D, 0x5E):
            mnemonic += "_" + CMP_SUFFIX.get((token >> 16) & 0x7, f"c{(token >> 16) & 0x7}")

        # The predicate source register (when present) sits right after the destination.
        pred_prefix = ""
        operands = op_toks
        if predicated and operands:
            pidx = 1 if has_dst else 0
            if pidx < len(operands):
                pred_prefix = f"({decode_reg(operands[pidx])}) "
                operands = operands[:pidx] + operands[pidx + 1:]

        rendered = ", ".join(
            decode_reg(t, is_dst=(has_dst and idx == 0)) for idx, t in enumerate(operands))
        text = f"{pred_prefix}{mnemonic} {rendered}".rstrip()
        if opcode not in OPCODES:
            text = f"; UNKNOWN opcode 0x{opcode:04x} (len {inst_len}): {text}"
        lines.append(text)

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
    parser.add_argument("--all", action="store_true",
                        help="Disassemble every shader (optionally filtered by --prefix) into one artifact")
    parser.add_argument("--prefix",
                        help="Comma-separated name prefixes for --all (e.g. 'ST,DISTTREE'); case-insensitive")
    parser.add_argument("--out", help="Optional output text file")
    args = parser.parse_args()

    sdp_path = Path(args.sdp)
    if args.all:
        prefixes = [p.strip().upper() for p in (args.prefix or "").split(",") if p.strip()]
        entries = [
            (name, size) for _, name, _, size, _ in iter_sdp_entries(sdp_path)
            if not prefixes or any(name.upper().startswith(p) for p in prefixes)
        ]
        lines = [
            "=== SpeedTree PC shader disassembly ===",
            f"Date: {datetime.datetime.now().isoformat(timespec='seconds')}",
            f"Package: {sdp_path}",
            f"Shaders: {len(entries)}" + (f" (prefix filter: {','.join(prefixes)})" if prefixes else ""),
            "",
        ]
        for name, _ in entries:
            lines.append("")
            try:
                lines.extend(format_shader_disassembly(sdp_path, name))
            except Exception as exc:  # noqa: BLE001 - keep batch going past a single bad shader
                lines.append(f"; ERROR disassembling {name}: {exc}")
    elif args.list:
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
