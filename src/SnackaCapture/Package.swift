// swift-tools-version:5.9
import PackageDescription

let package = Package(
    name: "SnackaCapture",
    platforms: [
        .macOS(.v13)  // ScreenCaptureKit audio requires macOS 13+
    ],
    products: [
        .executable(name: "SnackaCapture", targets: ["SnackaCapture"])
    ],
    dependencies: [
        // Pin to 1.3.x to avoid Swift 6.0 features in 1.5+
        .package(url: "https://github.com/apple/swift-argument-parser.git", .upToNextMinor(from: "1.3.0"))
    ],
    targets: [
        .executableTarget(
            name: "SnackaCapture",
            dependencies: [
                .product(name: "ArgumentParser", package: "swift-argument-parser")
            ],
            linkerSettings: [
                .linkedFramework("ScreenCaptureKit"),
                .linkedFramework("CoreMedia"),
                .linkedFramework("CoreVideo"),
                .linkedFramework("AVFoundation")
            ]
        )
    ]
)
