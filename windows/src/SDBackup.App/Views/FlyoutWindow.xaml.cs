using System;
using System.Windows;
using System.Windows.Input;

namespace SDBackup.App;

/// <summary>The panel above the notification area. Hides when another window is clicked, like Quick Settings.</summary>
public partial class FlyoutWindow : Window
{
    private DateTime _hiddenAt = DateTime.MinValue;

    public FlyoutWindow()
    {
        InitializeComponent();
        Deactivated += (_, _) =>
        {
            if (HideWhenDeactivated) HideFlyout();
        };
        SizeChanged += (_, _) => Reposition();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) HideFlyout();
        };
    }

    /// <summary>Hide when another window is clicked (off for the self-test, where nothing keeps focus).</summary>
    public bool HideWhenDeactivated { get; set; } = true;

    /// <summary>Clicking the tray icon while the panel is open first deactivates (hides) it; don't reopen it straight away.</summary>
    private bool JustHidden => DateTime.UtcNow - _hiddenAt < TimeSpan.FromMilliseconds(400);

    public void Toggle()
    {
        if (IsVisible) HideFlyout();
        else if (!JustHidden) ShowNearTray();
    }

    public void ShowNearTray()
    {
        MaxHeight = SystemParameters.WorkArea.Height;
        if (!IsVisible) Show();
        UpdateLayout();
        Reposition();
        Activate();
    }

    public void HideFlyout()
    {
        if (!IsVisible) return;
        _hiddenAt = DateTime.UtcNow;
        Hide();
    }

    /// <summary>
    /// Bottom-right corner of the work area, i.e. just above the taskbar's notification area. The panel is never
    /// taller than the work area; its content scrolls instead.
    /// </summary>
    private void Reposition()
    {
        var area = SystemParameters.WorkArea;
        if (MaxHeight != area.Height) MaxHeight = area.Height;
        Left = area.Right - ActualWidth;
        Top = Math.Max(area.Top, area.Bottom - ActualHeight);
    }
}
