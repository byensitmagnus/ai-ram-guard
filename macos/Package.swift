// swift-tools-version: 5.9
import PackageDescription

let package = Package(
    name: "AiRamGuardMac",
    platforms: [.macOS(.v13)],
    products: [.executable(name: "AiRamGuardMac", targets: ["AiRamGuardMac"])],
    targets: [
        .target(name: "ProcessProbe", publicHeadersPath: "include"),
        .target(name: "GuardCore", dependencies: ["ProcessProbe"]),
        .executableTarget(name: "AiRamGuardMac", dependencies: ["GuardCore"]),
        .testTarget(name: "GuardCoreTests", dependencies: ["GuardCore"])
    ]
)
