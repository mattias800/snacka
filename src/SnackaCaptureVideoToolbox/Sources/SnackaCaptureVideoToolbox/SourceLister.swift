import Foundation
import ScreenCaptureKit
import AVFoundation

/// Lists available capture sources using ScreenCaptureKit
enum SourceLister {
    static func getAvailableSources() async throws -> AvailableSources {
        let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)

        let displays = content.displays.enumerated().map { index, display in
            DisplaySource(
                id: "\(index)",
                name: "Display \(index + 1)",
                width: Int(display.width),
                height: Int(display.height)
            )
        }

        let windows = content.windows.compactMap { window -> WindowSource? in
            // Filter out windows without titles or from system processes
            guard let title = window.title, !title.isEmpty else { return nil }
            guard let app = window.owningApplication else { return nil }

            return WindowSource(
                id: "\(window.windowID)",
                name: title,
                appName: app.applicationName,
                bundleId: app.bundleIdentifier
            )
        }

        let applications = content.applications.map { app in
            ApplicationSource(
                bundleId: app.bundleIdentifier,
                name: app.applicationName
            )
        }

        // Enumerate cameras using AVFoundation
        let cameras = getAvailableCameras()

        return AvailableSources(
            displays: displays,
            windows: windows,
            applications: applications,
            cameras: cameras
        )
    }

    /// Returns available camera devices using AVFoundation
    static func getAvailableCameras() -> [CameraSource] {
        let discoverySession = AVCaptureDevice.DiscoverySession(
            deviceTypes: [.builtInWideAngleCamera, .externalUnknown],
            mediaType: .video,
            position: .unspecified
        )

        return discoverySession.devices.enumerated().map { index, device in
            let position: String
            switch device.position {
            case .front:
                position = "front"
            case .back:
                position = "back"
            case .unspecified:
                position = "unspecified"
            @unknown default:
                position = "unspecified"
            }

            return CameraSource(
                id: device.uniqueID,
                name: device.localizedName,
                index: index,
                position: position
            )
        }
    }

    /// Find a camera by unique ID or index
    static func findCamera(idOrIndex: String) -> AVCaptureDevice? {
        let cameras = AVCaptureDevice.DiscoverySession(
            deviceTypes: [.builtInWideAngleCamera, .externalUnknown],
            mediaType: .video,
            position: .unspecified
        ).devices

        // First try to find by unique ID
        if let device = cameras.first(where: { $0.uniqueID == idOrIndex }) {
            return device
        }

        // Then try to find by index
        if let index = Int(idOrIndex), index >= 0 && index < cameras.count {
            return cameras[index]
        }

        return nil
    }

    /// Find a display by index
    static func findDisplay(index: Int) async throws -> SCDisplay {
        let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
        guard index >= 0 && index < content.displays.count else {
            throw CaptureError.sourceNotFound("Display \(index) not found")
        }
        return content.displays[index]
    }

    /// Find a window by ID
    static func findWindow(id: Int) async throws -> SCWindow {
        let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
        guard let window = content.windows.first(where: { $0.windowID == CGWindowID(id) }) else {
            throw CaptureError.sourceNotFound("Window \(id) not found")
        }
        return window
    }

    /// Find an application by bundle ID
    static func findApplication(bundleId: String) async throws -> SCRunningApplication {
        let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
        guard let app = content.applications.first(where: { $0.bundleIdentifier == bundleId }) else {
            throw CaptureError.sourceNotFound("Application \(bundleId) not found")
        }
        return app
    }

    /// Get all windows for an application
    static func findWindowsForApplication(bundleId: String) async throws -> [SCWindow] {
        let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
        return content.windows.filter { $0.owningApplication?.bundleIdentifier == bundleId }
    }
}

enum CaptureError: Error, LocalizedError {
    case sourceNotFound(String)
    case captureNotSupported(String)
    case streamError(String)

    var errorDescription: String? {
        switch self {
        case .sourceNotFound(let msg): return "Source not found: \(msg)"
        case .captureNotSupported(let msg): return "Capture not supported: \(msg)"
        case .streamError(let msg): return "Stream error: \(msg)"
        }
    }
}
