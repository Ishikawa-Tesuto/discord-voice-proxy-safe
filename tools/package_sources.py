#!/usr/bin/env python3
"""Include both full trees, plus patches/build instructions, alongside GPL redistribution."""
from pathlib import Path
from zipfile import ZipFile, ZIP_DEFLATED
import sys

root = Path(__file__).resolve().parent.parent
if len(sys.argv) != 3:
    sys.exit('Usage: python tools/package_sources.py <force-proxy-dir> <out.zip>')
network = Path(sys.argv[1]).resolve()
out = Path(sys.argv[2]).resolve()
exclude_dirs = {'.git', '.vs', '_deps', 'x64', 'x86', 'Debug', 'Release', 'packages', 'vcpkg_installed', '__pycache__', 'obj', 'bin', 'build', 'dist'}

def add_tree(zf, folder, prefix):
    for path in sorted(folder.rglob('*')):
        rel = path.relative_to(folder)
        if any(part in exclude_dirs for part in rel.parts):
            continue
        if path.is_file():
            zf.write(path, (Path(prefix) / rel).as_posix())

if not (network / 'src/dllmain.cpp').is_file():
    sys.exit('Missing force-proxy checkout')
out.parent.mkdir(parents=True, exist_ok=True)
with ZipFile(out, 'w', ZIP_DEFLATED) as zf:
    add_tree(zf, root, 'discord-voice-proxy')
    add_tree(zf, network, 'force-proxy')
print(out)
