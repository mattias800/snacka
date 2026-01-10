// swift-tools-version: 5.9
import PackageDescription

let package = Package(
    name: "MiscordMetalRenderer",
    platforms: [
        .macOS(.v12)
    ],
    products: [
        .library(
            name: "MiscordMetalRenderer",
            type: .dynamic,
            targets: ["MiscordMetalRenderer"]
        ),
    ],
    targets: [
        .target(
            name: "MiscordMetalRenderer",
            dependencies: [],
            linkerSettings: [
                .linkedFramework("Metal"),
                .linkedFramework("MetalKit"),
                .linkedFramework("AppKit"),
                .linkedFramework("QuartzCore"),
                .linkedFramework("VideoToolbox"),
                .linkedFramework("CoreMedia"),
                .linkedFramework("CoreVideo")
            ]
        ),
    ]
)
