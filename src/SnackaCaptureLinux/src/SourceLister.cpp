#include "SourceLister.h"
#include "PulseMicrophoneCapturer.h"

#include <X11/Xlib.h>
#include <X11/extensions/Xrandr.h>

#include <linux/videodev2.h>
#include <sys/ioctl.h>
#include <fcntl.h>
#include <unistd.h>
#include <dirent.h>

#include <iostream>
#include <sstream>
#include <iomanip>
#include <cstring>
#include <algorithm>

namespace snacka {

SourceList SourceLister::GetAvailableSources() {
    SourceList sources;

    // Open X display
    Display* display = XOpenDisplay(nullptr);
    if (!display) {
        std::cerr << "SnackaCaptureLinux: Failed to open X display for source listing\n";
        return sources;
    }

    int screen = DefaultScreen(display);
    Window root = RootWindow(display, screen);

    // Try to get monitor information using XRandR
    int eventBase, errorBase;
    if (XRRQueryExtension(display, &eventBase, &errorBase)) {
        XRRScreenResources* resources = XRRGetScreenResources(display, root);
        if (resources) {
            for (int i = 0; i < resources->noutput; i++) {
                XRROutputInfo* outputInfo = XRRGetOutputInfo(display, resources, resources->outputs[i]);
                if (outputInfo && outputInfo->connection == RR_Connected && outputInfo->crtc) {
                    XRRCrtcInfo* crtcInfo = XRRGetCrtcInfo(display, resources, outputInfo->crtc);
                    if (crtcInfo) {
                        DisplayInfo info;
                        info.id = std::to_string(i);
                        info.name = outputInfo->name ? outputInfo->name : ("Display " + std::to_string(i));
                        info.width = crtcInfo->width;
                        info.height = crtcInfo->height;
                        info.isPrimary = (i == 0);  // Assume first is primary

                        sources.displays.push_back(info);
                        XRRFreeCrtcInfo(crtcInfo);
                    }
                }
                if (outputInfo) {
                    XRRFreeOutputInfo(outputInfo);
                }
            }
            XRRFreeScreenResources(resources);
        }
    }

    // If no monitors found via XRandR, add the default screen
    if (sources.displays.empty()) {
        DisplayInfo info;
        info.id = "0";
        info.name = "Default Screen";
        info.width = DisplayWidth(display, screen);
        info.height = DisplayHeight(display, screen);
        info.isPrimary = true;
        sources.displays.push_back(info);
    }

    // List top-level windows
    Window rootReturn, parentReturn;
    Window* children = nullptr;
    unsigned int numChildren = 0;

    if (XQueryTree(display, root, &rootReturn, &parentReturn, &children, &numChildren)) {
        for (unsigned int i = 0; i < numChildren && sources.windows.size() < 50; i++) {
            Window child = children[i];

            // Get window attributes
            XWindowAttributes attrs;
            if (XGetWindowAttributes(display, child, &attrs) == 0) {
                continue;
            }

            // Skip unmapped or tiny windows
            if (attrs.map_state != IsViewable || attrs.width < 100 || attrs.height < 100) {
                continue;
            }

            // Get window name
            char* windowName = nullptr;
            if (XFetchName(display, child, &windowName) && windowName) {
                WindowInfo info;
                info.id = std::to_string(static_cast<unsigned long>(child));
                info.name = windowName;
                info.appName = windowName;  // X11 doesn't easily give process name
                info.bundleId = "";

                sources.windows.push_back(info);
                XFree(windowName);
            }
        }

        if (children) {
            XFree(children);
        }
    }

    XCloseDisplay(display);

    // Enumerate cameras
    sources.cameras = EnumerateCameras();

    // Enumerate microphones
    sources.microphones = EnumerateMicrophones();

    return sources;
}

std::vector<MicrophoneInfo> SourceLister::EnumerateMicrophones() {
    return PulseMicrophoneCapturer::EnumerateMicrophones();
}

std::vector<CameraInfo> SourceLister::EnumerateCameras() {
    std::vector<CameraInfo> cameras;

    // Scan /dev for video devices
    DIR* dir = opendir("/dev");
    if (!dir) {
        return cameras;
    }

    std::vector<std::string> videoDevices;
    struct dirent* entry;
    while ((entry = readdir(dir)) != nullptr) {
        std::string name = entry->d_name;
        if (name.find("video") == 0) {
            videoDevices.push_back("/dev/" + name);
        }
    }
    closedir(dir);

    // Sort devices by name
    std::sort(videoDevices.begin(), videoDevices.end());

    int cameraIndex = 0;
    for (const auto& devicePath : videoDevices) {
        int fd = open(devicePath.c_str(), O_RDWR | O_NONBLOCK);
        if (fd < 0) {
            continue;
        }

        // Query device capabilities
        struct v4l2_capability cap;
        if (ioctl(fd, VIDIOC_QUERYCAP, &cap) < 0) {
            close(fd);
            continue;
        }

        // Check if this is a video capture device
        if (!(cap.device_caps & V4L2_CAP_VIDEO_CAPTURE)) {
            close(fd);
            continue;
        }

        CameraInfo info;
        info.id = devicePath;
        info.name = reinterpret_cast<const char*>(cap.card);
        info.index = cameraIndex++;

        cameras.push_back(info);
        close(fd);
    }

    return cameras;
}

void SourceLister::PrintSources(const SourceList& sources) {
    std::cerr << "\nAvailable Displays:\n";
    std::cerr << "-------------------\n";
    for (const auto& display : sources.displays) {
        std::cerr << "  [" << display.id << "] " << display.name
                  << " (" << display.width << "x" << display.height << ")"
                  << (display.isPrimary ? " [Primary]" : "") << "\n";
    }

    if (!sources.windows.empty()) {
        std::cerr << "\nAvailable Windows:\n";
        std::cerr << "------------------\n";
        for (const auto& window : sources.windows) {
            std::cerr << "  [" << window.id << "] " << window.name << "\n";
        }
    }

    std::cerr << "\nAvailable Cameras:\n";
    std::cerr << "------------------\n";
    if (sources.cameras.empty()) {
        std::cerr << "  (No cameras found)\n";
    } else {
        for (const auto& camera : sources.cameras) {
            std::cerr << "  [" << camera.index << "] " << camera.name
                      << " (" << camera.id << ")\n";
        }
    }

    std::cerr << "\nAvailable Microphones:\n";
    std::cerr << "----------------------\n";
    if (sources.microphones.empty()) {
        std::cerr << "  (No microphones found)\n";
    } else {
        for (const auto& mic : sources.microphones) {
            std::cerr << "  [" << mic.index << "] " << mic.name << "\n";
        }
    }

    std::cerr << "\n";
}

void SourceLister::PrintSourcesAsJson(const SourceList& sources) {
    std::cout << "{\n";
    std::cout << "  \"displays\": [\n";

    for (size_t i = 0; i < sources.displays.size(); i++) {
        const auto& display = sources.displays[i];
        std::cout << "    {\n";
        std::cout << "      \"id\": \"" << EscapeJson(display.id) << "\",\n";
        std::cout << "      \"name\": \"" << EscapeJson(display.name) << "\",\n";
        std::cout << "      \"width\": " << display.width << ",\n";
        std::cout << "      \"height\": " << display.height << ",\n";
        std::cout << "      \"isPrimary\": " << (display.isPrimary ? "true" : "false") << "\n";
        std::cout << "    }" << (i < sources.displays.size() - 1 ? "," : "") << "\n";
    }

    std::cout << "  ],\n";
    std::cout << "  \"windows\": [\n";

    for (size_t i = 0; i < sources.windows.size(); i++) {
        const auto& window = sources.windows[i];
        std::cout << "    {\n";
        std::cout << "      \"id\": \"" << EscapeJson(window.id) << "\",\n";
        std::cout << "      \"name\": \"" << EscapeJson(window.name) << "\",\n";
        std::cout << "      \"appName\": \"" << EscapeJson(window.appName) << "\",\n";
        std::cout << "      \"bundleId\": \"" << EscapeJson(window.bundleId) << "\"\n";
        std::cout << "    }" << (i < sources.windows.size() - 1 ? "," : "") << "\n";
    }

    std::cout << "  ],\n";
    std::cout << "  \"applications\": [],\n";

    // Cameras
    std::cout << "  \"cameras\": [\n";
    for (size_t i = 0; i < sources.cameras.size(); i++) {
        const auto& camera = sources.cameras[i];
        std::cout << "    {\n";
        std::cout << "      \"id\": \"" << EscapeJson(camera.id) << "\",\n";
        std::cout << "      \"name\": \"" << EscapeJson(camera.name) << "\",\n";
        std::cout << "      \"index\": " << camera.index << "\n";
        std::cout << "    }" << (i < sources.cameras.size() - 1 ? "," : "") << "\n";
    }
    std::cout << "  ],\n";

    // Microphones
    std::cout << "  \"microphones\": [\n";
    for (size_t i = 0; i < sources.microphones.size(); i++) {
        const auto& mic = sources.microphones[i];
        std::cout << "    {\n";
        std::cout << "      \"id\": \"" << EscapeJson(mic.id) << "\",\n";
        std::cout << "      \"name\": \"" << EscapeJson(mic.name) << "\",\n";
        std::cout << "      \"index\": " << mic.index << "\n";
        std::cout << "    }" << (i < sources.microphones.size() - 1 ? "," : "") << "\n";
    }
    std::cout << "  ]\n";

    std::cout << "}\n";
}

std::string SourceLister::EscapeJson(const std::string& str) {
    std::ostringstream escaped;
    for (char c : str) {
        switch (c) {
            case '"': escaped << "\\\""; break;
            case '\\': escaped << "\\\\"; break;
            case '\b': escaped << "\\b"; break;
            case '\f': escaped << "\\f"; break;
            case '\n': escaped << "\\n"; break;
            case '\r': escaped << "\\r"; break;
            case '\t': escaped << "\\t"; break;
            default:
                if (static_cast<unsigned char>(c) < 0x20) {
                    escaped << "\\u" << std::hex << std::setfill('0') << std::setw(4) << static_cast<int>(c);
                } else {
                    escaped << c;
                }
                break;
        }
    }
    return escaped.str();
}

}  // namespace snacka
