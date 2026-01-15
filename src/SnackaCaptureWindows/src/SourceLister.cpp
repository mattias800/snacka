#include "SourceLister.h"

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <Windows.h>
#include <Psapi.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mfreadwrite.h>

#pragma comment(lib, "mfplat.lib")
#pragma comment(lib, "mf.lib")
#pragma comment(lib, "mfreadwrite.lib")
#pragma comment(lib, "mfuuid.lib")

#include <iostream>
#include <sstream>
#include <iomanip>
#include <algorithm>

namespace snacka {

// Helper to escape JSON strings
static std::string EscapeJson(const std::string& s) {
    std::ostringstream o;
    for (char c : s) {
        switch (c) {
            case '"':  o << "\\\""; break;
            case '\\': o << "\\\\"; break;
            case '\b': o << "\\b";  break;
            case '\f': o << "\\f";  break;
            case '\n': o << "\\n";  break;
            case '\r': o << "\\r";  break;
            case '\t': o << "\\t";  break;
            default:
                if (static_cast<unsigned char>(c) < 0x20) {
                    o << "\\u" << std::hex << std::setfill('0') << std::setw(4) << static_cast<int>(c);
                } else {
                    o << c;
                }
        }
    }
    return o.str();
}

// Convert wide string to UTF-8
static std::string WideToUtf8(const std::wstring& wide) {
    if (wide.empty()) return "";
    int size = WideCharToMultiByte(CP_UTF8, 0, wide.c_str(), static_cast<int>(wide.size()), nullptr, 0, nullptr, nullptr);
    std::string result(size, 0);
    WideCharToMultiByte(CP_UTF8, 0, wide.c_str(), static_cast<int>(wide.size()), result.data(), size, nullptr, nullptr);
    return result;
}

struct MonitorEnumContext {
    std::vector<DisplayInfo>* displays;
    int index;
};

static BOOL CALLBACK MonitorEnumProc(HMONITOR hMonitor, HDC, LPRECT, LPARAM lParam) {
    auto* ctx = reinterpret_cast<MonitorEnumContext*>(lParam);

    MONITORINFOEXW mi = {};
    mi.cbSize = sizeof(mi);
    if (GetMonitorInfoW(hMonitor, &mi)) {
        DisplayInfo info;
        info.id = std::to_string(ctx->index);
        info.width = mi.rcMonitor.right - mi.rcMonitor.left;
        info.height = mi.rcMonitor.bottom - mi.rcMonitor.top;
        info.isPrimary = (mi.dwFlags & MONITORINFOF_PRIMARY) != 0;

        // Create a descriptive name
        std::string deviceName = WideToUtf8(mi.szDevice);
        std::ostringstream nameStream;
        nameStream << "Display " << (ctx->index + 1);
        if (!deviceName.empty()) {
            nameStream << " (" << deviceName << ")";
        }
        if (info.isPrimary) {
            nameStream << " - Primary";
        }
        info.name = nameStream.str();

        ctx->displays->push_back(info);
        ctx->index++;
    }

    return TRUE;
}

std::vector<DisplayInfo> SourceLister::EnumerateDisplays() {
    std::vector<DisplayInfo> displays;
    MonitorEnumContext ctx = { &displays, 0 };

    EnumDisplayMonitors(nullptr, nullptr, MonitorEnumProc, reinterpret_cast<LPARAM>(&ctx));

    return displays;
}

struct WindowEnumContext {
    std::vector<WindowInfo>* windows;
};

static BOOL CALLBACK WindowEnumProc(HWND hwnd, LPARAM lParam) {
    auto* ctx = reinterpret_cast<WindowEnumContext*>(lParam);

    // Skip invisible windows
    if (!IsWindowVisible(hwnd)) return TRUE;

    // Get window title
    int titleLen = GetWindowTextLengthW(hwnd);
    if (titleLen == 0) return TRUE;

    std::wstring title(titleLen + 1, L'\0');
    GetWindowTextW(hwnd, title.data(), titleLen + 1);
    title.resize(titleLen);

    // Skip empty titles
    if (title.empty() || title.find_first_not_of(L' ') == std::wstring::npos) {
        return TRUE;
    }

    // Get process name
    std::string appName;
    DWORD processId = 0;
    GetWindowThreadProcessId(hwnd, &processId);
    if (processId != 0) {
        HANDLE hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, processId);
        if (hProcess) {
            wchar_t exePath[MAX_PATH] = {};
            DWORD size = MAX_PATH;
            if (QueryFullProcessImageNameW(hProcess, 0, exePath, &size)) {
                std::wstring path(exePath);
                size_t lastSlash = path.find_last_of(L"\\/");
                size_t lastDot = path.rfind(L'.');
                if (lastSlash != std::wstring::npos) {
                    std::wstring fileName = path.substr(lastSlash + 1);
                    if (lastDot != std::wstring::npos && lastDot > lastSlash) {
                        fileName = path.substr(lastSlash + 1, lastDot - lastSlash - 1);
                    }
                    appName = WideToUtf8(fileName);
                }
            }
            CloseHandle(hProcess);
        }
    }

