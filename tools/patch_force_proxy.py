#!/usr/bin/env python3
"""Apply experimental Winsock safety patch to a fresh checked-out force-proxy.
Patches intentionally fail on upstream drift; no silent partial builds.
"""
from pathlib import Path
import sys

source_root = Path(__file__).resolve().parent.parent
if len(sys.argv) != 2:
    sys.exit('Usage: python tools/patch_force_proxy.py _deps/force-proxy')
checkout = Path(sys.argv[1]).resolve()
source = checkout / 'src/hooks.cpp'
dllmain = checkout / 'src/dllmain.cpp'
if not source.is_file() or not dllmain.is_file():
    sys.exit('force-proxy source files not found')

s = source.read_text(encoding='utf-8-sig')

def replace_once(old, new, desc):
    global s
    count = s.count(old)
    if count != 1:
        sys.exit(f'Unexpected upstream for {desc}: expected one match, got {count}')
    s = s.replace(old, new, 1)

replace_once(
    'bool IsMultiCastAddr(const sockaddr *addr)\n{\n\tuint32_t ip = ntohl(((SOCKADDR_IN*)addr)->sin_addr.s_addr);',
    'bool IsMultiCastAddr(const sockaddr *addr)\n{\n\tif (addr == nullptr || addr->sa_family != AF_INET) return false;\n\tuint32_t ip = ntohl(((SOCKADDR_IN*)addr)->sin_addr.s_addr);',
    'multicast address check')
replace_once('if (cmd == FIONBIO) {', 'if (cmd == FIONBIO && argp != nullptr) {', 'ioctlsocket argument')
replace_once(
    'int WINAPI Mine_connect(SOCKET s, const sockaddr* name, int namelen)\n{\n\tconst struct sockaddr_in* addr_in',
    'int WINAPI Mine_connect(SOCKET s, const sockaddr* name, int namelen)\n{\n\tif (!name || namelen < static_cast<int>(sizeof(sockaddr_in)) || name->sa_family != AF_INET)\n\t\treturn Real_connect(s, name, namelen);\n\tconst struct sockaddr_in* addr_in',
    'connect address validation')
replace_once(
    'int WINAPI Mine_sendto(SOCKET s, const char* buf, int len, int flags, const struct sockaddr* to, int tolen)\n{\n',
    'int WINAPI Mine_sendto(SOCKET s, const char* buf, int len, int flags, const struct sockaddr* to, int tolen)\n{\n'
    '\tif (!to || tolen < static_cast<int>(sizeof(sockaddr_in)) || to->sa_family != AF_INET) {\n'
    '\t\tif (SocketExistsInUdpAssociationMap(s)) { WSASetLastError(WSAEAFNOSUPPORT); return SOCKET_ERROR; }\n'
    '\t\treturn Real_sendto(s, buf, len, flags, to, tolen);\n\t}\n',
    'sendto destination validation')
replace_once(
    'int iTolen,\n\tLPWSAOVERLAPPED lpOverlapped,\n\tLPWSAOVERLAPPED_COMPLETION_ROUTINE lpCompletionRoutine\n)\n{\n\tudp_association_entry_t entryCopy;',
    'int iTolen,\n\tLPWSAOVERLAPPED lpOverlapped,\n\tLPWSAOVERLAPPED_COMPLETION_ROUTINE lpCompletionRoutine\n)\n{\n'
    '\tconst bool unsupported = lpOverlapped != nullptr || !lpTo || iTolen < static_cast<int>(sizeof(sockaddr_in)) || lpTo->sa_family != AF_INET || dwBufferCount != 1 || !lpBuffers || !lpBuffers[0].buf;\n'
    '\tif (unsupported) {\n'
    '\t\tif (SocketExistsInUdpAssociationMap(s)) { WSASetLastError(WSAEOPNOTSUPP); return SOCKET_ERROR; }\n'
    '\t\treturn Real_WSASendTo(s, lpBuffers, dwBufferCount, lpNumberOfBytesSent, dwFlags, lpTo, iTolen, lpOverlapped, lpCompletionRoutine);\n'
    '\t}\n\tudp_association_entry_t entryCopy;',
    'unsupported asynchronous WSASendTo')
replace_once(
    '\t\tExtractSockAddr(buf, from);\n\n\t\tmemmove(buf, &buf[10], received -= 10);',
    '\t\tif (from && fromlen && *fromlen >= static_cast<int>(sizeof(sockaddr_in)))\n\t\t\tExtractSockAddr(buf, from);\n\n\t\tmemmove(buf, &buf[10], received -= 10);',
    'optional recvfrom source address')
replace_once(
    ')\n{\n\tDWORD received = 0;\n\tif (lpNumberOfBytesRecvd == NULL)',
    ')\n{\n'
    '\tif (lpOverlapped != nullptr) {\n'
    '\t\tif (SocketExistsInUdpAssociationMap(s)) {\n'
    '\t\t\tWSASetLastError(WSAEOPNOTSUPP); return SOCKET_ERROR;\n'
    '\t\t}\n'
    '\t\treturn Real_WSARecvFrom(s, lpBuffers, dwBufferCount, lpNumberOfBytesRecvd, lpFlags, lpFrom, lpFromlen, lpOverlapped, lpCompletionRoutine);\n'
    '\t}\n'
    '\tDWORD received = 0;\n\tif (lpNumberOfBytesRecvd == NULL)',
    'unsupported asynchronous WSARecvFrom')
replace_once(
    '\t\tExtractSockAddr(lpBuffers->buf, lpFrom);\n\n\t\tmemmove(lpBuffers->buf, &lpBuffers->buf[10], *lpNumberOfBytesRecvd -= 10);',
    '\t\tif (!lpBuffers || dwBufferCount == 0 || !lpBuffers[0].buf) {\n'
    '\t\t\tWSASetLastError(WSAEINVAL); return SOCKET_ERROR;\n\t\t}\n'
    '\t\tif (lpFrom && lpFromlen && *lpFromlen >= static_cast<int>(sizeof(sockaddr_in)))\n'
    '\t\t\tExtractSockAddr(lpBuffers->buf, lpFrom);\n\n'
    '\t\tmemmove(lpBuffers->buf, &lpBuffers->buf[10], *lpNumberOfBytesRecvd -= 10);',
    'optional WSARecvFrom buffers')
patched_main = source_root / 'patches/force-proxy/dllmain.cpp'
if not patched_main.is_file():
    sys.exit('Missing replacement patches/force-proxy/dllmain.cpp')
# Only change the checkout after all patterns were validated.
source.write_text(s, encoding='utf-8')
dllmain.write_bytes(patched_main.read_bytes())
print('Patched force-proxy network source')
