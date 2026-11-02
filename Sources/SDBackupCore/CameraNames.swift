import Foundation

/// Turns the model names cameras write into their files into the short names people use for
/// folders: "Canon EOS R5" → "R5", "ILCE-7M4" → "A7 IV", "NIKON Z 6_2" → "Z6 II".
public enum CameraNames {
    private static let makePrefixes = [
        "Canon EOS ", "Canon ", "NIKON ", "Nikon ", "FUJIFILM ", "Fujifilm ", "OLYMPUS ", "Olympus ",
        "PENTAX ", "Panasonic ", "SONY ", "Sony ", "LEICA ", "Leica ", "RICOH ", "DJI ", "GoPro ",
    ]

    private static let romanNumerals = ["2": "II", "3": "III", "4": "IV", "5": "V", "6": "VI"]

    public static func shortName(make: String?, model: String) -> String {
        var name = model.trimmingCharacters(in: .whitespacesAndNewlines)

        // Drop a repeated make ("Canon EOS R5", "NIKON Z 6_2").
        if let prefix = makePrefixes.first(where: { name.hasPrefix($0) }) {
            name.removeFirst(prefix.count)
        } else if let make = make?.trimmingCharacters(in: .whitespacesAndNewlines), !make.isEmpty,
                  name.lowercased().hasPrefix(make.lowercased() + " ") {
            name.removeFirst(make.count + 1)
        }

        // Sony: ILME-FX3 → FX3, ILCE-7SM3 → A7S III, ILCE-6400 → A6400.
        if name.hasPrefix("ILME-") {
            name.removeFirst(5)
        } else if name.hasPrefix("ILCE-") {
            name = sonyAlpha(String(name.dropFirst(5)))
        }

        // Panasonic: DC-GH6 → GH6, DC-S5M2 → S5M2.
        if name.hasPrefix("DC-") {
            name.removeFirst(3)
        }

        // Nikon: "Z 6_2" → "Z6 II", "Z f" → "Zf".
        if name.hasPrefix("Z ") {
            name = name.replacingOccurrences(of: "_2", with: " II").replacingOccurrences(of: "_3", with: " III")
            name = "Z\(name.dropFirst(2))"
        }

        name = name.trimmingCharacters(in: .whitespacesAndNewlines)
        return name.isEmpty ? model.trimmingCharacters(in: .whitespacesAndNewlines) : name
    }

    /// "7M4" → "A7 IV", "7SM3" → "A7S III", "6400" → "A6400", "1" → "A1".
    private static func sonyAlpha(_ code: String) -> String {
        if let marker = code.range(of: "M", options: .backwards),
           marker.upperBound < code.endIndex,
           let roman = romanNumerals[String(code[marker.upperBound...])] {
            return "A\(code[..<marker.lowerBound]) \(roman)"
        }
        return "A\(code)"
    }
}
