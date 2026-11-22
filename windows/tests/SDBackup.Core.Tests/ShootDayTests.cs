using SDBackup.Core;

namespace SDBackup.Core.Tests;

/// <summary>A shoot that runs past midnight is one shoot day (1.4.2).</summary>
public sealed class ShootDayTests
{
    [Theory]
    [InlineData(18, 0, 21)]  // The event starts.
    [InlineData(23, 59, 21)]
    [InlineData(0, 30, 21)]  // After midnight: still the evening's day.
    [InlineData(2, 0, 21)]
    [InlineData(3, 59, 21)]
    [InlineData(4, 0, 22)]   // From 04:00, a new day (a sunrise shoot is its own day).
    [InlineData(12, 0, 22)]
    public void FilesShotBeforeFourCountAsTheDayBefore(int hour, int minute, int day)
    {
        // The evening on the 21st, everything else on the 22nd.
        var shot = new DateTime(2026, 11, hour >= 18 ? 21 : 22, hour, minute, 0, DateTimeKind.Local);
        Assert.Equal(new ShootDay(2026, 11, day), ShootDay.OfShot(shot));
    }

    [Fact]
    public void NewYearsEveStaysInTheOldYear() =>
        Assert.Equal(new ShootDay(2026, 12, 31), ShootDay.OfShot(new DateTime(2027, 1, 1, 1, 30, 0, DateTimeKind.Local)));
}
