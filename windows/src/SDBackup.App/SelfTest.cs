using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SDBackup.Windows;

namespace SDBackup.App;

/// <summary>
/// <c>Notch.exe --self-test &lt;folder&gt;</c>: opens the panel, the settings window and the folder structure editor with
/// sample data in light and dark mode, fails on any XAML error or data-binding error, and writes screenshots and a report to the
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
            _ = App.LoadIcon("Tray.ico");
            _ = App.LoadIcon("TrayAttention.ico");
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

                // Regressions seen before: a picker going blank after its options were refreshed, and the panel
                // running off the bottom of a small screen.
                if (Descendants<Image>(flyout).Any(image => image.Source is not { Width: > 0 }))
                {
                    throw new InvalidOperationException("An image in the panel (the footer logo) didn't load.");
                }
                var blank = Descendants<ComboBox>(flyout).Count(box => box.SelectedItem is null);
                if (blank > 0) throw new InvalidOperationException($"{blank} project picker(s) show no selection.");
                var workArea = SystemParameters.WorkArea;
                if (flyout.ActualHeight > workArea.Height + 1 || flyout.Top < workArea.Top - 1)
                {
                    throw new InvalidOperationException($"The panel ({flyout.ActualHeight:0} high at {flyout.Top:0}) doesn't fit the work area ({workArea}).");
                }
                var scroller = Descendants<ScrollViewer>(flyout).FirstOrDefault();
                if (scroller is { ScrollableHeight: > 0 })
                {
                    scroller.ScrollToEnd();
                    Settle();
                    Save(flyout, Path.Combine(folder, $"panel-{theme}-scrolled.png"));
                    report.Add($"Panel ({theme}): scrolls by {scroller.ScrollableHeight:0} in a {workArea.Height:0}-high work area");
                }

                model.Settings.PhotoApp = PhotoApp.LightroomClassic;
                var settings = new SettingsWindow { DataContext = model };
                settings.Show();
                Settle();
                Save(settings, Path.Combine(folder, $"settings-{theme}.png"));
                report.Add($"Settings ({theme}): {settings.ActualWidth:0}×{settings.ActualHeight:0}");
                // The lower sections (After importing, General, Memory) are below the fold.
                if (Descendants<ScrollViewer>(settings).FirstOrDefault() is { ScrollableHeight: > 0 } settingsScroller)
                {
                    settingsScroller.ScrollToEnd();
                    Settle();
                    Save(settings, Path.Combine(folder, $"settings-{theme}-scrolled.png"));
                }

                // The folder structure editor, with every kind of level and a problem to show.
                var editor = model.CreateFolderStructureEditor();
                editor.AddSplitCommand.Execute(null);
                editor.AddFolderCommand.Execute(null);
                editor.Levels.First(l => l.IsText).Text = "";
                editor.CameraFolders = false;
                var structureWindow = new FolderStructureWindow(editor) { Owner = settings };
                structureWindow.Show();
                Settle();
                Save(structureWindow, Path.Combine(folder, $"structure-{theme}.png"));
                report.Add($"Folder structure ({theme}): {editor.Levels.Count} levels, {editor.Problems.Count} problem(s), {editor.Examples.Count} examples");
                if (editor.Problems.Count != 1) throw new InvalidOperationException($"Expected one problem in the edited structure, got: {string.Join(" ", editor.Problems)}");
                var pickers = Descendants<ComboBox>(structureWindow).Where(box => box.IsVisible).ToList();
                if (pickers.Count == 0 || pickers.Any(box => box.SelectedItem is null))
                {
                    throw new InvalidOperationException($"{pickers.Count(box => box.SelectedItem is null)} of {pickers.Count} folder structure picker(s) show no selection.");
                }
                if (Descendants<ScrollViewer>(structureWindow).FirstOrDefault() is { ScrollableHeight: > 0 } structureScroller)
                {
                    structureScroller.ScrollToEnd();
                    Settle();
                    Save(structureWindow, Path.Combine(folder, $"structure-{theme}-scrolled.png"));
                }
                structureWindow.Close();

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

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
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
