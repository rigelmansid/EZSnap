// swift-tools-version:5.9
import PackageDescription

let package = Package(
    name: "Snapshot",
    platforms: [.macOS(.v14)],
    targets: [
        .executableTarget(
            name: "Snapshot",
            path: "Sources/Snapshot",
            linkerSettings: [
                .linkedFramework("ScreenCaptureKit"),
                .linkedFramework("Carbon"),
            ]
        )
    ]
)