    // Skip some system windows
    if (appName == "TextInputHost" || appName == "ApplicationFrameHost" ||
        appName == "SystemSettings" || appName == "ShellExperienceHost") {
        return TRUE;
    }

    WindowInfo info;
    info.id = std::to_string(reinterpret_cast<uintptr_t>(hwnd));
    info.name = WideToUtf8(title);
    info.appName = appName;
    info.bundleId = "";  // Not applicable on Windows

    // Truncate long titles
    if (info.name.length() > 100) {
        info.name = info.name.substr(0, 97) + "...";
    }

    ctx->windows->push_back(info);

    return TRUE;
}

std::vector<WindowInfo> SourceLister::EnumerateWindows() {
    std::vector<WindowInfo> windows;
    WindowEnumContext ctx = { &windows };

    EnumWindows(WindowEnumProc, reinterpret_cast<LPARAM>(&ctx));

    // Sort by app name
    std::sort(windows.begin(), windows.end(), [](const WindowInfo& a, const WindowInfo& b) {
        return a.appName < b.appName;
    });

    return windows;
}

std::vector<CameraInfo> SourceLister::EnumerateCameras() {
    std::vector<CameraInfo> cameras;

    // Initialize Media Foundation (required for device enumeration)
    HRESULT hr = MFStartup(MF_VERSION);
    if (FAILED(hr)) {
        return cameras;
    }

    // Create attributes for video capture devices
    IMFAttributes* pAttributes = nullptr;
    hr = MFCreateAttributes(&pAttributes, 1);
    if (FAILED(hr)) {
        MFShutdown();
        return cameras;
    }

    // Request video capture devices
    hr = pAttributes->SetGUID(
        MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE,
        MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID
    );
    if (FAILED(hr)) {
        pAttributes->Release();
        MFShutdown();
        return cameras;
    }

    // Enumerate devices
    IMFActivate** ppDevices = nullptr;
    UINT32 count = 0;
    hr = MFEnumDeviceSources(pAttributes, &ppDevices, &count);
    pAttributes->Release();

    if (FAILED(hr) || count == 0) {
        MFShutdown();
        return cameras;
    }

    // Get info for each device
    for (UINT32 i = 0; i < count; i++) {
        CameraInfo info;
        info.index = static_cast<int>(i);

        // Get friendly name
        WCHAR* friendlyName = nullptr;
        UINT32 nameLength = 0;
        hr = ppDevices[i]->GetAllocatedString(
            MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME,
            &friendlyName,
            &nameLength
        );
        if (SUCCEEDED(hr) && friendlyName) {
            info.name = WideToUtf8(friendlyName);
            CoTaskMemFree(friendlyName);
        }

        // Get symbolic link (unique device ID)
        WCHAR* symbolicLink = nullptr;
        UINT32 linkLength = 0;
        hr = ppDevices[i]->GetAllocatedString(
            MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK,
            &symbolicLink,
            &linkLength
        );
        if (SUCCEEDED(hr) && symbolicLink) {
            info.id = WideToUtf8(symbolicLink);
            CoTaskMemFree(symbolicLink);
        } else {
            // Fallback to index as ID
            info.id = std::to_string(i);
        }

        cameras.push_back(info);
        ppDevices[i]->Release();
    }

    CoTaskMemFree(ppDevices);
    MFShutdown();

    return cameras;
}

