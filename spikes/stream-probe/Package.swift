// swift-tools-version:5.9
// Spike for #49: measure the latency floor of a capture -> encode -> UDP -> decode pipeline.
import PackageDescription

let package = Package(
    name: "StreamProbe",
    platforms: [.macOS(.v13)],
    targets: [
        .executableTarget(
            name: "StreamProbe",
            path: "Sources/StreamProbe",
            swiftSettings: [.unsafeFlags(["-Ounchecked"])]
        ),
    ],
    swiftLanguageVersions: [.v5]
)
