#!/usr/bin/env python3
"""Force /MT for the x64 Release DWrite shim, eliminating early VC runtime imports.

This modifies the project in CI; force-proxy still uses /MD to match vcpkg Detours.
"""
from pathlib import Path
import sys

project = Path(sys.argv[1]) if len(sys.argv) == 2 else Path('proxy-dll/discord-voice-proxy.vcxproj')
if not project.is_file():
    sys.exit(f'Missing VC++ project: {project}')
text = project.read_text(encoding='utf-8-sig')
condition = '<ItemDefinitionGroup Condition="\'$(Configuration)|$(Platform)\'==\'Release|x64\'">'
if text.count(condition) != 1:
    sys.exit('Expected exactly one x64 Release ItemDefinitionGroup')
start = text.index(condition)
end = text.find('</ItemDefinitionGroup>', start)
if end == -1:
    sys.exit('Release|x64 group not closed')
group = text[start:end]
if '<ClCompile>' not in group or '</ClCompile>' not in group:
    sys.exit('Could not locate ClCompile settings')
if '<RuntimeLibrary>' in group:
    if '<RuntimeLibrary>MultiThreaded</RuntimeLibrary>' not in group:
        sys.exit('Conflicting RuntimeLibrary setting in Release|x64')
else:
    group = group.replace('<ClCompile>', '<ClCompile>\n      <RuntimeLibrary>MultiThreaded</RuntimeLibrary>', 1)
    text = text[:start] + group + text[end:]
    project.write_text(text, encoding='utf-8')
print(f'OK {project}: x64 Release loader uses static /MT runtime')
