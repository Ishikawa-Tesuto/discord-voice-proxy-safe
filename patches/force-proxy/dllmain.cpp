// Discord Voice Proxy Safe (experimental patch), 2026-10-11.
// Based on https://github.com/runetfreedom/force-proxy (GPL-3.0).
// Hooks MUST be initialized by StartForceProxy, outside DllMain.

#include "stdafx.h"
#pragma comment(lib, "ws2_32.lib")
#pragma comment(lib, "detours.lib")

IN_ADDR g_ProxyAddress;
uint16_t g_ProxyPort;
uint32_t g_ProxyTimeout;
char g_ProxyLogin[256];
char g_ProxyPassword[256];

static INIT_ONCE g_once = INIT_ONCE_STATIC_INIT;
static BOOL g_started = FALSE;

static BOOL CALLBACK InitProxy(PINIT_ONCE, PVOID, PVOID*) {
    char addressBuff[MAX_PATH] = "127.0.0.1";
    char portBuff[MAX_PATH] = "12334";
    char timeoutBuff[MAX_PATH] = {};
    g_ProxyLogin[0] = '\0';
    g_ProxyPassword[0] = '\0';
    GetEnvironmentVariableA("SOCKS5_PROXY_ADDRESS", addressBuff, sizeof(addressBuff));
    GetEnvironmentVariableA("SOCKS5_PROXY_PORT", portBuff, sizeof(portBuff));
    GetEnvironmentVariableA("SOCKS5_PROXY_LOGIN", g_ProxyLogin, sizeof(g_ProxyLogin));
    GetEnvironmentVariableA("SOCKS5_PROXY_PASSWORD", g_ProxyPassword, sizeof(g_ProxyPassword));

    // Refuse invalid proxy configuration rather than installing broken hooks.
    char* portEnd = nullptr;
    const long port = strtol(portBuff, &portEnd, 10);
    if (!portEnd || *portEnd || port < 1 || port > 65535 ||
        inet_pton(AF_INET, addressBuff, &g_ProxyAddress) != 1) {
        OutputDebugStringA("DVP Safe: invalid SOCKS5 address or port\n");
        return TRUE;
    }
    g_ProxyPort = htons(static_cast<uint16_t>(port));

    const DWORD timeoutLength = GetEnvironmentVariableA("SOCKS5_PROXY_TIMEOUT", timeoutBuff, sizeof(timeoutBuff));
    char* timeoutEnd = nullptr;
    const long seconds = strtol(timeoutBuff, &timeoutEnd, 10);
    g_ProxyTimeout = timeoutLength > 0 && timeoutLength < sizeof(timeoutBuff) &&
        timeoutEnd && *timeoutEnd == '\0' && seconds > 0 && seconds <= 300
        ? static_cast<uint32_t>(seconds) : 30;

    InitHooks(); // Still uses the upstream Detours hooks; see diagnostics README.
    g_started = TRUE;
    return TRUE;
}

extern "C" __declspec(dllexport) BOOL WINAPI StartForceProxy() {
    if (DetourIsHelperProcess()) return FALSE;
    if (!InitOnceExecuteOnce(&g_once, InitProxy, nullptr, nullptr)) return FALSE;
    return g_started;
}

// Deliberately no Detours attach/detach under the Windows loader lock.
// This DLL must remain loaded until process termination after StartForceProxy.
BOOL WINAPI DllMain(HMODULE self, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) DisableThreadLibraryCalls(self);
    return TRUE;
}
