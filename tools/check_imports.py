#!/usr/bin/env python3
"""Read PE DLL imports without third-party Python modules; fail on dynamic VC runtime.

Checks only early DLL dependencies, not compatibility with Discord or hook safety.
"""
from pathlib import Path
import struct
import sys


RUNTIME_PREFIXES = ('msvcp140', 'vcruntime140', 'concrt140')


def imports(path: Path) -> list[str]:
    data = path.read_bytes()
    if len(data) < 512 or data[:2] != b'MZ':
        raise ValueError('not an MZ executable')
    pe = struct.unpack_from('<I', data, 0x3C)[0]
    if data[pe:pe + 4] != b'PE\0\0':
        raise ValueError('missing PE signature')
    machine, count, _, _, _, opt_size, _ = struct.unpack_from('<HHIIIHH', data, pe + 4)
    if machine != 0x8664:
        raise ValueError('expected Windows x64')
    opt = pe + 24
    if struct.unpack_from('<H', data, opt)[0] != 0x20B:
        raise ValueError('expected PE32+')
    import_rva, _ = struct.unpack_from('<II', data, opt + 120)
    if not import_rva:
        return []
    sections = []
    for i in range(count):
        off = opt + opt_size + i * 40
        virtual_size, virtual_address, raw_size, raw_start = struct.unpack_from('<IIII', data, off + 8)
        sections.append((virtual_address, max(virtual_size, raw_size), raw_start))

    def offset(rva: int) -> int:
        for addr, size, raw in sections:
            if addr <= rva < addr + size:
                off = raw + (rva - addr)
                if off >= len(data):
                    raise ValueError('PE offset out of range')
                return off
        raise ValueError(f'invalid PE RVA: {rva:#x}')

    result = []
    desc = offset(import_rva)
    while True:
        original, ts, chain, name_rva, first = struct.unpack_from('<IIIII', data, desc)
        if not any((original, ts, chain, name_rva, first)):
            break
        name_start = offset(name_rva)
        name_end = data.index(b'\0', name_start)
        result.append(data[name_start:name_end].decode('ascii'))
        desc += 20
        if desc + 20 > len(data):
            raise ValueError('import descriptor overflow')
    return result


if __name__ == '__main__':
    try:
        if len(sys.argv) != 2:
            raise ValueError('Usage: check_imports.py path/to/DWrite.dll')
        path = Path(sys.argv[1])
        libraries = imports(path)
        print('PE imports:', ', '.join(libraries))
        dynamic = [dll for dll in libraries if dll.lower().startswith(RUNTIME_PREFIXES)]
        if dynamic:
            raise ValueError(f'Loader requires external Microsoft VC runtime: {dynamic}')
        print('OK: no MSVCP140/VCRUNTIME140/CONCRT140 imports')
    except (ValueError, OSError, IndexError, struct.error) as err:
        sys.exit(f'PE import validation failed: {err}')
