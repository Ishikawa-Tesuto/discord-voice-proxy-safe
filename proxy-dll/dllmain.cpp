// Discord Voice Proxy Safe (experimental patch), 2026-10-11.
// Derived from https://github.com/runetfreedom/discord-voice-proxy (GPL-3.0).
// No networking, detours, file IO, or additional DLL loading in DllMain.

#include <windows.h>
#define DWRITE_EXPORT __declspec(dllexport) WINAPI
#include <dwrite.h>  // Avoid dllimport while defining our DWriteCreateFactory export.
#include <filesystem>
#include <fstream>
#include <iterator>
#include <string>
#include <system_error>

namespace fs = std::filesystem;
namespace {
HMODULE g_self = nullptr;
HMODULE g_real_dwrite = nullptr;
HMODULE g_network_proxy = nullptr;
INIT_ONCE g_real_once = INIT_ONCE_STATIC_INIT;
INIT_ONCE g_proxy_once = INIT_ONCE_STATIC_INIT;

using FactoryFn = HRESULT (WINAPI*)(DWRITE_FACTORY_TYPE, REFIID, IUnknown**);
FactoryFn g_real_factory = nullptr;

fs::path SelfDirectory() {
    wchar_t module_path[32768] = {};
    DWORD length = GetModuleFileNameW(g_self, module_path, static_cast<DWORD>(std::size(module_path)));
    if (!length || length >= std::size(module_path)) return {};
    return fs::path(std::wstring(module_path, length)).parent_path();
}

void Log(const char* message, DWORD code = ERROR_SUCCESS) noexcept {
    try {
        wchar_t app_data[32768] = {};
        DWORD length = GetEnvironmentVariableW(L"LOCALAPPDATA", app_data, static_cast<DWORD>(std::size(app_data)));
        if (!length || length >= std::size(app_data)) return;
        fs::path folder = fs::path(std::wstring(app_data, length)) / L"DVP-Safe";
        std::error_code ec;
        fs::create_directories(folder, ec);
        if (ec) return;
        std::ofstream file(folder / L"loader.log", std::ios::app);
        if (!file) return;
        SYSTEMTIME now;
        GetLocalTime(&now);
        file << now.wYear << '-' << now.wMonth << '-' << now.wDay << ' '
             << now.wHour << ':' << now.wMinute << ':' << now.wSecond
             << " [" << GetCurrentProcessId() << "] " << message;
        if (code) file << " (Win32=" << code << ')';
        file << '\n';
    } catch (...) {
        // Diagnostic logging is never allowed to break Discord.
    }
}

BOOL CALLBACK InitRealDWrite(PINIT_ONCE, PVOID, PVOID*) {
    Log("DWriteCreateFactory reached: system factory initialization starting");
    g_real_dwrite = LoadLibraryExW(L"DWrite.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
    if (!g_real_dwrite) {
        Log("system DWrite.dll could not be loaded", GetLastError());
        return TRUE; // Cache failure and fail the factory call gracefully.
    }
    g_real_factory = reinterpret_cast<FactoryFn>(GetProcAddress(g_real_dwrite, "DWriteCreateFactory"));
    if (!g_real_factory) Log("DWriteCreateFactory export missing", GetLastError());
    else Log("system DWriteCreateFactory resolved");
    return TRUE;
}

std::string Trim(std::string s) {
    const auto a = s.find_first_not_of(" \t\r\n");
    if (a == std::string::npos) return {};
    const auto b = s.find_last_not_of(" \t\r\n");
    return s.substr(a, b - a + 1);
}

bool AllowedSetting(const std::string& key) {
    return key == "SOCKS5_PROXY_ADDRESS" || key == "SOCKS5_PROXY_PORT" ||
           key == "SOCKS5_PROXY_TIMEOUT" || key == "SOCKS5_PROXY_LOGIN" ||
           key == "SOCKS5_PROXY_PASSWORD" || key == "DVP_SAFE_MODE";
}

BOOL CALLBACK InitNetworkProxy(PINIT_ONCE, PVOID, PVOID*) {
    Log("network initialization entered");
    try {
        const auto directory = SelfDirectory();
        if (directory.empty()) { Log("could not determine DLL directory"); return TRUE; }
        std::ifstream config(directory / L"proxy.txt");
        if (!config) { Log("proxy.txt missing: proxy disabled"); return TRUE; }

        std::string line;
        while (std::getline(config, line)) {
            const auto pos = line.find('=');
            if (pos == std::string::npos) continue;
            const std::string key = Trim(line.substr(0, pos));
            const std::string value = Trim(line.substr(pos + 1));
            if (AllowedSetting(key)) SetEnvironmentVariableA(key.c_str(), value.c_str());
        }

        char disabled[8] = {};
        if (GetEnvironmentVariableA("DVP_SAFE_MODE", disabled, sizeof(disabled)) &&
            std::string(disabled) == "1") {
            Log("safe mode: network proxy not loaded");
            return TRUE;
        }

        // IMPORTANT: Only the rebuilt, explicitly initialized component is loaded.
        // Never auto-load the legacy force-proxy.dll: its DllMain installs hooks.
        const fs::path dll = directory / L"force-proxy-safe.dll";
        if (!fs::exists(dll)) { Log("force-proxy-safe.dll missing: proxy disabled"); return TRUE; }
        Log("loading force-proxy-safe.dll");
        g_network_proxy = LoadLibraryW(dll.c_str());
        if (!g_network_proxy) { Log("cannot load force-proxy-safe.dll", GetLastError()); return TRUE; }

        using StartFn = BOOL (WINAPI*)();
        const auto start = reinterpret_cast<StartFn>(GetProcAddress(g_network_proxy, "StartForceProxy"));
        if (!start) { Log("StartForceProxy export missing: proxy disabled", GetLastError()); return TRUE; }
        Log("calling StartForceProxy");
        if (!start()) Log("StartForceProxy initialization returned false");
        else Log("StartForceProxy invoked; network hook status is not independently verified");
    } catch (...) {
        Log("unexpected C++ exception during proxy initialization");
    }
    return TRUE;
}
} // namespace

// Export real DWrite ABI directly, instead of the original raw assembly JMP.
extern "C" __declspec(dllexport)
HRESULT WINAPI DWriteCreateFactory(DWRITE_FACTORY_TYPE type, REFIID iid, IUnknown** factory) WIN_NOEXCEPT {
    InitOnceExecuteOnce(&g_real_once, InitRealDWrite, nullptr, nullptr);
    if (!g_real_factory) return HRESULT_FROM_WIN32(ERROR_MOD_NOT_FOUND);
    // Initialize DirectWrite before installing network hooks.
    const HRESULT hr = g_real_factory(type, iid, factory);
    if (SUCCEEDED(hr)) InitOnceExecuteOnce(&g_proxy_once, InitNetworkProxy, nullptr, nullptr);
    else Log("system DWriteCreateFactory failed", static_cast<DWORD>(hr));
    return hr;
}

BOOL WINAPI DllMain(HINSTANCE self, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) {
        g_self = self;
        DisableThreadLibraryCalls(self);
    }
    return TRUE;
}
