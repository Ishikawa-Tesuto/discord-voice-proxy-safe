#!/usr/bin/env python3
"""Minimal dependency-free PE x64 DLL export check, not a runtime test."""
import struct
import sys
from pathlib import Path


def check(path: Path, export: str):
    data = path.read_bytes()
    if len(data) < 512 or data[:2] != b'MZ':
        raise ValueError('not a Windows PE file')
    pe = struct.unpack_from('<I', data, 0x3C)[0]
    if data[pe:pe+4] != b'PE\0\0':
        raise ValueError('missing PE signature')
    machine, section_count, _, _, _, opt_size, characteristics = struct.unpack_from('<HHIIIHH', data, pe + 4)
    if machine != 0x8664:
        raise ValueError(f'not x64 (machine=0x{machine:x})')
    if not (characteristics & 0x2000):
        raise ValueError('not a DLL')
    opt = pe + 24
    if struct.unpack_from('<H', data, opt)[0] != 0x20B:
        raise ValueError('not PE32+ executable format')
    rva, size = struct.unpack_from('<II', data, opt + 112)
    if not rva or not size:
        raise ValueError('no PE exports')
    sections = []
    for i in range(section_count):
        off = opt + opt_size + i * 40
        vsz, addr, rsz, raw = struct.unpack_from('<IIII', data, off + 8)
        sections.append((addr, max(vsz, rsz), raw))

    def offset(rva):
        for start, size, raw in sections:
            if start <= rva < start + size:
                return raw + (rva - start)
        raise ValueError(f'RVA out of PE sections: 0x{rva:x}')

    exp = offset(rva)
    names_count = struct.unpack_from('<I', data, exp + 24)[0]
    names_rva = struct.unpack_from('<I', data, exp + 32)[0]
    names_off = offset(names_rva)
    exports = []
    for idx in range(names_count):
        nrva = struct.unpack_from('<I', data, names_off + idx*4)[0]
        noff = offset(nrva)
        exports.append(data[noff:data.index(b'\0', noff)].decode('ascii'))
    if export not in exports:
        raise ValueError(f'missing {export} export; exported names: {exports}')
    print(f'OK {path}: Windows x64 DLL; exported {export}')


if __name__ == '__main__':
    try:
        if len(sys.argv) != 3:
            raise ValueError('Usage: verify_pe.py DLL required_export')
        check(Path(sys.argv[1]), sys.argv[2])
    except (ValueError, OSError, struct.error) as exc:
        sys.exit(f'PE validation failed: {exc}')
