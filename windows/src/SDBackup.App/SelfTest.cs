using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SDBackup.Core;
using SDBackup.Windows;

namespace SDBackup.App;

/// <summary>
/// <c>Notch.exe --self-test &lt;folder&gt;</c>: opens the panel, the settings window and the folder structure editor with
/// sample data in light and dark mode, and in Slovenian, fails on any XAML error or data-binding error, and writes screenshots
/// and a report to the folder. CI runs this on Windows to catch problems a compiler can't.
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

            foreach (var (language, dark) in new[] { (Loc.English, false), (Loc.English, true), (Loc.Slovenian, false) })
            {
                Loc.Use(language);
                var theme = (dark ? "dark" : "light") + (language == Loc.English ? "" : "-" + language);
                ThemeManager.Apply(app.Resources, forceDark: dark);

                var model = new AppModel(new AppSettings(null), new LibraryStore(null), new BackupLog(null), new SilentDialogs(), (_, _) => { }, null);
                model.LoadSampleState();

                var flyout = new FlyoutWindow { DataContext = model, HideWhenDeactivated = false };
                flyout.ShowNearTray();
                Settle();
                // Exercise the import panel: a new project whose photos go to a folder chosen with Change…, the second day in
                // the open multi-day shoot (light) or in a new one (dark), and a camera name.
                var draft = model.CurrentDraft ?? throw new InvalidOperationException("The sample state has no import draft.");
                var (first, second) = (draft.Days[0], draft.Days[1]);
                draft.SetFolder(first.Day, FolderStructure.Branch.Photo, Path.Combine(model.Settings.ProjectsFolder, "2026", "Clients"));
                first.ProjectName = "Ana & Jan Wedding";
                if (dark)
                {
                    second.Choice = DayChoice.NewShoot;
                    second.ShootName = "AMD Koledar 2027";
                }
                else
                {
                    second.Choice = second.Options.First(option => option.Choice.Kind == DayChoiceKind.NewInShoot).Choice;
                }
                second.ProjectName = "Maja";
                draft.Cameras[0].Name = "R5 II";
                Settle();
                Save(flyout, Path.Combine(folder, $"panel-{theme}.png"));
                report.Add($"Panel ({theme}): {flyout.ActualWidth:0}×{flyout.ActualHeight:0}, import ready: {draft.IsReady}");
                if (!draft.IsReady) throw new InvalidOperationException($"The sample import isn't ready: {draft.BlockingReason}");
                if (!first.HasChosenPhotoFolder || first.PhotoPlaceText != Loc.F("Photos → {0}", "Clients") || !first.ShowsVideoPlace)
                {
                    throw new InvalidOperationException($"The new project's places are wrong: “{first.PhotoPlaceText}”, “{first.VideoPlaceText}”.");
                }
                var holdButtons = Descendants<HoldButton>(flyout).Where(button => button.IsVisible).ToList();
                if (holdButtons.Count != (dark ? 0 : 1)) throw new InvalidOperationException($"The panel shows {holdButtons.Count} “Hold to finish” button(s).");
                if (!dark)
                {
                    // Screen readers invoke the button rather than hold it.
                    var peer = UIElementAutomationPeer.CreatePeerForElement(holdButtons[0]);
                    ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
                    Settle();
                    if (!second.ShootFinished || holdButtons[0].IsVisible) throw new InvalidOperationException("Invoking “Hold to finish” didn't finish the shoot.");
                }

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
                model.Settings.VideosFolder = Path.Combine(Path.GetTempPath(), "Notch self-test videos");
                var settings = new SettingsWindow { DataContext = model };
                settings.Show();
                Settle();
                Save(settings, Path.Combine(folder, $"settings-{theme}.png"));
                report.Add($"Settings ({theme}): {settings.ActualWidth:0}×{settings.ActualHeight:0}, {model.VersionText}");
                if (Descendants<TextBlock>(settings).All(text => text.Text != model.VersionText))
                {
                    throw new InvalidOperationException("Settings doesn't show the version.");
                }
                // The windows' own texts ({l:T …}) are in the language too.
                if (Descendants<TextBlock>(settings).All(text => text.Text != Loc.T("Multi-day shoots")))
                {
                    throw new InvalidOperationException($"Settings isn't in {language}.");
                }
                // The open multi-day shoot can be finished, the finished one reopened.
                var shootButtons = Descendants<Button>(settings).Where(button => button.IsVisible && button.DataContext is ShootViewModel).ToList();
                if (!shootButtons.Any(button => button is HoldButton && button.DataContext is ShootViewModel { IsOpen: true })
                    || !shootButtons.Any(button => button is not HoldButton && button.DataContext is ShootViewModel { IsClosed: true }))
                {
                    throw new InvalidOperationException("Settings doesn't list the multi-day shoots with their buttons.");
                }
                // The lower sections (After importing, General, Memory) are below the fold.
                if (Descendants<ScrollViewer>(settings).FirstOrDefault() is { ScrollableHeight: > 0 } settingsScroller)
                {
                    settingsScroller.ScrollToEnd();
                    Settle();
                    Save(settings, Path.Combine(folder, $"settings-{theme}-scrolled.png"));
                }

                // The folder structure editor: the photos' tab with every kind of level and a problem to show, then the
                // videos' tab with its project folders.
                var structures = model.CreateFolderStructureEditor();
                var photos = structures.Photos;
                photos.AddSplitCommand.Execute(null);
                photos.AddFolderCommand.Execute(null);
                photos.Levels.First(l => l.IsText).Text = "";
                photos.CameraFolders = false;
                var structureWindow = new FolderStructureWindow(structures) { Owner = settings };
                structureWindow.Show();
                Settle();
                Save(structureWindow, Path.Combine(folder, $"structure-{theme}.png"));
                report.Add($"Folder structure ({theme}), photos: {photos.Levels.Count} levels, {photos.Problems.Count} problem(s), {photos.Examples.Count} examples");
                if (photos.Problems.Count != 1) throw new InvalidOperationException($"Expected one problem in the edited structure, got: {string.Join(" ", photos.Problems)}");
                CheckPickers(structureWindow);
                if (Descendants<ScrollViewer>(structureWindow).FirstOrDefault() is { ScrollableHeight: > 0 } structureScroller)
                {
                    structureScroller.ScrollToEnd();
                    Settle();
                    Save(structureWindow, Path.Combine(folder, $"structure-{theme}-scrolled.png"));
                }

                structures.ShowVideos = true;
                Settle();
                var videos = structures.Videos;
                Save(structureWindow, Path.Combine(folder, $"structure-{theme}-videos.png"));
                report.Add($"Folder structure ({theme}), videos: {videos.Levels.Count} levels, {videos.VideoFolders.Count} project folders, {videos.Examples.Count} examples");
                if (videos.Problems.Count != 0) throw new InvalidOperationException($"The videos' structure has problems: {string.Join(" ", videos.Problems)}");
                var videoFolderBoxes = Descendants<TextBox>(structureWindow).Count(box => box.IsVisible && box.DataContext is VideoFolderViewModel);
                if (videoFolderBoxes != videos.VideoFolders.Count) throw new InvalidOperationException($"The Videos tab shows {videoFolderBoxes} of {videos.VideoFolders.Count} project folders.");
                CheckPickers(structureWindow);
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

    private static void CheckPickers(Window window)
    {
        var pickers = Descendants<ComboBox>(window).Where(box => box.IsVisible).ToList();
        if (pickers.Count == 0 || pickers.Any(box => box.SelectedItem is null))
        {
            throw new InvalidOperationException($"{pickers.Count(box => box.SelectedItem is null)} of {pickers.Count} picker(s) in {window.Title} show no selection.");
        }
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
