#pragma once

#include "Protocol.h"
#include <vector>

namespace snacka {

/// Utility class for listing available capture sources on Linux
class SourceLister {
public:
    /// Get list of available capture sources (displays, windows, and cameras)
    static SourceList GetAvailableSources();

    /// Enumerate available V4L2 video capture devices
    static std::vector<CameraInfo> EnumerateCameras();

    /// Print sources in human-readable format to stderr
    static void PrintSources(const SourceList& sources);

    /// Print sources as JSON to stdout
    static void PrintSourcesAsJson(const SourceList& sources);

private:
    /// Escape a string for JSON output
    static std::string EscapeJson(const std::string& str);
};

}  // namespace snacka
