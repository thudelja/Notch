using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SDBackup.Windows;

namespace SDBackup.App;

/// <summary>
/// <c>SDBackup.exe --self-test &lt;folder&gt;</c>: opens the panel and the settings window with sample data in light
/// and dark mode, fails on any XAML error or data-binding error, and writes screenshots and a report to the
/// folder. CI runs this on Windows to catch problems a compiler can't.
/// </summary>
internal static class SelfTest
{
    public static int Run(Application app, string folder)
    {
        Directory.CreateDirectory(folder);
        var report = new List<string>();
        var bindingErrors = new BindingErrorListener();
        PresentationTraceSources.Refresh();
        PresentationTraceSources.DataBindingSource.Listeners.Add(bindingErrors);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;

        var failed = false;
        try
        {
            System.Windows.Forms.Application.EnableVisualStyles();
            _ = App.LoadIcon("SDBackup.ico");
            _ = App.LoadIcon("SDBackupAttention.ico");
            report.Add("Icons: loaded");

            foreach (var dark in new[] { false, true })
            {
                var theme = dark ? "dark" : "light";
                ThemeManager.Apply(app.Resources, forceDark: dark);

                var model = new AppModel(new AppSettings(null), new LibraryStore(null), new BackupLog(null), new SilentDialogs(), (_, _) => { }, null);
                model.LoadSampleState();

                var flyout = new FlyoutWindow { DataContext = model, HideWhenDeactivated = false };
                flyout.ShowNearTray();
                Settle();
                // Exercise the import panel: pick another option, rename the project, edit a camera name.
                var draft = model.CurrentDraft ?? throw new InvalidOperationException("The sample state has no import draft.");
                draft.Days[1].Choice = draft.Days[1].Options.Last().Choice;
                draft.Days[0].ProjectName = "Ana & Jan Wedding";
                draft.Cameras[0].Name = "R5 II";
                Settle();
                Save(flyout, Path.Combine(folder, $"panel-{theme}.png"));
                report.Add($"Panel ({theme}): {flyout.ActualWidth:0}×{flyout.ActualHeight:0}, import ready: {draft.IsReady}");

                var settings = new SettingsWindow { DataContext = model };
                settings.Show();
                Settle();
                Save(settings, Path.Combine(folder, $"settings-{theme}.png"));
                report.Add($"Settings ({theme}): {settings.ActualWidth:0}×{settings.ActualHeight:0}");

                flyout.Close();
                settings.Close();
                model.Dispose();
            }
        }
        catch (Exception error)
        {
            failed = true;
            report.Add("FAILED: " + error);
        }

        Settle();
        if (bindingErrors.Errors.Count > 0)
        {
            failed = true;
            report.Add($"FAILED: {bindingErrors.Errors.Count} data-binding error(s):");
            report.AddRange(bindingErrors.Errors.Distinct().Select(e => "  " + e));
        }
        report.Insert(0, failed ? "SELF-TEST FAILED" : "SELF-TEST PASSED");
        File.WriteAllLines(Path.Combine(folder, "self-test.txt"), report);
        return failed ? 1 : 0;
    }

    /// <summary>Lets WPF finish pending layout, binding and rendering work.</summary>
    private static void Settle()
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        }
    }

    private static void Save(Window window, string path)
    {
        if (window.Content is not FrameworkElement content) return;
        var dpi = VisualTreeHelper.GetDpi(content);
        var width = (int)Math.Ceiling(content.ActualWidth * dpi.DpiScaleX);
        var height = (int)Math.Ceiling(content.ActualHeight * dpi.DpiScaleY);
        if (width <= 0 || height <= 0) return;

        var bitmap = new RenderTargetBitmap(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            // The panel is transparent around its rounded card; give the screenshot a desktop-like backdrop.
            context.DrawRectangle(new SolidColorBrush(ThemeManager.IsDark ? Color.FromRgb(0x1A, 0x2A, 0x45) : Color.FromRgb(0xD8, 0xE4, 0xF2)), null,
                new Rect(0, 0, content.ActualWidth, content.ActualHeight));
            context.DrawRectangle(new VisualBrush(content), null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
        }
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    private sealed class BindingErrorListener : TraceListener
    {
        private string _pending = "";

        public List<string> Errors { get; } = [];

        public override void Write(string? message) => _pending += message;

        public override void WriteLine(string? message)
        {
            Errors.Add(_pending + message);
            _pending = "";
        }
    }
}
