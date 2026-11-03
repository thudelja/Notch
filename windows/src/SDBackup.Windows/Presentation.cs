using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;

namespace SDBackup.Windows;

/// <summary>Follows Windows' light/dark setting and accent colour by replacing the app's brushes.</summary>
public static class ThemeManager
{
    public static bool IsDark { get; private set; }

    /// <param name="forceDark">Override the system setting (used by the self-test to render both).</param>
    public static void Apply(ResourceDictionary resources, bool? forceDark = null)
    {
        IsDark = forceDark ?? !SystemTheme.AppsUseLightTheme;
        var (r, g, b) = SystemTheme.Accent;
        var accent = Color.FromRgb(r, g, b);
        // Windows 11 uses a lighter accent on dark backgrounds.
        if (IsDark) accent = Mix(accent, Colors.White, 0.35);
        var accentHover = IsDark ? Mix(accent, Colors.White, 0.15) : Mix(accent, Colors.Black, 0.10);
        var luminance = (0.299 * accent.R + 0.587 * accent.G + 0.114 * accent.B) / 255;

        Set(resources, "AccentBrush", accent);
        Set(resources, "AccentHoverBrush", accentHover);
        Set(resources, "AccentForegroundBrush", luminance > 0.6 ? Colors.Black : Colors.White);

        if (IsDark)
        {
            Set(resources, "SurfaceBrush", 0xFF202020);
            Set(resources, "SurfaceStrokeBrush", 0xFF3A3A3A);
            Set(resources, "CardBrush", 0xFF2B2B2B);
            Set(resources, "CardStrokeBrush", 0xFF383838);
            Set(resources, "TextPrimaryBrush", 0xFFFFFFFF);
            Set(resources, "TextSecondaryBrush", 0xFFC8C8C8);
            Set(resources, "TextTertiaryBrush", 0xFF9D9D9D);
            Set(resources, "SubtleBrush", 0x14FFFFFF);
            Set(resources, "SubtleHoverBrush", 0x22FFFFFF);
            Set(resources, "SubtlePressedBrush", 0x10FFFFFF);
            Set(resources, "TrackBrush", 0xFF4A4A4A);
            Set(resources, "InputBrush", 0xFF2F2F2F);
            Set(resources, "InputStrokeBrush", 0xFF4B4B4B);
            Set(resources, "SuccessBrush", 0xFF6CCB5F);
            Set(resources, "WarningBrush", 0xFFFCE100);
            Set(resources, "ErrorBrush", 0xFFFF99A4);
            Set(resources, "OnStatusBrush", 0xFF1C1C1C);
        }
        else
        {
            Set(resources, "SurfaceBrush", 0xFFF3F3F3);
            Set(resources, "SurfaceStrokeBrush", 0xFFD6D6D6);
            Set(resources, "CardBrush", 0xFFFFFFFF);
            Set(resources, "CardStrokeBrush", 0xFFE5E5E5);
            Set(resources, "TextPrimaryBrush", 0xFF1B1B1B);
            Set(resources, "TextSecondaryBrush", 0xFF5D5D5D);
            Set(resources, "TextTertiaryBrush", 0xFF8A8A8A);
            Set(resources, "SubtleBrush", 0x0A000000);
            Set(resources, "SubtleHoverBrush", 0x14000000);
            Set(resources, "SubtlePressedBrush", 0x06000000);
            Set(resources, "TrackBrush", 0xFFD1D1D1);
            Set(resources, "InputBrush", 0xFFFFFFFF);
            Set(resources, "InputStrokeBrush", 0xFFC9C9C9);
            Set(resources, "SuccessBrush", 0xFF0F7B0F);
            Set(resources, "WarningBrush", 0xFF9D5D00);
            Set(resources, "ErrorBrush", 0xFFC42B1C);
            Set(resources, "OnStatusBrush", 0xFFFFFFFF);
        }
    }

    /// <summary>Gives a window a dark title bar in dark mode (Windows 10 20H1 and later).</summary>
    public static void ApplyTitleBar(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        var dark = IsDark ? 1 : 0;
        _ = DwmSetWindowAttribute(handle, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref dark, sizeof(int));
    }

    private static void Set(ResourceDictionary resources, string key, uint argb) =>
        Set(resources, key, Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));

    private static void Set(ResourceDictionary resources, string key, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        resources[key] = brush;
    }

    private static Color Mix(Color a, Color b, double amount) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * amount),
        (byte)(a.G + (b.G - a.G) * amount),
        (byte)(a.B + (b.B - a.B) * amount));

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}

/// <summary>null → Collapsed (or the reverse with <see cref="Invert"/>).</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value is null) ^ Invert ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>true → Collapsed.</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>"" or null → Collapsed.</summary>
public sealed class EmptyStringToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>An outcome's icon (Segoe Fluent Icons / Segoe MDL2 Assets).</summary>
public sealed class OutcomeToGlyphConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        Outcome.Completed => "",           // Completed
        Outcome.CompletedWithErrors => "", // Warning
        Outcome.Failed => "",              // Error
        _ => "",                           // Stop
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>An outcome's colour, from the theme.</summary>
public sealed class OutcomeToBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            Outcome.Completed => "SuccessBrush",
            Outcome.CompletedWithErrors => "WarningBrush",
            Outcome.Failed => "ErrorBrush",
            _ => "TextTertiaryBrush",
        };
        return Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
