#pragma once

#include "Protocol.h"
#include <string>

namespace snacka {

class SourceLister {
public:
    // Get all available capture sources
    static SourceList GetAvailableSources();

    // Output sources as JSON to stdout
    static void PrintSourcesAsJson(const SourceList& sources);

    // Output sources in human-readable format to stdout
    static void PrintSources(const SourceList& sources);

private:
    static std::vector<DisplayInfo> EnumerateDisplays();
    static std::vector<WindowInfo> EnumerateWindows();
    static std::vector<CameraInfo> EnumerateCameras();
};

}  // namespace snacka
