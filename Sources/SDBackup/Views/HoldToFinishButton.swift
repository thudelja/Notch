import SwiftUI

/// A button that acts only after being held for 1.5 seconds, so a shoot isn't finished by a stray
/// click. A ring fills while it's held; it plays a short hint (the ring starts filling and empties
/// again) when it appears, and empties again if released early.
struct HoldToFinishButton: View {
    let title: String
    let doneTitle: String
    let action: () -> Void

    static let duration: Double = 1.5

    @State private var progress: CGFloat = 0
    @State private var pressing = false
    @State private var done = false

    var body: some View {
        if done {
            Label(doneTitle, systemImage: "checkmark.circle.fill")
                .font(.caption)
                .foregroundStyle(.secondary)
        } else {
            HStack(spacing: 6) {
                ZStack {
                    Circle()
                        .stroke(Color.secondary.opacity(0.35), lineWidth: 2)
                    Circle()
                        .trim(from: 0, to: progress)
                        .stroke(Color.accentColor, style: StrokeStyle(lineWidth: 2, lineCap: .round))
                        .rotationEffect(.degrees(-90))
                }
                .frame(width: 14, height: 14)
                Text(title)
                    .font(.caption)
                    .lineLimit(1)
            }
            .padding(.horizontal, 9)
            .padding(.vertical, 4)
            .background(Capsule().fill(Color.secondary.opacity(pressing ? 0.22 : 0.12)))
            .contentShape(Capsule())
            .onLongPressGesture(minimumDuration: Self.duration, maximumDistance: 30) {
                finish()
            } onPressingChanged: { isPressing in
                pressing = isPressing
                if isPressing {
                    withAnimation(.linear(duration: Self.duration)) { progress = 1 }
                } else if !done {
                    withAnimation(.easeOut(duration: 0.25)) { progress = 0 }
                }
            }
            .onAppear(perform: hint)
            .help(String(localized: "Hold for 1.5 seconds"))
            .accessibilityElement(children: .combine)
            .accessibilityAddTraits(.isButton)
            .accessibilityAction { finish() }
        }
    }

    private func finish() {
        guard !done else { return }
        done = true
        action()
    }

    /// Fills a quarter of the ring and empties it again: this button is held, not clicked.
    private func hint() {
        Task { @MainActor in
            try? await Task.sleep(nanoseconds: 400_000_000)
            guard !pressing, !done else { return }
            withAnimation(.easeInOut(duration: 0.35)) { progress = 0.3 }
            try? await Task.sleep(nanoseconds: 500_000_000)
            guard !pressing, !done else { return }
            withAnimation(.easeInOut(duration: 0.35)) { progress = 0 }
        }
    }
}