SourceList SourceLister::GetAvailableSources() {
    SourceList sources;
    sources.displays = EnumerateDisplays();
    sources.windows = EnumerateWindows();
    sources.cameras = EnumerateCameras();
    // Applications list is empty on Windows (macOS-only concept)
    return sources;
}

void SourceLister::PrintSourcesAsJson(const SourceList& sources) {
    std::cout << "{\n";

    // Displays
    std::cout << "  \"displays\": [\n";
    for (size_t i = 0; i < sources.displays.size(); i++) {
        const auto& d = sources.displays[i];
        std::cout << "    {\n";
        std::cout << "      \"id\": \"" << EscapeJson(d.id) << "\",\n";
        std::cout << "      \"name\": \"" << EscapeJson(d.name) << "\",\n";
        std::cout << "      \"width\": " << d.width << ",\n";
        std::cout << "      \"height\": " << d.height << "\n";
        std::cout << "    }" << (i < sources.displays.size() - 1 ? "," : "") << "\n";
    }
    std::cout << "  ],\n";

    // Windows
    std::cout << "  \"windows\": [\n";
    for (size_t i = 0; i < sources.windows.size(); i++) {
        const auto& w = sources.windows[i];
        std::cout << "    {\n";
        std::cout << "      \"id\": \"" << EscapeJson(w.id) << "\",\n";
        std::cout << "      \"name\": \"" << EscapeJson(w.name) << "\",\n";
        std::cout << "      \"appName\": \"" << EscapeJson(w.appName) << "\",\n";
        std::cout << "      \"bundleId\": " << (w.bundleId.empty() ? "null" : "\"" + EscapeJson(w.bundleId) + "\"") << "\n";
        std::cout << "    }" << (i < sources.windows.size() - 1 ? "," : "") << "\n";
    }
    std::cout << "  ],\n";

    // Applications (empty on Windows)
    std::cout << "  \"applications\": [],\n";

    // Cameras
    std::cout << "  \"cameras\": [\n";
    for (size_t i = 0; i < sources.cameras.size(); i++) {
        const auto& c = sources.cameras[i];
        std::cout << "    {\n";
        std::cout << "      \"id\": \"" << EscapeJson(c.id) << "\",\n";
        std::cout << "      \"name\": \"" << EscapeJson(c.name) << "\",\n";
        std::cout << "      \"index\": " << c.index << "\n";
        std::cout << "    }" << (i < sources.cameras.size() - 1 ? "," : "") << "\n";
    }
    std::cout << "  ]\n";

    std::cout << "}\n";
}

void SourceLister::PrintSources(const SourceList& sources) {
    std::cout << "Displays:\n";
    for (const auto& d : sources.displays) {
        std::cout << "  [" << d.id << "] " << d.name << " (" << d.width << "x" << d.height << ")\n";
    }

    std::cout << "\nWindows:\n";
    for (const auto& w : sources.windows) {
        std::cout << "  [" << w.id << "] " << w.name;
        if (!w.appName.empty()) {
            std::cout << " - " << w.appName;
        }
        std::cout << "\n";
    }

    std::cout << "\nApplications:\n";
    std::cout << "  (Application capture not supported on Windows)\n";

    std::cout << "\nCameras:\n";
    for (const auto& c : sources.cameras) {
        std::cout << "  [" << c.index << "] " << c.name << "\n";
    }
}

}  // namespace snacka
