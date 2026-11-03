import AppKit

/// The menu bar icon: Notch's logo, an SD card with a check, drawn as a template image so macOS
/// colours it for light and dark menu bars. The full-colour logo is in branding/.
enum MenuBarIcon {
    /// Waiting for a card.
    static let normal = make(filled: false)
    /// Reading or copying a card, or cards are waiting for a project.
    static let filled = make(filled: true)

    private static let size: CGFloat = 18
    /// Drawn on a 24-unit grid; this fits the card into the menu bar's 18-point icon area.
    private static let unit: CGFloat = 18.0 / 22.0

    private static func make(filled: Bool) -> NSImage {
        let image = NSImage(size: NSSize(width: size, height: size), flipped: false) { _ in
            guard let context = NSGraphicsContext.current?.cgContext else { return false }
            // Use top-left origin coordinates, centred in the image.
            context.translateBy(x: (size - 24 * unit) / 2, y: size - (size - 24 * unit) / 2)
            context.scaleBy(x: unit, y: -unit)

            context.setFillColor(NSColor.black.cgColor)
            context.setStrokeColor(NSColor.black.cgColor)
            context.setLineWidth(1.6)
            context.setLineCap(.round)
            context.setLineJoin(.round)

            if filled {
                // A solid card with the check cut out of it.
                context.addPath(cardPath())
                context.fillPath()
                context.setBlendMode(.clear)
                context.setLineWidth(1.8)
                context.addPath(checkPath())
                context.strokePath()
            } else {
                context.addPath(cardPath())
                context.strokePath()
                context.addPath(checkPath())
                context.strokePath()
            }
            return true
        }
        image.isTemplate = true
        image.accessibilityDescription = "Notch"
        return image
    }

    /// An SD card: rounded corners, except the top right, which is cut off.
    private static func cardPath() -> CGPath {
        let left: CGFloat = 6, top: CGFloat = 3, right: CGFloat = 18.5, bottom: CGFloat = 21
        let radius: CGFloat = 1.5, cut: CGFloat = 3.5
        let path = CGMutablePath()
        path.move(to: CGPoint(x: left, y: top + radius))
        path.addArc(tangent1End: CGPoint(x: left, y: top), tangent2End: CGPoint(x: left + radius, y: top), radius: radius)
        path.addLine(to: CGPoint(x: right - cut, y: top))
        path.addLine(to: CGPoint(x: right, y: top + cut))
        path.addArc(tangent1End: CGPoint(x: right, y: bottom), tangent2End: CGPoint(x: right - radius, y: bottom), radius: radius)
        path.addArc(tangent1End: CGPoint(x: left, y: bottom), tangent2End: CGPoint(x: left, y: bottom - radius), radius: radius)
        path.closeSubpath()
        return path
    }

    private static func checkPath() -> CGPath {
        let path = CGMutablePath()
        path.move(to: CGPoint(x: 9, y: 12.3))
        path.addLine(to: CGPoint(x: 11.3, y: 14.6))
        path.addLine(to: CGPoint(x: 15.5, y: 10.2))
        return path
    }
}
