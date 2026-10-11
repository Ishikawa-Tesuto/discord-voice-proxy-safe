#!/usr/bin/env python3
"""Adjust the upstream VS project once to compile C++ DWriteCreateFactory instead of proxy.asm.
Safe to run again. Intended for the user's fork, not a separate checkout.
"""
from pathlib import Path
import sys

root = Path(__file__).resolve().parent.parent
project = root / 'proxy-dll' / 'discord-voice-proxy.vcxproj'
if not project.is_file():
    sys.exit('Missing proxy-dll/discord-voice-proxy.vcxproj: run inside the original fork')
s = project.read_text(encoding='utf-8-sig')
old = '''  <ItemGroup>
    <MASM Include="proxy.asm">
      <FileType>Document</FileType>
    </MASM>
  </ItemGroup>
'''
if old in s:
    if s.count(old) != 1:
        sys.exit('Unexpected extra MASM project entries')
    s = s.replace(old, '', 1)
# MASM custom imports are no longer required and may not be available on a CI image.
s = s.replace('    <Import Project="$(VCTargetsPath)\\BuildCustomizations\\masm.props" />\n', '')
s = s.replace('    <Import Project="$(VCTargetsPath)\\BuildCustomizations\\masm.targets" />\n', '')
if '<MASM Include=' in s:
    sys.exit('Unrecognized MASM project entry; refusing to patch')
if '<ClCompile Include="dllmain.cpp" />' not in s:
    sys.exit('Expected dllmain.cpp entry absent; upstream project changed')
project.write_text(s, encoding='utf-8')
print('OK: loader project uses C++ export and no proxy.asm')
