import AVFoundation
import Foundation
import ImageIO

/// When and with what a file was shot.
public struct CaptureInfo: Sendable, Equatable {
    public var date: Date?
    public var make: String?
    public var model: String?

    public init(date: Date? = nil, make: String? = nil, model: String? = nil) {
        self.date = date
        self.make = make
        self.model = model
    }
}

/// Reads capture information from files. Abstracted so tests can supply fixed answers.
public protocol MetadataReading: Sendable {
    func photoInfo(at url: URL) -> CaptureInfo
    func videoInfo(at url: URL) async -> CaptureInfo
}

/// Reads photo metadata with ImageIO (EXIF) and video metadata with AVFoundation.
public struct SystemMetadataReader: MetadataReading {
    public init() {}

    public func photoInfo(at url: URL) -> CaptureInfo {
        let options = [kCGImageSourceShouldCache: false] as CFDictionary
        guard let source = CGImageSourceCreateWithURL(url as CFURL, options),
              let properties = CGImageSourceCopyPropertiesAtIndex(source, 0, options) as? [String: Any]
        else { return CaptureInfo() }

        let exif = properties[kCGImagePropertyExifDictionary as String] as? [String: Any]
        let tiff = properties[kCGImagePropertyTIFFDictionary as String] as? [String: Any]
        let dateText = exif?[kCGImagePropertyExifDateTimeOriginal as String] as? String
            ?? tiff?[kCGImagePropertyTIFFDateTime as String] as? String
        return CaptureInfo(
            date: dateText.flatMap(MetadataParsing.parseDate),
            make: MetadataParsing.clean(tiff?[kCGImagePropertyTIFFMake as String] as? String),
            model: MetadataParsing.clean(tiff?[kCGImagePropertyTIFFModel as String] as? String)
        )
    }

    public func videoInfo(at url: URL) async -> CaptureInfo {
        let asset = AVURLAsset(url: url)
        guard let items = try? await asset.load(.metadata) else { return CaptureInfo() }
        var info = CaptureInfo()
        for item in items {
            guard let key = item.commonKey else { continue }
            if key == .commonKeyModel, info.model == nil {
                info.model = MetadataParsing.clean(try? await item.load(.stringValue))
            } else if key == .commonKeyMake, info.make == nil {
                info.make = MetadataParsing.clean(try? await item.load(.stringValue))
            }
        }
        return info
    }
}

public enum MetadataParsing {
    /// Parses "2026:11:02 14:03:22" (EXIF) or "2026-11-02T14:03:22+01:00" (ISO 8601) as the
    /// camera's local wall-clock time. Any time-zone offset is ignored on purpose: the shoot day is
    /// the day on the camera's clock.
    public static func parseDate(_ text: String) -> Date? {
        let separators: Set<Character> = [":", " ", "-", "T", "+", "."]
        let numbers = text.split(whereSeparator: { separators.contains($0) }).compactMap { Int($0) }
        guard numbers.count >= 3, numbers[0] > 1980, (1...12).contains(numbers[1]), (1...31).contains(numbers[2]) else {
            return nil // Cameras with an unset clock write "0000:00:00 00:00:00".
        }
        var components = DateComponents(year: numbers[0], month: numbers[1], day: numbers[2])
        if numbers.count >= 6 {
            components.hour = numbers[3]
            components.minute = numbers[4]
            components.second = numbers[5]
        }
        return Calendar.current.date(from: components)
    }

    /// Reads the clip XML that Sony (and some Canon) cameras write next to each video:
    /// `<Device manufacturer="Sony" modelName="ILCE-7M4" …/>` and `<CreationDate value="…"/>`.
    public static func parseClipXML(_ xml: String) -> CaptureInfo {
        CaptureInfo(
            date: attribute("value", ofElement: "CreationDate", in: xml).flatMap(parseDate),
            make: clean(attribute("manufacturer", ofElement: "Device", in: xml)),
            model: clean(attribute("modelName", ofElement: "Device", in: xml))
        )
    }

    static func attribute(_ name: String, ofElement element: String, in xml: String) -> String? {
        guard let elementRange = xml.range(of: "<\(element)\\s[^>]*>", options: .regularExpression) else { return nil }
        let tag = xml[elementRange]
        guard let attributeRange = tag.range(of: "\\s\(name)=\"[^\"]*\"", options: .regularExpression) else { return nil }
        // \sname="value" → value
        return String(tag[attributeRange].dropFirst(name.count + 3).dropLast())
    }

    static func clean(_ text: String?) -> String? {
        guard let text else { return nil }
        let cleaned = text.trimmingCharacters(in: CharacterSet.whitespacesAndNewlines.union(.controlCharacters))
        return cleaned.isEmpty ? nil : cleaned
    }
}
