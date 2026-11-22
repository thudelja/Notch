import Foundation

/// A calendar day on which something was shot, in the camera's local time.
public struct ShootDay: Hashable, Comparable, Codable, Sendable, CustomStringConvertible {
    public let year: Int
    public let month: Int
    public let day: Int

    public init(year: Int, month: Int, day: Int) {
        self.year = year
        self.month = month
        self.day = day
    }

    public init(_ date: Date, calendar: Calendar = .current) {
        let components = calendar.dateComponents([.year, .month, .day], from: date)
        self.init(year: components.year ?? 1970, month: components.month ?? 1, day: components.day ?? 1)
    }

    /// Files shot before this hour count as the day before, so a shoot that runs past midnight
    /// (an event from 18:00 to 02:00) is one shoot day.
    public static let startHour = 4

    /// The shoot day of a file shot at `date`: its calendar day, or the day before when shot
    /// between midnight and `startHour`.
    public static func of(_ date: Date, calendar: Calendar = .current) -> ShootDay {
        ShootDay(calendar.date(byAdding: .hour, value: -startHour, to: date) ?? date, calendar: calendar)
    }

    public static func < (lhs: ShootDay, rhs: ShootDay) -> Bool {
        (lhs.year, lhs.month, lhs.day) < (rhs.year, rhs.month, rhs.day)
    }

    public func date(calendar: Calendar = .current) -> Date {
        calendar.date(from: DateComponents(year: year, month: month, day: day)) ?? Date(timeIntervalSince1970: 0)
    }

    /// "02.11.2026"
    public var folderDate: String {
        "\(Self.twoDigits(day)).\(Self.twoDigits(month)).\(year)"
    }

    public var description: String { folderDate }

    static func twoDigits(_ value: Int) -> String {
        value < 10 ? "0\(value)" : "\(value)"
    }
}
