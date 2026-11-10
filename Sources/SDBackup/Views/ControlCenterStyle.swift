import SwiftUI

// Building blocks that mimic macOS Control Center: rounded translucent modules, circular toggle
// buttons that fill with the accent colour when on, and capsule "slider" progress bars.

enum ControlCenterMetrics {
    static let panelWidth: CGFloat = 320
    static let panelPadding: CGFloat = 12
    static let spacing: CGFloat = 10
    static let cornerRadius: CGFloat = 14
    static let modulePadding: CGFloat = 10
    /// Width of each half of the two-column top row.
    static let columnWidth: CGFloat = (panelWidth - panelPadding * 2 - spacing) / 2
}

extension Font {
    static let moduleTitle = Font.system(size: 12, weight: .semibold)
    static let moduleSubtitle = Font.system(size: 11)
}

// MARK: - Modules

/// The rounded, slightly lighter tile that every Control Center section sits on.
struct ModuleBackground: View {
    @Environment(\.colorScheme) private var colorScheme

    var body: some View {
        let shape = RoundedRectangle(cornerRadius: ControlCenterMetrics.cornerRadius, style: .continuous)
        shape
            .fill(Color.white.opacity(colorScheme == .dark ? 0.08 : 0.55))
            .overlay(shape.strokeBorder(Color.primary.opacity(colorScheme == .dark ? 0.12 : 0.06), lineWidth: 0.5))
            .shadow(color: .black.opacity(colorScheme == .dark ? 0 : 0.06), radius: 1, y: 0.5)
    }
}

struct Module<Content: View>: View {
    /// Stretch to the height offered by the parent (used for the two-column top row).
    var fillsHeight = false
    @ViewBuilder var content: Content

    var body: some View {
        content
            .padding(ControlCenterMetrics.modulePadding)
            .frame(maxWidth: .infinity, maxHeight: fillsHeight ? .infinity : nil, alignment: .topLeading)
            .background(ModuleBackground())
    }
}

struct ModuleTitle: View {
    let text: String

    init(_ text: String) {
        self.text = text
    }

    var body: some View {
        Text(text).font(.moduleTitle)
    }
}

/// A whole module that acts as one button, like Control Center's Focus or Screen Mirroring tiles.
struct ModuleButtonStyle: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .padding(ControlCenterMetrics.modulePadding)
            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .leading)
            .background(ModuleBackground())
            .contentShape(RoundedRectangle(cornerRadius: ControlCenterMetrics.cornerRadius, style: .continuous))
            .opacity(configuration.isPressed ? 0.7 : 1)
    }
}

/// Icon, title and optional subtitle, laid out like a Control Center tile.
struct TileLabel: View {
    let symbol: String
    let title: String
    var subtitle: String? = nil

    var body: some View {
        HStack(spacing: 8) {
            CircleIcon(symbol: symbol)
            VStack(alignment: .leading, spacing: 0) {
                Text(title)
                    .font(.moduleTitle)
                    .lineLimit(1)
                if let subtitle {
                    Text(subtitle)
                        .font(.moduleSubtitle)
                        .foregroundStyle(.secondary)
                        .lineLimit(1)
                }
            }
            Spacer(minLength: 0)
        }
        .foregroundStyle(.primary)
    }
}

// MARK: - Controls

/// The circular symbol button used throughout Control Center.
struct CircleIcon: View {
    let symbol: String
    var isOn = false
    var tint: Color = .accentColor
    var size: CGFloat = 26

    var body: some View {
        Image(systemName: symbol)
            .font(.system(size: size * 0.44, weight: .semibold))
            .foregroundStyle(isOn ? Color.white : Color.primary)
            .frame(width: size, height: size)
            .background(Circle().fill(isOn ? tint : Color.primary.opacity(0.1)))
            .animation(.easeOut(duration: 0.15), value: isOn)
    }
}

/// Circle + title + "On"/"Off", like the Wi-Fi and Bluetooth rows in Control Center.
struct ControlCenterToggleStyle: ToggleStyle {
    let symbol: String

    func makeBody(configuration: Configuration) -> some View {
        Button {
            configuration.isOn.toggle()
        } label: {
            HStack(spacing: 8) {
                CircleIcon(symbol: symbol, isOn: configuration.isOn)
                VStack(alignment: .leading, spacing: 0) {
                    configuration.label
                        .font(.moduleTitle)
                        .lineLimit(1)
                    Text(configuration.isOn ? String(localized: "On") : String(localized: "Off"))
                        .font(.moduleSubtitle)
                        .foregroundStyle(.secondary)
                }
                Spacer(minLength: 0)
            }
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .accessibilityValue(configuration.isOn ? String(localized: "On") : String(localized: "Off"))
    }
}

/// Small capsule button, like the ones inside Control Center modules.
struct PillButtonStyle: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .font(.system(size: 11, weight: .semibold))
            .padding(.horizontal, 10)
            .padding(.vertical, 4)
            .background(Capsule().fill(Color.primary.opacity(configuration.isPressed ? 0.2 : 0.1)))
            .contentShape(Capsule())
    }
}

/// A progress bar drawn like Control Center's Display and Sound sliders: a capsule track with a
/// white fill and the symbol inside on the left. Pass `nil` for an indeterminate state.
struct CapsuleProgress: View {
    let fraction: Double?
    let symbol: String
    var height: CGFloat = 22

    var body: some View {
        GeometryReader { proxy in
            let width = proxy.size.width
            ZStack(alignment: .leading) {
                Capsule()
                    .fill(Color.primary.opacity(0.12))

                if let fraction {
                    Capsule()
                        .fill(Color.white)
                        // Never narrower than a circle, so the symbol always sits on white.
                        .frame(width: max(height, width * min(max(fraction, 0), 1)))
                        .shadow(color: .black.opacity(0.2), radius: 1, y: 0.5)
                        .animation(.easeOut(duration: 0.3), value: fraction)
                } else {
                    TimelineView(.animation) { context in
                        let time = context.date.timeIntervalSinceReferenceDate
                        let segment = width * 0.35
                        let offset = (sin(time * 2.4) + 1) / 2 * (width - segment)
                        Capsule()
                            .fill(Color.white.opacity(0.85))
                            .frame(width: segment)
                            .offset(x: offset)
                    }
                    Circle()
                        .fill(Color.white)
                        .frame(width: height)
                }

                Image(systemName: symbol)
                    .font(.system(size: height * 0.45, weight: .bold))
                    .foregroundStyle(Color.black.opacity(0.6))
                    .frame(width: height, height: height)
            }
        }
        .frame(height: height)
        .clipShape(Capsule())
        .accessibilityElement(children: .ignore)
        .accessibilityLabel("Backup progress")
        .accessibilityValue(fraction.map { String(localized: "\(Int(($0 * 100).rounded())) percent") } ?? String(localized: "Checking the card"))
    }
}
