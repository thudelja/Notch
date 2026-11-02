import DiskArbitration
import Foundation

/// What the app knows about a mounted volume.
struct VolumeInfo: Identifiable, Hashable {
    let url: URL
    let name: String
    /// Volume UUID. FAT and exFAT cards get one derived from their serial number, which changes
    /// when the card is reformatted.
    let identifier: String?
    let capacity: Int64?
    let isRemovable: Bool
    let isLocal: Bool
    let isRootFileSystem: Bool
    let isDiskImage: Bool
    /// The built-in SD slot, or a USB reader whose name identifies it as a card reader.
    let isCardReader: Bool
    /// Has a DCIM folder (the camera file-system standard) or a known camera video folder.
    let hasCameraFolder: Bool

    var id: String { url.path }

    /// Identifies this card (until it is reformatted) for import records and camera names.
    var cardID: String { identifier ?? "\(name)-\(capacity ?? 0)" }

    /// Physically removable media: excludes the startup disk, network shares, disk images and
    /// external hard drives / SSDs (which macOS does not report as removable).
    var isRemovableMedia: Bool { isRemovable && isLocal && !isRootFileSystem && !isDiskImage }

    var looksLikeMemoryCard: Bool { isRemovableMedia && (isCardReader || hasCameraFolder) }

    /// Folder name inside the backup destination. The short ID keeps cards with identical names
    /// apart: every Canon card is called EOS_DIGITAL, and many others are NO NAME or Untitled.
    var backupFolderName: String {
        guard let identifier, !identifier.isEmpty else { return name }
        let shortID = identifier.replacingOccurrences(of: "-", with: "").prefix(8).uppercased()
        return "\(name) (\(shortID))"
    }
}

enum VolumeInspector {
    private static let resourceKeys: Set<URLResourceKey> = [
        .volumeLocalizedNameKey, .volumeNameKey, .volumeUUIDStringKey, .volumeTotalCapacityKey,
        .volumeIsRemovableKey, .volumeIsLocalKey, .volumeIsRootFileSystemKey,
    ]

    /// Folders that identify camera media.
    private static let cameraFolders = ["DCIM", "PRIVATE", "AVCHD", "MP_ROOT"]

    /// Words in a device's model/vendor/media name that identify a card reader.
    private static let cardReaderWords: Set<String> = [
        "sd", "sdhc", "sdxc", "microsd", "card", "cardreader", "reader", "mmc", "crw", "multicard", "cfexpress",
    ]

    static func mountedVolumes() -> [VolumeInfo] {
        let urls = FileManager.default.mountedVolumeURLs(includingResourceValuesForKeys: nil, options: [.skipHiddenVolumes]) ?? []
        return urls.compactMap { inspect($0) }
    }

    static func inspect(_ url: URL) -> VolumeInfo? {
        guard let values = try? url.resourceValues(forKeys: resourceKeys) else { return nil }
        let disk = diskDescription(for: url)

        let deviceProtocol = disk[kDADiskDescriptionDeviceProtocolKey as String] as? String ?? ""
        let model = disk[kDADiskDescriptionDeviceModelKey as String] as? String ?? ""
        let vendor = disk[kDADiskDescriptionDeviceVendorKey as String] as? String ?? ""
        let mediaName = disk[kDADiskDescriptionMediaNameKey as String] as? String ?? ""
        let mediaRemovable = disk[kDADiskDescriptionMediaRemovableKey as String] as? Bool ?? false

        let isDiskImage = model.localizedCaseInsensitiveContains("Disk Image") || deviceProtocol == "Virtual Interface"
        let words = Set(
            [model, vendor, mediaName].joined(separator: " ").lowercased()
                .split(whereSeparator: { !$0.isLetter && !$0.isNumber })
                .map(String.init)
        )
        // The built-in SDXC slot reports its protocol as "Secure Digital".
        let isCardReader = deviceProtocol == "Secure Digital" || !words.isDisjoint(with: cardReaderWords)

        let isRemovable = (values.volumeIsRemovable ?? false) || mediaRemovable
        let isLocal = values.volumeIsLocal ?? false
        let isRootFileSystem = values.volumeIsRootFileSystem ?? false

        // Only look inside volumes that could be a card.
        var hasCameraFolder = false
        if isRemovable && isLocal && !isRootFileSystem && !isDiskImage {
            hasCameraFolder = cameraFolders.contains { folder in
                var isDirectory: ObjCBool = false
                return FileManager.default.fileExists(atPath: url.appendingPathComponent(folder).path, isDirectory: &isDirectory)
                    && isDirectory.boolValue
            }
        }

        return VolumeInfo(
            url: url,
            name: values.volumeLocalizedName ?? values.volumeName ?? url.lastPathComponent,
            identifier: values.volumeUUIDString ?? volumeUUID(from: disk),
            capacity: values.volumeTotalCapacity.map { Int64($0) },
            isRemovable: isRemovable,
            isLocal: isLocal,
            isRootFileSystem: isRootFileSystem,
            isDiskImage: isDiskImage,
            isCardReader: isCardReader,
            hasCameraFolder: hasCameraFolder
        )
    }

    private static func diskDescription(for url: URL) -> [String: Any] {
        guard let session = DASessionCreate(kCFAllocatorDefault),
              let disk = DADiskCreateFromVolumePath(kCFAllocatorDefault, session, url as CFURL),
              let description = DADiskCopyDescription(disk) as NSDictionary? as? [String: Any]
        else { return [:] }
        return description
    }

    private static func volumeUUID(from description: [String: Any]) -> String? {
        guard let value = description[kDADiskDescriptionVolumeUUIDKey as String] else { return nil }
        let object = value as AnyObject
        guard CFGetTypeID(object) == CFUUIDGetTypeID() else { return nil }
        guard let string = CFUUIDCreateString(kCFAllocatorDefault, (object as! CFUUID)) else { return nil }
        return string as String
    }
}
