// swift-tools-version:5.9
import PackageDescription

let package = Package(
    name: "SDBackup",
    platforms: [.macOS(.v13)],
    products: [
        .executable(name: "SDBackup", targets: ["SDBackup"]),
    ],
    targets: [
        // Pure Foundation backup logic, kept separate from the UI so it can be unit tested.
        .target(name: "SDBackupCore"),

        // The menu bar app.
        .executableTarget(
            name: "SDBackup",
            dependencies: ["SDBackupCore"],
            linkerSettings: [.linkedFramework("DiskArbitration")]
        ),

        .testTarget(name: "SDBackupCoreTests", dependencies: ["SDBackupCore"]),
    ]
)
