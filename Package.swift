// swift-tools-version:5.9
import PackageDescription

let package = Package(
    name: "EZSnap",
    platforms: [.macOS(.v14)],
    targets: [
        .executableTarget(
            name: "EZSnap",
            path: "Sources/EZSnap",
            linkerSettings: [
                .linkedFramework("ScreenCaptureKit"),
                .linkedFramework("Carbon"),
            ]
        )
    ]
)
